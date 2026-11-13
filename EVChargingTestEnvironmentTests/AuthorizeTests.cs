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
using cloud.charging.open.protocols.OCPP.Node.Connections;

using WWCPNode    = cloud.charging.open.protocols.WWCP.Node.WWCPNode;
using Management  = cloud.charging.open.CSMS.CSMS;
using Controller  = cloud.charging.open.LocalController.LocalController;
using Station     = cloud.charging.open.ChargingStation.ChargingStation;
using ServerCertificateStore = cloud.charging.open.CSMS.OCPP.ServerCertificateStore;

#endregion

namespace cloud.charging.open.TestEnvironment.Tests
{

    /// <summary>
    /// A card at a real charging station, asked about at a real CSMS: the
    /// station boots, is accepted, and sends an OCPP Authorize for the card
    /// before it starts a session - over each of the ways a station signs in
    /// to its CSMS, and through a local controller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No single repository can hold this. The station's own tests have a
    /// stand-in for a CSMS that answers what it is told to, the CSMS's own
    /// tests a WebSocket client standing in for a station, and neither says
    /// whether the two programs agree: on who the station is, on where its
    /// request goes, and on what the answer means.
    /// </para>
    /// <para>
    /// The cards are what an operator really has: tokens a roaming partner
    /// sent over OCPI. The partner is a real EMSP, peered with the CSMS over
    /// OCPI 2.2.1 as RoamingPeeringTests peers them, and the cards are the
    /// ones it issues - one that may charge, and one it calls not valid. A
    /// third is nobody's.
    /// </para>
    /// <para>
    /// The transports are the ways of OCPP security profile 1 and of "OCPP
    /// over SSH": a password in HTTP Basic, a one-time password in HTTP TOTP,
    /// and an SSH key - and the way of security profile 3, a TLS client
    /// certificate. The same three questions are asked over each.
    /// </para>
    /// <para>
    /// For the certificate the operator has a CA of its own, made here and
    /// known to no machine: it signs the certificate of the CSMS and the one
    /// of the station, the CSMS is told to believe stations it vouches for,
    /// and the station keeps it as a TLS root of its own certificate store
    /// for "ocpp". Nothing is installed on the machine.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class AuthorizeTests
    {

        #region Data

        private const String  KnownCard       = "04A2B3C4D5E6F7";
        private const String  InvalidCard     = "04BBBBBBBBBBBB";
        private const String  NobodysCard     = "04CCCCCCCCCCCC";

        private const String  ThePassword     = "a-password-long-enough-for-ocpp";
        private const String  TheSecret       = "a-shared-secret-long-enough";

        private const String  ControllerId    = "lc001";
        private const String  UplinkPassword  = "the-controllers-password-at-its-csms";

        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

        private Management?        csms;
        private EMSP.EMSP?         emsp;
        private Controller?        controller;

        /// <summary>
        /// The operator's own CA, where a test wants one: no machine knows it.
        /// </summary>
        private X509Certificate2?  ca;
        private ECDsa?             caKey;

        private readonly List<Station>             stations     = [];
        private readonly List<String>              directories  = [];
        private readonly ConcurrentBag<DNSClient>  resolvers    = [];

        /// <summary>
        /// The actions of the requests that reached the CSMS, as they came.
        /// </summary>
        private readonly ConcurrentQueue<String>   atTheCSMS    = new ();

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public async Task StartTheCSMS_AndGiveItCards()
        {

            var directory = TestParties.TemporaryDirectory("csms-authorize");
            directories.Add(directory);

            // The station port of a CSMS speaks TLS or it does not, decided
            // once when it starts: the test of the client certificate gets a
            // CSMS on security profile 3, the others one on profile 1.
            var withTLS = TestContext.CurrentContext.Test.Arguments.Contains("certificate");

            if (withTLS)
                TheCSMSGetsACertificateOfTheOperatorsCA(directory);

            csms = await TestPorts.StartedOnFreshPorts(() => NewCSMS(directory, withTLS));

            csms.Node.OCPP.IN.OnJSONRequestMessageReceived += (timestamp, sender, connection, request, cancellationToken) => {
                atTheCSMS.Enqueue(request.Action);
                return Task.CompletedTask;
            };

            await AnEMSPIsPeered();

            await TheEMSPIssues(KnownCard,   Valid: true);
            await TheEMSPIssues(InvalidCard, Valid: false);

        }

        [TearDown]
        public async Task StopEverything()
        {

            foreach (var station in stations)
                await station.DisposeAsync();

            if (controller is not null)
                await controller.DisposeAsync();

            if (csms is not null)
                await csms.DisposeAsync();

            if (emsp is not null)
                await emsp.DisposeAsync();

            foreach (var resolver in resolvers)
                resolver.Dispose();

            foreach (var directory in directories)
                TestParties.Remove(directory);

            ca?.   Dispose();
            caKey?.Dispose();

            controller  = null;
            csms        = null;
            emsp        = null;
            ca          = null;
            caKey       = null;

            stations.   Clear();
            directories.Clear();
            resolvers.  Clear();
            atTheCSMS.  Clear();

        }

        #endregion


        #region (private) The CSMS, and its cards

        private Management CSMS
            => csms ?? throw new InvalidOperationException("No CSMS is running.");

        private DNSClient Resolver()
        {

            var resolver = new DNSClient([
                               new DNSServerConfig(IPv4Address.Parse("192.0.2.53"),   IPPort.DNS),
                               new DNSServerConfig(IPv6Address.Parse("2001:db8::53"), IPPort.DNS)
                           ]);

            resolvers.Add(resolver);

            return resolver;

        }

        /// <summary>
        /// A CSMS that is a charge point operator, with its station port and
        /// OCPP over SSH switched on: the port on security profile 1, or on
        /// profile 3 - TLS, and a station is known by its certificate.
        /// </summary>
        private Management NewCSMS(String Directory, Boolean WithTLS = false)
        {

            System.IO.Directory.CreateDirectory(Directory);

            var configFile = Path.Combine(Directory, "configuration.json");

            File.WriteAllText(configFile, new JObject(
                new JProperty("nts",         TestParties.NoTimeServers),
                new JProperty("ocppServer",  new JObject(
                    new JProperty("enabled",           true),
                    new JProperty("address",           "127.0.0.1"),
                    new JProperty("port",              TestPorts.Free()),
                    new JProperty("securityProfiles",  new JArray(WithTLS ? 3 : 1)),
                    new JProperty("reachableAs",       new JArray("127.0.0.1")),
                    new JProperty("subprotocols",      new JArray("ocpp2.1", "ocpp2.0.1")),
                    new JProperty("ssh",               new JObject(
                        new JProperty("enabled",         true)
                    ))
                )),
                new JProperty("ssh",         new JObject(
                    new JProperty("enabled",           true),
                    new JProperty("port",              TestPorts.Free())
                )),
                new JProperty("ocpi",        new JObject(
                    new JProperty("countryCode",     "DE"),
                    new JProperty("partyId",         "GEF"),
                    new JProperty("name",            "GraphDefined CPO"),
                    new JProperty("versions",        new JArray("2.2.1")),
                    new JProperty("remoteNetworks",  new JArray("127.0.0.0/8 => ALLOW", "::1 => ALLOW"))
                ))
            ).ToString());

            var management = new Management(
                                 DNSClient:       Resolver(),
                                 HTTPPort:        IPPort.Parse(TestPorts.Free()),
                                 AccountsPath:    Path.Combine(Directory, "accounts"),
                                 ConfigFile:      new WWCPConfigFile(configFile),
                                 LogToConsole:    false,
                                 BridgeDebugLog:  false
                             );

            // The controller of the last test signs in here with a password.
            Assert.That(management.StationLogins.TrySetPassword(ControllerId, UplinkPassword, null, "tests", out _, out var error), Is.True, error);

            // A local controller's login: in the overlay network, which the
            // controller dials by default, what it forwards in the name of
            // its stations is taken only from a login marked as one.
            Assert.That(management.StationLogins.TrySetLocalController(ControllerId, true, out error), Is.True, error);

            // Whatever the operator's CA vouches for is a station of this CSMS.
            if (WithTLS)
                Assert.That(management.ClientTrust.TryAdd(ca!.ExportCertificatePem(), "The operator's CA", out _, out _, out error), Is.True, error);

            return management;

        }

        #endregion

        #region (private) The operator's CA

        /// <summary>
        /// The operator's CA, made in memory. Its name is its own per run:
        /// Windows caches chain building by issuer name, and a second CA
        /// called what the last run called its own is looked up, found, and
        /// does not fit.
        /// </summary>
        private X509Certificate2 TheOperatorsCA()
        {

            if (ca is not null)
                return ca;

            caKey        = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            var request  = new CertificateRequest($"CN=The operator's CA {Guid.NewGuid()}", caKey, HashAlgorithmName.SHA256);

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

            ca           = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));

            return ca;

        }

        /// <summary>
        /// The operator's CA answers a signing request as it was made: with
        /// the names and the purposes it asked for.
        /// </summary>
        private String SignedByTheOperatorsCA(String CSR)
        {

            var issuer   = TheOperatorsCA();

            var request  = CertificateRequest.LoadSigningRequestPem(
                               CSR,
                               HashAlgorithmName.SHA256,
                               CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions
                           );

            using var certificate = request.Create(
                                        issuer.SubjectName,
                                        X509SignatureGenerator.CreateForECDsa(caKey!),
                                        DateTimeOffset.UtcNow.AddDays(-1),
                                        DateTimeOffset.UtcNow.AddDays(30),
                                        Guid.NewGuid().ToByteArray()
                                    );

            return certificate.ExportCertificatePem();

        }

        /// <summary>
        /// A key of the CSMS's own and a certificate of the operator's CA for
        /// it, in the store of the CSMS before it starts: this is what makes
        /// its station port speak TLS.
        /// </summary>
        private void TheCSMSGetsACertificateOfTheOperatorsCA(String Directory)
        {

            System.IO.Directory.CreateDirectory(Directory);

            using var store = new ServerCertificateStore(Path.Combine(Directory, ServerCertificateStore.DefaultDirectoryName));

            Assert.That(store.TryCreateKey("127.0.0.1", [ "127.0.0.1" ], null, out _, out var csr, out var error), Is.True, error);

            Assert.That(store.TryAddCertificate(SignedByTheOperatorsCA(csr!) + Environment.NewLine + TheOperatorsCA().ExportCertificatePem(),
                                                [ "127.0.0.1" ], out _, out _, out error),
                        Is.True, error);

        }

        /// <summary>
        /// A real EMSP, peered with the CSMS: the EMSP has a token ready for
        /// the operator, the operator is told it and where to present it, and
        /// registers.
        /// </summary>
        private async Task AnEMSPIsPeered()
        {

            var directory = TestParties.TemporaryDirectory("emsp-authorize");
            directories.Add(directory);

            emsp = TestParties.NewEMSP(directory);

            await emsp.Start();

            var atTheEMSP  = await emsp.AddRemotePartyAsync(new JObject(
                                            new JProperty("version",      "2.2.1"),
                                            new JProperty("countryCode",  CSMS.PartyId.CountryCode.ToString()),
                                            new JProperty("partyId",      CSMS.PartyId.PartyId.    ToString()),
                                            new JProperty("role",         "CPO"),
                                            new JProperty("name",         CSMS.BusinessDetails.Name)
                                        ));

            Assert.That(atTheEMSP.Success, Is.True, $"The EMSP would not take the operator as a partner: {atTheEMSP.Message}");

            var atTheCSMS  = await CSMS.AddRemotePartyAsync(new JObject(
                                            new JProperty("version",      "2.2.1"),
                                            new JProperty("countryCode",  emsp.PartyId.CountryCode.ToString()),
                                            new JProperty("partyId",      emsp.PartyId.PartyId.    ToString()),
                                            new JProperty("role",         "EMSP"),
                                            new JProperty("name",         emsp.BusinessDetails.Name),
                                            new JProperty("theirToken",   atTheEMSP.Data!.Value<String>("ourToken")),
                                            new JProperty("versionsURL",  emsp.OCPIVersionsURL.ToString())
                                        ));

            Assert.That(atTheCSMS.Success, Is.True, $"The operator would not take the EMSP as a partner: {atTheCSMS.Message}");

            var partner     = CSMS.OCPIVersions.
                                   SelectMany(version => version.RemoteParties).
                                   First     (party   => party.PartyId == emsp.PartyId.PartyId);

            var registered  = await CSMS.RegisterRemotePartyAsync("2.2.1", partner.Id.ToString());

            Assert.That(registered.Success, Is.True, $"The operator could not register with the EMSP: {registered.Message}{Story()}");

        }

        /// <summary>
        /// The EMSP issues a card - and the operator knows of it, because the
        /// EMSP sent it there over OCPI.
        /// </summary>
        private async Task TheEMSPIssues(String UID, Boolean Valid)
        {

            var issued = await emsp!.AddTokenAsync(new JObject(
                                         new JProperty("version",  "2.2.1"),
                                         new JProperty("uid",      UID),
                                         new JProperty("type",     "RFID"),
                                         new JProperty("valid",    Valid)
                                     ));

            Assert.That(issued.Success, Is.True, $"The EMSP would not issue the card: {issued.Message}");

            await Until(() => CSMS.OCPIVersions.SelectMany(version => version.Tokens).Any(token => token.Value<String>("uid") == UID),
                        $"The card {UID} the EMSP issued reaching the operator");

        }

        #endregion

        #region (private) The stations

        private Station NewStation(String? Directory = null)
        {

            var directory   = Directory ?? TestParties.TemporaryDirectory("cs-authorize");

            if (Directory is null)
                directories.Add(directory);

            System.IO.Directory.CreateDirectory(directory);

            var configFile  = Path.Combine(directory, "configuration.json");

            if (!File.Exists(configFile))
                File.WriteAllText(configFile, new JObject(new JProperty("nts", TestParties.NoTimeServers)).ToString());

            var station = new Station(
                              DNSClient:       Resolver(),
                              HTTPPort:        IPPort.Parse(TestPorts.Free()),
                              NoKiosk:         true,
                              AccountsPath:    Path.Combine(directory, "accounts"),
                              ConfigFile:      new WWCPConfigFile(configFile),
                              LogToConsole:    false,
                              BridgeDebugLog:  false
                          );

            stations.Add(station);

            return station;

        }

        private async Task<Station> Started(Station Station)
        {

            var directory  = Path.GetDirectoryName(Station.ConfigFile.Path)!;
            var attempt    = 0;

            return await TestPorts.StartedOnFreshPorts(() => attempt++ == 0 ? Station : NewStation(directory));

        }

        /// <summary>
        /// A station that signs in to the CSMS over the given transport,
        /// started, connected and accepted.
        /// </summary>
        private async Task<Station> AStationAtTheCSMS(String Transport, String Login)
        {

            var station         = NewStation();
            var webSocketPort   = CSMS.OCPPServerSettings.TCPPort!.Value.ToUInt16();
            String? credentials = null;
            String? error;
            String  url;
            String? hostKey     = null;
            String? certificate = null;

            switch (Transport)
            {

                case "basic":
                    Assert.That(CSMS.StationLogins.TrySetPassword(Login, ThePassword, null, "tests", out _, out error), Is.True, error);
                    Assert.That(station.Connections.TryAddAuthentication("The CSMS", "basic", Login, ThePassword, out credentials, out error), Is.True, error);
                    url = $"ws://127.0.0.1:{webSocketPort}";
                    break;

                case "totp":
                    Assert.That(CSMS.StationLogins.TrySetTOTP(Login, TheSecret, null, null, null, null, null, "tests", out _, out error), Is.True, error);
                    Assert.That(station.Connections.TryAddAuthentication("The CSMS", "totp", Login, TheSecret, out credentials, out error), Is.True, error);
                    url = $"ws://127.0.0.1:{webSocketPort}";
                    break;

                case "ssh":
                    var key = await station.SshKey.MakeIfNeeded();
                    Assert.That(CSMS.TryAddStationSSHKey(Login, SshPublicKey.FromHostKey(key).ToAuthorizedKeyLine(), "test", null, null, "tests", out _, out error, out _), Is.True, error);
                    url     = $"ssh://{Login}@127.0.0.1:{CSMS.SSHPort!.Value.ToUInt16()}";
                    hostKey = CSMS.SSHHostKey!.Split(' ', 2)[1];
                    break;

                case "certificate":

                    // A key of the station's own, and for it a certificate of
                    // the operator's CA that names the station: that name is
                    // who the CSMS takes it for.
                    Assert.That(station.ClientCertificates.TryCreateKey(Login, null, out var keyId, out var csr, out error), Is.True, error);
                    Assert.That(station.ClientCertificates.TryAddCertificate(SignedByTheOperatorsCA(csr!), out _, out _, out error), Is.True, error);

                    // The CSMS is believed because the operator's CA is a TLS
                    // root of the station's own store, kept for OCPP - the
                    // machine has never heard of it.
                    Assert.That(station.Certificates.Import(System.Text.Encoding.ASCII.GetBytes(TheOperatorsCA().ExportCertificatePem()),
                                                            CertificateKind.TLSRoot, null, "The operator's CA",
                                                            [ Station.OCPPCertificateUsage ], out _, out error),
                                Is.True, error);

                    url         = $"wss://127.0.0.1:{webSocketPort}";
                    certificate = keyId;
                    break;

                default:
                    throw new ArgumentException($"'{Transport}' is no transport these tests know.", nameof(Transport));

            }

            Assert.That(station.Connections.TryAddConnection($"The CSMS over {Transport}",
                                                             url,
                                                             "csms",
                                                             true,
                                                             credentials,
                                                             certificate,
                                                             out var connection,
                                                             out error,
                                                             SshHostKey: hostKey),
                        Is.True, error);

            station = await Started(station);

            await Until(() => StatusOf(station, connection!) == ConnectionStatus.Connected, $"'{Login}' connecting over {Transport}", station);
            await Until(() => RegistrationOf(station) == "Accepted",                       $"'{Login}' being accepted by the CSMS",  station);

            return station;

        }

        private static ConnectionStatus? StatusOf(Station Station, String Connection)
            => Station.ConnectionStates.TryGetValue(Connection, out var state) ? state.Status : null;

        private static String RegistrationOf(Station Station)
            => Station.RegistrationJSON()["2.1"]?["state"]?.Value<String>() ?? "";

        #endregion

        #region (private) AStationBelowAController()

        /// <summary>
        /// A controller that reports to the CSMS, and below it a station that
        /// signed in with a password, booted through it and was accepted by
        /// the CSMS.
        /// </summary>
        private async Task<Station> AStationBelowAController()
        {

            #region The controller, reporting to the CSMS

            var directory = TestParties.TemporaryDirectory("lc-authorize");
            directories.Add(directory);

            controller = await TestPorts.StartedOnFreshPorts(() => {

                System.IO.Directory.CreateDirectory(directory);

                var configFile = Path.Combine(directory, "configuration.json");

                File.WriteAllText(configFile, new JObject(
                    new JProperty("nts",         TestParties.NoTimeServers),
                    new JProperty("csms",        new JObject(
                        new JProperty("enabled",           true),
                        new JProperty("url",               $"ws://127.0.0.1:{CSMS.OCPPServerSettings.TCPPort}"),
                        new JProperty("securityProfile",   1),
                        new JProperty("nextHopNodeId",     "CSMS")
                    )),
                    new JProperty("ocppServer",  new JObject(
                        new JProperty("enabled",           true),
                        new JProperty("address",           "127.0.0.1"),
                        new JProperty("port",              TestPorts.Free()),
                        new JProperty("securityProfiles",  new JArray(1)),
                        new JProperty("subprotocols",      new JArray("ocpp2.1", "ocpp2.0.1"))
                    ))
                ).ToString());

                var localController = new Controller(
                                          DNSClient:       Resolver(),
                                          HTTPPort:        IPPort.Parse(TestPorts.Free()),
                                          AccountsPath:    Path.Combine(directory, "accounts"),
                                          ConfigFile:      new WWCPConfigFile(configFile),
                                          LogToConsole:    false,
                                          BridgeDebugLog:  false
                                      );

                Assert.That(localController.Node.Id.ToString(), Is.EqualTo(ControllerId));
                Assert.That(localController.CSMSLogin.TrySetPassword(ControllerId, UplinkPassword, out var loginError), Is.True, loginError);

                return localController;

            });

            await Until(() => controller.CSMSConnected, "The controller connecting to its CSMS");

            #endregion

            #region The station, below the controller

            Assert.That(controller.StationLogins.TrySetPassword("cs001", ThePassword, null, "tests", out _, out var error), Is.True, error);

            var station = NewStation();

            Assert.That(station.Connections.TryAddAuthentication("The controller", "basic", "cs001", ThePassword, out var credentials, out error), Is.True, error);

            Assert.That(station.Connections.TryAddConnection("The controller",
                                                             $"ws://127.0.0.1:{controller.OCPPServerSettings.TCPPort!.Value.ToUInt16()}",
                                                             "localcontroller",
                                                             true,
                                                             credentials,
                                                             null,
                                                             out var connection,
                                                             out error),
                        Is.True, error);

            station = await Started(station);

            await Until(() => StatusOf(station, connection!) == ConnectionStatus.Connected, "The station connecting to the controller",          station);
            await Until(() => atTheCSMS.Contains("BootNotification"),                      "The station's BootNotification reaching the CSMS", station);
            await Until(() => RegistrationOf(station) == "Accepted",                       "The CSMS's answer reaching the station",           station);

            #endregion

            return station;

        }

        #endregion

        #region (private) Until(Condition, What, Station = null) / Story(Station = null)

        private async Task Until(Func<Boolean> Condition, String What, Station? Station = null)
        {

            var until = DateTimeOffset.UtcNow + Patience;

            while (!Condition())
            {

                if (DateTimeOffset.UtcNow > until)
                    Assert.Fail($"{What} did not happen within {Patience.TotalSeconds:F0} s.{Story(Station)}");

                await Task.Delay(25);

            }

        }

        private String Story(Station? Station = null)

            => String.Join(
                   Environment.NewLine,
                   new[] { "", "What was said:" }.
                       Concat(new (String Who, WWCPNode? Node)[] { ("CSMS", csms), ("EMSP", emsp), ("LC", controller) }.
                                  SelectMany(speaker => speaker.Node?.Log.Recent(100).Select(entry => $"  {speaker.Who,-4} {entry.Timestamp:HH:mm:ss.fff} [{entry.LevelName}] {entry.Message}") ?? [])).
                       Concat(Station?.Log.Recent(100).Select(entry => $"  CS   {entry.Timestamp:HH:mm:ss.fff} [{entry.LevelName}] {entry.Message}") ?? []).
                       Concat(Station?.DialledConnections.Select(said => $"  CS   dialled {said.Key}: {said.Value}") ?? [])
               );

        #endregion

        #region (private) TheThreeCards(Station, Via)

        /// <summary>
        /// The three questions, asked of the given station: a card a partner
        /// calls valid starts a session, one it calls not valid and one that
        /// is nobody's do not - and each was asked about at the CSMS.
        /// </summary>
        private async Task TheThreeCards(Station Station, String Via)
        {

            #region A card that may charge

            var (started, _, startError) = await Station.StartLocally(null, KnownCard);

            Assert.That(started,                              Is.Not.Null, $"{Via}: the card a partner calls valid did not start a session: {startError}{Story(Station)}");
            Assert.That(started!.Value<Boolean>("started"),   Is.True);
            Assert.That(atTheCSMS.Count(action => action == "Authorize"), Is.EqualTo(1), $"{Via}: the CSMS was not asked about the card.{Story(Station)}");

            // Stopped again, and nobody is asked: whoever started a session
            // may end it.
            Assert.That(Station.TryStopLocally(started.Value<String>("sessionId"), out _, out _, out var stopError), Is.True, stopError);
            Assert.That(atTheCSMS.Count(action => action == "Authorize"), Is.EqualTo(1), $"{Via}: the CSMS was asked about a stop.");

            #endregion

            #region A card its partner calls not valid

            var (invalid, invalidRefusal, invalidError) = await Station.StartLocally(null, InvalidCard);

            Assert.Multiple(() => {
                Assert.That(invalid,          Is.Null,                                 $"{Via}: a card its partner calls not valid started a session.");
                Assert.That(invalidRefusal,   Is.EqualTo(LocalAppRefusal.NotAuthorized));
                Assert.That(invalidError,     Does.Contain("Invalid"));
            });

            #endregion

            #region A card that is nobody's

            var (nobodys, nobodysRefusal, nobodysError) = await Station.StartLocally(null, NobodysCard);

            Assert.Multiple(() => {
                Assert.That(nobodys,          Is.Null,                                 $"{Via}: a card no partner sent started a session.");
                Assert.That(nobodysRefusal,   Is.EqualTo(LocalAppRefusal.NotAuthorized));
                Assert.That(nobodysError,     Does.Contain("Unknown"));
                Assert.That(atTheCSMS.Count(action => action == "Authorize"), Is.EqualTo(3), $"{Via}: not every card was asked about at the CSMS.");
                Assert.That(Station.KioskJSON().ToString(), Does.Not.Contain("\"occupied\""),  $"{Via}: an outlet is taken after two cards were turned away.");
            });

            #endregion

        }

        #endregion


        #region AStationBoots_AndItsCardsAreDecidedOnByTheCSMS(Transport)

        /// <summary>
        /// A station signs in to its CSMS, boots, is accepted - and a card
        /// starts a session there exactly where the CSMS says it may: over a
        /// password, over a one-time password, over SSH, and over TLS with a
        /// certificate of its own.
        /// </summary>
        [TestCase("basic")]
        [TestCase("totp")]
        [TestCase("ssh")]
        [TestCase("certificate")]
        public async Task AStationBoots_AndItsCardsAreDecidedOnByTheCSMS(String Transport)
        {

            var station = await AStationAtTheCSMS(Transport, "cs001");

            Assert.That(atTheCSMS, Does.Contain("BootNotification"), $"The station's BootNotification did not reach the CSMS.{Story(station)}");

            if (Transport == "certificate")
            {

                var judgement = station.LastJudgementOf(Station.OCPPCertificateUsage, "127.0.0.1");

                Assert.Multiple(() => {
                    Assert.That(CSMS.OCPPServerTLS,           Is.True,  "The station port of the CSMS does not speak TLS.");
                    Assert.That(judgement?.Accepted,          Is.True,  $"The station did not believe the CSMS.{Story(station)}");
                    Assert.That(judgement?.RootFingerprint,   Is.EqualTo(CertificateEntry.ThumbprintOf(ca!)),
                                "The CSMS was believed through something else than the operator's CA.");
                    Assert.That(CSMS.StationCertificates.Get("cs001", "ChargingStationCertificate")?.Source, Is.EqualTo("presented"),
                                "The CSMS does not know the certificate the station signed in with.");
                    Assert.That(CSMS.Log.Recent(200).Select(entry => entry.Message),
                                Has.Some.Contains("'cs001' signed in").And.Contains("with a certificate (security profile 3)"));
                });

            }

            await TheThreeCards(station, Transport);

        }

        #endregion

        #region AStationBelowAController_HasItsCardsDecidedOnByTheCSMS()

        /// <summary>
        /// The whole chain as a site has it: the station signs in to a local
        /// controller, the controller reports to the CSMS, and the station's
        /// Authorize is sent on up and answered from there.
        /// </summary>
        /// <remarks>
        /// Before, the controller's OCPP node answered every Authorize itself
        /// with "Unauthorized!": it forwards for the stations on a list of its
        /// own, which nothing filled.
        /// </remarks>
        [Test]
        public async Task AStationBelowAController_HasItsCardsDecidedOnByTheCSMS()
        {

            var station = await AStationBelowAController();

            await TheThreeCards(station, "through the controller");

        }

        #endregion

        #region AControllerThatLostItsCSMS_StartsNoCard()

        /// <summary>
        /// A controller whose line to the CSMS went down has nobody to ask
        /// and decides on no card itself: the station below it, which the CSMS
        /// accepted while the line was up, is told so at once - and not left
        /// to wait for an answer that cannot come - and starts nothing.
        /// </summary>
        [Test]
        public async Task AControllerThatLostItsCSMS_StartsNoCard()
        {

            var station = await AStationBelowAController();

            await CSMS.DisposeAsync();
            csms = null;

            await Until(() => !controller!.CSMSConnected, "The controller noticing that its CSMS is gone", station);

            var before                    = atTheCSMS.Count(action => action == "Authorize");
            var asked                     = DateTimeOffset.UtcNow;
            var (started, refusal, why)   = await station.StartLocally(null, KnownCard);

            Assert.Multiple(() => {
                Assert.That(started,                         Is.Null, $"A card started a session with nobody to say it may.{Story(station)}");
                Assert.That(refusal,                         Is.EqualTo(LocalAppRefusal.NotAuthorized).Or.EqualTo(LocalAppRefusal.BackEndSilent), why);
                Assert.That(DateTimeOffset.UtcNow - asked,   Is.LessThan(TimeSpan.FromSeconds(5)), $"The station waited for an answer that could not come.{Story(station)}");
                Assert.That(atTheCSMS.Count(action => action == "Authorize"), Is.EqualTo(before));
                Assert.That(station.KioskJSON().ToString(),  Does.Not.Contain("\"occupied\""));
            });

        }

        #endregion

    }

}
