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

        [TestCase(authPassword, formatJSON)]
        [TestCase(authPassword, formatCBOR)]
        [TestCase(authTOTP,     formatJSON)]
        [TestCase(authTOTP,     formatCBOR)]
        public async Task WebSocket(String  Authentication,
                                    String  DataFormat)
        {

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

            await Until(() => RegistrationOf(chargingStation)        == "Accepted",                  $"'{login}' being accepted by the CSMS",  chargingStation, csms);

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


    }

}
