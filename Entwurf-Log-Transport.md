# Entwurf: GetLog über HTTP, HTTPS und SFTP zum Log-Server

Stand 2026-11-24. Gilt für `CS_2_Log` und `LC_2_Log`. Gebaut, siehe Abschnitt 6.
Dazu `UpdateFirmware` über HTTP, HTTPS und SFTP vom Download-Server
(`CS_2_Download`, `LC_2_Download`), siehe Abschnitt 7.

## 1. Ziel

- Das CSMS fragt eine Ladestation (CS) oder einen LocalController (LC) mit
  `GetLog` nach seinem Log. Der Knoten lädt es auf den Log-Server
  (`OpenChargingCloud/LogServer`).
- Wege: `PUT` über `http://` und `https://`, und `sftp://` nach dem Whitepaper
  „SFTP for Logfile Uploads and Firmware Downloads“.
- Der Knoten lädt nur zu einem Log-Server hoch, den seine eigene Konfiguration
  nennt. Eine Anfrage wählt unter diesen Servern aus. Einen neuen Server führt
  sie nie ein.
- Der Knoten meldet dem CSMS mit `LogStatusNotification`, wie der Upload lief:
  `Uploading`, dann `Uploaded`, `UploadFailure`, `PermissionDenied` oder
  `NotSupportedOperation`.

## 2. Befund

| Baustein | Ort | Stand |
|---|---|---|
| SFTP-Upload und -Download, Fileserver als SSH-Peers, eigene Schlüssel je Server und Login (`UploadFileAsync`, `JudgeFileTransfer`) | WWCP_Node `WWCPNode.FileTransfers.cs` | da |
| Antwort auf `GetLog`, Versuche, Abbruch durch eine neue Anfrage, Meldungen (`FileTransfers.StartUpload`) | WWCP_OCPP_Node | da, aber **nur `sftp://`**. `http(s)://` ergab `NotSupported` |
| Wer die Datei liefert (`FileTransfers.FileProvider`) | – | **fehlte** bei CS und LC. Jedes `GetLog` ergab `NotSupported` |
| `GetLog` an den LC selbst | WWCP_OCPPv2.1_LocalController | **fehlte**: kein Handler, kein `IFileTransferNode` |
| HTTP-Uploads | – | **fehlten** ganz |
| Log-Server mit `PUT` und SFTP-Bereich `/upload`, Uploader als Konten | LogServer | da, aber nicht im Superprojekt |

## 3. Entscheidungen (Achim, 2026-11-24)

1. **Bauen und testen.** HTTP-Uploads kommen in WWCP_Node und WWCP_OCPP_Node.
   CS und LC liefern ihr eigenes Log. Danach folgen die Tests.
2. **HTTP-Upload ist ein `PUT`**: die Datei als Body nach
   `<Verzeichnis>/<Dateiname>`, mit `If-None-Match: *`. Ein multipart-`POST`
   gibt es nicht.
3. **Der echte Log-Server** kommt als Submodul `libs/LogServer` ins
   Superprojekt. Die Tests laden zu ihm hoch.
4. **Zugangsdaten:** Login und Passwort können aus der URL der Anfrage kommen,
   wie ein CSMS sie in `GetLog` mitgibt
   (`http://CS001:<pw>@host:8363/CS001/`). Sie können auch aus der
   Konfiguration kommen. **Die der URL gehen vor.** Das Ziel muss trotzdem ein
   konfigurierter Server sein.

## 4. Was über die Leitung geht

### 4.1 HTTP und HTTPS

```
PUT /<Verzeichnis>/<Dateiname> HTTP/1.1
If-None-Match: *
Content-Type: application/octet-stream
Content-Length: <Größe>
Authorization: Basic <login:passwort>      (falls es welche gibt)
User-Agent: WWCP-Node/1.0
```

Das Verzeichnis ist der Pfad der URL. Die Antwort zählt so:

| Antwort | Ergebnis (`FileTransferOutcome`) | `UploadLogStatus` | Neuer Versuch |
|---|---|---|---|
| 2xx | `Done` | `Uploaded` | – |
| 412 | `FileExists` | `UploadFailure` | nein |
| 401 | `LoginRefused` | `PermissionDenied` | nein |
| 403 | `PermissionDenied` | `PermissionDenied` | nein |
| 404 | `NoSuchFile` | `PermissionDenied` | nein |
| 405, 501 | `MethodNotAllowed` | `NotSupportedOperation` | nein |
| 413 | `TooLarge` | `UploadFailure` | nein |
| 408, 429, 5xx | `Failed` | `UploadFailure` | ja |
| Weiterleitung, andere 4xx | `RejectedByServer` | `UploadFailure` | nein |
| Zertifikat nicht geglaubt | `CertificateRefused` | `UploadFailure` | nein |
| keine Verbindung, Leerlauf | `Unreachable` | `UploadFailure` | ja |

- **Weiterleitungen** werden nicht verfolgt.
- **Cookies** werden nicht gespeichert.
- **Namen** löst der DNS-Client des Knotens auf (`NameResolution.HTTPHandler`).
- **Server über HTTPS** prüft der Knoten wie den Server einer `wss://`-Verbindung.
  Es gelten seine TLS-Wurzeln für Verbindungen (`ConnectionsCertificateUsage`)
  und die der Maschine. Gelernt wird dabei nichts.

### 4.2 SFTP

Unverändert nach dem Whitepaper, nur jetzt zum Log-Server:

- Der Knoten meldet sich mit seinem eigenen Schlüssel für diesen Server und
  Login an.
- Er hält den Server an einen gepinnten Hostkey.
- Er lädt als `<name>.part` hoch und benennt die Datei nach dem letzten Byte um.
- Er überschreibt nichts.

### 4.3 Die Datei

`NodeLogFiles` liefert die Einträge des Event-Logs des Knotens aus der
angefragten Zeit, ein JSON-Objekt je Zeile.

- `DiagnosticsLog` (und `Diagnostics` von OCPP 1.6) enthält alle Einträge.
- `SecurityLog` enthält die Einträge mit dem Tag `security`.
- `DataCollectorLog` führt der Knoten nicht und antwortet `NothingToSend`.

Der Dateiname ist `<Identität>-<LogType>-<RequestId>-<yyyyMMddTHHmmssfffZ>.jsonl`.

### 4.4 Die Konfiguration des Knotens

```json
"httpFileServers": [
  { "id": "logs", "url": "https://logs.example.org:8363/{id}/",
    "user": "CS001", "password": "..." }
]
```

- **`url`:** Das Verzeichnis, in das hochgeladen werden darf, mit allem
  darunter. Schema, Host und Port müssen genau passen. Der Pfad zählt als
  Präfix. `{id}` steht für die Identität des Knotens
  (`FileTransferIdentity`).
- **`user` und `password`:** Optional, HTTP Basic. Sie gelten nur, wenn die URL
  der Anfrage keine Zugangsdaten trägt.
- **Lesen:** Der Abschnitt wird bei jeder Anfrage aus der Datei gelesen. Eine
  Änderung gilt ab der nächsten Anfrage.
- **Fehlerhafte Einträge** werden weggelassen, mit einer Warnung im Log.
  Höchstens 8 Einträge.

### 4.5 Was eine Anfrage ablehnt, bevor etwas gesendet wird

| Fall | `reasonCode` |
|---|---|
| Schema weder `sftp`, `http` noch `https` | `NotSupported` |
| leerer Login in der URL, Query oder Fragment, `..`, `%2F`, `%5C` oder Steuerzeichen im Pfad | `InvalidURL` |
| Schema, Host oder Port keines konfigurierten Servers, oder Pfad außerhalb seines Verzeichnisses | `FileServerNotPermitted` |
| nichts zu senden | `NothingToSend` |

Ein Code gilt für alles, was die Konfiguration nicht hat. Die Antwort sagt
nicht, welche Server der Knoten kennt. Das Log des Knotens sagt es genau, und
`FileServerNotPermitted` ist zugleich das Sicherheitsereignis
`FileTransferRefused`.

## 5. Reihenfolge

| # | Repo | Commit | Inhalt |
|---|---|---|---|
| 1 | WWCP_Node | `37420ec` | `HTTPFileServer`, `JudgeHTTPFileTransfer`, `UploadFileOverHTTPAsync` |
| 2 | WWCP_OCPP | `2d920247` | `http(s)://` in `FileTransfers`, `NodeLogFiles`, `GetLog` an den LC |
| 3 | ChargingStation | `48b425f` | `FileProvider` = eigenes Log |
| 4 | LocalController | `8d4a4b4` | `FileProvider` = eigenes Log |
| 5 | LogServer | `6ce500f` | `.part` erst nach dem Umbenennen übernehmen (Abschnitt 6.1) |
| 6 | Superprojekt | `9f7277c` | `CS_2_Log`, `LC_2_Log`, LogServer in Lösung und Tests |

Das Submodul `libs/LogServer` selbst steckt in `0a23d0f`. Eine parallele
Session hat den gestagten Eintrag dort mit committet, unter einer Nachricht zu
WWCP_Node.

## 6. Was gebaut ist

**WWCP_Node**

- `HTTPFileServer` (`id`, `url`, `user`, `password`, `Problem`,
  `AllowsDirectory`) und `WWCPNode.HTTPFileServers`.
- `JudgeHTTPFileTransfer(URL)`. Entscheidet ohne Netz, wie
  `JudgeFileTransfer` für SFTP.
- `UploadFileOverHTTPAsync(URL, Content, FileName, …)`: ein Versuch, ein `PUT`.
  - Die Zugangsdaten der URL gehen vor die der Konfiguration.
  - Über `http://` mit Passwort steht bei jedem Upload eine Warnung im Log.
  - Das Passwort selbst steht nie im Log.
  - Es gelten Leerlauf- und Verbindungs-Timeout wie bei SFTP
    (`FileTransferIdleTimeout`, `FileTransferConnectTimeout`).
- `FileTransferOutcome` hat `CertificateRefused`, `MethodNotAllowed` und
  `RejectedByServer` dazu.
- `FileTransferJudgement` hat `HTTPLocation` und `HTTPServer`.
- `OnHTTPFileServerCertificateRefused` meldet ein abgelehntes Zertifikat.

**WWCP_OCPP**

- `FileTransfers.StartUpload` nimmt `sftp://`, `https://` und `http://` und
  wählt danach das Urteil und den Upload.
- `MethodNotAllowed` wird zu `NotSupportedOperation`.
- Die Info von `InvalidURL` nennt kein Schema mehr.
- `DeviceModelBridge` macht aus einem abgelehnten Zertifikat das
  Sicherheitsereignis `InvalidFileServerCertificate`.
- `NodeLogFiles`: `Provider(Node)`, `Of(Node, Request)`, `FileName`.
- `TestLocalControllerNode` ist ein `IFileTransferNode`.
  - `GetLog` und `UpdateFirmware` für den LC selbst gehen an den Prozessor der
    Brücke.
  - Ohne Prozessor antwortet der LC `Rejected`/`NotSupported`.
  - `SendLogStatus` schickt `LogStatusNotification` an das CSMS.
- `FileTransferBridgeTests`: Ein `https://`-Server, den die Konfiguration nicht
  nennt, ist `FileServerNotPermitted`. `ftp://` ist `NotSupported`.

**ChargingStation, LocalController**

- `FileTransfers.FileProvider = NodeLogFiles.Provider(this)`.

**LogServer**

- `TakeInSftpUpload` übergeht `<name>.part`. Übernommen wird die Datei, wenn
  sie umbenannt wird. Siehe 6.1.

**Superprojekt**

- `libs/LogServer` liegt in `EVChargingTestEnvironment.slnx` und wird im
  Testprojekt referenziert.
- `AHopByHopTest` hat dazu:
  - `TrustsTheTestCA(Node)`: wie `BelievesTheTestCA`, aber unabhängig vom
    Testnamen;
  - `AServerCertificateOfTheTestCA()` und `AServerCertificateOfTheTestCAAsPEM()`:
    ein Server-Zertifikat der Test-CA, als Zertifikat mit Schlüssel und als
    PEM-Paar für `web.tls.certificate`.
- `Helpers/HTTPLogServer.cs` ist ein kleiner HTTP/1.1-Server der Tests.
  - Er nimmt `PUT`, achtet auf `If-None-Match`, fragt optional Basic Auth ab
    und liest Content-Length und chunked.
  - Mit `AnswerNext(…)` antwortet er auf die nächsten Anfragen mit festen
    Status. Damit lassen sich 412, 503/500 und 405 prüfen, die der Log-Server
    nicht absichtlich liefert.
- `AToTheLogServer.cs` enthält alle Fälle. `CS_2_Log.cs` und `LC_2_Log.cs`
  erzeugen nur den Knoten, wie bei `AToTheEnergyMeter`.
  - Der Knoten verbindet sich mit `ws://` und Passwort mit dem CSMS, das ihn
    annimmt.
  - Der LC wird beim CSMS als LocalController markiert.
  - Das CSMS schickt `GetLog`. Die Tests zählen die `LogStatusNotification`s
    beim CSMS mit.
  - Der Log-Server läuft mit `httpListen` 127.0.0.1, eigenem Upload-Port und
    SSH mit SFTP, bei HTTPS mit einem PEM-Paar der Test-CA unter
    `web.tls.certificate`.
  - Sein Uploader heißt wie die Identität des Knotens.

### 6.1 Was die Tests gefunden haben

**SFTP zum Log-Server schlug fehl.** Der Knoten lädt nach dem Whitepaper als
`<name>.part` hoch und benennt die Datei dann um. Der SFTP-Bereich des
Log-Servers meldet beim Schließen der Datei `PutInPlace`, und der Log-Server
nahm `<name>.part` sofort in seinen Store. Das Umbenennen fand nichts mehr
(`NoSuchFile`), und das CSMS hörte `PermissionDenied`. Behoben im Log-Server
(`6ce500f`).

### 6.2 Tests

`CS_2_Log` und `LC_2_Log` je 13 Fälle, zusammen 26, alle grün.

| Test | Gegen | Was er zeigt |
|---|---|---|
| `HTTP_UploadsItsLog` | Log-Server | Zugangsdaten in der URL. `Uploading`, `Uploaded`. Beim Log-Server als HTTP-Upload des Uploaders und der Station. Das Log sagt, dass der Knoten gefragt wurde. Warnung „in the clear“, Passwort nie im Log |
| `HTTPS_UploadsItsLog_SigningInAsItsConfigurationSays` | Log-Server, TLS | URL ohne Login, Zugangsdaten aus der Konfiguration. Server geglaubt über die Test-CA. Keine Klartext-Warnung |
| `HTTP_TheLoginOfTheURL_GoesBeforeThatOfItsConfiguration` | Log-Server | Falsches Passwort in der Konfiguration. Das der URL lädt hoch. Ohne URL-Login 401, also `PermissionDenied`, ohne zweiten Versuch |
| `HTTPS_SendsNothing_ToALogServerItDoesNotBelieve` | Log-Server, TLS | Test-CA nicht geglaubt: `UploadFailure`, kein Upload, genau ein `CertificateRefused` trotz `Retries: 3` |
| `HTTP_RefusesWhatItsConfigurationDoesNotName` | Log-Server und Testserver | Wurzel, fremdes Verzeichnis, fremder Port, anderes Schema: `FileServerNotPermitted`. Leerer Login, Query: `InvalidURL`. `ftp://`: `NotSupported`. Nichts erreicht einen Server, nichts wird gemeldet, die Antwort nennt keinen Port und keinen Namen |
| `HTTP_UploadsItsSecurityLog` | Log-Server | `SecurityLog` enthält nur Einträge mit `security`, darunter die vorher abgelehnte Anfrage. Der Name sagt `SecurityLog` |
| `HTTP_WritesNothingOver` | Testserver | 412: ein `PUT` mit `If-None-Match: *`, kein zweiter Versuch |
| `HTTP_TriesAgain_AfterTheServerFailed` | Testserver | 503, 500, 201: drei Versuche mit demselben Inhalt. `Uploading` nur einmal |
| `HTTP_AServerThatTakesNoPUT_IsNotSupported` | Testserver | 405: `NotSupportedOperation`, kein zweiter Versuch |
| `SFTP_UploadsItsLog` | Log-Server | Eigener Schlüssel, gepinnter Hostkey. Beim Log-Server als SFTP-Upload, genau einer (keine `.part`-Reste) |
| `SFTP_RefusesWhatItsConfigurationDoesNotName` | Log-Server | `/firmware/`, fremder Port, fremder Login: `FileServerNotPermitted`. Passwort in der URL: `InvalidURL` |
| `SFTP_SendsNothing_ToALogServerThatShowsAnotherHostKey` | Log-Server | Anderer Pin: `UploadFailure`, genau ein `HostKeyRefused` trotz `Retries: 2` |
| `SFTP_RefusesAnUpload_UntilItsKeyIsConfirmed` | Log-Server | Unbestätigter Schlüssel: `FileServerNotPermitted`. Nach `ConfirmFileTransferKey` wird hochgeladen |

Die Tests, deren Name mit `SFTP_` beginnt, schalten nach der Regel von
`AHopByHopTest` auch beim CSMS einen SSH-Server ein. Er wird nicht gebraucht,
stört aber nicht.

Daneben grün: WWCP_Node `FileTransfer`/`Sftp` (51), WWCP_OCPP_Node
`FileTransferBridgeTests` (13), LogServer (196).

In `LocalControllerTests` schlägt `SwitchedOff_ItIsLetGoOf_AndLetInAgainOnceSwitchedOn`
fehl (401 erwartet, 503 bekommen). Ursache ist WWCP_Node `168d938`
(abgeschaltete Verbindung mit 503 und Retry-After), nicht `GetLog`.

### 6.3 Abweichungen

- **Login und Passwort in der URL** sind bei HTTP erlaubt und gehen vor. Bei
  SFTP bleibt ein Passwort in der URL `InvalidURL`, wie im Whitepaper.
- **Ein Passwort über `http://`** ist erlaubt, in der URL wie in der
  Konfiguration. Der Log-Server nimmt es so an und warnt bei jedem Start. Der
  Knoten warnt bei jedem Upload.
- **`GetLog` an den LC** wird ohne Prozessor mit `Rejected`/`NotSupported`
  beantwortet, nicht wie bei der CS mit `Accepted`. Ein `Accepted`, auf das kein
  Upload folgt, wäre falsch.

### 6.4 Offen

- ~~**Downloads über HTTP** (`UpdateFirmware` mit `http(s)://`) bleiben
  `NotSupported`.~~ Gebaut, siehe Abschnitt 7.
- **`httpFileServers` gibt es nur in der Datei.** Es fehlen HTTP-API, Seite,
  Kommandozeile und eine Variable im Device Model, wie sie die SSH-Peers haben.
  Das Passwort steht im Klartext in der Konfiguration.
- **Ob die GetLog-URL mit Passwort in einem Nachrichten-Log von OCPP landet**
  (Debug-Log, HTTP-SSE der Knoten), ist nicht geprüft.
- **Das Log ist nur das Event-Log im Speicher**, standardmäßig die letzten
  2000 Einträge (`EventLog.DefaultCapacity`).
  Die Logdateien auf der Platte (`LogPath`) werden nicht gelesen, auch kein
  Archiv mehrerer Dateien.
- **`InvalidFileServerCertificate` beim CSMS** ist nicht Ende zu Ende geprüft.
  Die Tests sehen nur das Log des Knotens. Beim LC hängt das Melden an
  `csms.url`, nicht an den Verbindungen.
- **`GetDiagnostics` und `GetLog` von OCPP 1.6 über HTTP** laufen durch
  dasselbe `StartUpload`, haben aber keinen Test Ende zu Ende.
- **Das Gateway** hat keine `DeviceModelBridge` und keinen `FileProvider`. Ein
  `GetLog` an das Gateway selbst lädt nichts hoch.
- **Reste im Log-Server:** Eine `.part`-Datei, die nie umbenannt wird, bleibt
  in `.sftp-incoming` liegen.

## 7. UpdateFirmware über HTTP, HTTPS und SFTP vom Download-Server

Die Gegenrichtung derselben Schicht. `FileTransfers.StartDownload` nahm nur
`sftp://`, ein `UpdateFirmware` mit `http(s)://` ergab `NotSupported`. Jetzt
holt der Knoten ein Firmware-Image mit `GET` von einem HTTP-Fileserver seiner
Konfiguration, oder über SFTP. Die Gegenstelle ist der Download-Server
(`libs/DownloadServer`, 7.6), so wie der Log-Server die von `GetLog` ist.

### 7.1 Was über die Leitung geht

```
GET /<Pfad>/<Datei> HTTP/1.1
Range: bytes=<Offset>-                    (ab dem zweiten Versuch, wenn schon etwas da ist)
Authorization: Basic <login:passwort>     (falls es welche gibt)
User-Agent: WWCP-Node/1.0
```

| Antwort | Ergebnis | Neuer Versuch |
|---|---|---|
| 200 | `Done`. War schon etwas da, werden die ersten `Offset` Bytes übergangen | – |
| 206 mit `Content-Range` ab `Offset` | `Done`, angehängt | – |
| 206 ab einem anderen Byte | `RejectedByServer` | nein |
| 416 mit `Content-Range: bytes */<Offset>` | `Done`: es fehlte nichts mehr | – |
| Body bricht ab | `Unreachable` | ja, ab dem erreichten Byte |
| 404, 410 | `NoSuchFile` | nein |
| Weiterleitung (3xx) | `RejectedByServer`: wird nicht verfolgt | nein |
| größer als `MaxDownloadSize` (laut `Content-Length` oder `Content-Range`, sonst beim Lesen) | `TooLarge` | nein |
| sonst | wie beim Upload (4.1) | wie beim Upload |

Dem CSMS gemeldet wird wie über SFTP: `Downloading`, bei jedem neuen Versuch
`DownloadPaused` und wieder `Downloading`, am Ende `Downloaded` oder
`DownloadFailed`. Das Image liegt dann als `<RequestId>-<Datei>` im
Staging-Verzeichnis `downloads/` neben der Konfiguration. Was danach kommt
(Signatur prüfen, installieren), gehört dem `FileConsumer` und ist nicht Teil
des Transports.

### 7.2 Die Konfiguration

`httpFileServers` hat jetzt `directions`, wie die SSH-Peers:

```json
{ "id": "firmware", "url": "https://updates.example.org/firmware/",
  "directions": [ "download" ] }
```

- Ohne `directions` gelten beide Richtungen.
- Ein Upload braucht `upload`, ein Download braucht `download`, sonst ist die
  Anfrage `FileServerNotPermitted`.
- Ein Download muss unterhalb des Verzeichnisses liegen und eine Datei nennen.
  Eine URL, die auf `/` endet, ist `InvalidURL`.

Wie groß ein Image höchstens sein darf, setzt jeder Knoten selbst, und
`maxDownloadSize` in seiner Konfiguration geht dem vor:

```json
{ "maxDownloadSize": 536870912 }
```

| Knoten | Grenze ohne Konfiguration | Warum |
|---|---|---|
| Ladestation | 256 MiB (`ChargingStation.MaxDownloadSize`) | ein Firmware-Image einer Station, mit Luft, und nicht so viel, dass eine falsche URL die Platte füllt |
| LocalController | 1 GiB (`LocalController.MaxDownloadSize`) | das Image eines Controllers trägt ein ganzes System |

- Gelesen wird bei jedem Download, eine Änderung gilt also ab dem nächsten,
  ohne Neustart. Alle Versuche eines Downloads haben dieselbe Grenze.
- Ein Wert, der keine ganze Zahl über null ist (0, negativ, Bruch, Text),
  wird übergangen. Dann gilt die Grenze des Knotens, und das Log sagt warum.
- Die Grenze gilt für SFTP und HTTP(S) gleich.

### 7.3 Was gebaut ist

**WWCP_Node**

- `HTTPFileServer` hat `Directions`, `Ways`, `Allows(Direction)` und
  `AllowsPath(Direction, …)` statt `AllowsDirectory`.
- `JudgeHTTPFileTransfer(URL, Direction)` und `FileNameOf(Uri)`.
- `DownloadFileOverHTTPAsync(URL, Destination, Offset, MaxSize, …)`.
- Upload und Download teilen sich die Anmeldung (`SignIn`), die Wertung der
  Antworten (`Failed`) sowie Client, Zertifikatsprüfung, Leerlauf und
  Fehlerbehandlung (`TransferOverHTTP`).

**WWCP_OCPP**

- `FileTransfers.StartDownload` und der geplante Download nehmen
  `https://` und `http://`.
- Der Dateiname kommt aus dem letzten Pfadsegment.
- Vor einem geplanten Download (`RetrieveTimestamp`) wird beim Fälligwerden
  wieder über den richtigen Weg geurteilt.
- `FileTransfers.TheMaxDownloadSize`: `maxDownloadSize` der Konfiguration,
  sonst `MaxDownloadSize` des Knotens (`MaxDownloadSizeKey`).

**ChargingStation und LocalController**

- `MaxDownloadSize` als Konstante, beim Bau der `DeviceModelBridge` in
  `FileTransfers.MaxDownloadSize` gesetzt.

**WWCP_Node, für den Download-Server**

- `SftpArea.Downloaded` (`SftpAreaDownloaded`): Ein Bereich sagt seinem
  Besitzer, wenn eine Datei über SFTP gelesen und geschlossen wurde, mit dem
  ersten gelesenen Byte, der Zahl der Bytes und der Größe der Datei. Damit
  zeichnet der Download-Server auch SFTP-Downloads auf, und ein fortgesetzter
  Download sagt, ab wo er gelesen hat.

**Superprojekt**

- `libs/DownloadServer` als Submodul, in der Solution und im Testprojekt.
- `AFileTransferTest`: was Log- und Download-Tests teilen. Das sind das CSMS,
  der Knoten, der sich mit `ws://` und Passwort verbindet und angenommen wird,
  `HoldsTheHTTPFileServers` und `ATestServer`. `AToTheLogServer` und
  `AToTheDownloadServer` bauen darauf auf.
- Der Testserver heißt jetzt `TestFileServer` (vorher `HTTPLogServer`).
  Dazugekommen sind:
  - `GET` mit `Range`, also 206 und 416;
  - `BreakNextAfter(…)`: bricht den Body nach N Bytes ab;
  - `IgnoresRange`: ein Server ohne Ranges;
  - bei `AnswerNext(302)` ein `Location` zu einem anderen Host;
  - `ClaimNextAs(…)`: ein `Content-Length`, das größer ist als die Datei.
- `AToTheDownloadServer.cs` enthält alle Fälle. `CS_2_Download.cs` und
  `LC_2_Download.cs` erzeugen nur den Knoten. Sie hießen vorher
  `AToTheFirmwareServer`, `CS_2_Firmware` und `LC_2_Firmware`: Solange die
  Gegenstelle nur der Testserver war, klang der Name nach einem
  Firmware-Server, den es nicht gab. Jetzt heißen sie nach der Komponente am
  anderen Ende, wie `CS_2_Log` nach dem Log-Server.
- Wie bei den Log-Tests gehen die normalen Abläufe an den echten
  Download-Server. Was ein echter Server nicht absichtlich tut (Abbruch mitten
  im Body, 503, Redirect, eine falsche Länge, keine Ranges), macht der
  `TestFileServer`.

### 7.4 Tests

`CS_2_Download` und `LC_2_Download` je 17 Fälle, zusammen 34, alle grün. Mit
`CS_2_Log` und `LC_2_Log` sind es 60.

| Test | Gegenstelle | Was er zeigt |
|---|---|---|
| `HTTP_DownloadsAFirmwareImage_SigningInAsItsConfigurationSays` | Download-Server | URL ohne Login, Zugangsdaten aus der Konfiguration. `Downloading`, `Downloaded`, das Image Byte für Byte im Staging-Verzeichnis. Der Server hat genau einen `GET` des ganzen Images durch den Downloader des Knotens aufgezeichnet |
| `HTTPS_DownloadsAFirmwareImage_SigningInAsTheURLSays` | Download-Server, TLS | Login aus der URL, geglaubt über die Test-CA, das Passwort nie im Log |
| `HTTP_DownloadsWithoutALogin_WhereTheDownloadServerTakesNone` | Download-Server, `anonymousHTTP` | Ohne Login geladen, aufgezeichnet als `anonymous` |
| `HTTP_AWrongPassword_IsDownloadFailed` | Download-Server | 401: `DownloadFailed`, kein zweiter Versuch trotz `Retries: 3`, nichts geladen |
| `HTTP_AMissingImage_IsDownloadFailed` | Download-Server | 404: `DownloadFailed`, genau eine abgewiesene Anfrage |
| `HTTPS_FetchesNothing_FromAServerItDoesNotBelieve` | Download-Server, TLS | Zertifikat nicht geglaubt: keine Anfrage beim Server, genau ein `CertificateRefused`, nichts im Staging-Verzeichnis |
| `HTTP_ItsConfigurationSaysHowLargeAnImageMayBe` | Download-Server | `maxDownloadSize` genau so groß wie das Image: `Downloaded`. Ein Byte weniger: `DownloadFailed` nach einer Anfrage |
| `HTTP_RefusesWhatItsConfigurationDoesNotName` | Testserver | Anderes Verzeichnis, anderer Port, Server nur für Uploads: `FileServerNotPermitted`. URL ohne Datei, Query: `InvalidURL`. `ftp://`: `NotSupported`. Kein Server wird erreicht, nichts wird gemeldet |
| `HTTP_AnImageLargerThanTheNodeTakes_IsDownloadFailed` | Testserver | Der Server sagt ein Byte mehr als die Grenze des Knotens (256 MiB bzw. 1 GiB): `DownloadFailed` nach einer Anfrage trotz `Retries: 2`. Das Log nennt die Grenze |
| `HTTP_TakesUpWhereTheAttemptBeforeStopped` | Testserver | Abbruch nach 100000 Bytes. Dann `DownloadPaused`, `Downloading` und `Range: bytes=100000-`, beantwortet mit 206. Das Image ist ganz |
| `HTTP_AServerWithoutRanges_IsReadFromWhereItStopped` | Testserver | Server ohne Ranges: zweimal 200. Der schon geholte Teil wird übergangen, nichts ist doppelt |
| `HTTP_TriesAgain_AfterTheServerFailed` | Testserver | 503, dann 200. `DownloadPaused` dazwischen |
| `HTTP_ARedirect_IsNotFollowed` | Testserver | 302 zu einem anderen Host: `DownloadFailed`, eine Anfrage. Das Log sagt „is not followed“ |
| `SFTP_DownloadsAFirmwareImage` | Download-Server | Eigener Schlüssel, gepinnter Hostkey, `DownloadPaths: ["/firmware/"]`. Aufgezeichnet als SFTP-Download des ganzen Images ab Byte 0 |
| `SFTP_RefusesWhatItsConfigurationDoesNotName` | Download-Server | `/logs/`, fremder Port, fremder Login: `FileServerNotPermitted`. Passwort in der URL: `InvalidURL`. Nichts geladen |
| `SFTP_FetchesNothing_FromADownloadServerThatShowsAnotherHostKey` | Download-Server | Anderer Pin: `DownloadFailed`, genau ein `HostKeyRefused` trotz `Retries: 2` |
| `SFTP_RefusesADownload_UntilItsKeyIsConfirmed` | Download-Server | Unbestätigter Schlüssel: `FileServerNotPermitted`. Nach `ConfirmFileTransferKey` wird geladen |

Daneben grün:
- WWCP_Node `FileTransfer`/`Sftp` (51), darin
  `AFileIsDownloadedAndTakenUpWhereItStopped` mit dem neuen
  `SftpArea.Downloaded`: ganzer Download ab Byte 0, Fortsetzung ab Byte 50000.
- WWCP_OCPP_Node `FileTransferBridgeTests` (14).
- DownloadServer (208): WWCP_Nodes Konformitäts-Suite und die eigenen Tests
  (7.6).

### 7.5 Offen

- **Weiterleitungen** werden nie verfolgt, auch nicht zu einem Server, den die
  Konfiguration nennt. Firmware hinter einem CDN mit Redirect lädt so nicht.
- **Ein Server ohne Ranges** liefert ab dem zweiten Versuch alles von vorn.
  Der Knoten übergeht den Teil, den er schon hat, prüft aber nicht, ob es noch
  dieselbe Datei ist (kein `If-Range` mit ETag). Ob das Image echt ist,
  entscheidet erst die Signatur danach.
- ~~**`FileTransfers.MaxDownloadSize`** setzt bei CS und LC nichts.~~ Erledigt,
  siehe 7.2. Noch offen: Das CSMS kann die Grenze nicht lesen, es gibt keine
  Variable im Device Model. Ohne `Content-Length` (chunked) greift die Prüfung
  erst beim Lesen. Das ist in WWCP_Node gebaut, aber ohne Test Ende zu Ende.
- **`SignedUpdateFirmware` und `UpdateFirmware` von OCPP 1.6 über HTTP** laufen
  durch dasselbe `StartDownload`, haben aber keinen Test Ende zu Ende.
- ~~**Firmware über SFTP** hat im Superprojekt noch keinen Test.~~ Erledigt,
  siehe 7.4.
- **Fortsetzen am echten Download-Server**: Der Abbruch mitten im Body geht
  nur mit dem Testserver. Dass der Download-Server Ranges richtig beantwortet,
  prüfen seine eigenen Tests.
- Zum Download-Server selbst siehe 7.6.

### 7.6 Der Download-Server

`libs/DownloadServer` (`OpenChargingCloud/DownloadServer`) ist ein WWCP_Node
wie der Log-Server, aber allgemein: Er bietet Dateien an, Firmware-Images
zuerst, und alles andere, worauf Ladestationen und LocalController zeigen
sollen.

**Bereiche und Dateien**

- Die Dateien liegen in Bereichen, je einem Verzeichnis unter `files/`. Der
  Bereich ist der erste Teil des Pfads: `/firmware/station-2.1.0.bin`. Ohne
  Konfiguration gibt es nur `firmware`. `logs` ist als Name vergeben, weil
  jeder Knoten dort über SFTP seine Log-Dateien zeigt.
- Angeboten wird, was auf der Platte liegt. Eine Datei, die jemand von Hand in
  ihr Verzeichnis legt, ist sofort zu haben.
- Veröffentlicht wird über SFTP, über die Web-Oberfläche oder
  `PUT /api/v1/downloadServer/files/{area}/{name}`, oder durch Ablegen im
  Verzeichnis. Über API und SFTP wird erst unter verstecktem Namen geschrieben
  und dann umbenannt, damit niemand eine halbe Datei lädt.
- `SHA-256` jeder Datei wird gemerkt und neu gerechnet, wenn sich Größe oder
  Schreibzeit ändern. Er ist auch das `ETag`.

**Downloads**

| Weg | Was |
|---|---|
| HTTP | `GET /<Bereich>/<Datei>` auf Port 8365, als `downloader` mit Basic Auth, oder ohne Login bei `"anonymousHTTP": true`. Ein Byte-Bereich pro Anfrage (206), 416 hinter dem Ende, mehrere Bereiche oder ein nicht passendes `If-Range` ergeben die ganze Datei. `HEAD`. HTTPS mit dem Zertifikat der Web-Oberfläche. Kein `PUT`, nichts sonst |
| SFTP | `/<Bereich>/<Datei>` auf dem SSH-Server (22365), als `downloader` mit eigenem Schlüssel oder Passwort |

Jeder Download wird in `files/downloads.jsonl` aufgezeichnet: wer, welche
Datei, ab welchem Byte, wie viele Bytes, wie groß die Datei war, von wo.
Abgewiesene Anfragen (401, 403, 404) werden nur gezählt.

**Konfiguration** (`downloadServer`): `directory`, `areas`, `http`,
`httpListen`, `httpPort`, `anonymousHTTP`, `sftp`, `maxFileSize`. Die
Web-Oberfläche liegt auf 2365, weil 2363 der Log-Server und 2364 der
Registrierungsstelle der PKI gehört.

**Rollen**: `downloader` lädt und sieht sonst nichts. `publisher` sieht,
veröffentlicht und entfernt Dateien. `operator` sieht alles und
veröffentlicht, legt aber keine Downloader an.

**Was es sonst hat**: JSON-API unter `/api/v1/downloadServer`, Befehle
`downloadServer`, `files`, `downloads` und `downloaders` über SSH, und eine
Web-Oberfläche mit den Seiten Files, Downloads, Download server und
Downloaders.

**Tests** (208, alle grün): WWCP_Nodes Konformitäts-Suite, dazu Download
ganz, ab einem Byte, die letzten Bytes, ein Bereich in der Mitte, 416, `HEAD`,
mehrere Bereiche, fremdes `If-Range`, die Aufzeichnung mit erstem Byte; 401,
403 und 404 (auch versteckte Dateien und Wege aus dem Bereich hinaus), kein
`PUT`; Veröffentlichen und Entfernen als `publisher`; neues Passwort und
entfernter Downloader; die Einstellungen; die Auswertung von `Range`; die
Aufzeichnung über einen Neustart; anonyme Downloads; was eine abgebrochene
Veröffentlichung hinterließ. Dazu 62 Seitentests der Web-Oberfläche.

**Offen**

- **Über die Web-Oberfläche** nimmt der HTTP-Server eines Knotens höchstens
  8 MiB in einer Anfrage. Größere Images gehen über SFTP oder ins Verzeichnis.
  Die Seite sagt das und prüft die Größe vor dem Senden.
- **Die Aufzeichnung über HTTP** zählt die Bytes, die die Antwort trägt, nicht
  die, die wirklich angekommen sind. Bricht der Client ab, steht trotzdem die
  volle Zahl da. Über SFTP zählen die gelesenen Bytes.
- **Ein Programm** wie `LogServerCLI`, das den Download-Server allein startet,
  gibt es noch nicht.
- **Die Web-Oberfläche** ist gebaut, eingebettet und von den Seitentests
  geprüft, aber nicht in einem Browser angesehen.
- **Bereiche** sind flach, wie die Bereiche von SFTP. Unterverzeichnisse gibt
  es nicht; Versionen stehen im Dateinamen.
