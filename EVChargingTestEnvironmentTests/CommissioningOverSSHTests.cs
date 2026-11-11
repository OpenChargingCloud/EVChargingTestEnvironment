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

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.Mail;
using org.GraphDefined.Vanaheimr.Hermod.SSH;
using org.GraphDefined.Vanaheimr.Hermod.SSH.Client;
using org.GraphDefined.Vanaheimr.Hermod.Messaging;

using cloud.charging.open.protocols.WWCP;
using cloud.charging.open.protocols.WWCP.NetworkingNode;
using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.TestKit;
using cloud.charging.open.protocols.OCPP;
using cloud.charging.open.protocols.OCPPv2_1;
using cloud.charging.open.protocols.OCPPv2_1.CS;
using cloud.charging.open.protocols.OCPPv2_1.CSMS;

using cloud.charging.open.ChargingStation.OCPP;

using Station     = cloud.charging.open.ChargingStation.ChargingStation;
using Management  = cloud.charging.open.CSMS.CSMS;

#endregion

namespace cloud.charging.open.TestEnvironment.Tests
{

    /// <summary>
    /// Commissioning over SSH from end to end (plan 5.6.4): a tool speaking
    /// OCPP 2.1 to a real charging station on the station's own SSH server,
    /// signed in as an account of the station with that account's key - while
    /// the station reports to a real CSMS on its uplink.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The station's own tests have the tool and, where there is one, a CSMS
    /// of the least kind on the uplink. Here the CSMS is the one its program
    /// runs, reached over SSH or over a WebSocket, so that what the station
    /// sends its CSMS, and what the CSMS asks of it, go the way they go at a
    /// site while a tool is connected and after it went. The tool is a plain
    /// SSH message client, which says on the wire what a commissioning tool
    /// would and nothing more.
    /// </para>
    /// <para>
    /// One station and at most one CSMS per test, on fresh ports from the
    /// node's test kit, with their time servers switched off: nothing here
    /// leaves the machine.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class CommissioningOverSSHTests
    {

        #region (class) Tool

        /// <summary>
        /// A commissioning tool, connected: what it was sent, and a way to ask.
        /// </summary>
        private sealed class Tool(SshMessageClient Client)
        {

            private readonly ConcurrentQueue<JArray> received = new ();
            private          Int32                   next;

            public SshMessageClient      Client      { get; } = Client;
            public SshMessageConnection  Connection  { get; private set; } = default!;

            /// <summary>
            /// Every message the station sent the tool.
            /// </summary>
            public JArray[] Received
                => [.. received];

            /// <summary>
            /// Whether every message the tool was sent answers one of its own
            /// requests: no request of the station's, and no answer to anybody
            /// else's.
            /// </summary>
            public Boolean GotOnlyItsOwnAnswers
                => received.All(message => message[0].Value<Int32>() is 3 or 4 &&
                                           (message[1].Value<String>() ?? "").StartsWith("tool-", StringComparison.Ordinal));

            public async Task Connect()
            {

                // Before the connection is there, so that nothing said on it
                // goes past unheard.
                Client.OnTextMessageReceived += (timestamp, sender, connection, text, cancellationToken) => {
                    received.Enqueue(JArray.Parse(text));
                    return Task.CompletedTask;
                };

                Connection = await Client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(15));

            }

            /// <summary>
            /// The answer to a request of the given action - a CALLRESULT or a
            /// CALLERROR - or null where none came within ten seconds.
            /// </summary>
            public async Task<JArray?> Ask(String Action, JObject Payload)
            {

                var id     = $"tool-{Interlocked.Increment(ref next)}";

                await Connection.SendTextMessage(new JArray(2, id, Action, Payload).ToString(Formatting.None));

                var until  = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);

                while (DateTimeOffset.UtcNow < until)
                {

                    if (received.FirstOrDefault(message => message[0].Value<Int32>() is 3 or 4 && message[1].Value<String>() == id) is JArray answer)
                        return answer;

                    await Task.Delay(20);

                }

                return null;

            }

        }

        #endregion

        #region Data

        private const           String    Subsystem     = "ocpp@open.charging.cloud";
        private const           String    Root          = Station.DefaultAdminUser;
        private const           String    Installer     = "installer01";
        private const           String    StationLogin  = "cs001";

        private static readonly TimeSpan  Patience      = TimeSpan.FromSeconds(30);

        private readonly ISshHostKey                  rootKey       = SshHostKey.GenerateEd25519();
        private readonly ISshHostKey                  installerKey  = SshHostKey.GenerateEd25519();

        private readonly List<Station>                stations      = [];
        private readonly List<Tool>                   tools         = [];
        private readonly List<String>                 directories   = [];
        private readonly ConcurrentBag<DNSClient>     resolvers     = [];
        private readonly ConcurrentQueue<String>      atTheCSMS     = new ();

        private Station?     station;
        private Management?  csms;

        #endregion

        #region TearDown

        [TearDown]
        public async Task TakeThemAwayAgain()
        {

            foreach (var tool in tools)
                await tool.Client.DisposeAsync();

            foreach (var station in stations)
                await station.DisposeAsync();

            if (csms is not null)
                await csms.DisposeAsync();

            foreach (var resolver in resolvers)
                resolver.Dispose();

            foreach (var directory in directories)
                TestParties.Remove(directory);

            tools.      Clear();
            stations.   Clear();
            directories.Clear();
            resolvers.  Clear();
            atTheCSMS.  Clear();

            station  = null;
            csms     = null;

        }

        #endregion


        #region (private) The CSMS

        /// <summary>
        /// A name resolver with its servers written down: nothing here
        /// resolves a name, everything is dialled at 127.0.0.1.
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
        /// A CSMS that takes stations over a WebSocket and over SSH, started:
        /// the actions of the requests it takes are kept.
        /// </summary>
        private async Task<Management> ACSMS()
        {

            var directory = TestParties.TemporaryDirectory("csms-commissioning");
            directories.Add(directory);

            csms = await TestPorts.StartedOnFreshPorts(() => {

                       System.IO.Directory.CreateDirectory(directory);

                       var configFile = Path.Combine(directory, "configuration.json");

                       File.WriteAllText(configFile, new JObject(
                           new JProperty("nts",         TestParties.NoTimeServers),
                           new JProperty("ocppServer",  new JObject(
                               new JProperty("enabled",           true),
                               new JProperty("address",           "127.0.0.1"),
                               new JProperty("port",              TestPorts.Free()),
                               new JProperty("securityProfiles",  new JArray(1)),
                               new JProperty("subprotocols",      new JArray("ocpp2.1", "ocpp2.0.1")),
                               new JProperty("ssh",               new JObject(new JProperty("enabled", true)))
                           )),
                           new JProperty("ssh",         new JObject(
                               new JProperty("enabled",           true),
                               new JProperty("port",              TestPorts.Free())
                           ))
                       ).ToString());

                       return new Management(
                                  DNSClient:       Resolver(),
                                  HTTPPort:        IPPort.Parse(TestPorts.Free()),
                                  AccountsPath:    Path.Combine(directory, "accounts"),
                                  ConfigFile:      new WWCPConfigFile(configFile),
                                  LogToConsole:    false,
                                  BridgeDebugLog:  false
                              );

                   });

            csms.Node.OCPP.IN.OnJSONRequestMessageReceived += (timestamp, sender, connection, request, cancellationToken) => {
                atTheCSMS.Enqueue(request.Action);
                return Task.CompletedTask;
            };

            return csms;

        }

        private Management CSMS
            => csms ?? throw new InvalidOperationException("No CSMS is running.");

        /// <summary>
        /// How many requests of the given action the CSMS took.
        /// </summary>
        private Int32 AtTheCSMS(String Action)
            => atTheCSMS.Count(action => action == Action);

        /// <summary>
        /// The CSMS asks the station to send the given message.
        /// </summary>
        private Task<TriggerMessageResponse> TheCSMSAsks(MessageTrigger Message)

            => CSMS.Node.TriggerMessage(
                   Destination:       SourceRouting.To(NetworkingNode_Id.Parse(StationLogin)),
                   RequestedMessage:  Message
               );

        #endregion

        #region (private) The station

        /// <summary>
        /// A charging station in the given directory, built and not started.
        /// </summary>
        private Station NewStation(String Directory)
        {

            var station = new Station(
                              DNSClient:       Resolver(),
                              HTTPPort:        IPPort.Parse(TestPorts.Free()),
                              NoKiosk:         true,
                              AccountsPath:    Path.Combine(Directory, "accounts"),
                              ConfigFile:      new WWCPConfigFile(Path.Combine(Directory, "configuration.json")),
                              LogToConsole:    false,
                              BridgeDebugLog:  false
                          );

            stations.Add(station);

            return station;

        }

        /// <summary>
        /// A station with its SSH server on or off, commissioning switched on,
        /// off, or not mentioned in its file, reporting to the CSMS over the
        /// given uplink where there is one: started, with a key for root and an
        /// installer of its own.
        /// </summary>
        /// <param name="Commissioning">The "commissioning" section's switch, or null for no section.</param>
        /// <param name="SSH">Whether the station's SSH server is on.</param>
        /// <param name="Uplink">"ssh", "ws", or null for no CSMS.</param>
        private async Task<Station> AStation(Boolean?  Commissioning,
                                             Boolean   SSH     = true,
                                             String?   Uplink  = null)
        {

            var directory = TestParties.TemporaryDirectory("cs-commissioning");
            directories.Add(directory);

            Directory.CreateDirectory(directory);

            var configuration = new JObject(
                                    new JProperty("nts",  TestParties.NoTimeServers),
                                    new JProperty("ssh",  new JObject(
                                        new JProperty("enabled",  SSH),
                                        new JProperty("port",     TestPorts.Free())
                                    ))
                                );

            if (Commissioning.HasValue)
                configuration["commissioning"] = new JObject(new JProperty("enabled", Commissioning.Value));

            var configFile = Path.Combine(directory, "configuration.json");

            File.WriteAllText(configFile, configuration.ToString());

            var first = NewStation(directory);

            switch (Uplink)
            {

                case "ssh":
                {

                    var key = await first.SshKey.MakeIfNeeded();

                    Assert.That(CSMS.TryAddStationSSHKey(StationLogin, SshPublicKey.FromHostKey(key).ToAuthorizedKeyLine(), "test", null, null, "tests", out _, out var error, out _),
                                Is.True, error);

                    Assert.That(first.Connections.TryAddConnection("The CSMS over SSH",
                                                                   $"ssh://{StationLogin}@127.0.0.1:{CSMS.SSHPort!.Value.ToUInt16()}",
                                                                   "csms",
                                                                   true,
                                                                   null,
                                                                   null,
                                                                   out _,
                                                                   out error,
                                                                   SshHostKey: CSMS.SSHHostKey!.Split(' ', 2)[1]),
                                Is.True, error);

                    break;

                }

                case "ws":
                {

                    const String password = "a-password-long-enough-for-ocpp";

                    Assert.That(CSMS.StationLogins.TrySetPassword(StationLogin, password, null, "tests", out _, out var error), Is.True, error);

                    Assert.That(first.Connections.TryAddAuthentication("The CSMS", "basic", StationLogin, password, out var authentication, out error),
                                Is.True, error);

                    Assert.That(first.Connections.TryAddConnection("The CSMS over a WebSocket",
                                                                   $"ws://127.0.0.1:{CSMS.OCPPServerSettings.TCPPort!.Value.ToUInt16()}",
                                                                   "csms",
                                                                   true,
                                                                   authentication,
                                                                   null,
                                                                   out _,
                                                                   out error),
                                Is.True, error);

                    break;

                }

            }

            // Made again where a port of its was taken before it could bind
            // it: on fresh ports - the SSH port is the file's - reading its
            // connections and its key back.
            var attempt = 0;

            station = await TestPorts.StartedOnFreshPorts(() => {

                          if (attempt++ == 0)
                              return first;

                          var json = JObject.Parse(File.ReadAllText(configFile));
                          json["ssh"]!["port"] = TestPorts.Free();
                          File.WriteAllText(configFile, json.ToString());

                          return NewStation(directory);

                      });

            var added = await station.AuthorizeSSHKey(Root, LineOf(rootKey));
            Assert.That(added.IsAdded, Is.True, added.Reason);

            await MakeAccount(Installer, "installer", installerKey);

            if (Uplink is not null)
            {
                await Until(() => AtTheCSMS("BootNotification") > 0,                                 "The station's BootNotification reaching the CSMS");
                await Until(() => station.RegistrationJSON()["2.1"]?["state"]?.Value<String>() == "Accepted", "The station being accepted by the CSMS");
            }

            return station;

        }

        private Station TheStation
            => station ?? throw new InvalidOperationException("No station is running.");

        /// <summary>
        /// The name of the station's OCPP 2.1 node, which a tool talks to.
        /// </summary>
        private String StationNodeId
            => TheStation.CommissioningJSON()["station"]!.Value<String>()!;

        /// <summary>
        /// The station's OCPP 2.1 node, which the station keeps to itself: for
        /// a heartbeat now, rather than in five minutes.
        /// </summary>
        private TestChargingStationNode StationNode

            => (TestChargingStationNode) typeof(Station).GetProperty("OCPPv2_1Node", BindingFlags.Instance | BindingFlags.NonPublic)!.
                                                         GetValue(TheStation)!;

        /// <summary>
        /// An account besides root, in the given role, with the given key.
        /// </summary>
        private async Task MakeAccount(String Account, String Role, ISshHostKey Key)
        {

            await TheStation.ExtAPI.AddUser(new User(User_Id.Parse(Account), I18NString.Create(Languages.en, Account), SimpleEMailAddress.Parse($"{Account}@localhost")),
                                            SkipDefaultNotifications: true, SkipNewUserEMail: true, SkipNewUserNotifications: true);

            TheStation.ExtAPI.TryGetUser(User_Id.Parse(Account), out var stored);
            TheStation.ExtAPI.TryGetUserGroup(UserGroup_Id.Parse(Role), out var group);

            Assert.That((await TheStation.ExtAPI.AddUserToUserGroup((User) stored!, User2UserGroupEdgeLabel.IsMember, (UserGroup) group!)).IsSuccess,
                        Is.True, $"'{Account}' joining {Role}");

            var added = await TheStation.AuthorizeSSHKey(Account, LineOf(Key));

            Assert.That(added.IsAdded, Is.True, added.Reason);

        }

        /// <summary>
        /// The station's web interface, as root with the password it made up.
        /// </summary>
        private HttpClient AsRootOverHTTP()
        {

            var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{TheStation.HTTPPort.ToUInt16()}/") };

            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                                                           "Basic",
                                                           Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Root}:{TheStation.GeneratedPassword}"))
                                                       );

            return http;

        }

        /// <summary>
        /// PUT /api/v1/configuration/commissioning, as root.
        /// </summary>
        private async Task<(HttpStatusCode Status, JObject JSON)> SwitchOverHTTP(Boolean Enabled)
        {

            using var http      = AsRootOverHTTP();
            using var request   = new HttpRequestMessage(HttpMethod.Put, "api/v1/configuration/commissioning") {
                                      Content = new StringContent(new JObject(new JProperty("enabled", Enabled)).ToString(), Encoding.UTF8, "application/json")
                                  };
            using var response  = await http.SendAsync(request);
            var text            = await response.Content.ReadAsStringAsync();

            return (response.StatusCode, text.Length > 0 && text.TrimStart().StartsWith('{') ? JObject.Parse(text) : new JObject(new JProperty("text", text)));

        }

        #endregion

        #region (private) Tools, and plain SSH clients

        private static String LineOf(ISshHostKey Key)
            => SshPublicKey.FromHostKey(Key).ToAuthorizedKeyLine();

        private SshClientOptions SSHAs(String Login, ISshHostKey Key)

            => new () {
                   Username       = Login,
                   VerifyHostKey  = blob => TheStation.SSHHostKey == $"ssh-ed25519 {SshFingerprint.Sha256(blob)}",
                   Credentials    = [ Key ]
               };

        /// <summary>
        /// A tool connecting as the installer, or as the given account with its
        /// key, on the given SSH port or the station's, offering the given
        /// subprotocols and saying what its HELLO is given: the tool, or why it
        /// was not let in.
        /// </summary>
        private async Task<(Tool? Tool, String? Why)> Connect(JObject?   Hello         = null,
                                                              String?    Login         = null,
                                                              ISshHostKey? Key         = null,
                                                              String[]?  Subprotocols  = null,
                                                              UInt16?    Port          = null)
        {

            var tool = new Tool(new SshMessageClient(new SshMessageClientOptions {
                                    Host             = "127.0.0.1",
                                    Port             = Port ?? TheStation.SSHPort!.Value.ToUInt16(),
                                    Ssh              = SSHAs(Login ?? Installer, Key ?? installerKey),
                                    Subsystem        = Subsystem,
                                    Subprotocols     = Subprotocols ?? [ "ocpp2.1" ],
                                    HelloProperties  = Hello
                                }));

            tools.Add(tool);

            try
            {
                await tool.Connect();
                return (tool, null);
            }
            catch (TimeoutException)
            {
                throw;
            }
            catch (Exception e)
            {
                return (null, e.Message);
            }

        }

        /// <summary>
        /// Whether the given account's request for the subsystem is turned
        /// down, once it is signed in.
        /// </summary>
        private async Task<Boolean> RefusedTo(String Login, ISshHostKey Key)
        {

            await using var client = await SshClient.ConnectAsync("127.0.0.1", TheStation.SSHPort!.Value.ToUInt16(), SSHAs(Login, Key)).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

            try
            {
                var channel = await client.OpenSubsystemAsync(Subsystem).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                await channel.CloseAsync();
                return false;
            }
            catch (SshWireException)
            {
                return true;
            }

        }

        private static JObject Trigger(String Message)
            => new (new JProperty("requestedMessage", Message));

        private static async Task<Boolean> EndsWithin(SshMessageConnection Connection, TimeSpan Within)
        {

            var until = DateTime.UtcNow + Within;

            while (Connection.IsAlive && DateTime.UtcNow < until)
                await Task.Delay(50);

            return !Connection.IsAlive;

        }

        #endregion

        #region (private) Until(Condition, What) / Story()

        /// <summary>
        /// Wait for something to become true, or fail saying what did not
        /// happen, and what the station and the CSMS said meanwhile.
        /// </summary>
        private async Task Until(Func<Boolean> Condition, String What)
        {

            var until = DateTimeOffset.UtcNow + Patience;

            while (!Condition())
            {

                if (DateTimeOffset.UtcNow > until)
                    Assert.Fail($"{What} did not happen within {Patience.TotalSeconds:F0} s.{Story()}");

                await Task.Delay(25);

            }

        }

        /// <summary>
        /// What the CSMS and the station said, for a test that failed.
        /// </summary>
        private String Story()

            => String.Join(
                   Environment.NewLine,
                   new[] { "", "What was said:" }.
                       Concat(csms?.   Log.Recent(100).Select(entry => $"  CSMS {entry.Timestamp:HH:mm:ss.fff} [{entry.LevelName}] {entry.Message}") ?? []).
                       Concat(station?.Log.Recent(100).Select(entry => $"  CS   {entry.Timestamp:HH:mm:ss.fff} [{entry.LevelName}] {entry.Message}") ?? []).
                       Concat(station?.DialledConnections.Select(said => $"  CS   dialled {said.Key}: {said.Value}") ?? [])
               );

        #endregion


        // What works

        #region AnInstallersTool_IsAnsweredByTheStation_UnderItsAccountsName()

        /// <summary>
        /// An installer's tool signs in with the installer's own key, gets the
        /// subsystem, and is answered by the station's OCPP 2.1 node - which
        /// knows it by the account's name, not as the CSMS.
        /// </summary>
        [Test]
        public async Task AnInstallersTool_IsAnsweredByTheStation_UnderItsAccountsName()
        {

            await AStation(Commissioning: true);

            var (tool, why) = await Connect();

            Assert.That(tool, Is.Not.Null, $"{why}{Story()}");

            var answer = await tool!.Ask("TriggerMessage", Trigger("BootNotification"));

            Assert.Multiple(() => {

                Assert.That(tool.Connection.Subprotocol,  Is.EqualTo("ocpp2.1"));
                Assert.That(answer,                       Is.Not.Null,   $"The station did not answer the tool.{Story()}");
                Assert.That(answer?[0].Value<Int32>(),    Is.EqualTo(3), $"The station answered with an error: {answer}");
                Assert.That(answer?[2]?["status"]?.Value<String>(), Is.EqualTo("Accepted"), answer?.ToString());

                Assert.That(TheStation.CommissioningTools.Select(id => id.ToString()), Is.EqualTo(new[] { Installer }),
                            "The tool is not known by its account's name.");

                Assert.That(TheStation.SSHSessions.Any(session => session.Account == Installer && session.Label == Station.CommissioningLabel), Is.True,
                            "The station's SSH server does not say what the installer's session is for.");

            });

        }

        #endregion

        #region TheUplink_KeepsItsCSMS_WhileAToolIsConnected_AndAfterItWent(Uplink)

        /// <summary>
        /// A station reporting to a real CSMS, and a tool connected: what the
        /// tool asks for goes to the CSMS where it is the CSMS's - a
        /// BootNotification asked for is sent up the uplink; what the CSMS
        /// asks is answered to the CSMS, and the station's own heartbeat is
        /// answered by it. The
        /// tool is sent nothing but the answers to its own requests. After the
        /// tool went, all of it still works: the uplink kept its route.
        /// </summary>
        /// <remarks>
        /// The case behind the decision in 7.3 of the plan: a tool connected
        /// as the CSMS would have taken the station's one route to its CSMS
        /// away from the uplink, and left it pointing at nothing once it went.
        /// </remarks>
        [TestCase("ssh")]
        [TestCase("ws")]
        public async Task TheUplink_KeepsItsCSMS_WhileAToolIsConnected_AndAfterItWent(String Uplink)
        {

            await ACSMS();
            await AStation(Commissioning: true, Uplink: Uplink);

            var (tool, why) = await Connect();

            Assert.That(tool, Is.Not.Null, $"{why}{Story()}");

            // The tool asks the station to register again: the station does,
            // where it registers - with the CSMS.
            var bootsBefore       = AtTheCSMS("BootNotification");
            var answer            = await tool!.Ask("TriggerMessage", Trigger("BootNotification"));

            Assert.Multiple(() => {
                Assert.That(answer?[0].Value<Int32>(),              Is.EqualTo(3),          $"The tool was not answered: {answer}{Story()}");
                Assert.That(answer?[2]?["status"]?.Value<String>(), Is.EqualTo("Accepted"), answer?.ToString());
            });

            await Until(() => AtTheCSMS("BootNotification") > bootsBefore, "The BootNotification the tool asked for reaching the CSMS");

            // The CSMS asks, the tool still connected.
            bootsBefore           = AtTheCSMS("BootNotification");
            var asked             = await TheCSMSAsks(MessageTrigger.BootNotification);

            Assert.Multiple(() => {
                Assert.That(asked.Result.ResultCode,  Is.EqualTo(ResultCode.OK), $"The CSMS asking, a tool connected: {asked.Result}{Story()}");
                Assert.That(asked.Status,             Is.EqualTo(TriggerMessageStatus.Accepted));
            });

            await Until(() => AtTheCSMS("BootNotification") > bootsBefore, "The BootNotification the CSMS asked for reaching it");

            // The station's own heartbeat goes up and is answered.
            var heartbeat         = await StationNode.SendHeartbeat();

            Assert.Multiple(() => {
                Assert.That(heartbeat.Result.ResultCode,  Is.EqualTo(ResultCode.OK), $"The station's heartbeat, a tool connected: {heartbeat.Result}{Story()}");
                Assert.That(tool.GotOnlyItsOwnAnswers,    Is.True,                   $"The tool was sent what is not its own: {String.Join(" ", tool.Received.Select(message => message.ToString(Formatting.None)))}");
            });

            // The tool goes.
            await tool.Connection.Close(MessageCloseCode.NormalClosure, "done");

            await Until(() => !TheStation.CommissioningTools.Any(), "The tool going away");

            bootsBefore           = AtTheCSMS("BootNotification");
            var askedAfter        = await TheCSMSAsks(MessageTrigger.BootNotification);

            Assert.Multiple(() => {
                Assert.That(askedAfter.Result.ResultCode,  Is.EqualTo(ResultCode.OK), $"The CSMS asking after the tool went: {askedAfter.Result}{Story()}");
                Assert.That(askedAfter.Status,             Is.EqualTo(TriggerMessageStatus.Accepted));
            });

            await Until(() => AtTheCSMS("BootNotification") > bootsBefore, "The BootNotification the CSMS asked for after the tool went reaching it");

            var heartbeatAfter    = await StationNode.SendHeartbeat();

            Assert.That(heartbeatAfter.Result.ResultCode,  Is.EqualTo(ResultCode.OK), $"The station's heartbeat after the tool went: {heartbeatAfter.Result}{Story()}");

        }

        #endregion


        // Who is let in

        #region TheInstallerAndTheAdministrator_GetTheSubsystem_TheOperatorAndAViewerDoNot()

        /// <summary>
        /// Accounts of the station's four roles, each with a key of its own:
        /// the installer and the system administrator get the subsystem, the
        /// operator - who may read about commissioning - and a viewer do not.
        /// </summary>
        [Test]
        public async Task TheInstallerAndTheAdministrator_GetTheSubsystem_TheOperatorAndAViewerDoNot()
        {

            await AStation(Commissioning: true);

            var cpoKey     = SshHostKey.GenerateEd25519();
            var viewerKey  = SshHostKey.GenerateEd25519();

            await MakeAccount("cpo01",    "cpo",    cpoKey);
            await MakeAccount("viewer01", "viewer", viewerKey);

            var root       = await RefusedTo(Root,       rootKey);
            var installer  = await RefusedTo(Installer,  installerKey);
            var cpo        = await RefusedTo("cpo01",    cpoKey);
            var viewer     = await RefusedTo("viewer01", viewerKey);

            Assert.Multiple(() => {
                Assert.That(root,       Is.False, "The system administrator was refused.");
                Assert.That(installer,  Is.False, "The installer was refused.");
                Assert.That(cpo,        Is.True,  "The operator was let in.");
                Assert.That(viewer,     Is.True,  "A viewer was let in.");
            });

        }

        #endregion

        #region ATool_ConnectsAsItsAccount_AndAsNothingElse()

        /// <summary>
        /// A HELLO naming the CSMS, the station or another node than the
        /// account is refused, and so is an account named like the CSMS; a
        /// HELLO naming the account itself is not. With a CSMS on the uplink,
        /// which the refused tools would have taken the station's route to.
        /// </summary>
        [Test]
        public async Task ATool_ConnectsAsItsAccount_AndAsNothingElse()
        {

            await ACSMS();
            await AStation(Commissioning: true, Uplink: "ssh");

            var csmsKey = SshHostKey.GenerateEd25519();

            await MakeAccount("csms", "installer", csmsKey);

            var (asTheCSMS,    whyCSMS)     = await Connect(new JObject(new JProperty("networkingNodeId", "CSMS")));
            var (asTheStation, whyStation)  = await Connect(new JObject(new JProperty("networkingNodeId", StationNodeId)));
            var (asAnother,    whyAnother)  = await Connect(new JObject(new JProperty("networkingNodeId", "installer02")));
            var (namedSo,      whyNamed)    = await Connect(Login: "csms", Key: csmsKey);
            var (asItself,     why)         = await Connect(new JObject(new JProperty("networkingNodeId", Installer)));

            Assert.Multiple(() => {

                Assert.That(asTheCSMS,     Is.Null,     "A tool connected as the CSMS.");
                Assert.That(asTheStation,  Is.Null,     "A tool connected as the station.");
                Assert.That(asAnother,     Is.Null,     "A tool connected as another node than its account.");
                Assert.That(namedSo,       Is.Null,     "An account named like the CSMS connected as a tool.");
                Assert.That(asItself,      Is.Not.Null, $"{why}{Story()}");

                Assert.That(whyCSMS,       Does.Contain("refused"));
                Assert.That(whyNamed,      Does.Contain("CSMS"),        "An account named like the CSMS was not told why.");

            });

            // The CSMS still reaches the station, and the station the CSMS.
            var asked      = await TheCSMSAsks(MessageTrigger.BootNotification);
            var heartbeat  = await StationNode.SendHeartbeat();

            Assert.Multiple(() => {
                Assert.That(asked.Status,                 Is.EqualTo(TriggerMessageStatus.Accepted), $"{asked.Result}{Story()}");
                Assert.That(heartbeat.Result.ResultCode,  Is.EqualTo(ResultCode.OK),                 $"{heartbeat.Result}{Story()}");
                Assert.That(asItself!.GotOnlyItsOwnAnswers, Is.True);
            });

        }

        #endregion

        #region AToolOfOCPP16_IsServed_EveryToolGetsItsFirstChoice()

        /// <summary>
        /// OCPP 1.6 is served on the subsystem too: a tool gets the first of
        /// the subprotocols it offers that the station speaks - 1.6 where it
        /// offers 1.6 alone or first, 2.0.1 where it offers 2.0.1 first - and
        /// a tool offering nothing the station speaks is refused, with what is
        /// spoken.
        /// </summary>
        /// <remarks>
        /// Until ChargingStation 494a730 OCPP 1.6 was not served here, and a
        /// tool that offered it alone was refused. Since, the HELLO hands a
        /// tool to the station's node of its version (white paper "OCPP over
        /// SSH", decision 13; Hermod's SshMessageServer.RoutedBySubprotocol).
        /// </remarks>
        [Test]
        public async Task AToolOfOCPP16_IsServed_EveryToolGetsItsFirstChoice()
        {

            await AStation(Commissioning: true);

            var chosen = new List<String?>();

            foreach (var offered in new String[][] { [ "ocpp1.6" ], [ "ocpp2.0.1", "ocpp1.6" ], [ "ocpp1.6", "ocpp2.0.1" ] })
            {

                var (tool, why) = await Connect(Subprotocols: offered);

                Assert.That(tool, Is.Not.Null, $"A tool offering {String.Join(", ", offered)} was refused: {why}");

                chosen.Add(tool!.Connection.Subprotocol);

                await tool.Connection.Close(MessageCloseCode.NormalClosure, "done");

                await Until(() => !TheStation.CommissioningTools.Any(), "The tool going away");

            }

            var (none, refusal) = await Connect(Subprotocols: [ "ocpp1.5" ]);

            Assert.Multiple(() => {
                Assert.That(chosen,   Is.EqualTo(new[] { "ocpp1.6", "ocpp2.0.1", "ocpp1.6" }));
                Assert.That(none,     Is.Null, "A tool speaking nothing the station speaks got the subsystem.");
                Assert.That(refusal,  Does.Contain("unsupported-subprotocol"));
                Assert.That(refusal,  Does.Contain("ocpp2.1").And.Contain("ocpp1.6"), "The refusal does not say what is spoken.");
            });

        }

        #endregion


        // On and off

        #region OffWhereTheFileSaysNothing_NoToolGetsTheSubsystem()

        /// <summary>
        /// Commissioning is off where the station's file says nothing of it:
        /// neither root nor the installer gets the subsystem.
        /// </summary>
        [Test]
        public async Task OffWhereTheFileSaysNothing_NoToolGetsTheSubsystem()
        {

            await AStation(Commissioning: null);

            var root       = await RefusedTo(Root,      rootKey);
            var installer  = await RefusedTo(Installer, installerKey);

            Assert.Multiple(() => {
                Assert.That(TheStation.CommissioningServed,  Is.False, "Served where the file says nothing.");
                Assert.That(TheStation.CommissioningURL,     Is.Null);
                Assert.That(root,                            Is.True,  "Root got the subsystem.");
                Assert.That(installer,                       Is.True,  "The installer got the subsystem.");
            });

        }

        #endregion

        #region SwitchedOffOverHTTP_TheToolIsLetGoOf_AndOnAgain_OneGetsIn()

        /// <summary>
        /// Switched off over the station's web interface, as an administrator
        /// does it from the page: the tool connected is let go of, the
        /// subsystem is gone, and the file says so. Switched on again the same
        /// way, a tool gets in - without the station being started again.
        /// </summary>
        [Test]
        public async Task SwitchedOffOverHTTP_TheToolIsLetGoOf_AndOnAgain_OneGetsIn()
        {

            await AStation(Commissioning: true);

            // Longer than the test, so that it is switching off that lets the
            // tool go, and not the node's next round of checks.
            TheStation.SSHSessionRecheckEvery = TimeSpan.FromMinutes(10);

            var (tool, why) = await Connect();

            Assert.That(tool, Is.Not.Null, $"{why}{Story()}");

            var (status, json) = await SwitchOverHTTP(false);

            Assert.Multiple(() => {
                Assert.That(status,                             Is.EqualTo(HttpStatusCode.OK), json.ToString());
                Assert.That(json["served"]?.Value<Boolean>(),   Is.False);
                Assert.That(JObject.Parse(File.ReadAllText(TheStation.ConfigFile.Path))["commissioning"]?["enabled"]?.Value<Boolean>(), Is.False,
                            "Switched off, and not written down.");
            });

            Assert.That(await EndsWithin(tool!.Connection, TimeSpan.FromSeconds(10)), Is.True, "Switched off, and the tool stayed connected.");
            Assert.That(await RefusedTo(Installer, installerKey), Is.True, "Switched off, and the subsystem is still there.");

            (status, json) = await SwitchOverHTTP(true);

            Assert.That(status, Is.EqualTo(HttpStatusCode.OK), json.ToString());

            var (again, whyNot) = await Connect();

            Assert.That(again, Is.Not.Null, $"Switched on again, and no tool gets in: {whyNot}{Story()}");
            Assert.That((await again!.Ask("TriggerMessage", Trigger("BootNotification")))?[0].Value<Int32>(), Is.EqualTo(3));

        }

        #endregion

        #region TheStationsSSHServerOff_LetsTheToolGo_AndNoneIn_UntilItIsOnAgain()

        /// <summary>
        /// Commissioning on, and the station's SSH server switched off while a
        /// tool is connected: the tool is let go of, and no tool can connect -
        /// there is nothing listening. Where it starts off, commissioning has
        /// no URL. The SSH server on again, a tool gets in again: the subsystem
        /// is served by the server that runs now.
        /// </summary>
        [Test]
        public async Task TheStationsSSHServerOff_LetsTheToolGo_AndNoneIn_UntilItIsOnAgain()
        {

            await AStation(Commissioning: true);

            var port         = TheStation.SSHPort!.Value.ToUInt16();
            var (tool, why)  = await Connect();

            Assert.That(tool, Is.Not.Null, $"{why}{Story()}");

            var off = await TheStation.TryUpdateSSHConfiguration(new JObject(new JProperty("enabled", false)));

            Assert.That(off.IsChanged, Is.True, off.Error);

            Assert.That(await EndsWithin(tool!.Connection, TimeSpan.FromSeconds(10)), Is.True, "The SSH server switched off, and the tool stayed connected.");

            var (whileOff, whyNot) = await Connect(Port: port);

            Assert.Multiple(() => {
                Assert.That(whileOff,                                           Is.Null, "A tool got in with the station's SSH server off.");
                Assert.That(TheStation.CommissioningJSON()["url"]?.Type,        Is.EqualTo(JTokenType.Null), "Commissioning still says where to connect.");
                Assert.That(TheStation.CommissioningJSON()["sshServer"]?.Value<Boolean>(), Is.False);
            });

            var on = await TheStation.TryUpdateSSHConfiguration(new JObject(new JProperty("enabled", true)));

            Assert.That(on.IsChanged, Is.True, on.Error);

            var (again, whyAgain) = await Connect();

            Assert.That(again, Is.Not.Null, $"The SSH server on again, and no tool gets in: {whyAgain}{Story()}");
            Assert.That((await again!.Ask("TriggerMessage", Trigger("BootNotification")))?[0].Value<Int32>(), Is.EqualTo(3));

        }

        #endregion

        #region AStationWithItsSSHServerOff_HasNoDoorForATool()

        /// <summary>
        /// Commissioning switched on in the file of a station whose SSH server
        /// is off: it says it cannot be reached, and nothing listens.
        /// </summary>
        [Test]
        public async Task AStationWithItsSSHServerOff_HasNoDoorForATool()
        {

            await AStation(Commissioning: true, SSH: false);

            var json = TheStation.CommissioningJSON();

            Assert.Multiple(() => {
                Assert.That(json["enabled"]?.Value<Boolean>(),    Is.True);
                Assert.That(json["sshServer"]?.Value<Boolean>(),  Is.False);
                Assert.That(json["url"]?.Type,                    Is.EqualTo(JTokenType.Null), "Commissioning says where to connect, with no SSH server.");
                Assert.That(TheStation.SSHEnabled,                Is.False);
                Assert.That(TheStation.Log.Recent(200).Any(entry => entry.Message.Contains("SSH server of this charging station is off")), Is.True,
                            "Nobody is told that no tool can reach the station.");
            });

        }

        #endregion

    }

}
