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

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Hermod.SSH;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.WebSocket;

using cloud.charging.open.protocols.OCPI.Transport;
using cloud.charging.open.protocols.WWCP.Node.TestKit;
using cloud.charging.open.protocols.WWCP.Node.Connections;

#endregion

namespace cloud.charging.open.TestEnvironment.Tests.Connections.HopByHop
{

    /// <summary>
    /// An EMSP and a CSMS, the e-mobility service provider and the charge point
    /// operator of one roaming agreement, the EMSP connecting to the CSMS - the
    /// other way round than <see cref="CSMS_2_EMSP"/> - over a WebSocket of OCPI
    /// at the station port of the CSMS, or over the subsystem
    /// "ocpi@open.charging.cloud" of its SSH server. Both ends see the
    /// connection as up; nothing goes over it yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The CSMS is the server here, as it is in <see cref="CS_2_CSMS"/>: the
    /// station port hands a WebSocket that asks for a subprotocol of OCPI to its
    /// servers of OCPI, and lets it in as the EMSP written down there - and as
    /// nothing else.
    /// </para>
    /// <para>
    /// OCPI over HTTP is tested in one place, <see cref="CSMS_2_EMSP"/>.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class EMSP_2_CSMS : AHopByHopTest
    {

        #region Data

        private DNSClient?  dnsClient;
        private EMSP.EMSP?  emsp;
        private CSMS.CSMS?  csms;

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

            if (emsp is not null)
            {
                await emsp.Stop();
                await emsp.DisposeAsync();
            }

            if (csms is not null)
            {
                await csms.Stop();
                await csms.DisposeAsync();
            }

            emsp = null;
            csms = null;

        }

        #endregion


        #region (private) StartBoth(Version)

        /// <summary>
        /// The EMSP and the CSMS, each offering the given OCPI version and no
        /// other, started: the CSMS with its station port switched on - over TLS
        /// where the test connects over wss://, letting in a client certificate
        /// alone over mutual TLS -, and with an SSH server where it connects
        /// over ssh://.
        /// </summary>
        /// <param name="Version">"2.1.1", "2.2.1" or "2.3.0".</param>
        private async Task StartBoth(String Version)
        {

            var emspDirectory  = NewNodeDirectory("emsp",
                                     new JProperty("ocpi", new JObject(
                                         new JProperty("countryCode",     "DE"),
                                         new JProperty("partyId",         "GDF"),
                                         new JProperty("name",            "GraphDefined EMSP"),
                                         new JProperty("versions",        new JArray(Version))
                                     ))
                                 );

            var csmsDirectory  = NewServedNodeDirectory("csms",
                                     new JProperty("ocpi", new JObject(
                                         new JProperty("countryCode",     "DE"),
                                         new JProperty("partyId",         "GEF"),
                                         new JProperty("name",            "GraphDefined CPO"),
                                         new JProperty("versions",        new JArray(Version))
                                     ))
                                 );

            csms  = new CSMS.CSMS(
                        DNSClient:        dnsClient,
                        HTTPPort:         TestPorts.Free(),
                        AccountsPath:     csmsDirectory.Accounts,
                        ConfigFile:       csmsDirectory.ConfigFile,
                        LogToConsole:     false,
                        BridgeDebugLog:   false
                    );

            emsp  = new EMSP.EMSP(
                        DNSClient:        dnsClient,
                        HTTPPort:         TestPorts.Free(),
                        AccountsPath:     emspDirectory.Accounts,
                        ConfigFile:       emspDirectory.ConfigFile,
                        LogToConsole:     false,
                        BridgeDebugLog:   false
                    );

            await csms.Start();
            await emsp.Start();

        }

        #endregion

        #region (private) SubprotocolOf(Version, DataFormat)

        /// <summary>
        /// The subprotocol a connection of the given version of OCPI and data
        /// format agrees on: "ocpi2.2.1", "ocpi2.2.1cbor".
        /// </summary>
        private static String SubprotocolOf(String  Version,
                                            String  DataFormat)

            => DataFormat == formatCBOR
                   ? OCPIWireNames.CBOROf       (Version)
                   : OCPIWireNames.SubprotocolOf(Version);

        #endregion

        #region (private) TheEMSPConnectsOverAWebSocket(Version, Authentication, DataFormat, What)

        /// <summary>
        /// The CSMS writes the EMSP down as one that connects to it, and the EMSP
        /// connects to the station port of the CSMS over OCPI - with a password, a
        /// one-time password, or over mutual TLS with its certificate - and both
        /// say it is connected.
        /// </summary>
        /// <param name="Version">The version of OCPI.</param>
        /// <param name="Authentication">How the EMSP signs in: "password", "totp" - or "mTLS".</param>
        /// <param name="DataFormat">The data format: "json" or "cbor".</param>
        /// <param name="What">What the connection is, in words: "webSocket TLS".</param>
        /// <returns>The login it comes under, its connection at the EMSP, and the connection the CSMS wrote down for it.</returns>
        private async Task<(User_Id Login, String Connection, String Inbound)> TheEMSPConnectsOverAWebSocket(String  Version,
                                                                                                             String  Authentication,
                                                                                                             String  DataFormat,
                                                                                                             String  What)
        {

            var emsp         = this.emsp!;
            var csms         = this.csms!;

            var login        = User_Id.Parse("emsp-" + RandomExtensions.RandomString(8));
            var description  = I18NString.Create($"{emsp.PartyIdText} --({What}/{Authentication}/{DataFormat}/OCPI {Version})--> {csms.OCPP.NodeId}");
            var door         = $"{(OverTLS ? "wss" : "ws")}://127.0.0.1:{csms.OCPPServerSettings.TCPPort}/{login}";
            var error        = "";

            // The certificate of the test's CA that names the login, over mutual
            // TLS: the CSMS knows the EMSP by nothing else.
            var certificate  = Authentication == authMTLS
                                   ? ClientCertificate(emsp, csms, login.ToString())
                                   : null;

            BelievesTheTestCA(emsp);

            // The CSMS: the EMSP, written down as one that connects to it.
            Assert.That(
                csms.Connections.TryAddConnection(
                     I18NString.Create("The EMSP, connecting"),
                     URL.Parse(door),
                     "EMSP",
                     true,
                     null,
                     null,
                     out var inbound,
                     out error,
                     Inbound:      true,
                     OCPIVersion:  Version
                ),
                Is.True, error
            );

            // The EMSP: the connection to the CSMS, a CPO, and what it signs in with.
            Assert.That(
                emsp.Connections.TryAddConnection(
                     description,
                     URL.Parse(door),
                     "CPO",
                     true,
                     certificate is null ? Credentials(emsp, csms, login, Authentication, description) : null,
                     certificate,
                     out var connection,
                     out error,
                     Encoding:     DataFormat,
                     OCPIVersion:  Version
                ),
                Is.True, error
            );

            await Until(() => StatusOf(emsp, connection!) == ConnectionStatus.Connected,  $"'{login}' connecting via {What}",    emsp, csms);
            await Until(() => StatusOf(csms, inbound!)    == ConnectionStatus.Connected,  $"'{login}' being let in by the CSMS", emsp, csms);

            return (login, connection!, inbound!);

        }

        #endregion

        #region (private) TheEMSPConnectsViaSSH(Version, Authentication, DataFormat)

        /// <summary>
        /// The CSMS writes the EMSP down as one that connects to it over SSH, and
        /// the EMSP connects to the subsystem of OCPI of the SSH server of the
        /// CSMS - with a password, a one-time password, or its own key - pinning
        /// the host key the server shows; both say it is connected.
        /// </summary>
        /// <param name="Version">The version of OCPI.</param>
        /// <param name="Authentication">How the EMSP signs in: "password", "totp", "publicKey".</param>
        /// <param name="DataFormat">The data format: "json" or "cbor".</param>
        /// <returns>The login it comes under, its connection at the EMSP, and the connection the CSMS wrote down for it.</returns>
        private async Task<(User_Id Login, String Connection, String Inbound)> TheEMSPConnectsViaSSH(String  Version,
                                                                                                     String  Authentication,
                                                                                                     String  DataFormat)
        {

            var emsp         = this.emsp!;
            var csms         = this.csms!;

            var login        = User_Id.Parse("emsp-" + RandomExtensions.RandomString(8));
            var description  = I18NString.Create($"{emsp.PartyIdText} --(ssh/{Authentication}/{DataFormat}/OCPI {Version})--> {csms.OCPP.NodeId}");
            var door         = $"ssh://{login}@127.0.0.1:{csms.SSHPort}";
            var error        = "";

            Assert.That(csms.OCPIOverSSHServed, Is.True, "OCPP-over-SSH is switched on in the file, and OCPI over SSH is not served beside it.");

            // The CSMS: the EMSP, written down as one that connects to it over
            // SSH, under the login it signs in with.
            Assert.That(
                csms.Connections.TryAddConnection(
                     I18NString.Create("The EMSP, connecting via SSH"),
                     URL.Parse(door),
                     "EMSP",
                     true,
                     null,
                     null,
                     out var inbound,
                     out error,
                     Inbound:      true,
                     OCPIVersion:  Version
                ),
                Is.True, error
            );

            // What the EMSP signs in with: its password, the secret of its
            // one-time passwords, or its own key.
            String? credentials = null;

            if (Authentication == authPublicKey)
            {

                var key = await emsp.SshKey.MakeIfNeeded();

                Assert.That(
                    csms.TryAddStationSSHKey(
                        Login:         login,
                        Line:          SshPublicKey.FromHostKey(key).ToAuthorizedKeyLine(),
                        Label:         "test",
                        GroupId:       null,
                        Description:   description,
                        CreatedBy:     "tests",
                        Key:           out _,
                        Error:         out error,
                        NotSaved:      out _
                    ),
                    Is.True, error
                );

            }

            else
                credentials = Credentials(emsp, csms, login, Authentication, description);

            Assert.That(
                emsp.Connections.TryAddConnection(
                    Description:        description,
                    URL:                URL.Parse(door),
                    ConnectionType:     "CPO",
                    AutoConnect:        true,
                    AuthenticationId:   credentials,
                    CertificateId:      null,
                    Id:                 out var connection,
                    Error:              out error,
                    SshHostKey:         csms.SSHHostKey!.Split(' ', 2)[1],
                    Encoding:           DataFormat,
                    OCPIVersion:        Version
                ),
                Is.True, error
            );

            await Until(() => StatusOf(emsp, connection!) == ConnectionStatus.Connected,  $"'{login}' connecting via SSH",       emsp, csms);
            await Until(() => StatusOf(csms, inbound!)    == ConnectionStatus.Connected,  $"'{login}' being let in by the CSMS", emsp, csms);

            return (login, connection!, inbound!);

        }

        #endregion

        #region (private) AssertConnected(Login, Connection, Inbound, Subprotocol)

        /// <summary>
        /// Both ends see the connection as up, over OCPI: the EMSP connected by
        /// its networking node of OCPI and agreed on the given subprotocol, and
        /// the CSMS let the login in at a server of OCPI - not as a charging
        /// station - and says so with that subprotocol.
        /// </summary>
        private void AssertConnected(User_Id  Login,
                                     String   Connection,
                                     String   Inbound,
                                     String   Subprotocol)
        {

            var emsp   = this.emsp!;
            var csms   = this.csms!;

            var entry  = emsp.Connections.Connections.First(connection => connection.Id == Connection);

            Assert.Multiple(() => {

                Assert.That(StatusOf(emsp, Connection),          Is.EqualTo(ConnectionStatus.Connected),  $"The EMSP does not see the connection as up.{Story(emsp, csms)}");
                Assert.That(StatusOf(csms, Inbound),             Is.EqualTo(ConnectionStatus.Connected),  $"The CSMS does not see the connection as up.{Story(emsp, csms)}");

                Assert.That(IsConnectedAt(csms, Login),          Is.True,                                 $"The CSMS did not let '{Login}' in.{Story(emsp, csms)}");

                Assert.That(csms.OCPIServers.   SelectMany(server => server.ConnectedNetworkingNodeIds).
                                 Concat(csms.OCPISshServers.SelectMany(server => server.ConnectedNetworkingNodeIds)).
                                 Select(networkingNodeId => networkingNodeId.ToString()),
                            Does.Contain(Login.ToString()),
                            "The CSMS did not let the EMSP in at a server of OCPI.");

                Assert.That((csms.StationServer?.ConnectedNetworkingNodeIds ?? []).
                                 Concat(csms.StationSSHServer?.ConnectedNetworkingNodeIds ?? []).
                                 Select(networkingNodeId => networkingNodeId.ToString()),
                            Does.Not.Contain(Login.ToString()),
                            "The CSMS let the EMSP in as a charging station.");

                Assert.That(entry.Use,                           Is.EqualTo(ConnectionUse.OCPI));
                Assert.That(emsp.SubprotocolInUseOf(entry),      Is.EqualTo(Subprotocol),                 "The EMSP did not agree on the subprotocol of OCPI.");
                Assert.That(csms.OutboundConnections[Inbound],   Does.Contain($"({Subprotocol})"),        "The CSMS does not say it let the EMSP in over the subprotocol of OCPI.");

                // Made by the networking node of OCPI of the EMSP.
                Assert.That(emsp.OCPINetworkingNode.WWCPClients.Count(),  Is.EqualTo(1),  "The networking node of OCPI of the EMSP does not keep one connection, to the CSMS.");

            });

        }

        #endregion


        #region WebSocket     (Authentication, DataFormat, Version)

        /// <summary>
        /// The EMSP connects to the CSMS over a WebSocket of OCPI, with a password
        /// or a one-time password.
        /// </summary>
        /// <param name="Authentication">The authentication method to use.</param>
        /// <param name="DataFormat">The data format to use.</param>
        /// <param name="Version">The version of OCPI.</param>
        [TestCase(authPassword, formatJSON, "2.2.1")]
        [TestCase(authPassword, formatCBOR, "2.2.1")]
        [TestCase(authTOTP,     formatJSON, "2.2.1")]
        [TestCase(authTOTP,     formatCBOR, "2.2.1")]
        [TestCase(authPassword, formatJSON, "2.1.1")]
        [TestCase(authPassword, formatJSON, "2.3.0")]
        public async Task WebSocket(String  Authentication,
                                    String  DataFormat,
                                    String  Version)
        {

            await StartBoth(Version);

            var (login, connection, inbound) = await TheEMSPConnectsOverAWebSocket(Version, Authentication, DataFormat, "webSocket");

            AssertConnected(login, connection, inbound, SubprotocolOf(Version, DataFormat));

        }

        #endregion

        #region WebSocketTLS  (Authentication, DataFormat, Version)

        /// <summary>
        /// The EMSP connects to the CSMS over a WebSocket of OCPI over TLS, with a
        /// password or a one-time password, believing the CSMS by the test's CA.
        /// </summary>
        /// <param name="Authentication">The authentication method to use.</param>
        /// <param name="DataFormat">The data format to use.</param>
        /// <param name="Version">The version of OCPI.</param>
        [TestCase(authPassword, formatJSON, "2.2.1")]
        [TestCase(authPassword, formatCBOR, "2.2.1")]
        [TestCase(authTOTP,     formatJSON, "2.2.1")]
        [TestCase(authTOTP,     formatCBOR, "2.2.1")]
        [TestCase(authPassword, formatJSON, "2.3.0")]
        public async Task WebSocketTLS(String  Authentication,
                                       String  DataFormat,
                                       String  Version)
        {

            await StartBoth(Version);

            Assert.That(csms!.OCPPServerTLS, Is.True, "The station port of the CSMS does not speak TLS.");

            var (login, connection, inbound) = await TheEMSPConnectsOverAWebSocket(Version, Authentication, DataFormat, "webSocket TLS");

            AssertConnected(login, connection, inbound, SubprotocolOf(Version, DataFormat));

        }

        #endregion

        #region WebSocketMTLS (                DataFormat, Version)

        /// <summary>
        /// The EMSP connects to the CSMS over a WebSocket of OCPI over mutual TLS:
        /// it signs in with a certificate of the test's CA that names its login.
        /// </summary>
        /// <param name="DataFormat">The data format to use.</param>
        /// <param name="Version">The version of OCPI.</param>
        [TestCase(formatJSON, "2.2.1")]
        [TestCase(formatCBOR, "2.2.1")]
        [TestCase(formatJSON, "2.1.1")]
        public async Task WebSocketMTLS(String  DataFormat,
                                        String  Version)
        {

            await StartBoth(Version);

            Assert.That(csms!.OCPPServerTLS, Is.True, "The station port of the CSMS does not speak TLS.");

            var (login, connection, inbound) = await TheEMSPConnectsOverAWebSocket(Version, authMTLS, DataFormat, "webSocket mTLS");

            AssertConnected(login, connection, inbound, SubprotocolOf(Version, DataFormat));

        }

        #endregion

        #region SSH           (Authentication, DataFormat, Version)

        /// <summary>
        /// The EMSP connects to the subsystem of OCPI of the CSMS's SSH server, and
        /// pins the key the server shows: with its password, with a one-time
        /// password, or with its own key. The CSMS lets it in as an EMSP that
        /// connects to it, under the login it signs in to SSH with.
        /// </summary>
        /// <param name="Authentication">The authentication method to use.</param>
        /// <param name="DataFormat">The data format to use.</param>
        /// <param name="Version">The version of OCPI.</param>
        [TestCase(authPassword,  formatJSON, "2.2.1")]
        [TestCase(authPassword,  formatCBOR, "2.2.1")]
        [TestCase(authTOTP,      formatJSON, "2.2.1")]
        [TestCase(authTOTP,      formatCBOR, "2.2.1")]
        [TestCase(authPublicKey, formatJSON, "2.2.1")]
        [TestCase(authPublicKey, formatCBOR, "2.2.1")]
        [TestCase(authPassword,  formatJSON, "2.3.0")]
        public async Task SSH(String  Authentication,
                              String  DataFormat,
                              String  Version)
        {

            await StartBoth(Version);

            var (login, connection, inbound) = await TheEMSPConnectsViaSSH(Version, Authentication, DataFormat);

            AssertConnected(login, connection, inbound, SubprotocolOf(Version, DataFormat));

            var sshClients = emsp!.OCPINetworkingNode.WWCPSshClients.ToArray();

            Assert.That(sshClients,                                    Has.Length.EqualTo(1),                  "The EMSP does not keep one SSH connection, to the CSMS.");
            Assert.That(sshClients[0].MessageClient.Options.Subsystem, Is.EqualTo(OCPIWireNames.SshSubsystem), "The EMSP did not ask for the subsystem of OCPI.");
            Assert.That(sshClients[0].MessageClient.Options.Subsystem, Is.EqualTo("ocpi@open.charging.cloud"));

        }

        #endregion


        #region (private) ConnectsAt(Login, Password, Subprotocol)

        /// <summary>
        /// Whether a WebSocket under the given login, with its password, offering
        /// the given subprotocol, gets through the station port of the CSMS.
        /// </summary>
        private async Task<Boolean> ConnectsAt(String  Login,
                                               String  Password,
                                               String  Subprotocol)
        {

            var client = new WebSocketClient(
                             URL.Parse($"ws://127.0.0.1:{csms!.OCPPServerSettings.TCPPort}/{Login}"),
                             HTTPAuthentication:     HTTPBasicAuthentication.Create(Login, Password),
                             SecWebSocketProtocols:  [ Subprotocol ]
                         );

            try
            {

                var (_, response) = await client.Connect(MaxNumberOfRetries: 0);

                return response.HTTPStatusCode == HTTPStatusCode.SwitchingProtocols;

            }
            finally
            {
                await client.Close();
            }

        }

        #endregion

        #region AnEMSP_DoesNotSignIn_AsAChargingStation()

        /// <summary>
        /// The login of an EMSP that connects to the CSMS over OCPI does not open
        /// the station port over OCPP: the name of a connection of OCPI is that
        /// connection's, and no charging station's.
        /// </summary>
        [Test]
        public async Task AnEMSP_DoesNotSignIn_AsAChargingStation()
        {

            await StartBoth("2.2.1");

            var csms      = this.csms!;
            var login     = "emsp-" + RandomExtensions.RandomString(8);
            var password  = RandomExtensions.RandomString(32);

            Assert.That(csms.WebSocketLogins.TrySetPasswordAuthentication(User_Id.Parse(login), password, null, I18NString.Create("tests"), out var error),
                        Is.True, error);

            Assert.That(csms.Connections.TryAddConnection(I18NString.Create("The EMSP, connecting"),
                                                          URL.Parse($"ws://127.0.0.1:{csms.OCPPServerSettings.TCPPort}/{login}"),
                                                          "EMSP", true, null, null,
                                                          out _, out error,
                                                          Inbound:      true,
                                                          OCPIVersion:  "2.2.1"),
                        Is.True, error);

            Assert.That(await ConnectsAt(login, password, "ocpp2.1"),    Is.False, $"The EMSP signed in as a charging station.{Story(csms)}");
            Assert.That(await ConnectsAt(login, password, "ocpi2.2.1"),  Is.True,  $"The EMSP could not sign in over OCPI.{Story(csms)}");

        }

        #endregion

        #region AChargingStation_DoesNotSignIn_AsAnEMSP()

        /// <summary>
        /// The login of a charging station does not open the station port over
        /// OCPI: what is no EMSP written down as one that connects is turned away
        /// there - and gets in over OCPP as before.
        /// </summary>
        [Test]
        public async Task AChargingStation_DoesNotSignIn_AsAnEMSP()
        {

            await StartBoth("2.2.1");

            var csms      = this.csms!;
            var login     = "cs-" + RandomExtensions.RandomString(8);
            var password  = RandomExtensions.RandomString(32);

            Assert.That(csms.WebSocketLogins.TrySetPasswordAuthentication(User_Id.Parse(login), password, null, I18NString.Create("tests"), out var error),
                        Is.True, error);

            Assert.That(await ConnectsAt(login, password, "ocpi2.2.1"),  Is.False, $"The charging station signed in as an EMSP.{Story(csms)}");
            Assert.That(await ConnectsAt(login, password, "ocpp2.1"),    Is.True,  $"The charging station could not sign in over OCPP.{Story(csms)}");

        }

        #endregion

    }

}
