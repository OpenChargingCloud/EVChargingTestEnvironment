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

using cloud.charging.open.ChargingStation;
using cloud.charging.open.protocols.WWCP.Node.Connections;
using cloud.charging.open.protocols.WWCP.Node.Logins;

using WWCPNode    = cloud.charging.open.protocols.WWCP.Node.WWCPNode;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using cloud.charging.open.protocols.OCPPv2_1;
using cloud.charging.open.protocols.OCPPv2_1.NetworkingNode;
using cloud.charging.open.protocols.OCPPv2_1.CS;

#endregion

namespace cloud.charging.open.TestEnvironment.Tests.Connections.HopByHop
{

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

            var textMessages    = new List<String>();
            var jsonMessages    = new List<JArray>();
            var binaryMessages  = new List<Byte[]>();
            var cborMessages    = new List<CBORValue>();

            var server          = csms.StationServer!;

            server.OnTextMessageReceived   += (timestamp, srv, connection, frame,            eventTrackingId,               textMessage,   ct) => {
                textMessages.Add(textMessage);
                return Task.CompletedTask;
            };

            server.OnJSONMessageReceived   += (timestamp, srv, connection, messageTimestamp, eventTrackingId, sourceNodeId, jsonMessage,   ct) => {
                jsonMessages.Add(jsonMessage);
                return Task.CompletedTask;
            };

            server.OnBinaryMessageReceived += (timestamp, srv, connection, frame,            eventTrackingId,               binaryMessage, ct) => {
                binaryMessages.Add(binaryMessage);
                return Task.CompletedTask;
            };

            server.OnCBORMessageReceived   += (timestamp, srv, connection, messageTimestamp, eventTrackingId, sourceNodeId, cborMessage,   ct) => {
                cborMessages.Add(cborMessage);
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
                    description,
                    URL.Parse($"ws://{login}@127.0.0.1:{csms.OCPPServerSettings.TCPPort}"),
                    "CSMS",
                    true,
                    credentials,
                    null,
                    out var connection,
                    out     error,
                    Encoding: DataFormat
                ),
                Is.True, error
            );

            await Until(() => StatusOf(chargingStation, connection!) == ConnectionStatus.Connected,  $"'{login}' connecting over webSocket",   chargingStation, csms);

            // Sent by no node by itself: asked for, as after a reboot.
            _ = chargingStation.SendBootNotifications();

            var ocppV2_1 = (chargingStation.OCPPDialling.Node21 as IChargingStationNode)!;

            var boot1  = await ocppV2_1.SendBootNotification(
                                   BootReason:        BootReason.PowerUp
                               );


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


            var login        = "cs-" + RandomExtensions.RandomString(8);
            var description  = $"{chargingStation.Id} --(webSocket TLS/{Authentication}/{DataFormat})--> {csms.OCPP.NodeId}";
            var credentials  = Credentials(chargingStation, csms, login, Authentication, description);

            BelievesTheTestCA(chargingStation);

            Assert.That(
                chargingStation.Connections.TryAddConnection(
                     I18NString.Create(description),
                     URL.Parse($"wss://{login}@127.0.0.1:{csms.OCPPServerSettings.TCPPort}"),
                     "CSMS",
                     true,
                     credentials,
                     null,
                     out var connection,
                     out var error,
                     Encoding: DataFormat
                ),
                Is.True, error
            );

            await Until(() => StatusOf(chargingStation, connection!) == ConnectionStatus.Connected,  $"'{login}' connecting over webSocket TLS", chargingStation, csms);

            // Sent by no node by itself: asked for, as after a reboot.
            _ = chargingStation.SendBootNotifications();

            await Until(() => RegistrationOf(chargingStation)        == "Accepted",                  $"'{login}' being accepted by the CSMS",    chargingStation, csms);


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

        #region WebSocketMTLS (DataFormat)

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


            var login        = "cs-" + RandomExtensions.RandomString(8);
            var description  = $"{chargingStation.Id} --(webSocket mTLS/{DataFormat})--> {csms.OCPP.NodeId}";

            // A certificate of the test's CA that names the login: the CSMS
            // knows the station by nothing else, so the URL names nobody.
            var certificate  = ClientCertificate(chargingStation, csms, login);

            BelievesTheTestCA(chargingStation);

            Assert.That(
                chargingStation.Connections.TryAddConnection(
                     I18NString.Create(description),
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

            await Until(() => StatusOf(chargingStation, connection!) == ConnectionStatus.Connected,  $"'{login}' connecting over webSocket mTLS", chargingStation, csms);
            await Until(() => IsConnectedAt(csms, login),                                            $"'{login}' being let in by its certificate", chargingStation, csms);

            // Sent by no node by itself: asked for, as after a reboot.
            _ = chargingStation.SendBootNotifications();

            await Until(() => RegistrationOf(chargingStation)        == "Accepted",                  $"'{login}' being accepted by the CSMS",     chargingStation, csms);


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



        #region SSH(Authentication, DataFormat)

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

            var textMessages    = new List<String>();
            var jsonMessages    = new List<JArray>();
            var binaryMessages  = new List<Byte[]>();
            var cborMessages    = new List<CBORValue>();

            Assert.That(csms.StationSSHServer, Is.Not.Null, "The CSMS has no SSH server for its stations.");

            var server          = csms.StationSSHServer!;

            // As the messages arrive: the SSH message server of Hermod underneath.
            server.MessageServer.OnTextMessageReceived   += (timestamp, sender,    connection,                                                 textMessage,   ct) => {
                lock (textMessages)
                    textMessages.Add(textMessage);
                return Task.CompletedTask;
            };

            server.MessageServer.OnBinaryMessageReceived += (timestamp, sender,    connection,                                                 binaryMessage, ct) => {
                lock (binaryMessages)
                    binaryMessages.Add(binaryMessage);
                return Task.CompletedTask;
            };

            // As they are read: the WWCP server.
            server.OnJSONMessageReceived                 += (timestamp, transport, connection, messageTimestamp, eventTrackingId, sourceNodeId, jsonMessage,   ct) => {
                lock (jsonMessages)
                    jsonMessages.Add(jsonMessage);
                return Task.CompletedTask;
            };

            server.OnCBORMessageReceived                 += (timestamp, transport, connection, messageTimestamp, eventTrackingId, sourceNodeId, cborMessage,   ct) => {
                lock (cborMessages)
                    cborMessages.Add(cborMessage);
                return Task.CompletedTask;
            };

            #endregion


            var login        = "cs-" + RandomExtensions.RandomString(8);
            var description  = $"{chargingStation.Id} --(ssh/{Authentication}/{DataFormat})--> {csms.OCPP.NodeId}";
            var key          = await chargingStation.SshKey.MakeIfNeeded();
            var error        = "";
            String? credentials = null;

            Assert.That(csms.OCPPOverSSHServed, Is.True, "OCPP over SSH is switched on in the file, and not served.");

            // Its own key - or a password or the secret of one-time passwords,
            // and its own key then is one the controller does not know.
            if (Authentication == authPublicKey)
                Assert.That(
                    csms.TryAddStationSSHKey(
                        login,
                        SshPublicKey.FromHostKey(key).ToAuthorizedKeyLine(),
                        "test",
                        null,
                        null,
                        "tests",
                        out _,
                        out error,
                        out _
                    ),
                    Is.True, error
                );

            else
                credentials = Credentials(chargingStation, csms, login, Authentication, description);

            Assert.That(
                chargingStation.Connections.TryAddConnection(
                     I18NString.Create(description),
                     URL.Parse($"ssh://{login}@127.0.0.1:{csms.SSHPort!.Value.ToUInt16()}"),
                     "CSMS",
                     true,
                     credentials,
                     null,
                     out var connection,
                     out error,
                     SshHostKey:  csms.SSHHostKey!.Split(' ', 2)[1],
                     Encoding:    DataFormat
                ),
                Is.True, error
            );

            await Until(() => StatusOf(chargingStation, connection!) == ConnectionStatus.Connected, $"'{login}' connecting over SSH",           chargingStation, csms);
            await Until(() => IsConnectedAt(csms, login),                                           $"'{login}' being let in by the CSMS",      chargingStation, csms);

            // Sent by no node by itself: asked for, as after a reboot.
            _ = chargingStation.SendBootNotifications();

            await Until(() => RegistrationOf(chargingStation)        == "Accepted",                  $"'{login}' being accepted by the CSMS",    chargingStation, csms);


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
