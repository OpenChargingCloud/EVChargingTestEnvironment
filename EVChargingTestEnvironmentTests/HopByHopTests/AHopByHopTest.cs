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

    public abstract class AHopByHopTest : ATest
    {

        #region OverTLS / OverSSH

        /// <summary>
        /// Whether the running test connects over wss://: it is one named
        /// "WebSocketTLS" or "WebSocketMTLS", or one of its arguments says
        /// "wss". Known in [SetUp] already, so the node that is connected to gets
        /// a certificate before it starts.
        /// </summary>
        protected static Boolean OverTLS

            => TestContext.CurrentContext.Test.MethodName is "WebSocketTLS" or "WebSocketMTLS" ||
               TestContext.CurrentContext.Test.Arguments.Any(argument => argument as String == "wss");

        /// <summary>
        /// Whether the running test connects over wss:// and signs in with a
        /// client certificate: it is one named "WebSocketMTLS". The node that
        /// is connected to then lets in nothing else.
        /// </summary>
        protected static Boolean OverMTLS

            => TestContext.CurrentContext.Test.MethodName == "WebSocketMTLS";

        /// <summary>
        /// Whether the running test connects via ssh://: it is one named "SSH",
        /// or one of SFTP.
        /// </summary>
        protected static Boolean OverSSH

            => TestContext.CurrentContext.Test.MethodName == "SSH" || OverSFTP;

        /// <summary>
        /// Whether the running test moves files over SFTP, beside OCPP over
        /// SSH: its name begins with "SFTP_". The SSH server of the node that
        /// is connected to serves SFTP then.
        /// </summary>
        protected static Boolean OverSFTP

            => TestContext.CurrentContext.Test.MethodName?.StartsWith("SFTP_", StringComparison.Ordinal) == true;

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
        /// without TLS where none are given - and speaking OCPP 2.1 as JSON
        /// and as CBOR.
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
                   new JProperty("subprotocols",          new JArray("ocpp2.1", "ocpp2.0.1", "ocpp2.1cbor")),
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
        /// port of its own: where its station server takes OCPP-over-SSH -
        /// and, for a test of SFTP, files over SFTP.
        /// </summary>
        protected static JProperty SSHServer()
        {

            var ssh = new JObject(
                          new JProperty("enabled",  true),
                          new JProperty("port",     TestPorts.Free().ToUInt16())
                      );

            if (OverSFTP)
                ssh.Add("sftp", true);

            return new ("ssh", ssh);

        }

        #endregion

        #region NewServedNodeDirectory(Purpose, [ClientAuthentication,] params Sections)

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

            => NewServedNodeDirectory(
                   Purpose,
                   OverMTLS
                       ? "tlsClientCertificate"
                       : OverTLS
                             ? "httpAuthOverTLS"
                             : "httpAuthWithoutTLS",
                   Sections
               );

        /// <summary>
        /// The directory of a node that is connected to, letting in what signs in in
        /// the given way: a node further up a chain, say, that is connected to with
        /// a password while the node below it lets in client certificates.
        /// </summary>
        /// <param name="Purpose">What the directory is for, "cs" or "csms", part of its name.</param>
        /// <param name="ClientAuthentication">How a client may sign in: "httpAuthWithoutTLS", "httpAuthOverTLS", "tlsClientCertificate".</param>
        /// <param name="Sections">What else the file says, e.g. <see cref="ReportsUpwards"/>.</param>
        protected NodeDirectory NewServedNodeDirectory(String              Purpose,
                                                       String              ClientAuthentication,
                                                       params JProperty[]  Sections)
        {

            var nodeDirectory = NewNodeDirectory(
                                    Purpose,
                                    [
                                        StationServer(ClientAuthentication),
                                        .. (OverSSH ? new[] { SSHServer() } : Array.Empty<JProperty>()),
                                        .. Sections
                                    ]
                                );

            if (OverTLS)
                ACertificateOfTheTestCA(Path.GetDirectoryName(nodeDirectory.ConfigFile.Path)!);

            return nodeDirectory;

        }

        #endregion

        #region (private) TestCA / ACertificateOfTheTestCA(Directory, Listener = "ocpp")

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
        /// it starts: this is what makes its station port speak TLS - or the
        /// port of the given listener, as the WebSocket port of OCPI of an EMSP.
        /// </summary>
        /// <param name="Directory">The directory of the node's configuration file.</param>
        /// <param name="Listener">The listener the certificate is told: the station port's, "ocpp", where none is given.</param>
        protected void ACertificateOfTheTestCA(String   Directory,
                                               String?  Listener   = null)
        {

            var ca        = TestCA;
            var listener  = Listener ?? OCPPServingNode.StationListener;

            var store  = new CertificateStore(
                             Path.Combine(Directory, CertificatesConfiguration.DefaultDirectory),
                             new cloud.charging.open.protocols.WWCP.Node.Logging.EventLog(),
                             NodeName:      "Node",
                             Listeners:     [ listener ],
                             RequestKinds:  [ CertificateKind.TLSServerIdentity ]
                         );

            store.Reload();

            Assert.That(store.TryCreateRequest(CertificateKind.TLSServerIdentity,
                                               "CN=127.0.0.1",
                                               null,
                                               new CertificateRequestAsks(ReachableAs: [ "127.0.0.1" ]),
                                               [ listener ],
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

        #region WebSocketLoginsOf(Served) / ClientTrustOf(Served)

        /// <summary>
        /// The WebSocket logins of the node that is connected to: of its station
        /// port, or of the WebSocket port of OCPI of an EMSP.
        /// </summary>
        protected static WebSocketLogins WebSocketLoginsOf(WWCPNode Served)

            => Served switch {
                   OCPPServingNode  node  => node.WebSocketLogins,
                   EMSP.EMSP        emsp  => emsp.WebSocketLogins,
                   _                      => throw new ArgumentException($"'{Served.NodeNoun}' has no WebSocket logins these tests know.", nameof(Served))
               };

        /// <summary>
        /// Which chains the node that is connected to accepts a client
        /// certificate of.
        /// </summary>
        protected static ClientTrustStore ClientTrustOf(WWCPNode Served)

            => Served switch {
                   OCPPServingNode  node  => node.ClientTrust,
                   EMSP.EMSP        emsp  => emsp.ClientTrust,
                   _                      => throw new ArgumentException($"'{Served.NodeNoun}' accepts no client certificates these tests know.", nameof(Served))
               };

        #endregion

        #region ClientCertificate(Client, Served, Login)

        /// <summary>
        /// What the node that connects signs in with over mutual TLS: a key of
        /// its own, and for it a certificate of the test's CA that names the
        /// login - that name is who the node it connects to takes it for. The node
        /// it connects to believes what the test's CA vouches for.
        /// </summary>
        /// <returns>The certificate of the connecting node's store, to name as the connection's.</returns>
        protected String ClientCertificate(WWCPNode  Client,
                                           WWCPNode  Served,
                                           String    Subject)
        {

            String? error;

            // A node that makes no key for who it is - a CSMS - is given one,
            // as its operator would give it one: a key, and a certificate of
            // the test's CA for it, imported together.
            if (!Client.Certificates.RequestKinds.Contains(CertificateKind.TLSIdentity))
            {

                using var ownKey         = ECDsa.Create(ECCurve.NamedCurves.nistP256);

                var ownRequest           = new CertificateRequest($"CN={Subject}", ownKey, HashAlgorithmName.SHA256);

                ownRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                ownRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
                ownRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ new Oid("1.3.6.1.5.5.7.3.2") ], false));   // TLS client

                using var ownCertificate = ownRequest.Create(TestCA.SubjectName,
                                                          X509SignatureGenerator.CreateForECDsa(testCAKey!),
                                                          DateTimeOffset.UtcNow.AddDays(-1),
                                                          DateTimeOffset.UtcNow.AddDays(30),
                                                          Guid.NewGuid().ToByteArray());

                using var withKey        = ownCertificate.CopyWithPrivateKey(ownKey);

                Assert.That(Client.Certificates.Import(new X509Certificate2Collection { withKey, TestCA }.Export(X509ContentType.Pkcs12)!,
                                                        CertificateKind.TLSIdentity,
                                                        null,
                                                        Subject,
                                                        out var imported,
                                                        out error),
                            Is.True, error);

                Assert.That(ClientTrustOf(Served).TryAdd(TestCA.ExportCertificatePem(), "The CA of the test", out _, out _, out error),
                            Is.True, error);

                return imported!.Id;

            }

            Assert.That(Client.Certificates.TryCreateRequest(CertificateKind.TLSIdentity, Subject, null, null, null, null,
                                                              out var signingRequest, out error, out _),
                        Is.True, error);

            using var certificate = CertificateRequest.LoadSigningRequestPem(signingRequest!.CSR, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions).
                                        Create(TestCA.SubjectName,
                                               X509SignatureGenerator.CreateForECDsa(testCAKey!),
                                               DateTimeOffset.UtcNow.AddDays(-1),
                                               DateTimeOffset.UtcNow.AddDays(30),
                                               Guid.NewGuid().ToByteArray());

            Assert.That(Client.Certificates.TryAnswerRequest(certificate.ExportCertificatePem() + Environment.NewLine + TestCA.ExportCertificatePem(),
                                                              out var identity, out _, out _, out error, out _),
                        Is.True, error);

            Assert.That(identity, Is.Not.Null, "The signed certificate could not be put together with its key.");

            Assert.That(ClientTrustOf(Served).TryAdd(TestCA.ExportCertificatePem(), "The CA of the test", out _, out _, out error),
                        Is.True, error);

            return identity!.Id;

        }

        #endregion

        #region Credentials(Client, Served, Login, Authentication, Description)

        /// <summary>
        /// What the node that connects signs in with, and the login the node it
        /// connects to lets it in with: a password, or the secret of one-time
        /// passwords.
        /// </summary>
        /// <returns>The credentials of the connecting node's store.</returns>
        protected static String Credentials(WWCPNode    Client,
                                            WWCPNode    Served,
                                            User_Id     Login,
                                            String      Authentication,
                                            I18NString  Description)
        {

            String? credentials;
            String? error;

            switch (Authentication)
            {

                case authPassword:

                    var password = RandomExtensions.RandomString(32);

                    Assert.That(WebSocketLoginsOf(Served).TrySetPasswordAuthentication(Login, password, null, Description, out error),                           Is.True, error);
                    Assert.That(Client.Connections.    TryAddPasswordAuthentication(Login, password,       Description, out credentials, out error),       Is.True, error);

                    break;

                case authTOTP:

                    var sharedSecret = RandomExtensions.RandomString(16);

                    Assert.That(WebSocketLoginsOf(Served).TrySetTOTPAuthentication(Login, sharedSecret, null, null, null, null, null, Description, out error), Is.True, error);
                    Assert.That(Client.Connections.    TryAddTOTPAuthentication(Login, sharedSecret, Description, out credentials, out error),        Is.True, error);

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
        /// Whether the station server of the given node - with the servers of
        /// OCPI of a CSMS behind it, or the servers of OCPI of an EMSP - has
        /// someone of the given login connected, over a WebSocket or over SSH.
        /// </summary>
        protected static Boolean IsConnectedAt(WWCPNode  Server,
                                               User_Id   Login)

            => (Server switch {
                    CSMS.CSMS        csms  => (csms.StationServer?.ConnectedNetworkingNodeIds ?? []).
                                                  Concat(csms.StationSSHServer?.ConnectedNetworkingNodeIds ?? []).
                                                  Concat(csms.OCPIServers.   SelectMany(server => server.ConnectedNetworkingNodeIds)).
                                                  Concat(csms.OCPISshServers.SelectMany(server => server.ConnectedNetworkingNodeIds)),
                    OCPPServingNode  node  => (node.StationServer?.ConnectedNetworkingNodeIds ?? []).
                                                  Concat(node.StationSSHServer?.ConnectedNetworkingNodeIds ?? []),
                    EMSP.EMSP        emsp  => emsp.OCPIServers.   SelectMany(server => server.ConnectedNetworkingNodeIds).
                                                  Concat(emsp.OCPISshServers.SelectMany(server => server.ConnectedNetworkingNodeIds)),
                    _                      => []
                }).
                Any(networkingNodeId => networkingNodeId.ToString() == Login.ToString());

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

            testCA?.   Dispose();
            testCAKey?.Dispose();

            testCA     = null;
            testCAKey  = null;

        }

        #endregion

    }

}
