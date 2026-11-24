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

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.protocols.WWCP.Node;
using cloud.charging.open.protocols.WWCP.Node.TestKit;
using cloud.charging.open.protocols.WWCP.Node.Connections;

using OCPPServingNode  = cloud.charging.open.protocols.OCPP.Node.OCPPServingNode;

#endregion

namespace cloud.charging.open.TestEnvironment.Tests.Connections.HopByHop
{

    /// <summary>
    /// What every test of a node's file transfers needs: a CSMS that asks for
    /// them, the node - a charging station, a local controller - connected to
    /// it and accepted, the HTTP file servers of the node's configuration, and
    /// file servers of the test's own.
    /// </summary>
    /// <remarks>
    /// The node is the kind's to make (<see cref="NewNode"/>); what is asked of
    /// it, and of which file server, is the test's.
    /// </remarks>
    public abstract class AFileTransferTest : AHopByHopTest
    {

        #region Data

        protected DNSClient?        dnsClient;
        protected CSMS.CSMS?        csms;
        protected OCPPServingNode?  node;
        protected User_Id           login;

        private readonly ConcurrentQueue<IAsyncDisposable>  testServers  = new();

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public async Task Start()
        {

            dnsClient          = new DNSClient(SearchForIPv6DNSServers: false);

            var csmsDirectory  = NewServedNodeDirectory("csms");

            csms               = new CSMS.CSMS(
                                     DNSClient:        dnsClient,
                                     HTTPPort:         TestPorts.Free(),
                                     AccountsPath:     csmsDirectory.Accounts,
                                     ConfigFile:       csmsDirectory.ConfigFile,
                                     LogToConsole:     false,
                                     BridgeDebugLog:   false
                                 );

            await csms.Start();

            HearsOf(csms);

            (node, login)      = await NewNode(dnsClient);

            await TheNodeConnectsToItsCSMS();

        }

        [TearDown]
        public async Task Stop()
        {

            if (node is not null)
            {
                await node.Stop();
                await node.DisposeAsync();
            }

            if (csms is not null)
            {
                await csms.Stop();
                await csms.DisposeAsync();
            }

            while (testServers.TryDequeue(out var server))
                await server.DisposeAsync();

        }

        #endregion


        #region (protected abstract) NewNode(DNSClient) / NodeName / BeforeConnecting(CSMS, Login) / HearsOf(CSMS)

        /// <summary>
        /// The node whose file transfers are tested, started, and the login it
        /// connects to its CSMS under - its own name in the overlay network.
        /// </summary>
        protected abstract Task<(OCPPServingNode Node, User_Id Login)> NewNode(DNSClient DNSClient);

        /// <summary>
        /// What the node is called in a description.
        /// </summary>
        protected abstract String NodeName { get; }

        /// <summary>
        /// What the CSMS is to know of the node before it connects.
        /// </summary>
        protected virtual void BeforeConnecting(CSMS.CSMS  CSMS,
                                                User_Id    Login)
        { }

        /// <summary>
        /// What the test hears of the CSMS: the notifications of the node, say.
        /// </summary>
        protected virtual void HearsOf(CSMS.CSMS CSMS)
        { }

        #endregion

        #region (private) TheNodeConnectsToItsCSMS()

        /// <summary>
        /// The node connects to its CSMS over ws:// with a password and is
        /// accepted: what the CSMS asks of it reaches it, and what it tells of a
        /// transfer reaches the CSMS.
        /// </summary>
        private async Task TheNodeConnectsToItsCSMS()
        {

            var description  = I18NString.Create($"{NodeName} --(ws/password)--> {csms!.OCPP.NodeId}");
            var credentials  = Credentials(node!, csms, login, authPassword, description);

            BeforeConnecting(csms, login);

            Assert.That(
                node!.Connections.TryAddConnection(
                    description,
                    URL.Parse($"ws://{login}@127.0.0.1:{csms.OCPPServerSettings.TCPPort}"),
                    "CSMS",
                    true,
                    credentials,
                    null,
                    out var connection,
                    out var error
                ),
                Is.True, error
            );

            await Until(() => StatusOf(node, connection!) == ConnectionStatus.Connected, $"'{login}' connecting to its CSMS", node, csms);

            // Sent by no node by itself: asked for, as after a reboot.
            _ = node.SendBootNotifications();

            await Until(() => RegistrationOf(node) == "Accepted",                       $"'{login}' being accepted by its CSMS", node, csms);

        }

        #endregion


        #region (protected) Identity / HoldsTheHTTPFileServers(params Servers)

        /// <summary>
        /// The node's identity at a file server: its login there, its directory,
        /// and what "{id}" stands for.
        /// </summary>
        protected String Identity
            => node!.FileTransferIdentity;

        /// <summary>
        /// The node's configuration names the given HTTP file servers, and no
        /// other: "httpFileServers" of its file, read when a request is judged.
        /// </summary>
        protected void HoldsTheHTTPFileServers(params HTTPFileServer[] Servers)
        {

            Assert.That(node!.ConfigFile.TryReplaceSection(HTTPFileServer.SectionName,
                                                           new JArray(Servers.Select(server => server.ToJSON())),
                                                           out var error),
                        Is.True, error);

            Assert.That(node.HTTPFileServers.Select(server => server.Id), Is.EquivalentTo(Servers.Select(server => server.Id)),
                        $"The node does not hold the HTTP file servers it was given.{Story(node)}");

        }

        #endregion

        #region (protected) ATestServer(Secure = false, User = null, Password = null)

        /// <summary>
        /// A file server of the test's own, over HTTP - or over HTTPS, with a
        /// certificate of the test's CA -, taken away after the test.
        /// </summary>
        protected TestFileServer ATestServer(Boolean  Secure    = false,
                                             String?  User      = null,
                                             String?  Password  = null)
        {

            var server = Secure
                             ? TestFileServer.OverHTTPS(AServerCertificateOfTheTestCA(), User, Password)
                             : TestFileServer.OverHTTP (User, Password);

            testServers.Enqueue(server);

            return server;

        }

        #endregion

    }

}
