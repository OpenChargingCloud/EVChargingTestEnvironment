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

using WWCPNode    = cloud.charging.open.protocols.WWCP.Node.WWCPNode;
//using Management  = cloud.charging.open.CSMS.CSMS;
//using Controller  = cloud.charging.open.Gateway.Gateway;
//using Station     = cloud.charging.open.Gateway.Gateway;
using org.GraphDefined.Vanaheimr.Illias;

#endregion

namespace cloud.charging.open.TestEnvironment.Tests.Connections
{

    [TestFixture]
    public class CSMS_2_GW : ATestsBase
    {

        #region Data

        private DNSClient?       dnsClient;
        private CSMS.CSMS?       csms;
        private Gateway.Gateway? gateway;


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

            var csmsDirectory  = NewNodeDirectory("csms");

            csms             = new CSMS.CSMS(
                                   DNSClient:        dnsClient,
                                   HTTPPort:         IPPort.Parse(TestPorts.Free()),
                                   AccountsPath:     csmsDirectory.Accounts,
                                   ConfigFile:       csmsDirectory.ConfigFile,
                                   LogToConsole:     false,
                                   BridgeDebugLog:   false
                               );

            await csms.Start();


            var gwDirectory    = NewServedNodeDirectory("gw");

            gateway          = new Gateway.Gateway(
                                   DNSClient:        dnsClient,
                                   HTTPPort:         IPPort.Parse(TestPorts.Free()),
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


        #region CSMS_to_GW_viaWS(Transport, Authentication)

        /// <summary>
        /// The CSMS dials the gateway - the one node that dials
        /// downwards. The gateway lets it in as a connection the other
        /// end makes, written down under the name the CSMS signs in with; the
        /// CSMS names the node its connection leads to, and knows its login.
        /// </summary>
        [TestCase("ws", "basic")]
        public async Task CSMS_to_GW_viaWS(String  Transport,
                                           String  Authentication)
        {

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


            var login        = "csms-" + RandomExtensions.RandomString(8);
            var node         = gateway.Node.Id.ToString();
            var description  = $"{csms.OCPP.NodeId} --({Transport}/{Authentication})--> {node}";
            var door         = $"{Transport}://127.0.0.1:{gateway.OCPPServerSettings.TCPPort}/{login}";
            var error        = "";

            BelievesTheTestCA(csms);

            // The gateway: the CSMS, written down as a connection it makes.
            Assert.That(
                gateway.Connections.TryAddConnection(
                     "The CSMS, dialling in",
                     door,
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

            // The CSMS: a login of the node it dials, and the connection to it.
            Assert.That(csms.WebSocketLogins.TrySetPassword(node, RandomExtensions.RandomString(32), null, "tests", out error), Is.True, error);
            Assert.That(csms.WebSocketLogins.TrySetGateway(node, true, out error),                                                 Is.True, error);

            Assert.That(
                csms.Connections.TryAddConnection(
                     description,
                     door,
                     "Gateway",
                     true,
                     Credentials(csms, gateway, login, Authentication, description),
                     null,
                     out var connection,
                     out error,
                     RemoteNodeId: node
                ),
                Is.True, error
            );

            await Until(() => StatusOf(csms, connection!) == ConnectionStatus.Connected,     $"'{login}' dialling over {Transport}",                csms, gateway);
            await Until(() => StatusOf(gateway, inbound!) == ConnectionStatus.Connected, $"'{login}' being let in by the gateway", csms, gateway);
            await Until(() => csms.IsConnected(node),                                         $"'{node}' counting as connected at the CSMS",        csms, gateway);


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
