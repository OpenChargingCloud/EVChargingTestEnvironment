# Entwurf: WebSocket und SSH zum Energiezähler

Stand 2026-11-22. Gilt für `CS_2_EM` und `LC_2_EM`. Gebaut, siehe Abschnitt 6.

## 1. Ziel

- Eine Ladestation (CS) oder ein LocalController (LC) verbindet sich mit dem
  Energiezähler (EM). Wege: `ws://`, `wss://`, `wss://` mit Client-Zertifikat
  (mTLS) und `ssh://`.
- Anmeldung mit Passwort, TOTP, SSH-Schlüssel oder Zertifikat. Beide Seiten
  sehen die Verbindung als verbunden.
- Der EM spricht Modbus über SSH im Subsystem `modbus@open.charging.cloud`
  und über den WebSocket im Subprotokoll `modbus`.
- Jeder Login hat eine SunSpec-Rolle, über seine Gruppe. Der EM entscheidet
  jede Anfrage nach dieser Rolle, genau wie bei Modbus/TLS nach der Rolle im
  Zertifikat.
- Ein EVSE-Zähler der Ladestation (`meters[]`) wird über `mbaps://`, `ws://`,
  `wss://` oder `ssh://` gelesen.

## 2. Befund

| Baustein | Ort | Stand |
|---|---|---|
| Rollen `ReadOnlySunSpec` … `SuperAdministratorSunSpec`, `AuthorizationPolicy` | Hermod `SunSpecModbusTLS` | da, unabhängig vom Transport |
| Schleife „Frame lesen, Rolle prüfen, ans Backend, antworten“ (`PumpModbusAsync`) | Hermod `ModbusTlsFrontend`, privat | braucht nur einen `Stream` |
| `ModbusTCPClient(Stream)` | Hermod | nimmt jeden Stream |
| SSH-Subsystem als Stream (`SshMuxChannel.AsStream()`, `SSHClient.OpenSubsystem`) | Hermod | da, auf beiden Seiten |
| `AddSSHSubsystem`, `AddSSHSignIns`, `LoginSSHSignIns`, `WebSocketSignIns`, `ClientTrustStore`, `ConnectionStore` | WWCP_Node | da (seit dem EMSP) |
| SSH-Server des EM | WWCP_Node | da, trägt heute Kommandozeile und SFTP |
| Verbindungen des EM | – | **fehlen**: kein `ConnectionStore`, kein WebSocket-Port |
| `ConnectionType` für einen Zähler | WWCP_Node | **fehlt** |
| LC und Zähler | LocalController | **nichts**: der LC liest heute keinen Zähler |
| CS und Zähler | ChargingStation `Meters/` | liest über Modbus/TLS (`meters[]`), nicht über den `ConnectionStore` |

Modbus über SSH ist damit vor allem Verdrahtung. Der Rahmen ist derselbe wie
bei Modbus/TLS (MBAP-Header + PDU), nur der Stream darunter ist ein SSH-Kanal
statt TLS.

## 3. Vorschlag

### 3.1 Hermod

- Die Schleife des `ModbusTlsFrontend` wird öffentlich:
  `ServeAsync(Stream, Role, Peer, …)`. Sie entscheidet je Anfrage mit derselben
  `AuthorizationPolicy` und meldet `OnModbusRequest` wie bisher.
  Modbus/TLS ruft sie nach dem Handshake auf, SSH mit dem Kanal.

### 3.2 WWCP_Node

- `ConnectionType.EnergyMeter` (beim CS/LC ausgehend) sowie
  `ConnectionType.ChargingStation` und `ConnectionType.LocalController` (beim
  EM eingehend).
- `ConnectionUse.Modbus`.
- Namen auf dem Draht: SSH-Subsystem `modbus@open.charging.cloud`,
  WebSocket-Subprotokoll `modbus`.
- Über den WebSocket ist jede Binärnachricht genau ein Modbus/TCP-Frame
  (MBAP-Header + PDU), in beide Richtungen. Textnachrichten gibt es nicht.

### 3.3 Energiezähler

- `ConnectionStore` mit den Regeln „nimmt CS und LC an, gibt nichts aus“.
  Dazu `WebSocketLogins`, `LoginSSHSignIns` und `ClientTrustStore`.
- Ein WebSocket-Port, Abschnitt `webSocketServer`, Standard **2357** (frei).
- Das Subsystem `modbus@open.charging.cloud` auf dem SSH-Server. Ein Login
  bekommt nur dieses Subsystem: keine Kommandozeile, kein SFTP.
- **Rolle über die Gruppe:** Der EM hat je SunSpec-Rolle eine Login-Gruppe
  (`WebSocketLoginGroup`) mit dem Namen der Rolle: `readonlysunspec`,
  `gridservicesunspec`, `networkadministratorsunspec`,
  `superadministratorsunspec`, auf dem Schirm `ReadOnlySunSpec` usw. Die
  Gruppe eines Logins ist seine Rolle, für Passwort, TOTP, SSH-Schlüssel und
  Zertifikat gleich. Ein Login in einer anderen Gruppe hat keine Rolle und
  bekommt Ausnahme 01 für alles, wie ein Zertifikat ohne Rolle.
- Die Rolle wird je Anfrage nachgesehen und nicht beim Anmelden gemerkt. Ein
  Login, der die Gruppe wechselt, hat die neue Rolle bei der nächsten Anfrage.
- Über den WebSocket mit mTLS gilt ebenfalls die Gruppe. Eine Rolle im
  Zertifikat zählt nur auf dem Modbus/TLS-Port.
- `ModbusConnections` zeigt auch die SSH-Verbindungen, mit Login und Rolle.

### 3.4 CS und LC

- Ein Eintrag vom Typ `EnergyMeter`, ausgehend, verbindet sich beim Start.
- Lesen und Schreiben von Registern über diese Verbindung
  (`ReadRegisters`, `WriteRegisters`), über `ModbusTCPClient` auf dem
  Kanal-Stream.
- **CS:** Ein Eintrag in `meters[]` kann statt `host`/`port` eine `url`
  haben: `mbaps://host:802`, `ws://`, `wss://` oder `ssh://login@host:port`.
  Ohne `url` bleibt es Modbus/TLS wie bisher. Über WebSocket und SSH meldet
  sich die Station mit den Zugangsdaten des Eintrags an. Das Lesen
  (`EVSEMeter`) läuft über dieselbe Modbus-Schnittstelle, egal welcher Weg.

### 3.5 Tests

`CS_2_EM` und `LC_2_EM` bekommen je:

- `WebSocket`, `WebSocketTLS`, `WebSocketMTLS` und `SSH` (Passwort, TOTP,
  Schlüssel): beide Seiten sind verbunden.
- Modbus über SSH, je Rolle:
  - lesen geht mit jeder Rolle;
  - den Zählermodus (40094, *commanded*) schreiben `GridService` und höher,
    `ReadOnly` bekommt 01;
  - die Unit-Adresse (40068, *protected*) schreiben `NetworkAdministrator`
    und `SuperAdministrator`, `GridService` bekommt 01;
  - ohne Rolle: 01 für alles;
  - Gruppe gewechselt: die neue Rolle gilt bei der nächsten Anfrage.
- Modbus über den WebSocket mit denselben Fällen.
- `CS_2_EM`: ein EVSE-Zähler wird über `ssh://` und `wss://` gelesen.

## 4. Reihenfolge

| # | Repo | Inhalt |
|---|---|---|
| 1 | Hermod | `ServeAsync(Stream, …)` aus dem Frontend |
| 2 | WWCP_Node | `ConnectionType`s, `ConnectionUse.Modbus`, Namen auf dem Draht |
| 3 | ModbusTLSEnergyMeter | Store, Logins mit Rolle, WebSocket-Port, SSH-Subsystem |
| 4 | ChargingStation, LocalController | Eintrag `EnergyMeter`, Register lesen und schreiben |
| 5 | Superprojekt | `CS_2_EM.cs`, `LC_2_EM.cs` |

## 5. Entscheidungen (Achim, 2026-11-22)

1. **WebSocket:** trägt Modbus, Subprotokoll `modbus`.
2. **Rolle:** über die Login-Gruppe mit dem Namen der Rolle.
3. **CS:** `meters[]` wird mit umgestellt, ein EVSE-Zähler wird auch über
   `ws(s)://` und `ssh://` gelesen.

## 6. Was gebaut ist

**Hermod**

- `ModbusTlsFrontend.ServeAsync(Stream, RoleOf, Peer, Transport, Login)`
  bedient Modbus über einen beliebigen Stream. Die Rolle wird je Anfrage über
  `RoleOf()` erfragt. Modbus/TLS nutzt dieselbe Schleife mit der festen Rolle
  des Zertifikats.
- `ModbusConnectionInfo` hat `Transport` und `Login`.
- `ModbusTCPClient.Over(Stream)` ist ein Client auf einem fremden Stream.

**WWCP_Node**

- `ConnectionType.EnergyMeter` und `ConnectionType.MeterReader`,
  `ConnectionUse.Modbus`, `ProtocolVersion.Modbus`.
- `ConnectionRules.Every` enthält `EnergyMeter`. Ein `EnergyMeter` ist nur
  ausgehend, ein `MeterReader` nur eingehend. Eine Modbus-Verbindung nennt
  keinen Knoten und keine OCPI-Version.
- `Modbus/`:
  - `ModbusWireNames`;
  - `WebSocketMessageStream`: ein WebSocket als Stream, jede Schreiboperation
    ist eine Binärnachricht;
  - `ModbusWebSocketClient` und `ModbusWebSocketServer`, beide auf Hermods
    WebSocket ohne WWCP-Netzwerkknoten.
- `WWCPNode.Meters.cs`: `ConnectTo` geht für Modbus-Einträge zu
  `ConnectOverModbus`.
  - Über `ws://` und `wss://` meldet sich der Knoten an wie bei jeder
    Verbindung: Zugangsdaten, Key-Pins, CA-Prüfung.
  - Über `ssh://` nutzt er `SshLoginOf` und öffnet das Subsystem.
  - Bricht die Verbindung ab, verbindet er sich nach 3 s neu.
  - `HangUp` legt auch hier auf.
  - Die API dazu: `ReadMeterRegisters`, `WriteMeterRegisters`,
    `ExchangeWithMeter`, `MeterConnectionIsUp`, `MeterTransportOf`.

**ModbusTLSEnergyMeter**

- `ModbusTLSEnergyMeter.MeterReaders.cs` baut alles für die eingehenden
  Verbindungen auf:
  - der `ConnectionStore` nimmt nur `MeterReader`, eingehend;
  - `WebSocketLogins` mit den vier Rollengruppen, `ClientTrustStore`;
  - die Tür `WebSocketSignIns` vor dem `ModbusWebSocketServer`;
  - Abschnitt `webSocketServer` (`MeterReaderServerConfiguration`), Port
    2357, Zertifikat über den Listener `websocket`;
  - `LoginSSHSignIns` und das Subsystem `modbus@open.charging.cloud`.
- Bedient wird jede Verbindung über `frontend.ServeAsync`, also mit derselben
  Policy, denselben Backends und demselben Log wie Modbus/TLS.
- `SunSpecLoginGroups` legt die vier Gruppen beim Start an, falls sie fehlen,
  und übersetzt Gruppe in Rolle. `SunSpecRoleOf(Login)` liefert die Rolle
  eines Logins.
- `ModbusConnections` listet WebSocket- und SSH-Verbindungen mit Transport,
  Login und der Rolle der letzten Anfrage. `MeterReadersIn` listet, wer gerade
  angemeldet ist.
- Der Status je Eingangseintrag ist `Connected` oder `Waiting`.

**ChargingStation**

- `MeterEndpoint` hat `Over` (URL) und versteht `url`. `mbaps://` ergibt
  Host und Port, `ws://`, `wss://` und `ssh://` ergeben `Over`.
- `EVSEMeter` liest einen Zähler mit `Over` über die Verbindung der Station
  vom Typ `EnergyMeter` mit genau dieser URL (`ExchangeOverTheMeterConnection`).
- Die Einzelanfrage `Ask` geht ebenfalls über diese Verbindung.

**Tests**

- Superprojekt:
  - `AToTheEnergyMeter.cs` enthält alle Fälle;
  - `CS_2_EM.cs` und `LC_2_EM.cs` erzeugen nur die Station bzw. den LC;
  - `CS_2_EM` liest zusätzlich einen EVSE-Zähler über `ssh://` und `wss://`.
- Libs:
  - Hermod: `ModbusOverAStreamTests`;
  - WWCP_Node: `ModbusConnectionTests`;
  - Zähler: `MeterReadersTests`;
  - ChargingStation: `MetersConfigurationTests`, mit `url`.

### 6.1 Abweichungen vom Entwurf

- **Beim EM eingehend heißt der Typ `MeterReader`**, nicht `ChargingStation`
  oder `LocalController`. Ein Typ bestimmt das Protokoll
  (`ConnectionEntry.UseOf`). `ChargingStation` und `LocalController` tragen
  OCPP und würden beim EM falsch geführt.
- **`meters[].url` mit `ws(s)://` oder `ssh://`** verweist auf die
  `EnergyMeter`-Verbindung der Station mit derselben URL. Deren Zugangsdaten
  und Prüfung des Servers gelten. Der `meters[]`-Eintrag hat keine eigenen
  Zugangsdaten.
- Die API heißt `ReadMeterRegisters` und `WriteMeterRegisters` statt
  `ReadRegisters` und `WriteRegisters`.

### 6.2 Offen

- Signierte Sitzungen (`EVSEMeter.Command`) gehen weiter nur über Modbus/TLS.
  Über eine Verbindung antwortet die Station mit einer klaren Absage.
- Wird ein Eingangseintrag beim EM entfernt oder abgeschaltet, trennt der EM
  eine offene Verbindung nicht. Bis dahin gilt die Rolle der Gruppe weiter.
  Eine Station, deren `EnergyMeter`-Eintrag geändert wird, verbindet sich erst
  nach einem Abbruch neu.
- Der EM hat noch keine Seiten oder HTTP-API für seine Verbindungen und Logins.
- Der WebSocket-Port des EM nimmt nur einen Listener `websocket`. Ein Zertifikat
  ohne Listener wird auf allen Ports gezeigt.
