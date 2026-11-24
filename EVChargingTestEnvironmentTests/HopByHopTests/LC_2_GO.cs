/*
 * Copyright (c) 2014-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of EVChargingTestEnvironment <https://github.com/OpenChargingCloud/EVChargingTestEnvironment>
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
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

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Hermod.SSH;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.SSH.Client;

using cloud.charging.open.protocols.WWCP.Node.OpenADR;
using cloud.charging.open.protocols.WWCP.Node.TestKit;
using cloud.charging.open.protocols.WWCP.Node.Connections;

using GridOperatorNode  = cloud.charging.open.GridOperator.GridOperator;
using Controller        = cloud.charging.open.LocalController.LocalController;

#endregion

namespace cloud.charging.open.TestEnvironment.Tests.Connections.HopByHop
{

    /// <summary>
    /// A local controller connects to its grid operator as a VEN of OpenADR 3 -
    /// over ws://, wss://, wss:// with a client certificate and ssh:// - and asks
    /// the VTN of the grid operator through that connection, in either binding:
    /// the REST API of OpenADR, unchanged, in the tunnel of the connection; or
    /// the JSON messages of OpenADR, over which the VTN tells it of a new limit
    /// at once. Who it is, is the VEN the grid operator wrote down for its
    /// login: it has no client secret, and shows no token.
    /// </summary>
    [TestFixture]
    public class LC_2_GO : AHopByHopTest
    {

        #region Data

        /// <summary>
        /// The two grid connection points of the grid operator.
        /// </summary>
        private const String  PointOne  = "GCP-1";
        private const String  PointTwo  = "GCP-2";

        /// <summary>
        /// The two bindings of OpenADR over a connection.
        /// </summary>
        private const String  bindingREST      = "rest";
        private const String  bindingMessages  = "messages";

        private DNSClient?         dnsClient;
        private Controller?        controller;
        private GridOperatorNode?  grid;

        /// <summary>
        /// The directories and web ports of both nodes: a node restarted is
        /// started again from them.
        /// </summary>
        private NodeDirectory?     gridDirectory;
        private IPPort?            gridHTTPPort;
        private NodeDirectory?     controllerDirectory;
        private IPPort?            controllerHTTPPort;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public async Task Start()
        {

            // One fixture runs every test: each starts with nodes of their own.
            gridDirectory        = null;
            gridHTTPPort         = null;
            controllerDirectory  = null;
            controllerHTTPPort   = null;

            dnsClient   = new DNSClient(SearchForIPv6DNSServers: false);

            grid        = NewGridOperator();

            await grid.Start();

            foreach (var point in new[] { PointOne, PointTwo })
                Assert.That(grid.TryAddGridConnectionPoint(APoint(point), "test", out _, out var error, out _), Is.True, error);

            controller  = NewController();

            await controller.Start();

        }

        [TearDown]
        public async Task Stop()
        {

            if (controller is not null)
            {
                await controller.Stop();
                await controller.DisposeAsync();
            }

            if (grid is not null)
            {
                await grid.Stop();
                await grid.DisposeAsync();
            }

        }

        #endregion


        #region (private) NewGridOperator()

        /// <summary>
        /// A grid operator of its own directory: its WebSocket port of OpenADR
        /// switched on - over TLS, with a certificate of the test's CA, where the
        /// test connects over wss:// - and its SSH server, serving the subsystem
        /// of the REST API of OpenADR. Asked again, it is the grid operator of
        /// that directory and web port again, as it is after a restart.
        /// </summary>
        private GridOperatorNode NewGridOperator()
        {

            var fresh      = gridDirectory is null;

            var directory  = gridDirectory ??= NewNodeDirectory(
                                 "go",
                                 new JProperty("gridOperator", new JObject(
                                     new JProperty("@id",   "DE*SWJ"),
                                     new JProperty("name",  "Netze Beispielstadt")
                                 )),
                                 new JProperty("webSocketServer", new JObject(
                                     new JProperty("enabled",               true),
                                     new JProperty("address",               "127.0.0.1"),
                                     new JProperty("port",                  TestPorts.Free().ToUInt16()),
                                     new JProperty("clientAuthentication",  new JArray(OverMTLS
                                                                                          ? "tlsClientCertificate"
                                                                                          : OverTLS
                                                                                                ? "httpAuthOverTLS"
                                                                                                : "httpAuthWithoutTLS")),
                                     new JProperty("ssh",                   new JObject(
                                         new JProperty("enabled",           true)
                                     ))
                                 )),
                                 new JProperty("ssh", new JObject(
                                     new JProperty("enabled",  true),
                                     new JProperty("port",     TestPorts.Free().ToUInt16())
                                 ))
                             );

            if (OverTLS && fresh)
                ACertificateOfTheTestCA(Path.GetDirectoryName(directory.ConfigFile.Path)!, GridOperatorNode.VENListener);

            return new GridOperatorNode(
                       HTTPPort:        gridHTTPPort ??= TestPorts.Free(),
                       AccountsPath:    directory.Accounts,
                       ConfigFile:      directory.ConfigFile,
                       DNSClient:       dnsClient,
                       LogToConsole:    false,
                       BridgeDebugLog:  false
                   );

        }

        #endregion

        #region (private) NewController()

        /// <summary>
        /// A local controller of its own directory - asked again, the local
        /// controller of that directory and web port again, as it is after a
        /// restart.
        /// </summary>
        private Controller NewController()
        {

            var directory = controllerDirectory ??= NewNodeDirectory("lc");

            return new Controller(
                       DNSClient:       dnsClient,
                       HTTPPort:        controllerHTTPPort ??= TestPorts.Free(),
                       AccountsPath:    directory.Accounts,
                       ConfigFile:      directory.ConfigFile,
                       LogToConsole:    false,
                       BridgeDebugLog:  false
                   );

        }

        #endregion

        #region (private) RestartTheGridOperator() / RestartTheController()

        /// <summary>
        /// The grid operator stops, and starts again from its directory: what it
        /// wrote down it has again, what it held in its memory only is gone.
        /// </summary>
        private async Task RestartTheGridOperator()
        {

            await grid!.Stop();
            await grid.DisposeAsync();

            grid = NewGridOperator();

            await grid.Start();

        }

        /// <summary>
        /// The local controller stops, and starts again from its directory.
        /// </summary>
        private async Task RestartTheController()
        {

            await StopTheController();

            controller = NewController();

            await controller.Start();

        }

        /// <summary>
        /// The local controller stops.
        /// </summary>
        private async Task StopTheController()
        {

            await controller!.Stop();
            await controller.DisposeAsync();

        }

        #endregion

        #region (private) SwitchTheConnection(Inbound, On)

        /// <summary>
        /// The grid operator switches its connection of a VEN on or off.
        /// </summary>
        private void SwitchTheConnection(String   Inbound,
                                         Boolean  On)
        {

            var entry = grid!.Connections.Connections.First(entry => entry.Id == Inbound);

            Assert.That(grid.Connections.TryUpdateConnection(entry.Id,
                                                             entry.Description,
                                                             entry.URL,
                                                             "VEN",
                                                             On,
                                                             null,
                                                             null,
                                                             out var error,
                                                             Inbound:         true,
                                                             OpenADRBinding:  entry.OpenADRBinding == OpenADRBinding.REST ? bindingREST : bindingMessages,
                                                             VEN:             entry.VEN),
                        Is.True, error);

        }

        #endregion

        #region (private static) APoint(Id) / ALocalController(VENName, PoolId, PointId)

        /// <summary>
        /// A grid connection point, as POI 3 writes one.
        /// </summary>
        private static JObject APoint(String Id)

            => new (new JProperty("@id",                            Id),
                    new JProperty("name",                           new JObject(new JProperty("de", $"Netzanschluss {Id}"))),
                    new JProperty("voltageLevel",                   "LowVoltage"),
                    new JProperty("connectionType",                 "AC_ThreePhases"),
                    new JProperty("nominalVoltage",                 400),
                    new JProperty("nominalFrequency",               50),
                    new JProperty("contractedImportPower",          110000),
                    new JProperty("contractedImportApparentPower",  120000),
                    new JProperty("marketLocationIds",              new JArray("50123456789")));

        /// <summary>
        /// A local controller - a VEN - the grid operator lets in, behind the
        /// given point.
        /// </summary>
        private String AVEN(String PointId)
        {

            var venName = "ven-" + RandomExtensions.RandomString(8).ToLowerInvariant();

            Assert.That(grid!.TryAddLocalController(new JObject(new JProperty("venName",                venName),
                                                                new JProperty("chargingPoolId",         "DE*GEF*P" + RandomExtensions.RandomString(4).ToUpperInvariant()),
                                                                new JProperty("gridConnectionPointId",  PointId)),
                                                    "test", out _, out _, out var error, out _),
                        Is.True, error);

            return venName;

        }

        #endregion


        #region (private) TheControllerConnects(Authentication, Transport, PointId = GCP-1, Enabled = true, Binding = "rest", InboundBinding = null)

        /// <summary>
        /// The grid operator writes the local controller down as a VEN that
        /// connects - a VEN behind the given point -, and the local controller
        /// connects to the grid operator - over a WebSocket of the REST API of
        /// OpenADR with a password, a one-time password or a certificate, or
        /// over SSH with a password, a one-time password or its own key - and
        /// asks its VTN through that connection.
        /// </summary>
        /// <param name="Authentication">How the controller signs in: "password", "totp", "publicKey" - or "mTLS".</param>
        /// <param name="Transport">"ws", "wss" or "ssh".</param>
        /// <param name="PointId">The grid connection point the VEN is behind.</param>
        /// <param name="Enabled">Whether the grid operator's connection of the VEN is switched on.</param>
        /// <param name="Binding">"rest" or "messages": the binding both ends write the connection down with.</param>
        /// <param name="InboundBinding">The binding the grid operator writes the connection down with, where it is not the controller's.</param>
        /// <returns>The login, the VEN, the connection at the controller and the one at the grid operator.</returns>
        private async Task<(User_Id Login, String VEN, String Connection, String Inbound)> TheControllerConnects(String   Authentication,
                                                                                                               String   Transport,
                                                                                                               String   PointId   = PointOne,
                                                                                                               Boolean  Enabled   = true,
                                                                                                               String   Binding          = bindingREST,
                                                                                                               String?  InboundBinding   = null)
        {

            var controller   = this.controller!;
            var grid         = this.grid!;

            var login        = User_Id.Parse("lc-" + RandomExtensions.RandomString(8));
            var ven          = AVEN(PointId);
            var description  = I18NString.Create($"The local controller --({Transport}/{Authentication})--> the grid operator, as '{ven}'");
            var error        = "";

            var door         = Transport == "ssh"
                                   ? $"ssh://{login}@127.0.0.1:{grid.SSHPort!.Value.ToUInt16()}"
                                   : $"{Transport}://127.0.0.1:{grid.VENServerSettings.TCPPort!.Value.ToUInt16()}/{login}";

            if (Transport == "ssh")
                Assert.That(grid.OpenADROverSSHServed, Is.True, "OpenADR over SSH is switched on in the file, and not served.");
            else
                Assert.That(grid.VENServerRunning, Is.True, "The WebSocket server of OpenADR is switched on in the file, and not running.");

            if (Transport == "wss")
                Assert.That(grid.VENServerTLS, Is.True, "The WebSocket port of OpenADR of the grid operator does not speak TLS.");

            BelievesTheTestCA(controller);

            // The grid operator: the controller, written down as the VEN it is.
            Assert.That(
                grid.Connections.TryAddConnection(
                    I18NString.Create($"The local controller of '{ven}'"),
                    URL.Parse(door),
                    "VEN",
                    Enabled,
                    null,
                    null,
                    out var inbound,
                    out error,
                    Inbound:         true,
                    OpenADRBinding:  InboundBinding ?? Binding,
                    VEN:             ven
                ),
                Is.True, error
            );

            // What the controller signs in with.
            String? credentials  = null;
            String? certificate  = null;

            switch (Authentication)
            {

                case authPublicKey:

                    var key = await controller.SshKey.MakeIfNeeded();

                    Assert.That(
                        grid.WebSocketLogins.TryAddSSHKey(login,
                                                          SshPublicKey.FromHostKey(key).ToAuthorizedKeyLine(),
                                                          "test",
                                                          null,
                                                          description,
                                                          "tests",
                                                          out _,
                                                          out error,
                                                          out _),
                        Is.True, error
                    );

                    break;

                case authMTLS:

                    Assert.That(grid.WebSocketLogins.TrySetPasswordAuthentication(login, RandomExtensions.RandomString(32), null, description, out error), Is.True, error);

                    certificate = ClientCertificate(controller, grid, login.ToString());

                    break;

                default:

                    credentials = Credentials(controller, grid, login, Authentication, description);

                    break;

            }

            // The controller: the connection to the grid operator, and what it
            // signs in with.
            Assert.That(
                controller.Connections.TryAddConnection(
                    Description:       description,
                    URL:               URL.Parse(door),
                    ConnectionType:    "GridOperator",
                    AutoConnect:       true,
                    AuthenticationId:  credentials,
                    CertificateId:     certificate,
                    Id:                out var connection,
                    Error:             out error,
                    SshHostKey:        Transport == "ssh" ? grid.SSHHostKey!.Split(' ', 2)[1] : null,
                    OpenADRBinding:    Binding
                ),
                Is.True, error
            );

            // And its VTN, asked through that connection - and, in the binding
            // of messages, asked every hour only: what it reads before then, it
            // was told of.
            var openADR = new JObject(
                              new JProperty("enabled",         true),
                              new JProperty("vtnURL",          door),
                              new JProperty("requestTimeout",  3)
                          );

            if (Binding == bindingMessages)
                openADR.Add(new JProperty("pollEvery", 3600));

            var (changed, wrong, _) = await controller.TryUpdateOpenADRConfiguration(openADR);

            Assert.That(changed, Is.True, wrong);

            if (Enabled && (InboundBinding ?? Binding) == Binding)
            {
                await Until(() => StatusOf(controller, connection!) == ConnectionStatus.Connected,  $"'{login}' connecting over {Transport}",          controller, grid);
                await Until(() => StatusOf(grid,       inbound!)    == ConnectionStatus.Connected,  $"'{login}' being let in by the grid operator",  controller, grid);
            }

            return (login, ven, connection!, inbound!);

        }

        #endregion

        #region (private) AssertConnectedAndAsking(Login, VEN, Connection, Inbound, Transport, Binding = "rest")

        /// <summary>
        /// Both ends see the connection as up, over OpenADR - and through it, the
        /// controller reads the limit the grid operator sends to its point, and
        /// reports on it, as its VEN.
        /// </summary>
        private async Task AssertConnectedAndAsking(User_Id  Login,
                                                    String   VEN,
                                                    String   Connection,
                                                    String   Inbound,
                                                    String   Transport,
                                                    String   Binding   = bindingREST)
        {

            var controller  = this.controller!;
            var grid        = this.grid!;

            var entry       = controller.Connections.Connections.First(connection => connection.Id == Connection);

            Assert.Multiple(() => {

                Assert.That(StatusOf(controller, Connection),              Is.EqualTo(ConnectionStatus.Connected),  $"The controller does not see the connection as up.{Story(controller, grid)}");
                Assert.That(StatusOf(grid,       Inbound),                 Is.EqualTo(ConnectionStatus.Connected),  $"The grid operator does not see the connection as up.{Story(controller, grid)}");
                Assert.That(IsConnectedAt(grid, Login),                    Is.True,                                 $"The grid operator did not let '{Login}' in.{Story(controller, grid)}");

                Assert.That(entry.Use,                                     Is.EqualTo(ConnectionUse.OpenADR));
                Assert.That(entry.OpenADRBinding,                          Is.EqualTo(Binding == bindingMessages ? OpenADRBinding.Messages : OpenADRBinding.REST));
                Assert.That(controller.GridOperatorTransportOf(Connection), Is.EqualTo(Transport switch {
                                                                                         "ssh"  => "SSH",
                                                                                         "wss"  => "WebSocket TLS",
                                                                                         _      => "WebSocket"
                                                                                     }));

            });

            // The limit, sent to the point the VEN is behind.
            Assert.That(grid.TrySendLimit(new JObject(new JProperty("kind",                   "IMPORT_CAPACITY_LIMIT"),
                                                      new JProperty("limitKW",                44),
                                                      new JProperty("durationMinutes",        60),
                                                      new JProperty("gridConnectionPointId",  PointOne)),
                                          "test", out var limit, out var error, out _),
                        Is.True, error);

            var problem = await controller.PollOpenADR();

            Assert.That(problem, Is.Null, $"The controller did not ask the VTN through the connection.{Story(controller, grid)}");

            Assert.Multiple(() => {

                Assert.That(controller.ImportLimitNow?.KW,  Is.EqualTo(44),  $"The controller did not read the limit of its point.{Story(controller, grid)}");
                Assert.That(controller.OpenADRSignedIn,     Is.True);
                Assert.That(controller.OpenADRToken.TokenURL, Is.Null,       "The controller asked for a token, which the connection makes needless.");

                Assert.That(grid.OpenADR.Reports().Any(report => report.ClientID == VEN && report.EventId.ToString() == limit!.Id.ToString()),
                            Is.True,
                            $"The grid operator did not get the report of '{VEN}' on its limit.{Story(controller, grid)}");

            });

        }

        #endregion


        #region WebSocket     (Authentication, Binding)

        /// <summary>
        /// The controller connects to the grid operator over a WebSocket of
        /// OpenADR - of its REST API or of its JSON messages -, with a password
        /// or a one-time password, and asks the VTN through it.
        /// </summary>
        [TestCase(authPassword, bindingREST)]
        [TestCase(authTOTP,     bindingREST)]
        [TestCase(authPassword, bindingMessages)]
        [TestCase(authTOTP,     bindingMessages)]
        public async Task WebSocket(String Authentication, String Binding)
        {

            var (login, ven, connection, inbound) = await TheControllerConnects(Authentication, "ws", Binding: Binding);

            await AssertConnectedAndAsking(login, ven, connection, inbound, "ws", Binding);

        }

        #endregion

        #region WebSocketTLS  (Authentication, Binding)

        /// <summary>
        /// The controller connects to the grid operator over a WebSocket of
        /// OpenADR over TLS - of its REST API or of its JSON messages -, with a
        /// password or a one-time password, believing the grid operator by the
        /// test's CA, and asks the VTN through it.
        /// </summary>
        [TestCase(authPassword, bindingREST)]
        [TestCase(authTOTP,     bindingREST)]
        [TestCase(authPassword, bindingMessages)]
        [TestCase(authTOTP,     bindingMessages)]
        public async Task WebSocketTLS(String Authentication, String Binding)
        {

            var (login, ven, connection, inbound) = await TheControllerConnects(Authentication, "wss", Binding: Binding);

            await AssertConnectedAndAsking(login, ven, connection, inbound, "wss", Binding);

        }

        #endregion

        #region WebSocketMTLS (Binding)

        /// <summary>
        /// The controller connects to the grid operator over a WebSocket of
        /// OpenADR over mutual TLS - of its REST API or of its JSON messages -:
        /// it signs in with a certificate of the test's CA that names its login.
        /// </summary>
        [TestCase(bindingREST)]
        [TestCase(bindingMessages)]
        public async Task WebSocketMTLS(String Binding)
        {

            var (login, ven, connection, inbound) = await TheControllerConnects(authMTLS, "wss", Binding: Binding);

            await AssertConnectedAndAsking(login, ven, connection, inbound, "wss", Binding);

        }

        #endregion

        #region SSH           (Authentication, Binding)

        /// <summary>
        /// The controller connects to the subsystem of OpenADR of the grid
        /// operator's SSH server - of its REST API or of its JSON messages -,
        /// pinning the key the server shows - with its password, with a one-time
        /// password, or with its own key - and asks the VTN through it.
        /// </summary>
        [TestCase(authPassword,   bindingREST)]
        [TestCase(authTOTP,       bindingREST)]
        [TestCase(authPublicKey,  bindingREST)]
        [TestCase(authPassword,   bindingMessages)]
        [TestCase(authTOTP,       bindingMessages)]
        [TestCase(authPublicKey,  bindingMessages)]
        public async Task SSH(String Authentication, String Binding)
        {

            var (login, ven, connection, inbound) = await TheControllerConnects(Authentication, "ssh", Binding: Binding);

            await AssertConnectedAndAsking(login, ven, connection, inbound, "ssh", Binding);

        }

        #endregion


        #region OpenADR_ALimit_IsToldOverTheMessages_BeforeTheNextPoll(Transport)

        /// <summary>
        /// In the binding of messages the controller subscribes to the events
        /// over its connection, and a limit the grid operator sends to its point
        /// later reaches it by Notify - long before its next poll, an hour away
        /// -, as does the limit cancelled; the VEN behind the other point, also
        /// connected and subscribed, is told of neither.
        /// </summary>
        [TestCase("ws")]
        [TestCase("wss")]
        [TestCase("ssh")]
        public async Task OpenADR_ALimit_IsToldOverTheMessages_BeforeTheNextPoll(String Transport)
        {

            var grid        = this.grid!;
            var controller  = this.controller!;

            var (_, ven, _, _) = await TheControllerConnects(authPassword, Transport, Binding: bindingMessages);

            await Until(() => grid.Subscriptions.Of(ven).Count == 1, $"'{ven}' subscribing to the events over its connection", controller, grid);

            // The first poll is behind it; the next is an hour away.
            var asked = controller.OpenADRLastPoll.Asked;

            Assert.That(grid.TrySendLimit(new JObject(new JProperty("kind",                   "IMPORT_CAPACITY_LIMIT"),
                                                      new JProperty("limitKW",                44),
                                                      new JProperty("durationMinutes",        60),
                                                      new JProperty("gridConnectionPointId",  PointOne)),
                                          "test", out var limit, out var error, out _),
                        Is.True, error);

            await Until(() => controller.ImportLimitNow?.KW == 44, "the limit reaching the controller by Notify", controller, grid);

            Assert.Multiple(() => {
                Assert.That(controller.OpenADRNotificationsReceived,  Is.GreaterThanOrEqualTo(1));
                Assert.That(grid.NotificationsToldOverMessages,       Is.GreaterThanOrEqualTo(1));
                Assert.That(controller.OpenADRLastPoll.Asked,         Is.GreaterThan(asked), "The controller did not ask at once when it was told.");
            });

            await Until(() => grid.OpenADR.Reports().Any(report => report.ClientID == ven && report.EventId.ToString() == limit!.Id.ToString()),
                        $"the report of '{ven}' on the limit it was told of", controller, grid);

            Assert.That(grid.TryCancelLimit(limit!.Id.ToString(), "test", out error, out _), Is.True, error);

            await Until(() => controller.ImportLimitNow is null, "the cancelled limit leaving the controller by Notify", controller, grid);

        }

        #endregion


        #region OpenADR_AnotherVEN_DoesNotSeeTheLimit(Transport, Binding)

        /// <summary>
        /// A limit sent to the point of another VEN is none of this one's: the
        /// controller connected as the VEN behind the second point reads nothing
        /// of the first point's limit, and reports on nothing - and reads the
        /// limit of its own point once there is one.
        /// </summary>
        [TestCase("ws",  bindingREST)]
        [TestCase("ssh", bindingREST)]
        [TestCase("ws",  bindingMessages)]
        [TestCase("ssh", bindingMessages)]
        public async Task OpenADR_AnotherVEN_DoesNotSeeTheLimit(String Transport, String Binding)
        {

            var grid        = this.grid!;
            var controller  = this.controller!;

            // A VEN behind the first point, connected nowhere.
            AVEN(PointOne);

            var (_, ven, _, _) = await TheControllerConnects(authPassword, Transport, PointTwo, Binding: Binding);

            Assert.That(grid.TrySendLimit(new JObject(new JProperty("kind",                   "IMPORT_CAPACITY_LIMIT"),
                                                      new JProperty("limitKW",                11),
                                                      new JProperty("durationMinutes",        60),
                                                      new JProperty("gridConnectionPointId",  PointOne)),
                                          "test", out _, out var error, out _),
                        Is.True, error);

            Assert.That(await controller.PollOpenADR(), Is.Null, $"The controller did not ask the VTN.{Story(controller, grid)}");

            Assert.Multiple(() => {
                Assert.That(controller.ImportLimitNow,                                   Is.Null,  "The controller read the limit of another VEN's point.");
                Assert.That(grid.OpenADR.Reports().Any(report => report.ClientID == ven),   Is.False, "The controller reported on the limit of another VEN's point.");
            });

            Assert.That(grid.TrySendLimit(new JObject(new JProperty("kind",                   "IMPORT_CAPACITY_LIMIT"),
                                                      new JProperty("limitKW",                22),
                                                      new JProperty("durationMinutes",        60),
                                                      new JProperty("gridConnectionPointId",  PointTwo)),
                                          "test", out _, out error, out _),
                        Is.True, error);

            Assert.That(await controller.PollOpenADR(), Is.Null, $"The controller did not ask the VTN.{Story(controller, grid)}");

            Assert.That(controller.ImportLimitNow?.KW, Is.EqualTo(22), "The controller did not read the limit of its own point.");

        }

        #endregion

        #region OpenADR_ASwitchedOffConnection_IsNotLetIn(Transport, Binding)

        /// <summary>
        /// A VEN whose connection the grid operator switched off is not let in -
        /// over a WebSocket its WebSocket is refused, over SSH nothing is served
        /// on its subsystem - and it reads no limit.
        /// </summary>
        [TestCase("ws",  bindingREST)]
        [TestCase("ssh", bindingREST)]
        [TestCase("ws",  bindingMessages)]
        [TestCase("ssh", bindingMessages)]
        public async Task OpenADR_ASwitchedOffConnection_IsNotLetIn(String Transport, String Binding)
        {

            var grid        = this.grid!;
            var controller  = this.controller!;

            var (login, _, connection, inbound) = await TheControllerConnects(authPassword, Transport, Enabled: false, Binding: Binding);

            Assert.That(grid.TrySendLimit(new JObject(new JProperty("kind",                   "IMPORT_CAPACITY_LIMIT"),
                                                      new JProperty("limitKW",                33),
                                                      new JProperty("durationMinutes",        60),
                                                      new JProperty("gridConnectionPointId",  PointOne)),
                                          "test", out _, out var error, out _),
                        Is.True, error);

            if (Transport == "ws")
                await Until(() => StatusOf(controller, connection) == ConnectionStatus.Refused, $"'{login}' being refused", controller, grid);

            var problem = await controller.PollOpenADR();

            Assert.Multiple(() => {
                Assert.That(problem,                     Is.Not.Null,                                    "The controller asked the VTN through a connection that is switched off.");
                Assert.That(controller.ImportLimitNow,   Is.Null,                                        "The controller read a limit through a connection that is switched off.");
                Assert.That(StatusOf(grid, inbound),     Is.Not.EqualTo(ConnectionStatus.Connected),     "The grid operator let in a connection that is switched off.");
                Assert.That(IsConnectedAt(grid, login),  Is.False,                                       $"The grid operator let '{login}' in.");
            });

        }

        #endregion

        #region OpenADR31_ALimitForEveryVEN_IsReadWithOnlyItsOwnTarget(Transport)

        /// <summary>
        /// The object privacy of OpenADR 3.1, across two VENs: a limit for every
        /// local controller is targeted at both, the controller connected as the
        /// second reads it and reports on it - its report of its clientID - and
        /// the first, signed in over HTTPS as OpenADR has it, reads the limit
        /// with only its own target, and not the report of the second.
        /// </summary>
        [TestCase("ws")]
        [TestCase("ssh")]
        public async Task OpenADR31_ALimitForEveryVEN_IsReadWithOnlyItsOwnTarget(String Transport)
        {

            var grid        = this.grid!;
            var controller  = this.controller!;

            // The first VEN, behind the first point, with a client secret of its own.
            var first = "ven-" + RandomExtensions.RandomString(8).ToLowerInvariant();

            Assert.That(grid.TryAddLocalController(new JObject(new JProperty("venName",                first),
                                                               new JProperty("chargingPoolId",         "DE*GEF*P" + RandomExtensions.RandomString(4).ToUpperInvariant()),
                                                               new JProperty("gridConnectionPointId",  PointOne)),
                                                   "test", out _, out var secret, out var error, out _),
                        Is.True, error);

            // The second, behind the second point: the controller.
            var (_, second, _, _) = await TheControllerConnects(authPassword, Transport, PointTwo);

            Assert.That(grid.TrySendLimit(new JObject(new JProperty("kind",             "IMPORT_CAPACITY_LIMIT"),
                                                      new JProperty("limitKW",          33),
                                                      new JProperty("durationMinutes",  60)),
                                          "test", out var limit, out error, out _),
                        Is.True, error);

            Assert.That(limit!.Event.Targets, Is.EquivalentTo(new[] { "ven:" + first, "ven:" + second }));

            Assert.That(await controller.PollOpenADR(), Is.Null, $"The controller did not ask the VTN.{Story(controller, grid)}");

            var report = grid.OpenADR.Reports().SingleOrDefault(report => report.EventId.ToString() == limit.Id.ToString());

            Assert.Multiple(() => {
                Assert.That(controller.ImportLimitNow?.KW,  Is.EqualTo(33),      "The controller did not read the limit for every VEN.");
                Assert.That(report?.ClientID,               Is.EqualTo(second),  "The report is not of the clientID of the VEN that sent it.");
            });

            // The first VEN, over the REST API with its client credentials.
            using var http   = new HttpClient { BaseAddress = new Uri(grid.OpenADRVTNURL), Timeout = TimeSpan.FromSeconds(30) };

            var tokenAnswer  = await http.PostAsync("auth/token", new FormUrlEncodedContent(new Dictionary<String, String> {
                                                                      { "grant_type",     "client_credentials" },
                                                                      { "client_id",      first },
                                                                      { "client_secret",  secret! }
                                                                  }));
            var token        = JObject.Parse(await tokenAnswer.Content.ReadAsStringAsync());

            Assert.That((Int32) tokenAnswer.StatusCode, Is.EqualTo(200), token.ToString());

            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token["access_token"]!.Value<String>());

            var events   = JArray.Parse(await http.GetStringAsync("events"));
            var reports  = JArray.Parse(await http.GetStringAsync("reports"));

            Assert.Multiple(() => {
                Assert.That(events.Select(one => one["id"]?.Value<String>()),     Is.EqualTo(new[] { limit.Id.ToString() }));
                Assert.That(events[0]["targets"]?.Values<String>(),                Is.EqualTo(new[] { "ven:" + first }), "The first VEN sees the target of the second.");
                Assert.That(reports,                                               Is.Empty,                            "The first VEN reads the report of the second.");
            });

        }

        #endregion

        #region OpenADR_AVENOverSSH_GetsNoCommandLine(Binding)

        /// <summary>
        /// A login of a VEN over SSH gets the subsystem of OpenADR of its
        /// binding, and nothing else: no command line of the grid operator, and
        /// not the subsystem of the other binding.
        /// </summary>
        [TestCase(bindingREST)]
        [TestCase(bindingMessages)]
        public async Task OpenADR_AVENOverSSH_GetsNoCommandLine(String Binding)
        {

            var grid      = this.grid!;
            var login     = User_Id.Parse("lc-" + RandomExtensions.RandomString(8));
            var password  = RandomExtensions.RandomString(32);
            var ven       = AVEN(PointOne);

            Assert.That(grid.Connections.TryAddConnection(I18NString.Create($"The local controller of '{ven}'"),
                                                          URL.Parse($"ssh://{login}@127.0.0.1:{grid.SSHPort!.Value.ToUInt16()}"),
                                                          "VEN",
                                                          true,
                                                          null,
                                                          null,
                                                          out _,
                                                          out var error,
                                                          Inbound:         true,
                                                          OpenADRBinding:  Binding,
                                                          VEN:             ven),
                        Is.True, error);

            Assert.That(grid.WebSocketLogins.TrySetPasswordAuthentication(login, password, null, I18NString.Create("A VEN"), out error), Is.True, error);

            var itsOwn    = Binding == bindingMessages ? OpenADRWireNames.SshSubsystem     : OpenADRWireNames.RestSshSubsystem;
            var theOther  = Binding == bindingMessages ? OpenADRWireNames.RestSshSubsystem : OpenADRWireNames.SshSubsystem;

            await using var ssh = await SSHClient.Connect("127.0.0.1",
                                                          grid.SSHPort!.Value,
                                                          new SSHClientOptions {
                                                              Username       = login,
                                                              Password       = password,
                                                              VerifyHostKey  = hostKey => true
                                                          });

            // The subsystem it is written down for opens...
            var channel = await ssh.OpenSubsystem(itsOwn);
            await channel.CloseAsync();

            // ...and neither the other one nor the command line does.
            Assert.That(async () => await ssh.OpenSubsystem(theOther), Throws.Exception, $"A VEN of the binding '{Binding}' got the subsystem {theOther}.");
            Assert.That(async () => await ssh.OpenShell(),             Throws.Exception, "A VEN got the command line of the grid operator.");

        }

        #endregion


        #region OpenADR_AfterTheGridOperatorRestarts_ALimitIsToldOverTheNewConnection(Transport)

        /// <summary>
        /// The grid operator restarts, and the subscriptions it held in its
        /// memory only are gone with it: the controller connects again, finds no
        /// subscription of its own over the new connection and subscribes anew -
        /// and a limit sent then reaches it by Notify, long before its next poll.
        /// </summary>
        [TestCase("ws")]
        [TestCase("ssh")]
        public async Task OpenADR_AfterTheGridOperatorRestarts_ALimitIsToldOverTheNewConnection(String Transport)
        {

            var controller = this.controller!;

            var (_, ven, connection, inbound) = await TheControllerConnects(authPassword, Transport, Binding: bindingMessages);

            await Until(() => this.grid!.Subscriptions.Of(ven).Count == 1, $"'{ven}' subscribing to the events over its connection", controller, this.grid);

            await RestartTheGridOperator();

            var grid = this.grid!;

            await Until(() => StatusOf(grid,       inbound)    == ConnectionStatus.Connected, "the controller connecting again to the grid operator restarted", controller, grid);
            await Until(() => StatusOf(controller, connection) == ConnectionStatus.Connected, "the controller seeing its connection up again",                 controller, grid);
            await Until(() => grid.Subscriptions.Of(ven).Count == 1,                           $"'{ven}' subscribing anew over the new connection",              controller, grid);

            var told = controller.OpenADRNotificationsReceived;

            Assert.That(grid.TrySendLimit(new JObject(new JProperty("kind",                   "IMPORT_CAPACITY_LIMIT"),
                                                      new JProperty("limitKW",                55),
                                                      new JProperty("durationMinutes",        60),
                                                      new JProperty("gridConnectionPointId",  PointOne)),
                                          "test", out _, out var error, out _),
                        Is.True, error);

            await Until(() => controller.ImportLimitNow?.KW == 55, "the limit reaching the controller by Notify over the new connection", controller, grid);

            Assert.That(controller.OpenADRNotificationsReceived, Is.GreaterThan(told), "The controller read the limit, and was not told of it.");

        }

        #endregion

        #region OpenADR_AfterTheControllerRestarts_ItIsSubscribedOnce(Transport)

        /// <summary>
        /// The controller restarts and connects again; the grid operator still
        /// holds the subscription it made over its connection before: the
        /// controller finds it and subscribes no second time - and a limit sent
        /// then reaches it by Notify over the new connection, once.
        /// </summary>
        [TestCase("ws")]
        [TestCase("ssh")]
        public async Task OpenADR_AfterTheControllerRestarts_ItIsSubscribedOnce(String Transport)
        {

            var grid = this.grid!;

            var (_, ven, connection, inbound) = await TheControllerConnects(authPassword, Transport, Binding: bindingMessages);

            await Until(() => grid.Subscriptions.Of(ven).Count == 1, $"'{ven}' subscribing to the events over its connection", this.controller, grid);

            await RestartTheController();

            var controller = this.controller!;

            await Until(() => StatusOf(controller, connection) == ConnectionStatus.Connected, "the controller restarted connecting again", controller, grid);
            await Until(() => StatusOf(grid,       inbound)    == ConnectionStatus.Connected, "the grid operator letting it in again",     controller, grid);
            await Until(() => controller.OpenADRLastPoll.Asked is not null,                     "the controller asking over the new connection", controller, grid);

            Assert.That(grid.Subscriptions.Of(ven).Count, Is.EqualTo(1), $"The controller subscribed a second time.{Story(controller, grid)}");

            Assert.That(grid.TrySendLimit(new JObject(new JProperty("kind",                   "IMPORT_CAPACITY_LIMIT"),
                                                      new JProperty("limitKW",                66),
                                                      new JProperty("durationMinutes",        60),
                                                      new JProperty("gridConnectionPointId",  PointOne)),
                                          "test", out _, out var error, out _),
                        Is.True, error);

            await Until(() => controller.ImportLimitNow?.KW == 66, "the limit reaching the controller restarted by Notify", controller, grid);

            Assert.That(controller.OpenADRNotificationsReceived, Is.GreaterThanOrEqualTo(1), "The controller read the limit, and was not told of it.");

        }

        #endregion

        #region OpenADR_AConnectionOfTheOtherBinding_IsNotLetIn(Transport, Binding)

        /// <summary>
        /// A connection the controller speaks in the one binding and the grid
        /// operator wrote down for the other is not let in - over a WebSocket its
        /// subprotocol is refused before the upgrade, over SSH the subsystem it
        /// asks for is not served to it - and the controller reads no limit.
        /// </summary>
        [TestCase("ws",  bindingREST)]
        [TestCase("ws",  bindingMessages)]
        [TestCase("ssh", bindingREST)]
        [TestCase("ssh", bindingMessages)]
        public async Task OpenADR_AConnectionOfTheOtherBinding_IsNotLetIn(String Transport, String Binding)
        {

            var grid        = this.grid!;
            var controller  = this.controller!;
            var theOther    = Binding == bindingREST ? bindingMessages : bindingREST;

            var (login, _, connection, inbound) = await TheControllerConnects(authPassword, Transport, Binding: Binding, InboundBinding: theOther);

            await Until(() => StatusOf(controller, connection) is ConnectionStatus.Refused or ConnectionStatus.Failed,
                        $"'{login}', speaking '{Binding}', being turned away from a connection written down for '{theOther}'",
                        controller, grid);

            Assert.That(grid.TrySendLimit(new JObject(new JProperty("kind",                   "IMPORT_CAPACITY_LIMIT"),
                                                      new JProperty("limitKW",                33),
                                                      new JProperty("durationMinutes",        60),
                                                      new JProperty("gridConnectionPointId",  PointOne)),
                                          "test", out _, out var error, out _),
                        Is.True, error);

            var problem = await controller.PollOpenADR();

            Assert.Multiple(() => {
                Assert.That(problem,                     Is.Not.Null,                                 "The controller asked the VTN through a connection of the other binding.");
                Assert.That(controller.ImportLimitNow,   Is.Null,                                     "The controller read a limit through a connection of the other binding.");
                Assert.That(StatusOf(grid, inbound),     Is.Not.EqualTo(ConnectionStatus.Connected),  "The grid operator let in a connection of the other binding.");
                Assert.That(IsConnectedAt(grid, login),  Is.False,                                    $"The grid operator let '{login}' in.");
            });

        }

        #endregion

        #region OpenADR_ARemovedVEN_ReadsNoLimit(Transport, Binding)

        /// <summary>
        /// A VEN the grid operator lets sign in no more reads no limit over its
        /// connection, open as it may be still - the limit sent to its point
        /// afterwards, for the other VEN there: what it asks is refused.
        /// </summary>
        [TestCase("ws",  bindingREST)]
        [TestCase("ws",  bindingMessages)]
        [TestCase("ssh", bindingREST)]
        [TestCase("ssh", bindingMessages)]
        public async Task OpenADR_ARemovedVEN_ReadsNoLimit(String Transport, String Binding)
        {

            var grid        = this.grid!;
            var controller  = this.controller!;

            var (_, ven, _, _) = await TheControllerConnects(authPassword, Transport, Binding: Binding);

            // Another VEN behind the same point, that the limit goes to still.
            AVEN(PointOne);

            Assert.That(grid.TryRemoveLocalController(ven, "test", out var error, out _), Is.True, error);

            Assert.That(grid.TrySendLimit(new JObject(new JProperty("kind",                   "IMPORT_CAPACITY_LIMIT"),
                                                      new JProperty("limitKW",                33),
                                                      new JProperty("durationMinutes",        60),
                                                      new JProperty("gridConnectionPointId",  PointOne)),
                                          "test", out _, out error, out _),
                        Is.True, error);

            var problem = await controller.PollOpenADR();

            Assert.Multiple(() => {
                Assert.That(problem,                    Is.Not.Null,  $"The VTN answered a VEN it lets sign in no more.{Story(controller, grid)}");
                Assert.That(controller.ImportLimitNow,  Is.Null,      "A VEN the grid operator lets sign in no more read the limit of its point.");
            });

        }

        #endregion

        #region OpenADR_AConnectionSwitchedOff_IsLetGoOf_AndOnAgain_LetsItIn(Transport, Binding)

        /// <summary>
        /// The grid operator switches off the connection a controller is in over:
        /// the controller is let go of at once - not when its connection drops
        /// some time - and is not let in again; switched on, it connects again
        /// and reads the limit of its point. Over SSH it connects again by
        /// itself; over a WebSocket the refusal of the door was final to it, as
        /// every refusal of a WebSocket is, and it connects again once it starts
        /// anew.
        /// </summary>
        [TestCase("ws",  bindingREST)]
        [TestCase("ws",  bindingMessages)]
        [TestCase("ssh", bindingREST)]
        [TestCase("ssh", bindingMessages)]
        public async Task OpenADR_AConnectionSwitchedOff_IsLetGoOf_AndOnAgain_LetsItIn(String Transport, String Binding)
        {

            var grid        = this.grid!;
            var controller  = this.controller!;

            var (login, _, connection, inbound) = await TheControllerConnects(authPassword, Transport, Binding: Binding);

            SwitchTheConnection(inbound, On: false);

            await Until(() => !IsConnectedAt(grid, login),                                   $"the grid operator letting go of '{login}'",            controller, grid);
            await Until(() => StatusOf(controller, connection) != ConnectionStatus.Connected, "the controller seeing its connection go",             controller, grid);

            // Long enough for the controller to have tried again, after three seconds.
            await Task.Delay(TimeSpan.FromSeconds(4));

            Assert.Multiple(() => {
                Assert.That(IsConnectedAt(grid, login),             Is.False,                                   $"The grid operator let '{login}' in again over a connection switched off.{Story(controller, grid)}");
                Assert.That(StatusOf(grid, inbound),                Is.Not.EqualTo(ConnectionStatus.Connected));
                Assert.That(StatusOf(controller, connection),       Is.Not.EqualTo(ConnectionStatus.Connected));
            });

            SwitchTheConnection(inbound, On: true);

            if (Transport != "ssh")
            {
                Assert.That(StatusOf(controller, connection), Is.EqualTo(ConnectionStatus.Refused), $"The refusal of the door was not final to the controller.{Story(controller, grid)}");
                await RestartTheController();
                controller = this.controller!;
            }

            await Until(() => StatusOf(controller, connection) == ConnectionStatus.Connected, "the controller connecting again once its connection is on", controller, grid);
            await Until(() => StatusOf(grid,       inbound)    == ConnectionStatus.Connected, "the grid operator letting it in again",                     controller, grid);

            Assert.That(grid.TrySendLimit(new JObject(new JProperty("kind",                   "IMPORT_CAPACITY_LIMIT"),
                                                      new JProperty("limitKW",                77),
                                                      new JProperty("durationMinutes",        60),
                                                      new JProperty("gridConnectionPointId",  PointOne)),
                                          "test", out _, out var error, out _),
                        Is.True, error);

            if (Binding == bindingREST)
                Assert.That(await controller.PollOpenADR(), Is.Null, "The controller did not ask the VTN through its connection, on again.");

            await Until(() => controller.ImportLimitNow?.KW == 77, "the controller reading the limit over its connection, on again", controller, grid);

        }

        #endregion

        #region OpenADR_WhatCameWhileTheControllerWasOut_IsToldOnceItIsIn(Transport)

        /// <summary>
        /// A controller subscribed over its connection stops; a limit sent to its
        /// point meanwhile waits at the grid operator, and is told to it by
        /// Notify once it is started and in again - and nothing waits then.
        /// </summary>
        [TestCase("ws")]
        [TestCase("ssh")]
        public async Task OpenADR_WhatCameWhileTheControllerWasOut_IsToldOnceItIsIn(String Transport)
        {

            var grid = this.grid!;

            var (_, ven, connection, inbound) = await TheControllerConnects(authPassword, Transport, Binding: bindingMessages);

            await Until(() => grid.Subscriptions.Of(ven).Count == 1, $"'{ven}' subscribing to the events over its connection", this.controller, grid);

            await StopTheController();

            await Until(() => StatusOf(grid, inbound) != ConnectionStatus.Connected, "the grid operator seeing the controller go", grid);

            Assert.That(grid.TrySendLimit(new JObject(new JProperty("kind",                   "IMPORT_CAPACITY_LIMIT"),
                                                      new JProperty("limitKW",                88),
                                                      new JProperty("durationMinutes",        60),
                                                      new JProperty("gridConnectionPointId",  PointOne)),
                                          "test", out _, out var error, out _),
                        Is.True, error);

            await Until(() => grid.NotificationsWaitingFor(inbound) == 1, "the limit waiting for the controller", grid);

            controller = NewController();

            await controller.Start();

            var started = controller;

            await Until(() => StatusOf(started, connection) == ConnectionStatus.Connected, "the controller connecting again",                    started, grid);
            await Until(() => grid.NotificationsWaitingFor(inbound) == 0,                  "the limit that waited being told to the controller", started, grid);
            await Until(() => started.OpenADRNotificationsReceived >= 1,                   "the controller being told of the limit that waited",   started, grid);
            await Until(() => started.ImportLimitNow?.KW == 88,                            "the controller reading the limit",                     started, grid);

        }

        #endregion

    }

}
