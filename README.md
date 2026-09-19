# EVChargingTestEnvironment

A vehicle, a charging station, a local controller and a CSMS, in one process,
already pointed at each other and already holding the certificates they need —
until Ctrl+C.

```
  EV  ──ISO 15118──▶  ChargingStation  ──OCPP 2.1──▶  LocalController  ──OCPP 2.1──▶  CSMS
      SLAC, SDP, TLS                   ws:// or wss://                 ws:// or wss://
      or, with --mcs, a 10BASE-T1S bus
      with the coupler's sensors on it
```

Each of those four is its own repository and its own program, and each runs on
its own:
[EVCLI](https://github.com/OpenChargingCloud/EVCLI),
[ChargingStationCLI](https://github.com/OpenChargingCloud/ChargingStationCLI),
[LocalControllerCLI](https://github.com/OpenChargingCloud/LocalControllerCLI),
[CSMSCLI](https://github.com/OpenChargingCloud/CSMSCLI). What none of them can
do on its own is be a *site*. A vehicle needs a station to charge at, a station
needs something above it to report to, and all four need certificates somebody
has to have minted and handed out in the right directions. Separately that is
an afternoon of copying files between four directories, and every step of it is
a place to put the wrong one.

This repository is that wiring, and nothing else. It adds no fifth component.


### What happens at a start

1. **The certificates, before anything else exists.** Three hierarchies — see
   below — built into `data/pki/` and kept there between starts.
2. **The configuration files**, because every one of these four reads its own
   while it is being built and several of them open ports according to it.
3. **The four objects**, from the top down: the CSMS knows nothing of what is
   below it, and everything below has to be told where to dial.
4. **The certificates and logins handed out** — through the same methods each
   component's web interface calls, so everything handed out here shows up in
   the browser where it would have been put by hand.
5. **Start**, top down again, so nothing dials a port that is not open yet.


### Getting it

The libraries are submodules, so they have to come along:

```
git clone --recurse-submodules https://github.com/OpenChargingCloud/EVChargingTestEnvironment
```

If you already cloned it without them:

```
git submodule update --init --recursive
```

**On Windows**, turn long paths on first — the deepest file in the submodules is
well past the classic 260-character limit:

```
git config --global core.longpaths true
```


### Two things that are not in this repository

Neither is an oversight, and both are one command.

**The ISO 15118 schemas** are ISO's, published under a licence that grants use
and not redistribution. Nothing that touches ISO 15118 builds without them:

```
bash libs/WWCP_ISO15118/tools/download-schemas.sh
```

**The generated CSS** that a handful of the OCPP projects embed as manifest
resources. Those projects keep their stylesheets as SCSS and describe the
compilation in a `compilerconfig.json` — the format Visual Studio's Web Compiler
extension reads, which nothing on a command line does. On a machine that has
never had those projects open in Visual Studio the build stops on a missing
resource:

```
bash tools/build-webassets.sh
```

It needs `sass` (`npm install -g sass`) and is idempotent.


### Building and running

```
dotnet build EVChargingTestEnvironment.slnx
dotnet run --project EVChargingTestEnvironment
```

The build needs the .NET 10 SDK and Node.js; `dotnet build
-p:SkipFrontendBuild=true` leaves the four npm steps out and reuses whatever is
in each `Frontend/dist`.

```
  vehicle           http://127.0.0.1:2347/
  charging station  http://127.0.0.1:2348/
  display           http://127.0.0.1:2349/
  local controller  http://127.0.0.1:2350/
  CSMS              http://127.0.0.1:2351/
```

The first start makes up one account per component, `root`, and prints each
password once. `--help` lists the rest.


### Ports

Eight, counted off `--base-port` so that a second environment on one machine is
one switch away:

| | |
|---|---|
| `n+0` | the vehicle's web interface |
| `n+1` | the station's web interface |
| `n+2` | the station's display — a second server, with no sign-in on it |
| `n+3` | the local controller's web interface |
| `n+4` | the CSMS's web interface |
| `n+5` | the CSMS's OCPP port, where stations and controllers dial it |
| `n+6` | the local controller's OCPP port |
| `n+7` | the coupler's 10BASE-T1S bus under `--mcs` — a UDP multicast group, `239.151.18.1`, not a listener |

`n` is 2347 unless `--base-port` says otherwise, which is the vehicle's own port
when it runs alone. Nothing is listened on but the loopback address until
`--any`; the multicast group is the one exception, because a bus that only one
process can hear is not a bus.


### One server instead of seven ports: `--shared`

```
./run.sh --shared
```

One HTTP server on one port, the four web interfaces told apart by the first
path segment, and **one sign-in for all four**:

```
  http://127.0.0.1:2347/EV/...
  http://127.0.0.1:2347/ChargingStation/...
  http://127.0.0.1:2347/LocalController/...
  http://127.0.0.1:2347/CSMS/...
  http://127.0.0.1:2347/ext/login          ← one door
```

The single sign-on is not a mechanism bolted beside the four; it is a
consequence of how they already work. Each of them carries its roles as groups
in Hermod's `HTTPExtAPI` and asks, on every request, whether the account is in
the group — and the names overlap on purpose: all four have `systemadmin` and
`viewer`, three of them have `cpo`. So one `HTTPExtAPI` handed to all four,
with one set of accounts behind it, makes an account in `systemadmin` an
administrator of every one of them at once, with each component still deciding
for itself what that permits.

The two OCPP ports stay where they were: they serve a different network, present
different certificates and let in different callers, and one port would make one
set of rules out of two.

Own ports is the default, and deliberately so: four servers and four sets of
accounts is what each of these programs does when it is started alone, so it is
the arrangement a bug found here is also a bug in.


### The PKI

Built before anything else exists, into `data/pki/`, and reused at every start
after that. `--fresh-pki` builds it again — which invalidates every OCPP
certificate signed for an earlier start, so it moves the old hierarchy aside
rather than deleting it.

**Three hierarchies, and three separate roots.**

| | |
|---|---|
| `v2g/strict_15118_2_ecdsa_p256/` | ISO 15118-2 profile, ECDSA P-256. What the vehicle and the station here actually use |
| `v2g/strict_15118_20_ecdsa_p521/` | ISO 15118-20 profile, ECDSA P-521. The -20-faithful material |
| `ocpp/` | A root of its own above the charging cable |

The **-2 hierarchy is P-256 because the station's V2G endpoint is .NET's own TLS
stack**, and .NET on Windows and macOS will not carry the -20 curve at all — a
P-521 station certificate there is a station nothing can connect to.

The **-20 hierarchy** is where the OEM provisioning certificate comes from: the
-20 contract-installation exchange unwraps the issued key with an ECDH against
the station's ephemeral secp521r1 key, so a P-256 provisioning certificate takes
part in an exchange it cannot finish. It also holds the loopback directory
(`v2g/dev/`) in the shape the reference SECC writes and the reference EVCC
reads, so this environment's vehicle can be pointed at either.

The **OCPP root is separate from the V2G roots**, and that is the point: a
station that would accept a CSMS because some V2G root vouched for it has been
told something nobody meant to say, and the mistake is invisible until somebody
with a contract certificate turns out to be able to impersonate a management
system.

What each program is handed:

| | |
|---|---|
| station, V2G endpoint | the SECC leaf, its CPO Sub-CAs, and its private key |
| vehicle | Vehicle, contract, OEM and tariff certificates and both V2G roots, imported into its own certificate store at every start and named in its session by the handles they became |
| CSMS, local controller | a server certificate signed against their own signing request, and the OCPP root as an accepted chain |
| station, OCPP | a client certificate signed against its own signing request |

The OCPP certificates are signed against keys the three programs generated and
**kept** — their private keys never leave the store that made them, which is
what those stores are for. This repository is a certificate authority to them,
not a key factory.

All of it is protected with one password, written to `data/pki/pki.json` with
that file readable by its owner alone. The vehicle's store keeps its copies
without one, which is that store's rule, in a directory below `data/EV/` made
for its owner alone where the platform allows it.

`--evil-certs` also writes the PKI builder's deliberately malformed
certificates — an expired SECC leaf, a contract certificate with `serverAuth`,
an evil-twin root with identical Distinguished Names and different keys, and
nine more — for pointing at either side on purpose. See
`libs/WWCP_ISO15118/WWCP_ISO15118_PKI/README.md`.


### OCPP and TLS

Security profile 1 — a password over an unencrypted WebSocket — by default, and
that is a decision rather than an oversight.

Both ends of an encrypted OCPP port need this *machine* to know the OCPP root,
and neither can be given it from inside the process. The server will not present
a certificate whose chain .NET cannot build here; the client validates the one it
is shown the ordinary way, against the roots the machine has. A private root that
is in no store fails both — the server silently, at a port reporting itself as
running and encrypted, and the client as a bare `400 Bad Request` with the real
reason wrapped inside it.

Putting a root into somebody's certificate store is not something a test
environment should do while they are looking the other way. So the certificates
for profile 2 are minted and in place, `--ocpp-tls` turns them on, and the
console prints the one command that is still needed:

```
certutil -addstore -user Root "…\data\pki\ocpp\ocpp_root_trust.pem"       # Windows, no administrator needed
sudo cp …/ocpp_root_trust.pem /usr/local/share/ca-certificates/ocpp-test-root.crt && sudo update-ca-certificates
sudo security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain …/ocpp_root_trust.pem
```

The V2G side needs none of this: the vehicle is given the V2G roots as a file
and checks the station's chain against those, which is what ISO 15118 asks for
and is why that half works out of the box.


### How far a charging session gets

SLAC, SDP, the TCP connection and the **mutual TLS handshake** all run between
this vehicle and this station, against the certificates built at the start:

```
EV       15118 sdp   SDP: found a station at [fe80::…]:55031 (TLS, TCP) after 1 attempt(s) in 267 ms.
EV       15118 tls   TLS: presenting the Vehicle certificate CN=Vehicle (Test Environment) -2 (+2 intermediate(s)) for mutual TLS.
EV       15118 tls   TLS station: the chain is valid, anchored at CN=V2G Root CA (Test Environment) -2.
station  15118 v2g   Its first V2GTP frame is ExiMainstream, 79 bytes.
```

**A whole session does not.** The charging station's V2G listener reads the
vehicle's first frame and closes: the session state machines above it — the
SupportedAppProtocol handshake, the EXI messages of -2 or -20, the charging loop
— are not wired up in that library yet. A vehicle pointed at it therefore fails
with *"connection closed before a full 8-byte header arrived"*, which reads like
a network fault and is not one. This environment says so on the console at every
start rather than leaving it to be discovered.

For a whole session, point the vehicle at the reference SECC, which has them:

```
dotnet run --project libs/WWCP_ISO15118/WWCP_ISO15118_SECC -- --listen 15118
./run.sh --no-v2g          # so the two stations do not both answer SDP
```

and then, from the vehicle's **Charging** page or from `EVCLI`, connect to
`[::1]:15118` with `--pki-dir data/pki/v2g/dev`.

SLAC needs `AF_PACKET` and therefore Linux; on Windows and macOS the station
says so and carries on without it.


### MCS: a bus instead of a cable

```
./run.sh --mcs
```

A Megawatt Charging System coupler has no powerline and therefore no SLAC. Its
link is IEEE 802.3cg **10BASE-T1S**: one twisted pair with up to eight nodes on
it, the charging station as coordinator, and PLCA — Physical Layer Collision
Avoidance — handing each node a transmit opportunity in turn. The vehicle is one
node on that bus. The temperature sensors in the coupler's pins are the others.

Under `--mcs` the station coordinates such a bus, emulated on a UDP multicast
group in the way the SLAC emulation is: every frame reaches every node, and
nothing needs a list of peers. Two sensors, in the DC+ and DC- pins, join it at
the start; the vehicle joins at each charging session, with weight 3, and leaves
again after it:

```
station  15118 t1s   T1S: coordinating a 10BASE-T1S bus on UDP multicast 239.151.18.1:2354 as 00:15:5D:5B:03:0C; thermal limits 70 °C warning, 90 °C overload.
station  15118 t1s   T1S: node 1 'DC+ pin' (TemperatureSensor, 02:71:50:DA:11:76, weight 1) joined the bus.
station  15118 t1s   T1S: node 2 'DC- pin' (TemperatureSensor, 02:71:50:9D:CF:41, weight 1) joined the bus.
station  15118 t1s   T1S: node 3 'EV' (Vehicle, 02:71:50:05:CC:44, weight 3) joined the bus.
EV       15118 t1s   T1S: on the bus as node 3 in 281 ms - coordinator 00:15:5D:5B:03:0C, 3 opportunities per cycle.
```

Weight 3 means the vehicle is asked three times per cycle, interleaved between
the sensors — `EV, DC+, EV, DC-, EV` — so the node that carries the whole of
ISO 15118-20 is polled most often, and no sensor is polled less than once. The
cycle runs twice a second here, so every pin is read twice a second, and a pin
that stops answering is reported lost after a few cycles — which the station
treats as an alarm, since a sensor that has gone quiet is not one that has cooled.

**The overload.** The pins are rated for 500 A and heat with the square of the
current; `--mcs-current <A>` sets what they carry and `--mcs-sensors <n>` how many
of them there are, up to six. At 800 A:

```
./run.sh --mcs --mcs-current 800
```

```
station  15118 t1s thermal   T1S: DC+ pin is warm at 70,0 °C.                          ← after about 40 s
station  15118 t1s thermal   T1S: OVERLOAD - DC+ pin is overloaded at 90,1 °C. The coupler is being asked for more than it can carry.
env      COUPLER ALARM: DC+ pin is overload at 90,1 °C. Set --mcs-current below the pins' 500 A rating, or watch it cool with the current off.
```

The alarm clears again with five kelvin of hysteresis, so a pin hovering at the
limit does not flap. What the station does with it, so far, is *say so*: the
critical line above, and a `ThermalStateChanged` event on its V2G link that a
station's charging logic can stop or derate a session from. Reporting it
upwards — an OCPP `NotifyEvent`, a lowered `EVSE` limit — is not wired in yet.

**Which medium.** The vehicle's `session` section says, in four fields:
`t1sTransport` (`none`, `auto`, `afpacket` or `udp`), `t1sBus` (the group and
port of the emulated medium), `t1sInterface` (the adapter, or the interface to
join the group on) and `t1sWeight` (how many opportunities per cycle to ask
for). `--mcs` writes `udp` and the group; `auto` takes a real adapter through
AF_PACKET where there is one — Linux, CAP_NET_RAW — and declines everywhere
else, because the emulated medium is never chosen by itself. The same four are
on the vehicle's **Charging** page and, in `EVCLI`, `--t1s-transport`,
`--t1s-bus`, `--t1s-interface`, `--t1s-weight` and `--t1s` to join once and
say what came of it. The station takes the same choice in its `V2GOptions.T1S`.

The library behind it, what in it is the standard and what is the emulation, and
how a real 10BASE-T1S adapter plugs in, is in
`libs/WWCP_ISO15118/WWCP_ISO15118_T1S/README.md`. `--mcs` and `--no-v2g`
exclude each other: the bus is the station's, and without a station there is
nobody to coordinate it.


### What each of the four *is*

Said in that component's own `configuration.json` below `data/`, and on its own
Configuration page — exactly as when it runs alone. The switches here are about
the *site*: how many of the four there are, which ports they take, where their
certificates come from. Four programs' worth of switches on one command line
would be several hundred of them, and every one would be a second place for a
setting to disagree with the first.

```
data/
├── EV/                  configuration.json, accounts/
├── ChargingStation/     configuration.json, accounts/, ocpp-client-keys/, …
├── LocalController/     configuration.json, accounts/, ocpp-server-keys/, …
├── CSMS/                configuration.json, accounts/, ocpp-server-keys/, …
└── pki/                 v2g/, ocpp/, pki.json
```

One directory each, because all four call their configuration file
`configuration.json` and every other file each of them keeps lives beside that
one. `--shared` puts the accounts in `data/accounts/` instead, once.


### Where things are

| | |
|---|---|
| `EVChargingTestEnvironment/Program.cs` | the command line, and what the console says at a start |
| `EVChargingTestEnvironment/ChargingTestEnvironment.cs` | the four built, wired and started |
| `EVChargingTestEnvironment/ChargingTestEnvironment.Certificates.cs` | who is given what, and who may sign in where |
| `EVChargingTestEnvironment/PKI/` | the hierarchies, the PKCS#12 bundles, the OCPP certificate authority |
| `EVChargingTestEnvironment/SharedAccounts.cs` | the one `HTTPExtAPI` behind `--shared` |
| `EVChargingTestEnvironment/ConsoleMux.cs` | four event logs on one console, each line saying who said it |
| `tools/build-webassets.sh` | the generated CSS the OCPP projects embed |
| `libs/EV`, `libs/ChargingStation`, `libs/LocalController`, `libs/CSMS` | the four components themselves |
| `libs/WWCP_ISO15118/WWCP_ISO15118_T1S` | the 10BASE-T1S bus under `--mcs`: PLCA, the sensors in the pins, the thermal monitor |


### The tests

```
dotnet test EVChargingTestEnvironment.slnx
```

Each component's own suite starts real components and talks to them over HTTP
the way a browser does, and the 10BASE-T1S suite runs whole buses over real
multicast sockets. There is nothing here that tests the wiring itself yet.


### Your participation

This software is Open Source under the **Affero GPL 3.0 license**.
We appreciate your participation in this ongoing project, and your help to
improve it and the e-mobility ICT in general. If you find bugs, want to
request a feature or send us a pull request, feel free to use the normal
GitHub features to do so. For this please read the Contributor License
Agreement carefully and send us a signed copy or use a similar free and
open license.
