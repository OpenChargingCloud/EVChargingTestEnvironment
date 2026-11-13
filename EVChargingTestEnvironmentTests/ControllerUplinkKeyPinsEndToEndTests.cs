/*
 * Copyright (c) 2014-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of EVChargingTestEnvironment <https://github.com/OpenChargingCloud/EVChargingTestEnvironment>
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

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;

using cloud.charging.open.protocols.OCPP.Node.KeyPins;
using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

using WWCPNode          = cloud.charging.open.protocols.WWCP.Node.WWCPNode;
using Management        = cloud.charging.open.CSMS.CSMS;
using Controller        = cloud.charging.open.LocalController.LocalController;
using CSMSCertificates  = cloud.charging.open.CSMS.OCPP.ServerCertificateStore;

#endregion

namespace cloud.charging.open.TestEnvironment.Tests
{

    /// <summary>
    /// The key pin of a local controller's own line to its CSMS, from end to end
    /// ("Key Pins and TLS 1.3 Raw Public Keys for Local Controllers", sections
    /// 5.6 and 5.7, one level up): a real controller that dials a real CSMS over
    /// wss://, pins nothing, learns the key the CSMS shows on first use and
    /// reports it - and the CSMS, whose own key that is, that confirms it with a
    /// pin set signed with its own key.
    /// </summary>
    /// <remarks>
    /// The CSMS's tests have a fake controller, the controller's a fake CSMS.
    /// Here the pin learned is the pin of the certificate the CSMS really
    /// presents, the pin set is verified by the controller's own pin set
    /// signers, and the events come back over the line they are about.
    /// </remarks>
    [TestFixture]
    public class ControllerUplinkKeyPinsEndToEndTests
    {

        #region Data

        private const String  ControllerId    = "lc001";
        private const String  UplinkPassword  = "the-controllers-password-at-its-csms";

        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(45);

        private Management?        csms;
        private Controller?        controller;
        private X509Certificate2?  ca;
        private ECDsa?             caKey;

        private readonly List<String>     directories  = [];
        private readonly List<DNSClient>  resolvers    = [];

        #endregion

        #region TearDown

        [TearDown]
        public async Task StopEverything()
        {

            if (controller is not null)
                await controller.DisposeAsync();

            if (csms is not null)
                await csms.DisposeAsync();

            foreach (var resolver in resolvers)
                resolver.Dispose();

            foreach (var directory in directories)
                TestParties.Remove(directory);

            ca?.   Dispose();
            caKey?.Dispose();

            directories.Clear();
            resolvers.  Clear();

            controller  = null;
            csms        = null;
            ca          = null;
            caKey       = null;

        }

        #endregion


        #region (private) The CSMS and the controller

        private Management CSMS
            => csms       ?? throw new InvalidOperationException("No CSMS is running.");

        private Controller LC
            => controller ?? throw new InvalidOperationException("No controller is running.");

        private String Directory(String Purpose)
        {
            var directory = TestParties.TemporaryDirectory(Purpose);
            directories.Add(directory);
            System.IO.Directory.CreateDirectory(directory);
            return directory;
        }

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
        /// A key of the CSMS's own and a certificate of a CA made here for it, in
        /// the store of the CSMS before it starts: this is what makes its station
        /// port speak TLS. The CA's name is its own per run, as Windows caches
        /// chain building by issuer name.
        /// </summary>
        private void TheCSMSGetsACertificate(String Directory)
        {

            caKey        = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            var request  = new CertificateRequest($"CN=The operator's CA {Guid.NewGuid()}", caKey, HashAlgorithmName.SHA256);

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

            ca           = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));

            using var store = new CSMSCertificates(Path.Combine(Directory, CSMSCertificates.DefaultDirectoryName));

            Assert.That(store.TryCreateKey("127.0.0.1", [ "127.0.0.1" ], null, out _, out var csr, out var error), Is.True, error);

            using var certificate = CertificateRequest.LoadSigningRequestPem(csr!, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions).
                                        Create(ca.SubjectName,
                                               X509SignatureGenerator.CreateForECDsa(caKey),
                                               DateTimeOffset.UtcNow.AddDays(-1),
                                               DateTimeOffset.UtcNow.AddDays(30),
                                               Guid.NewGuid().ToByteArray());

            Assert.That(store.TryAddCertificate(certificate.ExportCertificatePem() + Environment.NewLine + ca.ExportCertificatePem(),
                                                [ "127.0.0.1" ], out _, out _, out error),
                        Is.True, error);

        }

        /// <summary>
        /// A CSMS whose station port speaks TLS, security profile 2, and which
        /// lets the controller in with its password, its login marked as a local
        /// controller's - started.
        /// </summary>
        private async Task StartTheCSMS(Boolean WithASigningKey)
        {

            var directory   = Directory("csms-uplink-keypins");
            var configFile  = Path.Combine(directory, "configuration.json");

            TheCSMSGetsACertificate(directory);

            csms = await TestPorts.StartedOnFreshPorts(() => {

                       File.WriteAllText(configFile, new JObject(
                           new JProperty("nts",         TestParties.NoTimeServers),
                           new JProperty("ocppServer",  new JObject(
                               new JProperty("enabled",           true),
                               new JProperty("address",           "127.0.0.1"),
                               new JProperty("port",              TestPorts.Free()),
                               new JProperty("securityProfiles",  new JArray(2)),
                               new JProperty("subprotocols",      new JArray("ocpp2.1"))
                           ))
                       ).ToString());

                       var management = new Management(
                                            DNSClient:       Resolver(),
                                            HTTPPort:        IPPort.Parse(TestPorts.Free()),
                                            AccountsPath:    Path.Combine(directory, "accounts"),
                                            ConfigFile:      new WWCPConfigFile(configFile),
                                            LogToConsole:    false,
                                            BridgeDebugLog:  false
                                        );

                       Assert.That(management.StationLogins.TrySetPassword       (ControllerId, UplinkPassword, null, "tests", out _, out var error), Is.True, error);
                       Assert.That(management.StationLogins.TrySetLocalController(ControllerId, true, out var markError),                           Is.True, markError);

                       return management;

                   });

            Assert.That(CSMS.OCPPServerTLS,         Is.True,           "a CSMS with a certificate does not speak TLS on its station port");
            Assert.That(CSMS.ServerKeyPins().Count, Is.EqualTo(1),     "the CSMS does not know the key pin of the key it presents");

            if (WithASigningKey)
                Assert.That(CSMS.PinSetSigner.TryCreate("uplink tests", out _, out var signerError), Is.True, signerError);

        }

        /// <summary>
        /// A controller that dials the CSMS over wss:// with its password, pins
        /// nothing, trusts the key the CSMS shows on first use, and believes pin
        /// sets of the given signer - started.
        /// </summary>
        private async Task StartTheController(String? PinSetSigner)
        {

            var directory   = Directory("lc-uplink-keypins");
            var configFile  = Path.Combine(directory, "configuration.json");

            controller = await TestPorts.StartedOnFreshPorts(() => {

                             File.WriteAllText(configFile, new JObject(
                                 new JProperty("nts",   TestParties.NoTimeServers),
                                 new JProperty("csms",  new JObject(
                                     new JProperty("enabled",                true),
                                     new JProperty("url",                    $"wss://127.0.0.1:{CSMS.OCPPServerSettings.TCPPort}"),
                                     new JProperty("securityProfile",        2),
                                     new JProperty("networkingMode",         "overlay"),
                                     new JProperty("reconnectInitialDelay",  1),
                                     new JProperty("reconnectMaxDelay",      2)
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

                             Assert.That(localController.CSMSLogin.TrySetPassword(ControllerId, UplinkPassword, out var uplinkError),          Is.True, uplinkError);
                             Assert.That(localController.CSMSPins. TrySetTrust(new ServerKeyPinSettings(null, "Key", null), out var trustError), Is.True, trustError);

                             if (PinSetSigner is not null)
                                 Assert.That(localController.CSMSPins.TryAddPinSetSigner(PinSetSigner, "The CSMS", out var signerError), Is.True, signerError);

                             return localController;

                         });

        }

        private IPinnedTLSEndpoint? Uplink
            => LC.CSMSPins.Secure.FirstOrDefault();

        #endregion

        #region (private) Until(Condition, What) / Said(Node, Text) / Story

        private async Task Until(Func<Boolean> Condition, String What)
        {

            var until = DateTimeOffset.UtcNow + Patience;

            while (!Condition())
            {

                if (DateTimeOffset.UtcNow > until)
                    Assert.Fail($"{What} did not happen within {Patience.TotalSeconds:F0} s.{Story}");

                await Task.Delay(25);

            }

        }

        private static Boolean Said(WWCPNode? Node, String Text)
            => Node?.Log.Recent(500).Any(entry => entry.Message.Contains(Text, StringComparison.Ordinal)) ?? false;

        private String Story

            => String.Join(
                   Environment.NewLine,
                   new[] { "", "What was said:" }.
                       Concat(new (String Who, WWCPNode? Node)[] { ("CSMS", csms), ("LC", controller) }.
                                  SelectMany(speaker => speaker.Node?.Log.Recent(100).Select(entry => $"  {speaker.Who,-4} {entry.Timestamp:HH:mm:ss.fff} [{entry.LevelName}] {entry.Message}") ?? []))
               );

        #endregion


        #region TheKeyPinTheControllerLearnedOfItsCSMS_IsConfirmedByTheCSMS_AtOnce()

        /// <summary>
        /// The controller learns the key pin of the key the CSMS presents, on
        /// probation, and reports KeyPinLearned over that line; the CSMS finds
        /// the pin to be its own, asks the controller - on probation - for its
        /// report of TLSClientController, and sends it a pin set signed with its
        /// own key; the controller takes it and reports KeyPinConfirmed. The
        /// line never dropped.
        /// </summary>
        [Test]
        public async Task TheKeyPinTheControllerLearnedOfItsCSMS_IsConfirmedByTheCSMS_AtOnce()
        {

            await StartTheCSMS(WithASigningKey: true);
            await StartTheController(CSMS.PinSetSigner.PublicKey);

            var own = CSMS.ServerKeyPins().Single();

            await Until(() => Uplink?.ServerKeyPinState == KeyPinState.Confirmed,                  "The CSMS confirming the key pin the controller learned");
            await Until(() => CSMS.KeyPins.Get(ControllerId)?.Uplink?.Accepted == true,             "The CSMS noting the pin set taken");
            await Until(() => CSMS.KeyPins.Get(ControllerId)?.Uplink?.State    == "Confirmed",      "The controller's KeyPinConfirmed reaching the CSMS");

            var noted = CSMS.KeyPins.Get(ControllerId)?.Uplink;

            Assert.Multiple(() => {
                Assert.That(Uplink?.ServerKeyPins.Select(pin => pin.ToHex()),  Is.EqualTo(new[] { own.ToHex() }), Story);
                Assert.That(Uplink?.ServerKeyPinsVersion,                      Is.EqualTo(1));
                Assert.That(Uplink?.ServerKeyPinLearned,                       Is.Null, "the learned key pin is still on probation");
                Assert.That(LC.CSMSConnected,                                  Is.True, "the controller lost its line");
                Assert.That(CSMS.IsConnected(ControllerId),                    Is.True);
                Assert.That(noted?.LearnedKeyPin,                              Is.EqualTo(own.ToHex()));
                Assert.That(noted?.OursWhenLearned,                            Is.True);
                Assert.That(noted?.Endpoint,                                   Is.EqualTo(Uplink?.Id));
                Assert.That(noted?.Pins,                                       Is.EqualTo(new[] { own.ToHex() }));
                Assert.That(noted?.Version,                                    Is.EqualTo(1));
            });

        }

        #endregion

        #region PinsNamedByHand_WithoutTheOneTheControllerLearned_MakeItRefuseItsServer()

        /// <summary>
        /// A CSMS without a signing key sends nothing, and says so. Once it has
        /// one and the controller believes it, pins named by hand that do not
        /// have the key pin the controller learned are signed and taken all the
        /// same - and the controller refuses the server it learned it of: it
        /// hangs up, and is held to the pins it was handed, which this CSMS's
        /// key does not fit.
        /// </summary>
        [Test]
        public async Task PinsNamedByHand_WithoutTheOneTheControllerLearned_MakeItRefuseItsServer()
        {

            await StartTheCSMS(WithASigningKey: false);
            await StartTheController(PinSetSigner: null);

            var own = CSMS.ServerKeyPins().Single();

            await Until(() => Said(csms, $"'keyPins uplink {ControllerId}' before its probation runs out"), "The CSMS saying why it sends no pin set");

            var before = (State:    Uplink?.ServerKeyPinState,
                          Learned:  Uplink?.ServerKeyPinLearned,
                          Sent:     CSMS.KeyPins.Get(ControllerId)?.Uplink?.Sent);

            Assert.That(CSMS.PinSetSigner.TryCreate("uplink tests", out var signer, out var signerError), Is.True, signerError);
            Assert.That(LC.CSMSPins.TryAddPinSetSigner(signer!, "The CSMS", out var addError),            Is.True, addError);

            using var somebodyElses = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var other               = KeyPin.FromKey(somebodyElses);

            var run = await CSMS.ConfirmUplinkAsync(ControllerId, [ other ]);

            await Until(() => Uplink?.ServerKeyPins.Select(pin => pin.ToHex()).SequenceEqual([ other.ToHex() ]) == true, "The controller holding the pins it was handed");
            await Until(() => !CSMS.IsConnected(ControllerId),                                                          "The controller hanging up on the server it refused");

            Assert.Multiple(() => {
                Assert.That(before.State,                    Is.EqualTo(KeyPinState.Learned), Story);
                Assert.That(before.Learned?.ToHex(),         Is.EqualTo(own.ToHex()));
                Assert.That(before.Sent,                     Is.Null, "a pin set was sent without a signing key");
                Assert.That(run.Done,                        Is.True, String.Join(" ", run.Said));
                Assert.That(run.Said,                        Has.Some.Contains(own.ToHex()).And.Contains("will refuse the server"));
                Assert.That(Uplink?.ServerKeyPinLearned,     Is.Null);
                Assert.That(Uplink?.ServerKeyPinState,       Is.Not.EqualTo(KeyPinState.Confirmed), "a key pin that was not among the signed ones was confirmed");
            });

        }

        #endregion

    }

}
