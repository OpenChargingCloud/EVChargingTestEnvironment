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
    public class GW_2_LC : AHopByHopTest
    {

        #region Data

        private DNSClient?                       dnsClient;
        private Gateway.Gateway?                 gateway;
        private LocalController.LocalController? localController;


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

            var gwDirectory    = NewNodeDirectory("gw");

            gateway          = new Gateway.Gateway(
                                   DNSClient:        dnsClient,
                                   HTTPPort:         TestPorts.Free(),
                                   AccountsPath:     gwDirectory.Accounts,
                                   ConfigFile:       gwDirectory.ConfigFile,
                                   LogToConsole:     false,
                                   BridgeDebugLog:   false
                               );

            await gateway.Start();


            var lcDirectory    = NewServedNodeDirectory("lc");

            localController  = new LocalController.LocalController(
                                   DNSClient:        dnsClient,
                                   HTTPPort:         TestPorts.Free(),
                                   AccountsPath:     lcDirectory.Accounts,
                                   ConfigFile:       lcDirectory.ConfigFile,
                                   LogToConsole:     false,
                                   BridgeDebugLog:   false
                               );

            await localController.Start();

        }

        [TearDown]
        public async Task Stop()
        {

            if (gateway is not null)
            {
                await gateway.Stop();
                await gateway.DisposeAsync();
            }

            if (localController is not null)
            {
                await localController.Stop();
                await localController.DisposeAsync();
            }

        }

        #endregion


        #region WebSocket(Transport, Authentication)

        /// <summary>
        /// The gateway connects to the local controller and is let in. Whether it is accepted is
        /// for the CSMS above the local controller to say, and there is none here: so it
        /// is the line, at both its ends, that is tested.
        /// </summary>
        [TestCase("ws", authPassword)]
        public async Task WebSocket(String  Transport,
                                    String  Authentication)
        {

            if (gateway is null)
            {
                Assert.Fail("The gateway is not running!");
                return;
            }

            if (localController is null)
            {
                Assert.Fail("The local controller is not running!");
                return;
            }


            var login        = User_Id.Parse("gw-" + RandomExtensions.RandomString(8));
            var description  = I18NString.Create($"{gateway.OCPP.NodeId} --({Transport}/{Authentication})--> {localController.OCPP.NodeId}");

            var credentials  = Credentials(gateway, localController, login, Authentication, description);

            BelievesTheTestCA(gateway);

            Assert.That(
                gateway.Connections.TryAddConnection(
                     description,
                     URL.Parse($"{Transport}://{login}@127.0.0.1:{localController.OCPPServerSettings.TCPPort}"),
                     "LocalController",
                     true,
                     credentials,
                     null,
                     out var connection,
                     out var error
                ),
                Is.True, error
            );

            await Until(() => StatusOf(gateway, connection!) == ConnectionStatus.Connected, $"'{login}' connecting over {Transport}", gateway, localController);
            await Until(() => IsConnectedAt(localController, login),                              $"'{login}' being let in by the local controller", gateway, localController);


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
