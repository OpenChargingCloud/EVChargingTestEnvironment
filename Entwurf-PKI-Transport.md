# Entwurf: WebSocket und SSH zwischen CSMS und PKI

Stand 2026-11-22. Gebaut, siehe Abschnitt 6.

## 1. Ziel

`CSMS_2_PKI`: Ein CSMS verbindet sich als Registrierungsstelle (RA) mit der
PKI über

- `ws://`, `wss://` und `wss://` mit Client-Zertifikat (mTLS),
- `ssh://`, mit dem SSH-Subsystem `pki@open.charging.cloud`.

Er meldet sich mit Passwort, TOTP, SSH-Schlüssel oder Zertifikat an. Über die
Leitung schickt er OCPP-ähnliche Anfragen. Die PKI macht daraus zum Beispiel
aus dem CSR einer Ladestation ein Zertifikat.

## 2. Befund

| Baustein | Ort | Stand |
|---|---|---|
| Store, CSR signieren (`TryIssueFromCSR`), sperren (`TryRevoke`), CRL, Status | PKI, `PKIStore` | da |
| Was über EST signiert wird (Aussteller, Profil, Laufzeit je Label) | PKI, `ESTSettings` | da |
| `ConnectionStore`, `WebSocketLogins`, Login-Prüfung, `AddSSHSubsystem`, `StreamWebSocketServer` | WWCP_Node | da, vom Netzbetreiber und Energiezähler genutzt |
| Verbindungen der PKI | – | **fehlten**: kein `ConnectionStore`, kein WebSocket-Port |
| Nachrichten zwischen RA und PKI | – | **fehlten** |

## 3. Entscheidungen

Die Entscheidungen folgen den bisherigen Transporten. Wo es eine Wahl gab,
steht sie hier.

1. **Rahmen wie OCPP-J.** `[2, id, Action, {…}]`, `[3, id, {…}]`,
   `[4, id, Code, Text, {…}]`. Über WebSocket ist jede Nachricht eine
   Textnachricht. Über SSH ist jede Nachricht eine Zeile, abgeschlossen mit
   LF. Höchstens 1 MiB je Nachricht.
2. **Signiert wird, was EST signiert.** `SignCertificate` nennt ein EST-Label
   oder keins. Aussteller, Profil und Laufzeit kommen aus dem Abschnitt
   `pki.est`. Ohne passendes Label wird nichts signiert. So gibt es keinen
   zweiten Weg, der anders entscheidet als EST.
3. **Sperren darf eine RA nur, was sie selbst hat signieren lassen.** Das
   Zertifikat trägt `createdBy = "ra:<login>"`. Status, CA-Zertifikate und CRL
   darf jede RA abfragen. Rollen über Login-Gruppen (wie beim Zähler) gibt es
   nicht.
4. **Verbindungstypen:** `PKI` (ausgehend, beim CSMS und jedem anderen
   Knoten) und `RegistrationAuthority` (eingehend, bei der PKI), dazu
   `ConnectionUse.PKI` und `ProtocolVersion.PKI`. `PKI` steht in
   `ConnectionRules.Every`.
5. **Port 2364** für den WebSocket-Server der PKI (Abschnitt
   `webSocketServer`, Listener `ra`). 2346 bis 2363 sind vergeben.

## 4. Nachrichten

| Action | Anfrage | Antwort |
|---|---|---|
| `SignCertificate` | `csr` (PEM), `label`? | `status`, `certificateChain` (Zertifikat und Sub-CAs, PEM), `certificateId` (SHA-256), `statusInfo`? |
| `GetCACertificates` | `label`? | `status`, `certificateChain` (Sub-CA bis Root, PEM) |
| `GetCertificateStatus` | `certificateId` oder `serialNumber` | `status`, `certificateStatus` (`valid`, `revoked`, `expired`, `notYetValid`, `unknown`), `certificateId`, `revokedAt`?, `revocationReason`? |
| `GetCRL` | `issuerId` oder `label`? | `status`, `crl` (PEM), `issuerId` |
| `RevokeCertificate` | `certificateId` oder `serialNumber`, `reason` (RFC 5280) | `status`, `statusInfo`? |
| `GetOCSPResponse` | `ocspRequest` (DER in Base64, höchstens 16384 Zeichen) | `status`, `ocspResponse` (DER in Base64, signiert vom Responder der CA), `statusInfo`? |

`status` ist `Accepted` oder `Rejected`. `statusInfo.reasonCode` sagt den
Grund: `UnknownLabel`, `InvalidCSR`, `NotSigned`, `NotFound`, `NotYours`,
`UnknownCA`, `NoCRL`, `UnknownReason`, `NotRevoked`, `NotSaved`,
`InvalidOCSPRequest`.

Fehler als `[4, …]`: `NotImplemented` für eine unbekannte Action,
`FormatViolation` für eine kaputte Nachricht oder fehlende Felder,
`SecurityError`, wenn die Verbindung inzwischen abgeschaltet ist,
`InternalError` sonst.

## 5. Reihenfolge

| # | Repo | Inhalt |
|---|---|---|
| 1 | WWCP_Node | Typen, Namen, Nachrichten, Kanal, Client-Seite |
| 2 | PKI | Store, Logins, Port 2364, SSH-Subsystem, Antworten |
| 3 | CSMS, WWCP_OCPP | nur Testlisten der Verbindungstypen |
| 4 | Superprojekt | `CSMS_2_PKI` |

## 6. Was gebaut ist

**WWCP_Node**

- `ConnectionType.PKI` und `ConnectionType.RegistrationAuthority`,
  `ConnectionUse.PKI`, `ProtocolVersion.PKI`. Die Richtungsregeln stehen in
  `ConnectionRules`, wie bei Zähler und Netzbetreiber.
- `PKI/PKIWireNames.cs`: Subprotokoll `pki`, Subsystem
  `pki@open.charging.cloud`, Größengrenze.
- `PKI/PKIMessages.cs`: `PKIMessage` (Rahmen, `ToLine`, `TryParse`),
  `PKIActions`, `PKIErrorCodes`, `PKIException`, je Action ein Request- und
  ein Response-Record mit `ToJSON` und `Parse`.
- `PKI/PKIMessageChannel.cs`: ein Strom von Zeilen. Anfragen gehen raus, die
  Antworten werden über die Id zugeordnet. Eingehende Anfragen beantwortet
  ein Handler. Endet der Strom, bekommen wartende Anfragen eine `IOException`.
- `StreamWebSocketClient` und `StreamWebSocketServer` haben `TextLines`. Dann
  ist jede Textnachricht eine Zeile des Stroms und jede geschriebene Zeile
  eine Textnachricht. So läuft derselbe Kanal über WebSocket und SSH.
- `Connecting/WWCPNode.PKIs.cs`: `ConnectToThePKI` über ws, wss, wss mit
  Client-Zertifikat und ssh. Nach einem Abbruch wird nach 3 s neu verbunden.
  `HangUp` legt auf. Die API dazu: `AskThePKI`, `SignCertificateAtThePKI`,
  `GetCACertificatesOfThePKI`, `GetCertificateStatusAtThePKI`,
  `GetCRLOfThePKI`, `RevokeCertificateAtThePKI`, `GetOCSPResponseOfThePKI`,
  `PKIConnectionIsUp`,
  `PKITransportOf`.
- Test: `PKIMessagesTests` (Rahmen, Kanal, Ende, Typen).

**PKI**

- `PKI.RegistrationAuthorities.cs`: `ConnectionStore` nur mit
  `RegistrationAuthority` eingehend, `WebSocketLogins`, `ClientTrust`,
  `SshKey`, die Tür `RASignIns`, der `StreamWebSocketServer` mit
  `TextLines` und `LoginSSHSignIns` für das Subsystem. Ein Login über SSH
  bekommt nur das Subsystem und keine Kommandozeile.
- `Configuration/RAServerConfiguration.cs`: Abschnitt `webSocketServer`,
  Port 2364, standardmäßig aus.
- `PKI` ist `partial`, nennt den Listener `ra`, startet den Server in
  `OnListening` und hält ihn in `OnStopping` an.
- Jede Anfrage prüft, ob die Verbindung noch eingeschaltet ist.
- `RALoginsIn` listet, wer gerade angemeldet ist.

**CSMS, WWCP_OCPP**

- In `ConnectingTests` und `ConnectionRulesTests` steht jetzt `PKI` in der
  Liste der Verbindungstypen.

**Superprojekt**

- `AHopByHopTest` kennt die PKI bei Logins, Client-Vertrauen und
  `IsConnectedAt`.
- `CSMS_2_PKI` (17 Tests):
  - WebSocket, WebSocketTLS, WebSocketMTLS und SSH mit Passwort, TOTP,
    Schlüssel und Zertifikat. Jeweils: CSR signieren, Kette prüfen,
    CA-Zertifikate lesen, Status nach Id und Seriennummer, sperren, Status
    `revoked`, CRL lesen, die das Zertifikat nennt;
  - unbekanntes Label und kein Label werden abgelehnt, nichts wird signiert;
  - ein kaputter CSR wird abgelehnt;
  - fremde Zertifikate werden nicht gesperrt (`NotYours`);
  - eine unbekannte Action gibt `NotImplemented`, die Verbindung bleibt;
  - eine abgeschaltete Verbindung kommt nicht herein;
  - über SSH gibt es keine Kommandozeile.

## 7. SignCertificate der Ladestation über die Verbindung

Stand 2026-11-22. Gebaut.

**Konfiguration.** In `certificateSigning` darf `chargingStationCertificate`
bzw. `v2gCertificate` statt einer EST-Adresse die URL einer Verbindung des
CSMS vom Typ `PKI` sein (`ws://`, `wss://`, `ssh://`). Das EST-Label steht
hinter `#`:

```json
"certificateSigning": {
  "chargingStationCertificate": "wss://pki.example.org:2364/csms-01#stations",
  "v2gCertificate":             "ssh://csms-01@pki.example.org:22#secc"
}
```

- Das Label: Buchstaben, Ziffern, `-` und `_`, höchstens 64 Zeichen. Ohne
  `#` signiert die PKI ohne Label.
- Eine EST-Adresse darf weiterhin kein Fragment haben.
- Ein `apiKey` wird für die Verbindung nicht gebraucht. Es gilt die Anmeldung
  der Verbindung.
- Die Adresse wird so zurückgeschrieben, wie sie angegeben wurde
  (`OriginalString`).
- Der Host einer Verbindung gibt keinen OCSP-Responder frei (`MayAsk`). Das
  bleibt den EST-Hosts und `ocspResponders` vorbehalten.

**Ablauf.** Alles vor dem Signieren bleibt wie bei EST: CSR prüfen,
Common Name = Station, einer zur Zeit, sofort `Accepted`. Dann:

1. `SignOverTheConnection` sucht die ausgehende `PKI`-Verbindung, deren URL
   der Adresse ohne Fragment entspricht.
2. Es schickt `SignCertificate` mit dem PEM-CSR der Station und dem Label.
3. Aus der Antwortkette nimmt es das Zertifikat und die Sub-CAs. Ein Root
   wird nicht mitgeschickt.
4. Weiter geht es wie bei EST mit `CertificateSigned`, `StationCertificates`
   und dem Eintrag `signed`.

Schlägt etwas fehl, steht der Eintrag auf `failed` mit Grund:

- keine Verbindung mit dieser URL;
- `Rejected` mit `reasonCode`;
- Zeitüberschreitung oder Abbruch.

**Tests**

- CSMS: `CertificateSigningConfigurationTests` (URL und Label, Ablehnungen,
  EST unverändert).
- Superprojekt, `CSMS_2_PKI` (jetzt 22 Tests):
  - `Station_SignCertificate_IsSignedByThePKI` über ws, wss und ssh. Eine
    Station mit OCPP 2.1 schickt `SignCertificate`. Sie bekommt
    `CertificateSigned` mit Zertifikat und Sub-CA, für ihren eigenen
    Schlüssel. Bei der PKI trägt das Zertifikat `ra:<login>` des CSMS.
  - Ein Label, unter dem die PKI nichts signiert: Die Station bekommt
    `Accepted`, aber kein Zertifikat. Der CSMS vermerkt `UnknownLabel`.
  - Eine Adresse ohne passende Verbindung: Der CSMS vermerkt, welche
    Verbindung fehlt.

### 7.1 Offen

- Die Weboberfläche, die HTTP-API und die Kommandozeile der PKI kennen die
  Verbindungen und WebSocket-Logins noch nicht.
- Die Weboberfläche des CSMS erklärt die neue Form der Adresse noch nicht.
- Eine abgeschaltete Verbindung wird nicht getrennt, sie bekommt aber auf
  jede weitere Anfrage `SecurityError`.
- Keine Server-Schlüsselerzeugung (`serverkeygen`) über die Nachrichten:
  ein privater Schlüssel geht nicht über diese Leitung.

## 8. GetCertificateStatus der Ladestation über die Verbindung

Stand 2026-11-23. Gebaut.

**Nachricht.** `GetOCSPResponse` (Abschnitt 4) trägt eine OCSP-Anfrage nach
RFC 6960 zur PKI und deren Antwort zurück, beide als DER in Base64. Die PKI
beantwortet sie mit `PKIStore.AnswerOCSP`, genau wie `POST v1/pki/ocsp`.
Die Antwort ist also vom OCSP-Responder der CA signiert, und Station und EV
prüfen sie wie bisher. Hat die PKI die CA nicht, ist die Antwort `Rejected`
mit `UnknownCA` (und der unsignierten Antwort `unauthorized`). Jede
Registrierungsstelle darf fragen: Über HTTP ist der Responder ohnehin offen.

**Ablauf im CSMS.** `GetCertificateStatus` der Station:

1. Der CSMS baut die OCSP-Anfrage aus Hash-Algorithmus, Issuer-Hashes und
   Seriennummer, wie bisher.
2. Er fragt jede Verbindung, die `certificateSigning` nennt (jede URL nur
   einmal), mit `GetOCSPResponse`. Welchen Responder die Station nennt,
   spielt dabei keine Rolle: Gefragt wird die eigene PKI, nichts sonst.
3. Die erste PKI, die die CA hat, liefert die Antwort. `UnknownCA` heißt:
   die nächste Verbindung.
4. Hat keine sie, geht es weiter wie bisher über HTTP an den Responder der
   Station, wenn `MayAsk` das erlaubt. Sonst `Failed`: `refused`, oder
   `failed` mit dem Grund, wenn eine Verbindung nicht gefragt werden konnte.

Der Eintrag in `CertificateStatuses` hat jetzt `via`: die URL der
Verbindung, über die gefragt wurde, oder null bei HTTP.

**Tests**

- WWCP_Node: `PKIMessagesTests.GetOCSPResponse_CarriesDERInBase64`.
- Superprojekt, `CSMS_2_PKI` (jetzt 26 Tests):
  - `Station_GetCertificateStatus_IsAnsweredByThePKI` über ws, wss und ssh.
    Die Station nennt einen Responder, den der CSMS über HTTP nicht fragen
    würde. Sie bekommt eine vom Responder der Sub-CA signierte Antwort:
    `good`, und nach der Sperrung `revoked`. Der Eintrag nennt die
    Verbindung als `via`.
  - `Station_GetCertificateStatus_OfACAThePKIDoesNotHave_IsNotAsked`: eine
    fremde CA, ein fremder Responder, also `Failed` und `refused`.

## 9. Verbindungen und Logins der Registrierungsstellen verwalten

Stand 2026-11-24. Gebaut (die Seiten: siehe 9.3).

Bisher gab es die Verwaltung von Verbindungen, Logins und Client-Vertrauen
nur in WWCP_OCPP. Sie ist jetzt in WWCP_Node, damit jeder Knoten sie hat:
zuerst die PKI, später auch Netzbetreiber, EMSP und Zähler.

### 9.1 WWCP_Node

- `Web/NodeHTTPAPI.Connections.cs`: die Routen der Verbindungen
  (`v1/configuration/connections`, `…/authentications`, `…/sshKeys`,
  `v1/status/connections`, Test, Pin-Set-Signer), unverändert aus
  `OCPPConnectingHTTPAPI` übernommen. Ein Knoten schaltet sie mit
  `RegisterConnectionRoutes(resource)` ein.
- `Web/NodeHTTPAPI.WebSocketLogins.cs`: die Routen der Logins eines
  WebSocket-Servers unter einer Wurzel: `logins` (Passwort, TOTP mit
  Bindung, SSH-Schlüssel, Key-Pins), `groups`, `trust`. Eingeschaltet mit
  `RegisterWebSocketLoginRoutes(new WebSocketLoginRoutes(root, logins,
  trust, resource, noun, tags, loginRemoved, marks))`. `WebSocketLoginMark`
  beschreibt Markierungen wie `localController` und `gateway`; höchstens
  eine ist wahr. Ein SSH-Schlüssel für einen Namen, der auch ein Konto ist,
  wird abgelehnt.
- `CommandLine/LoginKeysCommand.cs` und `LoginPinsCommand.cs`: die Befehle
  für SSH-Schlüssel und Key-Pins der Logins, als Basisklassen.

### 9.2 WWCP_OCPP

- `OCPPConnectingHTTPAPI` ruft nur noch `RegisterConnectionRoutes` auf.
- `OCPPServingHTTPAPI.OCPPServer` behält `GET`/`PUT ocpp-server` und
  registriert den Rest mit `RegisterWebSocketLoginRoutes` unter
  `v1/configuration/ocpp-server`, mit „charging station“ und den
  Markierungen `LocalController` und `Gateway`.
- `StationKeysCommand` und `StationPinsCommand` erben von den
  Basisklassen. Name, Texte und Konstruktor bleiben gleich.

Routen, JSON und Texte für OCPP sind unverändert.

### 9.3 PKI

- `PKIAccess.RegistrationAuthorities` (`registrationAuthorities`): neue
  Ressource. Lesen dürfen Operator und Registrar (sie lesen alles),
  ändern nur Administratoren. Wer eine Registrierungsstelle zulässt, lässt
  in ihrem Namen CSRs signieren.
- `HTTPAPI/PKIHTTPAPI.RegistrationAuthorities.cs`:
  - die Routen der Verbindungen an dieser Ressource;
  - `GET v1/configuration/ra-server`: `configuration` (Abschnitt
    `webSocketServer`), `state` (`running`, `tls`, `ssh`, `sshPort`),
    `connected` (wer gerade verbunden ist), `signed` (Zertifikate je
    Login, nach `CreatedBy = ra:<login>`);
  - die Login-Routen unter `v1/configuration/ra-server` (`logins`,
    `groups`, `trust`) mit „registration authority“ und ohne Markierungen.
  - Ein `PUT` des Servers gibt es nicht: Er wird in der Datei geändert,
    danach startet man die PKI neu.
- `CommandLine/RaCommands.cs`:
  - `ras`: Server, jede Registrierungsstelle mit Verbindung (an/aus),
    verbunden oder nicht, Zahl der signierten Zertifikate, womit sie sich
    anmeldet; Logins ohne Verbindung;
  - `raKeys` und `raPins`: SSH-Schlüssel und Key-Pins, mit dem, was eine
    Registrierungsstelle draußen hält.
- Seiten: Die gemeinsamen Seiten für Verbindungen, Logins und
  Client-Vertrauen liegen in `WWCP_Node/Frontend`. Die PKI zeigt sie für
  ihre Registrierungsstellen, dazu eine Übersicht des Servers.

Eine Registrierungsstelle wird zweimal eingetragen, wie jeder, der sich
mit einem Knoten verbindet:

1. als eingehende Verbindung vom Typ `RegistrationAuthority`
   (`wss://host:2364/<login>` oder `ssh://<login>@host:22`); sie wird
   abgeschaltet, um die Stelle auszusperren;
2. als Login mit Passwort, TOTP, SSH-Schlüssel oder Key-Pin.

### 9.4 Tests

- PKI, `RegistrationAuthorityAPITests`:
  - ein Administrator trägt Verbindung und Login ein (Passwort, Key-Pin),
    schaltet sie ab und an und entfernt beides; Markierungen von OCPP
    werden abgelehnt;
  - Registrar und Operator lesen, ändern aber nichts (`403`);
  - `ras`, `raPins` und `raKeys` an der Konsole.
- OCPP, CSMS: die bestehenden Tests der Routen und Befehle, unverändert.

### 9.5 Offen

- `PUT` des Servers der Registrierungsstellen (Port, TLS, SSH) zur
  Laufzeit.

## 10. Netzbetreiber, EMSP und Zähler auf den gemeinsamen Teilen

Die drei hatten Verbindungen, Logins und angenommene Ketten schon
(`ConnectionStore`, `WebSocketLogins`, `ClientTrustStore`), aber keine
Routen, Seiten und Befehle dafür. Jetzt wie die PKI in §9:
`RegisterConnectionRoutes`, `RegisterWebSocketLoginRoutes`, ein `GET` des
Servers, ein Befehl zum Auflisten und die Unterklassen von
`LoginKeysCommand`/`LoginPinsCommand`.

| Knoten | Ressource | Wurzel der Login-Routen | Login heißt | Befehle |
|---|---|---|---|---|
| Netzbetreiber | `openadr` (bestehend) | `v1/configuration/ven-server` | local controller | `vens`, `venKeys`, `venPins` |
| EMSP | `partners` (bestehend) | `v1/configuration/ocpi-server` | charge point operator | `cpos`, `cpoKeys`, `cpoPins` |
| Zähler | `meterReaders` (neu: Viewer und Auditor lesen, nur Admins ändern) | `v1/configuration/reader-server` | meter reader | `readers`, `readerKeys`, `readerPins` |

- Der `GET` des Servers sagt Konfiguration, Zustand (`running`, `tls`,
  `ssh`, `sshPort`) und wer verbunden ist; beim Zähler dazu die
  SunSpec-Rolle jedes Logins (`roles`).
- `cpoKeys` und `readerKeys` gehen über das bestehende
  `TryAddCPOSSHKey`/`TryAddMeterReaderSSHKey`: ein Name, der auch ein
  Konto ist, wird abgelehnt.
- Der Zähler hat jetzt einen `SshKey` (`meter@<Rechner>`), weil das JSON
  der Verbindungen ihn nennt.
- `localControllers` des Netzbetreibers bleibt, was es war: die
  HTTPS-Logins mit Client-ID und Secret. Die WebSocket-/SSH-Seite ist
  `vens`.

### 10.1 WWCP_Node

- `POST configuration/connections` und `.../update` reichen
  `ocpiVersion` durch; vorher ließ sich eine CPO-Verbindung über die API
  nicht anlegen.
- Die Seite der Verbindungen kennt die Typen von OCPI, Modbus und
  OpenADR (`useOf`, wie `ConnectionEntry.UseOf`). Sie fragt nach der
  Version von OCPI, der Bindung von OpenADR und dem VEN, jeweils nur auf
  einem Knoten, der solche Verbindungen hat, und schickt jedes Feld nur
  für den Typ, der es trägt. Die Frage nach OCPP entfällt, wo der Knoten
  keine OCPP-Verbindung hat.
- `WebSocketLoginRoutes.WhyGroupStays`: eine Gruppe, die der Knoten
  braucht, wird mit `409` abgelehnt. Der Zähler nutzt das für die vier
  SunSpec-Gruppen; außerhalb der API entfernte Gruppen legt er weiter neu
  an.
- Rechte im Frontend ohne Rücksicht auf Groß- und Kleinschreibung
  (`holds`), wie der Knoten sie nimmt. Er sagt
  `registrationauthorities:read`. Die Menüeinträge der PKI fragten nach
  `registrationAuthorities:read` und erschienen deshalb nie.

### 10.2 Tests

- Netzbetreiber `VENServerAPITests`, EMSP `OCPIServerAPITests`, Zähler
  `MeterReaderAPITests`: Routen, Rechte nach Rolle, ein über HTTP
  angelegter Login meldet sich wirklich an, und die Befehle.
- WWCP_Node, Frontend: die Felder für OCPI, OpenADR und VEN sowie `holds`.

### 10.3 Offen

- Das Formular der Verbindungen zeichnet sich bei einem Wechsel des Typs
  nicht neu. Die Felder für OCPI, OpenADR und VEN stehen deshalb da, wo
  der Knoten solche Typen hat, und werden nur für den passenden Typ
  geschickt.
