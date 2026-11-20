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
    public class LC_2_CS : AHopByHopTest
    {

        #region Data

        private DNSClient?                        dnsClient;
        private LocalController.LocalController?  localController;
        private ChargingStation.ChargingStation?  chargingStation;


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

            // Without the section "csms": a node below is dialled all the
            // same, whether the controller reports to a CSMS or not.
            var lcDirectory    = NewNodeDirectory("lc");

            localController  = new LocalController.LocalController(
                                   DNSClient:        dnsClient,
                                   HTTPPort:         IPPort.Parse(TestPorts.Free()),
                                   AccountsPath:     lcDirectory.Accounts,
                                   ConfigFile:       lcDirectory.ConfigFile,
                                   LogToConsole:     false,
                                   BridgeDebugLog:   false
                               );

            await localController.Start();


            var csDirectory    = NewServedNodeDirectory("cs");

            chargingStation  = new ChargingStation.ChargingStation(
                                   DNSClient:        dnsClient,
                                   HTTPPort:         IPPort.Parse(TestPorts.Free()),
                                   NoKiosk:          true,
                                   AccountsPath:     csDirectory.Accounts,
                                   ConfigFile:       csDirectory.ConfigFile,
                                   LogToConsole:     false,
                                   BridgeDebugLog:   false
                               );

            await chargingStation.Start();

        }

        [TearDown]
        public async Task Stop()
        {

            if (localController is not null)
            {
                await localController.Stop();
                await localController.DisposeAsync();
            }

            if (chargingStation is not null)
            {
                await chargingStation.Stop();
                await chargingStation.DisposeAsync();
            }

        }

        #endregion


        #region WebSocket(Transport, Authentication)

        /// <summary>
        /// The local controller dials the charging station, as the CSMS does (see
        /// CSMS_2_CS): every node dials downwards alike. The connection names
        /// the charging station at its other end, which it is spoken to by;
        /// the charging station lets it in with the login it signs in with.
        /// </summary>
        [TestCase("ws", authPassword)]
        public async Task WebSocket(String  Transport,
                                    String  Authentication)
        {

            if (localController is null)
            {
                Assert.Fail("The local controller is not running!");
                return;
            }

            if (chargingStation is null)
            {
                Assert.Fail("The charging station is not running!");
                return;
            }


            var login        = "lc-" + RandomExtensions.RandomString(8);
            var node         = chargingStation.Id?.ToString() ?? ChargingStation.ChargingStation.DefaultOCPP21NodeId;
            var description  = $"{localController.OCPP.NodeId} --({Transport}/{Authentication})--> {chargingStation.Id}";

            Assert.That(
                localController.Connections.TryAddConnection(
                     I18NString.Create(description),
                     URL.Parse($"{Transport}://{login}@127.0.0.1:{chargingStation.OCPPServerSettings.TCPPort}"),
                     "ChargingStation",
                     true,
                     Credentials(localController, chargingStation, login, Authentication, description),
                     null,
                     out var connection,
                     out var error,
                     RemoteNodeId: node
                ),
                Is.True, error
            );

            await Until(() => StatusOf(localController, connection!) == ConnectionStatus.Connected, $"'{login}' connecting over {Transport}", localController, chargingStation);
            await Until(() => IsConnectedAt(chargingStation, login),                          $"'{login}' being let in by the charging station", localController, chargingStation);


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
