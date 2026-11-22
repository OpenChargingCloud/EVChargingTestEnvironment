# Entwurf: Verbindungen als unveränderliche Transport-Einträge

Stand 2026-11-18, gemessen an WWCP_Node `d4752ca`, WWCP_OCPP `65633d1c`,
ChargingStation `d585ee7`, LocalController `f3ee43a` und CSMS `ced29fd`.
Das ist nur ein Entwurf, im Code ist noch nichts geändert.

**Vorgaben von Achim (Revision 2):**

1. Einen Wechsel des Schemas muss es nicht vorrangig geben. Verbindungseinträge sind **unveränderlich**.
2. `ConnectionType` und `OCPPVersion` gehören **nicht** in diese Schicht. Hier
   geht es nur um Verbindungen über WebSocket, SSH, Modbus usw., die jeweils
   eigene Sonder-Eigenschaften haben.

---

## 1. Befund

### 1.1 `ConnectionEntry` vermischt drei Schichten

`libs/WWCP_Node/WWCP_Node/Connections/ConnectionEntry.cs` (2237 Zeilen,
`sealed class`, alle Eigenschaften mit `internal set`) trägt flach
nebeneinander:

| Schicht | Eigenschaften heute |
|---|---|
| **Transport** | URL, `Ssh*` (9), `ServerKeyPin*`, `RawPublicKeys`, `TrustOnFirstUse`, `Pins`, `PinsVersion` (11), `HTTPVersion`, `Identities`/`CertificateId`, `AuthenticationId`, `Inbound` |
| **Rolle im Knotennetz** | `ConnectionType` (CSMS, CSMSBackup, LocalController, ChargingStation, Gateway), `RemoteNodeId`, `AutoConnect`, Zugehörigkeit zu einem `PeerEntry` |
| **OCPP** | `OCPPVersion`, `Encoding` (= Wahl des Subprotokolls `ocpp2.1`/`ocpp2.1cbor`), `SignatureFormats` (JWS/COSE der OCPP-Nachrichten) |

Welche Transport-Felder gelten, steht nicht im Typ. Es folgt zur Laufzeit
aus `URL.Scheme` und `Inbound`. Deshalb lesen die Aufrufer an rund 320
Stellen transportspezifische Felder und verzweigen dabei über
`IsSSH`/`IsSecure`/`Inbound`: WWCP_Node 256, WWCP_OCPP_Node 28,
LocalController 21, ChargingStation 12, CSMS 6. Dazu kommen 136 Stellen in
den Tests.

### 1.2 OCPP sitzt schon in WWCP_Node

Seit aa9745d („Every WWCP node dials“) liegt das Verbinden in WWCP_Node. Die
OCPP-Begriffe sind dabei mitgewandert:

| Begriff | Treffer in WWCP_Node außerhalb von `ConnectionEntry.cs` | Dateien |
|---|---|---|
| `OCPPVersion` | 96 | `ConnectionRules`, `ConnectionState`, `ConnectionStore`, `PeerEntry`, `ConnectingProtocol`, `WWCPNode.Accepting/.Connections/.Connecting/.Hooks/.KeyPins/.Peers` |
| `ConnectionType` | 41 | `ConnectionRules`, `ConnectionStore`, `PeerEntry`, `WWCPNode.Connections`, `WWCPNode.HTTPS`, Pipelines |
| `SignatureFormats` | 24 | `ConnectionStore`, `ConnectingProtocol`, `WWCPNode.Connections`, `WWCPNode.Hooks` |
| `RemoteNodeId` | 21 | `ConnectionRules`, `ConnectionStore`, `WWCPNode.Connections` |

Beispiele:
- `ConnectingProtocol.ConnectingNodeOrNoneOf(OCPPVersion)`
- `ConnectionRules.Check(url, type, version, …)` mit „speaks no OCPP 1.6“
- `PeerEntry`: „Every endpoint of a peer speaks one OCPP version“

### 1.3 Die anderen Verbindungsarten machen es schon richtig

Für alle anderen Verbindungen gibt es bereits **unveränderliche Records**.
Sie haben dieselben Sonder-Eigenschaften, nur jeweils eigene Bausteine:

| Record | Ort | Sonder-Eigenschaften |
|---|---|---|
| `NTSServerConfiguration` | WWCP_Node `Configuration/` | Hostname, NTS-KE-/NTP-Port, `ServerPins? Pins`, AEAD, `NTSServerSSH? SSH` |
| `NTSServerSSH` | WWCP_Node `Configuration/` | Port, User, `HostKeyPins`, `HostCAs`, `OnMismatch` |
| `SSHPeerConfiguration` | WWCP_Node `Configuration/` | Address, User, `NTSServerSSH Trust`, FileTransfers |
| `DNSConfiguration` (Namensserver) | WWCP_Node `Configuration/` | `ServerPins? Pins` je Server |
| `MeterConfiguration` + `MeterEndpoint` | ChargingStation `Configuration/`, `Meters/` | Host, Port (802), Unit, `ServerPins? Pins`, `ReadIdentity`, `WriteIdentity` |

Die SSH-Prüfung des Servers gibt es also schon **doppelt**:
`ConnectionEntry.Ssh*` und `NTSServerSSH`. Die TLS-Prüfung des Servers gibt es
**dreifach**: `ConnectionEntry.ServerKeyPins/Pins`, `ServerPins` an
NTS/DNS/Zähler und `ServerKeyPinning`.

## 2. Ziel

Eine **Transportschicht** in WWCP_Node, ohne OCPP, mit unveränderlichen
Einträgen, je einer Art pro Transport. Darüber zwei dünne Schichten für die
**Verwendung**:

```
┌──────────────────────────────────────────────────────────────────────────┐
│ WWCP_OCPP_Node   OCPPUse            OCPPVersion, Subprotokoll/Encoding,  │
│                                     SignatureFormats                     │
├──────────────────────────────────────────────────────────────────────────┤
│ WWCP_Node        NodeLink           Rolle (heute ConnectionType),        │
│ (Wählen)                            RemoteNodeId, AutoConnect, Peer      │
├──────────────────────────────────────────────────────────────────────────┤
│ WWCP_Node        ConnectionEntry    WebSocket / SSH / Modbus-TLS / …     │
│ (Transport)      + Bausteine        TLSServerCheck, SSHServerCheck,      │
│                                     ClientIdentities, Credentials        │
└──────────────────────────────────────────────────────────────────────────┘
```

Die Transportschicht kennt weder OCPP noch Rollen. Die Rollenschicht kennt
die Knotenarten des WWCP-Netzes, aber kein OCPP. Nur die oberste Schicht ist
OCPP.

## 3. Schicht 1: Transport

### 3.1 Die Einträge

```csharp
namespace cloud.charging.open.protocols.WWCP.Node.Connections;

/// Eine Verbindung, wie sie aufgeschrieben ist: wohin, und woran die
/// Gegenseite erkannt wird und womit dieser Knoten sich ausweist. Nichts
/// darüber, wofür sie benutzt wird.
public abstract record ConnectionEntry(String          Id,
                                       String          Description,
                                       DateTimeOffset  CreatedAt)
{
    /// Der Diskriminator in Datei und API: "websocket" | "ssh" | "modbus-tls" | …
    public abstract String   Transport    { get; }
    public abstract Boolean  IsEncrypted  { get; }
    /// Für Meldungen und Log: "wss://host:443/path", "ssh://user@host:2222", "modbus-tls://host:802/1".
    public abstract String   Where        { get; }

    internal abstract void   WriteInto(JObject JSON);
}

/// ws:// oder wss://, dieser Knoten wählt.
public sealed record WebSocketConnection(String                Id,
                                         String                Description,
                                         DateTimeOffset        CreatedAt,
                                         URL                   URL,
                                         WebSocketHTTPVersion  HTTPVersion,
                                         TLSServerCheck?       ServerCheck,   // null genau bei ws://
                                         ClientIdentities      Identities,    // TLS-Client-Zertifikate
                                         String?               CredentialsId) // Basic / TOTP aus dem Anmeldespeicher
    : ConnectionEntry(Id, Description, CreatedAt)
{
    public override String  Transport         => "websocket";
    public override Boolean IsEncrypted       => ServerCheck is not null;
    public Boolean          BindsItsPassword  => HTTPVersion == WebSocketHTTPVersion.HTTP3;
}

/// ws:// oder wss://, die Gegenseite wählt diesen Knoten an.
public sealed record InboundWebSocketConnection(String           Id,
                                                String           Description,
                                                DateTimeOffset   CreatedAt,
                                                URL              URL,          // letztes Pfadsegment = Name der Gegenseite
                                                Boolean          OverTLS,
                                                TLSClientCheck?  ClientCheck,  // nur bei OverTLS
                                                String?          CredentialsId)
    : ConnectionEntry(Id, Description, CreatedAt)
{
    public override String Transport => "websocket";
    public String          Name      => NameOf(URL);
}

/// ssh://user@host:port – das Subsystem bestimmt die Verwendung, nicht der Eintrag.
public sealed record SSHConnection(String          Id,
                                   String          Description,
                                   DateTimeOffset  CreatedAt,
                                   DomainName      Host,
                                   UInt16          Port,
                                   String          User,
                                   SSHServerCheck  ServerCheck,
                                   ClientIdentities Keys)        // leer = eigener SSH-Schlüssel des Knotens
    : ConnectionEntry(Id, Description, CreatedAt)
{
    public override String  Transport   => "ssh";
    public override Boolean IsEncrypted => true;
}

/// Modbus/TCP Security (Modbus über TLS), Port 802.
public sealed record ModbusTLSConnection(String          Id,
                                         String          Description,
                                         DateTimeOffset  CreatedAt,
                                         String          Host,
                                         UInt16          Port,         // 802
                                         Byte            Unit,
                                         TLSServerCheck  ServerCheck,
                                         String?         ReadIdentity,
                                         String?         WriteIdentity)
    : ConnectionEntry(Id, Description, CreatedAt)
{
    public override String  Transport   => "modbus-tls";
    public override Boolean IsEncrypted => true;
}
```

Später dazu, mit denselben Bausteinen: `NTSConnection` (NTS-KE über TLS + NTP)
und `DNSConnection` (DoT/DoH). Heute sind das `NTSServerConfiguration` und
die Namensserver in `DNSConfiguration`.

**Namen:** `SSHConnection` ist heute schon ein `enum` in
`WWCP_Node/SecureShell/SSHPeer.cs:33` (Dedicated/Shared). In ChargingStation
gibt es `internal sealed class ModbusTLSConnection` für die laufende
Verbindung. Vorschlag: Das Enum wird zu `SSHChannelSharing`, die interne
Klasse zu `ModbusTLSClient`. Dann können die Einträge so heißen, wie man sie
liest.

### 3.2 Die Bausteine, die sich alle teilen

```csharp
/// Woran ein TLS-Server erkannt wird: an seiner CA – mit Pins, die das
/// einengen – oder an seinem Schlüssel. Eins von beiden, nie beides.
public abstract record TLSServerCheck;

public sealed record CAChecked(ServerPins?  Pins,            // die heutigen ServerPins von NTS, DNS, Zähler
                               UInt64       PinsVersion)
    : TLSServerCheck;

public sealed record KeyPinned(IReadOnlyList<KeyPin>  Pins,
                               UInt64                 PinsVersion,
                               Boolean                Confirmed,
                               RawPublicKeyMode       RawPublicKeys,
                               KeyTrustOnFirstUse     TrustOnFirstUse,
                               TimeSpan               ProbationTime,
                               KeyPin?                Learned,
                               DateTimeOffset?        LearnedAt)
    : TLSServerCheck
{
    public KeyPinState State => …;   // heute ServerKeyPinState
}

/// Woran ein TLS-Client erkannt wird, der diesen Knoten anwählt.
public abstract record TLSClientCheck;
public sealed record ClientKeyPinned (IReadOnlyList<KeyPin> Pins) : TLSClientCheck;
public sealed record ClientCAChecked (ServerPins            Pins) : TLSClientCheck;

/// Woran ein SSH-Server erkannt wird – heute doppelt: ConnectionEntry.Ssh* und NTSServerSSH.
public sealed record SSHServerCheck(IReadOnlyList<String>  HostKeyPins,
                                    IReadOnlyList<String>  HostCAs,
                                    SshOnPinMismatch       OnMismatch,
                                    SshTrustOnFirstUse     TrustOnFirstUse,
                                    TimeSpan               ProbationTime,
                                    UInt64                 HostKeysVersion,
                                    String?                LearnedHostKey,
                                    DateTimeOffset?        LearnedAt);

/// Womit dieser Knoten sich ausweist, jedes ab wann – heute ConnectionIdentity.
public sealed record ClientIdentities(IReadOnlyList<ConnectionIdentity> All);
```

Was dadurch strukturell wird, statt zur Laufzeit geprüft zu werden:

| heute Laufzeitprüfung | danach |
|---|---|
| `CheckOneWay`: CA-geprüft oder Key-Pins, nie beides | `TLSServerCheck` ist `CAChecked` **oder** `KeyPinned` |
| `CheckKeyPinned`: Raw Public Keys nur bei Key-Pinning | `RawPublicKeys` gibt es nur an `KeyPinned` |
| `ValidateHostKey`: Host-Key nur bei ssh:// | Host-Keys gibt es nur an `SSHConnection` |
| `TryParseHTTPVersion`: keine HTTP-Version bei ssh:// | `HTTPVersion` gibt es nur an `WebSocketConnection` |
| `CheckInbound`: eingehend weder Host-Key noch Raw Keys, TOFU oder HTTP-Version | `InboundWebSocketConnection` hat diese Felder nicht |
| `ServerKeyPins` wird bei `Inbound` als Client-Pins gelesen | eigenes Feld `ClientCheck` mit eigenem Typ |

Was als Invariante im Konstruktor bleibt:
- `KeyPinned` ohne Pins nur mit `TrustOnFirstUse == Key`
- HTTP/3 nur mit `ServerCheck is not null`
- `ClientCAChecked`: kein TOFU, `OnMismatch == Refuse`, Roots oder Gruppe genannt

### 3.3 Unveränderlich, auch im Store

- Jede Änderung ist `with { … }`. Der Store setzt das Ergebnis unter
  `updateLock` mit `connections[id] = changed` an die Stelle des alten
  Eintrags. Das macht er beim Rollback heute schon (`connections[Id] = was`,
  [ConnectionStore.cs:1639](libs/WWCP_Node/WWCP_Node/Connections/ConnectionStore.cs:1639)).
  Rollback heißt dann nur noch: die alte Referenz behalten. `Copy()` entfällt.
- Gelerntes ist genauso eine Änderung, etwa ein Host-Key nach TOFU, ein
  gelernter Key-Pin oder ein signierter Pin-Satz:
  ```csharp
  if (connections.TryGetValue(Id, out var found) &&
      found is SSHConnection { ServerCheck: { TrustOnFirstUse: SshTrustOnFirstUse.HostKey,
                                              HostKeyPins.Count: 0, LearnedHostKey: null } check } ssh &&
      ssh.Host == Host)
      connections[Id] = ssh with { ServerCheck = check with { LearnedHostKey = fingerprint, LearnedAt = now } };
  ```
- Wer wählt, holt sich den Eintrag einmal und arbeitet mit diesem Stand. Er
  kann nicht mehr mitten in einer Änderung lesen, etwa die neuen Pins, aber
  den alten gelernten Pin.
- **Kein Wechsel der Art.** `TryUpdateConnection` ändert eine
  `WebSocketConnection` nur zu einer `WebSocketConnection`; ws:// ↔ wss://
  bleibt erlaubt, denn beides ist WebSocket. Von WebSocket zu SSH oder von
  „ich wähle“ zu „ich werde gewählt“ heißt es: „Das ist eine andere Art von
  Verbindung, leg sie neu an.“ Die Webseite sperrt im Bearbeiten-Formular
  das Schema-Feld (bzw. bietet nur ws/wss an).
  Damit fallen die Behalteregeln für einen Wechsel des Transports
  ([ConnectionStore.cs:1420–1615](libs/WWCP_Node/WWCP_Node/Connections/ConnectionStore.cs:1420))
  ersatzlos weg: das Zurücksetzen der SSH-Felder, wenn ssh:// verlassen
  wird (1572), `turned` (1433, 1470) und `HTTPVersion → Auto` bei ssh://
  (1545). Übrig bleiben nur die Regeln innerhalb **einer** Art (siehe 6.).

## 4. Schicht 2: Rolle im Knotennetz (WWCP_Node, Wählen)

```csharp
namespace cloud.charging.open.protocols.WWCP.Node.Connecting;

/// Wofür dieser Knoten eine Verbindung benutzt: wer am anderen Ende ist,
/// unter welchem Namen, und ob er von selbst wählt.
public sealed record NodeLink(String    ConnectionId,   // → ConnectionEntry.Id
                              NodeRole  Role,           // heute ConnectionType
                              String?   RemoteNodeId,
                              Boolean   AutoConnect,
                              IUse?     Use);           // die Protokollschicht, s. 5.
```

- `NodeRole` ist das heutige `ConnectionType` mit neuem Namen: CSMS,
  CSMSBackup, LocalController, ChargingStation, Gateway. Das sind Arten von
  **WWCP-Knoten**, kein OCPP. Darum bleibt es in WWCP_Node, aber nicht am
  Transport. *Ob es so heißen soll, ist offen (siehe 9.).*
- `ConnectionRules` prüft dann einen `NodeLink` zusammen mit der Art seines
  Eintrags. „Dieser Knoten wählt nicht über SSH“ wird zu
  `Rules.Transports.Contains(entry.Transport)`. Den OCPP-1.6-Teil der Regeln
  übernimmt Schicht 3.
- `PeerEntry` (Ausweichverbindungen) fasst `NodeLink`s zusammen, keine
  Einträge. „Alle Endpunkte eines Peers sprechen eine OCPP-Version“ ist eine
  Regel von Schicht 3. Schicht 2 fragt dafür nur über einen Hook nach, ob
  zwei `Use`s zusammenpassen.
- Das Wählen in `WWCPNode.Connections.cs` bekommt `(ConnectionEntry, NodeLink)`
  und verzweigt **einmal** nach der Art:
  ```csharp
  switch (entry)
  {
      case WebSocketConnection ws:  await ConnectOverWebSocket(ws, link, ct); break;
      case SSHConnection       ssh: await ConnectOverSSH(ssh, link, ct); break;
      default: Fail(link, ConnectionStatus.NotConnected, $"Not connected: {entry.Where}: this node does not connect over {entry.Transport}.");
  }
  ```
  Was am Ende über die Verbindung gesprochen wird, also WebSocket-Subprotokolle
  oder das SSH-Subsystem, liefert die Protokollschicht über den
  `ConnectingProtocol`-Hook, den es schon gibt.

## 5. Schicht 3: OCPP (WWCP_OCPP_Node)

```csharp
namespace cloud.charging.open.protocols.OCPP.Node;

public sealed record OCPPUse(OCPPVersion            Version,
                             MessageEncodings       Encoding,          // JSON | CBOR | Auto → Subprotokolle
                             WWCPSignatureFormats?  SignatureFormats)  // null = Signaturrichtlinie
    : IUse
{
    public String[] SubProtocols => …;   // heute ConnectingProtocol.SubProtocolsOf(entry)
}
```

- Was heute mit `OCPPVersion` in WWCP_Node steht, kommt hierher, und
  WWCP_Node fragt es über die vorhandenen Hooks ab:
  - `ConnectingNodeOrNoneOf(OCPPVersion)` wird zu `ConnectingNodeOrNoneOf(IUse)`.
  - In `WWCPNode.Accepting` wird der Filter `entry.OCPPVersion == Version`
    zu einem Hook „Welcher Knoten nimmt diese eingehende Verbindung an?“.
  - `SetSignatureFormatsOf(ConnectionEntry, …)` und
    `SignatureFormatsOf(ConnectionEntry)` hängen dann an `OCPPUse`.
- `TryParseEncoding`, `TryParseSignatureFormats`, `TryParseVersion` und
  `AsText(OCPPVersion)` ziehen von `ConnectionEntry` hierher um.

## 6. Was von `TryUpdateConnection` übrig bleibt

Weil sich die Art nicht mehr ändert, bleiben nur die Behalteregeln innerhalb
einer Art. Sie müssen vor dem Umbau einzeln als Tabellentest festgehalten
werden:

| # | Regel heute (Zeile in `ConnectionStore.cs`) | danach |
|---|---|---|
| R1 | Host-Key bleibt, wenn nicht geschickt oder wie gezeigt (1368) | `SSHConnection`: bleibt |
| R2 | Key-Pins bleiben, wenn nicht geschickt oder wie gezeigt (1388) | `WebSocketConnection` mit `KeyPinned`: bleibt; ws:// → wss:// fängt bei `CAChecked(null)` an |
| R3 | Raw Keys/TOFU bleiben ohne `KeyPinTrust` (1440) | bleibt innerhalb von `KeyPinned` |
| R5 | `pins` + `pinsAsShown` → `ServerPins.TryAfterAPageSaved` (1487) | bleibt, jetzt in `CAChecked` bzw. `ClientCAChecked` |
| R6 | gelernter Host-Key entfällt mit neuem Host-Key oder neuer Adresse (1561) | bleibt |
| R7 | `SshHostKeysVersion` entfällt mit neuem Host-Key (1567) | bleibt |
| R9 | gelernter Key-Pin entfällt mit neuen Pins, neuer URL, TOFU aus oder gesetzten Pins (1590) | bleibt |
| R10 | Version/`Confirmed` der Key-Pins entfallen mit neuen oder leeren Pins (1596) | bleibt |
| R11 | `PinsVersion` entfällt, sobald sich die Roots ändern (1609) | bleibt |
| ~~R4~~ | Richtung gewechselt (`turned`) | **fällt weg**: andere Art |
| ~~R8~~ | weg von ssh://: SSH-Felder zurücksetzen | **fällt weg**: andere Art |
| ~~R12~~ | `HTTPVersion` → Auto bei ssh:// | **fällt weg**: SSH hat keine |

Ebenfalls weg ist das Problem, dass heute Werte des anderen Transports
unsichtbar im Speicher überleben. Ein Beispiel ist die Key-Pin-Probezeit
nach wss → ssh → wss.

## 7. Datei und API

Für die **Rückwärtskompatibilität** bleibt die heutige flache Form lesbar.
`TryParse` erkennt sie daran, dass `transport` fehlt oder nur aus dem
URL-Schema folgt, und verteilt die Schlüssel auf die drei Schichten:

| Schlüssel heute | geht an |
|---|---|
| `id`, `description`, `createdAt`, `url`, `sshHostKey`, `ssh*`, `serverKeyPins`, `serverKeyPin*`, `rawPublicKeys`, `trustOnFirstUse`, `pins`, `pinsVersion`, `clientKeyPins`, `clientPins`, `httpVersion`, `identities`, `certificateId`, `authenticationId`, `inbound` | Schicht 1 |
| `connectionType`, `remoteNodeId`, `autoConnect`, Peer-Zugehörigkeit | Schicht 2 |
| `ocppVersion`, `encoding`, `signatureFormats` | Schicht 3 (Hook beim Lesen) |

Wie geschrieben wird, ist zu entscheiden (siehe 9.):

- **(a) Flach wie heute.** Jede Schicht schreibt ihre Schlüssel in dasselbe
  Objekt. Das ist kompatibel mit allen Webseiten und älteren Ständen.
- **(b) Verschachtelt.** Etwa so:
  `{ "id", "description", "transport": "websocket", "websocket": { "url", "httpVersion", "server": {…}, … }, "link": { "role", "remoteNodeId", "autoConnect" }, "ocpp": { "version", "encoding", "signatureFormats" } }`.
  Das ist sauberer und passt zu den drei Schichten. Ältere Stände könnten
  eine so geschriebene Datei aber nicht mehr lesen, und die Webseiten
  müssten nachziehen.

Meine Empfehlung ist (a) für diesen Umbau und (b) als eigener, späterer
Schritt mit Versionsfeld in der Datei.

## 8. Reihenfolge der Commits

| # | Repo | Inhalt | bricht? |
|---|---|---|---|
| 0 | WWCP_Node | **Nur Tests:** Golden-File-Rundreise für jede heutige JSON-Form (ssh mit/ohne Pins, TOFU + gelernt, wss CA/ServerPins/Key-Pins/TOFU/Raw, ws, eingehend ws/wss mit clientKeyPins/clientPins, alte Datei mit `certificateId`); Tabelle R1–R11; Test „Art wechseln wird abgelehnt“ (zunächst als `[Ignore]`, s. 2) | nein |
| 1 | WWCP_Node | Bausteine `TLSServerCheck`, `TLSClientCheck`, `SSHServerCheck`, `ClientIdentities` + Einträge der Schicht 1. Neben dem alten `ConnectionEntry`, noch unbenutzt | nein |
| 2 | WWCP_Node | `ConnectionStore` hält Schicht-1-Einträge + `NodeLink` + ein undurchsichtiges `IUse`. Der alte `ConnectionEntry` wird zur **Lesesicht** (eine `class`, die aus Eintrag + Link + Use die alten Eigenschaften berechnet), damit WWCP_OCPP_Node, ChargingStation, LocalController und CSMS unverändert bauen. Art-Wechsel wird abgelehnt | nein, nur das Verhalten bei Art-Wechsel |
| 3 | WWCP_Node | Wählen/Annehmen/KeyPins in WWCP_Node auf `switch (entry)` + `NodeLink`; `OCPPVersion`/`SignatureFormats` nur noch über `IUse` und Hooks | nein |
| 4 | WWCP_OCPP | `OCPPUse`, Hook-Implementierungen; WWCP_OCPP_Node auf Schicht 1–3 | nein |
| 5a/b/c | ChargingStation, LocalController, CSMS | je ein Commit, unabhängig | nein |
| 6 | WWCP_Node | `OCPPVersion` aus WWCP_Node entfernen (zieht nach WWCP_OCPP um), alte Lesesicht `[Obsolete]` | Warnungen |
| 7 | WWCP_Node | Lesesicht entfernen | erst nach 4–5 |
| 8+ | WWCP_Node, ChargingStation | NTS, DNS, Zähler: `NTSServerSSH` → `SSHServerCheck`, `ServerPins` an NTS/DNS/Zähler → `CAChecked`, `MeterConfiguration` → `ModbusTLSConnection`. Ob sie in denselben Store kommen, ist eine eigene Entscheidung | je Schritt nein |

Nach jedem Schritt zieht das Superprojekt die Zeiger nach.

## 9. Offene Entscheidungen

1. **Namen:** `NodeRole` statt `ConnectionType`? `NodeLink` und `IUse`? Das Enum
   `SSHConnection` → `SSHChannelSharing` und ChargingStations interne
   `ModbusTLSConnection` → `ModbusTLSClient`, damit die Einträge frei sind?
2. **Art-Wechsel:** komplett ablehnen (Vorschlag) oder intern als „löschen
   und mit derselben Id neu anlegen“ anbieten, damit Peer und Zustand
   erhalten bleiben?
3. **Datei:** flach (a) oder verschachtelt (b), siehe 7.
4. **Ein Store für alles?** Sollen NTS, DNS und Zähler im selben
   `ConnectionStore` landen oder nur dieselben Bausteine nutzen und in ihren
   Abschnitten bleiben? Für den ersten Schritt schlage ich vor: nur die
   Bausteine.
5. **Wer und wann?** `ConnectionStore.cs` und `WWCPNode.Connections.cs` sind
   gerade die Dateien, an denen die ChargingStation-, LocalController- und
   CSMS-Sessions am meisten arbeiten. Schritt 2 und 3 brauchen ein ruhiges
   Fenster.
