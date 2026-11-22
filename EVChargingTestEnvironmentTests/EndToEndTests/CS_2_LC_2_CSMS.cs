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
//using Management  = cloud.charging.open.CSMS.CSMS;
//using Controller  = cloud.charging.open.LocalController.LocalController;
//using Station     = cloud.charging.open.ChargingStation.ChargingStation;
using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

#endregion

namespace cloud.charging.open.TestEnvironment.Tests.Connections.HopByHop
{

    [TestFixture]
    public class CS_2_LC_2_CSMS : AHopByHopTest
    {

        #region Data

        private DNSClient?                        dnsClient;
        private ChargingStation.ChargingStation?  chargingStation;
        private LocalController.LocalController?  localController;
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
                                     HTTPPort:         TestPorts.Free(),
                                     NoKiosk:          true,
                                     AccountsPath:     csDirectory.Accounts,
                                     ConfigFile:       csDirectory.ConfigFile,
                                     LogToConsole:     false,
                                     BridgeDebugLog:   false
                                 );

            await chargingStation.Start();


            // The local controller reports upwards, to the CSMS above it.
            var lcDirectory    = NewServedNodeDirectory("lc", ReportsUpwards());

            localController    = new LocalController.LocalController(
                                     DNSClient:        dnsClient,
                                     HTTPPort:         TestPorts.Free(),
                                     AccountsPath:     lcDirectory.Accounts,
                                     ConfigFile:       lcDirectory.ConfigFile,
                                     LogToConsole:     false,
                                     BridgeDebugLog:   false
                                 );

            await localController.Start();


            // The local controller connects to the CSMS, with a password or
            // one-time passwords - whatever the station below signs in with.
            var csmsDirectory  = NewServedNodeDirectory("csms", OverTLS ? "httpAuthOverTLS" : "httpAuthWithoutTLS");

            csms               = new CSMS.CSMS(
                                     DNSClient:        dnsClient,
                                     HTTPPort:         TestPorts.Free(),
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

            if (localController is not null)
            {
                await localController.Stop();
                await localController.DisposeAsync();
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
        /// The station connects to the local controller and is let in. Whether it is accepted is
        /// for the CSMS above the local controller to say, and there is none here: so it
        /// is the line, at both its ends, that is tested.
        /// </summary>
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

            if (localController is null)
            {
                Assert.Fail("The local controller is not running!");
                return;
            }

            if (csms is null)
            {
                Assert.Fail("The CSMS is not running!");
                return;
            }


            var login        = User_Id.Parse("cs-" + RandomExtensions.RandomString(8));
            var description  = I18NString.Create($"{chargingStation.Id} --(webSocket/{Authentication}/{DataFormat})--> {localController.OCPP.NodeId}");

            var credentials  = Credentials(chargingStation, localController, login, Authentication, description);

            BelievesTheTestCA(chargingStation);

            Assert.That(
                chargingStation.Connections.TryAddConnection(
                     description,
                     URL.Parse($"ws://{login}@127.0.0.1:{localController.OCPPServerSettings.TCPPort}"),
                     "LocalController",
                     true,
                     credentials,
                     null,
                     out var connection,
                     out var error
                ),
                Is.True, error
            );

            await Until(() => StatusOf(chargingStation, connection!) == ConnectionStatus.Connected, $"'{login}' connecting over webSocket",            chargingStation, localController);
            await Until(() => IsConnectedAt(localController, login),                                $"'{login}' being let in by the local controller", chargingStation, localController);


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
        /// The station connects to the local controller and is let in. Whether it is accepted is
        /// for the CSMS above the local controller to say, and there is none here: so it
        /// is the line, at both its ends, that is tested.
        /// </summary>
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

            if (localController is null)
            {
                Assert.Fail("The local controller is not running!");
                return;
            }

            if (csms is null)
            {
                Assert.Fail("The CSMS is not running!");
                return;
            }


            var login        = User_Id.Parse("cs-" + RandomExtensions.RandomString(8));
            var description  = I18NString.Create($"{chargingStation.Id} --(webSocketTLS/{Authentication}/{DataFormat})--> {localController.OCPP.NodeId}");

            var credentials  = Credentials(chargingStation, localController, login, Authentication, description);

            BelievesTheTestCA(chargingStation);

            Assert.That(
                chargingStation.Connections.TryAddConnection(
                     description,
                     URL.Parse($"wss://{login}@127.0.0.1:{localController.OCPPServerSettings.TCPPort}"),
                     "LocalController",
                     true,
                     credentials,
                     null,
                     out var connection,
                     out var error
                ),
                Is.True, error
            );

            await Until(() => StatusOf(chargingStation, connection!) == ConnectionStatus.Connected, $"'{login}' connecting over webSocketTLS",         chargingStation, localController);
            await Until(() => IsConnectedAt(localController, login),                                $"'{login}' being let in by the local controller", chargingStation, localController);


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

        #region WebSocketMTLS (Authentication, DataFormat)

        /// <summary>
        /// The whole chain: the local controller connects to the CSMS over TLS with a
        /// password or one-time passwords, the station connects to the local
        /// controller over mutual TLS with a certificate of its own - and is
        /// accepted by the CSMS above the local controller. Both lines speak
        /// the given data format.
        /// </summary>
        /// <param name="Authentication">How the local controller signs in to the CSMS. The station signs in with its certificate, which says who it is by itself.</param>
        /// <param name="DataFormat">What both lines are written in, JSON or CBOR.</param>
        [TestCase(authPassword, formatJSON)]
        [TestCase(authPassword, formatCBOR)]
        [TestCase(authTOTP,     formatJSON)]
        [TestCase(authTOTP,     formatCBOR)]
        public async Task WebSocketMTLS(String  Authentication,
                                        String  DataFormat)
        {

            if (chargingStation is null)
            {
                Assert.Fail("The charging station is not running!");
                return;
            }

            if (localController is null)
            {
                Assert.Fail("The local controller is not running!");
                return;
            }

            if (csms is null)
            {
                Assert.Fail("The CSMS is not running!");
                return;
            }


            #region The local controller, up to the CSMS: with a password or one-time passwords

            // Under its own name: in the overlay network what it sends comes from it.
            var lcLogin        = User_Id.Parse(localController.Node.Id.ToString());
            var description    = I18NString.Create($"{localController.OCPP.NodeId} --(webSocketTLS/{Authentication}/{DataFormat})--> {csms.OCPP.NodeId}");

            var lcCredentials  = Credentials(localController, csms, lcLogin, Authentication, description);

            BelievesTheTestCA(localController);

            // What it forwards in the name of its stations is taken only from
            // a login marked as a local controller's.
            Assert.That(csms.WebSocketLogins.TrySetLocalController(lcLogin, true, out var markError), Is.True, markError);

            Assert.That(
                localController.Connections.TryAddConnection(
                     description,
                     URL.Parse($"wss://{lcLogin}@127.0.0.1:{csms.OCPPServerSettings.TCPPort}"),
                     "CSMS",
                     true,
                     lcCredentials,
                     null,
                     out var uplinkConnection,
                     out var uplinkError,
                     Encoding: DataFormat
                ),
                Is.True, uplinkError
            );

            await Until(() => StatusOf(localController, uplinkConnection!) == ConnectionStatus.Connected, $"'{lcLogin}' connecting over webSocketTLS", localController, csms);

            // Sent by no node by itself: asked for, as after a reboot.
            _ = localController.SendBootNotifications();

            await Until(() => RegistrationOf(localController)              == "Accepted",                 $"'{lcLogin}' being accepted by the CSMS",   localController, csms);

            #endregion

            #region The station, to the local controller: with a certificate

            var login        = User_Id.Parse("cs-" + RandomExtensions.RandomString(8));
            var description2  = I18NString.Create($"{chargingStation.Id} --(webSocketMTLS/{DataFormat})--> {localController.OCPP.NodeId}");

            // A certificate of the test's CA that names the login: the local
            // controller knows the station by nothing else, so the URL names nobody.
            var certificate  = ClientCertificate(chargingStation, localController, login.ToString());

            BelievesTheTestCA(chargingStation);

            Assert.That(
                chargingStation.Connections.TryAddConnection(
                     description2,
                     URL.Parse($"wss://127.0.0.1:{localController.OCPPServerSettings.TCPPort}"),
                     "LocalController",
                     true,
                     null,
                     certificate,
                     out var connection,
                     out var error,
                     Encoding: DataFormat
                ),
                Is.True, error
            );

            await Until(() => StatusOf(chargingStation, connection!) == ConnectionStatus.Connected, $"'{login}' connecting over webSocketMTLS",                         chargingStation, localController, csms);
            await Until(() => IsConnectedAt(localController, login),                                $"'{login}' being let in by its certificate",                       chargingStation, localController, csms);

            // Sent by no node by itself: asked for, as after a reboot.
            _ = chargingStation.SendBootNotifications();

            await Until(() => RegistrationOf(chargingStation)        == "Accepted",                 $"'{login}' being accepted by the CSMS above the local controller", chargingStation, localController, csms);

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


        #region SSH(Authentication)

        /// <summary>
        /// The station connects to the local controller's SSH server, and pins the
        /// key the server shows: with its password, with a one-time password
        /// bound to the SSH session, or with its own key.
        /// </summary>
        [TestCase(authPassword)]
        [TestCase(authTOTP)]
        [TestCase(authPublicKey)]
        public async Task SSH(String Authentication)
        {

            if (chargingStation is null)
            {
                Assert.Fail("The charging station is not running!");
                return;
            }

            if (localController is null)
            {
                Assert.Fail("The local controller is not running!");
                return;
            }


            var login        = User_Id.Parse("cs-" + RandomExtensions.RandomString(8));
            var description  = I18NString.Create($"{chargingStation.Id} --(ssh/{Authentication})--> {localController.OCPP.NodeId}");
            var key          = await chargingStation.SshKey.MakeIfNeeded();
            var error        = "";
            String? credentials = null;

            Assert.That(localController.OCPPOverSSHServed, Is.True, "OCPP-over-SSH is switched on in the file, and not served.");

            // Its own key - or a password or the secret of one-time passwords,
            // and its own key then is one the controller does not know.
            if (Authentication == authPublicKey)
                Assert.That(
                    localController.TryAddStationSSHKey(
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
                credentials = Credentials(chargingStation, localController, login, Authentication, description);

            Assert.That(
                chargingStation.Connections.TryAddConnection(
                     description,
                     URL.Parse($"ssh://{login}@127.0.0.1:{localController.SSHPort!.Value.ToUInt16()}"),
                     "LocalController",
                     true,
                     credentials,
                     null,
                     out var connection,
                     out error,
                     SshHostKey: localController.SSHHostKey!.Split(' ', 2)[1]
                ),
                Is.True, error
            );

            await Until(() => StatusOf(chargingStation, connection!) == ConnectionStatus.Connected, $"'{login}' connecting over SSH",                   chargingStation, localController);
            await Until(() => IsConnectedAt(localController, login),                                $"'{login}' being let in by the local controller",  chargingStation, localController);

        }

        #endregion

    }

}
