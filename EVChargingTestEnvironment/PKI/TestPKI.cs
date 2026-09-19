/*
 * Copyright (c) 2014-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of the EV Charging Test Environment
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

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;

using cloud.charging.open.protocols.ISO15118.PKI;
using cloud.charging.open.protocols.ISO15118.PKI.Evil;

#endregion

namespace cloud.charging.open.TestEnvironment.PKI
{

    /// <summary>
    /// Every certificate this environment runs on, built before anything is
    /// started and handed out from one place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the thing the four single-component repositories cannot do for
    /// themselves, and the reason this one exists. A vehicle started on its own
    /// has to be given a contract certificate by somebody; a station has to be
    /// given something to listen with; a CSMS has to be told whose client
    /// certificates it may believe. Separately that is four manual steps, and
    /// each of them is a place to put the wrong file. Here it is one function
    /// that runs before the first port is opened.
    /// </para>
    /// <para>
    /// <b>Three hierarchies, and why.</b>
    /// </para>
    /// <list type="bullet">
    ///   <item>
    ///     <b>V2G, ISO 15118-2 profile, ECDSA P-256.</b> What the vehicle and
    ///     the station in this environment actually use. P-256 because the
    ///     station's endpoint is .NET's own TLS stack, and .NET on Windows and
    ///     macOS will not carry the -20 curve at all - a P-521 station
    ///     certificate there is a station nothing can connect to.
    ///   </item>
    ///   <item>
    ///     <b>V2G, ISO 15118-20 profile, ECDSA P-521.</b> The -20-faithful
    ///     material: the OEM provisioning certificate, whose key the -20
    ///     contract-installation exchange agrees must be secp521r1, and the
    ///     loopback directory the BouncyCastle backend reads when this
    ///     environment's vehicle is pointed at the reference SECC instead.
    ///   </item>
    ///   <item>
    ///     <b>OCPP.</b> A root of its own above the cable - see
    ///     <see cref="OCPPHierarchy"/> for why it is not the V2G one.
    ///   </item>
    /// </list>
    /// <para>
    /// <b>Built once and kept.</b> The OCPP certificates are signed against
    /// keys the three programs generated and kept, so a root made afresh at
    /// every start would silently invalidate every one of them. The whole
    /// directory is therefore reused where it is intact and rebuilt where it is
    /// not; <c>--fresh-pki</c> rebuilds it on purpose.
    /// </para>
    /// </remarks>
    public sealed class TestPKI
    {

        #region Data

        /// <summary>
        /// Where the certificates live below the repository root, unless
        /// another place is named.
        /// </summary>
        public const String  DefaultDirectoryName  = "pki";

        /// <summary>
        /// What this file says about itself, so that a directory written by an
        /// older version is rebuilt rather than half-read.
        /// </summary>
        public const Int32   Layout                = 1;

        /// <summary>
        /// The manifest, which also carries the one password every PKCS#12 file
        /// here is protected with.
        /// </summary>
        /// <remarks>
        /// One password for the lot, written to a file only its owner may read
        /// rather than made up per certificate and kept nowhere. The
        /// alternative on a test bench is what it always is: no password at
        /// all, and private keys lying about in files anything can open.
        /// </remarks>
        public const String  ManifestFileName      = "pki.json";

        #endregion

        #region Properties

        /// <summary>Where all of this lives.</summary>
        public String          Directory              { get; }

        /// <summary>What opens every PKCS#12 file in it.</summary>
        public String          Password               { get; }

        /// <summary>The OCPP root and its two CAs - kept in object form, because signing requests arrive at every start.</summary>
        public OCPPHierarchy   OCPP                   { get; }

        /// <summary>Whether this run built the hierarchies rather than finding them.</summary>
        public Boolean         WasBuilt               { get; }


        /// <summary>The V2G root(s) a station's certificate must chain to, in one PEM file - for anything that reads a trust file.</summary>
        public String          V2GRootTrustFile       => Path.Combine(Directory, "v2g", "v2g_root_trust.pem");

        /// <summary>
        /// The two V2G roots one by one, as the vehicle's certificate store
        /// takes them: one certificate per file, each a trust anchor of its
        /// own.
        /// </summary>
        public IEnumerable<String> V2GRootCertificateFiles
            => System.IO.Directory.Exists(Path.Combine(Directory, "v2g"))
                   ? System.IO.Directory.GetDirectories(Path.Combine(Directory, "v2g"), "strict_*")
                                        .Select  (profile => Path.Combine(profile, "01_v2g_root_ca"))
                                        .Where   (System.IO.Directory.Exists)
                                        .SelectMany(rootDirectory => System.IO.Directory.GetFiles(rootDirectory, "*.cert.pem"))
                                        .OrderBy (file => file)
                   : [];

        /// <summary>The OCPP root, for the trust store of everything that accepts a client certificate.</summary>
        public String          OCPPRootTrustFile      => Path.Combine(Directory, "ocpp", "ocpp_root_trust.pem");

        /// <summary>What the station's V2G endpoint listens with: the SECC leaf and the CPO Sub-CAs.</summary>
        public String          SECCBundleFile         => Bundle("secc");

        /// <summary>Who the vehicle is in the TLS handshake.</summary>
        public String          VehicleBundleFile      => Bundle("vehicle");

        /// <summary>Who pays: the Plug &amp; Charge contract certificate.</summary>
        public String          ContractBundleFile     => Bundle("contract");

        /// <summary>What the vehicle was born with, on the curve ISO 15118-20 provisioning agrees on.</summary>
        public String          OEMBundleFile          => Bundle("oem");

        /// <summary>The public key a signed tariff is checked against; the signing half is not in it.</summary>
        public String          TariffBundleFile       => Bundle("tariff");

        /// <summary>
        /// The ISO 15118-20 loopback directory: <c>vehicle.0.der</c> and its
        /// two Sub-CAs, <c>vehicle.key</c>, and <c>secc.leaf.der</c> to pin.
        /// </summary>
        /// <remarks>
        /// The shape the reference SECC writes and the reference EVCC reads, so
        /// that a vehicle from this environment can be pointed at either.
        /// </remarks>
        public String          ISO20DevDirectory      => Path.Combine(Directory, "v2g", "dev");

        /// <summary>The malformed certificates, where they were asked for.</summary>
        public String          EvilDirectory          => Path.Combine(Directory, "v2g", "evil");

        /// <summary>Whether the malformed certificates are there.</summary>
        public Boolean         HasEvilCertificates    => System.IO.Directory.Exists(EvilDirectory);

        #endregion

        #region Constructor(s)

        private TestPKI(String         Directory,
                        String         Password,
                        OCPPHierarchy  OCPP,
                        Boolean        WasBuilt)
        {

            this.Directory  = Directory;
            this.Password   = Password;
            this.OCPP       = OCPP;
            this.WasBuilt   = WasBuilt;

        }

        #endregion


        #region (static) Open(Directory, Fresh, Evil, CommonNameSuffix, Say)

        /// <summary>
        /// The certificates of this environment: read back where an earlier
        /// start left them intact, and built where it did not.
        /// </summary>
        /// <param name="Directory">Where they live.</param>
        /// <param name="Fresh">Build them again even where they are intact.</param>
        /// <param name="Evil">Also write the deliberately malformed certificates, for pointing at either side on purpose.</param>
        /// <param name="CommonNameSuffix">Appended to every common name, so two environments on one machine can be told apart.</param>
        /// <param name="Say">Where the two or three sentences this has to say go.</param>
        public static TestPKI Open(String          Directory,
                                   Boolean         Fresh              = false,
                                   Boolean         Evil               = false,
                                   String?         CommonNameSuffix   = null,
                                   Action<String>? Say                = null)
        {

            var directory  = Path.GetFullPath(Directory);
            var say        = Say ?? (_ => { });

            #region What is already there

            if (!Fresh &&
                TryReadManifest(directory, out var password, out var complete) &&
                complete &&
                OCPPHierarchy.TryLoad(Path.Combine(directory, "ocpp"), out var loaded) &&
                loaded is not null)
            {
                say($"The certificates in '{directory}' are reused; --fresh-pki builds them again.");
                return new TestPKI(directory, password!, loaded, WasBuilt: false);
            }

            #endregion

            #region Otherwise: all of it, from nothing

            if (System.IO.Directory.Exists(directory))
            {
                // Rather than writing a new hierarchy into an old one, where
                // the two would be indistinguishable afterwards and half the
                // chains would not verify.
                say($"Building a new PKI in '{directory}' - what was there is moved aside.");
                MoveAside(directory);
            }

            System.IO.Directory.CreateDirectory(directory);

            var newPassword = RandomPassword();
            var random      = new SecureRandom();
            var suffix      = CommonNameSuffix ?? "(Test Environment)";

            // A suffix per hierarchy, and not for tidiness: the common names
            // come from the role, so two hierarchies built with one suffix
            // produce two roots with identical Distinguished Names and
            // different keys - which is exactly the evil_twin_root the PKI
            // builder writes on purpose to catch validators that pin by name.
            // Building one by accident, in the trust file both of them are in,
            // would make every run a test of something nobody meant to test.
            var suffix2     = $"{suffix} -2";
            var suffix20    = $"{suffix} -20";

            #region The two V2G hierarchies

            var iso2   = V2GHierarchy.Build(
                             V2GAlgorithm.EcdsaP256,
                             random,
                             CommonNameSuffix:   suffix2,
                             V2GProfileOptions:  new V2GProfileOptions(
                                                     V2GProfileFlavor.Strict15118_2,
                                                     V2GAlgorithm.EcdsaP256,
                                                     V2GPolicySet.None
                                                 )
                         );

            var iso20  = V2GHierarchy.Build(
                             V2GAlgorithm.EcdsaP521,
                             random,
                             CommonNameSuffix:   suffix20,
                             V2GProfileOptions:  new V2GProfileOptions(
                                                     V2GProfileFlavor.Strict15118_20,
                                                     V2GAlgorithm.EcdsaP521,
                                                     V2GPolicySet.None
                                                 )
                         );

            var v2gDirectory = Path.Combine(directory, "v2g");

            V2GIO.WriteHierarchy(iso2,  v2gDirectory);
            V2GIO.WriteHierarchy(iso20, v2gDirectory);

            #endregion

            #region The trust file: both roots, because both are used here

            // Both, and in one file. The vehicle takes every certificate in a
            // PEM file as a root, and this environment really does present
            // material from both hierarchies - the -2 one on the wire between
            // its own two ends, the -20 one when its vehicle is pointed at the
            // reference station instead. A trust file with one root in it
            // would make one of those two runs fail for a reason that has
            // nothing to do with what is being tested.
            File.WriteAllText(
                Path.Combine(v2gDirectory, "v2g_root_trust.pem"),
                Pkcs12Bundle.ToPEM(
                    iso2. Root.Certificate,
                    iso20.Root.Certificate
                )
            );

            #endregion

            #region The bundles the programs actually open

            var bundles = Path.Combine(v2gDirectory, "bundles");

            System.IO.Directory.CreateDirectory(bundles);

            // The station. P-256, because this one is served by .NET's TLS.
            Pkcs12Bundle.Write(Path.Combine(bundles, "secc.pfx"),     iso2.SeccLeaf,     newPassword, iso2.CpoSubCa2,     iso2.CpoSubCa1);

            // The vehicle, and who pays. The same hierarchy as the station, so
            // that the chain either side presents really does chain.
            Pkcs12Bundle.Write(Path.Combine(bundles, "vehicle.pfx"),  iso2.VehicleLeaf,  newPassword, iso2.VehicleSubCa2, iso2.VehicleSubCa1);
            Pkcs12Bundle.Write(Path.Combine(bundles, "contract.pfx"), iso2.ContractLeaf, newPassword, iso2.MoSubCa2,      iso2.MoSubCa1);

            // What the vehicle was born with - out of the -20 hierarchy, and
            // only out of that one: the contract-installation exchange unwraps
            // the issued key with an ECDH against the station's ephemeral
            // secp521r1 key, so a P-256 provisioning certificate takes part in
            // an exchange it cannot finish.
            Pkcs12Bundle.Write(Path.Combine(bundles, "oem.pfx"),      iso20.OemProvLeaf, newPassword, iso20.OemSubCa2,    iso20.OemSubCa1);

            // The tariff key, public half only.
            var tariffSigner = V2GCertificateBuilder.Issue(
                                   V2GCertProfile.ForRole(
                                       V2GRole.MOSigningLeaf,
                                       new V2GProfileOptions(
                                           V2GProfileFlavor.Strict15118_2,
                                           V2GAlgorithm.EcdsaP256,
                                           V2GPolicySet.None
                                       ),
                                       suffix2
                                   ),
                                   V2GAlgorithm.EcdsaP256,
                                   random,
                                   iso2.MoSubCa2
                               );

            V2GIO.WriteIssued(tariffSigner, Path.Combine(v2gDirectory, "mo_signer"));

            Pkcs12Bundle.WritePublicOnly(Path.Combine(bundles, "tariff.pfx"), tariffSigner, newPassword);

            #endregion

            #region The ISO 15118-20 loopback directory

            var dev = Path.Combine(v2gDirectory, "dev");

            System.IO.Directory.CreateDirectory(dev);

            File.WriteAllBytes         (Path.Combine(dev, "vehicle.0.der"), iso20.VehicleLeaf.   Certificate.GetEncoded());
            File.WriteAllBytes         (Path.Combine(dev, "vehicle.1.der"), iso20.VehicleSubCa2. Certificate.GetEncoded());
            File.WriteAllBytes         (Path.Combine(dev, "vehicle.2.der"), iso20.VehicleSubCa1. Certificate.GetEncoded());
            OwnerOnlyFile.WriteBytes   (Path.Combine(dev, "vehicle.key"),   PrivateKeyInfoFactory.CreatePrivateKeyInfo(iso20.VehicleLeaf.KeyPair.Private).GetEncoded());
            File.WriteAllBytes         (Path.Combine(dev, "secc.leaf.der"), iso20.SeccLeaf.      Certificate.GetEncoded());

            #endregion

            #region The malformed ones, where they were asked for

            if (Evil)
            {

                var evil = Path.Combine(v2gDirectory, "evil");

                System.IO.Directory.CreateDirectory(evil);

                V2GIO.WriteEvilVariants(V2GEvilFactory.Build(iso2,  random), V2GAlgorithm.EcdsaP256, evil);
                V2GIO.WriteEvilVariants(V2GEvilFactory.Build(iso20, random), V2GAlgorithm.EcdsaP521, evil);

            }

            #endregion

            #region The OCPP root and its two CAs

            var ocppDirectory = Path.Combine(directory, "ocpp");

            System.IO.Directory.CreateDirectory(ocppDirectory);

            var ocpp = OCPPHierarchy.Build(CommonNameSuffix);

            ocpp.Write(ocppDirectory);

            #endregion

            WriteManifest(directory, newPassword);

            say($"A new PKI was built in '{directory}': ISO 15118-2 (P-256), ISO 15118-20 (P-521) and OCPP." +
                (Evil ? " The malformed certificates are in 'v2g/evil'." : ""));

            return new TestPKI(directory, newPassword, ocpp, WasBuilt: true);

            #endregion

        }

        #endregion


        #region SECCCertificate()

        /// <summary>
        /// What the station's V2G endpoint listens with, as the one shape a
        /// .NET TLS server takes: the leaf carrying its private key, with the
        /// CPO Sub-CAs beside it.
        /// </summary>
        /// <remarks>
        /// The key set differs on Windows for the reason
        /// <see cref="Pkcs12Bundle.AsCertificate"/> gives: SChannel will not
        /// serve an ephemeral key, and a station holding one fails every
        /// handshake from inside it.
        /// </remarks>
        public (X509Certificate2 Leaf, X509Certificate2Collection Chain) SECCCertificate()
        {

            var all   = X509CertificateLoader.LoadPkcs12CollectionFromFile(
                            SECCBundleFile,
                            Password,
                            OperatingSystem.IsWindows()
                                ? X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet
                                : X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet
                        );

            // The one with the private key is the leaf; the rest are the CPO
            // Sub-CAs, and they are what the endpoint sends along with it. A
            // station that sends only the leaf is refused by every vehicle
            // that checks the chain, in words that blame the vehicle.
            var leaf  = all.FirstOrDefault(one => one.HasPrivateKey)
                            ?? throw new InvalidOperationException(
                                   $"'{SECCBundleFile}' has no certificate with a private key in it, so the station has " +
                                    "nothing to listen with. Build the PKI again with --fresh-pki."
                               );

            return (
                leaf,
                new X509Certificate2Collection(all.Where(one => !ReferenceEquals(one, leaf)).ToArray())
            );

        }

        #endregion

        #region Describe()

        /// <summary>
        /// The four lines somebody starting this wants: what was built, where,
        /// and what opens it.
        /// </summary>
        public IEnumerable<String> Describe()
        {

            yield return $"directory      {Directory}";
            yield return $"V2G roots      {V2GRootTrustFile}";
            yield return $"OCPP root      {OCPPRootTrustFile}";
            yield return $"passwords      {Path.Combine(Directory, ManifestFileName)} (the one password every .pfx here has)";

        }

        #endregion


        #region (private) Bundle(Name)

        private String Bundle(String Name)
            => Path.Combine(Directory, "v2g", "bundles", $"{Name}.pfx");

        #endregion

        #region (private static) The manifest

        private static void WriteManifest(String Directory, String Password)

            => OwnerOnlyFile.Write(
                   Path.Combine(Directory, ManifestFileName),
                   new JObject(
                       new JProperty("layout",     Layout),
                       new JProperty("createdAt",  DateTimeOffset.UtcNow.ToString("o")),
                       new JProperty("v2g",        new JArray("strict_15118_2_ecdsa_p256", "strict_15118_20_ecdsa_p521")),
                       new JProperty("ocpp",       "ecdsa_p256"),

                       // In the manifest rather than beside it, because there
                       // is exactly one thing here to keep from being read by
                       // somebody else and one file to get the mode right on.
                       new JProperty("password",   Password)
                   ).ToString(Formatting.Indented) + Environment.NewLine
               );

        private static Boolean TryReadManifest(String       Directory,
                                               out String?  Password,
                                               out Boolean  Complete)
        {

            Password  = null;
            Complete  = false;

            var path = Path.Combine(Directory, ManifestFileName);

            if (!File.Exists(path))
                return false;

            try
            {

                var manifest = JObject.Parse(File.ReadAllText(path));

                if (manifest.Value<Int32>("layout") != Layout)
                    return false;

                Password = manifest.Value<String>("password");

                if (Password is null)
                    return false;

                // A manifest is not a promise that the files are there: a
                // half-copied directory has one and nothing else, and the
                // failure it would otherwise produce is a TLS handshake that
                // says nothing about a missing file.
                Complete = new[] {
                               Path.Combine(Directory, "v2g", "v2g_root_trust.pem"),
                               Path.Combine(Directory, "v2g", "bundles", "secc.pfx"),
                               Path.Combine(Directory, "v2g", "bundles", "vehicle.pfx"),
                               Path.Combine(Directory, "v2g", "bundles", "contract.pfx"),
                               Path.Combine(Directory, "v2g", "bundles", "oem.pfx"),
                               Path.Combine(Directory, "v2g", "bundles", "tariff.pfx"),
                               Path.Combine(Directory, "v2g", "dev", "secc.leaf.der"),
                               Path.Combine(Directory, "ocpp", "ocpp_root_trust.pem")
                           }.All(File.Exists);

                return true;

            }
            catch
            {
                return false;
            }

        }

        #endregion

        #region (private static) MoveAside(Directory) / RandomPassword()

        /// <summary>
        /// Rename what is there out of the way, so that a rebuild never mixes
        /// two hierarchies in one directory - and so that the keys of the old
        /// one are still there if somebody wanted them.
        /// </summary>
        private static void MoveAside(String Directory)
        {

            var moved = $"{Directory.TrimEnd(Path.DirectorySeparatorChar)}.{DateTime.Now:yyyyMMdd-HHmmss}";

            try
            {
                System.IO.Directory.Move(Directory, moved);
            }
            catch (Exception e)
            {
                throw new InvalidOperationException(
                          $"The old PKI in '{Directory}' could not be moved to '{moved}': {e.Message} " +
                           "Move or remove it by hand and start again.",
                          e
                      );
            }

        }

        private static String RandomPassword()

            // URL-safe base64 of 24 random bytes: no quoting to get wrong when
            // it is pasted into a shell, and nothing in it a command line has
            // an opinion about.
            => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).
                       Replace('+', '-').
                       Replace('/', '_').
                       TrimEnd('=');

        #endregion

    }

}
