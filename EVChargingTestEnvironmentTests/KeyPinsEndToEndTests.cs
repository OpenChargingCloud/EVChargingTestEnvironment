/*
 * Copyright (c) 2014-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of the EV charging test environment
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

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;

using cloud.charging.open.protocols.OCPP.Node.KeyPins;
using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

using cloud.charging.open.ChargingStation.OCPP;
using cloud.charging.open.LocalController.OCPP;

using WWCPNode    = cloud.charging.open.protocols.WWCP.Node.WWCPNode;
using Management  = cloud.charging.open.CSMS.CSMS;
using Controller  = cloud.charging.open.LocalController.LocalController;
using Station     = cloud.charging.open.ChargingStation.ChargingStation;

#endregion

namespace cloud.charging.open.TestEnvironment.Tests
{

    /// <summary>
    /// Key pins from end to end ("Key Pins and TLS 1.3 Raw Public Keys for Local
    /// Controllers", sections 5.5 to 5.7, work package K4, phase C): a real
    /// charging station that learns its local controller's key pin on first use
    /// over wss://, a real controller that dials a real CSMS in the overlay
    /// network, and the CSMS that confirms the pin with a signed pin set - and
    /// rolls the controller's key over without the station losing its
    /// connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What no repository can test on its own: the CSMS's tests have a fake
    /// controller and station on one WebSocket, the controller's a fake CSMS,
    /// the station's a fake controller that signs as a CSMS would. Here the
    /// pin set is signed by the CSMS's own key, carried by the controller's
    /// FORWARD, and verified by the station; the station's report of
    /// TLSClientController, asked while it is on probation, goes up the same
    /// way.
    /// </para>
    /// <para>
    /// The station signs in to the controller with a password over TLS,
    /// security profile 2: the key pin is the server's, which is what this is
    /// about. One CSMS, one controller and one station per test, on fresh
    /// ports, with their time servers switched off.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class KeyPinsEndToEndTests
    {

        #region Data

        private const String  ControllerId     = "lc001";
        private const String  StationId        = "cs001";

        private const String  UplinkPassword   = "the-controllers-password-at-its-csms";
        private const String  StationPassword  = "the-stations-password-at-its-controller";

        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(45);

        private Management?   csms;
        private Controller?   controller;
        private KeyPin        currentPin  = default!;
        private String        csmsSigner  = "";

        private readonly List<Station>    stations     = [];
        private readonly List<String>     directories  = [];
        private readonly List<DNSClient>  resolvers    = [];

        #endregion

        #region SetUp / TearDown

        /// <summary>
        /// A CSMS with a signing key for pin sets, and a controller with a
        /// server key on its station port that dials the CSMS in the overlay
        /// network, connected.
        /// </summary>
        [SetUp]
        public async Task StartTheCSMSAndTheController()
        {

            var csmsDirectory        = Directory("csms-keypins");
            var controllerDirectory  = Directory("lc-keypins");

            csms        = await TestPorts.StartedOnFreshPorts(() => NewCSMS(csmsDirectory));

            Assert.That(CSMS.PinSetSigner.TryCreate("phase C", out var signer, out var signerError), Is.True, signerError);

            csmsSigner  = signer!;

            // Before the controller is built: whether its station port speaks
            // TLS at all is decided when it starts.
            using (var store = new ServerCertificateStore(Path.Combine(controllerDirectory, ServerCertificateStore.DefaultDirectoryName)))
            {
                Assert.That(store.TryCreateServerKey(null, AsNext: false, out _, out var current, out var keyError), Is.True, keyError);
                currentPin = current!;
            }

            controller  = await TestPorts.StartedOnFreshPorts(() => NewController(controllerDirectory));

            await Until(() => LC.CSMSConnected && CSMS.IsConnected(ControllerId) && CSMS.InOverlayNetwork(ControllerId),
                        "The controller connecting to its CSMS in the overlay network");

            Assert.That(LC.OCPPServerTLS, Is.True, "a controller with a server key does not speak TLS on its station port");

        }

        [TearDown]
        public async Task StopEverything()
        {

            foreach (var station in stations)
                await station.DisposeAsync();

            if (controller is not null)
                await controller.DisposeAsync();

            if (csms is not null)
                await csms.DisposeAsync();

            foreach (var resolver in resolvers)
                resolver.Dispose();

            foreach (var directory in directories)
                TestParties.Remove(directory);

            stations.   Clear();
            directories.Clear();
            resolvers.  Clear();

            controller  = null;
            csms        = null;

        }

        #endregion


        #region (private) The CSMS, the controller and the station

        private String Directory(String Purpose)
        {
            var directory = TestParties.TemporaryDirectory(Purpose);
            directories.Add(directory);
            System.IO.Directory.CreateDirectory(directory);
            return directory;
        }

        /// <summary>
        /// A name resolver with its servers written down: nothing here resolves
        /// a name, everything is dialled at 127.0.0.1.
        /// </summary>
        private DNSClient Resolver()
        {

            var resolver = new DNSClient([
                               new DNSServerConfig(IPv4Address.Parse("192.0.2.53"),   IPPort.DNS),
                               new DNSServerConfig(IPv6Address.Parse("2001:db8::53"), IPPort.DNS)
                           ]);

            resolvers.Add(resolver);

            return resolver;

        }

        /// <summary>
        /// A CSMS whose station server listens on the loopback address, and
        /// which lets the controller in with its password.
        /// </summary>
        private Management NewCSMS(String Directory)
        {

            var configFile = Path.Combine(Directory, "configuration.json");

            File.WriteAllText(configFile, new JObject(
                new JProperty("nts",         TestParties.NoTimeServers),
                new JProperty("ocppServer",  new JObject(
                    new JProperty("enabled",           true),
                    new JProperty("address",           "127.0.0.1"),
                    new JProperty("port",              TestPorts.Free()),
                    new JProperty("securityProfiles",  new JArray(1)),
                    new JProperty("subprotocols",      new JArray("ocpp2.1"))
                ))
            ).ToString());

            var management = new Management(
                                 DNSClient:       Resolver(),
                                 HTTPPort:        IPPort.Parse(TestPorts.Free()),
                                 AccountsPath:    Path.Combine(Directory, "accounts"),
                                 ConfigFile:      new WWCPConfigFile(configFile),
                                 LogToConsole:    false,
                                 BridgeDebugLog:  false
                             );

            Assert.That(management.StationLogins.TrySetPassword(ControllerId, UplinkPassword, null, "tests", out _, out var error), Is.True, error);

            // A local controller's login: what it forwards in the name of the
            // stations behind it is taken, which a station's would not be.
            Assert.That(management.StationLogins.TrySetLocalController(ControllerId, true, out var markError), Is.True, markError);

            return management;

        }

        /// <summary>
        /// A controller whose station port speaks TLS with the server key made
        /// beforehand, security profile 2, and which dials the CSMS in the
        /// overlay network - which is what lets the CSMS address the stations
        /// behind it.
        /// </summary>
        private Controller NewController(String Directory)
        {

            var configFile = Path.Combine(Directory, "configuration.json");

            File.WriteAllText(configFile, new JObject(
                new JProperty("nts",         TestParties.NoTimeServers),
                new JProperty("csms",        new JObject(
                    new JProperty("enabled",                true),
                    new JProperty("url",                    $"ws://127.0.0.1:{CSMS.OCPPServerSettings.TCPPort}"),
                    new JProperty("securityProfile",        1),
                    new JProperty("networkingMode",         "overlay"),
                    new JProperty("reconnectInitialDelay",  1),
                    new JProperty("reconnectMaxDelay",      2)
                )),
                new JProperty("ocppServer",  new JObject(
                    new JProperty("enabled",           true),
                    new JProperty("address",           "127.0.0.1"),
                    new JProperty("port",              TestPorts.Free()),
                    new JProperty("securityProfiles",  new JArray(2)),
                    new JProperty("subprotocols",      new JArray("ocpp2.1"))
                ))
            ).ToString());

            var localController = new Controller(
                                      DNSClient:       Resolver(),
                                      HTTPPort:        IPPort.Parse(TestPorts.Free()),
                                      AccountsPath:    Path.Combine(Directory, "accounts"),
                                      ConfigFile:      new WWCPConfigFile(configFile),
                                      LogToConsole:    false,
                                      BridgeDebugLog:  false
                                  );

            Assert.That(localController.Node.Id.ToString(), Is.EqualTo(ControllerId));

            Assert.That(localController.CSMSLogin.    TrySetPassword(ControllerId, UplinkPassword, out var uplinkError),                     Is.True, uplinkError);
            Assert.That(localController.StationLogins.TrySetPassword(StationId,    StationPassword, null, "tests", out _, out var loginError), Is.True, loginError);

            return localController;

        }

        /// <summary>
        /// A charging station, built and not started, in a directory of its own
        /// - or in the given one, as a station made again from there.
        /// </summary>
        private Station NewStation(String? Directory = null)
        {

            var directory   = Directory ?? this.Directory("cs-keypins");
            var configFile  = Path.Combine(directory, "configuration.json");

            if (!File.Exists(configFile))
                File.WriteAllText(configFile, new JObject(new JProperty("nts", TestParties.NoTimeServers)).ToString());

            var station = new Station(
                              DNSClient:       Resolver(),
                              HTTPPort:        IPPort.Parse(TestPorts.Free()),
                              NoKiosk:         true,
                              AccountsPath:    Path.Combine(directory, "accounts"),
                              ConfigFile:      new WWCPConfigFile(configFile),
                              LogToConsole:    false,
                              BridgeDebugLog:  false
                          );

            stations.Add(station);

            return station;

        }

        /// <summary>
        /// The station started - made again from its directory, on fresh ports,
        /// where a port of its was taken before it could bind it.
        /// </summary>
        private async Task<Station> Started(Station Station)
        {

            var directory  = Path.GetDirectoryName(Station.ConfigFile.Path)!;
            var attempt    = 0;

            return await TestPorts.StartedOnFreshPorts(() => attempt++ == 0 ? Station : NewStation(directory));

        }

        /// <summary>
        /// A station behind the controller, as it is commissioned: it dials the
        /// controller's wss:// port with its password, pins nothing, learns the
        /// key the controller shows on first use, and believes pin sets the
        /// CSMS signed - started.
        /// </summary>
        private async Task<(Station Station, String Connection)> AStationBehindTheController()
        {

            var station = NewStation();

            Assert.That(station.Connections.TryAddPinSetSigner(csmsSigner, "The CSMS", out var signerError, out _), Is.True, signerError);

            Assert.That(station.Connections.TryAddAuthentication("The local controller", "basic", StationId, StationPassword, out var authentication, out var error),
                        Is.True, error);

            Assert.That(station.Connections.TryAddConnection("The local controller",
                                                             $"wss://127.0.0.1:{LC.OCPPServerSettings.TCPPort}/{StationId}",
                                                             "localcontroller",
                                                             true,
                                                             authentication,
                                                             null,
                                                             out var connection,
                                                             out error,
                                                             KeyPinTrust: new ServerKeyPinSettings(null, "Key", null)),
                        Is.True, error);

            return (await Started(station), connection!);

        }

        /// <summary>
        /// A station behind the controller whose learned key pin the CSMS
        /// confirmed.
        /// </summary>
        private async Task<(Station Station, String Connection)> AConfirmedStation()
        {

            var (station, connection) = await AStationBehindTheController();

            await Until(() => EntryOf(station).ServerKeyPinState == KeyPinState.Confirmed,       "The CSMS confirming the key pin the station learned", station);
            await Until(() => CSMS.KeyPins.StationOf(StationId)?.Accepted == true,               "The CSMS noting the pin set taken",                    station);
            await Until(() => Said(csms, $"'{StationId}' confirmed the key pin it learned"),     "The station's KeyPinConfirmed reaching the CSMS",      station);

            return (station, connection);

        }

        private static ConnectionEntry EntryOf(Station Station)
            => Station.Connections.Connections.Single();

        private static String[] PinsOf(Station Station)
            => [.. EntryOf(Station).ServerKeyPins.Select(pin => pin.ToHex())];

        private static Boolean IsConnected(Station Station, String Connection)
            => Station.ConnectionStates.TryGetValue(Connection, out var state) && state.Status == ConnectionStatus.Connected;

        private static String RegistrationOf(Station Station)
            => Station.RegistrationJSON()["2.1"]?["state"]?.Value<String>() ?? "";

        private Management CSMS
            => csms       ?? throw new InvalidOperationException("No CSMS is running.");

        private Controller LC
            => controller ?? throw new InvalidOperationException("No controller is running.");

        #endregion

        #region (private) Until(Condition, What, Station = null) / Said(Node, Text) / Story(Station)

        private async Task Until(Func<Boolean> Condition, String What, Station? Station = null)
        {

            var until = DateTimeOffset.UtcNow + Patience;

            while (!Condition())
            {

                if (DateTimeOffset.UtcNow > until)
                    Assert.Fail($"{What} did not happen within {Patience.TotalSeconds:F0} s.{Story(Station)}");

                await Task.Delay(25);

            }

        }

        private static Boolean Said(WWCPNode? Node, String Text)
            => Node?.Log.Recent(500).Any(entry => entry.Message.Contains(Text, StringComparison.Ordinal)) ?? false;

        /// <summary>
        /// What the CSMS, the controller and a station said, for a test that failed.
        /// </summary>
        private String Story(Station? Station = null)

            => String.Join(
                   Environment.NewLine,
                   new[] { "", "What was said:" }.
                       Concat(new (String Who, WWCPNode? Node)[] { ("CSMS", csms), ("LC", controller) }.
                                  SelectMany(speaker => speaker.Node?.Log.Recent(100).Select(entry => $"  {speaker.Who,-4} {entry.Timestamp:HH:mm:ss.fff} [{entry.LevelName}] {entry.Message}") ?? [])).
                       Concat(Station?.Log.Recent(100).Select(entry => $"  CS   {entry.Timestamp:HH:mm:ss.fff} [{entry.LevelName}] {entry.Message}") ?? []).
                       Concat(Station?.DialledConnections.Select(said => $"  CS   dialled {said.Key}: {said.Value}") ?? [])
               );

        #endregion


        #region ALearnedKeyPin_IsConfirmedByTheCSMS_ThroughTheController()

        /// <summary>
        /// The station learns the controller's key pin on first use, boots
        /// through the controller, and reports KeyPinLearned; the CSMS reads the
        /// controller's pins, asks the station - on probation - for its report
        /// of TLSClientController, and sends it a pin set signed with its own
        /// key, which the controller carries down and the station takes: the
        /// pin is confirmed, and the station never lost its connection.
        /// </summary>
        [Test]
        public async Task ALearnedKeyPin_IsConfirmedByTheCSMS_ThroughTheController()
        {

            var (station, connection) = await AConfirmedStation();

            var entry = EntryOf(station);

            Assert.Multiple(() => {
                Assert.That(PinsOf(station),                                 Is.EqualTo(new[] { currentPin.ToHex() }), Story(station));
                Assert.That(entry.ServerKeyPinsVersion,                      Is.EqualTo(1));
                Assert.That(entry.ServerKeyPinLearned,                       Is.Null, "the learned key pin is still on probation");
                Assert.That(IsConnected(station, connection),                Is.True, "the station lost its connection");
                Assert.That(RegistrationOf(station),                         Is.EqualTo("Accepted"));
                Assert.That(CSMS.KeyPins.LocalControllerOf(StationId),       Is.EqualTo(ControllerId));
                Assert.That(CSMS.KeyPins.StationOf(StationId)?.Connection,   Is.EqualTo(connection), "the pin set went to another endpoint");
                Assert.That(CSMS.KeyPins.Get(ControllerId)?.CurrentKeyPin,   Is.EqualTo(currentPin.ToHex()));
            });

        }

        #endregion

        #region TheControllersKey_IsRolledOver_WithoutTheStationDropping_AndTheStationDialsTheNextKey()

        /// <summary>
        /// The controller announces a next key; the CSMS rolls it over - the
        /// station handed [current, next], asked whether it has the next pin,
        /// the controller switched, the station handed [next] - while the
        /// station stays connected. Started again, the station dials the
        /// controller, which shows its next key now, and gets in.
        /// </summary>
        [Test]
        public async Task TheControllersKey_IsRolledOver_WithoutTheStationDropping_AndTheStationDialsTheNextKey()
        {

            var (station, connection) = await AConfirmedStation();

            Assert.That(LC.ServerCertificates.TryCreateServerKey(null, AsNext: true, out _, out var next, out var nextError), Is.True, nextError);

            #region The rollover, the station's connection watched throughout

            var drops     = 0;
            var watching  = new CancellationTokenSource();

            var watcher   = Task.Run(async () => {

                                var was = true;

                                while (!watching.IsCancellationRequested)
                                {

                                    var now = IsConnected(station, connection);

                                    if (was && !now)
                                        Interlocked.Increment(ref drops);

                                    was = now;

                                    await Task.Delay(5);

                                }

                            });

            var run       = await CSMS.RolloverAsync(ControllerId, TimeSpan.Zero);

            watching.Cancel();
            await watcher;
            watching.Dispose();

            #endregion

            var entry = EntryOf(station);

            Assert.Multiple(() => {
                Assert.That(run.Done,                                            Is.True, String.Join(Environment.NewLine, run.Said) + Story(station));
                Assert.That(PinsOf(station),                                     Is.EqualTo(new[] { next!.ToHex() }));
                Assert.That(entry.ServerKeyPinsVersion,                          Is.EqualTo(3), "not [current, next] and then [next], each a version above the last");

                // Confirmed is what a pin set that confirmed a learned pin left;
                // the rollover's pin sets confirm nothing, they configure.
                Assert.That(entry.ServerKeyPinState,                             Is.EqualTo(KeyPinState.Configured));
                Assert.That(LC.ServerCertificates.CurrentKey?.KeyPin.ToHex(),    Is.EqualTo(next.ToHex()), "the controller was not switched");
                Assert.That(drops,                                               Is.Zero, "the station lost its connection during the rollover");
                Assert.That(IsConnected(station, connection),                    Is.True);
            });

            #region Started again: the station dials the controller's next key

            var directory = Path.GetDirectoryName(station.ConfigFile.Path)!;

            await station.DisposeAsync();
            stations.Remove(station);

            var again           = await Started(NewStation(directory));
            var againConnection = EntryOf(again).Id;

            await Until(() => IsConnected(again, againConnection),     "The station dialling the controller again, which shows its next key", again);
            await Until(() => RegistrationOf(again) == "Accepted",     "The station being accepted again",                                     again);

            Assert.Multiple(() => {
                Assert.That(PinsOf(again),                     Is.EqualTo(new[] { next.ToHex() }));
                Assert.That(EntryOf(again).ServerKeyPinState,  Is.EqualTo(KeyPinState.Configured));
                Assert.That(EntryOf(again).ServerKeyPinLearned, Is.Null, "the station learned a key on first use again");
            });

            #endregion

        }

        #endregion

    }

}
