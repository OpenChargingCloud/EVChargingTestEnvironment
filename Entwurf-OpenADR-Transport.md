# Entwurf: WebSocket und SSH zwischen LocalController und Netzbetreiber

Stand 2026-11-22. Entschieden (Abschnitt 5). Die Schritte A, B und C sind gebaut (Abschnitte 7, 8 und 9).

## 1. Ziel

`LC_2_GO`: Ein LocalController verbindet sich mit dem Netzbetreiber (GridOperator) über

- `ws://`, `wss://` und `wss://` mit Client-Zertifikat (mTLS),
- `ssh://`, mit dem SSH-Subsystem `openadr@open.charging.cloud`.

Er meldet sich mit Passwort, TOTP, SSH-Schlüssel oder Zertifikat an. Über die
Leitung spricht er OpenADR 3 als VEN mit dem VTN des Netzbetreibers: Er liest
Programme und Events (die Limits) und schickt Reports.

## 2. Befund

| Baustein | Ort | Stand |
|---|---|---|
| VTN (`/openadr3`: `auth/token`, `programs`, `events`, `vens`, `reports`) | GridOperator, `OpenADRVTNAPI` | nur HTTP(S), OAuth Client Credentials |
| VEN-Logins (`LocalControllerLogin`: VEN-Name, Pool, Netzanschlusspunkt, Secret) | GridOperator, `OpenADRStore` | da |
| VEN-Client (`VENClient`) | LocalController | `HttpClient` mit austauschbarem `HttpMessageHandler` |
| Konfiguration `openADR` (`vtnURL`, `clientID`, `pollEvery`) | LocalController | nur `http(s)://` |
| `HandleHTTPStreamAsync(Stream, …)` | Hermod `AHTTPServer` | öffentlich: bedient HTTP/1.1 auf einem beliebigen Stream |
| `ConnectionStore`, `WebSocketLogins`, Login-Prüfung, `AddSSHSubsystem`, `WebSocketMessageStream` | WWCP_Node | da, vom Energiezähler und vom EMSP genutzt |
| OpenADR-Datenstrukturen | WWCP_OpenADR | **3.0.1**, nichts von 3.1 |

Der Netzbetreiber ist ein reiner `WWCPNode` wie der Energiezähler. Er hat
noch keinen `ConnectionStore`, keine WebSocket-Logins, keinen WebSocket-Port
und kein SSH-Subsystem.

## 3. Vorschlag

### 3.1 Was über die Leitung geht

**HTTP/1.1 der OpenADR-3-REST-API, unverändert, im Stream der Verbindung.**

- Netzbetreiber: Ein angenommener Stream (SSH-Kanal oder `WebSocketMessageStream`)
  geht an `HandleHTTPStreamAsync` des HTTP-Servers, auf dem der VTN liegt.
  Ein zweiter VTN entsteht nicht. Was über HTTPS geht, geht auch hier, und
  nichts anderes.
- LocalController: Der `VENClient` bekommt einen `SocketsHttpHandler`, dessen
  `ConnectCallback` den Stream der Verbindung liefert, statt eine TCP-Verbindung
  zu öffnen. Der VEN-Client selbst ändert sich nicht.
- Über WebSocket trägt eine Binärnachricht einen Abschnitt des Streams, wie
  bei Modbus.

Die Alternative wäre eine eigene Nachrichtenform, also Anfrage und Antwort als
JSON-Nachrichten und neue Events vom VTN gepusht. Das ist mehr Code auf beiden
Seiten und ein zweiter Weg zu jedem VTN-Endpunkt.

### 3.2 Anmeldung

Vorschlag: **Der Login der Verbindung entscheidet, welcher VEN es ist.** Über
WebSocket und SSH braucht es dann kein OAuth-Token mehr. Der eingehende
Eintrag beim Netzbetreiber nennt den VEN-Namen. Der VTN behandelt jede Anfrage
auf diesem Stream so, als hätte dieser VEN ein gültiges Token gezeigt. Das
entspricht Modbus, wo die Gruppe des Logins die SunSpec-Rolle bestimmt.

Die Alternative: OAuth Client Credentials zusätzlich im Tunnel. Dann braucht
der Netzbetreiber keine Änderung am VTN, aber der LocalController braucht zwei
Geheimnisse.

### 3.3 Namen und Verbindungsmodell

- SSH-Subsystem `openadr@open.charging.cloud`, WebSocket-Subprotokoll
  `openadr3` (bei 3.1: `openadr3.1`).
- `ConnectionType.GridOperator` (ausgehend, beim LocalController) und
  `ConnectionType.VEN` (eingehend, beim Netzbetreiber), dazu
  `ConnectionUse.OpenADR`.
- Netzbetreiber: ein eigener WebSocket-Port, Abschnitt `webSocketServer`,
  Standard **2363**. 2346 bis 2362 sind vergeben.
- LocalController: `openADR.vtnURL` nimmt zusätzlich `ws://`, `wss://` und
  `ssh://` an. Ein solcher URL geht über die `GridOperator`-Verbindung mit
  demselben URL, wie bei `meters[]` des Energiezählers.

### 3.4 OpenADR 3.1

WWCP_OpenADR kann heute nur 3.0.1. Laut OpenADR Alliance bringt 3.1 vor allem:

- Targets als Liste von Strings statt als Liste von ValuesMaps;
- Object Privacy: Der VTN filtert Programme und Events nach den Targets des VEN;
- `GET /notifiers` und MQTT als Weg für Benachrichtigungen;
- VEN-bezogene Topics.

Der Transport aus 3.1 hängt nicht an der Version. Der Umstieg auf 3.1 betrifft
die Datenstrukturen, den VTN und den VEN-Client und ist ein eigener Schritt.

## 4. Reihenfolge

| # | Repo | Inhalt |
|---|---|---|
| 1 | WWCP_Node | `ConnectionType.GridOperator`/`VEN`, `ConnectionUse.OpenADR`, Namen auf dem Draht |
| 2 | GridOperator | Store, WebSocket-Logins, Port 2363, SSH-Subsystem, VTN über Stream, VEN aus dem Login |
| 3 | LocalController | `GridOperator`-Verbindung, `VENClient` über deren Stream |
| 4 | Superprojekt | `LC_2_GO`: WebSocket, WebSocketTLS, WebSocketMTLS, SSH; Limits lesen, Report schicken, Rollentrennung |

## 5. Entscheidungen (Achim, 2026-11-22)

1. **Draht:** beides. Die unveränderte OpenADR-REST-API im Tunnel *und*
   OCPP-ähnliche JSON-Nachrichten, die OpenADR über WebSocket und SSH abbilden.
2. **Anmeldung:** Der Login der Verbindung entscheidet, welcher VEN es ist. Im
   Tunnel und in den Nachrichten gibt es kein OAuth-Token.
3. **OpenADR 3.1:** jetzt, in derselben Runde wie der Transport. WWCP_OpenADR,
   der VTN und der VEN-Client gehen auf 3.1.0. 3.0.1 bleibt nicht daneben
   stehen, es gibt keine Rückwärtskompatibilität.

## 6. Ausgestaltung nach den Entscheidungen

### 6.1 Namen auf dem Draht

| Bindung | WebSocket-Subprotokoll | SSH-Subsystem | Rahmen |
|---|---|---|---|
| Nachrichten | `openadr3.1` | `openadr@open.charging.cloud` | Textnachricht = ein JSON-Array |
| REST im Tunnel | `openadr3.1-rest` | `openadr-rest@open.charging.cloud` | HTTP/1.1 im Stream; über WebSocket je Binärnachricht ein Abschnitt |

### 6.2 Nachrichten

Die Rahmen sind die von OCPP-J und kommen ohne WWCP-Netzwerkpfad aus:

- `[2, "<id>", "<Action>", {…}]` für eine Anfrage;
- `[3, "<id>", {…}]` für eine Antwort;
- `[4, "<id>", "<Code>", "<Text>", {…}]` für einen Fehler. Die Details sind das
  `problem` von OpenADR mit `status`.

Jede REST-Operation des VTN wird eine Action:

- `GetPrograms` und `GetProgram`;
- `GetEvents` und `GetEvent`;
- `GetVENs` und `GetVEN`;
- `GetResources`;
- `GetReports`, `GetReport`, `CreateReport`, `UpdateReport` und `DeleteReport`;
- `GetSubscriptions`, `CreateSubscription` und `DeleteSubscription`;
- `GetNotifiers`.

Die Nutzlast einer Anfrage enthält die Query-Parameter und den Body, die
Antwort enthält den Body der REST-Antwort. Die Abbildung gilt in beide
Richtungen. Der VTN schickt dem VEN `Notify` mit einer `notification` von
OpenADR 3.1, wenn ein Abo des VEN passt. Ein Abo, das über die Nachrichten
angelegt wurde, wird über dieselbe Verbindung beliefert. Webhook oder MQTT
braucht es dafür nicht.

Beide Bindungen laufen über denselben VTN-Code. Was der VEN über HTTPS nicht
sehen darf, sieht er auch hier nicht. Das gilt auch für die Object Privacy
von 3.1.

### 6.3 Verbindungen

- `ConnectionType.GridOperator` ist ausgehend beim LocalController,
  `ConnectionType.VEN` eingehend beim Netzbetreiber. Dazu kommt
  `ConnectionUse.OpenADR`.
- Ein Eintrag sagt mit `openADRBinding` (`messages` oder `rest`), welche Bindung
  er spricht. Ohne Angabe ist es `messages`.
- Der eingehende `VEN`-Eintrag beim Netzbetreiber nennt mit `ven` den VEN-Namen
  eines `LocalControllerLogin`. Seine Anfragen gelten als Anfragen dieses VEN.
  Der Login gibt nur die OpenADR-Subsysteme frei.
- Der Netzbetreiber bekommt `ConnectionStore`, `WebSocketLogins`, `SshKey`, den
  WebSocket-Port 2363 (Abschnitt `webSocketServer`), beide Subsysteme und
  `AddSSHSignIns`.
- Beim LocalController nimmt `openADR.vtnURL` auch `ws://`, `wss://` und
  `ssh://`. Ein solcher URL geht über die `GridOperator`-Verbindung mit
  demselben URL. `clientID` braucht er dann nicht. Der Abruf der Limits geht
  über eine Schnittstelle mit zwei Umsetzungen: HTTP (direkt oder im Tunnel)
  und Nachrichten.

### 6.4 Reihenfolge

| # | Inhalt |
|---|---|
| A | Transport: WWCP_Node-Typen, Netzbetreiber (Store, Logins, Port, Subsysteme), LocalController-Verbindung, REST im Tunnel |
| B | OpenADR 3.1: WWCP_OpenADR, VTN des Netzbetreibers, VEN-Client des LocalControllers |
| C | Nachrichten: Rahmen, Actions, `Notify`, auf beiden Seiten |
| D | `LC_2_GO`: WebSocket, WebSocketTLS, WebSocketMTLS, SSH, je mit Nachrichten und REST; Limits lesen, Report schicken, Notify, Rollentrennung |

## 7. Schritt A gebaut

Stand 2026-11-22. Gebaut ist der Transport mit der REST-Bindung. Die
Nachrichten (Schritt C) und OpenADR 3.1 (Schritt B) fehlen noch.

### 7.1 Was gebaut ist

- **WWCP_Node:**
  - `ConnectionType.GridOperator` (ausgehend) und `ConnectionType.VEN`
    (eingehend), dazu `ConnectionUse.OpenADR`.
  - Das Feld `openADRBinding` (`messages` oder `rest`, ohne Angabe
    `messages`) und das Feld `ven`. Ein eingehender `VEN`-Eintrag muss `ven`
    nennen, jeder andere Eintrag darf es nicht.
  - Beide Felder gehen durch Datei, Store, Änderung und die
    Verbindungs-API von WWCP_OCPP.
  - Die Bindung steckt in `ProtocolVersion.OpenADR(Bindung)`. Ein Server lässt
    deshalb nur Einträge seiner eigenen Bindung ein.
  - `OpenADR/OpenADRWireNames.cs` enthält die Namen aus 6.1 und den Pfad des
    VTN im Tunnel, `/openadr3/`.
  - `WWCPNode.GridOperators.cs` verbindet über ws, wss, wss mit
    Client-Zertifikat und ssh und versucht es nach 3 s erneut.
    `OpenGridOperatorStream` und `GridOperatorHTTPHandler` geben den Stream
    an einen HTTP-Client.
- **GridOperator:**
  - Er bekommt `ConnectionStore`, `WebSocketLogins`, `ClientTrust` und
    `SshKey`, dazu den WebSocket-Port 2363 (Abschnitt `webSocketServer`,
    Listener `openadr`) und das Subsystem `openadr-rest@open.charging.cloud`.
  - Die Tür lässt nur ein, was als eingeschalteter eingehender `VEN`-Eintrag
    der REST-Bindung aufgeschrieben ist. Über SSH bekommt ein Login nur das
    Subsystem seines Eintrags und keine Kommandozeile.
  - Der VTN nimmt eine Anfrage aus dem Tunnel als Anfrage des VEN, den der
    Eintrag nennt (`VENTunnelStream`). Ein Bearer-Token darin zählt nicht.
- **LocalController:**
  - `openADR.vtnURL` nimmt auch `ws://`, `wss://` und `ssh://`. Dann braucht
    er weder `clientID` noch ein Geheimnis.
  - Der VTN wird über die `GridOperator`-Verbindung mit demselben URL und
    der Bindung `rest` gefragt. `VENClient` läuft dabei mit
    `SignedInByTheConnection` und holt kein Token.
  - Neu ist die Schnittstelle `IVTNClient`. Dort kommt in Schritt C der
    Nachrichten-Client hinzu.
- **Superprojekt:** `LC_2_GO` deckt ab:
  - WebSocket, WebSocketTLS, WebSocketMTLS und SSH mit Passwort, TOTP,
    Schlüssel und Zertifikat;
  - das Lesen des Limits und den Report beim Netzbetreiber;
  - dass ein anderer VEN das Limit nicht sieht;
  - dass eine ausgeschaltete Verbindung nicht hereinkommt;
  - dass es über SSH keine Kommandozeile gibt.

Alle Tests sind grün:

| Suite | Tests |
|---|---|
| WWCP_Node | 1649 |
| GridOperator | 209 |
| LocalController | 658 |
| Energiezähler | 402 |
| CSMS | 595 |
| OCPP_Node | 492 |
| ChargingStation | 1093 |
| Gateway | 273 |
| EMSP | 416 |
| Env | 331, davon 13 in `LC_2_GO` |

In CSMS und OCPP_Node standen erwartete Listen der Verbindungstypen. Dort
kommt jetzt `GridOperator` vor.

### 7.2 Abweichungen von Abschnitt 6

1. **Ein zweiter VTN, auf einem eigenen HTTP-Server, der nie lauscht.** Der
   Tunnel geht nicht an den HTTP-Server der Weboberfläche, sondern an einen
   eigenen. Darauf liegt nur der VTN, derselbe Code über demselben Store. So
   erreicht eine Anfrage durch den Tunnel weder `/api` noch die Weboberfläche.
   Getestet in `VENTunnelTests`.
2. **Ein Tunnel ist eine HTTP-Verbindung.** Gibt der HTTP-Client des
   LocalControllers den Stream zurück, endet die Verbindung. Sie wird sofort
   neu aufgebaut, ohne die 3 s.
3. **Kein Leerlauf-Timeout im Tunnel.** Der Tunnel-Server wartet bis zu einem
   Tag auf den Kopf der nächsten Anfrage, weil ein VEN nur alle 30 s fragt.
4. **Die Modbus-Klassen für WebSocket sind allgemein geworden.** Aus
   `ModbusWebSocketClient` und `ModbusWebSocketServer` wurden
   `StreamWebSocketClient` und `StreamWebSocketServer`, jeweils mit dem
   Subprotokoll als Parameter. `WebSocketMessageStream` liegt jetzt in
   `Streams/`. Der Energiezähler ist angepasst.
5. **`GridOperator` steht in `ConnectionRules.Every`.** Jeder Knoten darf sich
   also mit einem Netzbetreiber verbinden, so wie mit einem Energiezähler.
6. **Ein `vtnURL` einer Verbindung bekommt keinen Schrägstrich angehängt.** Die
   Verbindung wird bei jeder Anfrage gesucht. Eine später angelegte Verbindung
   wird also ohne Neustart genommen.
7. **Die Bindung `messages` ist schon gültig, wird aber nicht gesprochen.** Der
   LocalController vermerkt eine solche Verbindung als „nicht verbunden“ und
   nennt den Grund. Der Netzbetreiber wartet auf einen solchen Eintrag und
   sagt dazu, dass er die Nachrichten noch nicht spricht.

### 7.3 Offen

- Schritt B (OpenADR 3.1) und Schritt C (Nachrichten, `Notify`).
- Für Verbindungen und WebSocket-Logins hat der Netzbetreiber noch keine
  Seiten, keine HTTP-API und keine Kommandozeile.
- Ein ausgeschalteter oder gelöschter Eintrag schließt einen offenen Tunnel
  nicht. Der Tunnel bleibt offen, bis er abbricht, wie beim Energiezähler.
  Ein gelöschter `LocalControllerLogin` bekommt dagegen sofort auf jede
  Anfrage 401.
- Die Weboberflächen von LocalController und Netzbetreiber kennen die neuen
  Felder noch nicht.

## 8. Schritt B gebaut

Stand 2026-11-22. Alles OpenADR spricht 3.1.0. 3.0.1 gibt es nicht mehr.
Grundlage sind die YAML von 3.1.0 (in der reformatierten Fassung, wegen des
Fehlers in `BlVenRequest.targets.default`), die Definitions und der User
Guide.

### 8.1 Was sich geändert hat

- **WWCP_OpenADR:**
  - `Version` ist `v3.1.0`.
  - Targets sind Strings (1 bis 128 Zeichen), überall: Programm, Event,
    VEN, Ressource, Subscription, ReportDescriptor, Notification. Lesen und
    Schreiben gehen über `OpenADRTargets`. Eine ValuesMap als Target wird
    abgelehnt.
  - `clientID` gibt es an Report, Subscription, VEN und Ressource
    (`OpenADRClientID`).
  - Programm: Die neun Felder von 3.0.1 sind weg, dazu kommen `attributes`.
    Die Attributtypen (`PROGRAM_LONG_NAME`, `RETAILER_NAME` und so weiter)
    stehen in `ValueType`. `programDescriptions` sind Objekte mit `URL`.
  - Event: `intervals` ist optional, `duration` ist neu.
  - Report: ohne `programID`, mit `clientID`.
  - Subscription: `programID` ist optional, `clientID` ist neu.
  - VEN: kein `resources` mehr. `ToRequestJSON` schreibt `BL_VEN_REQUEST`
    oder `VEN_VEN_REQUEST`. Ein `VEN_VEN_REQUEST` mit `targets` oder
    `clientID` wird abgelehnt.
  - Ressource: `venID` ist Pflicht. Die Varianten `BL_RESOURCE_REQUEST` und
    `VEN_RESOURCE_REQUEST` gibt es wie beim VEN.
  - `intervalPeriod.start` ist optional.
  - Payload-Deskriptoren brauchen `objectType`.
  - `reportDescriptor.reportIntervals` ist neu (`ReportIntervals`).
  - Operationen heißen `CREATE`, `READ`, `UPDATE` und `DELETE`. Die Liste ist
    geschlossen: `GET`, `POST` und `PUT` werden abgelehnt.
  - Neu sind `AuthServerInfo`, `NotifiersResponse`, `MQTTNotifierBinding`
    und `NotifierTopicsResponse`.
  - Neue Werte: `PRICE_ALTERNATE`, `DISPATCH_INSTRUCTION`,
    `CONTROL_LEVEL_OFFSET`, `CONTROL_LEVEL_OFFSET_PERCENT`, `THERMS`, die
    vier Request-Objekttypen.
  - Der generische Server der Bibliothek hat die Ressourcen unter
    `/resources` und antwortet auf `GET /notifiers`. Der Client fragt
    Programme mit `skip`, `limit` und `targets`.
  - Nebenbei behoben: `ObjectOperation` las `callbackURL` statt
    `callbackUrl`, `ReportDescriptor` konnte `payloadType` nicht schreiben,
    VEN und Ressource gaben `SUBSCRIPTION` als Objekttyp an, ein
    Event-Payload-Deskriptor schrieb die Währung als Text statt als
    ISO-Code.
- **Netzbetreiber (VTN):**
  - Die `clientID` eines VEN ist sein VEN-Name. Das gilt auch für einen VEN
    im Tunnel aus Schritt A.
  - Er gewährt jedem VEN drei Targets (siehe 8.3).
  - Object Privacy:
    - Ein VEN liest ein Event mit Targets nur, wenn sie sich mit seinen
      treffen. Fragt er mit `?targets=`, zählt die Schnittmenge mit seinen.
    - Er sieht nur seine eigenen Targets (Target Hiding).
    - Reports und Subscriptions liest, ändert und löscht er nur mit seiner
      `clientID`. Bei fremden heißt die Antwort 404.
  - Reports: `PUT` und `DELETE` sind neu. Der VTN schreibt die `clientID`,
    egal was im Report steht. `clientName` bleibt, wie der VEN ihn schickt.
  - Subscriptions: `GET`, `POST`, `PUT` und `DELETE`. Vor dem Speichern
    prüft der VTN jeden Callback mit einem `echo`. Benachrichtigt wird per
    Webhook bei neuen und gelöschten Limits, bei Reports und bei
    Subscriptions des VEN.
  - `GET /notifiers` antwortet `{"WEBHOOK": true}`.
  - `GET /resources` liefert eine leere Liste.
  - `GET /events?active=true` lässt vergangene Events weg. Ohne den
    Parameter werden sie mitgeliefert.
  - Die Scopes im Token sind `read_targets read_ven_objects write_reports
    write_subscriptions`.
  - Ein Fehler an `auth/token` ist ein Problem nach RFC 9457, kein
    OAuth-Fehler mehr.
  - Das Programm trägt seine Namen als `attributes`.
- **LocalController:** Der VEN-Client fragt `events?active=true`. Ein Report
  geht ohne `programID` und ohne `clientID` hinaus.
- **Für Schritt C vorbereitet:**
  - `OpenADRSubscriptions` hält die Subscriptions. Die Abfrage, welche
    Subscription eine Änderung erfährt (`OfTargetedObject`, `OfOwnObject`),
    hängt nicht an der Zustellung.
  - `GridOperator.OnOpenADRNotification` meldet jede Benachrichtigung.
  - Per Webhook geht nur, was einen `http(s)`-Callback hat. Jeder andere
    Callback bleibt der Zustellung in Schritt C überlassen.

### 8.2 Wo die Spezifikation unklar ist

1. **`objectType` von VEN und Ressource:** In Antworten steht `VEN` bzw.
   `RESOURCE`, wie in den Beispielen des User Guide. Gelesen wird auch
   `BL_…`/`VEN_…_REQUEST`.
2. **`read_bl`:** Der Scope ist nicht deklariert und wird hier nicht
   gebraucht. Der VTN hat keine BL-Clients über OAuth, die Geschäftslogik ist
   der Netzbetreiber selbst (Web, API, CLI).
3. **`GET /notifiers` mit `read_all`:** Jeder angemeldete VEN darf fragen, wie
   im User Guide.
4. **Objekte ohne Targets:** Sie sind für jeden VEN sichtbar. Das betrifft
   das Programm. Ein Limit hat immer Targets.
5. **VEN ohne `?targets=`:** Er liest mit allen gewährten Targets, wie im
   User Guide, Beispiel 8.13-5.
6. **MQTT-Topics:** Es gibt keinen Broker. `/notifiers/mqtt/topics/…`
   liefert 404.
7. **Echo:** Die Antwort muss genau der Wert sein, auch in JSON-Anführungszeichen.

### 8.3 Die Targets des Netzbetreibers

Jeder VEN bekommt drei Targets:

- `ven:<VEN-Name>`
- `gridConnectionPoint:<Netzanschlusspunkt>`
- `chargingPool:<Ladepark>`

Ein Limit trägt `ven:<Name>` für jeden VEN, dem es gilt, wie bisher die
VEN_NAME-Targets. Die Präfixe verhindern, dass ein VEN, der so heißt wie ein
Netzanschlusspunkt, dessen Limits sieht.

### 8.4 Abweichungen

- Subscriptions, Reports und Tokens liegen nur im Speicher. Nach einem
  Neustart muss ein VEN sich neu anmelden und neu abonnieren.
- **Webhooks:**
  - Ein Callback muss `https` sein. `http` gilt nur, wenn der VEN selbst über
    `http` kam, also wenn der Netzbetreiber ohne TLS läuft.
  - Ein VEN, der über WebSocket oder SSH kommt, braucht immer `https`.
  - Private Adressen werden nicht gesperrt, Benachrichtigungen nicht
    signiert. Weiterleitungen folgt der Webhook nicht.
- Ein VEN kann seinen VEN-Eintrag und seine Ressourcen nicht selbst anlegen
  (403). Die legt nur der Netzbetreiber an.
- `?targets=` wird an Kommas geteilt (Hermods `GetStrings`). Ein Target mit
  Komma kann man so nicht abfragen.
- Ein gespeichertes Limit im alten Format (ValuesMap-Targets) wird beim
  Start als nicht lesbar gemeldet und ausgelassen.

### 8.5 Tests

| Suite | Tests |
|---|---|
| WWCP_OpenADR | 62 (43 neu) |
| GridOperator | 220 (11 neu) |
| LocalController | 661 (3 neu) |
| WWCP_Node | 1649 |
| Env | 333 (2 neu) |

Neu sind:

- `OpenADR31Tests` und `OpenADR31RoundTripTests` in WWCP_OpenADR:
  - jedes geänderte Objekt, gelesen, geschrieben und wieder gelesen, wo es
    geht nach den Beispielen des User Guide;
  - die Formen von 3.0.1 werden abgelehnt: ValuesMap-Targets und die
    Operationen `GET`, `POST` und `PUT`.
- `OpenADR31Tests` beim Netzbetreiber:
  - Object Privacy über zwei VENs, für Events, Programm, VEN-Einträge,
    Reports und Subscriptions, mit Target Hiding;
  - `?targets=` und `?active=true`;
  - `/resources` und was nur der Netzbetreiber schreibt;
  - Subscriptions mit Anlegen, Lesen, Ändern und Löschen, je nach
    `clientID`;
  - die Echo-Prüfung: ein falsches Echo, kein Callback und `ftp` werden
    abgelehnt;
  - `CREATE`, `UPDATE` und `DELETE` erreichen nur die passende
    Subscription;
  - `notifiers`, `auth/server`, `auth/token` als Problem, die Scopes.
- `OpenADRTests` beim LocalController:
  - ein Limit nach 3.1 mit `?active=true`;
  - ein Report ohne `programID`;
  - ein Event nach 3.0.1 wird übergangen;
  - ein Event nur für Reports ist kein Limit.
- `LC_2_GO.OpenADR31_ALimitForEveryVEN_IsReadWithOnlyItsOwnTarget` über
  ws und ssh.

### 8.6 Offen

- Schritt C: die Nachrichten mit `Notify` über `OnOpenADRNotification` und
  `OpenADRSubscriptions`.
- MQTT und die Topics.
- Ein VEN, der eigene VEN- und Ressourcen-Einträge schreibt.
- Die Weboberfläche zeigt Subscriptions und die gewährten Targets noch
  nicht. Die JSON-API liefert sie unter `localControllers`.

## 9. Schritt C gebaut

Stand 2026-11-22. Die Bindung `messages` ist gebaut, auf beiden Seiten. Ein
LocalController spricht mit dem VTN seines Netzbetreibers in OCPP-ähnlichen
JSON-Nachrichten, über WebSocket (`openadr3.1`) und über SSH
(`openadr@open.charging.cloud`).

### 9.1 Was gebaut ist

- **WWCP_OpenADR**, neuer Ordner `Messaging/`:
  - `OpenADRMessageFrame` liest und schreibt die Rahmen `[2,…]`, `[3,…]` und
    `[4,…]` wie OCPP-J. Die Id hat 1 bis 36 Zeichen, einen Netzwerkpfad gibt
    es nicht.
  - `OpenADRActions` enthält 19 Actions, jede mit ihrer REST-Operation
    (Methode, Pfad, Id, Body, Query). Dazu kommen `TryToHTTPRequest`,
    `FromHTTPResponse` und `ErrorCodeOf`.
  - `OpenADRMessageEndpoint` fragt, wartet auf die Antwort (30 s) und
    beantwortet Anfragen. Eine eingehende Anfrage wird neben der Nachricht
    beantwortet, in der sie kam. So kann ein Handler, der selbst zurückfragt,
    nicht hängen bleiben.
- **WWCP_Node:**
  - `StreamWebSocketServer` kennt jetzt `MessageSubprotocols`. Eine Verbindung
    eines solchen Subprotokolls hat keinen Stream, ihre Textnachrichten gehen
    an `OnTextMessageOf`.
  - `MessageWebSocketClient` ist neu.
  - `WWCPNode.GridOperators.cs` verbindet in der Bindung `messages` über ws,
    wss, wss mit Client-Zertifikat und ssh. Über SSH laufen Hermods
    Nachrichten, wie bei OCPP über SSH. Für den Knoten darüber gibt es
    `OnGridOperatorMessage`, `OnGridOperatorMessageLink` und
    `SendToGridOperator`. Der Platzhalter aus Schritt A ist weg.
- **Netzbetreiber:**
  - Der WebSocket-Port 2363 spricht beide Subprotokolle, der SSH-Server dient
    beide Subsysteme.
  - Die Tür lässt einen Login nur mit dem Subprotokoll der Bindung seines
    Eintrags ein, sonst kommt 400 vor dem Upgrade. Über SSH bekommt ein Login
    nur das Subsystem seiner Bindung, wie schon in Schritt A.
  - Der Platzhalter aus Schritt A ist weg.
  - Neu sind `VENMessageConnectionsIn` und `NotificationsToldOverMessages`.
- **LocalController:**
  - `MessageVTNClient` setzt `IVTNClient` über die Nachrichten um.
    `ThroughAConnection` wählt bei jeder Anfrage nach der Bindung des
    Eintrags zwischen REST und Nachrichten.
  - Beim ersten Abruf über eine neue Verbindung abonniert er die Events
    (`CREATE`, `UPDATE`, `DELETE`), mit der URL der Verbindung als Callback.
    Vorher fragt er `GetSubscriptions` mit seinem `clientName`. So entsteht
    nach einem Neuaufbau kein zweites Abo.
  - Auf `Notify` zu `EVENT` oder `PROGRAM` fragt er sofort. Kommt die
    Verbindung hoch, fragt er auch sofort. Der Poll bleibt als Rückfall.

### 9.2 Die Naht

Eine Anfrage als Nachricht wird zur HTTP-Anfrage der REST-API, für die sie
steht. Sie geht an den VTN der Tunnel aus Schritt A, über einen eigenen Stream
(`OneRequestStream`). Der hält die Anfrage und nimmt die Antwort auf. Er steckt
in einem `VENTunnelStream` mit `ViaMessages`, der VEN ist der des Eintrags.
Damit gibt es einen VTN und eine Prüfung (Object Privacy, Report,
Subscription) für beide Bindungen, ohne zweite Umsetzung. `/api` und die
Weboberfläche sind auch über Nachrichten nicht erreichbar.

### 9.3 Wie Abos über Nachrichten zugestellt werden

- Ein Abo über die Nachrichten trägt als Callback ein `ws://`, `wss://` oder
  `ssh://`, üblich die URL, mit der der VEN sich verbunden hat. Der VTN prüft
  ihn nicht mit einem Echo, denn der Callback ist die Verbindung selbst.
- Zum Abo merkt sich der VTN den Eintrag der Verbindung
  (`OpenADRSubscriptions.DeliverOverConnection`). `Notify` geht über die
  Verbindung, die gerade unter diesem Eintrag drin ist, nach einem Neuaufbau
  also über die neue.
- Ist keine drin, geht die Benachrichtigung verloren. Der VEN liest beim
  nächsten Abruf, was er verpasst hat. Der LocalController fragt nach dem
  Neuaufbau sofort.
- Über die REST-API, ob HTTPS oder Tunnel, ist ein solcher Callback keiner:
  400.
- Webhook und Verbindung lassen sich in einem Abo mischen. Jede
  `objectOperation` geht auf ihrem eigenen Weg.

### 9.4 Abweichungen von Abschnitt 6

1. Eine Liste kommt als `{"events": […]}`, weil eine Nutzlast ein Objekt sein
   muss.
2. `GetResource` und `GetSubscription` sind dazugekommen, weil der VTN sie hat.
3. Eine Property, die eine Action nicht kennt, wird nicht übergangen, sondern
   mit `FormationViolation` beantwortet. So wird ein vertippter Filter nicht
   als „kein Filter“ gelesen.
4. Fehlercodes:
   - Für eine kaputte Nachricht gelten die Codes von OCPP-J:
     `RpcFrameworkError` mit Id „-1“, `MessageTypeNotSupported`,
     `FormationViolation` und `NotImplemented`.
   - Für eine Antwort des VTN steht der Code für den HTTP-Status:
     `BadRequest`, `Unauthorized`, `Forbidden`, `NotFound`, `Conflict`,
     `InternalError` und `GenericError`.
   - Die Details sind das Problem mit `status`.
5. Listen in der Query werden mit Kommas verbunden, so wie der VTN sie liest.
6. Es gibt weder CBOR noch Signaturen.

### 9.5 Tests

| Suite | Tests |
|---|---|
| WWCP_OpenADR | 119 (57 neu) |
| GridOperator | 229 (9 neu) |
| LocalController | 665 (4 neu) |
| WWCP_Node | 1653 |
| Energiezähler | 402 |
| Env | 371, davon 31 in `LC_2_GO` (16 neu) |

Neu sind:

- **WWCP_OpenADR:**
  - die Rahmen und was keiner ist;
  - jede Action als REST-Operation, und was eine Action nicht nimmt;
  - Antworten und HTTP-Fehler;
  - zwei Enden im Gespräch, auch mit einem Handler, der zurückfragt;
  - Zeitüberschreitung und Ende der Verbindung.
- **Netzbetreiber:**
  - jede Action über ws und über ssh;
  - die Fehler einer Anfrage;
  - Object Privacy über zwei VENs;
  - `Notify` nur an den eigenen VEN, über ws und über ssh;
  - der Verbindungs-Callback über HTTPS wird abgelehnt;
  - was nicht hereinkommt: ein ausgeschalteter Eintrag, die falsche Bindung
    (beide Richtungen), ein falsches Passwort.
- **LocalController**, gegen einen VTN der Nachrichten:
  - Lesen, Report und genau ein Abo;
  - `Notify` führt sofort zum Abruf;
  - ein Fehler des VTN;
  - eine Verbindung, die nicht steht.
- **`LC_2_GO`:**
  - WebSocket, WebSocketTLS, WebSocketMTLS und SSH je in beiden Bindungen;
  - ein Limit erreicht den LocalController per `Notify` vor dem nächsten
    Poll, über ws, wss und ssh, und der Widerruf ebenso;
  - ein anderer VEN und ein ausgeschalteter Eintrag, in beiden Bindungen;
  - über SSH gibt es keine Kommandozeile und nicht das Subsystem der anderen
    Bindung.

### 9.6 Offen

- Ein ausgeschalteter oder gelöschter Eintrag schließt eine offene Verbindung
  nicht, wie in Schritt A.
- `Notify` wird nicht gepuffert, solange der VEN nicht verbunden ist.
- MQTT.
- Die Weboberflächen von LocalController und Netzbetreiber zeigen Bindung und
  Abos nicht.
