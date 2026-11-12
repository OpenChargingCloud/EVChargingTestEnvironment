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
using System.Text;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.SSH;

using cloud.charging.open.protocols.WWCP;
using cloud.charging.open.protocols.WWCP.NetworkingNode;
using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.TestKit;
using cloud.charging.open.protocols.OCPP;
using cloud.charging.open.protocols.OCPP.SSH;
using cloud.charging.open.protocols.OCPPv2_1;
using cloud.charging.open.protocols.OCPPv2_1.CS;
using cloud.charging.open.protocols.OCPPv2_1.LC;

using WWCPNode    = cloud.charging.open.protocols.WWCP.Node.WWCPNode;
using OCPPv21Node = cloud.charging.open.protocols.OCPPv2_1.NetworkingNode.INetworkingNode;
using Management  = cloud.charging.open.CSMS.CSMS;
using Controller  = cloud.charging.open.LocalController.LocalController;
using Station     = cloud.charging.open.ChargingStation.ChargingStation;

#endregion

namespace cloud.charging.open.TestEnvironment.Tests
{

    /// <summary>
    /// OCPP over SSH from end to end, with a real local controller as the
    /// server: the tests of <see cref="OCPPOverSSHTests"/>, and the chain up
    /// to a CSMS.
    /// </summary>
    /// <remarks>
    /// The controller answers what its stations send to their CSMS - the
    /// BootNotification, the heartbeat, a binary data transfer - itself: its
    /// OCPP node takes "CSMS" as one of its own names, which is what the
    /// node's BootNotification handler was written for. That keeps the shared
    /// tests to the SSH leg below the controller. The whole chain - a
    /// controller that reports to a CSMS and forwards its stations' requests
    /// up - has two tests of its own, a station over SSH and one over a
    /// WebSocket (<see cref="AStationsRequest_ThroughTheController_IsAnsweredByTheCSMS"/>).
    /// </remarks>
    [TestFixture]
    public class LocalControllerOverSSHTests : OCPPOverSSHTests
    {

        #region Data

        /// <summary>
        /// Who the controller is at its CSMS: its OCPP node's default name.
        /// </summary>
        private const String  ControllerId    = "lc001";

        /// <summary>
        /// The controller's password at the CSMS.
        /// </summary>
        private const String  UplinkPassword  = "the-controllers-password-at-its-csms";

        private Management?   csms;
        private Controller?   controller;
        private String        controllerDirectory  = "";
        private IPPort        controllerHTTPPort;

        #endregion

        #region SetUp

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

        #endregion


        #region The server, as OCPPOverSSHTests asks for it

        protected override String         TheServer
            => "the controller";

        protected override String         ConnectionType
            => "localcontroller";

        protected override WWCPNode       Server
            => LC;

        protected override OCPPv21Node    OCPPNode
            => LC.Node;

        protected override OCPPSshServer  StationSSHServer
            => LC.StationSSHServer!;

        protected override UInt16         WebSocketPort
            => LC.OCPPServerSettings.TCPPort!.Value.ToUInt16();

        protected override IEnumerable<NetworkingNode_Id> WebSocketStations
            => LC.StationServer!.ConnectedNetworkingNodeIds;

        protected override void GiveTheKey(String Login, ISshHostKey Key, String? Group = null)
        {
            Assert.That(LC.TryAddStationSSHKey(Login, LineOf(Key), "test", Group, null, "tests", out _, out var error, out _),
                        Is.True, error);
        }

        protected override void TakeAwayTheKey(String Login, String Fingerprint)
        {
            Assert.That(LC.StationLogins.TryRemoveSSHKey(Login, Fingerprint, out var error), Is.True, error);
        }

        protected override void GiveThePassword(String Login, String Password)
        {
            Assert.That(LC.StationLogins.TrySetPassword(Login, Password, null, "tests", out _, out var error), Is.True, error);
        }

        protected override Task<TriggerMessageResponse> AskForABootNotification(String Login)

            => LC.Node.TriggerMessage(
                   Destination:       SourceRouting.To(NetworkingNode_Id.Parse(Login)),
                   RequestedMessage:  MessageTrigger.BootNotification
               );

        protected override async Task TheServerStops()
        {
            await LC.DisposeAsync();
            controller = null;
        }

        protected override async Task TheServerStartsAgain()
        {
            controller = NewController(controllerDirectory, null, controllerHTTPPort);
            AnswersForTheCSMS(controller);
            await controller.Start();
        }

        protected override async Task StopTheServers()
        {

            if (controller is not null)
                await controller.DisposeAsync();

            if (csms is not null)
                await csms.DisposeAsync();

            controller  = null;
            csms        = null;

        }

        protected override IEnumerable<(String Who, WWCPNode? Node)> Speakers
            => [ ("CSMS", csms), ("LC", controller) ];

        #endregion

        #region (private) The controller

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

        #endregion

        #region (private) The CSMS above it

        /// <summary>
        /// A CSMS whose station server listens on the loopback address, and
        /// which lets the controller in with its password.
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

            // A local controller's login: in the overlay network, which the
            // controller dials by default, what it forwards in the name of
            // its stations is taken only from a login marked as one.
            Assert.That(management.StationLogins.TrySetLocalController(ControllerId, true, out var markError), Is.True, markError);

            return management;

        }

        private Management CSMS
            => csms ?? throw new InvalidOperationException("No CSMS is running.");

        /// <summary>
        /// A CSMS, and in place of the fixture's controller one that reports
        /// to it and answers for nobody: what its stations send to their CSMS
        /// is forwarded. The actions of the requests the CSMS takes are kept.
        /// </summary>
        private async Task<ConcurrentQueue<String>> AControllerReportingToACSMS()
        {

            var csmsDirectory = TestParties.TemporaryDirectory("csms-ssh");
            directories.Add(csmsDirectory);

            csms = await TestPorts.StartedOnFreshPorts(() => NewCSMS(csmsDirectory));

            await LC.DisposeAsync();
            controller = null;

            controllerDirectory = TestParties.TemporaryDirectory("lc-ssh-uplink");
            directories.Add(controllerDirectory);

            controller = await TestPorts.StartedOnFreshPorts(() => NewController(controllerDirectory, ControllerConfiguration(CSMS)));

            await Until(() => LC.CSMSConnected, "The controller connecting to its CSMS");

            var atTheCSMS = new ConcurrentQueue<String>();

            CSMS.Node.OCPP.IN.OnJSONRequestMessageReceived   += (timestamp, sender, connection, request, cancellationToken) => {
                atTheCSMS.Enqueue(request.Action);
                return Task.CompletedTask;
            };

            CSMS.Node.OCPP.IN.OnBinaryRequestMessageReceived += (timestamp, sender, connection, request, cancellationToken) => {
                atTheCSMS.Enqueue(request.Action);
                return Task.CompletedTask;
            };

            return atTheCSMS;

        }

        /// <summary>
        /// A heartbeat and a status notification of the given station, both
        /// answered by the CSMS: requests the controller has no filter of its
        /// own for, which it answered "Filtered" itself until WWCP_OCPP made a
        /// local controller forward what no filter decides on.
        /// </summary>
        private async Task RequestsWithoutAFilter_AreAnsweredByTheCSMS(Station Station, ConcurrentQueue<String> AtTheCSMS)
        {

            var heartbeat  = await NodeOf(Station).SendHeartbeat();

            var status     = await NodeOf(Station).SendStatusNotification(
                                       EVSEId:       EVSE_Id.     Parse(1),
                                       ConnectorId:  Connector_Id.Parse(1),
                                       Timestamp:    DateTimeOffset.UtcNow,
                                       Status:       ConnectorStatus.Available
                                   );

            Assert.Multiple(() => {
                Assert.That(heartbeat.Result.ResultCode,  Is.EqualTo(ResultCode.OK),         $"The heartbeat: {heartbeat.Result}{Story(Station)}");
                Assert.That(status.   Result.ResultCode,  Is.EqualTo(ResultCode.OK),         $"The status notification: {status.Result}{Story(Station)}");
                Assert.That(AtTheCSMS,                    Does.Contain("Heartbeat"),          "The heartbeat did not reach the CSMS.");
                Assert.That(AtTheCSMS,                    Does.Contain("StatusNotification"), "The status notification did not reach the CSMS.");
            });

        }

        #endregion


        #region AStationsRequest_ThroughTheController_IsAnsweredByTheCSMS()

        /// <summary>
        /// The whole chain as a site has it, over SSH below the controller: a
        /// controller that reports to a CSMS forwards its station's
        /// BootNotification, a binary data transfer, a heartbeat and a status
        /// notification up, and the CSMS's answers find their way back down to
        /// the station.
        /// </summary>
        /// <remarks>
        /// Before WWCP_OCPP fixed it, the answer to a request a controller
        /// forwarded up was taken for the controller's own and lost: it comes
        /// back over the controller's client connection, and in standard
        /// networking mode names nobody.
        /// </remarks>
        [Test]
        public async Task AStationsRequest_ThroughTheController_IsAnsweredByTheCSMS()
        {

            var atTheCSMS     = await AControllerReportingToACSMS();

            var (station, _)  = await AStationDialling("cs001");

            await Until(() => atTheCSMS.Contains("BootNotification"),  "The station's BootNotification reaching the CSMS", station);
            await Until(() => RegistrationOf(station) == "Accepted",   "The CSMS's answer reaching the station",           station);

            var data          = Encoding.UTF8.GetBytes("Up to the CSMS and back");
            var transfer      = await NodeOf(station).TransferBinaryData(
                                          VendorId:             Vendor_Id. GraphDefined,
                                          MessageId:            Message_Id.GraphDefined_TestMessage,
                                          Data:                 data,
                                          SerializationFormat:  SerializationFormats.BinaryTextIds
                                      );

            Assert.Multiple(() => {
                Assert.That(atTheCSMS,                  Does.Contain("BinaryDataTransfer"),  "The binary data transfer did not reach the CSMS.");
                Assert.That(transfer.Result.ResultCode, Is.EqualTo(ResultCode.OK),           $"The binary data transfer: {transfer.Result}{Story(station)}");
                Assert.That(transfer.Data,              Is.EqualTo(data.AsEnumerable().Reverse().ToArray()));
            });

            await RequestsWithoutAFilter_AreAnsweredByTheCSMS(station, atTheCSMS);

        }

        #endregion

        #region AWebSocketStationsRequest_ThroughTheController_IsAnsweredByTheCSMS()

        /// <summary>
        /// The same chain with the station on the controller's WebSocket port:
        /// what was lost there was lost for every transport.
        /// </summary>
        [Test]
        public async Task AWebSocketStationsRequest_ThroughTheController_IsAnsweredByTheCSMS()
        {

            var atTheCSMS     = await AControllerReportingToACSMS();

            var (station, _)  = await AWebSocketStation("cs-ws");

            await Until(() => atTheCSMS.Contains("BootNotification"),  "The station's BootNotification reaching the CSMS", station);
            await Until(() => RegistrationOf(station) == "Accepted",   "The CSMS's answer reaching the station",           station);

            await RequestsWithoutAFilter_AreAnsweredByTheCSMS(station, atTheCSMS);

        }

        #endregion

    }

}
