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

#endregion

namespace cloud.charging.open.TestEnvironment.Tests.Connections.HopByHop
{

    [TestFixture]
    public class LC_2_CSMS : AHopByHopTest
    {

        #region Data

        private DNSClient?                       dnsClient;
        private LocalController.LocalController? localController;
        private CSMS.CSMS?                       csms;


        private const           String    KnownCard       = "04A2B3C4D5E6F7";
        private const           String    InvalidCard     = "04BBBBBBBBBBBB";
        private const           String    NobodysCard     = "04CCCCCCCCCCCC";

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public async Task Start()
        {

            dnsClient        = new DNSClient(
                                   SearchForIPv6DNSServers: false
                               );

            var lcDirectory    = NewNodeDirectory("lc", ReportsUpwards());

            localController  = new LocalController.LocalController(
                                   DNSClient:        dnsClient,
                                   HTTPPort:         IPPort.Parse(TestPorts.Free()),
                                   AccountsPath:     lcDirectory.Accounts,
                                   ConfigFile:       lcDirectory.ConfigFile,
                                   LogToConsole:     false,
                                   BridgeDebugLog:   false
                               );

            await localController.Start();


            var csmsDirectory  = NewServedNodeDirectory("csms");

            csms             = new CSMS.CSMS(
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


        #region LC_to_CSMS_viaWS(Transport, Authentication)

        /// <summary>
        /// The local controller dials the CSMS, under its own name: what it
        /// sends in the overlay network comes from it. That it reports
        /// upwards at all its file says, in the section "csms".
        /// </summary>
        [TestCase("ws", "basic")]
        public async Task LC_to_CSMS_viaWS(String  Transport,
                                           String  Authentication)
        {

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


            var login        = localController.Node.Id.ToString();
            var description  = $"{localController.OCPP.NodeId} --({Transport}/{Authentication})--> {csms.OCPP.NodeId}";

            var credentials  = Credentials(localController, csms, login, Authentication, description);

            BelievesTheTestCA(localController);

            // In the overlay network what the local controller sends in its own
            // name is taken only from a login marked as one of its kind.
            Assert.That(csms.WebSocketLogins.TrySetLocalController(login, true, out var markError), Is.True, markError);

            Assert.That(
                localController.Connections.TryAddConnection(
                     description,
                     $"{Transport}://{login}@127.0.0.1:{csms.OCPPServerSettings.TCPPort}",
                     "CSMS",
                     true,
                     credentials,
                     null,
                     out var connection,
                     out var error
                ),
                Is.True, error
            );

            await Until(() => StatusOf(localController, connection!) == ConnectionStatus.Connected, $"'{login}' connecting over {Transport}", localController, csms);
            await Until(() => RegistrationOf(localController) == "Accepted",                       $"'{login}' being accepted by the CSMS", localController, csms);


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
