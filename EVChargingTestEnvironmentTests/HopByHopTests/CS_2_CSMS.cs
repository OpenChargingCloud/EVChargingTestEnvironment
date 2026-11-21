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

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Hermod.SSH;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.protocols.WWCP.Node.TestKit;
using cloud.charging.open.protocols.WWCP.Node.Connections;
using cloud.charging.open.protocols.OCPP.WebSockets;
using cloud.charging.open.protocols.OCPPv2_1;
using cloud.charging.open.protocols.OCPPv2_1.CS;

#endregion

namespace cloud.charging.open.TestEnvironment.Tests.Connections.HopByHop
{

    /// <summary>
    /// Test a connection between a charging station and a CSMS
    /// over a hop-by-hop connection using HTTP(S) WebSockets or SSH.
    /// </summary>
    [TestFixture]
    public class CS_2_CSMS : AHopByHopTest
    {

        #region Data

        private DNSClient?                        dnsClient;
        private ChargingStation.ChargingStation?  chargingStation;
        private CSMS.CSMS?                        csms;


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

            var csDirectory    = NewNodeDirectory("cs");

            chargingStation    = new ChargingStation.ChargingStation(
                                     DNSClient:        dnsClient,
                                     HTTPPort:         IPPort.Parse(TestPorts.Free()),
                                     NoKiosk:          true,
                                     AccountsPath:     csDirectory.Accounts,
                                     ConfigFile:       csDirectory.ConfigFile,
                                     LogToConsole:     false,
                                     BridgeDebugLog:   false
                                 );

            await chargingStation.Start();


            var csmsDirectory  = NewServedNodeDirectory("csms");

            csms               = new CSMS.CSMS(
                                     DNSClient:        dnsClient,
                                     HTTPPort:         IPPort.Parse(TestPorts.Free()),
                                     AccountsPath:     csmsDirectory.Accounts,
                                     ConfigFile:       csmsDirectory.ConfigFile,
                                     LogToConsole:     false,
                                     BridgeDebugLog:   false
                                 );

            await csms.Start();

        }


        [TearDown]
        public async Task Stop()
        {

            if (chargingStation is not null)
            {
                await chargingStation.Stop();
                await chargingStation.DisposeAsync();
            }

            if (csms is not null)
            {
                await csms.Stop();
                await csms.DisposeAsync();
            }

        }

        #endregion


        // An OCPP frame: [2, messageId, action, payload]
        private static Boolean IsBootNotification(JArray? JSON)
            => JSON is not null && JSON.Count > 2 && JSON[2]?.ToString() == "BootNotification";

        private static Boolean IsBootNotification(CBORValue? CBOR)
        {

            if (CBOR.HasValue && OCPP_CBORFrame.TryAsJSON(CBOR.Value, out var json, out _))
                return IsBootNotification(json);

            return false;

        }


        #region WebSocket     (Authentication, DataFormat)

        /// <summary>
        /// The charging station connects to the CSMS over a WebSocket connection.
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

            if (chargingStation is null)
            {
                Assert.Fail("The charging station is not running!");
                return;
            }

            if (csms is null)
            {
                Assert.Fail("The CSMS is not running!");
                return;
            }

            #endregion

            #region Hook up the CSMS WebSocketServer events

            var textMessages    = new ConcurrentLog<String>();
            var jsonMessages    = new ConcurrentLog<JArray>();
            var binaryMessages  = new ConcurrentLog<Byte[]>();
            var cborMessages    = new ConcurrentLog<CBORValue>();

            var server          = csms.StationServer!;

            server.OnTextMessageReceived   += (timestamp, srv, connection, frame,            eventTrackingId,               textMessage,   ct) => {
                textMessages.  Add(textMessage);
                return Task.CompletedTask;
            };

            server.OnJSONMessageReceived   += (timestamp, srv, connection, messageTimestamp, eventTrackingId, sourceNodeId, jsonMessage,   ct) => {
                jsonMessages.  Add(jsonMessage);
                return Task.CompletedTask;
            };

            server.OnBinaryMessageReceived += (timestamp, srv, connection, frame,            eventTrackingId,               binaryMessage, ct) => {
                binaryMessages.Add(binaryMessage);
                return Task.CompletedTask;
            };

            server.OnCBORMessageReceived   += (timestamp, srv, connection, messageTimestamp, eventTrackingId, sourceNodeId, cborMessage,   ct) => {
                cborMessages.  Add(cborMessage);
                return Task.CompletedTask;
            };

            #endregion


            var login        = "cs-" + RandomExtensions.RandomString(8);
            var description  = I18NString.Create($"{chargingStation.Id} --(webSocket/{Authentication}/{DataFormat})--> {csms.OCPP.NodeId}");

            String? credentials;
            String? error;

            switch (Authentication)
            {

                case authPassword:

                    var password = RandomExtensions.RandomString(32);

                    Assert.That(
                        csms.WebSocketLogins.TrySetPasswordAuthentication(
                            Login:         login,
                            Password:      password,
                            GroupId:       null,
                            Description:   description,
                            Error:         out error
                        ),
                        Is.True,
                        error
                    );

                    Assert.That(
                        chargingStation.Connections.TryAddPasswordAuthentication(
                            Login:         login,
                            Password:      password,
                            Description:   description,
                            Id:            out credentials,
                            Error:         out error
                        ),
                        Is.True,
                        error
                    );

                    break;


                case authTOTP:

                    var sharedSecret = RandomExtensions.RandomString(16);

                    Assert.That(
                        csms.WebSocketLogins.TrySetTOTPAuthentication(
                            Login:               login,
                            SharedSecret:        sharedSecret,
                            ValidityTime:        null,
                            Length:              null,
                            Alphabet:            null,
                            HashAlgorithm:       null,
                            GroupId:             null,
                            Description:         description,
                            Error:               out error
                        ),
                        Is.True,
                        error
                    );

                    Assert.That(
                        chargingStation.Connections.TryAddTOTPAuthentication(
                            Login:               login,
                            SharedSecret:        sharedSecret,
                            Description:         description,
                            Id:                  out credentials,
                            Error:               out error,
                            ValiditySeconds:     null,
                            Length:              null,
                            Alphabet:            null,
                            HashAlgorithm:       null,
                            TLSChannelBinding:   null
                        ),
                        Is.True,
                        error
                    );

                    break;


                default:
                    throw new ArgumentException($"'{Authentication}' is no way of signing in these tests know.", nameof(Authentication));

            }

            Assert.That(
                chargingStation.Connections.TryAddConnection(
                    Description:        description,
                    URL:                URL.Parse($"ws://{login}@127.0.0.1:{csms.OCPPServerSettings.TCPPort}"),
                    ConnectionType:     "CSMS",
                    AutoConnect:        true,
                    AuthenticationId:   credentials,
                    CertificateId:      null,
                    Id:                 out var connection,
                    Error:              out     error,
                    OCPPVersion:        null,
                    SshHostKey:         null,
                    SshTrust:           null,
                    ServerKeyPins:      null,
                    KeyPinTrust:        null,
                    Pins:               null,
                    HTTPVersion:        null,
                    Encoding:           DataFormat,
                    SignatureFormats:   null,
                    Identities:         null,
                    Inbound:            null,
                    RemoteNodeId:       null
                ),
                Is.True, error
            );


            await Until(() => StatusOf(chargingStation, connection!) == ConnectionStatus.Connected,  $"'{login}' connecting over webSocket",   chargingStation, csms);

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


            //var ocppV2_1 = (chargingStation.OCPPDialling.Node21 as IChargingStationNode)!;

            //var boot1  = await ocppV2_1.SendBootNotification(
            //                       BootReason:        BootReason.PowerUp
            //                   );

            //var boot2 = await ocppV2_1.OCPP.OUT.BootNotification(
            //               new protocols.OCPPv2_1.CS.BootNotificationRequest(
            //                   Destination:       protocols.WWCP.NetworkingNode.SourceRouting.To(protocols.WWCP.NetworkingNode.NetworkingNode_Id.CSMS),
            //                   ChargingStation:   new protocols.OCPPv2_1.ChargingStation(
            //                                          "Model",
            //                                          "Vendor"
            //                                      ),
            //                   Reason:            BootReason.PowerUp,
            //                   NetworkPath:       protocols.WWCP.NetworkingNode.NetworkPath.From(ocppV2_1.Id)
            //               )
            //           );

            await Until(() => RegistrationOf(chargingStation)        == "Accepted",                  $"'{login}' being accepted by the CSMS",  chargingStation, csms);

            //var auth = await ocppV2_1.OCPP.OUT.Authorize(
            //               new protocols.OCPPv2_1.CS.AuthorizeRequest(
            //                   Destination:   protocols.WWCP.NetworkingNode.SourceRouting.To(
            //                                      protocols.WWCP.NetworkingNode.NetworkingNode_Id.CSMS
            //                                  ),
            //                   IdToken:       IdToken.TryParseRFID7("04A2B3C4D5E6F7")!
            //               )
            //           );


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

        #region WebSocketTLS  (Authentication, DataFormat)

        /// <summary>
        /// The charging station connects to the CSMS over a WebSocket connection with TLS encryption.
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

            if (chargingStation is null)
            {
                Assert.Fail("The charging station is not running!");
                return;
            }

            if (csms is null)
            {
                Assert.Fail("The CSMS is not running!");
                return;
            }

            #endregion

            #region Hook up the CSMS WebSocketServer events

            var textMessages    = new ConcurrentLog<String>();
            var jsonMessages    = new ConcurrentLog<JArray>();
            var binaryMessages  = new ConcurrentLog<Byte[]>();
            var cborMessages    = new ConcurrentLog<CBORValue>();

            var server          = csms.StationServer!;

            server.OnTextMessageReceived   += (timestamp, srv, connection, frame,            eventTrackingId,               textMessage,   ct) => {
                textMessages.  Add(textMessage);
                return Task.CompletedTask;
            };

            server.OnJSONMessageReceived   += (timestamp, srv, connection, messageTimestamp, eventTrackingId, sourceNodeId, jsonMessage,   ct) => {
                jsonMessages.  Add(jsonMessage);
                return Task.CompletedTask;
            };

            server.OnBinaryMessageReceived += (timestamp, srv, connection, frame,            eventTrackingId,               binaryMessage, ct) => {
                binaryMessages.Add(binaryMessage);
                return Task.CompletedTask;
            };

            server.OnCBORMessageReceived   += (timestamp, srv, connection, messageTimestamp, eventTrackingId, sourceNodeId, cborMessage,   ct) => {
                cborMessages.  Add(cborMessage);
                return Task.CompletedTask;
            };

            #endregion


            var login        = "cs-" + RandomExtensions.RandomString(8);
            var description  = I18NString.Create($"{chargingStation.Id} --(webSocket TLS/{Authentication}/{DataFormat})--> {csms.OCPP.NodeId}");

            String? credentials;
            String? error;

            switch (Authentication)
            {

                case authPassword:

                    var password = RandomExtensions.RandomString(32);

                    Assert.That(
                        csms.WebSocketLogins.TrySetPasswordAuthentication(
                            Login:         login,
                            Password:      password,
                            GroupId:       null,
                            Description:   description,
                            Error:         out error
                        ),
                        Is.True,
                        error
                    );

                    Assert.That(
                        chargingStation.Connections.TryAddPasswordAuthentication(
                            Login:         login,
                            Password:      password,
                            Description:   description,
                            Id:            out credentials,
                            Error:         out error
                        ),
                        Is.True,
                        error
                    );

                    break;


                case authTOTP:

                    var sharedSecret = RandomExtensions.RandomString(16);

                    Assert.That(
                        csms.WebSocketLogins.TrySetTOTPAuthentication(
                            Login:               login,
                            SharedSecret:        sharedSecret,
                            ValidityTime:        null,
                            Length:              null,
                            Alphabet:            null,
                            HashAlgorithm:       null,
                            GroupId:             null,
                            Description:         description,
                            Error:               out error
                        ),
                        Is.True,
                        error
                    );

                    Assert.That(
                        chargingStation.Connections.TryAddTOTPAuthentication(
                            Login:               login,
                            SharedSecret:        sharedSecret,
                            Description:         description,
                            Id:                  out credentials,
                            Error:               out error,
                            ValiditySeconds:     null,
                            Length:              null,
                            Alphabet:            null,
                            HashAlgorithm:       null,
                            TLSChannelBinding:   null
                        ),
                        Is.True,
                        error
                    );

                    break;


                default:
                    throw new ArgumentException($"'{Authentication}' is no way of signing in these tests know.", nameof(Authentication));

            }


            BelievesTheTestCA(chargingStation);




            Assert.That(
                chargingStation.Connections.TryAddConnection(
                    Description:        description,
                    URL:                URL.Parse($"wss://{login}@127.0.0.1:{csms.OCPPServerSettings.TCPPort}"),
                    ConnectionType:     "CSMS",
                    AutoConnect:        true,
                    AuthenticationId:   credentials,
                    CertificateId:      null,
                    Id:                 out var connection,
                    Error:              out     error,
                    OCPPVersion:        null,
                    SshHostKey:         null,
                    SshTrust:           null,
                    ServerKeyPins:      null,
                    KeyPinTrust:        null,
                    Pins:               null,
                    HTTPVersion:        null,
                    Encoding:           DataFormat,
                    SignatureFormats:   null,
                    Identities:         null,
                    Inbound:            null,
                    RemoteNodeId:       null
                ),
                Is.True, error
            );


            await Until(() => StatusOf(chargingStation, connection!) == ConnectionStatus.Connected,  $"'{login}' connecting over webSocket TLS", chargingStation, csms);

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

        #region WebSocketMTLS (                DataFormat)

        /// <summary>
        /// The charging station connects to the CSMS over a WebSocket connection with mutual TLS authentication.
        /// </summary>
        /// <param name="DataFormat">The data format to use.</param>
        [TestCase("json")]
        [TestCase("cbor")]
        public async Task WebSocketMTLS(String DataFormat)
        {

            #region Initial checks

            if (chargingStation is null)
            {
                Assert.Fail("The charging station is not running!");
                return;
            }

            if (csms is null)
            {
                Assert.Fail("The CSMS is not running!");
                return;
            }

            #endregion

            #region Hook up the CSMS WebSocketServer events

            var textMessages    = new ConcurrentLog<String>();
            var jsonMessages    = new ConcurrentLog<JArray>();
            var binaryMessages  = new ConcurrentLog<Byte[]>();
            var cborMessages    = new ConcurrentLog<CBORValue>();

            var server          = csms.StationServer!;

            server.OnTextMessageReceived   += (timestamp, srv, connection, frame,            eventTrackingId,               textMessage,   ct) => {
                textMessages.  Add(textMessage);
                return Task.CompletedTask;
            };

            server.OnJSONMessageReceived   += (timestamp, srv, connection, messageTimestamp, eventTrackingId, sourceNodeId, jsonMessage,   ct) => {
                jsonMessages.  Add(jsonMessage);
                return Task.CompletedTask;
            };

            server.OnBinaryMessageReceived += (timestamp, srv, connection, frame,            eventTrackingId,               binaryMessage, ct) => {
                binaryMessages.Add(binaryMessage);
                return Task.CompletedTask;
            };

            server.OnCBORMessageReceived   += (timestamp, srv, connection, messageTimestamp, eventTrackingId, sourceNodeId, cborMessage,   ct) => {
                cborMessages.  Add(cborMessage);
                return Task.CompletedTask;
            };

            #endregion


            var login        = "cs-" + RandomExtensions.RandomString(8);
            var description  = I18NString.Create($"{chargingStation.Id} --(webSocket mTLS/{DataFormat})--> {csms.OCPP.NodeId}");

            // A certificate of the test's CA that names the login: the CSMS
            // knows the station by nothing else, so the URL names nobody.
            var certificate  = ClientCertificate(chargingStation, csms, login);

            BelievesTheTestCA(chargingStation);

            Assert.That(
                chargingStation.Connections.TryAddConnection(
                     description,
                     URL.Parse($"wss://127.0.0.1:{csms.OCPPServerSettings.TCPPort}"),
                     "CSMS",
                     true,
                     null,
                     certificate,
                     out var connection,
                     out var error,
                     Encoding: DataFormat
                ),
                Is.True, error
            );

            await Until(() => StatusOf(chargingStation, connection!) == ConnectionStatus.Connected,  $"'{login}' connecting over webSocket mTLS",  chargingStation, csms);
            await Until(() => IsConnectedAt(csms, login),                                            $"'{login}' being let in by its certificate", chargingStation, csms);

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
        /// The station dials the CSMS's SSH server, and pins the
        /// key the server shows: with its password, with a one-time password
        /// bound to the SSH session, or with its own key.
        /// </summary>
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

            if (chargingStation is null)
            {
                Assert.Fail("The charging station is not running!");
                return;
            }

            if (csms is null)
            {
                Assert.Fail("The CSMS is not running!");
                return;
            }

            #endregion

            #region Hook up the CSMS SSH server events

            var textMessages    = new ConcurrentLog<String>();
            var jsonMessages    = new ConcurrentLog<JArray>();
            var binaryMessages  = new ConcurrentLog<Byte[]>();
            var cborMessages    = new ConcurrentLog<CBORValue>();

            Assert.That(csms.StationSSHServer, Is.Not.Null, "The CSMS has no SSH server for its stations.");

            var server          = csms.StationSSHServer!;

            server.MessageServer.OnTextMessageReceived   += (timestamp, sender,    connection,                                                  textMessage,   ct) => {
                textMessages.  Add(textMessage);
                return Task.CompletedTask;
            };

            server.OnJSONMessageReceived                 += (timestamp, transport, connection, messageTimestamp, eventTrackingId, sourceNodeId, jsonMessage,   ct) => {
                jsonMessages.  Add(jsonMessage);
                return Task.CompletedTask;
            };

            server.MessageServer.OnBinaryMessageReceived += (timestamp, sender,    connection,                                                  binaryMessage, ct) => {
                binaryMessages.Add(binaryMessage);
                return Task.CompletedTask;
            };

            server.OnCBORMessageReceived                 += (timestamp, transport, connection, messageTimestamp, eventTrackingId, sourceNodeId, cborMessage,   ct) => {
                cborMessages.  Add(cborMessage);
                return Task.CompletedTask;
            };

            #endregion


            var login        = "cs-" + RandomExtensions.RandomString(8);
            var description  = I18NString.Create($"{chargingStation.Id} --(ssh/{Authentication}/{DataFormat})--> {csms.OCPP.NodeId}");
            var key          = await chargingStation.SshKey.MakeIfNeeded();
            var error        = "";
            String? credentials = null;

            Assert.That(csms.OCPPOverSSHServed, Is.True, "OCPP over SSH is switched on in the file, and not served.");

            // Its own key - or a password or the secret of one-time passwords,
            // and its own key then is one the controller does not know.

            switch (Authentication)
            {

                case authPassword:

                    var password = RandomExtensions.RandomString(32);

                    Assert.That(
                        csms.WebSocketLogins.TrySetPasswordAuthentication(
                            Login:         login,
                            Password:      password,
                            GroupId:       null,
                            Description:   description,
                            Error:         out error
                        ),
                        Is.True,
                        error
                    );

                    Assert.That(
                        chargingStation.Connections.TryAddPasswordAuthentication(
                            Login:         login,
                            Password:      password,
                            Description:   description,
                            Id:            out credentials,
                            Error:         out error
                        ),
                        Is.True,
                        error
                    );

                    break;


                case authTOTP:

                    var sharedSecret = RandomExtensions.RandomString(16);

                    Assert.That(
                        csms.WebSocketLogins.TrySetTOTPAuthentication(
                            Login:               login,
                            SharedSecret:        sharedSecret,
                            ValidityTime:        null,
                            Length:              null,
                            Alphabet:            null,
                            HashAlgorithm:       null,
                            GroupId:             null,
                            Description:         description,
                            Error:               out error
                        ),
                        Is.True,
                        error
                    );

                    Assert.That(
                        chargingStation.Connections.TryAddTOTPAuthentication(
                            Login:               login,
                            SharedSecret:        sharedSecret,
                            Description:         description,
                            Id:                  out credentials,
                            Error:               out error,
                            ValiditySeconds:     null,
                            Length:              null,
                            Alphabet:            null,
                            HashAlgorithm:       null,
                            TLSChannelBinding:   null
                        ),
                        Is.True,
                        error
                    );

                    break;


                case authPublicKey:
                    Assert.That(
                        csms.TryAddStationSSHKey(
                            Id:            login,
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
                    break;


                default:
                    throw new ArgumentException($"'{Authentication}' is no way of signing in these tests know.", nameof(Authentication));

            }

            Assert.That(
                chargingStation.Connections.TryAddConnection(
                    Description:        description,
                    URL:                URL.Parse($"ssh://{login}@127.0.0.1:{csms.SSHPort}"),
                    ConnectionType:     "CSMS",
                    AutoConnect:        true,
                    AuthenticationId:   credentials,
                    CertificateId:      null,
                    Id:                 out var connection,
                    Error:              out error,
                    OCPPVersion:        null,
                    SshHostKey:         csms.SSHHostKey!.Split(' ', 2)[1],
                    SshTrust:           null,
                    ServerKeyPins:      null,
                    KeyPinTrust:        null,
                    Pins:               null,
                    HTTPVersion:        null,
                    Encoding:           DataFormat,
                    SignatureFormats:   null,
                    Identities:         null,
                    Inbound:            null,
                    RemoteNodeId:       null
                ),
                Is.True, error
            );

            await Until(() => StatusOf(chargingStation, connection!) == ConnectionStatus.Connected, $"'{login}' connecting over SSH",           chargingStation, csms);
            await Until(() => IsConnectedAt(csms, login),                                           $"'{login}' being let in by the CSMS",      chargingStation, csms);

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
