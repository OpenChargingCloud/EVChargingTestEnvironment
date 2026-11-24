/*
 * Copyright (c) 2014-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of EVChargingTestEnvironment <https://github.com/OpenChargingCloud/EVChargingTestEnvironment>
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#region Usings

using System.Text;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Hermod.SSH;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.SSH.Client;

using cloud.charging.open.protocols.WWCP.Node.PKI;
using cloud.charging.open.protocols.WWCP.Node.TestKit;
using cloud.charging.open.protocols.WWCP.Node.Connections;

using cloud.charging.open.PKI.Issuance;
using cloud.charging.open.PKI.Enrollment;
using cloud.charging.open.PKI.Revocation;

using PKINode  = cloud.charging.open.PKI.PKI;

#endregion

namespace cloud.charging.open.TestEnvironment.Tests.Connections.HopByHop
{

    /// <summary>
    /// A CSMS connects to its PKI as a registration authority - over ws://,
    /// wss://, wss:// with a client certificate and ssh:// - and asks it in the
    /// messages of a PKI, framed as OCPP-J frames its own: it has the CSR of a
    /// charging station signed, reads the CA certificates and the CRL, asks
    /// whether a certificate is revoked, and revokes one it had signed.
    /// </summary>
    [TestFixture]
    public class CSMS_2_PKI : AHopByHopTest
    {

        #region Data

        /// <summary>
        /// The label of EST the certificates of the charging stations are signed at.
        /// </summary>
        private const String  StationLabel  = "stations";

        private DNSClient?    dnsClient;
        private CSMS.CSMS?    csms;
        private PKINode?      pki;

        private IssuedCertificate?  root;
        private IssuedCertificate?  subCA;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public async Task Start()
        {

            dnsClient  = new DNSClient(SearchForIPv6DNSServers: false);

            pki        = NewPKI();

            await pki.Start();

            // A root, a sub-CA below it, and EST that signs the client
            // certificates of the stations below the sub-CA - at the label
            // "stations", and nothing without a label.
            Assert.That(pki.Store.TryCreateRootCA(new SubjectName("Station Root CA", "OpenChargingCloud", null, "DE"), KeyAlgorithm.ECC_P256, 3650, null, null, null,
                                                  out root, out var error, out _),
                        Is.True, error);

            Assert.That(pki.Store.TryIssue(root!.Id, null, CertificateProfile.SubCA, new SubjectName("Station Sub-CA", "OpenChargingCloud", null, "DE"), KeyAlgorithm.ECC_P256, 1460, [], null, null, null,
                                           out subCA, out error, out _),
                        Is.True, error);

            Assert.That(pki.TrySetEST(new ESTSettings(null,
                                                      new Dictionary<String, ESTProfile> {
                                                          { StationLabel, new ESTProfile(subCA!.Id, CertificateProfile.Client, 365) }
                                                      },
                                                      false),
                                      out error),
                        Is.True, error);

            // The CSMS with its station server, for the stations whose CSRs it
            // sends on to the PKI.
            var directory  = NewNodeDirectory("csms", StationServer());

            csms  = new CSMS.CSMS(
                        DNSClient:       dnsClient,
                        HTTPPort:        TestPorts.Free(),
                        AccountsPath:    directory.Accounts,
                        ConfigFile:      directory.ConfigFile,
                        LogToConsole:    false,
                        BridgeDebugLog:  false
                    );

            await csms.Start();

        }

        [TearDown]
        public async Task Stop()
        {

            if (csms is not null)
            {
                await csms.Stop();
                await csms.DisposeAsync();
            }

            if (pki is not null)
            {
                await pki.Stop();
                await pki.DisposeAsync();
            }

            csms = null;
            pki  = null;

        }

        #endregion


        #region (private) NewPKI()

        /// <summary>
        /// A PKI of its own directory: its WebSocket port of the registration
        /// authorities switched on - over TLS, with a certificate of the test's
        /// CA, where the test connects over wss:// - and its SSH server, serving
        /// the subsystem of the messages of a PKI.
        /// </summary>
        private PKINode NewPKI()
        {

            var directory  = NewNodeDirectory(
                                 "pki",
                                 new JProperty("webSocketServer", new JObject(
                                     new JProperty("enabled",               true),
                                     new JProperty("address",               "127.0.0.1"),
                                     new JProperty("port",                  TestPorts.Free().ToUInt16()),
                                     new JProperty("clientAuthentication",  new JArray(OverMTLS
                                                                                          ? "tlsClientCertificate"
                                                                                          : OverTLS
                                                                                                ? "httpAuthOverTLS"
                                                                                                : "httpAuthWithoutTLS")),
                                     new JProperty("ssh",                   new JObject(
                                         new JProperty("enabled",           true)
                                     ))
                                 )),
                                 new JProperty("ssh", new JObject(
                                     new JProperty("enabled",  true),
                                     new JProperty("port",     TestPorts.Free().ToUInt16())
                                 ))
                             );

            if (OverTLS)
                ACertificateOfTheTestCA(Path.GetDirectoryName(directory.ConfigFile.Path)!, PKINode.RAListener);

            return new PKINode(
                       HTTPPort:        TestPorts.Free(),
                       AccountsPath:    directory.Accounts,
                       ConfigFile:      directory.ConfigFile,
                       DNSClient:       dnsClient,
                       LogToConsole:    false,
                       BridgeDebugLog:  false
                   );

        }

        #endregion

        #region (private static) ACSR(CommonName)

        /// <summary>
        /// The CSR of a charging station, for a key of ECC P-256 it made.
        /// </summary>
        private static String ACSR(String CommonName)
        {

            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            return new CertificateRequest($"CN={CommonName}, O=GraphDefined CPO, C=DE", key, HashAlgorithmName.SHA256).
                       CreateSigningRequestPem();

        }

        #endregion


        #region (private) TheCSMSConnects(Authentication, Transport, Enabled = true)

        /// <summary>
        /// The PKI writes the CSMS down as a registration authority that
        /// connects, and the CSMS connects to the PKI - over a WebSocket of the
        /// messages of a PKI with a password, a one-time password or a
        /// certificate, or over SSH with a password, a one-time password or its
        /// own key.
        /// </summary>
        /// <param name="Authentication">How the CSMS signs in: "password", "totp", "publicKey" - or "mTLS".</param>
        /// <param name="Transport">"ws", "wss" or "ssh".</param>
        /// <param name="Enabled">Whether the PKI's connection of the CSMS is switched on.</param>
        /// <returns>The login, the connection at the CSMS and the one at the PKI.</returns>
        private async Task<(User_Id Login, String Connection, String Inbound)> TheCSMSConnects(String   Authentication,
                                                                                              String   Transport,
                                                                                              Boolean  Enabled = true)
        {

            var csms         = this.csms!;
            var pki          = this.pki!;

            var login        = User_Id.Parse("csms-" + RandomExtensions.RandomString(8));
            var description  = I18NString.Create($"The CSMS --({Transport}/{Authentication})--> the PKI");
            var error        = "";

            var door         = Transport == "ssh"
                                   ? $"ssh://{login}@127.0.0.1:{pki.SSHPort!.Value.ToUInt16()}"
                                   : $"{Transport}://127.0.0.1:{pki.RAServerSettings.TCPPort!.Value.ToUInt16()}/{login}";

            if (Transport == "ssh")
                Assert.That(pki.PKIOverSSHServed, Is.True, "The messages of the PKI over SSH are switched on in the file, and not served.");
            else
                Assert.That(pki.RAServerRunning,  Is.True, "The WebSocket server of the PKI is switched on in the file, and not running.");

            if (Transport == "wss")
                Assert.That(pki.RAServerTLS, Is.True, "The WebSocket port of the PKI does not speak TLS.");

            BelievesTheTestCA(csms);

            // The PKI: the CSMS, written down as a registration authority.
            Assert.That(
                pki.Connections.TryAddConnection(
                    I18NString.Create("The CSMS, a registration authority"),
                    URL.Parse(door),
                    "RegistrationAuthority",
                    Enabled,
                    null,
                    null,
                    out var inbound,
                    out error,
                    Inbound: true
                ),
                Is.True, error
            );

            // What the CSMS signs in with.
            String? credentials  = null;
            String? certificate  = null;

            switch (Authentication)
            {

                case authPublicKey:

                    var key = await csms.SshKey.MakeIfNeeded();

                    Assert.That(
                        pki.WebSocketLogins.TryAddSSHKey(login,
                                                         SshPublicKey.FromHostKey(key).ToAuthorizedKeyLine(),
                                                         "test",
                                                         null,
                                                         description,
                                                         "tests",
                                                         out _,
                                                         out error,
                                                         out _),
                        Is.True, error
                    );

                    break;

                case authMTLS:

                    Assert.That(pki.WebSocketLogins.TrySetPasswordAuthentication(login, RandomExtensions.RandomString(32), null, description, out error), Is.True, error);

                    certificate = ClientCertificate(csms, pki, login.ToString());

                    break;

                default:

                    credentials = Credentials(csms, pki, login, Authentication, description);

                    break;

            }

            // The CSMS: the connection to the PKI, and what it signs in with.
            Assert.That(
                csms.Connections.TryAddConnection(
                    Description:       description,
                    URL:               URL.Parse(door),
                    ConnectionType:    "PKI",
                    AutoConnect:       true,
                    AuthenticationId:  credentials,
                    CertificateId:     certificate,
                    Id:                out var connection,
                    Error:             out error,
                    SshHostKey:        Transport == "ssh" ? pki.SSHHostKey!.Split(' ', 2)[1] : null
                ),
                Is.True, error
            );

            if (Enabled)
            {
                await Until(() => StatusOf(csms, connection!) == ConnectionStatus.Connected,  $"'{login}' connecting over {Transport}",  csms, pki);
                await Until(() => StatusOf(pki,  inbound!)    == ConnectionStatus.Connected,  $"'{login}' being let in by the PKI",      csms, pki);
            }

            return (login, connection!, inbound!);

        }

        #endregion

        #region (private) AssertConnectedAndAsking(Login, Connection, Inbound, Transport)

        /// <summary>
        /// Both ends see the connection as up - and through it, the CSMS has the
        /// CSR of a station signed below the sub-CA, reads the CA certificates,
        /// asks the status of what was signed, revokes it, asks again, and reads
        /// the CRL that says so.
        /// </summary>
        private async Task AssertConnectedAndAsking(User_Id  Login,
                                                    String   Connection,
                                                    String   Inbound,
                                                    String   Transport)
        {

            var csms   = this.csms!;
            var pki    = this.pki!;

            var entry  = csms.Connections.Connections.First(connection => connection.Id == Connection);

            Assert.Multiple(() => {

                Assert.That(StatusOf(csms, Connection),         Is.EqualTo(ConnectionStatus.Connected),  $"The CSMS does not see the connection as up.{Story(csms, pki)}");
                Assert.That(StatusOf(pki,  Inbound),            Is.EqualTo(ConnectionStatus.Connected),  $"The PKI does not see the connection as up.{Story(csms, pki)}");
                Assert.That(IsConnectedAt(pki, Login),          Is.True,                                 $"The PKI did not let '{Login}' in.{Story(csms, pki)}");

                Assert.That(entry.Use,                          Is.EqualTo(ConnectionUse.PKI));
                Assert.That(csms.PKITransportOf(Connection),    Is.EqualTo(Transport switch {
                                                                               "ssh"  => "SSH",
                                                                               "wss"  => "WebSocket TLS",
                                                                               _      => "WebSocket"
                                                                           }));

            });

            // SignCertificate: the CSR of a station, signed below the sub-CA.
            var signed = await csms.SignCertificateAtThePKI(Connection, new SignCertificateRequest(ACSR("CS-0001"), StationLabel));

            Assert.That(signed.Status, Is.EqualTo(PKIStatus.Accepted), $"The PKI did not sign: {signed.StatusInfo?.AdditionalInfo}{Story(csms, pki)}");

            var chain        = new X509Certificate2Collection();
            chain.ImportFromPem(signed.CertificateChain!);

            var certificate  = pki.Store.Get(signed.CertificateId!);

            Assert.Multiple(() => {
                Assert.That(chain.Count,                                     Is.EqualTo(2),  "The chain is not the certificate and the sub-CA.");
                Assert.That(chain[0].GetNameInfo(X509NameType.SimpleName, false), Is.EqualTo("CS-0001"));
                Assert.That(chain[0].Issuer,                                 Is.EqualTo(chain[1].Subject));
                Assert.That(chain[1].GetCertHashString(HashAlgorithmName.SHA256), Is.EqualTo(subCA!.Id).IgnoreCase);
                Assert.That(certificate?.Profile,                            Is.EqualTo(CertificateProfile.Client));
                Assert.That(certificate?.CreatedBy,                          Is.EqualTo(PKINode.CreatedByPrefix + Login));
            });

            // GetCACertificates: the sub-CA and its root.
            var cas = await csms.GetCACertificatesOfThePKI(Connection, new GetCACertificatesRequest(StationLabel));

            var caChain = new X509Certificate2Collection();
            caChain.ImportFromPem(cas.CertificateChain!);

            Assert.Multiple(() => {
                Assert.That(cas.Status,                                                  Is.EqualTo(PKIStatus.Accepted));
                Assert.That(caChain.Select(ca => ca.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant()),
                            Is.EqualTo(new[] { subCA!.Id.ToLowerInvariant(), root!.Id.ToLowerInvariant() }));
            });

            // GetCertificateStatus: valid, by its id and by its serial number.
            var status   = await csms.GetCertificateStatusAtThePKI(Connection, new GetCertificateStatusRequest(CertificateId: signed.CertificateId));
            var bySerial = await csms.GetCertificateStatusAtThePKI(Connection, new GetCertificateStatusRequest(SerialNumber:  chain[0].SerialNumber));

            Assert.Multiple(() => {
                Assert.That(status.  CertificateStatus,  Is.EqualTo("valid"));
                Assert.That(bySerial.CertificateStatus,  Is.EqualTo("valid"));
                Assert.That(bySerial.CertificateId,      Is.EqualTo(signed.CertificateId));
            });

            // RevokeCertificate: what it had signed.
            var revoked = await csms.RevokeCertificateAtThePKI(Connection, new RevokeCertificateRequest(CertificateId: signed.CertificateId, Reason: "cessationOfOperation"));

            Assert.That(revoked.Status, Is.EqualTo(PKIStatus.Accepted), revoked.StatusInfo?.AdditionalInfo);

            status = await csms.GetCertificateStatusAtThePKI(Connection, new GetCertificateStatusRequest(CertificateId: signed.CertificateId));

            Assert.Multiple(() => {
                Assert.That(status.CertificateStatus,  Is.EqualTo("revoked"));
                Assert.That(status.RevocationReason,   Is.EqualTo("cessationOfOperation"));
                Assert.That(status.RevokedAt,          Is.Not.Null);
            });

            // GetCRL: the CRL of the sub-CA, which names it now.
            var crl = await csms.GetCRLOfThePKI(Connection, new GetCRLRequest(Label: StationLabel));

            Assert.Multiple(() => {
                Assert.That(crl.Status,    Is.EqualTo(PKIStatus.Accepted), crl.StatusInfo?.AdditionalInfo);
                Assert.That(crl.IssuerId,  Is.EqualTo(subCA!.Id));
                Assert.That(crl.CRL,       Does.StartWith("-----BEGIN X509 CRL-----"));
            });

            var der = Convert.FromBase64String(String.Concat(crl.CRL!.Split('\n').Where(line => !line.StartsWith("-----")).Select(line => line.Trim())));

            Assert.That(CertificateRevocationListBuilder.Load(der, out _).RemoveEntry(chain[0].SerialNumberBytes.Span),
                        Is.True,
                        "The CRL of the sub-CA does not name the certificate revoked.");

        }

        #endregion


        #region WebSocket     (Authentication)

        /// <summary>
        /// The CSMS connects to the PKI over a WebSocket of the messages of a
        /// PKI, with a password or a one-time password, and asks it.
        /// </summary>
        [TestCase(authPassword)]
        [TestCase(authTOTP)]
        public async Task WebSocket(String Authentication)
        {

            var (login, connection, inbound) = await TheCSMSConnects(Authentication, "ws");

            await AssertConnectedAndAsking(login, connection, inbound, "ws");

        }

        #endregion

        #region WebSocketTLS  (Authentication)

        /// <summary>
        /// The CSMS connects to the PKI over a WebSocket of the messages of a
        /// PKI over TLS, with a password or a one-time password, believing the
        /// PKI by the test's CA, and asks it.
        /// </summary>
        [TestCase(authPassword)]
        [TestCase(authTOTP)]
        public async Task WebSocketTLS(String Authentication)
        {

            var (login, connection, inbound) = await TheCSMSConnects(Authentication, "wss");

            await AssertConnectedAndAsking(login, connection, inbound, "wss");

        }

        #endregion

        #region WebSocketMTLS ()

        /// <summary>
        /// The CSMS connects to the PKI over a WebSocket of the messages of a
        /// PKI over mutual TLS: it signs in with a certificate of the test's CA
        /// that names its login.
        /// </summary>
        [Test]
        public async Task WebSocketMTLS()
        {

            var (login, connection, inbound) = await TheCSMSConnects(authMTLS, "wss");

            await AssertConnectedAndAsking(login, connection, inbound, "wss");

        }

        #endregion

        #region SSH           (Authentication)

        /// <summary>
        /// The CSMS connects to the subsystem of the messages of a PKI of the
        /// PKI's SSH server, pinning the key the server shows - with its
        /// password, with a one-time password, or with its own key - and asks it.
        /// </summary>
        [TestCase(authPassword)]
        [TestCase(authTOTP)]
        [TestCase(authPublicKey)]
        public async Task SSH(String Authentication)
        {

            var (login, connection, inbound) = await TheCSMSConnects(Authentication, "ssh");

            await AssertConnectedAndAsking(login, connection, inbound, "ssh");

        }

        #endregion


        #region PKI_AnUnknownLabel_IsRejected(Transport)

        /// <summary>
        /// A CSR at a label EST does not sign at - or without a label, where the
        /// PKI signs nothing without one - is rejected, and nothing is signed.
        /// </summary>
        [TestCase("ws")]
        [TestCase("ssh")]
        public async Task PKI_AnUnknownLabel_IsRejected(String Transport)
        {

            var csms   = this.csms!;
            var pki    = this.pki!;
            var count  = pki.Store.Count;

            var (_, connection, _) = await TheCSMSConnects(authPassword, Transport);

            var unknown  = await csms.SignCertificateAtThePKI(connection, new SignCertificateRequest(ACSR("CS-0002"), "nowhere"));
            var noLabel  = await csms.SignCertificateAtThePKI(connection, new SignCertificateRequest(ACSR("CS-0003")));

            Assert.Multiple(() => {
                Assert.That(unknown.Status,                  Is.EqualTo(PKIStatus.Rejected));
                Assert.That(unknown.StatusInfo?.ReasonCode,  Is.EqualTo("UnknownLabel"));
                Assert.That(noLabel.Status,                  Is.EqualTo(PKIStatus.Rejected));
                Assert.That(pki.Store.Count,                 Is.EqualTo(count), "The PKI signed something.");
            });

        }

        #endregion

        #region PKI_ABrokenCSR_IsRejected()

        /// <summary>
        /// What is no CSR is rejected as one.
        /// </summary>
        [Test]
        public async Task PKI_ABrokenCSR_IsRejected()
        {

            var (_, connection, _) = await TheCSMSConnects(authPassword, "ws");

            var answer = await csms!.SignCertificateAtThePKI(connection, new SignCertificateRequest("-----BEGIN CERTIFICATE REQUEST-----\nnonsense\n-----END CERTIFICATE REQUEST-----", StationLabel));

            Assert.Multiple(() => {
                Assert.That(answer.Status,                  Is.EqualTo(PKIStatus.Rejected));
                Assert.That(answer.StatusInfo?.ReasonCode,  Is.EqualTo("InvalidCSR"));
            });

        }

        #endregion

        #region PKI_WhatItDidNotSign_IsNotRevoked(Transport)

        /// <summary>
        /// A registration authority revokes what it had signed, and nothing
        /// else: a certificate the people of the PKI signed stays valid.
        /// </summary>
        [TestCase("ws")]
        [TestCase("ssh")]
        public async Task PKI_WhatItDidNotSign_IsNotRevoked(String Transport)
        {

            var csms  = this.csms!;
            var pki   = this.pki!;

            Assert.That(pki.Store.TryIssue(subCA!.Id, null, CertificateProfile.Client, new SubjectName("CS-9999", "OpenChargingCloud", null, "DE"), KeyAlgorithm.ECC_P256, 30, [], null, null, "test",
                                           out var other, out var error, out _),
                        Is.True, error);

            var (_, connection, _) = await TheCSMSConnects(authPassword, Transport);

            var answer = await csms.RevokeCertificateAtThePKI(connection, new RevokeCertificateRequest(CertificateId: other!.Id, Reason: "keyCompromise"));

            Assert.Multiple(() => {
                Assert.That(answer.Status,                  Is.EqualTo(PKIStatus.Rejected));
                Assert.That(answer.StatusInfo?.ReasonCode,  Is.EqualTo("NotYours"));
                Assert.That(pki.Store.RevocationOf(other.Id), Is.Null, "The certificate was revoked.");
            });

        }

        #endregion

        #region PKI_AnUnknownAction_IsNotImplemented()

        /// <summary>
        /// An action no PKI is asked is answered with the error NotImplemented,
        /// as OCPP-J answers one - and the connection stays up.
        /// </summary>
        [Test]
        public async Task PKI_AnUnknownAction_IsNotImplemented()
        {

            var csms = this.csms!;

            var (_, connection, _) = await TheCSMSConnects(authPassword, "ws");

            var problem = await Assert.ThrowsAsync<PKIException>(async () => await csms.AskThePKI(connection, "MakeMeARootCA", []));

            Assert.That(problem!.Code, Is.EqualTo(PKIErrorCodes.NotImplemented));

            var status = await csms.GetCertificateStatusAtThePKI(connection, new GetCertificateStatusRequest(SerialNumber: "00"));

            Assert.That(status.CertificateStatus, Is.EqualTo("unknown"));

        }

        #endregion

        #region PKI_ASwitchedOffConnection_IsNotLetIn(Transport)

        /// <summary>
        /// A registration authority whose connection the PKI switched off is not
        /// let in - over a WebSocket it is turned away for now, and goes on
        /// trying, over SSH nothing is served on its subsystem - and has nothing
        /// signed.
        /// </summary>
        [TestCase("ws")]
        [TestCase("ssh")]
        public async Task PKI_ASwitchedOffConnection_IsNotLetIn(String Transport)
        {

            var csms  = this.csms!;
            var pki   = this.pki!;
            var count = pki.Store.Count;

            var (login, connection, inbound) = await TheCSMSConnects(authPassword, Transport, Enabled: false);

            if (Transport == "ws")
                await Until(() => StatusOf(csms, connection) == ConnectionStatus.Trying &&
                                  csms.ConnectionStates.TryGetValue(connection, out var state) && state.Said.Contains("turned it away for now"),
                            $"'{login}' being turned away for now", csms, pki);

            Assert.That(async () => await csms.SignCertificateAtThePKI(connection, new SignCertificateRequest(ACSR("CS-0004"), StationLabel), TimeSpan.FromSeconds(3)),
                        Throws.Exception,
                        "The CSMS had a CSR signed through a connection that is switched off.");

            Assert.Multiple(() => {
                Assert.That(pki.Store.Count,           Is.EqualTo(count),                           "The PKI signed something.");
                Assert.That(StatusOf(pki, inbound),    Is.Not.EqualTo(ConnectionStatus.Connected),  "The PKI let in a connection that is switched off.");
                Assert.That(IsConnectedAt(pki, login), Is.False,                                    $"The PKI let '{login}' in.");
            });

        }

        #endregion

        #region (private) AStation(Id) / SendTo(Station, Message) / ReceiveFrom(Station)

        private const String StationPassword = "a-password-long-enough-for-ocpp";

        /// <summary>
        /// A charging station of OCPP 2.1 connected to the CSMS: as much of one
        /// as sends a SignCertificate request and takes CertificateSigned.
        /// </summary>
        private async Task<ClientWebSocket> AStation(String Id)
        {

            Assert.That(csms!.WebSocketLogins.TrySetPasswordAuthentication(User_Id.Parse(Id), StationPassword, null, null, out var error), Is.True, error);

            var station = new ClientWebSocket();
            station.Options.AddSubProtocol("ocpp2.1");
            station.Options.SetRequestHeader("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Id}:{StationPassword}")));

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await station.ConnectAsync(new Uri($"ws://127.0.0.1:{csms.OCPPServerSettings.TCPPort!.Value.ToUInt16()}"), timeout.Token);

            return station;

        }

        private static async Task SendTo(ClientWebSocket Station, JArray Message)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Station.SendAsync(Encoding.UTF8.GetBytes(Message.ToString(Newtonsoft.Json.Formatting.None)), WebSocketMessageType.Text, true, timeout.Token);
        }

        /// <summary>
        /// The next OCPP message of the CSMS to the station.
        /// </summary>
        private static async Task<JArray> ReceiveFrom(ClientWebSocket Station)
        {

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var buffer        = new Byte[64 * 1024];
            var message       = new MemoryStream();

            while (true)
            {

                var received = await Station.ReceiveAsync(buffer, timeout.Token);

                if (received.MessageType == WebSocketMessageType.Close)
                    throw new InvalidOperationException("The CSMS closed the connection of the station.");

                message.Write(buffer, 0, received.Count);

                if (received.EndOfMessage)
                    return JArray.Parse(Encoding.UTF8.GetString(message.ToArray()));

            }

        }

        #endregion

        #region (private) TheCSMSSignsAt(Connection, Label)

        /// <summary>
        /// The CSMS told to send the CSRs of its stations to the PKI over the
        /// given connection, at the given label of EST.
        /// </summary>
        private void TheCSMSSignsAt(String  Connection,
                                    String  Label)
        {

            var url = csms!.Connections.Connections.First(entry => entry.Id == Connection).URL.ToString();

            Assert.That(csms.TryUpdateCertificateSigning(new JObject(new JProperty("chargingStationCertificate", $"{url}#{Label}")), out var error, out _),
                        Is.True, error);

        }

        #endregion


        #region Station_SignCertificate_IsSignedByThePKI(Transport)

        /// <summary>
        /// A charging station sends SignCertificate to the CSMS; the CSMS sends
        /// its CSR on to the PKI over its connection to it - a WebSocket or SSH -
        /// and hands the certificate the PKI signed, with the sub-CA above it, to
        /// the station with CertificateSigned.
        /// </summary>
        [TestCase("ws")]
        [TestCase("wss")]
        [TestCase("ssh")]
        public async Task Station_SignCertificate_IsSignedByThePKI(String Transport)
        {

            var csms  = this.csms!;
            var pki   = this.pki!;

            var (login, connection, _) = await TheCSMSConnects(authPassword, Transport);

            TheCSMSSignsAt(connection, StationLabel);

            var stationId     = "cs-" + RandomExtensions.RandomString(6).ToLowerInvariant();
            using var station = await AStation(stationId);
            using var key     = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            var csr           = new CertificateRequest($"CN={stationId}, O=GraphDefined CPO, C=DE", key, HashAlgorithmName.SHA256).CreateSigningRequestPem();

            await SendTo(station, new JArray(2, "m1", "SignCertificate", new JObject(new JProperty("csr",              csr),
                                                                                     new JProperty("certificateType",  "ChargingStationCertificate"))));

            var answer = await ReceiveFrom(station);

            Assert.That(answer[2]?["status"]?.Value<String>(), Is.EqualTo("Accepted"), answer.ToString());

            var signed = await ReceiveFrom(station);

            Assert.That(signed[2]?.Value<String>(), Is.EqualTo("CertificateSigned"), $"{signed}{Story(csms, pki)}");

            await SendTo(station, new JArray(3, signed[1]!.Value<String>()!, new JObject(new JProperty("status", "Accepted"))));

            var chain = new X509Certificate2Collection();
            chain.ImportFromPem(signed[3]!["certificateChain"]!.Value<String>()!);

            using var leaf       = chain[0];
            using var publicKey  = leaf.GetECDsaPublicKey()!;
            var fingerprint      = leaf.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant();

            Assert.Multiple(() => {
                Assert.That(leaf.GetNameInfo(X509NameType.SimpleName, false),  Is.EqualTo(stationId));
                Assert.That(publicKey.ExportSubjectPublicKeyInfo(),            Is.EqualTo(key.ExportSubjectPublicKeyInfo()), "The certificate is not for the station's own key.");
                Assert.That(chain.Count,                                       Is.EqualTo(2), "The chain is not the certificate and the sub-CA, without the root.");
                Assert.That(chain[1].GetCertHashString(HashAlgorithmName.SHA256), Is.EqualTo(subCA!.Id).IgnoreCase);
                Assert.That(pki.Store.Get(fingerprint)?.CreatedBy,             Is.EqualTo(PKINode.CreatedByPrefix + login), "The PKI did not sign it for the CSMS.");
            });

            await Until(() => csms.CertificateSignings.FirstOrDefault()?.Outcome == "signed", "the CSMS noting the certificate as taken", csms, pki);

            Assert.That(csms.CertificateSignings.First().Certificate, Is.EqualTo(fingerprint));

        }

        #endregion

        #region Station_GetCertificateStatus_IsAnsweredByThePKI(Transport)

        /// <summary>
        /// A charging station asks the CSMS with GetCertificateStatus whether
        /// its certificate is revoked; the CSMS asks the OCSP responder of the
        /// PKI over its connection to it - a WebSocket or SSH -, though the
        /// station names a responder the CSMS would not ask over HTTP, and hands
        /// the signed answer back: good, and revoked once it is.
        /// </summary>
        [TestCase("ws")]
        [TestCase("wss")]
        [TestCase("ssh")]
        public async Task Station_GetCertificateStatus_IsAnsweredByThePKI(String Transport)
        {

            var csms  = this.csms!;
            var pki   = this.pki!;

            var (_, connection, _) = await TheCSMSConnects(authPassword, Transport);

            TheCSMSSignsAt(connection, StationLabel);

            Assert.That(pki.Store.RefreshRevocation(), Is.Empty, "The PKI did not make the OCSP responder of its sub-CA.");

            var stationId     = "cs-" + RandomExtensions.RandomString(6).ToLowerInvariant();
            using var station = await AStation(stationId);

            var signed        = await csms.SignCertificateAtThePKI(connection, new SignCertificateRequest(ACSR(stationId), StationLabel));
            Assert.That(signed.Status, Is.EqualTo(PKIStatus.Accepted), $"The PKI did not sign: {signed.StatusInfo?.AdditionalInfo}{Story(csms, pki)}");

            var chain         = new X509Certificate2Collection();
            chain.ImportFromPem(signed.CertificateChain!);

            using var leaf    = chain[0];
            using var ca      = chain[1];
            using var signer  = pki.Store.ResponderOf(subCA!.Id)!;
            var url           = csms.Connections.Connections.First(entry => entry.Id == connection).URL.ToString().TrimEnd('/');

            async Task<OCSPSingleResponse> Asked(String MessageId, String Expected)
            {

                await SendTo(station, AskedAbout(MessageId, leaf, ca, "http://ocsp.nowhere.example/"));

                var answer = await ReceiveFrom(station);

                Assert.That(answer[2]?["status"]?.Value<String>(), Is.EqualTo("Accepted"), $"{answer}{Story(csms, pki)}");
                Assert.That(OCSP.TryParseResponse(Convert.FromBase64String(answer[2]!["ocspResult"]!.Value<String>()!), out var read, out var error), Is.True, error);

                var entry = csms.CertificateStatuses.First();

                Assert.Multiple(() => {
                    Assert.That(read!.Status,            Is.EqualTo(OCSPResponseStatus.Successful));
                    Assert.That(read.IsSignedBy(signer), Is.True, "The answer is not signed by the OCSP responder of the sub-CA.");
                    Assert.That(read.Responses,          Has.Count.EqualTo(1));
                    Assert.That(entry.Outcome,           Is.EqualTo(Expected));
                    Assert.That(entry.Via,               Is.EqualTo(url), "The CSMS did not ask over its connection to the PKI.");
                });

                return read!.Responses[0];

            }

            Assert.That((await Asked("m1", "good")).Status, Is.EqualTo(OCSPCertStatus.Good));

            var revoked = await csms.RevokeCertificateAtThePKI(connection, new RevokeCertificateRequest(SerialNumber: leaf.SerialNumber, Reason: "keyCompromise"));
            Assert.That(revoked.Status, Is.EqualTo(PKIStatus.Accepted), revoked.StatusInfo?.AdditionalInfo);

            Assert.That((await Asked("m2", "revoked")).Status, Is.EqualTo(OCSPCertStatus.Revoked));

        }

        #endregion

        #region Station_GetCertificateStatus_OfACAThePKIDoesNotHave_IsNotAsked()

        /// <summary>
        /// Where the station asks about a certificate below a CA the PKI does
        /// not have, and names a responder the CSMS does not ask over HTTP, it
        /// is answered Failed: the CSMS asks nowhere else on a station's word.
        /// </summary>
        [Test]
        public async Task Station_GetCertificateStatus_OfACAThePKIDoesNotHave_IsNotAsked()
        {

            var csms  = this.csms!;
            var pki   = this.pki!;

            var (_, connection, _) = await TheCSMSConnects(authPassword, "ws");

            TheCSMSSignsAt(connection, StationLabel);

            var stationId       = "cs-" + RandomExtensions.RandomString(6).ToLowerInvariant();
            using var station   = await AStation(stationId);

            using var key       = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var stranger  = new CertificateRequest("CN=Someone else's CA", key, HashAlgorithmName.SHA256).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

            await SendTo(station, AskedAbout("m1", stranger, stranger, "http://ocsp.nowhere.example/"));

            var answer = await ReceiveFrom(station);

            Assert.Multiple(() => {
                Assert.That(answer[2]?["status"]?.Value<String>(),  Is.EqualTo("Failed"), answer.ToString());
                Assert.That(csms.CertificateStatuses.First().Outcome, Is.EqualTo("refused"));
                Assert.That(csms.CertificateStatuses.First().Via,     Is.Null);
            });

        }

        #endregion

        #region (private) AskedAbout(MessageId, Certificate, Issuer, Responder)

        /// <summary>
        /// The GetCertificateStatus of a station: whether the given certificate,
        /// below the given CA, is revoked - asked of the given responder.
        /// </summary>
        private static JArray AskedAbout(String            MessageId,
                                         X509Certificate2  Certificate,
                                         X509Certificate2  Issuer,
                                         String            Responder)

            => new (2, MessageId, "GetCertificateStatus",
                    new JObject(
                        new JProperty("ocspRequestData", new JObject(
                            new JProperty("hashAlgorithm",   "SHA256"),
                            new JProperty("issuerNameHash",  Convert.ToHexString(SHA256.HashData(Issuer.SubjectName.RawData)).ToLowerInvariant()),
                            new JProperty("issuerKeyHash",   Convert.ToHexString(SHA256.HashData(Issuer.PublicKey.EncodedKeyValue.RawData)).ToLowerInvariant()),
                            new JProperty("serialNumber",    Certificate.SerialNumber),
                            new JProperty("responderURL",    Responder)
                        ))
                    ));

        #endregion

        #region Station_SignCertificate_AtALabelThePKIDoesNotSignAt_Fails()

        /// <summary>
        /// Where the PKI signs nothing at the label the CSMS asks at, the station
        /// is answered Accepted - the CSR goes on - but gets no certificate, and
        /// the CSMS notes why.
        /// </summary>
        [Test]
        public async Task Station_SignCertificate_AtALabelThePKIDoesNotSignAt_Fails()
        {

            var csms  = this.csms!;
            var pki   = this.pki!;
            var count = pki.Store.Count;

            var (_, connection, _) = await TheCSMSConnects(authPassword, "ws");

            TheCSMSSignsAt(connection, "nowhere");

            var stationId     = "cs-" + RandomExtensions.RandomString(6).ToLowerInvariant();
            using var station = await AStation(stationId);

            await SendTo(station, new JArray(2, "m1", "SignCertificate", new JObject(new JProperty("csr", ACSR(stationId)))));

            var answer = await ReceiveFrom(station);

            Assert.That(answer[2]?["status"]?.Value<String>(), Is.EqualTo("Accepted"), answer.ToString());

            await Until(() => csms.CertificateSignings.FirstOrDefault()?.Outcome == "failed", "the CSMS noting that the PKI did not sign", csms, pki);

            Assert.Multiple(() => {
                Assert.That(csms.CertificateSignings.First().Detail,  Does.Contain("UnknownLabel"));
                Assert.That(pki.Store.Count,                          Is.EqualTo(count), "The PKI signed something.");
            });

        }

        #endregion

        #region Station_SignCertificate_WithoutAConnectionOfThatURL_Fails()

        /// <summary>
        /// An address of a connection the CSMS has not written down is no PKI:
        /// the CSR goes nowhere, and the CSMS says which connection is missing.
        /// </summary>
        [Test]
        public async Task Station_SignCertificate_WithoutAConnectionOfThatURL_Fails()
        {

            var csms = this.csms!;

            Assert.That(csms.TryUpdateCertificateSigning(new JObject(new JProperty("chargingStationCertificate", "wss://127.0.0.1:1/nobody#stations")), out var error, out _),
                        Is.True, error);

            var stationId     = "cs-" + RandomExtensions.RandomString(6).ToLowerInvariant();
            using var station = await AStation(stationId);

            await SendTo(station, new JArray(2, "m1", "SignCertificate", new JObject(new JProperty("csr", ACSR(stationId)))));

            await ReceiveFrom(station);

            await Until(() => csms.CertificateSignings.FirstOrDefault()?.Outcome == "failed", "the CSMS noting that there is no such connection", csms, pki);

            Assert.That(csms.CertificateSignings.First().Detail, Does.Contain("no connection of this"));

        }

        #endregion


        #region PKI_ARegistrationAuthorityOverSSH_GetsNoCommandLine()

        /// <summary>
        /// A login of a registration authority over SSH gets the subsystem of
        /// the messages of a PKI, and nothing else: no command line of the PKI.
        /// </summary>
        [Test]
        public async Task PKI_ARegistrationAuthorityOverSSH_GetsNoCommandLine()
        {

            var pki       = this.pki!;
            var login     = User_Id.Parse("csms-" + RandomExtensions.RandomString(8));
            var password  = RandomExtensions.RandomString(32);

            Assert.That(pki.Connections.TryAddConnection(I18NString.Create("The CSMS, a registration authority"),
                                                         URL.Parse($"ssh://{login}@127.0.0.1:{pki.SSHPort!.Value.ToUInt16()}"),
                                                         "RegistrationAuthority",
                                                         true,
                                                         null,
                                                         null,
                                                         out _,
                                                         out var error,
                                                         Inbound: true),
                        Is.True, error);

            Assert.That(pki.WebSocketLogins.TrySetPasswordAuthentication(login, password, null, I18NString.Create("A CSMS"), out error), Is.True, error);

            await using var ssh = await SSHClient.Connect("127.0.0.1",
                                                          pki.SSHPort!.Value,
                                                          new SSHClientOptions {
                                                              Username       = login,
                                                              Password       = password,
                                                              VerifyHostKey  = hostKey => true
                                                          });

            // The subsystem it is written down for opens...
            var channel = await ssh.OpenSubsystem(PKIWireNames.SshSubsystem);
            await channel.CloseAsync();

            // ...and the command line does not.
            Assert.That(async () => await ssh.OpenShell(), Throws.Exception, "A registration authority got the command line of the PKI.");

        }

        #endregion

    }

}
