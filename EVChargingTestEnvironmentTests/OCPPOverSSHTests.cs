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

using cloud.charging.open.ChargingStation.OCPP;

using WWCPNode    = cloud.charging.open.protocols.WWCP.Node.WWCPNode;
using OCPPv21Node = cloud.charging.open.protocols.OCPPv2_1.NetworkingNode.INetworkingNode;
using Station     = cloud.charging.open.ChargingStation.ChargingStation;

#endregion

namespace cloud.charging.open.TestEnvironment.Tests
{

    /// <summary>
    /// OCPP over SSH from end to end: a real charging station dialling a real
    /// server at "ssh://", signed in with its own key, on the subsystem
    /// "ocpp@open.charging.cloud" of the server's SSH server - a local
    /// controller (<see cref="LocalControllerOverSSHTests"/>) or a CSMS
    /// (<see cref="CSMSOverSSHTests"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// What the plan "OCPP-over-SSH.md" asks of the integration in 5.8, and
    /// what no repository can test on its own: the server's tests dial it
    /// with a plain SSH message client, and the station's tests dial an SSH
    /// server of their own that stands in for a CSMS. Here both ends are the
    /// classes their programs run.
    /// </para>
    /// <para>
    /// Where the station cannot be made to do what a case is about - say
    /// nothing after the subsystem, ask for a shell, send a frame longer than
    /// it would ever send - a plain SSH client stands in for it, signed in as
    /// a station with a key the server was given, against the same real
    /// server.
    /// </para>
    /// <para>
    /// Every test here is asked of both servers, which build OCPP over SSH
    /// from the same parts and decide who is let in each with its own station
    /// logins. What only one of them has is in its own fixture.
    /// </para>
    /// <para>
    /// One server per test, on fresh ports from the node's test kit, with
    /// its time servers switched off: nothing here leaves the machine.
    /// </para>
    /// </remarks>
    public abstract class OCPPOverSSHTests
    {

        #region Data

        /// <summary>
        /// The subsystem the stations speak OCPP on.
        /// </summary>
        protected const String  Subsystem       = OCPPSshServer.OCPPSubsystem;

        /// <summary>
        /// The largest message the server takes over SSH in these tests: the
        /// smallest it can be set to, written down rather than left at the
        /// default, so that a frame just over it is a test of what the file
        /// says and not only of Hermod's default.
        /// </summary>
        protected const UInt32  MaxMessageSize  = 64 * 1024;

        /// <summary>
        /// How long anything in here is waited for.
        /// </summary>
        protected static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

        private   readonly List<Station>            stations     = [];
        protected readonly List<String>             directories  = [];
        private   readonly ConcurrentBag<DNSClient> resolvers    = [];

        #endregion

        #region The server, as each fixture has it

        /// <summary>
        /// What the server is called in what a failed test says:
        /// "the controller", "the CSMS".
        /// </summary>
        protected abstract String   TheServer         { get; }

        /// <summary>
        /// What a station calls a connection to the server:
        /// "localcontroller", "csms".
        /// </summary>
        protected abstract String   ConnectionType    { get; }

        /// <summary>
        /// The server as the node it is: its SSH server, its sessions, its log.
        /// </summary>
        protected abstract WWCPNode       Server            { get; }

        /// <summary>
        /// Its OCPP 2.1 node.
        /// </summary>
        protected abstract OCPPv21Node    OCPPNode          { get; }

        /// <summary>
        /// Its server of OCPP over SSH.
        /// </summary>
        protected abstract OCPPSshServer  StationSSHServer  { get; }

        /// <summary>
        /// The port its station server listens on for WebSockets.
        /// </summary>
        protected abstract UInt16         WebSocketPort     { get; }

        /// <summary>
        /// The stations connected to it over a WebSocket.
        /// </summary>
        protected abstract IEnumerable<NetworkingNode_Id> WebSocketStations { get; }

        /// <summary>
        /// Give the server the key of a station, in the given login group or
        /// in the default one.
        /// </summary>
        protected abstract void GiveTheKey(String Login, ISshHostKey Key, String? Group = null);

        /// <summary>
        /// Take a key of a station away from the server.
        /// </summary>
        protected abstract void TakeAwayTheKey(String Login, String Fingerprint);

        /// <summary>
        /// Give the server the password of a station on its WebSocket port.
        /// </summary>
        protected abstract void GiveThePassword(String Login, String Password);

        /// <summary>
        /// The server asks the station to send its BootNotification again.
        /// </summary>
        protected abstract Task<TriggerMessageResponse> AskForABootNotification(String Login);

        /// <summary>
        /// The server stops.
        /// </summary>
        protected abstract Task TheServerStops();

        /// <summary>
        /// The server starts again from its directory: the same ports, the same
        /// station keys, the same host key.
        /// </summary>
        protected abstract Task TheServerStartsAgain();

        /// <summary>
        /// Every server of the test stops.
        /// </summary>
        protected abstract Task StopTheServers();

        /// <summary>
        /// The servers whose log a failed test tells.
        /// </summary>
        protected abstract IEnumerable<(String Who, WWCPNode? Node)> Speakers { get; }

        #endregion

        #region TearDown

        [TearDown]
        public async Task StopEverything()
        {

            foreach (var station in stations)
                await station.DisposeAsync();

            await StopTheServers();

            foreach (var resolver in resolvers)
                resolver.Dispose();

            foreach (var directory in directories)
                TestParties.Remove(directory);

            stations.   Clear();
            directories.Clear();
            resolvers.  Clear();

        }

        #endregion


        #region (protected) The server, as the stations see it

        protected UInt16 SSHPort
            => Server.SSHPort!.Value.ToUInt16();

        /// <summary>
        /// The server's host key as a station pins it: "SHA256:...".
        /// </summary>
        protected String ServersHostKey
            => Server.SSHHostKey!.Split(' ', 2)[1];

        protected Boolean IsConnectedOverSSH(String Login)
            => StationSSHServer.ConnectedNetworkingNodeIds.Any(id => id.ToString() == Login);

        protected Boolean HasAnSSHSessionOf(String Login)
            => Server.SSHSessions.Any(session => session.Account == Login);

        #endregion

        #region (protected) The stations

        /// <summary>
        /// A name resolver with its servers written down, rather than one that
        /// enumerates every network interface of the machine - see the
        /// station's own TestStations.Resolver for why that matters. Nothing
        /// here resolves a name: everything is dialled at 127.0.0.1.
        /// </summary>
        protected DNSClient Resolver()
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
        protected Station NewStation(String? Directory = null)
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
        protected async Task<Station> Started(Station Station)
        {

            var directory  = Path.GetDirectoryName(Station.ConfigFile.Path)!;
            var attempt    = 0;

            return await TestPorts.StartedOnFreshPorts(() => attempt++ == 0 ? Station : NewStation(directory));

        }

        /// <summary>
        /// A station that dials the server at the given URL, its key given to
        /// the server under the given login - in the given login group - where
        /// it is to be known, started.
        /// </summary>
        protected async Task<(Station Station, String Connection)> AStationDialling(String   Login,
                                                                                    String?  Path        = null,
                                                                                    String?  HostKey     = null,
                                                                                    Boolean  KeyIsKnown  = true,
                                                                                    String?  Group       = null)
        {

            var station = NewStation();
            var key     = await station.SshKey.MakeIfNeeded();

            if (KeyIsKnown)
                GiveTheKey(Login, key, Group);

            Assert.That(station.Connections.TryAddConnection($"OCPP over SSH to {TheServer}",
                                                             $"ssh://{Login}@127.0.0.1:{SSHPort}{Path}",
                                                             ConnectionType,
                                                             true,
                                                             null,
                                                             null,
                                                             out var id,
                                                             out var error,
                                                             SshHostKey: HostKey),
                        Is.True, error);

            return (await Started(station), id!);

        }

        protected static ConnectionStatus? StatusOf(Station Station, String Connection)
            => Station.ConnectionStates.TryGetValue(Connection, out var state) ? state.Status : null;

        protected static String RegistrationOf(Station Station)
            => Station.RegistrationJSON()["2.1"]?["state"]?.Value<String>() ?? "";

        /// <summary>
        /// The station's OCPP 2.1 node, which the station keeps to itself: for
        /// what a test cannot wait for the station to do by itself, a heartbeat
        /// every five minutes as the server asks.
        /// </summary>
        protected static TestChargingStationNode NodeOf(Station Station)

            => (TestChargingStationNode) typeof(Station).GetProperty("OCPPv2_1Node", BindingFlags.Instance | BindingFlags.NonPublic)!.
                                                         GetValue(Station)!;

        /// <summary>
        /// A station connected over SSH, registered by the server, and
        /// accepted by it.
        /// </summary>
        protected async Task<(Station Station, String Connection)> AStationConnected(String   Login,
                                                                                     String?  Group  = null)
        {

            var (station, connection) = await AStationDialling(Login, Group: Group);

            await Until(() => StatusOf(station, connection) == ConnectionStatus.Connected, $"'{Login}' connecting over SSH",        station);
            await Until(() => IsConnectedOverSSH(Login),                                  $"'{Login}' being registered by {TheServer}", station);
            await Until(() => RegistrationOf(station) == "Accepted",                       $"'{Login}' being accepted by {TheServer}",   station);

            return (station, connection);

        }

        /// <summary>
        /// A station that dials the server's WebSocket port with a password,
        /// started.
        /// </summary>
        protected async Task<(Station Station, String Connection)> AWebSocketStation(String Login)
        {

            const String password = "a-password-long-enough-for-ocpp";

            GiveThePassword(Login, password);

            var station = NewStation();

            Assert.That(station.Connections.TryAddAuthentication(TheServer, "basic", Login, password, out var authentication, out var error),
                        Is.True, error);

            Assert.That(station.Connections.TryAddConnection(TheServer,
                                                             $"ws://127.0.0.1:{WebSocketPort}",
                                                             ConnectionType,
                                                             true,
                                                             authentication,
                                                             null,
                                                             out var connection,
                                                             out error),
                        Is.True, error);

            return (await Started(station), connection!);

        }

        #endregion

        #region (protected) Plain SSH clients, standing in for a station

        protected static String LineOf(ISshHostKey Key)
            => SshPublicKey.FromHostKey(Key).ToAuthorizedKeyLine();

        private SshClientOptions SSHAs(String Login, ISshHostKey Key)

            => new () {
                   Username       = Login,
                   VerifyHostKey  = blob => Server.SSHHostKey == $"ssh-ed25519 {SshFingerprint.Sha256(blob)}",
                   Credentials    = [ Key ]
               };

        protected async Task<SshClient> SignedInAs(String Login, ISshHostKey Key)

            => await SshClient.ConnectAsync("127.0.0.1", SSHPort, SSHAs(Login, Key)).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        /// <summary>
        /// Whether the server refused the given subsystem.
        /// </summary>
        protected static async Task<Boolean> RefusesTheSubsystem(SshClient Client, String Name)
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

        #region (protected) Until(Condition, What, Station = null)

        /// <summary>
        /// Wait for something to become true, or fail saying what did not
        /// happen, and what the servers and the station said meanwhile.
        /// </summary>
        protected async Task Until(Func<Boolean> Condition, String What, Station? Station = null)
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
        /// What the servers and a station said, for a test that failed.
        /// </summary>
        protected String Story(Station? Station = null)

            => String.Join(
                   Environment.NewLine,
                   new[] { "", "What was said:" }.
                       Concat(Speakers.SelectMany(speaker => speaker.Node?.Log.Recent(100).Select(entry => $"  {speaker.Who,-4} {entry.Timestamp:HH:mm:ss.fff} [{entry.LevelName}] {entry.Message}") ?? [])).
                       Concat(Station?.Log.Recent(100).Select(entry => $"  CS   {entry.Timestamp:HH:mm:ss.fff} [{entry.LevelName}] {entry.Message}") ?? []).
                       Concat(Station?.DialledConnections.Select(said => $"  CS   dialled {said.Key}: {said.Value}") ?? [])
               );

        #endregion


        // What works

        #region AStationBootsOverSSH_AndItsHeartbeatIsAnswered()

        /// <summary>
        /// The station dials "ssh://cs001@...", signs in with its own key, and
        /// registers: the server takes its BootNotification over an SSH
        /// connection, signed in as cs001, speaking OCPP 2.1, and accepts it.
        /// A heartbeat afterwards is answered over the same connection.
        /// </summary>
        [Test]
        public async Task AStationBootsOverSSH_AndItsHeartbeatIsAnswered()
        {

            var boots       = new ConcurrentQueue<IMessageConnection?>();
            var heartbeats  = new ConcurrentQueue<IMessageConnection?>();

            // Every request as it arrives at the server, before anything
            // answers or forwards it.
            OCPPNode.OCPP.IN.OnJSONRequestMessageReceived += (timestamp, sender, connection, request, cancellationToken) => {

                if (request.Action == "BootNotification")
                    boots.     Enqueue(connection);

                if (request.Action == "Heartbeat")
                    heartbeats.Enqueue(connection);

                return Task.CompletedTask;

            };

            var (station, _) = await AStationConnected("cs001");

            Assert.That(boots, Is.Not.Empty, $"The station was accepted, and {TheServer} saw no BootNotification.");

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

        #region TheServerAsksTheStation_OverItsSSHConnection()

        /// <summary>
        /// A request of the server reaches the station over its SSH connection
        /// and is answered: asked to send its BootNotification again, the
        /// station says yes and does.
        /// </summary>
        [Test]
        public async Task TheServerAsksTheStation_OverItsSSHConnection()
        {

            var boots = new ConcurrentQueue<String>();

            OCPPNode.OCPP.IN.OnJSONRequestMessageReceived += (timestamp, sender, connection, request, cancellationToken) => {
                if (connection is SshMessageConnection && request.Action == "BootNotification")
                    boots.Enqueue(request.Payload["reason"]?.Value<String>() ?? "");
                return Task.CompletedTask;
            };

            var (station, _) = await AStationConnected("cs001");

            var answer = await AskForABootNotification("cs001");

            Assert.That(answer.Result.ResultCode,  Is.EqualTo(ResultCode.OK), $"{answer.Result}{Story(station)}");
            Assert.That(answer.Status,             Is.EqualTo(TriggerMessageStatus.Accepted));

            await Until(() => boots.Contains("Triggered"), $"The BootNotification {TheServer} asked for", station);

        }

        #endregion

        #region BinaryMessages_GoBothWays_AsBinaryFrames()

        /// <summary>
        /// Binary data transfers, from the station to the server and from the
        /// server to the station, each answered - and each carried as a BINARY
        /// frame of the SSH message protocol, not as text.
        /// </summary>
        [Test]
        public async Task BinaryMessages_GoBothWays_AsBinaryFrames()
        {

            var binaryAtTheServer = 0;

            StationSSHServer.OnBinaryMessageReceived += (timestamp, transport, connection, messageTimestamp, eventTrackingId, sourceNodeId, binaryMessage, cancellationToken) => {
                Interlocked.Increment(ref binaryAtTheServer);
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
                Assert.That(binaryAtTheServer,          Is.GreaterThan(0), $"No BINARY frame reached {TheServer}.");
            });

            var downwards  = await OCPPNode.TransferBinaryData(
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

        #region AStation_ComesBack_AfterTheServerRestarts()

        /// <summary>
        /// The server stops and starts again - from its directory, with the
        /// same ports, the same station keys and the same host key - and the
        /// station comes back by itself, believes the host key it pinned at
        /// first, and is reached again.
        /// </summary>
        [Test]
        public async Task AStation_ComesBack_AfterTheServerRestarts()
        {

            var (station, connection) = await AStationConnected("cs001");

            var pinned = station.Connections.Connections.Single().SshHostKey;

            Assert.That(pinned, Is.EqualTo(ServersHostKey), $"The station did not pin the host key of {TheServer}.");

            await TheServerStops();

            await Until(() => StatusOf(station, connection) != ConnectionStatus.Connected, $"The station noticing {TheServer} is gone", station);

            await TheServerStartsAgain();

            await Until(() => StatusOf(station, connection) == ConnectionStatus.Connected, "The station coming back", station);
            await Until(() => IsConnectedOverSSH("cs001"),                                $"The station being registered by {TheServer}, started again", station);

            Assert.That(station.Connections.Connections.Single().SshHostKey, Is.EqualTo(pinned), "The pin changed across the restart.");

            var answer = await AskForABootNotification("cs001");

            Assert.That(answer.Status, Is.EqualTo(TriggerMessageStatus.Accepted), $"{answer.Result}{Story(station)}");

        }

        #endregion

        #region AWebSocketStation_AndAnSSHStation_AreOnOneServerAtOnce()

        /// <summary>
        /// One station over a WebSocket with a password, another over SSH with
        /// its key, on the same server at the same time: both are accepted,
        /// each is registered by the server it came in on, and each is reached.
        /// </summary>
        [Test]
        public async Task AWebSocketStation_AndAnSSHStation_AreOnOneServerAtOnce()
        {

            var (webSocketStation, webSocketConnection) = await AWebSocketStation("cs-ws");
            var (sshStation,       sshConnection)       = await AStationConnected("cs-ssh");

            await Until(() => StatusOf(webSocketStation, webSocketConnection) == ConnectionStatus.Connected, "The WebSocket station connecting",     webSocketStation);
            await Until(() => RegistrationOf(webSocketStation) == "Accepted",                                "The WebSocket station being accepted", webSocketStation);

            Assert.Multiple(() => {
                Assert.That(StatusOf(sshStation, sshConnection),                       Is.EqualTo(ConnectionStatus.Connected), "The SSH station dropped when the WebSocket one came.");
                Assert.That(IsConnectedOverSSH("cs-ssh"),                              Is.True);
                Assert.That(IsConnectedOverSSH("cs-ws"),                               Is.False, "The WebSocket station is registered over SSH.");
                Assert.That(WebSocketStations.Any(id => id.ToString() == "cs-ws"),     Is.True,  "The WebSocket station is not registered by the WebSocket server.");
                Assert.That(WebSocketStations.Any(id => id.ToString() == "cs-ssh"),    Is.False, "The SSH station is registered by the WebSocket server.");
            });

            foreach (var login in new[] { "cs-ws", "cs-ssh" })
            {

                var answer = await AskForABootNotification(login);

                Assert.That(answer.Status, Is.EqualTo(TriggerMessageStatus.Accepted), $"'{login}' was not reached: {answer.Result}{Story()}");

            }

        }

        #endregion


        // What does not

        #region AServerShowingAnotherHostKeyThanThePinnedOne_IsNotBelieved()

        /// <summary>
        /// A station that pinned another host key than the server's does not
        /// believe it: it does not sign in, is not registered, gives up, and
        /// keeps its pin.
        /// </summary>
        [Test]
        public async Task AServerShowingAnotherHostKeyThanThePinnedOne_IsNotBelieved()
        {

            var someoneElse = SshFingerprint.Sha256(SshHostKey.GenerateEd25519().PublicKeyBlob);

            var (station, connection) = await AStationDialling("cs001", HostKey: someoneElse);

            await Until(() => StatusOf(station, connection) == ConnectionStatus.Refused, $"Refusing {TheServer}", station);

            Assert.Multiple(() => {
                Assert.That(station.DialledConnections[connection],                Does.Contain("was not believed").And.Contain(ServersHostKey).And.Contain(someoneElse));
                Assert.That(HasAnSSHSessionOf("cs001"),                           Is.False, $"The station signed in to {TheServer}, which it did not believe.");
                Assert.That(IsConnectedOverSSH("cs001"),                          Is.False);
                Assert.That(station.Connections.Connections.Single().SshHostKey,  Is.EqualTo(someoneElse), "The pin was changed by a server that showed another key.");
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

            Assert.That(station.Connections.Connections.Single().SshHostKey, Is.EqualTo(ServersHostKey),
                        "The host key shown on the first connection was not written down.");

        }

        #endregion

        #region AStationWhoseKeyTheServerDoesNotKnow_IsTurnedAway()

        /// <summary>
        /// A station whose key the server was never given - under its own
        /// login, with another station's key known there - is turned away at
        /// the door, says so, and does not try again.
        /// </summary>
        [Test]
        public async Task AStationWhoseKeyTheServerDoesNotKnow_IsTurnedAway()
        {

            // cs001 is known, with a key that is not the station's.
            GiveTheKey("cs001", SshHostKey.GenerateEd25519());

            var (station, connection) = await AStationDialling("cs001", KeyIsKnown: false);

            await Until(() => StatusOf(station, connection) == ConnectionStatus.Refused, "Being turned away", station);

            Assert.Multiple(() => {
                Assert.That(station.DialledConnections[connection],  Does.Contain("did not let this station in").And.Contain("not tried again"));
                Assert.That(HasAnSSHSessionOf("cs001"),             Is.False, $"A key {TheServer} does not know signed in.");
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
        /// waited for: the server closes the channel once the HELLO is due
        /// (Hermod's 10 s), and never registers it.
        /// </summary>
        [Test]
        public async Task NoHelloInTime_EndsTheChannel()
        {

            var key = SshHostKey.GenerateEd25519();

            GiveTheKey("cs-quiet", key);

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

            GiveTheKey("cs001", key);

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

            // Out of the server towards its own web interface, and the other
            // way round: a port on the server for the station.
            var outwards = true;

            try
            {
                var channel = await client.Multiplexer.OpenChannelAsync(
                                        "direct-tcpip",
                                        [ .. SshString("127.0.0.1"), .. SshUInt32(Server.HTTPPort.ToUInt16()),
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
                Assert.That(outwards,                                    Is.False, $"A station got a forwarded connection out of {TheServer}.");
                Assert.That(inwards,                                     Is.False, "A station got a port forwarded to it.");
                Assert.That(await RefusesTheSubsystem(client, Subsystem), Is.False, "A station did not get OCPP.");
            });

        }

        #endregion

        #region AnAccountWithoutTheRight_GetsNoOCPP()

        /// <summary>
        /// An account of the server - its administrator, even - signed in with
        /// its own key, is not given the OCPP subsystem: that takes a
        /// permission no account has by default.
        /// </summary>
        [Test]
        public async Task AnAccountWithoutTheRight_GetsNoOCPP()
        {

            var key    = SshHostKey.GenerateEd25519();
            var added  = await Server.AuthorizeSSHKey(WWCPNode.DefaultAdminUser, LineOf(key));

            Assert.That(added.IsAdded, Is.True, added.Reason);

            await using var client = await SignedInAs(WWCPNode.DefaultAdminUser, key);

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
            Server.SSHSessionRecheckEvery = TimeSpan.FromMinutes(10);

            var (cs001, connection001) = await AStationConnected("cs001");
            var (cs002, connection002) = await AStationConnected("cs002");

            var change = await Server.TryUpdateSSHConfiguration(new JObject(
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
            Server.SSHSessionRecheckEvery = TimeSpan.FromMinutes(10);

            var (station, connection) = await AStationConnected("cs001");

            TakeAwayTheKey("cs001", SshFingerprint.Sha256(station.SshKey.Key!.PublicKeyBlob));

            await Until(() => !IsConnectedOverSSH("cs001") && !HasAnSSHSessionOf("cs001"), "The session of a key taken away ending", station);
            await Until(() => StatusOf(station, connection) == ConnectionStatus.Refused,    "The station being turned away when it comes back", station);

            Assert.That(station.DialledConnections[connection], Does.Contain("did not let this station in"));

        }

        #endregion

        #region AFrameLongerThanTheLimit_EndsTheConnection_BeforeItsPayload()

        /// <summary>
        /// A TEXT frame whose header says it is longer than the server takes
        /// ends the connection with CLOSE 1009 - on the header alone, before
        /// any of what it announces is sent, let alone allocated.
        /// </summary>
        [Test]
        public async Task AFrameLongerThanTheLimit_EndsTheConnection_BeforeItsPayload()
        {

            var key = SshHostKey.GenerateEd25519();

            GiveTheKey("cs-big", key);

            await using var client  = await SignedInAs("cs-big", key);

            var channel             = await client.OpenSubsystemAsync(Subsystem).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            var input               = channel.Input;

            await channel.SendDataAsync(SshMessageFraming.Encode(SshMessageFrameType.Hello, SshMessageHello.Create([ "ocpp2.1" ], null).ToBytes()));

            var welcome             = await SshMessageFraming.ReadAsync(input, SshMessageFraming.DefaultMaxMessageSize).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.That(welcome?.Type, Is.EqualTo(SshMessageFrameType.Welcome));
            Assert.That(SshMessageWelcome.Parse(welcome!.Value.Payload).MaxMessageSize, Is.EqualTo((UInt64) MaxMessageSize),
                        $"The limit {TheServer} announced is not the one in its file.");

            await Until(() => IsConnectedOverSSH("cs-big"), "Registering the station");

            // The header of a TEXT frame one byte longer than the limit, and
            // nothing of its payload.
            var header = new Byte[SshMessageFraming.HeaderLength];
            BinaryPrimitives.WriteUInt32BigEndian(header, MaxMessageSize + 2);
            header[4]  = (Byte) SshMessageFrameType.Text;

            await channel.SendDataAsync(header);

            var answer = await SshMessageFraming.ReadAsync(input, SshMessageFraming.DefaultMaxMessageSize).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.That(answer?.Type, Is.EqualTo(SshMessageFrameType.Close), $"No CLOSE: {TheServer} waited for a payload longer than it takes.");
            Assert.That(SshMessageFraming.DecodeClose(answer!.Value.Payload).Code, Is.EqualTo(MessageCloseCode.MessageTooBig));

            await Until(() => !IsConnectedOverSSH("cs-big"), $"The connection being forgotten by {TheServer}");

        }

        #endregion

    }

}
