/*
 * Copyright (c) 2014-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of the EV Charging Test Environment
 * <https://github.com/OpenChargingCloud/EVChargingTestEnvironment>
 *
 * Licensed under the Affero GPL license, Version 3.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.gnu.org/licenses/agpl.html
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#region Usings

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.EV.ISO15118;
using cloud.charging.open.TestEnvironment.PKI;

using cloud.charging.open.protocols.ISO15118.T1S.Nodes;
using cloud.charging.open.protocols.ISO15118.T1S.Transport;

using EVConfig       = cloud.charging.open.EV.Configuration;
using StationConfig  = cloud.charging.open.ChargingStation.Configuration;
using StationV2G     = cloud.charging.open.ChargingStation.ISO15118;
using ControlConfig  = cloud.charging.open.LocalController.Configuration;
using CSMSConfig     = cloud.charging.open.CSMS.Configuration;
using EMSPConfig     = cloud.charging.open.EMSP.Configuration;

using Vehicle        = cloud.charging.open.EV.EV;
using Station        = cloud.charging.open.ChargingStation.ChargingStation;
using Controller     = cloud.charging.open.LocalController.LocalController;
using Management     = cloud.charging.open.CSMS.CSMS;
using Provider       = cloud.charging.open.EMSP.EMSP;

#endregion

namespace cloud.charging.open.TestEnvironment
{

    /// <summary>
    /// One vehicle, one charging station, one local controller, one charging
    /// station management system and one e-mobility service provider, in one
    /// process, already pointed at each other and already holding the
    /// certificates they need.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each of these five is its own repository and its own program, and each
    /// of them runs on its own. What none of them can do on its own is be a
    /// <em>site</em>: a vehicle needs a station to charge at, a station needs
    /// something above it to report to, and all of them need certificates that
    /// somebody has to have minted and handed out in the right directions.
    /// That is what this class is - not a sixth component, but the wiring
    /// between five.
    /// </para>
    /// <para>
    /// The EMSP is the one of the five that nothing else here dials: it is the
    /// other end of an OCPI roaming agreement, and there is no CPO speaking
    /// OCPI on this bench yet. It is built and started all the same - its web
    /// interface, its OCPI endpoints below <c>/ext</c>, its log on the same
    /// console - so that a partner can be pointed at it, and so that the bench
    /// shows what a site with roaming on it looks like.
    /// </para>
    /// <para>
    /// <b>The order things happen in is the whole design.</b>
    /// </para>
    /// <list type="number">
    ///   <item>The certificates, before anything else exists. They decide what the station can listen with and what the vehicle may believe.</item>
    ///   <item>The configuration files, because every one of these five reads its own at construction and several of them open ports according to it.</item>
    ///   <item>The five objects, from the top down: the CSMS knows nothing of what is below it, and everything below has to be told where to dial. The EMSP stands beside the CSMS and dials nothing.</item>
    ///   <item>The certificates and logins handed out, which needs the objects (their key stores are theirs) and has to happen before they start (starting is when they dial).</item>
    ///   <item>Start, from the top down again, so that nothing dials a port that is not open yet.</item>
    /// </list>
    /// </remarks>
    public sealed partial class ChargingTestEnvironment : IAsyncDisposable
    {

        #region Data

        private readonly TestEnvironmentOptions  options;
        private readonly HTTPServer?             sharedServer;
        private readonly HTTPExtAPI?             sharedExtAPI;

        private Boolean                          started;

        private readonly List<TemperatureSensorNode>     sensors      = [];
        private readonly List<UdpMulticastT1STransport>  sensorMedia  = [];

        #endregion

        #region Properties

        /// <summary>Every certificate this site runs on.</summary>
        public TestPKI      Certificates        { get; }

        /// <summary>The vehicle, unless it was left out.</summary>
        public Vehicle?     EV                  { get; }

        /// <summary>The charging station.</summary>
        public Station      Station             { get; }

        /// <summary>The local controller, unless it was left out.</summary>
        public Controller?  Controller          { get; }

        /// <summary>The charging station management system.</summary>
        public Management   CSMS                { get; }

        /// <summary>The e-mobility service provider, unless it was left out.</summary>
        public Provider?    EMSP                { get; }

        /// <summary>The one console the five of them write to, each line saying which of them it came from.</summary>
        public ConsoleMux   Console             { get; }

        /// <summary>
        /// The temperature sensors in the coupler's pins, on an MCS bench:
        /// nodes on the station's 10BASE-T1S bus, beside the vehicle.
        /// </summary>
        /// <remarks>
        /// Objects of their own rather than something inside the station,
        /// because that is what they are: a sensor in a pin is not the
        /// station's software, it is a thing on the same wire that the station
        /// asks. Set <see cref="TemperatureSensorNode.Current_A"/> on them and
        /// the pins heat.
        /// </remarks>
        public IReadOnlyList<TemperatureSensorNode>  Sensors
            => sensors;

        /// <summary>What was decided before any of this was built.</summary>
        public TestEnvironmentOptions Options
            => options;

        /// <summary>
        /// Where to point a browser, per component, in the order somebody
        /// reading a console wants them.
        /// </summary>
        public IEnumerable<(String Name, URL URL)> WebInterfaces
        {
            get
            {

                if (EV is not null)
                    yield return ("vehicle",           EV.WebInterfaceURL);

                yield return     ("charging station",  Station.WebInterfaceURL);

                if (Station.KioskURL.HasValue)
                    yield return ("display",           Station.KioskURL.Value);

                if (Controller is not null)
                    yield return ("local controller",  Controller.WebInterfaceURL);

                yield return     ("CSMS",              CSMS.WebInterfaceURL);

                if (EMSP is not null)
                    yield return ("EMSP",              EMSP.WebInterfaceURL);

            }
        }

        /// <summary>
        /// Where somebody signs in. One place where they share a server, and
        /// one per component where they do not.
        /// </summary>
        public IEnumerable<String> SignInURLs
        {
            get
            {

                if (options.Shared)
                {
                    yield return $"http://{options.Host}:{options.SharedPort}" +
                                 $"{TestEnvironmentOptions.SharedExtAPIPath.ToString().TrimEnd('/')}/login";
                    yield break;
                }

                foreach (var (name, url) in WebInterfaces)
                {
                    if (name != "display")
                        yield return $"{url}ext/login";
                }

            }
        }

        #region GeneratedPasswords()

        /// <summary>
        /// The passwords that were made up at this start because there were no
        /// accounts yet, and who they are for.
        /// </summary>
        /// <remarks>
        /// Only ever at a first start, and only ever here: what is kept is the
        /// hash the accounts make of it, so this is the one moment the password
        /// exists anywhere it can be read. Where the five share one set of
        /// accounts there is one of these rather than five - which is the point
        /// of sharing them.
        /// </remarks>
        public IEnumerable<(String Whose, String Password)> GeneratedPasswords()
        {

            // Asked in the order they were built, so that the shared set of
            // accounts - which whichever of them started first made - is named
            // once and by the component that made it.
            if (CSMS.GeneratedPassword is { } csms)
                yield return (options.Shared ? "all five" : "the CSMS", csms);

            if (EMSP?.GeneratedPassword is { } emsp)
                yield return ("the EMSP", emsp);

            if (Controller?.GeneratedPassword is { } controller)
                yield return ("the local controller", controller);

            if (Station.GeneratedPassword is { } station)
                yield return ("the charging station", station);

            if (EV?.GeneratedPassword is { } vehicle)
                yield return ("the vehicle", vehicle);

        }

        #endregion

        #endregion

        #region Constructor(s)

        private ChargingTestEnvironment(TestEnvironmentOptions  Options,
                                        TestPKI                 Certificates,
                                        ConsoleMux              Console,
                                        HTTPServer?             SharedServer,
                                        HTTPExtAPI?             SharedExtAPI,
                                        Vehicle?                EV,
                                        Station                 Station,
                                        Controller?             Controller,
                                        Management              CSMS,
                                        Provider?               EMSP)
        {

            this.options       = Options;
            this.Certificates  = Certificates;
            this.Console       = Console;
            this.sharedServer  = SharedServer;
            this.sharedExtAPI  = SharedExtAPI;
            this.EV            = EV;
            this.Station       = Station;
            this.Controller    = Controller;
            this.CSMS          = CSMS;
            this.EMSP          = EMSP;

        }

        #endregion


        #region (static) Create(Options, Say)

        /// <summary>
        /// Build the whole site. Nothing listens yet: <see cref="Start"/> does.
        /// </summary>
        /// <param name="Options">What the command line settled on.</param>
        /// <param name="Say">Where the handful of sentences this has to say before there is a log to write to go.</param>
        public static ChargingTestEnvironment Create(TestEnvironmentOptions  Options,
                                                     Action<String>?         Say   = null)
        {

            var say = Say ?? (_ => { });

            #region 1) The certificates, before anything that might want one

            var certificates = TestPKI.Open(
                                   Options.PKIPath ?? Path.Combine(Options.DataPath, TestPKI.DefaultDirectoryName),
                                   Options.FreshPKI,
                                   Options.EvilCertificates,
                                   CommonNameSuffix:  "(Test Environment)",
                                   Say:               say
                               );

            #endregion

            #region 2) What each of them is told in writing

            Directory.CreateDirectory(Options.VehicleDirectory);
            Directory.CreateDirectory(Options.StationDirectory);
            Directory.CreateDirectory(Options.CSMSDirectory);

            if (!Options.NoLocalController)
                Directory.CreateDirectory(Options.ControllerDirectory);

            if (!Options.NoEMSP)
                Directory.CreateDirectory(Options.EMSPDirectory);

            WriteConfigurations(Options, certificates);

            #endregion

            #region 3) One console and one name resolver for all five

            // Each of the five keeps an event log of its own - five copies of
            // the same type in five namespaces, because each of them ships as
            // a program that may not depend on the others - so there is no one
            // log to hand them. What they can share is the console, and the
            // console is the one place they have to be read together.
            var console    = new ConsoleMux((Int32) Options.ConsoleLogLevel);

            var dnsClient  = new DNSClient(SearchForIPv6DNSServers: true);

            #endregion

            #region 4) The one HTTP server and the one set of accounts, where they share

            HTTPServer?  sharedServer  = null;
            HTTPExtAPI?  sharedExtAPI  = null;

            if (Options.Shared)
            {

                sharedServer  = new HTTPServer(
                                    IPAddress:       Options.Address,
                                    TCPPort:         Options.SharedPort,
                                    HTTPServerName:  "OpenChargingCloud EV Charging Test Environment",
                                    DNSClient:       dnsClient
                                );

                sharedExtAPI  = SharedAccounts.Build(
                                    sharedServer,
                                    TestEnvironmentOptions.SharedExtAPIPath,
                                    Options.SharedAccountsPath
                                );

            }

            #endregion

            #region 5) The five of them, from the top down

            var csms       = new Management(

                                 DNSClient:            dnsClient,
                                 HTTPServer:           sharedServer,
                                 BasePath:             Options.Shared ? TestEnvironmentOptions.CSMSBasePath : null,
                                 ExtAPI:               sharedExtAPI,
                                 HTTPHostname:         Options.Address,
                                 HTTPPort:             Options.CSMSPort,
                                 AccountsPath:         Options.Shared
                                                           ? Options.SharedAccountsPath
                                                           : Path.Combine(Options.CSMSDirectory, "accounts"),
                                 ConfigFile:           new CSMSConfig.CSMSConfigFile(
                                                           Path.Combine(Options.CSMSDirectory, CSMSConfig.CSMSConfigFile.DefaultFileName)
                                                       ),
                                 Frontend:             FrontendOf(Options, "CSMS", "CSMS"),

                                 // Off on all five: what each of them writes
                                 // reaches the console through the multiplexer
                                 // below, which says which of the five said it.
                                 // Five ConsoleLogs would say nothing of the
                                 // kind and interleave.
                                 LogToConsole:         false,

                                 // On here and on none of the others. DebugX is
                                 // one static listener list for the whole
                                 // process, so five bridges would put every
                                 // line the libraries below write into five
                                 // logs and onto the console five times.
                                 BridgeDebugLog:       Options.BridgeDebugLog

                             );

            // Beside the CSMS rather than below it: an EMSP is the other end
            // of a roaming agreement, and nothing on this bench dials it yet.
            // It is told nothing in WriteConfigurations for the same reason -
            // who it is (DE-GDF unless its file says otherwise) and which OCPI
            // versions it offers are in its own file, and its partners are in
            // the library's own files beside it, where its web interface puts
            // them.
            Provider? emsp = null;

            if (!Options.NoEMSP)
                emsp       = new Provider(

                                 DNSClient:            dnsClient,
                                 HTTPServer:           sharedServer,
                                 BasePath:             Options.Shared ? TestEnvironmentOptions.EMSPBasePath : null,
                                 ExtAPI:               sharedExtAPI,
                                 HTTPHostname:         Options.Address,
                                 HTTPPort:             Options.EMSPPort,
                                 AccountsPath:         Options.Shared
                                                           ? Options.SharedAccountsPath
                                                           : Path.Combine(Options.EMSPDirectory, "accounts"),
                                 ConfigFile:           new EMSPConfig.EMSPConfigFile(
                                                           Path.Combine(Options.EMSPDirectory, EMSPConfig.EMSPConfigFile.DefaultFileName)
                                                       ),
                                 Frontend:             FrontendOf(Options, "EMSP", "EMSP"),
                                 LogToConsole:         false,
                                 BridgeDebugLog:       false

                             );

            Controller? controller = null;

            if (!Options.NoLocalController)
                controller = new Controller(

                                 DNSClient:            dnsClient,
                                 HTTPServer:           sharedServer,
                                 BasePath:             Options.Shared ? TestEnvironmentOptions.ControllerBasePath : null,
                                 ExtAPI:               sharedExtAPI,
                                 HTTPHostname:         Options.Address,
                                 HTTPPort:             Options.ControllerPort,
                                 AccountsPath:         Options.Shared
                                                           ? Options.SharedAccountsPath
                                                           : Path.Combine(Options.ControllerDirectory, "accounts"),
                                 ConfigFile:           new ControlConfig.ControllerConfigFile(
                                                           Path.Combine(Options.ControllerDirectory, ControlConfig.ControllerConfigFile.DefaultFileName)
                                                       ),
                                 Frontend:             FrontendOf(Options, "LocalController", "LocalController"),
                                 LogToConsole:         false,
                                 BridgeDebugLog:       false

                             );

            var station    = new Station(

                                 DNSClient:            dnsClient,
                                 HTTPServer:           sharedServer,
                                 BasePath:             Options.Shared ? TestEnvironmentOptions.StationBasePath : null,
                                 ExtAPI:               sharedExtAPI,
                                 HTTPHostname:         Options.Address,
                                 HTTPPort:             Options.StationPort,
                                 AccountsPath:         Options.Shared
                                                           ? Options.SharedAccountsPath
                                                           : Path.Combine(Options.StationDirectory, "accounts"),
                                 ConfigFile:           new StationConfig.StationConfigFile(
                                                           Path.Combine(Options.StationDirectory, StationConfig.StationConfigFile.DefaultFileName)
                                                       ),
                                 KioskPort:            Options.KioskPort,
                                 KioskHostname:        Options.Address,
                                 NoKiosk:              Options.NoKiosk || Options.Shared,
                                 Frontend:             FrontendOf(Options, "ChargingStation", "ChargingStation"),
                                 V2G:                  V2GOptionsFor(Options, certificates),
                                 LogToConsole:         false,
                                 BridgeDebugLog:       false

                             );

            Vehicle? vehicle = null;

            if (!Options.NoVehicle)
                vehicle = new Vehicle(

                              HTTPHostname:         Options.Address,
                              HTTPPort:             Options.VehiclePort,
                              HTTPServer:           sharedServer,
                              BasePath:             Options.Shared ? TestEnvironmentOptions.VehicleBasePath : null,
                              ExtAPI:               sharedExtAPI,
                              AccountsPath:         Options.Shared
                                                        ? Options.SharedAccountsPath
                                                        : Path.Combine(Options.VehicleDirectory, "accounts"),
                              ConfigFile:           new EVConfig.EVConfigFile(
                                                        Path.Combine(Options.VehicleDirectory, EVConfig.EVConfigFile.DefaultFileName)
                                                    ),
                              DNSClient:            dnsClient,
                              Frontend:             FrontendOf(Options, "EV", "EV"),

                              // The vehicle keeps its certificates in a store
                              // of its own, below its directory. The bundles
                              // of this environment's PKI are imported into it
                              // once the vehicle exists, in HandOutCertificates,
                              // which is also where the session is told which
                              // of them to use.
                              CertificatesPath:     Path.Combine(Options.VehicleDirectory, "certificates"),

                              LogToConsole:         false,
                              BridgeDebugLog:       false

                          );

            #endregion

            #region 6) The five logs onto the one console

            // After all five exist, and in the order the console should show
            // them in when they all start at once.
            console.Attach(csms.Log,       "CSMS");

            if (emsp is not null)
                console.Attach(emsp.Log,       "EMSP");

            if (controller is not null)
                console.Attach(controller.Log, "LC");

            console.Attach(station.Log,    "station");

            if (vehicle is not null)
                console.Attach(vehicle.Log, "EV");

            #endregion

            return new ChargingTestEnvironment(
                       Options,
                       certificates,
                       console,
                       sharedServer,
                       sharedExtAPI,
                       vehicle,
                       station,
                       controller,
                       csms,
                       emsp
                   );

        }

        #endregion

        #region Start()

        /// <summary>
        /// Open every port, hand out every certificate, and let the three OCPP
        /// components find each other.
        /// </summary>
        public async Task Start()
        {

            if (started)
                return;

            #region The certificates and logins, before anything dials

            // Between construction and starting, and it has to be exactly
            // there: the key stores these are written into belong to the
            // components and do not exist before they are built, and starting
            // is the moment each of them dials out with whatever it has.
            await HandOutCertificates();

            #endregion

            #region Started from the top down, so nothing dials a closed port

            if (sharedServer is not null)
            {
                // One server for all five, so it is started here rather than
                // five times over - Start() on each of them would take the
                // same socket five times.
                await sharedServer.Start();
            }

            await CSMS.Start();

            // Right after the CSMS, and nothing below waits for it: nothing
            // dials it. Second rather than first so that, where the five share
            // their accounts, it is still the CSMS that makes them at a first
            // start and is named for it.
            if (EMSP is not null)
                await EMSP.Start();

            if (Controller is not null)
                await Controller.Start();

            await Station.Start();

            // After the station, which coordinates the bus they join; before
            // the vehicle, so that its first BEACON finds the pins already
            // there and the vehicle's join is the last of the cycle's arrivals.
            if (options.MCS)
                await StartSensors();

            if (EV is not null)
                await EV.Start();

            // After everything is listening, because a registration is HTTP in
            // both directions: the operator fetches the provider's versions and
            // POSTs its credentials, and the provider calls back to the URL in
            // them before it answers.
            await PeerTheOperatorWithTheProvider();

            #endregion

            #region How far a session can get here, said once

            // Said at every start rather than discovered at the end of a run.
            // Everything below the session - SLAC, SDP, the TCP connection,
            // the mutual TLS handshake against the certificates this
            // environment just minted - works between these two. The session
            // itself does not, because the charging station's V2G listener
            // reads the vehicle's first frame and closes: the state machines
            // above it are not wired up in that library yet. A vehicle pointed
            // at it therefore fails with "connection closed before a full
            // 8-byte header arrived", which reads like a network fault and is
            // not one.
            if (EV is not null && !options.NoV2G)
                Console.Say(
                    "env",
                    "ISO 15118: SLAC, SDP and the mutual TLS handshake run between this vehicle and this station. " +
                    "A whole session does not: the station's V2G listener reads the first frame and closes, because " +
                    "the session state machines above it are not in that library yet. For a full session, point this " +
                    "vehicle at the reference SECC in libs/WWCP_ISO15118 - the README says how."
                );

            #endregion

            started = true;

        }

        #endregion

        #region Stop()

        /// <summary>
        /// The other way round: the vehicle first, the CSMS last, so that
        /// nothing is left talking to something that has already gone.
        /// </summary>
        public async Task Stop()
        {

            if (!started)
                return;

            Console.Say("env", "The test environment is shutting down.");

            if (EV is not null)
                await EV.Stop();

            // Off the bus with a LEAVE each, while the coordinator is still
            // there to hear it.
            await StopSensors();

            await Station.Stop();

            if (Controller is not null)
                await Controller.Stop();

            await CSMS.Stop();

            if (EMSP is not null)
                await EMSP.Stop();

            if (sharedServer is not null)
                await sharedServer.Stop();

            started = false;

        }

        #endregion

        #region DisposeAsync()

        public async ValueTask DisposeAsync()
        {

            try
            {
                await Stop();
            }
            catch (Exception e)
            {
                // Fully qualified: this class has a Console of its own, which
                // is the five components' console and not the process's.
                System.Console.Error.WriteLine($"The test environment did not stop cleanly: {e.Message}");
            }

            if (EV is not null)
                await EV.DisposeAsync();

            await Station.   DisposeAsync();

            if (Controller is not null)
                await Controller.DisposeAsync();

            await CSMS.      DisposeAsync();

            if (EMSP is not null)
                await EMSP.DisposeAsync();

        }

        #endregion


        #region (private) StartSensors() / StopSensors()

        /// <summary>
        /// The temperature sensors in the coupler's pins: one node on the bus
        /// each, on media of their own, carrying whatever current the bench
        /// was told they carry.
        /// </summary>
        /// <remarks>
        /// DC+ and DC- first, because those are the pins that carry the
        /// current and the ones a coupler's thermal management watches; then
        /// PE, then whatever else somebody asked for. Every one of them heats
        /// with the same current, because a bench has one number for it - a
        /// real coupler's pins differ, and a real bench would give each its
        /// own model.
        /// </remarks>
        private async Task StartSensors()
        {

            var names = new[] { "DC+ pin", "DC- pin", "PE pin", "CP pin", "PP pin", "shield" };

            for (var i = 0; i < options.MCSSensors; i++)
            {

                var medium = new UdpMulticastT1STransport(
                                 protocols.ISO15118.T1S.T1SConstants.RandomLocalMac(),
                                 options.T1SBus
                             );

                var sensor = new TemperatureSensorNode(
                                 medium,
                                 new TemperatureSensorOptions(i < names.Length ? names[i] : $"pin {i + 1}")
                             ) {
                                 Current_A = options.MCSCurrent_A
                             };

                var name = sensor.Options.Name;

                sensor.Follower.Attached  += (_, assign) => Console.Say("sensor", $"'{name}' is node {assign.NodeId} on the coupler's bus, carrying {sensor.Current_A:F0} A.");
                sensor.Follower.Detached  += (_, reason) => Console.Say("sensor", $"'{name}' is off the bus: {reason}.");

                await medium.StartAsync();
                await sensor.StartAsync();

                sensorMedia.Add(medium);
                sensors.    Add(sensor);

                // One at a time, each on the bus before the next asks. Two
                // asking in the same discovery opportunity collide and back
                // off for a random few cycles each, and the bench would then
                // number its pins in whatever order the dice fell.
                if (!await sensor.Follower.WaitUntilAttachedAsync(TimeSpan.FromSeconds(10)))
                    Console.Say("sensor", $"'{name}' was not given a node identifier within 10 s and keeps asking.");

            }

            // What the station hears from them goes to its log; what the bench
            // wants to know is said here, once.
            if (Station.V2G is { } link)
                link.ThermalStateChanged += (_, change) => {
                    if (change.IsAlarm)
                        Console.Say("env", $"COUPLER ALARM: {change.Node.Name} is {change.To.ToString().ToLowerInvariant()}" +
                                           (change.Temperature_C is { } c ? $" at {c:F1} °C" : "") +
                                           $". Set --mcs-current below the pins' 500 A rating, or watch it cool with the current off.");
                    else if (change.IsAllClear)
                        Console.Say("env", $"Coupler clear: {change.Node.Name} is {change.To.ToString().ToLowerInvariant()} again" +
                                           (change.Temperature_C is { } c ? $" at {c:F1} °C" : "") + ".");
                };

            Console.Say("env", $"MCS: {sensors.Count} temperature sensor(s) on the coupler's bus at {options.T1SBus}, " +
                               $"carrying {options.MCSCurrent_A:F0} A" +
                               (options.MCSCurrent_A > 500
                                    ? " - over the pins' 500 A rating, so they will overheat within a minute."
                                    : options.MCSCurrent_A > 0
                                        ? " - within the pins' rating; they warm up and settle."
                                        : " - a cold coupler. --mcs-current <A> heats it.") +
                               " The station polls every node twice a second; the vehicle joins at each session.");

        }

        /// <summary>
        /// The sensors leave the bus and let go of their media.
        /// </summary>
        private async Task StopSensors()
        {

            foreach (var sensor in sensors)
                await sensor.DisposeAsync();

            foreach (var medium in sensorMedia)
                await medium.DisposeAsync();

            sensors.    Clear();
            sensorMedia.Clear();

        }

        #endregion

        #region (private static) FrontendOf(Options, Component, Directory)

        /// <summary>
        /// Where a component's web interface comes from: the bundle inside its
        /// assembly, or - while somebody is working on it - the directory
        /// webpack writes.
        /// </summary>
        private static IStaticContentSource? FrontendOf(TestEnvironmentOptions  Options,
                                                        String                  Component,
                                                        String                  Directory)
        {

            if (!Options.FrontendFromDisk)
                return null;

            var path = Path.Combine("libs", Component, Directory, "Frontend", "dist");

            return System.IO.Directory.Exists(path)
                       ? new FileSystemContentSource(path)
                       : null;

        }

        #endregion

        #region (private static) V2GOptionsFor(Options, Certificates)

        /// <summary>
        /// What the station offers a vehicle on the wire below the charging
        /// cable - and the certificate it offers it with.
        /// </summary>
        /// <remarks>
        /// The SECC leaf out of the ISO 15118-2 hierarchy, because this
        /// endpoint is .NET's own TLS: the -20 profile's secp521r1 is
        /// unavailable there on Windows and macOS, and a station holding a
        /// certificate its own TLS stack will not serve is a station nothing
        /// can connect to.
        /// </remarks>
        private static StationV2G.V2GOptions V2GOptionsFor(TestEnvironmentOptions  Options,
                                                           TestPKI                 Certificates)
        {

            if (Options.NoV2G)
                return StationV2G.V2GOptions.Off;

            var (leaf, chain) = Certificates.SECCCertificate();

            return new StationV2G.V2GOptions {

                       Enabled                 = true,
                       InterfaceName           = Options.InterfaceName,
                       ServerCertificate       = leaf,

                       // With the Sub-CAs, so that a vehicle can build a chain
                       // to the root it was given. Without them the handshake
                       // fails at the vehicle, reporting the vehicle's own
                       // trust store as the problem.
                       ServerCertificateChain  = chain,

                       SDP                     = true,

                       // On, and on is right here and nowhere else: the vehicle
                       // that is looking for this station is another object in
                       // this same process, and without this the multicast it
                       // sends never comes back up the stack to be answered.
                       MulticastLoopback       = true,

                       // No SLAC on a bus: a megawatt coupler has no powerline
                       // to sound. Auto would only say so at every start.
                       SlacTransport           = Options.MCS
                                                     ? StationV2G.SlacTransportKind.None
                                                     : StationV2G.SlacTransportKind.Auto,

                       // No T1S here: the bus is in the station's own
                       // configuration file, which WriteConfigurations wrote
                       // before this station was built and which it reads as
                       // it is constructed.

                   };

        }

        #endregion

        #region (private static) WriteConfigurations(Options, Certificates)

        /// <summary>
        /// What each of the four that talk to each other is told in writing,
        /// before it reads it. The EMSP is told nothing: nothing here dials it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Written to their configuration files rather than passed to their
        /// constructors, and for the reason each of those programs already
        /// gives for its own switches: these four keep what they are told, and
        /// their web interfaces edit the same files. A port handed to a
        /// constructor and kept nowhere is a port the Configuration page would
        /// show as something else.
        /// </para>
        /// <para>
        /// Merged rather than replaced, so that everything somebody changed in
        /// a web interface between two starts survives - only the fields that
        /// say where these four find each other are written here.
        /// </para>
        /// </remarks>
        private static void WriteConfigurations(TestEnvironmentOptions  Options,
                                                TestPKI                 Certificates)
        {

            #region The CSMS: the port the stations and controllers dial

            var csmsFile = new CSMSConfig.CSMSConfigFile(
                               Path.Combine(Options.CSMSDirectory, CSMSConfig.CSMSConfigFile.DefaultFileName)
                           );

            if (!csmsFile.TryMergeSection(
                     CSMSConfig.OCPPServerConfiguration.SectionName,
                     new JObject(
                         new JProperty("enabled",      true),
                         new JProperty("address",      Options.Address.ToString()),
                         new JProperty("port",             Options.CSMSOCPPPort.ToUInt16()),
                         new JProperty("reachableAs",      new JArray(Options.Host, "localhost")),

                         // Written out rather than left at the default, and
                         // this is the field that decides whether the port is
                         // encrypted at all: anything above 1 in it turns TLS
                         // on. A port that is encrypted while the things below
                         // it dial ws:// answers them with a corrupted frame
                         // and no explanation, which is what this environment
                         // did until 2026-09-19.
                         new JProperty("securityProfiles", new JArray(Options.OCPPSecurityProfiles))
                     ),
                     out var csmsError))
            {
                throw new InvalidOperationException($"The CSMS could not be told which port to listen on: {csmsError}");
            }

            #endregion

            #region The local controller: its own port, and where it reports

            if (!Options.NoLocalController)
            {

                var controllerFile = new ControlConfig.ControllerConfigFile(
                                         Path.Combine(Options.ControllerDirectory, ControlConfig.ControllerConfigFile.DefaultFileName)
                                     );

                if (!controllerFile.TryMergeSection(
                         ControlConfig.OCPPServerConfiguration.SectionName,
                         new JObject(
                             new JProperty("enabled",      true),
                             new JProperty("address",      Options.Address.ToString()),
                             new JProperty("port",             Options.ControllerOCPPPort.ToUInt16()),
                             new JProperty("reachableAs",      new JArray(Options.Host, "localhost")),
                             new JProperty("securityProfiles", new JArray(Options.OCPPSecurityProfiles))
                         ),
                         out var serverError))
                {
                    throw new InvalidOperationException($"The local controller could not be told which port to listen on: {serverError}");
                }

                if (!controllerFile.TryMergeSection(
                         ControlConfig.CSMSConfiguration.SectionName,
                         new JObject(
                             new JProperty("enabled",          true),
                             new JProperty("url",              $"{Options.OCPPScheme}://{Options.Host}:{Options.CSMSOCPPPort}"),

                             // The same decision as the port above it, and it
                             // has to be the same one: a controller that dials
                             // ws:// at a port listening for wss:// is answered
                             // with a corrupted frame, and one that dials wss://
                             // at a plain port waits for a handshake nobody is
                             // going to make. See TestEnvironmentOptions.OCPPTLS
                             // for why this is off unless it is asked for.
                             new JProperty("securityProfile",  Options.OCPPSecurityProfile),
                             new JProperty("nextHopNodeId",    "CSMS")
                         ),
                         out var upstreamError))
                {
                    throw new InvalidOperationException($"The local controller could not be told where to report: {upstreamError}");
                }

            }

            #endregion

            #region The station: what it offers below the cable

            var stationFile = new StationConfig.StationConfigFile(
                                  Path.Combine(Options.StationDirectory, StationConfig.StationConfigFile.DefaultFileName)
                              );

            var v2g = new JObject(
                          new JProperty("enabled",      !Options.NoV2G),
                          new JProperty("sdp",          !Options.NoV2G),
                          new JProperty("loopback",     true),
                          new JProperty("interface",    Options.InterfaceName),

                          // The coupler's bus, written here rather than handed
                          // to the constructor, because this is where every
                          // other thing the station is gets said - and because
                          // a bus in the file is one somebody can see and
                          // change on the Configuration page afterwards.
                          //
                          // Half a second between cycles: every pin asked twice
                          // a second, which is a log somebody can read and a
                          // reaction time no thermistor can beat.
                          new JProperty("t1sTransport", Options.MCS ? "udp" : "none"),
                          new JProperty("t1sCycleMs",   500)
                      );

            if (Options.MCS)
                v2g["t1sBus"] = Options.T1SBus.ToString();

            if (!stationFile.TryMergeSection(
                     StationConfig.V2GConfiguration.SectionName,
                     v2g,
                     out var v2gError))
            {
                throw new InvalidOperationException($"The charging station could not be told what to offer a vehicle: {v2gError}");
            }

            #endregion

            #region The vehicle: which station, and with what

            if (!Options.NoVehicle)
            {

                var vehicleFile = new EVConfig.EVConfigFile(
                                      Path.Combine(Options.VehicleDirectory, EVConfig.EVConfigFile.DefaultFileName)
                                  );

                if (!vehicleFile.TryMergeSection(
                         EVConfig.V2GConfiguration.SectionName,
                         new JObject(

                             new JProperty("interface",          Options.InterfaceName),

                             // The station this vehicle is looking for is
                             // another object in this same process, on the same
                             // interface. Without this the multicast request
                             // goes out and never comes back up the stack to be
                             // answered, and the discovery times out against a
                             // station that is sitting right there.
                             //
                             // Set on both sides on purpose: whose socket this
                             // switch belongs to is a thing the platforms
                             // disagree about - POSIX says the sender's,
                             // Windows the receiver's - so a bench sets it on
                             // both and stops caring which.
                             new JProperty("multicastLoopback",  true)

                         ),
                         out var linkError))
                {
                    throw new InvalidOperationException($"The vehicle could not be told where to look for a station: {linkError}");
                }

                // No "connect": the station in this environment answers SDP, so
                // the vehicle finds it the way a vehicle does. A connect
                // address written here would take the one thing worth watching
                // out of the run.
                //
                // No certificates either: those are handles into the vehicle's
                // store, which does not exist before the vehicle does, so they
                // are imported and named in HandOutCertificates instead.
                var session = new JObject(
                                  new JProperty("tls",                  "dotnet"),
                                  new JProperty("pkiDirectory",         Certificates.ISO20DevDirectory)
                              );

                // The coupler's bus on an MCS bench, joined before SDP and left
                // when the session ends.
                if (Options.MCS)
                {
                    session["t1sTransport"]  = "udp";
                    session["t1sBus"]        = Options.T1SBus.ToString();
                }

                if (!vehicleFile.TryMergeSection(
                         EVConfig.SessionConfiguration.SectionName,
                         session,
                         out var sessionError))
                {
                    throw new InvalidOperationException($"The vehicle could not be given its certificates: {sessionError}");
                }

                // What a merge keeps that should go. A merge keeps whatever it
                // is not told about - a null is ignored, not written - so two
                // kinds of leftover have to be taken out by hand: a bus left
                // over from yesterday's --mcs, with nobody coordinating it
                // today, which the vehicle would wait ten seconds for at every
                // session; and the certificate paths of a file written before
                // the vehicle had a store, which the vehicle now refuses by
                // name rather than reads.
                if (vehicleFile.TryLoadDocument(out var document, out _) &&
                    document[EVConfig.SessionConfiguration.SectionName] is JObject section)
                {

                    var stale = false;

                    if (!Options.MCS)
                        foreach (var field in new[] { "t1sTransport", "t1sBus", "t1sInterface", "t1sWeight" })
                            stale |= section.Remove(field);

                    stale |= section.Remove("trustRoots");

                    foreach (var field in EVConfig.SessionConfiguration.CertificateFields)
                    {
                        if (section[field] is { Type: JTokenType.String } token &&
                            token.Value<String>() is { } written &&
                            (written.Contains('/') || written.Contains('\\') || written.Contains('.')))
                        {
                            stale |= section.Remove(field);
                        }
                    }

                    if (stale && !vehicleFile.TryWrite(document, out var writeError))
                        throw new InvalidOperationException($"The vehicle's stale settings could not be forgotten: {writeError}");

                }

            }

            #endregion

        }

        #endregion

    }

}
