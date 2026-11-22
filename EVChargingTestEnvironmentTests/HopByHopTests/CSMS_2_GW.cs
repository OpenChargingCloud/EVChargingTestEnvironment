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
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Hermod.SSH;

using cloud.charging.open.protocols.WWCP.Node.Certificates;
using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

using cloud.charging.open.Gateway;
using cloud.charging.open.protocols.WWCP.Node.Connections;
using cloud.charging.open.protocols.WWCP.Node.Logins;
using cloud.charging.open.protocols.WWCP.SSH;
using cloud.charging.open.protocols.WWCP.WebSockets;
using cloud.charging.open.protocols.OCPP.WebSockets;

using WWCPNode    = cloud.charging.open.protocols.WWCP.Node.WWCPNode;
//using Management  = cloud.charging.open.CSMS.CSMS;
//using Controller  = cloud.charging.open.Gateway.Gateway;
//using Station     = cloud.charging.open.Gateway.Gateway;
using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

#endregion

namespace cloud.charging.open.TestEnvironment.Tests.Connections.HopByHop
{

    /// <summary>
    /// Test a connection between a CSMS and a gateway, the CSMS connecting to the
    /// gateway - the one node that connects downwards - over a hop-by-hop
    /// connection using HTTP(S) WebSockets or SSH.
    /// </summary>
    [TestFixture]
    public class CSMS_2_GW : AHopByHopTest
    {

        #region Data

        private DNSClient?        dnsClient;
        private CSMS.CSMS?        csms;
        private Gateway.Gateway?  gateway;


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


            var gwDirectory    = NewServedNodeDirectory("gw");

            gateway            = new Gateway.Gateway(
                                     DNSClient:        dnsClient,
                                     HTTPPort:         TestPorts.Free(),
                                     AccountsPath:     gwDirectory.Accounts,
                                     ConfigFile:       gwDirectory.ConfigFile,
                                     LogToConsole:     false,
                                     BridgeDebugLog:   false
                                 );

            await gateway.Start();

        }

        [TearDown]
        public async Task Stop()
        {

            if (csms is not null)
            {
                await csms.Stop();
                await csms.DisposeAsync();
            }

            if (gateway is not null)
            {
                await gateway.Stop();
                await gateway.DisposeAsync();
            }

        }

        #endregion


        #region IsBootNotification(JSON/CBOR)

        // An OCPP request: [2, messageId, action, payload] - or, as the CSMS
        // and a node of its overlay network such as this one speak,
        // [2, destination, networkPath, messageId, action, payload]. The
        // action comes before the payload either way.
        private static Boolean IsBootNotification(JArray? JSON)
            => JSON?.Count >= 4 && JSON[JSON.Count - 2]?.ToString() == "BootNotification";

        private static Boolean IsBootNotification(CBORValue? CBOR)
        {

            if (CBOR.HasValue && OCPP_CBORFrame.TryAsJSON(CBOR.Value, out var json, out _))
                return IsBootNotification(json);

            return false;

        }

        #endregion

        #region TheWebSocketClientOf(CSMS)

        /// <summary>
        /// The WebSocket client the CSMS connected to the gateway with:
        /// what the gateway sends arrives there.
        /// </summary>
        /// <param name="CSMS">The CSMS.</param>
        private static IWWCPWebSocketClient TheWebSocketClientOf(CSMS.CSMS CSMS)
        {

            var clients = CSMS.OCPPConnecting.Node21.WWCPWebSocketClients.ToArray();

            Assert.That(clients, Has.Length.EqualTo(1), "The CSMS does not keep one WebSocket connection, to the gateway.");

            return clients[0];

        }

        #endregion

        #region TheSshClientOf(CSMS)

        /// <summary>
        /// The SSH client the CSMS connected to the gateway with: what the
        /// gateway sends arrives there.
        /// </summary>
        /// <param name="CSMS">The CSMS.</param>
        private static WWCPSshClient TheSshClientOf(CSMS.CSMS CSMS)
        {

            var clients = CSMS.OCPPConnecting.Node21.WWCPSshClients.ToArray();

            Assert.That(clients, Has.Length.EqualTo(1), "The CSMS does not keep one SSH connection, to the gateway.");

            return clients[0];

        }

        #endregion


        #region WebSocket     (Authentication, DataFormat)

        /// <summary>
        /// The CSMS connects to a gateway over a WebSocket connection.
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

            if (gateway is null)
            {
                Assert.Fail("The gateway is not running!");
                return;
            }

            #endregion


            var login        = User_Id.Parse("csms-" + RandomExtensions.RandomString(8));
            var node         = gateway.Node.Id.ToString();
            var nodeLogin    = User_Id.Parse(node);
            var description  = I18NString.Create($"{csms.OCPP.NodeId} --(webSocket/{Authentication}/{DataFormat})--> {node}");
            var door         = $"ws://127.0.0.1:{gateway.OCPPServerSettings.TCPPort}/{login}";
            var error        = "";

            // The gateway: the CSMS, written down as a connection it makes.
            Assert.That(
                gateway.Connections.TryAddConnection(
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
            Assert.That(csms.WebSocketLogins.TrySetGateway(nodeLogin, true, out error),                                                                               Is.True, error);

            Assert.That(
                csms.Connections.TryAddConnection(
                     description,
                     URL.Parse(door),
                     "Gateway",
                     true,
                     Credentials(csms, gateway, login, Authentication, description),
                     null,
                     out var connection,
                     out error,
                     Encoding:      DataFormat,
                     RemoteNodeId:  node
                ),
                Is.True, error
            );

            await Until(() => StatusOf(csms, connection!)         == ConnectionStatus.Connected,  $"'{login}' connecting via webSocket",             csms, gateway);
            await Until(() => StatusOf(gateway, inbound!) == ConnectionStatus.Connected,  $"'{login}' being let in by the gateway", csms, gateway);
            await Until(() => csms.IsConnected(node),                                     $"'{node}' counting as connected at the CSMS",     csms, gateway);

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

            _ = gateway.SendBootNotifications();

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

            await Until(() => RegistrationOf(gateway)     == "Accepted",                  $"'{node}' being accepted by the CSMS",            csms, gateway);


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
        /// The CSMS connects to a gateway over a WebSocket connection with TLS encryption.
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

            if (gateway is null)
            {
                Assert.Fail("The gateway is not running!");
                return;
            }

            #endregion


            var login        = User_Id.Parse("csms-" + RandomExtensions.RandomString(8));
            var node         = gateway.Node.Id.ToString();
            var nodeLogin    = User_Id.Parse(node);
            var description  = I18NString.Create($"{csms.OCPP.NodeId} --(webSocket TLS/{Authentication}/{DataFormat})--> {node}");
            var door         = $"wss://127.0.0.1:{gateway.OCPPServerSettings.TCPPort}/{login}";
            var error        = "";

            BelievesTheTestCA(csms);

            // The gateway: the CSMS, written down as a connection it makes.
            Assert.That(
                gateway.Connections.TryAddConnection(
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
            Assert.That(csms.WebSocketLogins.TrySetGateway(nodeLogin, true, out error),                                                                               Is.True, error);

            Assert.That(
                csms.Connections.TryAddConnection(
                     description,
                     URL.Parse(door),
                     "Gateway",
                     true,
                     Credentials(csms, gateway, login, Authentication, description),
                     null,
                     out var connection,
                     out error,
                     Encoding:      DataFormat,
                     RemoteNodeId:  node
                ),
                Is.True, error
            );

            await Until(() => StatusOf(csms, connection!) == ConnectionStatus.Connected,  $"'{login}' connecting via webSocket TLS",     csms, gateway);
            await Until(() => StatusOf(gateway, inbound!) == ConnectionStatus.Connected,  $"'{login}' being let in by the gateway",      csms, gateway);
            await Until(() => csms.IsConnected(node),                                     $"'{node}' counting as connected at the CSMS", csms, gateway);

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

            _ = gateway.SendBootNotifications();

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

            await Until(() => RegistrationOf(gateway)     == "Accepted",                  $"'{node}' being accepted by the CSMS",            csms, gateway);


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
        /// The CSMS connects to a gateway over a WebSocket connection with mutual TLS authentication.
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

            if (gateway is null)
            {
                Assert.Fail("The gateway is not running!");
                return;
            }

            #endregion


            var login        = User_Id.Parse("csms-" + RandomExtensions.RandomString(8));
            var node         = gateway.Node.Id.ToString();
            var nodeLogin    = User_Id.Parse(node);
            var description  = $"{csms.OCPP.NodeId} --(webSocket mTLS/{DataFormat})--> {node}";
            var door         = $"wss://127.0.0.1:{gateway.OCPPServerSettings.TCPPort}/{login}";
            var error        = "";

            // A certificate of the test's CA that names the login: the charging
            // station knows the CSMS by nothing else.
            var certificate  = ClientCertificate(csms, gateway, login.ToString());

            BelievesTheTestCA(csms);

            // The gateway: the CSMS, written down as a connection it makes.
            Assert.That(
                gateway.Connections.TryAddConnection(
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
            Assert.That(csms.WebSocketLogins.TrySetGateway(nodeLogin, true, out error),                                                                               Is.True, error);

            Assert.That(
                csms.Connections.TryAddConnection(
                     I18NString.Create(description),
                     URL.Parse(door),
                     "Gateway",
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

            await Until(() => StatusOf(csms, connection!)         == ConnectionStatus.Connected,  $"'{login}' connecting via webSocket mTLS",        csms, gateway);
            await Until(() => StatusOf(gateway, inbound!) == ConnectionStatus.Connected,  $"'{login}' being let in by its certificate",      csms, gateway);
            await Until(() => csms.IsConnected(node),                                     $"'{node}' counting as connected at the CSMS",     csms, gateway);

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

            _ = gateway.SendBootNotifications();

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

            await Until(() => RegistrationOf(gateway)     == "Accepted",                  $"'{node}' being accepted by the CSMS",            csms, gateway);


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
        /// The CSMS connects to the OCPP subsystem of the gateway's SSH
        /// server, and pins the key the server shows: with its password, with
        /// a one-time password, or with its own key. The gateway lets
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

            if (gateway is null)
            {
                Assert.Fail("The gateway is not running!");
                return;
            }

            #endregion


            var login        = User_Id.Parse("csms-" + RandomExtensions.RandomString(8));
            var node         = gateway.Node.Id.ToString();
            var nodeLogin    = User_Id.Parse(node);
            var description  = I18NString.Create($"{csms.OCPP.NodeId} --(ssh/{Authentication}/{DataFormat})--> {node}");
            var door         = $"ssh://{login}@127.0.0.1:{gateway.SSHPort}";
            var error        = "";

            Assert.That(gateway.OCPPOverSSHServed, Is.True, "OCPP-over-SSH is switched on in the file, and not served.");

            // The gateway: the CSMS, written down as a connection it
            // makes - over SSH, under the login it signs in with.
            Assert.That(
                gateway.Connections.TryAddConnection(
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
            Assert.That(csms.WebSocketLogins.TrySetGateway(nodeLogin, true, out error),                                                                               Is.True, error);

            String? credentials = null;

            if (Authentication == authPublicKey)
            {

                var key = await csms.SshKey.MakeIfNeeded();

                Assert.That(
                    gateway.TryAddStationSSHKey(
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
                credentials = Credentials(csms, gateway, login, Authentication, description);

            Assert.That(
                csms.Connections.TryAddConnection(
                    Description:        description,
                    URL:                URL.Parse(door),
                    ConnectionType:     "Gateway",
                    AutoConnect:        true,
                    AuthenticationId:   credentials,
                    CertificateId:      null,
                    Id:                 out var connection,
                    Error:              out error,
                    SshHostKey:         gateway.SSHHostKey!.Split(' ', 2)[1],
                    Encoding:           DataFormat,
                    RemoteNodeId:       node
                ),
                Is.True, error
            );

            await Until(() => StatusOf(csms, connection!) == ConnectionStatus.Connected,  $"'{login}' connecting via SSH",               csms, gateway);
            await Until(() => StatusOf(gateway, inbound!) == ConnectionStatus.Connected,  $"'{login}' being let in by the gateway",      csms, gateway);
            await Until(() => csms.IsConnected(node),                                     $"'{node}' counting as connected at the CSMS", csms, gateway);

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

            _ = gateway.SendBootNotifications();

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

            await Until(() => RegistrationOf(gateway)     == "Accepted",                  $"'{node}' being accepted by the CSMS",            csms, gateway);


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


    }

}
