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

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Hermod.SSH;
using org.GraphDefined.Vanaheimr.Hermod.SSH.Client;
using org.GraphDefined.Vanaheimr.Hermod.Messaging;

using cloud.charging.open.protocols.WWCP;
using cloud.charging.open.protocols.WWCP.NetworkingNode;
using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.TestKit;
using cloud.charging.open.protocols.OCPP;
using cloud.charging.open.protocols.OCPP.SSH;
using cloud.charging.open.protocols.OCPPv2_1;
using cloud.charging.open.protocols.OCPPv2_1.CS;
using cloud.charging.open.protocols.OCPPv2_1.LC;

using cloud.charging.open.ChargingStation.OCPP;

using Management  = cloud.charging.open.CSMS.CSMS;
using Controller  = cloud.charging.open.LocalController.LocalController;
using Station     = cloud.charging.open.ChargingStation.ChargingStation;

#endregion

namespace cloud.charging.open.TestEnvironment.Tests
{

    /// <summary>
    /// OCPP over SSH from end to end: a real charging station dialling a real
    /// local controller at "ssh://", signed in with its own key, on the
    /// subsystem "ocpp@open.charging.cloud" of the controller's SSH server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What the plan "OCPP-over-SSH.md" asks of the integration in 5.8, and
    /// what neither repository can test on its own: the controller's tests
    /// dial it with a plain SSH message client, and the station's tests dial
    /// an SSH server of their own that stands in for a CSMS. Here both ends
    /// are the classes their programs run.
    /// </para>
    /// <para>
    /// Where the station cannot be made to do what a case is about - say
    /// nothing after the subsystem, ask for a shell, send a frame longer than
    /// it would ever send - a plain SSH client stands in for it, signed in as
    /// a station with a key the controller was given, against the same real
    /// controller.
    /// </para>
    /// <para>
    /// The controller answers what its stations send to their CSMS - the
    /// BootNotification, the heartbeat, a binary data transfer - itself: its
    /// OCPP node takes "CSMS" as one of its own names, which is what the
    /// node's BootNotification handler was written for. A controller that
    /// forwards them instead loses the answers on the way back down
    /// (<see cref="AStationsRequest_ThroughTheController_IsAnsweredByTheCSMS"/>),
    /// over SSH as over a WebSocket; these tests are about the SSH leg below
    /// the controller, which that does not touch.
    /// </para>
    /// <para>
    /// One controller per test, on fresh ports from the node's test kit, with
    /// its time servers switched off: nothing here leaves the machine.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class OCPPOverSSHTests
    {

        #region Data

        /// <summary>
        /// The subsystem the stations speak OCPP on.
        /// </summary>
        private const String  Subsystem       = OCPPSshServer.OCPPSubsystem;

        /// <summary>
        /// The largest message the controller takes over SSH in these tests:
        /// the smallest it can be set to, written down rather than left at the
        /// default, so that a frame just over it is a test of what the file
        /// says and not only of Hermod's default.
        /// </summary>
        private const UInt32  MaxMessageSize  = 64 * 1024;

        /// <summary>
        /// How long anything in here is waited for.
        /// </summary>
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

        /// <summary>
        /// The controller's password at the CSMS.
        /// </summary>
        private const String  UplinkPassword  = "the-controllers-password-at-its-csms";

        private Management?                      csms;
        private String                           csmsDirectory         = "";

        private Controller?                      controller;
        private String                           controllerDirectory   = "";
        private IPPort                           controllerHTTPPort;

        private readonly List<Station>           stations              = [];
        private readonly List<String>            directories           = [];
        private readonly ConcurrentBag<DNSClient> resolvers            = [];

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public async Task StartTheController()
        {

            controllerDirectory = TestParties.TemporaryDirectory("lc-ssh");
            directories.Add(controllerDirectory);

            // Made again, on fresh ports, where another test run on this
            // machine took one before the controller could bind it.
            controller = await TestPorts.StartedOnFreshPorts(() => NewController(controllerDirectory, ControllerConfiguration()));

            AnswersForTheCSMS(controller);

            Assert.That(controller.OCPPOverSSHServed, Is.True, "OCPP over SSH is switched on in the file, and not served.");

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


        #region (private) The CSMS

        /// <summary>
        /// A CSMS whose station server listens on the loopback address, and
        /// which lets the controller in with its password: for the one test
        /// that has the controller report to a CSMS.
        /// </summary>
        private Management NewCSMS(String Directory)
        {

            System.IO.Directory.CreateDirectory(Directory);

            var configFile = Path.Combine(Directory, "configuration.json");

            File.WriteAllText(configFile, new JObject(
                new JProperty("nts",         TestParties.NoTimeServers),
                new JProperty("ocppServer",  new JObject(
                    new JProperty("enabled",           true),
                    new JProperty("address",           "127.0.0.1"),
                    new JProperty("port",              TestPorts.Free()),
                    new JProperty("securityProfiles",  new JArray(1)),
                    new JProperty("subprotocols",      new JArray("ocpp2.1", "ocpp2.0.1"))
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

            return management;

        }

        private Management CSMS
            => csms ?? throw new InvalidOperationException("No CSMS is running.");

        #endregion

        #region (private) The controller

        /// <summary>
        /// Who the controller is at its CSMS: its OCPP node's default name.
        /// </summary>
        private const String ControllerId = "lc001";

        /// <summary>
        /// A controller whose station server listens on the loopback address,
        /// over a WebSocket and over SSH, with an SSH server of its own - and
        /// which reports to the given CSMS, where one is given.
        /// </summary>
        private static JObject ControllerConfiguration(Management? CSMS = null)

            => new (
                   new JProperty("nts",         TestParties.NoTimeServers),
                   new JProperty("csms",        CSMS is null
                       ? new JObject(
                             new JProperty("enabled",           false)
                         )
                       : new JObject(
                             new JProperty("enabled",           true),
                             new JProperty("url",               $"ws://127.0.0.1:{CSMS.OCPPServerSettings.TCPPort}"),
                             new JProperty("securityProfile",   1),
                             new JProperty("nextHopNodeId",     "CSMS")
                         )),
                   new JProperty("ocppServer",  new JObject(
                       new JProperty("enabled",           true),
                       new JProperty("address",           "127.0.0.1"),
                       new JProperty("port",              TestPorts.Free()),
                       new JProperty("securityProfiles",  new JArray(1)),
                       new JProperty("subprotocols",      new JArray("ocpp2.1", "ocpp2.0.1")),
                       new JProperty("ssh",               new JObject(
                           new JProperty("enabled",         true),
                           new JProperty("maxMessageSize",  MaxMessageSize)
                       ))
                   )),
                   new JProperty("ssh",         new JObject(
                       new JProperty("enabled",           true),
                       new JProperty("port",              TestPorts.Free()),

                       // Served, so that a station refused SFTP is refused
                       // for being a station, and not because nobody gets it.
                       new JProperty("sftp",              true)
                   ))
               );

        /// <summary>
        /// A controller in the given directory, built and not started: with
        /// the given configuration written first, or with what is there.
        /// </summary>
        private Controller NewController(String    Directory,
                                         JObject?  Configuration,
                                         IPPort?   HTTPPort  = null)
        {

            System.IO.Directory.CreateDirectory(Directory);

            var configFile = Path.Combine(Directory, "configuration.json");

            if (Configuration is not null)
                File.WriteAllText(configFile, Configuration.ToString());

            controllerHTTPPort = HTTPPort ?? IPPort.Parse(TestPorts.Free());

            var localController = new Controller(
                                      DNSClient:       Resolver(),
                                      HTTPPort:        controllerHTTPPort,
                                      AccountsPath:    Path.Combine(Directory, "accounts"),
                                      ConfigFile:      new WWCPConfigFile(configFile),
                                      LogToConsole:    false,
                                      BridgeDebugLog:  false
                                  );

            Assert.That(localController.Node.Id.ToString(), Is.EqualTo(ControllerId));

            Assert.That(localController.CSMSLogin.TrySetPassword(ControllerId, UplinkPassword, out var error), Is.True, error);

            return localController;

        }

        /// <summary>
        /// The controller answers what its stations send to their CSMS: its
        /// OCPP node takes "CSMS" for one of its own names - its own handler
        /// answers a BootNotification, Accepted, every five minutes - and a
        /// heartbeat is answered with the time.
        /// </summary>
        private static void AnswersForTheCSMS(Controller Controller)
        {

            Controller.Node.OCPP.IN.AnycastIds.Add(NetworkingNode_Id.CSMS);

            Controller.Node.OCPP.IN.OnHeartbeat += (timestamp, sender, connection, request, cancellationToken) =>

                Task.FromResult(
                    new protocols.OCPPv2_1.CSMS.HeartbeatResponse(
                        Request:      request,
                        CurrentTime:  DateTimeOffset.UtcNow
                    )
                );

        }

        private Controller LC
            => controller ?? throw new InvalidOperationException("No controller is running.");

        private UInt16 SSHPort
            => LC.SSHPort!.Value.ToUInt16();

        /// <summary>
        /// The controller's host key as a station pins it: "SHA256:...".
        /// </summary>
        private String ControllersHostKey
            => LC.SSHHostKey!.Split(' ', 2)[1];

        /// <summary>
        /// Give the controller the key of a station.
        /// </summary>
        private void TheControllerKnows(String Login, ISshHostKey Key)
        {
            Assert.That(LC.TryAddStationSSHKey(Login, LineOf(Key), "test", null, null, "tests", out _, out var error, out _),
                        Is.True, error);
        }

        private Boolean IsConnectedOverSSH(String Login)
            => LC.StationSSHServer!.ConnectedNetworkingNodeIds.Any(id => id.ToString() == Login);

        private Boolean HasAnSSHSessionOf(String Login)
            => LC.SSHSessions.Any(session => session.Account == Login);

        #endregion

        #region (private) The stations

        /// <summary>
        /// A name resolver with its servers written down, rather than one that
        /// enumerates every network interface of the machine - see the
        /// station's own TestStations.Resolver for why that matters. Nothing
        /// here resolves a name: everything is dialled at 127.0.0.1.
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
        /// A charging station, built and not started, in a directory of its
        /// own - or in the given one, as a station made again from there.
        /// </summary>
        private Station NewStation(String? Directory = null)
        {

            var directory   = Directory ?? TestParties.TemporaryDirectory("cs-ssh");

            if (Directory is null)
                directories.Add(directory);

            System.IO.Directory.CreateDirectory(directory);

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
        /// The station started - and where a port of its was taken before it
        /// could bind it, made again from its directory on fresh ports, which
        /// reads its connections and its key back.
        /// </summary>
        private async Task<Station> Started(Station Station)
        {

            var directory  = Path.GetDirectoryName(Station.ConfigFile.Path)!;
            var attempt    = 0;

            return await TestPorts.StartedOnFreshPorts(() => attempt++ == 0 ? Station : NewStation(directory));

        }

        /// <summary>
        /// A station that dials the controller at the given URL, its key given
        /// to the controller under the given login where it is to be known,
        /// started.
        /// </summary>
        private async Task<(Station Station, String Connection)> AStationDialling(String   Login,
                                                                                  String?  Path        = null,
                                                                                  String?  HostKey     = null,
                                                                                  Boolean  KeyIsKnown  = true)
        {

            var station = NewStation();
            var key     = await station.SshKey.MakeIfNeeded();

            if (KeyIsKnown)
                TheControllerKnows(Login, key);

            Assert.That(station.Connections.TryAddConnection("The local controller over SSH",
                                                             $"ssh://{Login}@127.0.0.1:{SSHPort}{Path}",
                                                             "localcontroller",
                                                             true,
                                                             null,
                                                             null,
                                                             out var id,
                                                             out var error,
                                                             SshHostKey: HostKey),
                        Is.True, error);

            return (await Started(station), id!);

        }

        private static ConnectionStatus? StatusOf(Station Station, String Connection)
            => Station.ConnectionStates.TryGetValue(Connection, out var state) ? state.Status : null;

        private static String RegistrationOf(Station Station)
            => Station.RegistrationJSON()["2.1"]?["state"]?.Value<String>() ?? "";

        /// <summary>
        /// The station's OCPP 2.1 node, which the station keeps to itself: for
        /// what a test cannot wait for the station to do by itself, a heartbeat
        /// every five minutes as the controller asks.
        /// </summary>
        private static TestChargingStationNode NodeOf(Station Station)

            => (TestChargingStationNode) typeof(Station).GetProperty("OCPPv2_1Node", BindingFlags.Instance | BindingFlags.NonPublic)!.
                                                         GetValue(Station)!;

        /// <summary>
        /// A station connected over SSH, registered by the controller, and
        /// accepted by it.
        /// </summary>
        private async Task<(Station Station, String Connection)> AStationConnected(String Login)
        {

            var (station, connection) = await AStationDialling(Login);

            await Until(() => StatusOf(station, connection) == ConnectionStatus.Connected, $"'{Login}' connecting over SSH", station);
            await Until(() => IsConnectedOverSSH(Login),                                  $"The controller registering '{Login}'", station);
            await Until(() => RegistrationOf(station) == "Accepted",                       $"The controller accepting '{Login}'", station);

            return (station, connection);

        }

        #endregion

        #region (private) Plain SSH clients, standing in for a station

        private static String LineOf(ISshHostKey Key)
            => SshPublicKey.FromHostKey(Key).ToAuthorizedKeyLine();

        private SshClientOptions SSHAs(String Login, ISshHostKey Key)

            => new () {
                   Username       = Login,
                   VerifyHostKey  = blob => LC.SSHHostKey == $"ssh-ed25519 {SshFingerprint.Sha256(blob)}",
                   Credentials    = [ Key ]
               };

        private async Task<SshClient> SignedInAs(String Login, ISshHostKey Key)

            => await SshClient.ConnectAsync("127.0.0.1", SSHPort, SSHAs(Login, Key)).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        /// <summary>
        /// Whether the server refused the given subsystem.
        /// </summary>
        private static async Task<Boolean> RefusesTheSubsystem(SshClient Client, String Name)
        {
            try
            {
                var channel = await Client.OpenSubsystemAsync(Name).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                await channel.CloseAsync();
                return false;
            }
            catch (SshWireException)
            {
                return true;
            }
        }

        private static Byte[] SshString(String Text)
        {
            var bytes = Encoding.UTF8.GetBytes(Text);
            var wire  = new Byte[4 + bytes.Length];
            BinaryPrimitives.WriteUInt32BigEndian(wire, (UInt32) bytes.Length);
            bytes.CopyTo(wire, 4);
            return wire;
        }

        private static Byte[] SshUInt32(UInt32 Value)
        {
            var wire = new Byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(wire, Value);
            return wire;
        }

        #endregion

        #region (private) Until(Condition, What, Station = null)

        /// <summary>
        /// Wait for something to become true, or fail saying what did not
        /// happen, and what the controller and the station said meanwhile.
        /// </summary>
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

        /// <summary>
        /// What the controller and a station said, for a test that failed.
        /// </summary>
        private String Story(Station? Station = null)

            => String.Join(
                   Environment.NewLine,
                   new[] { "", "What was said:" }.
                       Concat(csms?.      Log.Recent(100).Select(entry => $"  CSMS {entry.Timestamp:HH:mm:ss.fff} [{entry.LevelName}] {entry.Message}") ?? []).
                       Concat(controller?.Log.Recent(100).Select(entry => $"  LC  {entry.Timestamp:HH:mm:ss.fff} [{entry.LevelName}] {entry.Message}") ?? []).
                       Concat(Station?.   Log.Recent(100).Select(entry => $"  CS  {entry.Timestamp:HH:mm:ss.fff} [{entry.LevelName}] {entry.Message}") ?? []).
                       Concat(Station?.DialledConnections.Select(said => $"  CS  dialled {said.Key}: {said.Value}") ?? [])
               );

        #endregion


        // What works

        #region AStationBootsOverSSH_AndItsHeartbeatIsAnswered()

        /// <summary>
        /// The station dials "ssh://cs001@...", signs in with its own key, and
        /// registers: the controller takes its BootNotification over an SSH
        /// connection, signed in as cs001, speaking OCPP 2.1, and accepts it.
        /// A heartbeat afterwards is answered over the same connection.
        /// </summary>
        [Test]
        public async Task AStationBootsOverSSH_AndItsHeartbeatIsAnswered()
        {

            var boots       = new ConcurrentQueue<IMessageConnection?>();
            var heartbeats  = new ConcurrentQueue<IMessageConnection?>();

            // Every request as it arrives at the controller, before it is
            // forwarded to the CSMS.
            LC.Node.OCPP.IN.OnJSONRequestMessageReceived += (timestamp, sender, connection, request, cancellationToken) => {

                if (request.Action == "BootNotification")
                    boots.     Enqueue(connection);

                if (request.Action == "Heartbeat")
                    heartbeats.Enqueue(connection);

                return Task.CompletedTask;

            };

            var (station, _) = await AStationConnected("cs001");

            Assert.That(boots, Is.Not.Empty, "The station was accepted, and the controller saw no BootNotification.");

            var bootCameOver = boots.First() as SshMessageConnection;

            Assert.Multiple(() => {
                Assert.That(bootCameOver,                Is.Not.Null,                      "The BootNotification did not come over SSH.");
                Assert.That(bootCameOver?.Transport,     Is.EqualTo(MessageTransport.SSH));
                Assert.That(bootCameOver?.Username,      Is.EqualTo("cs001"),              "The BootNotification came over another login than the station's.");
                Assert.That(bootCameOver?.Subprotocol,   Is.EqualTo("ocpp2.1"));
            });

            var heartbeat = await NodeOf(station).SendHeartbeat();

            Assert.Multiple(() => {
                Assert.That(heartbeat.Result.ResultCode,               Is.EqualTo(ResultCode.OK), $"{heartbeat.Result}{Story(station)}");
                Assert.That(heartbeats.LastOrDefault(),                Is.SameAs(bootCameOver),   "The heartbeat did not come over the station's SSH connection.");
                Assert.That((heartbeat.CurrentTime - DateTimeOffset.UtcNow).Duration(), Is.LessThan(TimeSpan.FromMinutes(1)));
            });

        }

        #endregion

        #region TheControllerAsksTheStation_OverItsSSHConnection()

        /// <summary>
        /// A request of the controller reaches the station over its SSH
        /// connection and is answered: asked to send its BootNotification
        /// again, the station says yes and does.
        /// </summary>
        [Test]
        public async Task TheControllerAsksTheStation_OverItsSSHConnection()
        {

            var boots = new ConcurrentQueue<String>();

            LC.Node.OCPP.IN.OnJSONRequestMessageReceived += (timestamp, sender, connection, request, cancellationToken) => {
                if (connection is SshMessageConnection && request.Action == "BootNotification")
                    boots.Enqueue(request.Payload["reason"]?.Value<String>() ?? "");
                return Task.CompletedTask;
            };

            var (station, _) = await AStationConnected("cs001");

            var answer = await LC.Node.TriggerMessage(
                                   Destination:       SourceRouting.To(NetworkingNode_Id.Parse("cs001")),
                                   RequestedMessage:  MessageTrigger.BootNotification
                               );

            Assert.That(answer.Result.ResultCode,  Is.EqualTo(ResultCode.OK), $"{answer.Result}{Story(station)}");
            Assert.That(answer.Status,             Is.EqualTo(TriggerMessageStatus.Accepted));

            await Until(() => boots.Contains("Triggered"), "The BootNotification the controller asked for", station);

        }

        #endregion

        #region BinaryMessages_GoBothWays_AsBinaryFrames()

        /// <summary>
        /// Binary data transfers, from the station to the controller and from
        /// the controller to the station, each answered - and each carried as
        /// a BINARY frame of the SSH message protocol, not as text.
        /// </summary>
        [Test]
        public async Task BinaryMessages_GoBothWays_AsBinaryFrames()
        {

            var binaryAtTheController = 0;

            LC.StationSSHServer!.OnBinaryMessageReceived += (timestamp, transport, connection, messageTimestamp, eventTrackingId, sourceNodeId, binaryMessage, cancellationToken) => {
                Interlocked.Increment(ref binaryAtTheController);
                return Task.CompletedTask;
            };

            var (station, _) = await AStationConnected("cs001");

            var data       = Encoding.UTF8.GetBytes("Hello over SSH!");
            var reversed   = data.AsEnumerable().Reverse().ToArray();

            var upwards    = await NodeOf(station).TransferBinaryData(
                                       VendorId:             Vendor_Id. GraphDefined,
                                       MessageId:            Message_Id.GraphDefined_TestMessage,
                                       Data:                 data,
                                       SerializationFormat:  SerializationFormats.BinaryTextIds
                                   );

            Assert.Multiple(() => {
                Assert.That(upwards.Result.ResultCode,  Is.EqualTo(ResultCode.OK), $"{upwards.Result}{Story(station)}");
                Assert.That(upwards.Status,             Is.EqualTo(BinaryDataTransferStatus.Accepted));
                Assert.That(upwards.Data,               Is.EqualTo(reversed));
                Assert.That(binaryAtTheController,      Is.GreaterThan(0), "The controller got no BINARY frame.");
            });

            var downwards  = await LC.Node.TransferBinaryData(
                                       Destination:          SourceRouting.To(NetworkingNode_Id.Parse("cs001")),
                                       VendorId:             Vendor_Id. GraphDefined,
                                       MessageId:            Message_Id.GraphDefined_TestMessage,
                                       Data:                 data
                                   );

            Assert.Multiple(() => {
                Assert.That(downwards.Result.ResultCode,  Is.EqualTo(ResultCode.OK), $"{downwards.Result}{Story(station)}");
                Assert.That(downwards.Status,             Is.EqualTo(BinaryDataTransferStatus.Accepted));
                Assert.That(downwards.Data,               Is.EqualTo(reversed));
            });

        }

        #endregion

        #region AStation_ComesBack_AfterTheControllerRestarts()

        /// <summary>
        /// The controller stops and starts again - from its directory, with
        /// the same ports, the same station keys and the same host key - and
        /// the station comes back by itself, believes the host key it pinned at
        /// first, and is reached again.
        /// </summary>
        [Test]
        public async Task AStation_ComesBack_AfterTheControllerRestarts()
        {

            var (station, connection) = await AStationConnected("cs001");

            var pinned = station.Connections.Connections.Single().SshHostKey;

            Assert.That(pinned, Is.EqualTo(ControllersHostKey), "The station did not pin the controller's host key.");

            await LC.DisposeAsync();
            controller = null;

            await Until(() => StatusOf(station, connection) != ConnectionStatus.Connected, "The station noticing the controller is gone", station);

            controller = NewController(controllerDirectory, null, controllerHTTPPort);
            AnswersForTheCSMS(controller);
            await controller.Start();

            await Until(() => StatusOf(station, connection) == ConnectionStatus.Connected, "The station coming back", station);
            await Until(() => IsConnectedOverSSH("cs001"),                                "The new controller registering the station", station);

            Assert.That(station.Connections.Connections.Single().SshHostKey, Is.EqualTo(pinned), "The pin changed across the restart.");

            var answer = await LC.Node.TriggerMessage(
                                   Destination:       SourceRouting.To(NetworkingNode_Id.Parse("cs001")),
                                   RequestedMessage:  MessageTrigger.BootNotification
                               );

            Assert.That(answer.Status, Is.EqualTo(TriggerMessageStatus.Accepted), $"{answer.Result}{Story(station)}");

        }

        #endregion

        #region AWebSocketStation_AndAnSSHStation_AreOnOneControllerAtOnce()

        /// <summary>
        /// One station over a WebSocket with a password, another over SSH with
        /// its key, on the same controller at the same time: both are accepted,
        /// each is registered by the server it came in on, and each is reached.
        /// </summary>
        [Test]
        public async Task AWebSocketStation_AndAnSSHStation_AreOnOneControllerAtOnce()
        {

            const String password = "a-password-long-enough-for-ocpp";

            Assert.That(LC.StationLogins.TrySetPassword("cs-ws", password, null, "tests", out _, out var error), Is.True, error);

            var webSocketStation = NewStation();

            Assert.That(webSocketStation.Connections.TryAddAuthentication("The local controller", "basic", "cs-ws", password, out var authentication, out error),
                        Is.True, error);

            Assert.That(webSocketStation.Connections.TryAddConnection("The local controller",
                                                                      $"ws://127.0.0.1:{LC.OCPPServerSettings.TCPPort}",
                                                                      "localcontroller",
                                                                      true,
                                                                      authentication,
                                                                      null,
                                                                      out var webSocketConnection,
                                                                      out error),
                        Is.True, error);

            webSocketStation = await Started(webSocketStation);

            var (sshStation, sshConnection) = await AStationConnected("cs-ssh");

            await Until(() => StatusOf(webSocketStation, webSocketConnection!) == ConnectionStatus.Connected, "The WebSocket station connecting", webSocketStation);
            await Until(() => RegistrationOf(webSocketStation) == "Accepted",                                 "The WebSocket station being accepted", webSocketStation);

            Assert.Multiple(() => {
                Assert.That(StatusOf(sshStation, sshConnection),                                                Is.EqualTo(ConnectionStatus.Connected), "The SSH station dropped when the WebSocket one came.");
                Assert.That(IsConnectedOverSSH("cs-ssh"),                                                       Is.True);
                Assert.That(IsConnectedOverSSH("cs-ws"),                                                        Is.False, "The WebSocket station is registered over SSH.");
                Assert.That(LC.StationServer!.ConnectedNetworkingNodeIds.Any(id => id.ToString() == "cs-ws"),  Is.True,  "The WebSocket station is not registered by the WebSocket server.");
                Assert.That(LC.StationServer!.ConnectedNetworkingNodeIds.Any(id => id.ToString() == "cs-ssh"), Is.False, "The SSH station is registered by the WebSocket server.");
            });

            foreach (var login in new[] { "cs-ws", "cs-ssh" })
            {

                var answer = await LC.Node.TriggerMessage(
                                       Destination:       SourceRouting.To(NetworkingNode_Id.Parse(login)),
                                       RequestedMessage:  MessageTrigger.BootNotification
                                   );

                Assert.That(answer.Status, Is.EqualTo(TriggerMessageStatus.Accepted), $"'{login}' was not reached: {answer.Result}{Story()}");

            }

        }

        #endregion


        #region AStationsRequest_ThroughTheController_IsAnsweredByTheCSMS()

        /// <summary>
        /// The whole chain as a site has it: a controller that reports to a
        /// CSMS answers nothing of its stations' own, and forwards their
        /// BootNotification to the CSMS - whose answer has to find its way
        /// back down to the station, which is then accepted.
        /// </summary>
        /// <remarks>
        /// Ignored until the finding is fixed: the BootNotification reaches
        /// the CSMS and the CSMS answers it, and the answer is lost in the
        /// controller. Over a WebSocket station the same.
        /// </remarks>
        [Test]
        [Ignore("WWCP_OCPP finding (2026-11-08): in Standard networking mode the answer to a request a controller forwarded up " +
                "comes back over its client connection, and OCPPWebSocketAdapterIN addresses it to the controller itself - only " +
                "a server connection asks FORWARD.GetForwardedNodeId - so it never reaches the station, over SSH as over a WebSocket.")]
        public async Task AStationsRequest_ThroughTheController_IsAnsweredByTheCSMS()
        {

            csmsDirectory = TestParties.TemporaryDirectory("csms-ssh");
            directories.Add(csmsDirectory);

            csms = await TestPorts.StartedOnFreshPorts(() => NewCSMS(csmsDirectory));

            // A controller of its own for this test: one that reports to the
            // CSMS, and answers for nobody.
            await LC.DisposeAsync();
            controller = null;

            controllerDirectory = TestParties.TemporaryDirectory("lc-ssh-uplink");
            directories.Add(controllerDirectory);

            controller = await TestPorts.StartedOnFreshPorts(() => NewController(controllerDirectory, ControllerConfiguration(CSMS)));

            await Until(() => LC.CSMSConnected, "The controller connecting to its CSMS");

            var atTheCSMS = new ConcurrentQueue<String>();

            CSMS.Node.OCPP.IN.OnJSONRequestMessageReceived += (timestamp, sender, connection, request, cancellationToken) => {
                atTheCSMS.Enqueue(request.Action);
                return Task.CompletedTask;
            };

            var (station, _) = await AStationDialling("cs001");

            await Until(() => atTheCSMS.Contains("BootNotification"),  "The station's BootNotification reaching the CSMS", station);
            await Until(() => RegistrationOf(station) == "Accepted",   "The CSMS's answer reaching the station",           station);

        }

        #endregion


        // What does not

        #region AControllerShowingAnotherHostKeyThanThePinnedOne_IsNotBelieved()

        /// <summary>
        /// A station that pinned another host key than the controller's does
        /// not believe it: it does not sign in, is not registered, gives up, and
        /// keeps its pin.
        /// </summary>
        [Test]
        public async Task AControllerShowingAnotherHostKeyThanThePinnedOne_IsNotBelieved()
        {

            var someoneElse = SshFingerprint.Sha256(SshHostKey.GenerateEd25519().PublicKeyBlob);

            var (station, connection) = await AStationDialling("cs001", HostKey: someoneElse);

            await Until(() => StatusOf(station, connection) == ConnectionStatus.Refused, "Refusing the controller", station);

            Assert.Multiple(() => {
                Assert.That(station.DialledConnections[connection],                Does.Contain("was not believed").And.Contain(ControllersHostKey).And.Contain(someoneElse));
                Assert.That(HasAnSSHSessionOf("cs001"),                           Is.False, "The station signed in to a controller it did not believe.");
                Assert.That(IsConnectedOverSSH("cs001"),                          Is.False);
                Assert.That(station.Connections.Connections.Single().SshHostKey,  Is.EqualTo(someoneElse), "The pin was changed by a controller that showed another key.");
            });

        }

        #endregion

        #region HostKey_NonePinned_TheFirstOneShownIsBelievedAndPinned_ImplicitTOFU()

        /// <summary>
        /// Today's first contact: a station with no host key written down
        /// believes the first one it is shown, and writes it down - trust on
        /// first use without being asked for it, as OpenSSH's
        /// StrictHostKeyChecking=accept-new (ChargingStation 256d77f,
        /// SshHostKeyTrust.Verify and PinIfFirst).
        /// </summary>
        /// <remarks>
        /// The plan (5.6.1) asked for TOFU only where a connection says "tofu",
        /// and whether this stays is not decided. Should it go, this is the
        /// test to turn round: a station with nothing pinned is then refused,
        /// says why, and pins nothing.
        /// </remarks>
        [Test]
        public async Task HostKey_NonePinned_TheFirstOneShownIsBelievedAndPinned_ImplicitTOFU()
        {

            var (station, connection) = await AStationDialling("cs001", HostKey: null);

            await Until(() => StatusOf(station, connection) == ConnectionStatus.Connected, "Connecting with no host key pinned", station);

            Assert.That(station.Connections.Connections.Single().SshHostKey, Is.EqualTo(ControllersHostKey),
                        "The host key shown on the first connection was not written down.");

        }

        #endregion

        #region AStationWhoseKeyTheControllerDoesNotKnow_IsTurnedAway()

        /// <summary>
        /// A station whose key the controller was never given - under its own
        /// login, with another station's key known there - is turned away at
        /// the door, says so, and does not try again.
        /// </summary>
        [Test]
        public async Task AStationWhoseKeyTheControllerDoesNotKnow_IsTurnedAway()
        {

            // cs001 is known, with a key that is not the station's.
            TheControllerKnows("cs001", SshHostKey.GenerateEd25519());

            var (station, connection) = await AStationDialling("cs001", KeyIsKnown: false);

            await Until(() => StatusOf(station, connection) == ConnectionStatus.Refused, "Being turned away", station);

            Assert.Multiple(() => {
                Assert.That(station.DialledConnections[connection],  Does.Contain("did not let this station in").And.Contain("not tried again"));
                Assert.That(HasAnSSHSessionOf("cs001"),             Is.False, "A key the controller does not know signed in.");
                Assert.That(IsConnectedOverSSH("cs001"),            Is.False);
            });

        }

        #endregion

        #region AHelloNamingAnotherStationThanTheLogin_IsRefused()

        /// <summary>
        /// A station signed in as cs001 whose HELLO says it is cs999 - the
        /// path of "ssh://cs001@.../cs999" - is refused: the login is the
        /// identity, and nobody allowed cs001 to act for cs999. Neither is
        /// registered.
        /// </summary>
        [Test]
        public async Task AHelloNamingAnotherStationThanTheLogin_IsRefused()
        {

            var (station, connection) = await AStationDialling("cs001", Path: "/cs999");

            await Until(() => StatusOf(station, connection) == ConnectionStatus.Refused, "Being refused", station);

            Assert.Multiple(() => {
                Assert.That(station.DialledConnections[connection],  Does.Contain("refused it the OCPP subsystem"));
                Assert.That(IsConnectedOverSSH("cs001"),            Is.False, "The login was registered.");
                Assert.That(IsConnectedOverSSH("cs999"),            Is.False, "The station named in the HELLO was registered.");
            });

        }

        #endregion

        #region NoHelloInTime_EndsTheChannel()

        /// <summary>
        /// A station that opens the subsystem and then says nothing is not
        /// waited for: the controller closes the channel once the HELLO is due
        /// (Hermod's 10 s), and never registers it.
        /// </summary>
        [Test]
        public async Task NoHelloInTime_EndsTheChannel()
        {

            var key = SshHostKey.GenerateEd25519();

            TheControllerKnows("cs-quiet", key);

            await using var client  = await SignedInAs("cs-quiet", key);

            var channel             = await client.OpenSubsystemAsync(Subsystem).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            var opened              = DateTimeOffset.UtcNow;

            Assert.That(await Task.WhenAny(channel.Closed, Task.Delay(TimeSpan.FromSeconds(3))), Is.Not.SameAs(channel.Closed),
                        "The channel was closed at once, not when the HELLO was due.");

            Assert.That(await Task.WhenAny(channel.Closed, Task.Delay(TimeSpan.FromSeconds(20))), Is.SameAs(channel.Closed),
                        $"A channel without a HELLO was still open after {(DateTimeOffset.UtcNow - opened).TotalSeconds:F0} s.{Story()}");

            Assert.That(IsConnectedOverSSH("cs-quiet"), Is.False);

        }

        #endregion

        #region AStationLogin_GetsNoShell_NoCommand_NoSFTP_NoForwarding()

        /// <summary>
        /// Signed in with a station's key, nothing but OCPP: no command line,
        /// no command, no SFTP, and no port forwarding in either direction.
        /// </summary>
        [Test]
        public async Task AStationLogin_GetsNoShell_NoCommand_NoSFTP_NoForwarding()
        {

            var key = SshHostKey.GenerateEd25519();

            TheControllerKnows("cs001", key);

            await using var client = await SignedInAs("cs001", key);

            async Task<Boolean> Granted(String Type, Byte[] Payload)
            {
                var channel = await client.Multiplexer.OpenChannelAsync("session").AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                try
                {
                    return await channel.SendRequestAsync(Type, true, Payload).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                }
                finally
                {
                    await channel.CloseAsync();
                }
            }

            // Out of the controller towards its own web interface, and the
            // other way round: a port on the controller for the station.
            var outwards = true;

            try
            {
                var channel = await client.Multiplexer.OpenChannelAsync(
                                        "direct-tcpip",
                                        [ .. SshString("127.0.0.1"), .. SshUInt32(controllerHTTPPort.ToUInt16()),
                                          .. SshString("127.0.0.1"), .. SshUInt32(50000) ]
                                    ).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                await channel.CloseAsync();
            }
            catch (Exception e) when (e is not TimeoutException)
            {
                outwards = false;
            }

            var inwards = await client.Multiplexer.SendGlobalRequestAsync("tcpip-forward", true,
                                                                          [ .. SshString("127.0.0.1"), .. SshUInt32(0) ]).
                                                   AsTask().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Multiple(async () => {
                Assert.That(await Granted("shell", []),                  Is.False, "A station got the command line.");
                Assert.That(await Granted("exec",  SshString("who")),    Is.False, "A station got a command.");
                Assert.That(await RefusesTheSubsystem(client, "sftp"),   Is.True,  "A station got SFTP.");
                Assert.That(outwards,                                    Is.False, "A station got a forwarded connection out of the controller.");
                Assert.That(inwards,                                     Is.False, "A station got a port forwarded to it.");
                Assert.That(await RefusesTheSubsystem(client, Subsystem), Is.False, "A station did not get OCPP.");
            });

        }

        #endregion

        #region AnAccountWithoutTheRight_GetsNoOCPP()

        /// <summary>
        /// An account of the controller - its administrator, even - signed in
        /// with its own key, is not given the OCPP subsystem: that takes a
        /// permission no account has by default.
        /// </summary>
        [Test]
        public async Task AnAccountWithoutTheRight_GetsNoOCPP()
        {

            var key    = SshHostKey.GenerateEd25519();
            var added  = await LC.AuthorizeSSHKey(Controller.DefaultAdminUser, LineOf(key));

            Assert.That(added.IsAdded, Is.True, added.Reason);

            await using var client = await SignedInAs(Controller.DefaultAdminUser, key);

            var shell   = await client.Multiplexer.OpenChannelAsync("session").AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            var console = await shell.SendRequestAsync("shell", true, []).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

            await shell.CloseAsync();

            Assert.That(console,                                      Is.True, "The account was not signed in for what it may do: its command line.");
            Assert.That(await RefusesTheSubsystem(client, Subsystem), Is.True, "An account got OCPP over SSH.");

        }

        #endregion

        #region AnAccessRule_LocksAStationOut()

        /// <summary>
        /// A rule that denies a station the OCPP subsystem ends the session it
        /// has, at once, and lets it back in no more; another station is not
        /// touched by it.
        /// </summary>
        /// <remarks>
        /// Whether the station goes on trying is the station's to decide, and
        /// not asserted: today it does, with its back-off, because a subsystem
        /// refused on the SSH channel is no answer it takes as final - only a
        /// login not taken and a REFUSED after the HELLO are.
        /// </remarks>
        [Test]
        public async Task AnAccessRule_LocksAStationOut()
        {

            // Longer than the test, so that only the rule's own change can do it.
            LC.SSHSessionRecheckEvery = TimeSpan.FromMinutes(10);

            var (cs001, connection001) = await AStationConnected("cs001");
            var (cs002, connection002) = await AStationConnected("cs002");

            var change = await LC.TryUpdateSSHConfiguration(new JObject(
                                   new JProperty("accessRules", new JArray(
                                       new JObject(
                                           new JProperty("action",     "deny"),
                                           new JProperty("principal",  "station:cs001"),
                                           new JProperty("service",    $"subsystem:{Subsystem}")
                                       )
                                   ))
                               ));

            Assert.That(change.Error, Is.Null, change.Error);

            await Until(() => !IsConnectedOverSSH("cs001") && StatusOf(cs001, connection001) != ConnectionStatus.Connected,
                        "The rule ending cs001's session", cs001);

            // Coming back with its own key: signed in, and refused OCPP.
            await using var again = await SignedInAs("cs001", cs001.SshKey.Key!);

            Assert.Multiple(async () => {
                Assert.That(await RefusesTheSubsystem(again, Subsystem),  Is.True,  "A station a rule denies got OCPP again.");
                Assert.That(IsConnectedOverSSH("cs001"),                  Is.False, "A station a rule denies is connected.");
                Assert.That(StatusOf(cs002, connection002),               Is.EqualTo(ConnectionStatus.Connected), "A rule about cs001 ended cs002's session.");
                Assert.That(IsConnectedOverSSH("cs002"),                  Is.True);
            });

        }

        #endregion

        #region AKeyTakenAwayWhileItsSessionRuns_EndsTheSession()

        /// <summary>
        /// The key of a station connected over SSH is taken away: its session
        /// ends at once, not at the node's next round of checks, and the
        /// station is not let in again.
        /// </summary>
        [Test]
        public async Task AKeyTakenAwayWhileItsSessionRuns_EndsTheSession()
        {

            // Longer than the test, so that only the store's own news can do it.
            LC.SSHSessionRecheckEvery = TimeSpan.FromMinutes(10);

            var (station, connection) = await AStationConnected("cs001");

            var fingerprint = SshFingerprint.Sha256(station.SshKey.Key!.PublicKeyBlob);

            Assert.That(LC.StationLogins.TryRemoveSSHKey("cs001", fingerprint, out var error), Is.True, error);

            await Until(() => !IsConnectedOverSSH("cs001") && !HasAnSSHSessionOf("cs001"), "The session of a key taken away ending", station);
            await Until(() => StatusOf(station, connection) == ConnectionStatus.Refused,    "The station being turned away when it comes back", station);

            Assert.That(station.DialledConnections[connection], Does.Contain("did not let this station in"));

        }

        #endregion

        #region AFrameLongerThanTheLimit_EndsTheConnection_BeforeItsPayload()

        /// <summary>
        /// A TEXT frame whose header says it is longer than the controller
        /// takes ends the connection with CLOSE 1009 - on the header alone,
        /// before any of what it announces is sent, let alone allocated.
        /// </summary>
        [Test]
        public async Task AFrameLongerThanTheLimit_EndsTheConnection_BeforeItsPayload()
        {

            var key = SshHostKey.GenerateEd25519();

            TheControllerKnows("cs-big", key);

            await using var client  = await SignedInAs("cs-big", key);

            var channel             = await client.OpenSubsystemAsync(Subsystem).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            var input               = channel.Input;

            await channel.SendDataAsync(SshMessageFraming.Encode(SshMessageFrameType.Hello, SshMessageHello.Create([ "ocpp2.1" ], null).ToBytes()));

            var welcome             = await SshMessageFraming.ReadAsync(input, SshMessageFraming.DefaultMaxMessageSize).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.That(welcome?.Type, Is.EqualTo(SshMessageFrameType.Welcome));
            Assert.That(SshMessageWelcome.Parse(welcome!.Value.Payload).MaxMessageSize, Is.EqualTo((UInt64) MaxMessageSize),
                        "The controller announced another limit than its file says.");

            await Until(() => IsConnectedOverSSH("cs-big"), "Registering the station");

            // The header of a TEXT frame one byte longer than the limit, and
            // nothing of its payload.
            var header = new Byte[SshMessageFraming.HeaderLength];
            BinaryPrimitives.WriteUInt32BigEndian(header, MaxMessageSize + 2);
            header[4]  = (Byte) SshMessageFrameType.Text;

            await channel.SendDataAsync(header);

            var answer = await SshMessageFraming.ReadAsync(input, SshMessageFraming.DefaultMaxMessageSize).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.That(answer?.Type, Is.EqualTo(SshMessageFrameType.Close), "The controller waited for a payload longer than it takes.");
            Assert.That(SshMessageFraming.DecodeClose(answer!.Value.Payload).Code, Is.EqualTo(MessageCloseCode.MessageTooBig));

            await Until(() => !IsConnectedOverSSH("cs-big"), "The controller forgetting the connection");

        }

        #endregion

    }

}
