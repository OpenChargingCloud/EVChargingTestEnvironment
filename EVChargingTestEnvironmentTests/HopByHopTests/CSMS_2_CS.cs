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

using System.Security.Cryptography;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Hermod.SSH;
using org.GraphDefined.Vanaheimr.Hermod.SSH.SFTP;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.protocols.WWCP.SSH;
using cloud.charging.open.protocols.WWCP.WebSockets;
using cloud.charging.open.protocols.WWCP.Node.TestKit;
using cloud.charging.open.protocols.WWCP.Node.Connections;
using cloud.charging.open.protocols.OCPP.WebSockets;

#endregion

namespace cloud.charging.open.TestEnvironment.Tests.Connections.HopByHop
{

    /// <summary>
    /// Test a connection between a CSMS and a charging station, the CSMS
    /// connecting to the charging station - the one node that connects downwards -
    /// over a hop-by-hop connection using HTTP(S) WebSockets or SSH.
    /// </summary>
    [TestFixture]
    public class CSMS_2_CS : AHopByHopTest
    {

        #region Data

        private DNSClient?                        dnsClient;
        private CSMS.CSMS?                        csms;
        private ChargingStation.ChargingStation?  chargingStation;
        private NodeDirectory?                    csDirectory;


        private const           String    KnownCard       = "04A2B3C4D5E6F7";
        private const           String    InvalidCard     = "04BBBBBBBBBBBB";
        private const           String    NobodysCard     = "04CCCCCCCCCCCC";

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public async Task Start()
        {

            dnsClient          = new DNSClient(
                                     SearchForIPv6DNSServers: false
                                 );

            var csmsDirectory  = NewNodeDirectory("csms");

            csms               = new CSMS.CSMS(
                                     DNSClient:        dnsClient,
                                     HTTPPort:         TestPorts.Free(),
                                     AccountsPath:     csmsDirectory.Accounts,
                                     ConfigFile:       csmsDirectory.ConfigFile,
                                     LogToConsole:     false,
                                     BridgeDebugLog:   false
                                 );

            await csms.Start();


            csDirectory        = NewServedNodeDirectory("cs");

            await StartTheChargingStation();

        }

        /// <summary>
        /// Start the charging station of its directory - once, or again after
        /// it was stopped: it reads what it kept there. For a test of SFTP it
        /// writes its log files, one a day, which it serves to read.
        /// </summary>
        private async Task StartTheChargingStation()
        {

            chargingStation    = new ChargingStation.ChargingStation(
                                     DNSClient:        dnsClient,
                                     HTTPPort:         TestPorts.Free(),
                                     NoKiosk:          true,
                                     AccountsPath:     csDirectory!.Accounts,
                                     ConfigFile:       csDirectory.ConfigFile,
                                     LogToConsole:     false,
                                     LogPath:          OverSFTP
                                                           ? Path.Combine(Path.GetDirectoryName(csDirectory.ConfigFile.Path)!, "logs")
                                                           : null,
                                     BridgeDebugLog:   false
                                 );

            await chargingStation.Start();

        }

        [TearDown]
        public async Task Stop()
        {

            if (csms is not null)
            {
                await csms.Stop();
                await csms.DisposeAsync();
            }

            if (chargingStation is not null)
            {
                await chargingStation.Stop();
                await chargingStation.DisposeAsync();
            }

        }

        #endregion


        #region IsBootNotification(JSON/CBOR)

        // An OCPP frame: [2, messageId, action, payload]
        private static Boolean IsBootNotification(JArray? JSON)
            => JSON?.Count > 2 && JSON[2]?.ToString() == "BootNotification";

        private static Boolean IsBootNotification(CBORValue? CBOR)
        {

            if (CBOR.HasValue && OCPP_CBORFrame.TryAsJSON(CBOR.Value, out var json, out _))
                return IsBootNotification(json);

            return false;

        }

        #endregion

        #region TheWebSocketClientOf(CSMS)

        /// <summary>
        /// The WebSocket client the CSMS connected to the charging station with:
        /// what the charging station sends arrives there.
        /// </summary>
        /// <param name="CSMS">The CSMS.</param>
        private static IWWCPWebSocketClient TheWebSocketClientOf(CSMS.CSMS CSMS)
        {

            var clients = CSMS.OCPPConnecting.Node21.WWCPWebSocketClients.ToArray();

            Assert.That(clients, Has.Length.EqualTo(1), "The CSMS does not keep one WebSocket connection, to the charging station.");

            return clients[0];

        }

        #endregion

        #region TheSshClientOf(CSMS)

        /// <summary>
        /// The SSH client the CSMS connected to the charging station with: what the
        /// charging station sends arrives there.
        /// </summary>
        /// <param name="CSMS">The CSMS.</param>
        private static WWCPSshClient TheSshClientOf(CSMS.CSMS CSMS)
        {

            var clients = CSMS.OCPPConnecting.Node21.WWCPSshClients.ToArray();

            Assert.That(clients, Has.Length.EqualTo(1), "The CSMS does not keep one SSH connection, to the charging station.");

            return clients[0];

        }

        #endregion


        #region WebSocket     (Authentication, DataFormat)

        /// <summary>
        /// The CSMS connects to a charging station over a WebSocket connection.
        /// </summary>
        /// <param name="Authentication">The authentication method to use.</param>
        /// <param name="DataFormat">The data format to use.</param>
        [TestCase(authPassword, formatJSON)]
        [TestCase(authPassword, formatCBOR)]
        [TestCase(authTOTP,     formatJSON)]
        [TestCase(authTOTP,     formatCBOR)]
        public async Task WebSocket(String  Authentication,
                                    String  DataFormat)
        {

            #region Initial checks

            if (csms is null)
            {
                Assert.Fail("The CSMS is not running!");
                return;
            }

            if (chargingStation is null)
            {
                Assert.Fail("The charging station is not running!");
                return;
            }

            #endregion


            var login        = User_Id.Parse("csms-" + RandomExtensions.RandomString(8));
            var node         = chargingStation.Id?.ToString() ?? ChargingStation.ChargingStation.DefaultOCPP21NodeId;
            var nodeLogin    = User_Id.Parse(node);
            var description  = I18NString.Create($"{csms.OCPP.NodeId} --(webSocket/{Authentication}/{DataFormat})--> {node}");
            var door         = $"ws://127.0.0.1:{chargingStation.OCPPServerSettings.TCPPort}/{login}";
            var error        = "";

            // The charging station: the CSMS, written down as a connection it makes.
            Assert.That(
                chargingStation.Connections.TryAddConnection(
                     I18NString.Create("The CSMS, connecting"),
                     URL.Parse(door),
                     "CSMS",
                     true,
                     null,
                     null,
                     out var inbound,
                     out error,
                     Inbound: true
                ),
                Is.True, error
            );

            // The CSMS: a login of the node it connects to, and the connection to it.
            Assert.That(csms.WebSocketLogins.TrySetPasswordAuthentication(nodeLogin, RandomExtensions.RandomString(32), null, I18NString.Create("tests"), out error), Is.True, error);

            Assert.That(
                csms.Connections.TryAddConnection(
                     description,
                     URL.Parse(door),
                     "ChargingStation",
                     true,
                     Credentials(csms, chargingStation, login, Authentication, description),
                     null,
                     out var connection,
                     out error,
                     Encoding:      DataFormat,
                     RemoteNodeId:  node
                ),
                Is.True, error
            );

            await Until(() => StatusOf(csms, connection!)         == ConnectionStatus.Connected,  $"'{login}' connecting via webSocket",             csms, chargingStation);
            await Until(() => StatusOf(chargingStation, inbound!) == ConnectionStatus.Connected,  $"'{login}' being let in by the charging station", csms, chargingStation);

            #region Hook up the events of the CSMS's WebSocket client

            var textMessages    = new ConcurrentLog<String>();
            var jsonMessages    = new ConcurrentLog<JArray>();
            var binaryMessages  = new ConcurrentLog<Byte[]>();
            var cborMessages    = new ConcurrentLog<CBORValue>();

            var client          = TheWebSocketClientOf(csms);

            client.OnTextMessageReceived   += (timestamp, webSocketClient, webSocketConnection, frame,            eventTrackingId,               textMessage,   ct) => {
                textMessages.  Add(textMessage);
                return Task.CompletedTask;
            };

            client.OnJSONMessageReceived   += (timestamp, webSocketClient, webSocketConnection, eventTrackingId, messageTimestamp, sourceNodeId, jsonMessage,   ct) => {
                jsonMessages.  Add(jsonMessage);
                return Task.CompletedTask;
            };

            client.OnBinaryMessageReceived += (timestamp, webSocketClient, webSocketConnection, frame,            eventTrackingId,               binaryMessage, ct) => {
                binaryMessages.Add(binaryMessage);
                return Task.CompletedTask;
            };

            client.OnCBORMessageReceived   += (timestamp, webSocketClient, webSocketConnection, eventTrackingId, messageTimestamp, sourceNodeId, cborMessage,   ct) => {
                cborMessages.  Add(cborMessage);
                return Task.CompletedTask;
            };

            #endregion

            // Sent by no node by itself: asked for, as after a reboot.
            var jsonFrom = jsonMessages.End;
            var cborFrom = cborMessages.End;

            _ = chargingStation.SendBootNotifications();

            var bootNotificationReceived = DataFormat == formatJSON

                                               ? (await jsonMessages.WaitFor(
                                                            message => IsBootNotification(message.Value),
                                                            TimeSpan.FromSeconds(30),
                                                            From: jsonFrom
                                                        ))?.Timestamp

                                               : (await cborMessages.WaitFor(
                                                            message => IsBootNotification(message.Value),
                                                            TimeSpan.FromSeconds(30),
                                                            From: cborFrom
                                                        ))?.Timestamp;

            Assert.That(bootNotificationReceived, Is.Not.Null, "No BootNotification received at the CSMS!");

            await Until(() => RegistrationOf(chargingStation)     == "Accepted",                  $"'{node}' being accepted by the CSMS",            csms, chargingStation);


            #region Validate received message numbers

            switch (DataFormat)
            {

                case formatJSON:
                    Assert.That(textMessages,   Has.Count.GreaterThanOrEqualTo(1),  "No text messages received at the CSMS!");
                    Assert.That(jsonMessages,   Has.Count.GreaterThanOrEqualTo(1),  "No JSON messages received at the CSMS!");
                    Assert.That(binaryMessages, Has.Count.EqualTo(0),               "Binary messages received at the CSMS!");
                    Assert.That(cborMessages,   Has.Count.EqualTo(0),               "CBOR messages received at the CSMS!");
                    break;

                case formatCBOR:
                    Assert.That(textMessages,   Has.Count.EqualTo(0),               "Text messages received at the CSMS!");
                    Assert.That(jsonMessages,   Has.Count.EqualTo(0),               "JSON messages received at the CSMS!");
                    Assert.That(binaryMessages, Has.Count.GreaterThanOrEqualTo(1),  "No binary messages received at the CSMS!");
                    Assert.That(cborMessages,   Has.Count.GreaterThanOrEqualTo(1),  "No CBOR messages received at the CSMS!");
                    break;

            }

            #endregion


            //var station = await AStationAtTheCSMS(Transport, "cs001");

            //Assert.That(atTheCSMS, Does.Contain("BootNotification"), $"The station's BootNotification did not reach the CSMS.{Story(station)}");

            //if (Transport == "certificate")
            //{

            //    var judgement = station.LastJudgementOf(Station.OCPPCertificateUsage, "127.0.0.1");

            //    Assert.Multiple(() => {
            //        Assert.That(CSMS.OCPPServerTLS,           Is.True,  "The station port of the CSMS does not speak TLS.");
            //        Assert.That(judgement?.Accepted,          Is.True,  $"The station did not believe the CSMS.{Story(station)}");
            //        Assert.That(judgement?.RootFingerprint,   Is.EqualTo(CertificateEntry.ThumbprintOf(ca!)),
            //                    "The CSMS was believed through something else than the operator's CA.");
            //        Assert.That(CSMS.StationCertificates.Get("cs001", "ChargingStationCertificate")?.Source, Is.EqualTo("presented"),
            //                    "The CSMS does not know the certificate the station signed in with.");
            //        Assert.That(CSMS.Log.Recent(200).Select(entry => entry.Message),
            //                    Has.Some.Contains("'cs001' signed in").And.Contains("with a certificate (security profile 3)"));
            //    });

            //}

            //await TheThreeCards(station, Transport);

        }

        #endregion

        #region WebSocketTLS  (Authentication, DataFormat)

        /// <summary>
        /// The CSMS connects to a charging station over a WebSocket connection with TLS encryption.
        /// </summary>
        /// <param name="Authentication">The authentication method to use.</param>
        /// <param name="DataFormat">The data format to use.</param>
        [TestCase(authPassword, formatJSON)]
        [TestCase(authPassword, formatCBOR)]
        [TestCase(authTOTP,     formatJSON)]
        [TestCase(authTOTP,     formatCBOR)]
        public async Task WebSocketTLS(String  Authentication,
                                       String  DataFormat)
        {

            #region Initial checks

            if (csms is null)
            {
                Assert.Fail("The CSMS is not running!");
                return;
            }

            if (chargingStation is null)
            {
                Assert.Fail("The charging station is not running!");
                return;
            }

            #endregion


            var login        = User_Id.Parse("csms-" + RandomExtensions.RandomString(8));
            var node         = chargingStation.Id?.ToString() ?? ChargingStation.ChargingStation.DefaultOCPP21NodeId;
            var nodeLogin    = User_Id.Parse(node);
            var description  = I18NString.Create($"{csms.OCPP.NodeId} --(webSocket TLS/{Authentication}/{DataFormat})--> {node}");
            var door         = $"wss://127.0.0.1:{chargingStation.OCPPServerSettings.TCPPort}/{login}";
            var error        = "";

            BelievesTheTestCA(csms);

            // The charging station: the CSMS, written down as a connection it makes.
            Assert.That(
                chargingStation.Connections.TryAddConnection(
                     I18NString.Create("The CSMS, connecting"),
                     URL.Parse(door),
                     "CSMS",
                     true,
                     null,
                     null,
                     out var inbound,
                     out error,
                     Inbound: true
                ),
                Is.True, error
            );

            // The CSMS: a login of the node it connects to, and the connection to it.
            Assert.That(csms.WebSocketLogins.TrySetPasswordAuthentication(nodeLogin, RandomExtensions.RandomString(32), null, I18NString.Create("tests"), out error), Is.True, error);

            Assert.That(
                csms.Connections.TryAddConnection(
                     description,
                     URL.Parse(door),
                     "ChargingStation",
                     true,
                     Credentials(csms, chargingStation, login, Authentication, description),
                     null,
                     out var connection,
                     out error,
                     Encoding:      DataFormat,
                     RemoteNodeId:  node
                ),
                Is.True, error
            );

            await Until(() => StatusOf(csms, connection!)         == ConnectionStatus.Connected,  $"'{login}' connecting via webSocket TLS",         csms, chargingStation);
            await Until(() => StatusOf(chargingStation, inbound!) == ConnectionStatus.Connected,  $"'{login}' being let in by the charging station", csms, chargingStation);

            #region Hook up the events of the CSMS's WebSocket client

            var textMessages    = new ConcurrentLog<String>();
            var jsonMessages    = new ConcurrentLog<JArray>();
            var binaryMessages  = new ConcurrentLog<Byte[]>();
            var cborMessages    = new ConcurrentLog<CBORValue>();

            var client          = TheWebSocketClientOf(csms);

            client.OnTextMessageReceived   += (timestamp, webSocketClient, webSocketConnection, frame,            eventTrackingId,               textMessage,   ct) => {
                textMessages.  Add(textMessage);
                return Task.CompletedTask;
            };

            client.OnJSONMessageReceived   += (timestamp, webSocketClient, webSocketConnection, eventTrackingId, messageTimestamp, sourceNodeId, jsonMessage,   ct) => {
                jsonMessages.  Add(jsonMessage);
                return Task.CompletedTask;
            };

            client.OnBinaryMessageReceived += (timestamp, webSocketClient, webSocketConnection, frame,            eventTrackingId,               binaryMessage, ct) => {
                binaryMessages.Add(binaryMessage);
                return Task.CompletedTask;
            };

            client.OnCBORMessageReceived   += (timestamp, webSocketClient, webSocketConnection, eventTrackingId, messageTimestamp, sourceNodeId, cborMessage,   ct) => {
                cborMessages.  Add(cborMessage);
                return Task.CompletedTask;
            };

            #endregion

            // Sent by no node by itself: asked for, as after a reboot.
            var jsonFrom = jsonMessages.End;
            var cborFrom = cborMessages.End;

            _ = chargingStation.SendBootNotifications();

            var bootNotificationReceived = DataFormat == formatJSON

                                               ? (await jsonMessages.WaitFor(
                                                            message => IsBootNotification(message.Value),
                                                            TimeSpan.FromSeconds(30),
                                                            From: jsonFrom
                                                        ))?.Timestamp

                                               : (await cborMessages.WaitFor(
                                                            message => IsBootNotification(message.Value),
                                                            TimeSpan.FromSeconds(30),
                                                            From: cborFrom
                                                        ))?.Timestamp;

            Assert.That(bootNotificationReceived, Is.Not.Null, "No BootNotification received at the CSMS!");

            await Until(() => RegistrationOf(chargingStation)     == "Accepted",                  $"'{node}' being accepted by the CSMS",            csms, chargingStation);


            #region Validate received message numbers

            switch (DataFormat)
            {

                case formatJSON:
                    Assert.That(textMessages,   Has.Count.GreaterThanOrEqualTo(1),  "No text messages received at the CSMS!");
                    Assert.That(jsonMessages,   Has.Count.GreaterThanOrEqualTo(1),  "No JSON messages received at the CSMS!");
                    Assert.That(binaryMessages, Has.Count.EqualTo(0),               "Binary messages received at the CSMS!");
                    Assert.That(cborMessages,   Has.Count.EqualTo(0),               "CBOR messages received at the CSMS!");
                    break;

                case formatCBOR:
                    Assert.That(textMessages,   Has.Count.EqualTo(0),               "Text messages received at the CSMS!");
                    Assert.That(jsonMessages,   Has.Count.EqualTo(0),               "JSON messages received at the CSMS!");
                    Assert.That(binaryMessages, Has.Count.GreaterThanOrEqualTo(1),  "No binary messages received at the CSMS!");
                    Assert.That(cborMessages,   Has.Count.GreaterThanOrEqualTo(1),  "No CBOR messages received at the CSMS!");
                    break;

            }

            #endregion


            //var station = await AStationAtTheCSMS(Transport, "cs001");

            //Assert.That(atTheCSMS, Does.Contain("BootNotification"), $"The station's BootNotification did not reach the CSMS.{Story(station)}");

            //if (Transport == "certificate")
            //{

            //    var judgement = station.LastJudgementOf(Station.OCPPCertificateUsage, "127.0.0.1");

            //    Assert.Multiple(() => {
            //        Assert.That(CSMS.OCPPServerTLS,           Is.True,  "The station port of the CSMS does not speak TLS.");
            //        Assert.That(judgement?.Accepted,          Is.True,  $"The station did not believe the CSMS.{Story(station)}");
            //        Assert.That(judgement?.RootFingerprint,   Is.EqualTo(CertificateEntry.ThumbprintOf(ca!)),
            //                    "The CSMS was believed through something else than the operator's CA.");
            //        Assert.That(CSMS.StationCertificates.Get("cs001", "ChargingStationCertificate")?.Source, Is.EqualTo("presented"),
            //                    "The CSMS does not know the certificate the station signed in with.");
            //        Assert.That(CSMS.Log.Recent(200).Select(entry => entry.Message),
            //                    Has.Some.Contains("'cs001' signed in").And.Contains("with a certificate (security profile 3)"));
            //    });

            //}

            //await TheThreeCards(station, Transport);

        }

        #endregion

        #region WebSocketMTLS (                DataFormat)

        /// <summary>
        /// The CSMS connects to a charging station over a WebSocket connection with mutual TLS authentication.
        /// </summary>
        /// <param name="DataFormat">The data format to use.</param>
        [TestCase(formatJSON)]
        [TestCase(formatCBOR)]
        public async Task WebSocketMTLS(String DataFormat)
        {

            #region Initial checks

            if (csms is null)
            {
                Assert.Fail("The CSMS is not running!");
                return;
            }

            if (chargingStation is null)
            {
                Assert.Fail("The charging station is not running!");
                return;
            }

            #endregion


            var login        = User_Id.Parse("csms-" + RandomExtensions.RandomString(8));
            var node         = chargingStation.Id?.ToString() ?? ChargingStation.ChargingStation.DefaultOCPP21NodeId;
            var nodeLogin    = User_Id.Parse(node);
            var description  = $"{csms.OCPP.NodeId} --(webSocket mTLS/{DataFormat})--> {node}";
            var door         = $"wss://127.0.0.1:{chargingStation.OCPPServerSettings.TCPPort}/{login}";
            var error        = "";

            // A certificate of the test's CA that names the login: the charging
            // station knows the CSMS by nothing else.
            var certificate  = ClientCertificate(csms, chargingStation, login.ToString());

            BelievesTheTestCA(csms);

            // The charging station: the CSMS, written down as a connection it makes.
            Assert.That(
                chargingStation.Connections.TryAddConnection(
                     I18NString.Create("The CSMS, connecting"),
                     URL.Parse(door),
                     "CSMS",
                     true,
                     null,
                     null,
                     out var inbound,
                     out error,
                     Inbound: true
                ),
                Is.True, error
            );

            // The CSMS: a login of the node it connects to, and the connection to it.
            Assert.That(csms.WebSocketLogins.TrySetPasswordAuthentication(nodeLogin, RandomExtensions.RandomString(32), null, I18NString.Create("tests"), out error), Is.True, error);

            Assert.That(
                csms.Connections.TryAddConnection(
                     I18NString.Create(description),
                     URL.Parse(door),
                     "ChargingStation",
                     true,
                     null,
                     certificate,
                     out var connection,
                     out error,
                     Encoding:      DataFormat,
                     RemoteNodeId:  node
                ),
                Is.True, error
            );

            await Until(() => StatusOf(csms, connection!)         == ConnectionStatus.Connected,  $"'{login}' connecting via webSocket mTLS",        csms, chargingStation);
            await Until(() => StatusOf(chargingStation, inbound!) == ConnectionStatus.Connected,  $"'{login}' being let in by its certificate",      csms, chargingStation);

            #region Hook up the events of the CSMS's WebSocket client

            var textMessages    = new ConcurrentLog<String>();
            var jsonMessages    = new ConcurrentLog<JArray>();
            var binaryMessages  = new ConcurrentLog<Byte[]>();
            var cborMessages    = new ConcurrentLog<CBORValue>();

            var client          = TheWebSocketClientOf(csms);

            client.OnTextMessageReceived   += (timestamp, webSocketClient, webSocketConnection, frame,            eventTrackingId,               textMessage,   ct) => {
                textMessages.  Add(textMessage);
                return Task.CompletedTask;
            };

            client.OnJSONMessageReceived   += (timestamp, webSocketClient, webSocketConnection, eventTrackingId, messageTimestamp, sourceNodeId, jsonMessage,   ct) => {
                jsonMessages.  Add(jsonMessage);
                return Task.CompletedTask;
            };

            client.OnBinaryMessageReceived += (timestamp, webSocketClient, webSocketConnection, frame,            eventTrackingId,               binaryMessage, ct) => {
                binaryMessages.Add(binaryMessage);
                return Task.CompletedTask;
            };

            client.OnCBORMessageReceived   += (timestamp, webSocketClient, webSocketConnection, eventTrackingId, messageTimestamp, sourceNodeId, cborMessage,   ct) => {
                cborMessages.  Add(cborMessage);
                return Task.CompletedTask;
            };

            #endregion

            // Sent by no node by itself: asked for, as after a reboot.
            var jsonFrom = jsonMessages.End;
            var cborFrom = cborMessages.End;

            _ = chargingStation.SendBootNotifications();

            var bootNotificationReceived = DataFormat == formatJSON

                                               ? (await jsonMessages.WaitFor(
                                                            message => IsBootNotification(message.Value),
                                                            TimeSpan.FromSeconds(30),
                                                            From: jsonFrom
                                                        ))?.Timestamp

                                               : (await cborMessages.WaitFor(
                                                            message => IsBootNotification(message.Value),
                                                            TimeSpan.FromSeconds(30),
                                                            From: cborFrom
                                                        ))?.Timestamp;

            Assert.That(bootNotificationReceived, Is.Not.Null, "No BootNotification received at the CSMS!");

            await Until(() => RegistrationOf(chargingStation)     == "Accepted",                  $"'{node}' being accepted by the CSMS",            csms, chargingStation);


            #region Validate received message numbers

            switch (DataFormat)
            {

                case formatJSON:
                    Assert.That(textMessages,   Has.Count.GreaterThanOrEqualTo(1),  "No text messages received at the CSMS!");
                    Assert.That(jsonMessages,   Has.Count.GreaterThanOrEqualTo(1),  "No JSON messages received at the CSMS!");
                    Assert.That(binaryMessages, Has.Count.EqualTo(0),               "Binary messages received at the CSMS!");
                    Assert.That(cborMessages,   Has.Count.EqualTo(0),               "CBOR messages received at the CSMS!");
                    break;

                case formatCBOR:
                    Assert.That(textMessages,   Has.Count.EqualTo(0),               "Text messages received at the CSMS!");
                    Assert.That(jsonMessages,   Has.Count.EqualTo(0),               "JSON messages received at the CSMS!");
                    Assert.That(binaryMessages, Has.Count.GreaterThanOrEqualTo(1),  "No binary messages received at the CSMS!");
                    Assert.That(cborMessages,   Has.Count.GreaterThanOrEqualTo(1),  "No CBOR messages received at the CSMS!");
                    break;

            }

            #endregion

        }

        #endregion


        #region SSH           (Authentication, DataFormat)

        /// <summary>
        /// The CSMS connects to the OCPP subsystem of the charging station's SSH
        /// server, and pins the key the server shows: with its password, with
        /// a one-time password, or with its own key. The charging station lets
        /// it in as a connection the other end makes, under the login it signs
        /// in to SSH with.
        /// </summary>
        /// <param name="Authentication">The authentication method to use.</param>
        /// <param name="DataFormat">The data format to use.</param>
        [TestCase(authPassword,  formatJSON)]
        [TestCase(authPassword,  formatCBOR)]
        [TestCase(authTOTP,      formatJSON)]
        [TestCase(authTOTP,      formatCBOR)]
        [TestCase(authPublicKey, formatJSON)]
        [TestCase(authPublicKey, formatCBOR)]
        public async Task SSH(String  Authentication,
                              String  DataFormat)
        {

            #region Initial checks

            if (csms is null)
            {
                Assert.Fail("The CSMS is not running!");
                return;
            }

            if (chargingStation is null)
            {
                Assert.Fail("The charging station is not running!");
                return;
            }

            #endregion


            var login        = User_Id.Parse("csms-" + RandomExtensions.RandomString(8));
            var node         = chargingStation.Id?.ToString() ?? ChargingStation.ChargingStation.DefaultOCPP21NodeId;
            var nodeLogin    = User_Id.Parse(node);
            var description  = I18NString.Create($"{csms.OCPP.NodeId} --(ssh/{Authentication}/{DataFormat})--> {node}");
            var door         = $"ssh://{login}@127.0.0.1:{chargingStation.SSHPort}";
            var error        = "";

            Assert.That(chargingStation.OCPPOverSSHServed, Is.True, "OCPP-over-SSH is switched on in the file, and not served.");

            // The charging station: the CSMS, written down as a connection it
            // makes - over SSH, under the login it signs in with.
            Assert.That(
                chargingStation.Connections.TryAddConnection(
                     I18NString.Create("The CSMS, connecting via SSH"),
                     URL.Parse(door),
                     "CSMS",
                     true,
                     null,
                     null,
                     out var inbound,
                     out error,
                     Inbound: true
                ),
                Is.True, error
            );

            // The CSMS: a login of the node it connects to, what it signs in with -
            // its password, the secret of its one-time passwords, or its own
            // key -, and the connection to it.
            Assert.That(csms.WebSocketLogins.TrySetPasswordAuthentication(nodeLogin, RandomExtensions.RandomString(32), null, I18NString.Create("tests"), out error), Is.True, error);

            String? credentials = null;

            if (Authentication == authPublicKey)
            {

                var key = await csms.SshKey.MakeIfNeeded();

                Assert.That(
                    chargingStation.TryAddStationSSHKey(
                        Login:         login,
                        Line:          SshPublicKey.FromHostKey(key).ToAuthorizedKeyLine(),
                        Label:         "test",
                        GroupId:       null,
                        Description:   description,
                        CreatedBy:     "tests",
                        Key:           out _,
                        Error:         out error,
                        NotSaved:      out _
                    ),
                    Is.True, error
                );

            }

            else
                credentials = Credentials(csms, chargingStation, login, Authentication, description);

            Assert.That(
                csms.Connections.TryAddConnection(
                    Description:        description,
                    URL:                URL.Parse(door),
                    ConnectionType:     "ChargingStation",
                    AutoConnect:        true,
                    AuthenticationId:   credentials,
                    CertificateId:      null,
                    Id:                 out var connection,
                    Error:              out error,
                    SshHostKey:         chargingStation.SSHHostKey!.Split(' ', 2)[1],
                    Encoding:           DataFormat,
                    RemoteNodeId:       node
                ),
                Is.True, error
            );

            await Until(() => StatusOf(csms, connection!)         == ConnectionStatus.Connected,  $"'{login}' connecting via SSH",                   csms, chargingStation);
            await Until(() => StatusOf(chargingStation, inbound!) == ConnectionStatus.Connected,  $"'{login}' being let in by the charging station", csms, chargingStation);

            #region Hook up the events of the CSMS's SSH client

            var textMessages    = new ConcurrentLog<String>();
            var jsonMessages    = new ConcurrentLog<JArray>();
            var binaryMessages  = new ConcurrentLog<Byte[]>();
            var cborMessages    = new ConcurrentLog<CBORValue>();

            var client          = TheSshClientOf(csms);

            // As the messages arrive: the SSH message client of Hermod underneath.
            client.MessageClient.OnTextMessageReceived   += (timestamp, sender,    sshConnection,                                                  textMessage,   ct) => {
                textMessages.  Add(textMessage);
                return Task.CompletedTask;
            };

            client.MessageClient.OnBinaryMessageReceived += (timestamp, sender,    sshConnection,                                                  binaryMessage, ct) => {
                binaryMessages.Add(binaryMessage);
                return Task.CompletedTask;
            };

            // As they are read: the WWCP client.
            client.OnJSONMessageReceived                 += (timestamp, transport, sshConnection, messageTimestamp, eventTrackingId, sourceNodeId, jsonMessage,   ct) => {
                jsonMessages.  Add(jsonMessage);
                return Task.CompletedTask;
            };

            client.OnCBORMessageReceived                 += (timestamp, transport, sshConnection, messageTimestamp, eventTrackingId, sourceNodeId, cborMessage,   ct) => {
                cborMessages.  Add(cborMessage);
                return Task.CompletedTask;
            };

            #endregion

            // Sent by no node by itself: asked for, as after a reboot.
            var jsonFrom = jsonMessages.End;
            var cborFrom = cborMessages.End;

            _ = chargingStation.SendBootNotifications();

            var bootNotificationReceived = DataFormat == formatJSON

                                               ? (await jsonMessages.WaitFor(
                                                            message => IsBootNotification(message.Value),
                                                            TimeSpan.FromSeconds(30),
                                                            From: jsonFrom
                                                        ))?.Timestamp

                                               : (await cborMessages.WaitFor(
                                                            message => IsBootNotification(message.Value),
                                                            TimeSpan.FromSeconds(30),
                                                            From: cborFrom
                                                        ))?.Timestamp;

            Assert.That(bootNotificationReceived, Is.Not.Null, "No BootNotification received at the CSMS!");

            await Until(() => RegistrationOf(chargingStation)     == "Accepted",                  $"'{node}' being accepted by the CSMS",            csms, chargingStation);


            #region Validate received message numbers

            switch (DataFormat)
            {

                case formatJSON:
                    Assert.That(textMessages,   Has.Count.GreaterThanOrEqualTo(1),  "No text messages received at the CSMS!");
                    Assert.That(jsonMessages,   Has.Count.GreaterThanOrEqualTo(1),  "No JSON messages received at the CSMS!");
                    Assert.That(binaryMessages, Has.Count.EqualTo(0),               "Binary messages received at the CSMS!");
                    Assert.That(cborMessages,   Has.Count.EqualTo(0),               "CBOR messages received at the CSMS!");
                    break;

                case formatCBOR:
                    Assert.That(textMessages,   Has.Count.EqualTo(0),               "Text messages received at the CSMS!");
                    Assert.That(jsonMessages,   Has.Count.EqualTo(0),               "JSON messages received at the CSMS!");
                    Assert.That(binaryMessages, Has.Count.GreaterThanOrEqualTo(1),  "No binary messages received at the CSMS!");
                    Assert.That(cborMessages,   Has.Count.GreaterThanOrEqualTo(1),  "No CBOR messages received at the CSMS!");
                    break;

            }

            #endregion

        }

        #endregion



        // SFTP

        #region (private) TheCSMSConnectsViaSSH(Authentication)

        /// <summary>
        /// The CSMS connects to the charging station via SSH, as in
        /// <see cref="SSH"/> - with its password, with a one-time password, or
        /// with its own key -, and both sides say it is connected.
        /// </summary>
        /// <param name="Authentication">The authentication method to use.</param>
        /// <returns>The login it comes under, its connection, and the connection the charging station wrote down for it.</returns>
        private async Task<(User_Id Login, String Connection, String Inbound)> TheCSMSConnectsViaSSH(String Authentication)
        {

            var login        = User_Id.Parse("csms-" + RandomExtensions.RandomString(8));
            var node         = chargingStation!.Id?.ToString() ?? ChargingStation.ChargingStation.DefaultOCPP21NodeId;
            var nodeLogin    = User_Id.Parse(node);
            var description  = I18NString.Create($"{csms!.OCPP.NodeId} --(ssh/{Authentication})--> {node}");
            var door         = $"ssh://{login}@127.0.0.1:{chargingStation.SSHPort}";
            var error        = "";

            Assert.That(chargingStation.SFTPEnabled, Is.True, "SFTP is switched on in the file, and not served.");

            Assert.That(
                chargingStation.Connections.TryAddConnection(
                     I18NString.Create("The CSMS, connecting via SSH"),
                     URL.Parse(door),
                     "CSMS",
                     true,
                     null,
                     null,
                     out var inbound,
                     out error,
                     Inbound: true
                ),
                Is.True, error
            );

            Assert.That(csms.WebSocketLogins.TrySetPasswordAuthentication(nodeLogin, RandomExtensions.RandomString(32), null, I18NString.Create("tests"), out error), Is.True, error);

            // What the CSMS signs in with: its password, the secret of its
            // one-time passwords, or its own key.
            String? credentials = null;

            if (Authentication == authPublicKey)
            {

                var key = await csms.SshKey.MakeIfNeeded();

                Assert.That(
                    chargingStation.TryAddStationSSHKey(
                        Login:         login,
                        Line:          SshPublicKey.FromHostKey(key).ToAuthorizedKeyLine(),
                        Label:         "test",
                        GroupId:       null,
                        Description:   description,
                        CreatedBy:     "tests",
                        Key:           out _,
                        Error:         out error,
                        NotSaved:      out _
                    ),
                    Is.True, error
                );

            }

            else
                credentials = Credentials(csms, chargingStation, login, Authentication, description);

            Assert.That(
                csms.Connections.TryAddConnection(
                    Description:        description,
                    URL:                URL.Parse(door),
                    ConnectionType:     "ChargingStation",
                    AutoConnect:        true,
                    AuthenticationId:   credentials,
                    CertificateId:      null,
                    Id:                 out var connection,
                    Error:              out error,
                    SshHostKey:         chargingStation.SSHHostKey!.Split(' ', 2)[1],
                    RemoteNodeId:       node
                ),
                Is.True, error
            );

            await Until(() => StatusOf(csms, connection!)            == ConnectionStatus.Connected,  $"'{login}' connecting via SSH",                   csms, chargingStation);
            await Until(() => StatusOf(chargingStation, inbound!)    == ConnectionStatus.Connected,  $"'{login}' being let in by the charging station", csms, chargingStation);

            return (login, connection!, inbound!);

        }

        #endregion

        #region (private) SFTPOfTheCSMS()

        /// <summary>
        /// SFTP of the CSMS at the charging station: one more channel of the
        /// SSH connection it speaks OCPP over - once that is up.
        /// </summary>
        private async Task<SFTPClient> SFTPOfTheCSMS()
        {

            await Until(() => csms!.OCPPConnecting.Node21.WWCPSshClients.Any(client => client.MessageClient.Ssh is not null),
                        "The SSH connection of the CSMS being up",
                        csms, chargingStation);

            var sshClient = csms!.OCPPConnecting.Node21.WWCPSshClients.
                                Select       (client => client.MessageClient.Ssh).
                                LastOrDefault(client => client is not null)!;

            return await sshClient.OpenSFTPClient();

        }

        #endregion


        #region (private static) NamesIn(SFTPClient, Path)

        /// <summary>
        /// The names in the given folder over SFTP, "." and ".." left out.
        /// </summary>
        private static async Task<String[]> NamesIn(SFTPClient  SFTPClient,
                                                    String      Path)

            => [.. (await SFTPClient.ListDirectoryAsync(Path)).
                       Select(entry => entry.Name).
                       Where (name  => name is not "." and not "..").
                       Order (StringComparer.Ordinal)];

        #endregion

        #region (private static) ReadWhileWritten(File)

        /// <summary>
        /// What a file holds now that its node may still be writing - a log
        /// file of the day.
        /// </summary>
        private static Byte[] ReadWhileWritten(String File)
        {
            using var file = new FileStream(File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var read = new MemoryStream();
            file.CopyTo(read);
            return read.ToArray();
        }

        #endregion

        #region (private static) Refused(Code, What)

        /// <summary>
        /// The code an SFTP operation was refused with - or null where it was
        /// carried out.
        /// </summary>
        private static async Task<SftpStatusCode?> Refused(Func<Task> What)
        {
            try
            {
                await What();
                return null;
            }
            catch (SftpException refused)
            {
                return refused.Code;
            }
        }

        #endregion


        #region SFTP_UploadsAFirmwareImage_AndSeesItAgain(Authentication)

        /// <summary>
        /// The CSMS connects to the charging station via SSH and opens SFTP beside
        /// OCPP, over the same connection: it sees a tree of its own, and in
        /// it its own files, and puts up a firmware image in folders it makes.
        /// The charging station keeps it on its disk, in the directory of the
        /// CSMS's login beside its configuration file. Neither of them takes
        /// anything away, so the CSMS sees its files and folders again: over
        /// SFTP anew, and after the charging station was restarted and the
        /// CSMS connected again by itself.
        /// </summary>
        /// <param name="Authentication">The authentication method to use.</param>
        [TestCase(authPassword)]
        [TestCase(authTOTP)]
        [TestCase(authPublicKey)]
        public async Task SFTP_UploadsAFirmwareImage_AndSeesItAgain(String Authentication)
        {

            #region Initial checks

            if (csms is null)
            {
                Assert.Fail("The CSMS is not running!");
                return;
            }

            if (chargingStation is null)
            {
                Assert.Fail("The charging station is not running!");
                return;
            }

            #endregion


            var (login, connection, inbound) = await TheCSMSConnectsViaSSH(Authentication);

            var image   = RandomNumberGenerator.GetBytes(300_000);   // more than one chunk
            var notes   = "Firmware 2.1.0: what is new."u8.ToArray();

            #region Put up the firmware image

            await using (var sftp = await SFTPOfTheCSMS())
            {

                Assert.That(await NamesIn(sftp, "/"),      Is.EqualTo([ "files", "logs" ]), "The CSMS sees more, or less, than its own files and the log files.");
                Assert.That(await NamesIn(sftp, "/files"), Is.Empty,                        "The files of the CSMS are not its own: there is something there already.");

                await sftp.MakeDirectoryAsync("/files/firmware");
                await sftp.MakeDirectoryAsync("/files/firmware/2.1.0");

                await sftp.UploadAsync       ("/files/firmware/2.1.0/image.bin",    image);
                await sftp.UploadAsync       ("/files/firmware/2.1.0/release.txt",  notes);

                // Nothing above its own files, and nothing outside them.
                var intoTheRoot   = await Refused(async () => await sftp.UploadAsync("/image.bin", image));
                var aFolderThere  = await Refused(async () => await sftp.MakeDirectoryAsync("/firmware"));
                var outOfItsTree  = await Refused(async () => await sftp.StatAsync("/files/../../configuration.json"));

                Assert.Multiple(() => {
                    Assert.That(intoTheRoot,   Is.EqualTo(SftpStatusCode.PermissionDenied), "The CSMS put a file into the root of its tree.");
                    Assert.That(aFolderThere,  Is.EqualTo(SftpStatusCode.PermissionDenied), "The CSMS made a folder in the root of its tree.");
                    Assert.That(outOfItsTree,  Is.EqualTo(SftpStatusCode.NoSuchFile),       "The CSMS reached out of its tree.");
                });

            }

            #endregion

            #region On the disk of the charging station

            var directory = chargingStation.SftpDirectoryOf(login);

            Assert.Multiple(() => {
                Assert.That(Path.GetDirectoryName(directory),                                               Is.EqualTo(Path.Combine(Path.GetDirectoryName(csDirectory!.ConfigFile.Path)!, "sftp")));
                Assert.That(File.ReadAllBytes(Path.Combine(directory, "firmware", "2.1.0", "image.bin")),   Is.EqualTo(image), "The firmware image is not on the disk of the charging station, as it was put up.");
                Assert.That(File.ReadAllBytes(Path.Combine(directory, "firmware", "2.1.0", "release.txt")), Is.EqualTo(notes));
            });

            #endregion

            #region What the CSMS sees again

            async Task ItSeesItsFilesAgain(String When)
            {

                await using var sftp = await SFTPOfTheCSMS();

                Assert.That(await NamesIn(sftp, "/files"),                Is.EqualTo(new[] { "firmware" }),                   $"The CSMS does not see its folder again {When}.");
                Assert.That(await NamesIn(sftp, "/files/firmware"),       Is.EqualTo(new[] { "2.1.0" }),                      $"The CSMS does not see its folder again {When}.");
                Assert.That(await NamesIn(sftp, "/files/firmware/2.1.0"), Is.EqualTo(new[] { "image.bin", "release.txt" }),   $"The CSMS does not see its files again {When}.");

                Assert.That((await sftp.StatAsync("/files/firmware/2.1.0/image.bin")).Size, Is.EqualTo(image.Length));
                Assert.That(await sftp.DownloadAsync("/files/firmware/2.1.0/image.bin"),    Is.EqualTo(image),                $"The firmware image is not what was put up, {When}.");

            }

            #endregion

            // Over SFTP anew, over the same connection.
            await ItSeesItsFilesAgain("over SFTP anew");

            // The charging station restarted: the CSMS connects again by itself.
            await chargingStation.Stop();
            await chargingStation.DisposeAsync();

            await StartTheChargingStation();

            await Until(() => StatusOf(chargingStation, inbound)    == ConnectionStatus.Connected,  $"'{login}' being let in again by the restarted charging station", csms, chargingStation);
            await Until(() => StatusOf(csms,            connection) == ConnectionStatus.Connected,  $"'{login}' connecting to the restarted charging station again",   csms, chargingStation);

            await ItSeesItsFilesAgain("after the charging station restarted, and the CSMS connected again");

        }

        #endregion

        #region SFTP_DownloadsALogFile_ButMayNotRemoveIt(Authentication)

        /// <summary>
        /// The CSMS connects to the charging station via SSH, opens SFTP beside
        /// OCPP and downloads a log file of the charging station - what it said
        /// up to then. It may not remove it, nor rename, replace or add one:
        /// the log files are evidence, and stay as they are.
        /// </summary>
        /// <param name="Authentication">The authentication method to use.</param>
        [TestCase(authPassword)]
        [TestCase(authTOTP)]
        [TestCase(authPublicKey)]
        public async Task SFTP_DownloadsALogFile_ButMayNotRemoveIt(String Authentication)
        {

            #region Initial checks

            if (csms is null)
            {
                Assert.Fail("The CSMS is not running!");
                return;
            }

            if (chargingStation is null)
            {
                Assert.Fail("The charging station is not running!");
                return;
            }

            #endregion


            var (login, _, _) = await TheCSMSConnectsViaSSH(Authentication);

            await using var sftp = await SFTPOfTheCSMS();

            var logs     = await NamesIn(sftp, "/logs");

            Assert.That(logs, Is.Not.Empty, "The CSMS sees no log file of the charging station.");

            var name     = logs.Last();
            var path     = $"/logs/{name}";
            var onDisk   = Path.Combine(chargingStation.LogPath!, name);

            var log      = await sftp.DownloadAsync(path);
            var now      = ReadWhileWritten(onDisk);

            Assert.Multiple(() => {
                Assert.That(log,                                      Is.Not.Empty,         "The log file came down empty.");
                Assert.That(now.Take(log.Length),                     Is.EqualTo(log),      "What came down is not what the charging station wrote.");
                Assert.That(System.Text.Encoding.UTF8.GetString(log), Does.Contain(login.ToString()),  "The log file does not tell of the CSMS that connected.");
            });

            // Not removed, renamed, replaced or added to.
            var removed        = await Refused(async () => await sftp.RemoveAsync(path));
            var renamed        = await Refused(async () => await sftp.RenameAsync(path, "/files/" + name));
            var writtenOver    = await Refused(async () => await sftp.UploadAsync(path, "nothing happened"u8.ToArray()));
            var forged         = await Refused(async () => await sftp.UploadAsync("/logs/forged.log", log));
            var folderRemoved  = await Refused(async () => await sftp.RemoveDirectoryAsync("/logs"));

            Assert.Multiple(() => {
                Assert.That(removed,        Is.EqualTo(SftpStatusCode.PermissionDenied), "The CSMS removed a log file.");
                Assert.That(renamed,        Is.EqualTo(SftpStatusCode.PermissionDenied), "The CSMS renamed a log file.");
                Assert.That(writtenOver,    Is.EqualTo(SftpStatusCode.PermissionDenied), "The CSMS wrote over a log file.");
                Assert.That(forged,         Is.EqualTo(SftpStatusCode.PermissionDenied), "The CSMS put a log file there.");
                Assert.That(folderRemoved,  Is.EqualTo(SftpStatusCode.PermissionDenied), "The CSMS removed the folder of the log files.");
            });

            Assert.Multiple(() => {
                Assert.That(File.Exists(onDisk),                                                Is.True,          "The log file is gone from the disk of the charging station.");
                Assert.That(ReadWhileWritten(onDisk).Take(log.Length),                          Is.EqualTo(log),  "The log file on the disk is not what it was.");
                Assert.That(File.Exists(Path.Combine(chargingStation.LogPath!, "forged.log")),  Is.False,         "A log file was put on the disk of the charging station.");
            });

            // What it may do in its own files, it may do there.
            await sftp.UploadAsync("/files/" + name, log);

            Assert.That(await Refused(async () => await sftp.RemoveAsync("/files/" + name)), Is.Null, "The CSMS may not remove its own copy of the log file.");

        }

        #endregion


    }

}
