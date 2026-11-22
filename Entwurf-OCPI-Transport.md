# Entwurf: WebSocket und SSH zwischen CSMS und EMSP

Stand 2026-11-21. Schritt 1 ist gebaut, in beide Richtungen (Abschnitt 7).
Der OCPI-Teil von `CSMS_2_EMSP` (HTTP) lief schon vorher, siehe Abschnitt 6.

## 1. Ziel

**Schritt 1 (dieser Entwurf):** Ein CSMS verbindet sich mit einem EMSP über

- `ws://`, `wss://` und `wss://` mit Client-Zertifikat (mTLS),
- `ssh://`.

Er meldet sich mit Passwort, TOTP, SSH-Schlüssel oder Zertifikat an. Beide
Seiten sehen die Verbindung als verbunden. Über die Leitung geht noch nichts
außer dem, was der Transport selbst spricht. `CSMS_2_EMSP` bekommt dafür die
Fälle `WebSocket`, `WebSocketTLS`, `WebSocketMTLS` und `SSH`, so wie
`CS_2_CSMS`.

**Schritt 2 (später):** OCPI-over-WebSocket und OCPI-over-SSH. OCPI-Module
werden als Nachrichten über diese Leitung definiert statt als HTTP-Aufrufe.

## 2. Befund

Der EMSP ist ein reiner `WWCPNode`, kein `OCPPServingNode`.

| Baustein | Ort | Für den EMSP nutzbar? |
|---|---|---|
| `ConnectionStore`, `ConnectionEntry`, `ConnectionRules` | WWCP_Node | ja, aber `OCPPVersion` und `ConnectionType` sind OCPP-förmig |
| Verbinden (`ConnectTo`, `ConnectOverSSH`, Note/Fail) | WWCP_Node | ja |
| Annehmen (`ServeInbound`, `RouteInbound`, `LetInAsInbound`) | WWCP_Node | nach `OCPPVersion` geschlüsselt, braucht einen Netzwerkknoten |
| `WebSocketLogins`, `LoginSSHSignIns`, SSH-Server, `AddSSHSubsystem` | WWCP_Node | ja |
| Login-Prüfung (`ValidateStation`, `ClientTrustStore`, Stationsport) | **WWCP_OCPP_Node**, privat | nein |
| `WWCPWebSocketServer`/`Client`, `WWCPSshServer`/`Client`, `AWWCPNetworkingNode` | WWCP_Core | ja, protokollfrei |

Was heute blockiert:

1. **Kein Connection-Store im EMSP.** `Connections` ist `default!`. Nur CSMS,
   ChargingStation, LocalController und Gateway setzen ihn.
2. **`ConnectionType` kennt keinen EMSP.** `TryParseType` kennt nur `csms`,
   `csmsbackup`, `localcontroller`, `chargingstation` und `gateway`. Der CSMS
   lehnt die Verbindung schon bei `Validate` ab.
3. **Annehmen braucht einen Netzwerkknoten.** `RouteInbound` ruft
   `ConnectingNodeOf(OCPPVersion)` auf und wirft ohne einen solchen Knoten.
   Die Verbindung wird deshalb nie als verbunden vermerkt.
4. **Die Login-Prüfung gibt es nur im OCPP-Teil.** Das betrifft Passwort,
   TOTP, Zertifikat und Key-Pin. `ValidateStation` ist privat in
   `OCPPServingNode`.
5. **Der CSMS spricht auf dem Draht OCPP.** Er bietet die Subprotokolle
   `ocpp2.1`/`ocpp2.1cbor` an und das SSH-Subsystem `ocpp@open.charging.cloud`.

## 3. Vorschlag

### 3.1 WWCP_Node: Annehmen ohne OCPP

- Die Login-Prüfung aus `OCPPServingNode.ValidateStation` zieht als
  `InboundWebSocketServer` nach WWCP_Node. Das umfasst:
  - Basic-Passwort, TOTP als Passwort, `Authorization: TOTP` und TOTP-Header;
  - Client-Zertifikat und Key-Pin;
  - `LetIn`.
  - Dazu `ClientTrustStore` und das Lesen des Abschnitts `webSocketServer`.

  `OCPPServingNode` nutzt den Baustein danach selbst, und der EMSP nutzt
  denselben.
- `ServeInbound`/`RouteInbound` fragen einen Hook „Welcher Netzwerkknoten
  nimmt diese Verbindung?“, statt `ConnectingNodeOf(OCPPVersion)` aufzurufen.
  Das ist Schicht 3 aus `Entwurf-ConnectionTransport.md`, hier nur so weit,
  wie der EMSP es braucht.

### 3.2 WWCP_OCPI: ein OCPI-Netzwerkknoten

- `OCPINetworkingNode : AWWCPNetworkingNode` in `WWCP_OCPI_Common`, für CSMS
  und EMSP. Er hängt an `WWCPWebSocketServer`/`Client` und
  `WWCPSshServer`/`Client`. In Schritt 1 nimmt er an und hält die Leitung,
  in Schritt 2 trägt er die OCPI-Module.
- Namen auf dem Draht (Vorschlag):
  - WebSocket-Subprotokolle `ocpi2.1.1`, `ocpi2.2.1`, `ocpi2.3.0`, je mit
    einer `…cbor`-Variante;
  - SSH-Subsystem `ocpi@open.charging.cloud`.

### 3.3 Verbindungsmodell

- `ConnectionType` nennt die Gegenstelle: beim CSMS `ConnectionType.EMSP`,
  beim EMSP eingehend `ConnectionType.CPO`. Später kommt `EMSP_2_CSMS` mit
  denselben Bausteinen dazu, dann mit umgekehrter Richtung.
- Eine Verbindung sagt, welches Protokoll sie trägt: OCPP mit `OCPPVersion`
  oder OCPI mit OCPI-Version. Das ist die `IUse`-Schicht aus
  `Entwurf-ConnectionTransport.md`, hier nur in ihrer kleinsten Form.
  `ConnectingProtocol` liefert danach Subprotokolle, Subsystem und Knoten je
  Protokoll.
- Ein EMSP-Eintrag beim CSMS braucht keinen `RemoteNodeId`-Login. Hinter
  einem EMSP stehen keine Stationen.

### 3.4 EMSP

- Er bekommt einen `ConnectionStore` mit den Regeln „nimmt CSMS an, kein
  OCPP 1.6“, außerdem `WebSocketLogins` und `SshKey`.
- Er bekommt einen eigenen WebSocket-Port, Abschnitt `webSocketServer` wie
  beim CSMS. Standard wäre der nächste freie Port der Reihe, **2356**.
- Er bekommt das Subsystem `ocpi@open.charging.cloud` auf seinem SSH-Server,
  der heute nur die Kommandozeile trägt, plus `AddSSHSignIns`.
- Er bekommt die Seiten für Verbindungen und Logins. Ob sie in Schritt 1
  nötig sind, ist offen.

## 4. Reihenfolge

| # | Repo | Inhalt |
|---|---|---|
| 1 | WWCP_Node | `InboundWebSocketServer` und Trust-Store aus WWCP_OCPP_Node herunterziehen; Hook für den annehmenden Knoten |
| 2 | WWCP_OCPP | `OCPPServingNode` auf den Baustein umstellen, ohne Verhaltensänderung, alle Tests grün |
| 3 | WWCP_Node | `ConnectionType.EMSP`/`CPO` und Protokoll an der Verbindung (OCPP/OCPI) |
| 4 | WWCP_OCPI | `OCPINetworkingNode`, Subprotokolle und Subsystem |
| 5 | EMSP | Store, Logins, Port, SSH-Subsystem |
| 6 | CSMS | EMSP-Verbindungen über den OCPI-Knoten |
| 7 | Superprojekt | `CSMS_2_EMSP`: WebSocket, WebSocketTLS, WebSocketMTLS, SSH |

## 5. Entscheidungen (Achim, 2026-11-21)

1. **Login-Prüfung:** wird nach WWCP_Node verschoben. `OCPPServingNode` und
   der EMSP nutzen denselben Baustein.
2. **Namen auf dem Draht:** `ocpiX.Y.Z` (+ `…cbor`) und
   `ocpi@open.charging.cloud`.
3. **Verbindungsmodell:** die kleinste `IUse`-Form jetzt, `ConnectionType.EMSP`
   und `ConnectionType.CPO`.
4. **JSON/CBOR:** wie bei OCPP zwei Subprotokolle je Version. Das ist noch
   offen, Vorschlag: ja.

## 6. Was schon da ist

`CSMS_2_EMSP` testet OCPI über HTTP in 2.1.1, 2.2.1 und 2.3.0:

- das Peering, von beiden Seiten gestartet;
- die Karten des EMSP, über die der CSMS entscheidet (bekannt, ungültig,
  unbekannt);
- die Locations des CSMS, die den EMSP erreichen. Der CSMS schickt alle,
  wenn er sich beim EMSP registriert, und jede neue sofort, egal wer das
  Peering begonnen hat. Eine gelöschte Location wird dem EMSP nicht
  mitgeteilt, weil OCPI dafür kein Löschen kennt.

Dabei behoben (WWCP_OCPI): Der 2.1.1-Client des EMSP hat `PUT` für einen
Token ohne `{country_code}/{party_id}` geschickt, und der CPO hat mit 405
geantwortet. Antworten auf Get-, Put- und PatchToken wurden ohne Land und
Partei aus der URL geparst.

## 7. Die umgekehrte Richtung: `EMSP_2_CSMS`

Der EMSP verbindet sich mit dem CSMS, Client und Server tauschen die Rollen.
Gebaut mit denselben Bausteinen wie `CSMS_2_EMSP`:

- **EMSP:** Ein Eintrag vom Typ `CPO` mit `ocpiVersion`, ausgehend. Er läuft
  über den `OCPINetworkingNode` des EMSP und `MessageConnecting` mit den
  OCPI-Namen. Die Regeln des EMSP lassen `CPO` ein- und ausgehend zu. Der
  EMSP verbindet sich beim Start und legt beim Stopp auf.
- **CSMS:** Der Stationsport reicht einen WebSocket mit einem Subprotokoll
  `ocpi2.x.y` (oder `…cbor`) an einen OCPI-Server je Version weiter, so wie
  er OCPP 1.6 an den Knoten von OCPP 1.6 weiterreicht. Diese Server hängen am
  `OCPINetworkingNode` des CSMS und haben keinen eigenen Port. Die Tür ist ein
  eigener `WebSocketSignIns` über dieselben WebSocket-Logins und dieselben
  Ketten. Er lässt nur ein, was als eingehender Eintrag vom Typ `EMSP`
  aufgeschrieben ist. Über SSH dient der CSMS das Subsystem
  `ocpi@open.charging.cloud`, solange OCPP-over-SSH bedient wird. Der
  vorhandene `LoginSSHSignIns` gibt einem EMSP-Login nur dieses Subsystem.
- **Kein Wechsel der Rolle:** Eine Station meldet sich nicht über OCPI an, ein
  EMSP nicht als Station über OCPP. Über SSH öffnet ein EMSP-Login nur das
  OCPI-Subsystem. Ein OCPI-Server über SSH lässt nichts ein, was nicht sein
  eigener Eintrag ist. Getestet beim CSMS (`EMSP_2_CSMS`) und beim EMSP
  (`OCPIServerTests`).
- **Port des EMSP:** 2356 ist schon der Port des RoamingHub. Der WebSocket-Port
  des EMSP ist deshalb **2359**.

OCPI über HTTP bleibt nur in `CSMS_2_EMSP` getestet.
