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
using System.Text;
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

using WWCPNode         = cloud.charging.open.protocols.WWCP.Node.WWCPNode;
using OCPPServingNode  = cloud.charging.open.protocols.OCPP.Node.OCPPServingNode;
//using Management  = cloud.charging.open.CSMS.CSMS;
//using Controller  = cloud.charging.open.LocalController.LocalController;
//using Station     = cloud.charging.open.ChargingStation.ChargingStation;
using org.GraphDefined.Vanaheimr.Illias;

#endregion

namespace cloud.charging.open.TestEnvironment.Tests.Connections.HopByHop
{

    public abstract class AEndToEndTests : ATest
    {

        #region OverTLS / OverSSH

        /// <summary>
        /// Whether the running test connects over wss://: one of its arguments
        /// says so. Known in [SetUp] already, so the node that is connected to
        /// gets a certificate before it starts.
        /// </summary>
        protected static Boolean OverTLS

            => TestContext.CurrentContext.Test.Arguments.Any(argument => argument as String == "wss");

        /// <summary>
        /// Whether the running test connects over ssh://: it is one named "SSH".
        /// </summary>
        protected static Boolean OverSSH

            => TestContext.CurrentContext.Test.MethodName == "SSH";

        #endregion


        #region NodeDirectory

        /// <summary>
        /// Where one node of one test keeps what it keeps: its accounts and its
        /// configuration file.
        /// </summary>
        /// <param name="Accounts">The directory of its accounts.</param>
        /// <param name="ConfigFile">Its configuration file.</param>
        protected sealed record NodeDirectory(String          Accounts,
                                              WWCPConfigFile  ConfigFile);

        #endregion

        #region StationServer(ClientAuthentication)

        /// <summary>
        /// The "webSocketServer" section of a node that other nodes connect to: its
        /// station server switched on, on 127.0.0.1 and a port of its own,
        /// letting in what signs in in the given ways - HTTP authentication
        /// without TLS where none are given.
        /// </summary>
        /// <remarks>
        /// Off where the file says nothing, so a node that is to be connected to
        /// needs it in its file.
        /// </remarks>
        /// <param name="ClientAuthentication">How a client may sign in: "httpAuthWithoutTLS", "httpAuthOverTLS", "tlsClientCertificate".</param>
        protected static JProperty StationServer(params String[] ClientAuthentication)

            => new ("webSocketServer", new JObject(
                   new JProperty("enabled",               true),
                   new JProperty("address",               "127.0.0.1"),
                   new JProperty("port",                  TestPorts.Free().ToUInt16()),
                   new JProperty("clientAuthentication",  new JArray(ClientAuthentication.Length > 0 ? ClientAuthentication : [ "httpAuthWithoutTLS" ])),
                   new JProperty("subprotocols",          new JArray("ocpp2.1", "ocpp2.0.1")),
                   new JProperty("ssh",                   new JObject(
                       new JProperty("enabled",           OverSSH)
                   ))
               ));

        #endregion

        #region ReportsUpwards()

        /// <summary>
        /// The "csms" section of a local controller that reports upwards: its
        /// lines up are the connections of its store, but only where this
        /// says that it reports to anything at all - and a CSMS that connects to it
        /// is let in only then.
        /// </summary>
        protected static JProperty ReportsUpwards()

            => new ("csms", new JObject(
                   new JProperty("enabled",  true)
               ));

        #endregion

        #region SSHServer()

        /// <summary>
        /// The "ssh" section of a node with an SSH server of its own, on a
        /// port of its own: where its station server takes OCPP-over-SSH.
        /// </summary>
        protected static JProperty SSHServer()

            => new ("ssh", new JObject(
                   new JProperty("enabled",  true),
                   new JProperty("port",     TestPorts.Free().ToUInt16())
               ));

        #endregion

        #region NewServedNodeDirectory(Purpose, params Sections)

        /// <summary>
        /// The directory of the node that is connected to: its station server
        /// switched on - over TLS, with a certificate of the test's CA, where
        /// the test connects over wss://, and with an SSH server where it connects
        /// over ssh://.
        /// </summary>
        /// <param name="Purpose">What the directory is for, "cs" or "csms", part of its name.</param>
        /// <param name="Sections">What else the file says, e.g. <see cref="ReportsUpwards"/>.</param>
        protected NodeDirectory NewServedNodeDirectory(String              Purpose,
                                                       params JProperty[]  Sections)
        {

            var nodeDirectory = NewNodeDirectory(
                                    Purpose,
                                    [
                                        StationServer(OverTLS ? "httpAuthOverTLS" : "httpAuthWithoutTLS"),
                                        .. (OverSSH ? new[] { SSHServer() } : Array.Empty<JProperty>()),
                                        .. Sections
                                    ]
                                );

            if (OverTLS)
                ACertificateOfTheTestCA(Path.GetDirectoryName(nodeDirectory.ConfigFile.Path)!);

            return nodeDirectory;

        }

        #endregion

        #region (private) TestCA / ACertificateOfTheTestCA(Directory)

        /// <summary>
        /// A CA of the test's own. Its name is its own per test, as Windows
        /// caches chain building by issuer name.
        /// </summary>
        private X509Certificate2 TestCA
        {
            get
            {

                if (testCA is null)
                {

                    testCAKey    = ECDsa.Create(ECCurve.NamedCurves.nistP256);

                    var request  = new CertificateRequest($"CN=The CA of a basic test {Guid.NewGuid()}", testCAKey, HashAlgorithmName.SHA256);

                    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
                    request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

                    testCA       = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));

                }

                return testCA;

            }
        }

        /// <summary>
        /// A key of the node's own and a certificate of the test's CA for it,
        /// for 127.0.0.1, in the store beside its configuration file before
        /// it starts: this is what makes its station port speak TLS.
        /// </summary>
        /// <param name="Directory">The directory of the node's configuration file.</param>
        private void ACertificateOfTheTestCA(String Directory)
        {

            var ca     = TestCA;

            var store  = new CertificateStore(
                             Path.Combine(Directory, CertificatesConfiguration.DefaultDirectory),
                             new cloud.charging.open.protocols.WWCP.Node.Logging.EventLog(),
                             NodeName:      "Node",
                             Listeners:     [ OCPPServingNode.StationListener ],
                             RequestKinds:  [ CertificateKind.TLSServerIdentity ]
                         );

            store.Reload();

            Assert.That(store.TryCreateRequest(CertificateKind.TLSServerIdentity,
                                               "CN=127.0.0.1",
                                               null,
                                               new CertificateRequestAsks(ReachableAs: [ "127.0.0.1" ]),
                                               [ OCPPServingNode.StationListener ],
                                               null,
                                               out var request,
                                               out var error,
                                               out _),
                        Is.True, error);

            using var certificate = CertificateRequest.LoadSigningRequestPem(request!.CSR, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions).
                                        Create(ca.SubjectName,
                                               X509SignatureGenerator.CreateForECDsa(testCAKey!),
                                               DateTimeOffset.UtcNow.AddDays(-1),
                                               DateTimeOffset.UtcNow.AddDays(30),
                                               Guid.NewGuid().ToByteArray());

            Assert.That(store.TryAnswerRequest(certificate.ExportCertificatePem() + Environment.NewLine + ca.ExportCertificatePem(),
                                               out _, out _, out _, out error, out _),
                        Is.True, error);

        }

        #endregion

        #region BelievesTheTestCA(Node)

        /// <summary>
        /// Where the test connects over wss://, the node that connects believes the
        /// test's CA: a TLS root of its own store, kept for what it judges the
        /// servers of its connections by - the machine has never heard of it.
        /// </summary>
        /// <param name="Node">The node that connects.</param>
        protected void BelievesTheTestCA(WWCPNode Node)
        {

            if (!OverTLS)
                return;

            Assert.That(Node.Certificates.Import(Encoding.ASCII.GetBytes(TestCA.ExportCertificatePem()),
                                                 CertificateKind.TLSRoot,
                                                 null,
                                                 "The CA of the test",
                                                 [ Node.ConnectionsCertificateUsage ],
                                                 out _,
                                                 out var error),
                        Is.True, error);

        }

        #endregion

        #region Credentials(Client, Served, Login, Authentication, Description)

        /// <summary>
        /// What the node that connects signs in with, and the login the node it
        /// connects to lets it in with: a password, or the secret of one-time
        /// passwords.
        /// </summary>
        /// <returns>The credentials of the connecting node's store.</returns>
        protected static String Credentials(WWCPNode         Client,
                                            OCPPServingNode  Served,
                                            User_Id          Login,
                                            String           Authentication,
                                            String           Description)
        {

            String? credentials;
            String? error;

            switch (Authentication)
            {

                case authPassword:
                case "basic":

                    var password = RandomExtensions.RandomString(32);

                    Assert.That(Served. WebSocketLogins.TrySetPasswordAuthentication(Login, password, null, I18NString.Create("tests"), out error),                             Is.True, error);
                    Assert.That(Client.Connections.TryAddPasswordAuthentication(Login, password, I18NString.Create(Description), out credentials, out error),       Is.True, error);

                    break;

                case authTOTP:

                    var sharedSecret = RandomExtensions.RandomString(16);

                    Assert.That(Served. WebSocketLogins.TrySetTOTPAuthentication(Login, sharedSecret, null, null, null, null, null, I18NString.Create("tests"), out error), Is.True, error);
                    Assert.That(Client.Connections.TryAddTOTPAuthentication(Login, sharedSecret, I18NString.Create(Description), out credentials, out error),        Is.True, error);

                    break;

                default:
                    throw new ArgumentException($"'{Authentication}' is no way of signing in these tests know.", nameof(Authentication));

            }

            return credentials!;

        }

        #endregion

        #region Until(Condition, What, params Nodes) / Story(params Nodes)

        /// <summary>
        /// Wait for what is to happen, and fail with what the given nodes said
        /// where it does not.
        /// </summary>
        protected static async Task Until(Func<Boolean>       Condition,
                                          String              What,
                                          params WWCPNode?[]  Nodes)
        {

            var until = DateTimeOffset.UtcNow + Waiting;

            while (!Condition())
            {

                if (DateTimeOffset.UtcNow > until)
                    Assert.Fail($"{What} did not happen within {Waiting.TotalSeconds:F0} s.{Story(Nodes)}");

                await Task.Delay(25);

            }

        }

        /// <summary>
        /// What the given nodes said, and what each said of its connections.
        /// </summary>
        protected static String Story(params WWCPNode?[] Nodes)

            => String.Join(
                   Environment.NewLine,
                   new[] { "", "What was said:" }.
                       Concat(Nodes.OfType<WWCPNode>().SelectMany(node =>
                                  node.Log.Recent(100).Select(entry => $"  {node.NodeNoun,-16} {entry.Timestamp:HH:mm:ss.fff} [{entry.LevelName}] {entry.Message}").
                                  Concat(node.OutboundConnections.Select(said => $"  {node.NodeNoun,-16} connection {said.Key}: {said.Value}"))))
               );

        #endregion

        #region StatusOf(Node, Connection) / RegistrationOf(Node) / IsConnectedAt(Server, Login)

        /// <summary>
        /// The state of a connection of the given node.
        /// </summary>
        protected static ConnectionStatus? StatusOf(WWCPNode  Node,
                                                    String    Connection)

            => Node.ConnectionStates.TryGetValue(Connection, out var state)
                   ? state.Status
                   : null;

        /// <summary>
        /// What the back end of the given node said to its BootNotification.
        /// </summary>
        protected static String RegistrationOf(OCPPServingNode Node)

            => Node.RegistrationJSON()["2.1"]?["state"]?.Value<String>() ?? "";

        /// <summary>
        /// Whether the station server of the given node has someone of the
        /// given login connected, over a WebSocket or over SSH.
        /// </summary>
        protected static Boolean IsConnectedAt(OCPPServingNode  Server,
                                               User_Id          Login)

            => (Server.StationServer?.   ConnectedNetworkingNodeIds ?? []).
                   Concat(Server.StationSSHServer?.ConnectedNetworkingNodeIds ?? []).
                   Any(id => id.ToString() == Login.ToString());

        #endregion

        #region NewNodeDirectory(Purpose, params Sections)

        /// <summary>
        /// A directory of its own for one node of one test, taken away again
        /// after the test: no two nodes - and no two tests - read each other's
        /// connections, logins or configuration.
        /// </summary>
        /// <remarks>
        /// The file asks no time server: it is read at construction, and it is
        /// Start() that would otherwise put a timer on the network.
        /// </remarks>
        /// <param name="Purpose">What the directory is for, "cs" or "csms", part of its name.</param>
        /// <param name="Sections">What else the file says, e.g. <see cref="StationServer"/>.</param>
        protected NodeDirectory NewNodeDirectory(String              Purpose,
                                                 params JProperty[]  Sections)
        {

            var directory   = TestParties.TemporaryDirectory(Purpose);

            nodeDirectories.Enqueue(directory);

            Directory.CreateDirectory(directory);

            var configFile  = Path.Combine(directory, WWCPConfigFile.DefaultFileName);

            File.WriteAllText(
                configFile,
                new JObject(
                    Sections.Prepend(new JProperty("nts", TestParties.NoTimeServers)).ToArray<Object>()
                ).ToString()
            );

            return new NodeDirectory(
                       Path.Combine(directory, "accounts"),
                       new WWCPConfigFile(configFile)
                   );

        }

        #endregion

        #region RemoveNodeDirectories()

        /// <summary>
        /// The test's node directories taken away - after the TearDown of the
        /// test itself, which NUnit runs first, so that the nodes have let go
        /// of their files.
        /// </summary>
        [TearDown]
        public void RemoveNodeDirectories()
        {

            while (nodeDirectories.TryDequeue(out var directory))
                TestParties.Remove(directory);

            // Its nodes have stopped: the CA their TLS put into the Windows
            // store comes out of it again.
            InstalledAuthorities.Remove(testCA);

            testCA?.   Dispose();
            testCAKey?.Dispose();

            testCA     = null;
            testCAKey  = null;

        }

        #endregion

    }

}
