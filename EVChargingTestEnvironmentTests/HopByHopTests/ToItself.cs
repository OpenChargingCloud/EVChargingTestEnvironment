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

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.SSH;

using cloud.charging.open.protocols.WWCP.Node.TestKit;
using cloud.charging.open.protocols.WWCP.Node.Connections;
using cloud.charging.open.protocols.OCPP.Node;

#endregion

namespace cloud.charging.open.TestEnvironment.Tests.Connections.HopByHop
{

    /// <summary>
    /// A node that connects to itself: a connection of its own store to its own
    /// station port, signed in with a login of its own - a self-test of both
    /// ends of a node at once. Every kind of node alike: a client and a
    /// server of WebSockets and of SSH are the same on each.
    /// </summary>
    [TestFixture]
    public class ToItself : AHopByHopTest
    {

        #region Data

        private DNSClient?        dnsClient;
        private OCPPServingNode?  node;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeTheResolver()
        {
            dnsClient = new DNSClient(
                            SearchForIPv6DNSServers: false
                        );
        }

        [TearDown]
        public async Task Stop()
        {

            if (node is not null)
            {
                await node.Stop();
                await node.DisposeAsync();
            }

            node = null;

        }

        #endregion

        #region (private) Started(Kind)

        /// <summary>
        /// A node of the given kind, with its station port switched on -
        /// over TLS where the test connects over wss:// - and started.
        /// </summary>
        /// <param name="Kind">"cs", "lc", "gw" or "csms".</param>
        private async Task<OCPPServingNode> Started(String Kind)
        {

            // A local controller has lines up only where it reports upwards.
            var directory = Kind == "lc"
                                ? NewServedNodeDirectory(Kind, ReportsUpwards())
                                : NewServedNodeDirectory(Kind);

            OCPPServingNode started = Kind switch {

                "cs"    => new ChargingStation.ChargingStation(
                               DNSClient:        dnsClient,
                               HTTPPort:         TestPorts.Free(),
                               NoKiosk:          true,
                               AccountsPath:     directory.Accounts,
                               ConfigFile:       directory.ConfigFile,
                               LogToConsole:     false,
                               BridgeDebugLog:   false
                           ),

                "lc"    => new LocalController.LocalController(
                               DNSClient:        dnsClient,
                               HTTPPort:         TestPorts.Free(),
                               AccountsPath:     directory.Accounts,
                               ConfigFile:       directory.ConfigFile,
                               LogToConsole:     false,
                               BridgeDebugLog:   false
                           ),

                "gw"    => new Gateway.Gateway(
                               DNSClient:        dnsClient,
                               HTTPPort:         TestPorts.Free(),
                               AccountsPath:     directory.Accounts,
                               ConfigFile:       directory.ConfigFile,
                               LogToConsole:     false,
                               BridgeDebugLog:   false
                           ),

                "csms"  => new CSMS.CSMS(
                               DNSClient:        dnsClient,
                               HTTPPort:         TestPorts.Free(),
                               AccountsPath:     directory.Accounts,
                               ConfigFile:       directory.ConfigFile,
                               LogToConsole:     false,
                               BridgeDebugLog:   false
                           ),

                _       => throw new ArgumentException($"'{Kind}' is no kind of node these tests know.", nameof(Kind))

            };

            node = started;

            await started.Start();

            return started;

        }

        #endregion


        #region WebSocket(Kind, Transport, Authentication)

        /// <summary>
        /// The node connects to its own station port, as its back end, with a login
        /// of its own: it is connected as the one that connects, and let in as
        /// the one that is connected to.
        /// </summary>
        [TestCase("cs",   "ws",  authPassword)]
        [TestCase("cs",   "wss", authPassword)]
        [TestCase("cs",   "ws",  authTOTP)]
        [TestCase("lc",   "ws",  authPassword)]
        [TestCase("lc",   "wss", authPassword)]
        [TestCase("lc",   "ws",  authTOTP)]
        [TestCase("gw",   "ws",  authPassword)]
        [TestCase("gw",   "wss", authPassword)]
        [TestCase("gw",   "ws",  authTOTP)]
        [TestCase("csms", "ws",  authPassword)]
        [TestCase("csms", "wss", authPassword)]
        [TestCase("csms", "ws",  authTOTP)]
        public async Task WebSocket(String  Kind,
                                    String  Transport,
                                    String  Authentication)
        {

            var itself       = await Started(Kind);

            var login        = User_Id.Parse("self-" + RandomExtensions.RandomString(8));
            var description  = I18NString.Create($"{itself.NodeNoun} --({Transport}/{Authentication})--> itself");

            var credentials  = Credentials(itself, itself, login, Authentication, description);

            BelievesTheTestCA(itself);

            Assert.That(
                itself.Connections.TryAddConnection(
                     description,
                     URL.Parse($"{Transport}://{login}@127.0.0.1:{itself.OCPPServerSettings.TCPPort}"),
                     "CSMS",
                     true,
                     credentials,
                     null,
                     out var connection,
                     out var error
                ),
                Is.True, error
            );

            await Until(() => StatusOf(itself, connection!) == ConnectionStatus.Connected,  $"'{login}' connecting over {Transport}",           itself);
            await Until(() => IsConnectedAt(itself, login),                                 $"'{login}' being let in by the node it connected to",  itself);

        }

        #endregion

        #region SSH(Kind, Authentication)

        /// <summary>
        /// The node connects to its own SSH server, as its back end, and pins the
        /// host key it shows itself: with a password, with a one-time password
        /// bound to the SSH session, or with its own key. It is connected as the
        /// one that connects, and let in as the one that is connected to.
        /// </summary>
        [TestCase("cs",   authPassword)]
        [TestCase("cs",   authTOTP)]
        [TestCase("cs",   authPublicKey)]
        [TestCase("lc",   authPassword)]
        [TestCase("lc",   authTOTP)]
        [TestCase("lc",   authPublicKey)]
        [TestCase("gw",   authPassword)]
        [TestCase("gw",   authTOTP)]
        [TestCase("gw",   authPublicKey)]
        [TestCase("csms", authPassword)]
        [TestCase("csms", authTOTP)]
        [TestCase("csms", authPublicKey)]
        public async Task SSH(String  Kind,
                              String  Authentication)
        {

            var itself       = await Started(Kind);

            var login        = User_Id.Parse("self-" + RandomExtensions.RandomString(8));
            var description  = I18NString.Create($"{itself.NodeNoun} --(ssh/{Authentication})--> itself");
            var error        = "";
            String? credentials = null;

            Assert.That(itself.OCPPOverSSHServed, Is.True, "OCPP-over-SSH is switched on in the file, and not served.");

            // Its own key, known to itself as a station's - or a password or the
            // secret of one-time passwords, and its own key then is one it does
            // not know.
            if (Authentication == authPublicKey)
            {

                var key = await itself.SshKey.MakeIfNeeded();

                Assert.That(
                    itself.TryAddStationSSHKey(
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

            }

            else
                credentials = Credentials(itself, itself, login, Authentication, description);

            Assert.That(
                itself.Connections.TryAddConnection(
                     description,
                     URL.Parse($"ssh://{login}@127.0.0.1:{itself.SSHPort!.Value.ToUInt16()}"),
                     "CSMS",
                     true,
                     credentials,
                     null,
                     out var connection,
                     out error,
                     SshHostKey: itself.SSHHostKey!.Split(' ', 2)[1]
                ),
                Is.True, error
            );

            await Until(() => StatusOf(itself, connection!) == ConnectionStatus.Connected,  $"'{login}' connecting over SSH",                     itself);
            await Until(() => IsConnectedAt(itself, login),                                 $"'{login}' being let in by the node it connected to",  itself);

        }

        #endregion

    }

}
