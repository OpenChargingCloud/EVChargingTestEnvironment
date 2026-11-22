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

using cloud.charging.open.protocols.OCPPv2_1;
using cloud.charging.open.protocols.OCPI.Transport;
using cloud.charging.open.protocols.WWCP.Node.TestKit;
using cloud.charging.open.protocols.WWCP.Node.Connections;

#endregion

namespace cloud.charging.open.TestEnvironment.Tests.Connections.HopByHop
{

    /// <summary>
    /// A CSMS and an EMSP, the charge point operator and the e-mobility
    /// service provider of one roaming agreement, talking OCPI to each other
    /// over HTTP: the peering, started from either end, the cards of the
    /// EMSP the CSMS decides on, and the locations of the CSMS the EMSP
    /// learns - in every OCPI version both of them speak.
    /// </summary>
    /// <remarks>
    /// And the connections over a WebSocket and over SSH between the two - what
    /// OCPI-over-WebSocket and OCPI-over-SSH are to travel over: the CSMS
    /// connects to the WebSocket port of OCPI of the EMSP, or to the subsystem
    /// "ocpi@open.charging.cloud" of its SSH server, and both ends see the
    /// connection as up. Nothing goes over it yet.
    /// </remarks>
    [TestFixture]
    public class CSMS_2_EMSP : AHopByHopTest
    {

        #region Data

        private DNSClient?  dnsClient;
        private CSMS.CSMS?  csms;
        private EMSP.EMSP?  emsp;

        private const String  KnownCard    = "04A2B3C4D5E6F7";
        private const String  InvalidCard  = "04BBBBBBBBBBBB";
        private const String  NobodysCard  = "04CCCCCCCCCCCC";

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

            if (csms is not null)
            {
                await csms.Stop();
                await csms.DisposeAsync();
            }

            if (emsp is not null)
            {
                await emsp.Stop();
                await emsp.DisposeAsync();
            }

            csms = null;
            emsp = null;

        }

        #endregion


        #region (private) StartBoth(Version, params EMSPSections)

        /// <summary>
        /// The CSMS and the EMSP, each offering the given OCPI version and no
        /// other, started - the EMSP with what else its file is to say, e.g.
        /// <see cref="OCPIServer"/>. Where the test connects over wss://, the
        /// WebSocket port of OCPI of the EMSP has a certificate of the test's CA.
        /// </summary>
        /// <param name="Version">"2.1.1", "2.2.1" or "2.3.0".</param>
        /// <param name="EMSPSections">What else the file of the EMSP says.</param>
        private async Task StartBoth(String              Version,
                                     params JProperty[]  EMSPSections)
        {

            var csmsDirectory  = NewNodeDirectory("csms",
                                     new JProperty("ocpi", new JObject(
                                         new JProperty("countryCode",     "DE"),
                                         new JProperty("partyId",         "GEF"),
                                         new JProperty("name",            "GraphDefined CPO"),
                                         new JProperty("versions",        new JArray(Version)),

                                         // Its partner is on this machine, which a
                                         // CSMS does not call unless told so.
                                         new JProperty("remoteNetworks",  new JArray("127.0.0.0/8 => ALLOW", "::1 => ALLOW"))
                                     ))
                                 );

            var emspDirectory  = NewNodeDirectory("emsp",
                                     [
                                         new JProperty("ocpi", new JObject(
                                             new JProperty("countryCode",     "DE"),
                                             new JProperty("partyId",         "GDF"),
                                             new JProperty("name",            "GraphDefined EMSP"),
                                             new JProperty("versions",        new JArray(Version))
                                         )),
                                         .. EMSPSections
                                     ]
                                 );

            if (OverTLS)
                ACertificateOfTheTestCA(Path.GetDirectoryName(emspDirectory.ConfigFile.Path)!, EMSP.EMSP.WebSocketListener);

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

        #region (private) Peered(Version, WhoStarts)

        /// <summary>
        /// The two peered on the given version: the one that does not start
        /// hands out a token, the one that starts is told it and where the
        /// other's versions are, and goes - GET versions, GET the version
        /// details, POST its credentials.
        /// </summary>
        /// <param name="Version">The OCPI version.</param>
        /// <param name="WhoStarts">"csms" or "emsp".</param>
        private async Task Peered(String  Version,
                                  String  WhoStarts)
        {

            var csms = this.csms!;
            var emsp = this.emsp!;

            if (WhoStarts == "csms")
            {

                var emspToken  = await TheEMSPAddsTheCPO(Version);

                await TheCPOAddsTheEMSP(Version, emspToken, emsp.OCPIVersionsURL.ToString());

                var result     = await csms.RegisterRemotePartyAsync(Version, CPOsViewOfTheEMSP(Version).Id.ToString());

                Assert.That(result.Success, Is.True, $"The CSMS could not register with the EMSP: {result.Message}{Story(csms, emsp)}");

            }

            else
            {

                var cpoToken   = await TheCPOAddsTheEMSP(Version);

                await TheEMSPAddsTheCPO(Version, cpoToken, csms.OCPIVersionsURL.ToString());

                var result     = await emsp.RegisterRemotePartyAsync(Version, EMSPsViewOfTheCPO(Version).Id.ToString());

                Assert.That(result.Success, Is.True, $"The EMSP could not register with the CSMS: {result.Message}{Story(csms, emsp)}");

            }

        }

        /// <summary>
        /// The EMSP learns of the CSMS: with a token and nothing else, it is
        /// the CSMS that has to come; with the CSMS's token and versions URL as
        /// well, the EMSP can go there itself.
        /// </summary>
        private async Task<String> TheEMSPAddsTheCPO(String   Version,
                                                     String?  CPOToken     = null,
                                                     String?  VersionsURL  = null)
        {

            var body = new JObject(
                           new JProperty("version",      Version),
                           new JProperty("countryCode",  csms!.PartyId.CountryCode.ToString()),
                           new JProperty("partyId",      csms.PartyId.PartyId.    ToString()),
                           new JProperty("role",         "CPO"),
                           new JProperty("name",         csms.BusinessDetails.Name)
                       );

            if (CPOToken    is not null)  body.Add("theirToken",  CPOToken);
            if (VersionsURL is not null)  body.Add("versionsURL", VersionsURL);

            var result = await emsp!.AddRemotePartyAsync(body);

            Assert.That(result.Success, Is.True, $"The EMSP would not take the CSMS as a partner: {result.Message}");

            return result.Data!.Value<String>("ourToken")!;

        }

        /// <summary>
        /// The CSMS learns of the EMSP, the same two ways round.
        /// </summary>
        private async Task<String> TheCPOAddsTheEMSP(String   Version,
                                                     String?  EMSPToken    = null,
                                                     String?  VersionsURL  = null)
        {

            var body = new JObject(
                           new JProperty("version",      Version),
                           new JProperty("countryCode",  emsp!.PartyId.CountryCode.ToString()),
                           new JProperty("partyId",      emsp.PartyId.PartyId.    ToString()),
                           new JProperty("role",         "EMSP"),
                           new JProperty("name",         emsp.BusinessDetails.Name)
                       );

            if (EMSPToken   is not null)  body.Add("theirToken",  EMSPToken);
            if (VersionsURL is not null)  body.Add("versionsURL", VersionsURL);

            var result = await csms!.AddRemotePartyAsync(body);

            Assert.That(result.Success, Is.True, $"The CSMS would not take the EMSP as a partner: {result.Message}");

            return result.Data!.Value<String>("ourToken")!;

        }

        #endregion

        #region (private) CPOsViewOfTheEMSP(Version) / EMSPsViewOfTheCPO(Version)

        private CSMS.OCPI.RemotePartySummary CPOsViewOfTheEMSP(String Version)
        {

            var partner = csms!.OCPIVersions.
                                Where     (version => version.Label == Version).
                                SelectMany(version => version.RemoteParties).
                                FirstOrDefault(party => party.PartyId == emsp!.PartyId.PartyId);

            Assert.That(partner, Is.Not.Null, $"The CSMS does not know the EMSP on OCPI {Version}.");

            return partner!;

        }

        private EMSP.OCPI.RemotePartySummary EMSPsViewOfTheCPO(String Version)
        {

            var partner = emsp!.OCPIVersions.
                                Where     (version => version.Label == Version).
                                SelectMany(version => version.RemoteParties).
                                FirstOrDefault(party => party.PartyId == csms!.PartyId.PartyId);

            Assert.That(partner, Is.Not.Null, $"The EMSP does not know the CSMS on OCPI {Version}.");

            return partner!;

        }

        #endregion


        #region OCPI_Peering(Version, WhoStarts)

        /// <summary>
        /// The CSMS and the EMSP peer over OCPI, started from either end. Either
        /// way it ends in the same place: each holds a token of the other's
        /// and the URL to present it at, both call the peering registered, and
        /// the two tokens match across the wire.
        /// </summary>
        /// <param name="Version">The OCPI version.</param>
        /// <param name="WhoStarts">"csms" or "emsp".</param>
        [TestCase("2.1.1", "csms")]
        [TestCase("2.1.1", "emsp")]
        [TestCase("2.2.1", "csms")]
        [TestCase("2.2.1", "emsp")]
        [TestCase("2.3.0", "csms")]
        [TestCase("2.3.0", "emsp")]
        public async Task OCPI_Peering(String  Version,
                                       String  WhoStarts)
        {

            await StartBoth(Version);
            await Peered   (Version, WhoStarts);

            var atTheCPO   = CPOsViewOfTheEMSP(Version);
            var atTheEMSP  = EMSPsViewOfTheCPO(Version);

            Assert.Multiple(() => {

                Assert.That(atTheCPO. Registered,  Is.True, $"The CSMS does not call the peering complete.{Story(csms, emsp)}");
                Assert.That(atTheEMSP.Registered,  Is.True, $"The EMSP does not call the peering complete.{Story(csms, emsp)}");

                // What one side hands out is what the other presents, both ways.
                Assert.That(atTheCPO. OurToken?.  ToString(),  Is.EqualTo(atTheEMSP.TheirToken?.ToString()),
                            "The token the CSMS expects from the EMSP is not the one the EMSP presents.");

                Assert.That(atTheEMSP.OurToken?.  ToString(),  Is.EqualTo(atTheCPO. TheirToken?.ToString()),
                            "The token the EMSP expects from the CSMS is not the one the CSMS presents.");

                // Each knows where to reach the other, and on which version.
                Assert.That(atTheCPO. TheirVersionsURL?.ToString(),  Is.EqualTo(emsp!.OCPIVersionsURL.ToString()));
                Assert.That(atTheEMSP.TheirVersionsURL?.ToString(),  Is.EqualTo(csms!.OCPIVersionsURL.ToString()));

                Assert.That(atTheCPO. SelectedVersion?.ToString(),   Is.EqualTo(Version));
                Assert.That(atTheEMSP.SelectedVersion?.ToString(),   Is.EqualTo(Version));

            });

        }

        #endregion

        #region OCPI_TheCardsOfTheEMSP_AreDecidedOnByTheCSMS(Version)

        /// <summary>
        /// The EMSP issues its cards, and the CSMS decides on them: one issued
        /// before the peering, which the EMSP sends with all the others after
        /// it, one issued after it as no longer valid, which it sends at once,
        /// and one nobody issued.
        /// </summary>
        /// <param name="Version">The OCPI version.</param>
        [TestCase("2.1.1")]
        [TestCase("2.2.1")]
        [TestCase("2.3.0")]
        public async Task OCPI_TheCardsOfTheEMSP_AreDecidedOnByTheCSMS(String Version)
        {

            await StartBoth(Version);

            var known    = await emsp!.AddTokenAsync(new JObject(
                                                         new JProperty("version",  Version),
                                                         new JProperty("uid",      KnownCard)
                                                     ));

            Assert.That(known.Success, Is.True, known.Message);

            await Peered(Version, "emsp");

            var invalid  = await emsp.AddTokenAsync(new JObject(
                                                        new JProperty("version",  Version),
                                                        new JProperty("uid",      InvalidCard),
                                                        new JProperty("valid",    false)
                                                    ));

            Assert.That(invalid.Success, Is.True, invalid.Message);

            await Until(() => csms!.AuthorizationOf(KnownCard).  Status == AuthorizationStatus.Accepted,  $"'{KnownCard}' being accepted by the CSMS",           csms, emsp);
            await Until(() => csms!.AuthorizationOf(InvalidCard).Status == AuthorizationStatus.Invalid,   $"'{InvalidCard}' being known as invalid by the CSMS", csms, emsp);

            Assert.That(csms!.AuthorizationOf(NobodysCard).Status, Is.EqualTo(AuthorizationStatus.Unknown), "A card nobody issued is not unknown to the CSMS.");

        }

        #endregion

        #region (private) OCPIServer(ClientAuthentication)

        /// <summary>
        /// The "webSocketServer" section of the EMSP: its WebSocket port of OCPI
        /// switched on, on 127.0.0.1 and a port of its own, letting in what
        /// signs in in the given way - and OCPI over SSH as well, where the
        /// test connects over ssh://.
        /// </summary>
        /// <param name="ClientAuthentication">How a client may sign in: "httpAuthWithoutTLS", "httpAuthOverTLS", "tlsClientCertificate".</param>
        private static JProperty OCPIServer(String ClientAuthentication)

            => new ("webSocketServer", new JObject(
                   new JProperty("enabled",               true),
                   new JProperty("address",               "127.0.0.1"),
                   new JProperty("port",                  TestPorts.Free().ToUInt16()),
                   new JProperty("clientAuthentication",  new JArray(ClientAuthentication)),
                   new JProperty("ssh",                   new JObject(
                       new JProperty("enabled",           OverSSH)
                   ))
               ));

        #endregion

        #region (private) StartBothServed(Version)

        /// <summary>
        /// The CSMS and the EMSP of the given version, the EMSP letting a CPO
        /// connect: over ws:// by HTTP authentication, over wss:// by HTTP
        /// authentication over TLS or - over mutual TLS - by its certificate,
        /// over ssh:// on the SSH server of its own.
        /// </summary>
        /// <param name="Version">"2.1.1", "2.2.1" or "2.3.0".</param>
        private Task StartBothServed(String Version)

            => StartBoth(
                   Version,
                   [
                       OCPIServer(OverMTLS
                                      ? "tlsClientCertificate"
                                      : OverTLS
                                            ? "httpAuthOverTLS"
                                            : "httpAuthWithoutTLS"),
                       .. (OverSSH ? new[] { SSHServer() } : Array.Empty<JProperty>())
                   ]
               );

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

        #region (private) TheCSMSConnectsOverAWebSocket(Version, Authentication, DataFormat, Scheme)

        /// <summary>
        /// The EMSP writes the CSMS down as a CPO that connects to it, and the
        /// CSMS connects to the WebSocket port of OCPI of the EMSP - with a
        /// password, a one-time password, or over mutual TLS with its
        /// certificate - and both say it is connected.
        /// </summary>
        /// <param name="Version">The version of OCPI.</param>
        /// <param name="Authentication">How the CSMS signs in: "password", "totp" - or "mTLS".</param>
        /// <param name="DataFormat">The data format: "json" or "cbor".</param>
        /// <param name="What">What the connection is, in words: "webSocket TLS".</param>
        /// <returns>The login it comes under, its connection at the CSMS, and the connection the EMSP wrote down for it.</returns>
        private async Task<(User_Id Login, String Connection, String Inbound)> TheCSMSConnectsOverAWebSocket(String  Version,
                                                                                                             String  Authentication,
                                                                                                             String  DataFormat,
                                                                                                             String  What)
        {

            var csms         = this.csms!;
            var emsp         = this.emsp!;

            var login        = User_Id.Parse("csms-" + RandomExtensions.RandomString(8));
            var description  = I18NString.Create($"{csms.OCPP.NodeId} --({What}/{Authentication}/{DataFormat}/OCPI {Version})--> {emsp.PartyIdText}");
            var door         = $"{(OverTLS ? "wss" : "ws")}://127.0.0.1:{emsp.OCPIServerSettings.TCPPort}/{login}";
            var error        = "";

            Assert.That(emsp.OCPIServerRunning, Is.True, "The WebSocket server of OCPI is switched on in the file, and not running.");

            // The certificate of the test's CA that names the login, over mutual
            // TLS: the EMSP knows the CSMS by nothing else.
            var certificate  = Authentication == authMTLS
                                   ? ClientCertificate(csms, emsp, login.ToString())
                                   : null;

            BelievesTheTestCA(csms);

            // The EMSP: the CSMS, written down as a CPO that connects to it.
            Assert.That(
                emsp.Connections.TryAddConnection(
                     I18NString.Create("The CSMS, connecting"),
                     URL.Parse(door),
                     "CPO",
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

            // The CSMS: the connection to the EMSP, and what it signs in with.
            Assert.That(
                csms.Connections.TryAddConnection(
                     description,
                     URL.Parse(door),
                     "EMSP",
                     true,
                     certificate is null ? Credentials(csms, emsp, login, Authentication, description) : null,
                     certificate,
                     out var connection,
                     out error,
                     Encoding:     DataFormat,
                     OCPIVersion:  Version
                ),
                Is.True, error
            );

            await Until(() => StatusOf(csms, connection!) == ConnectionStatus.Connected,  $"'{login}' connecting via {What}",    csms, emsp);
            await Until(() => StatusOf(emsp, inbound!)    == ConnectionStatus.Connected,  $"'{login}' being let in by the EMSP", csms, emsp);

            return (login, connection!, inbound!);

        }

        #endregion

        #region (private) TheCSMSConnectsViaSSH(Version, Authentication, DataFormat)

        /// <summary>
        /// The EMSP writes the CSMS down as a CPO that connects to it over SSH,
        /// and the CSMS connects to the subsystem of OCPI of the SSH server of the
        /// EMSP - with a password, a one-time password, or its own key - pinning
        /// the host key the server shows; both say it is connected.
        /// </summary>
        /// <param name="Version">The version of OCPI.</param>
        /// <param name="Authentication">How the CSMS signs in: "password", "totp", "publicKey".</param>
        /// <param name="DataFormat">The data format: "json" or "cbor".</param>
        /// <returns>The login it comes under, its connection at the CSMS, and the connection the EMSP wrote down for it.</returns>
        private async Task<(User_Id Login, String Connection, String Inbound)> TheCSMSConnectsViaSSH(String  Version,
                                                                                                     String  Authentication,
                                                                                                     String  DataFormat)
        {

            var csms         = this.csms!;
            var emsp         = this.emsp!;

            var login        = User_Id.Parse("csms-" + RandomExtensions.RandomString(8));
            var description  = I18NString.Create($"{csms.OCPP.NodeId} --(ssh/{Authentication}/{DataFormat}/OCPI {Version})--> {emsp.PartyIdText}");
            var door         = $"ssh://{login}@127.0.0.1:{emsp.SSHPort}";
            var error        = "";

            Assert.That(emsp.OCPIOverSSHServed, Is.True, "OCPI over SSH is switched on in the file, and not served.");

            // The EMSP: the CSMS, written down as a CPO that connects to it over
            // SSH, under the login it signs in with.
            Assert.That(
                emsp.Connections.TryAddConnection(
                     I18NString.Create("The CSMS, connecting via SSH"),
                     URL.Parse(door),
                     "CPO",
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

            // What the CSMS signs in with: its password, the secret of its
            // one-time passwords, or its own key.
            String? credentials = null;

            if (Authentication == authPublicKey)
            {

                var key = await csms.SshKey.MakeIfNeeded();

                Assert.That(
                    emsp.TryAddCPOSSHKey(
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
                credentials = Credentials(csms, emsp, login, Authentication, description);

            Assert.That(
                csms.Connections.TryAddConnection(
                    Description:        description,
                    URL:                URL.Parse(door),
                    ConnectionType:     "EMSP",
                    AutoConnect:        true,
                    AuthenticationId:   credentials,
                    CertificateId:      null,
                    Id:                 out var connection,
                    Error:              out error,
                    SshHostKey:         emsp.SSHHostKey!.Split(' ', 2)[1],
                    Encoding:           DataFormat,
                    OCPIVersion:        Version
                ),
                Is.True, error
            );

            await Until(() => StatusOf(csms, connection!) == ConnectionStatus.Connected,  $"'{login}' connecting via SSH",       csms, emsp);
            await Until(() => StatusOf(emsp, inbound!)    == ConnectionStatus.Connected,  $"'{login}' being let in by the EMSP", csms, emsp);

            return (login, connection!, inbound!);

        }

        #endregion

        #region (private) AssertConnected(Login, Connection, Inbound, Subprotocol)

        /// <summary>
        /// Both ends see the connection as up, over OCPI: the CSMS connected by
        /// its networking node of OCPI and agreed on the given subprotocol, and
        /// the EMSP let the login in, and says so with that subprotocol.
        /// </summary>
        private void AssertConnected(User_Id  Login,
                                     String   Connection,
                                     String   Inbound,
                                     String   Subprotocol)
        {

            var csms   = this.csms!;
            var emsp   = this.emsp!;

            var entry  = csms.Connections.Connections.First(connection => connection.Id == Connection);

            Assert.Multiple(() => {

                Assert.That(StatusOf(csms, Connection),          Is.EqualTo(ConnectionStatus.Connected),  $"The CSMS does not see the connection as up.{Story(csms, emsp)}");
                Assert.That(StatusOf(emsp, Inbound),             Is.EqualTo(ConnectionStatus.Connected),  $"The EMSP does not see the connection as up.{Story(csms, emsp)}");

                Assert.That(IsConnectedAt(emsp, Login),          Is.True,                                 $"The EMSP did not let '{Login}' in.{Story(csms, emsp)}");

                Assert.That(entry.Use,                           Is.EqualTo(ConnectionUse.OCPI));
                Assert.That(csms.SubprotocolInUseOf(entry),      Is.EqualTo(Subprotocol),                 "The CSMS did not agree on the subprotocol of OCPI.");
                Assert.That(emsp.OutboundConnections[Inbound],   Does.Contain($"({Subprotocol})"),        "The EMSP does not say it let the CSMS in over the subprotocol of OCPI.");

                // Made by the networking node of OCPI, and not by the one of OCPP.
                Assert.That(csms.OCPINetworkingNode.WWCPClients.Count(),     Is.EqualTo(1),  "The networking node of OCPI of the CSMS does not keep one connection, to the EMSP.");
                Assert.That(csms.OCPPConnecting.Node21.WWCPClients.Count(),  Is.EqualTo(0),  "The networking node of OCPP of the CSMS keeps a connection.");

            });

        }

        #endregion


        #region WebSocket     (Authentication, DataFormat, Version)

        /// <summary>
        /// The CSMS connects to the EMSP over a WebSocket of OCPI, with a password
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

            await StartBothServed(Version);

            var (login, connection, inbound) = await TheCSMSConnectsOverAWebSocket(Version, Authentication, DataFormat, "webSocket");

            AssertConnected(login, connection, inbound, SubprotocolOf(Version, DataFormat));

        }

        #endregion

        #region WebSocketTLS  (Authentication, DataFormat, Version)

        /// <summary>
        /// The CSMS connects to the EMSP over a WebSocket of OCPI over TLS, with a
        /// password or a one-time password, believing the EMSP by the test's CA.
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
        public async Task WebSocketTLS(String  Authentication,
                                       String  DataFormat,
                                       String  Version)
        {

            await StartBothServed(Version);

            Assert.That(emsp!.OCPIServerTLS, Is.True, "The WebSocket port of OCPI of the EMSP does not speak TLS.");

            var (login, connection, inbound) = await TheCSMSConnectsOverAWebSocket(Version, Authentication, DataFormat, "webSocket TLS");

            AssertConnected(login, connection, inbound, SubprotocolOf(Version, DataFormat));

        }

        #endregion

        #region WebSocketMTLS (                DataFormat, Version)

        /// <summary>
        /// The CSMS connects to the EMSP over a WebSocket of OCPI over mutual TLS:
        /// it signs in with a certificate of the test's CA that names its login.
        /// </summary>
        /// <param name="DataFormat">The data format to use.</param>
        /// <param name="Version">The version of OCPI.</param>
        [TestCase(formatJSON, "2.2.1")]
        [TestCase(formatCBOR, "2.2.1")]
        [TestCase(formatJSON, "2.3.0")]
        public async Task WebSocketMTLS(String  DataFormat,
                                        String  Version)
        {

            await StartBothServed(Version);

            Assert.That(emsp!.OCPIServerTLS, Is.True, "The WebSocket port of OCPI of the EMSP does not speak TLS.");

            var (login, connection, inbound) = await TheCSMSConnectsOverAWebSocket(Version, authMTLS, DataFormat, "webSocket mTLS");

            AssertConnected(login, connection, inbound, SubprotocolOf(Version, DataFormat));

        }

        #endregion

        #region SSH           (Authentication, DataFormat, Version)

        /// <summary>
        /// The CSMS connects to the subsystem of OCPI of the EMSP's SSH server, and
        /// pins the key the server shows: with its password, with a one-time
        /// password, or with its own key. The EMSP lets it in as a CPO that
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
        [TestCase(authPassword,  formatJSON, "2.1.1")]
        [TestCase(authPublicKey, formatJSON, "2.3.0")]
        public async Task SSH(String  Authentication,
                              String  DataFormat,
                              String  Version)
        {

            await StartBothServed(Version);

            var (login, connection, inbound) = await TheCSMSConnectsViaSSH(Version, Authentication, DataFormat);

            AssertConnected(login, connection, inbound, SubprotocolOf(Version, DataFormat));

            var sshClients = csms!.OCPINetworkingNode.WWCPSshClients.ToArray();

            Assert.That(sshClients,                                    Has.Length.EqualTo(1),                "The CSMS does not keep one SSH connection, to the EMSP.");
            Assert.That(sshClients[0].MessageClient.Options.Subsystem, Is.EqualTo(OCPIWireNames.SshSubsystem), "The CSMS did not ask for the subsystem of OCPI.");
            Assert.That(sshClients[0].MessageClient.Options.Subsystem, Is.EqualTo("ocpi@open.charging.cloud"));

        }

        #endregion


        #region OCPI_TheLocationsOfTheCSMS_ReachTheEMSP(Version)

        /// <summary>
        /// The CSMS publishes its locations, and the EMSP learns of them: one
        /// published before the peering, which the CSMS sends with all the
        /// others when it registers with the EMSP, and one published after it,
        /// which it sends at once.
        /// </summary>
        /// <param name="Version">The OCPI version.</param>
        [TestCase("2.1.1")]
        [TestCase("2.2.1")]
        [TestCase("2.3.0")]
        public async Task OCPI_TheLocationsOfTheCSMS_ReachTheEMSP(String Version)
        {

            await StartBoth(Version);

            await Published(Version, "LOC0001");
            await Peered   (Version, "csms");
            await Published(Version, "LOC0002");

            await Until(() => EMSPKnowsLocation(Version, "LOC0001"), "'LOC0001' reaching the EMSP", csms, emsp);
            await Until(() => EMSPKnowsLocation(Version, "LOC0002"), "'LOC0002' reaching the EMSP", csms, emsp);

        }

        #endregion

        #region OCPI_ALocationOfTheCSMS_ReachesTheEMSPThatRegistered(Version)

        /// <summary>
        /// The EMSP registers with the CSMS, and a location the CSMS publishes
        /// after that reaches it all the same: whoever started the peering,
        /// the CSMS has the EMSP's token and versions URL.
        /// </summary>
        /// <param name="Version">The OCPI version.</param>
        [TestCase("2.1.1")]
        [TestCase("2.2.1")]
        [TestCase("2.3.0")]
        public async Task OCPI_ALocationOfTheCSMS_ReachesTheEMSPThatRegistered(String Version)
        {

            await StartBoth(Version);

            await Peered   (Version, "emsp");
            await Published(Version, "LOC0003");

            await Until(() => EMSPKnowsLocation(Version, "LOC0003"), "'LOC0003' reaching the EMSP", csms, emsp);

        }

        #endregion

        #region (private) Published(Version, Id) / EMSPKnowsLocation(Version, Id)

        /// <summary>
        /// The CSMS publishes a location of the given identification.
        /// </summary>
        private async Task Published(String Version, String Id)
        {

            var published = await csms!.AddLocationAsync(new JObject(
                                                              new JProperty("version",     Version),
                                                              new JProperty("id",          Id),
                                                              new JProperty("name",        "GraphDefined Parking"),
                                                              new JProperty("address",     "Biberweg 18"),
                                                              new JProperty("city",        "Jena"),
                                                              new JProperty("postalCode",  "07749"),
                                                              new JProperty("country",     "DEU"),
                                                              new JProperty("latitude",    50.9),
                                                              new JProperty("longitude",   11.6),
                                                              new JProperty("timeZone",    "Europe/Berlin")
                                                          ));

            Assert.That(published.Success, Is.True, published.Message);

        }

        /// <summary>
        /// Whether the EMSP holds the location of the given identification.
        /// </summary>
        private Boolean EMSPKnowsLocation(String Version, String Id)

            => emsp!.OCPIVersions.
                     Where     (version  => version.Label == Version).
                     SelectMany(version  => version.Locations).
                     Any       (location => location.Value<String>("id") == Id);

        #endregion

    }

}
