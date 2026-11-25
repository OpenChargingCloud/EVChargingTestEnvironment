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

using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

using org.GraphDefined.Vanaheimr.Hermod.PKI;

using DotNetX509 = System.Security.Cryptography.X509Certificates;

#endregion

namespace cloud.charging.open.TestEnvironment.PKI
{

    /// <summary>
    /// The certificates the OCPP side of this environment authenticates with:
    /// a root of its own, a CA for the servers and a CA for the clients.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A second root, and deliberately so.</b> The V2G hierarchy below the
    /// cable and this one above it are two trust domains that happen to meet in
    /// one building. A charging station that would accept a CSMS because some
    /// V2G root vouched for it has been told something nobody meant to say, and
    /// the mistake is invisible until somebody with a contract certificate
    /// turns out to be able to impersonate a management system. Two roots make
    /// that impossible rather than unlikely.
    /// </para>
    /// <para>
    /// <b>Why the leaves are not minted here.</b> Every one of the three OCPP
    /// programs keeps its own key store, generates its own key and hands out a
    /// signing request; the private key never leaves the machine that made it,
    /// which is the point of the arrangement and the reason those stores exist.
    /// So this class is a certificate authority and not a key factory: it signs
    /// what it is given, and the only private keys it holds are its own.
    /// </para>
    /// <para>
    /// Built with Hermod's own <see cref="PKIFactory"/> - the code that made
    /// the signing requests in the first place, which is the shortest way to be
    /// sure that what is signed is what was asked for. Its subject names are a
    /// common name and nothing else: PKIFactory writes the "CN=" itself.
    /// </para>
    /// <para>
    /// <b>What Windows keeps of it.</b> Where a node serves or presents a
    /// certificate of this hierarchy, .NET's TLS on Windows checks whether the
    /// operating system can build its chain, and - this root being trusted
    /// nowhere - adds the CAs of the chain to "Intermediate Certification
    /// Authorities", of the local machine where it may write there, else of the
    /// current user. Nothing takes them out again: every start left another
    /// "OCPP Test Server CA". <see cref="RemoveFromIntermediateStores"/> does,
    /// once the environment stopped.
    /// </para>
    /// </remarks>
    public sealed class OCPPHierarchy
    {

        #region Data

        /// <summary>
        /// The curve everything here uses.
        /// </summary>
        /// <remarks>
        /// secp256r1 rather than something larger, because this material is
        /// carried over ordinary TLS by .NET's own stack on three operating
        /// systems, and P-256 is the one curve all three do well. The V2G side
        /// is where the curve is a matter of conformance; here it is a matter
        /// of what works everywhere.
        /// </remarks>
        public const String  Curve  = "secp256r1";

        private static readonly TimeSpan  rootLifetime    = TimeSpan.FromDays(365 * 10);
        private static readonly TimeSpan  subCALifetime   = TimeSpan.FromDays(365 *  5);
        private static readonly TimeSpan  leafLifetime    = TimeSpan.FromDays(365 *  2);

        #endregion

        #region Properties

        /// <summary>The root everything here chains up to.</summary>
        public AsymmetricCipherKeyPair  RootKeyPair       { get; }

        /// <summary>The root certificate - what goes into a trust store.</summary>
        public X509Certificate          RootCertificate   { get; }

        /// <summary>The CA that signs what a CSMS and a local controller listen with.</summary>
        public AsymmetricCipherKeyPair  ServerCAKeyPair   { get; }

        /// <summary>The server CA's certificate.</summary>
        public X509Certificate          ServerCA          { get; }

        /// <summary>The CA that signs what a charging station and a local controller connect with.</summary>
        public AsymmetricCipherKeyPair  ClientCAKeyPair   { get; }

        /// <summary>The client CA's certificate.</summary>
        public X509Certificate          ClientCA          { get; }


        /// <summary>
        /// The root alone, as PEM: what every trust store in this environment
        /// is given, and the only certificate that travels by hand.
        /// </summary>
        public String RootTrustPEM
            => RootCertificate.ToPEM();

        #endregion

        #region Constructor(s)

        private OCPPHierarchy(AsymmetricCipherKeyPair  RootKeyPair,
                              X509Certificate          RootCertificate,
                              AsymmetricCipherKeyPair  ServerCAKeyPair,
                              X509Certificate          ServerCA,
                              AsymmetricCipherKeyPair  ClientCAKeyPair,
                              X509Certificate          ClientCA)
        {

            this.RootKeyPair      = RootKeyPair;
            this.RootCertificate  = RootCertificate;
            this.ServerCAKeyPair  = ServerCAKeyPair;
            this.ServerCA         = ServerCA;
            this.ClientCAKeyPair  = ClientCAKeyPair;
            this.ClientCA         = ClientCA;

        }

        #endregion


        #region (static) Build(CommonNameSuffix = null)

        /// <summary>
        /// Three key pairs and three certificates, made here and now.
        /// </summary>
        /// <param name="CommonNameSuffix">Appended to every common name, so that two environments on one machine can be told apart.</param>
        public static OCPPHierarchy Build(String? CommonNameSuffix = null)
        {

            var suffix           = CommonNameSuffix is null ? "" : $" {CommonNameSuffix}";

            var rootKeyPair      = PKIFactory.GenerateECCKeyPair(Curve);
            var rootCertificate  = PKIFactory.CreateRootCACertificate(
                                       $"OCPP Test Root CA{suffix}",
                                       rootKeyPair,
                                       rootLifetime
                                   );

            var serverCAKeyPair  = PKIFactory.GenerateECCKeyPair(Curve);
            var serverCA         = PKIFactory.CreateIntermediateCA(
                                       $"OCPP Test Server CA{suffix}",
                                       serverCAKeyPair.Public,
                                       rootKeyPair.Private,
                                       rootCertificate,
                                       subCALifetime,
                                       PathLenConstraint: 0
                                   );

            var clientCAKeyPair  = PKIFactory.GenerateECCKeyPair(Curve);
            var clientCA         = PKIFactory.CreateIntermediateCA(
                                       $"OCPP Test Client CA{suffix}",
                                       clientCAKeyPair.Public,
                                       rootKeyPair.Private,
                                       rootCertificate,
                                       subCALifetime,
                                       PathLenConstraint: 0
                                   );

            return new OCPPHierarchy(
                       rootKeyPair,
                       rootCertificate,
                       serverCAKeyPair,
                       serverCA,
                       clientCAKeyPair,
                       clientCA
                   );

        }

        #endregion

        #region (static) TryLoad(Directory, out Hierarchy)

        /// <summary>
        /// Read a hierarchy back that an earlier start wrote, or answer false
        /// where there is none.
        /// </summary>
        /// <remarks>
        /// Reloaded rather than rebuilt, because the three programs keep the
        /// certificates that were signed for them between starts: a root made
        /// afresh every morning would invalidate every one of them, and a
        /// charging station would be turned away by a CSMS it was talking to
        /// yesterday with nothing anywhere saying why.
        /// </remarks>
        public static Boolean TryLoad(String Directory, out OCPPHierarchy? Hierarchy)
        {

            Hierarchy = null;

            try
            {

                var rootKeyPair      = ReadKeyPair    (Path.Combine(Directory, "01_root_ca",   "root_ca.key.pem"));
                var rootCertificate  = ReadCertificate(Path.Combine(Directory, "01_root_ca",   "root_ca.cert.pem"));

                var serverCAKeyPair  = ReadKeyPair    (Path.Combine(Directory, "02_server_ca", "server_ca.key.pem"));
                var serverCA         = ReadCertificate(Path.Combine(Directory, "02_server_ca", "server_ca.cert.pem"));

                var clientCAKeyPair  = ReadKeyPair    (Path.Combine(Directory, "03_client_ca", "client_ca.key.pem"));
                var clientCA         = ReadCertificate(Path.Combine(Directory, "03_client_ca", "client_ca.cert.pem"));

                if (rootKeyPair     is null || rootCertificate is null ||
                    serverCAKeyPair is null || serverCA        is null ||
                    clientCAKeyPair is null || clientCA        is null)
                {
                    return false;
                }

                // A root whose time is up is worse than no root: everything it
                // signs is refused, and the refusal names the leaf.
                rootCertificate.CheckValidity();

                Hierarchy = new OCPPHierarchy(
                                rootKeyPair,
                                rootCertificate,
                                serverCAKeyPair,
                                serverCA,
                                clientCAKeyPair,
                                clientCA
                            );

                return true;

            }
            catch
            {
                // Anything at all - a missing file, half a file, a root that
                // expired - means this hierarchy has to be made again. The
                // caller says so on the console; there is nothing here worth
                // distinguishing.
                return false;
            }

        }

        #endregion

        #region Write(Directory)

        /// <summary>
        /// Write the three CAs where <see cref="TryLoad"/> will find them, and
        /// the root on its own where a trust store can be pointed at it.
        /// </summary>
        public void Write(String Directory)
        {

            Save("01_root_ca",   "root_ca",   RootKeyPair,     RootCertificate);
            Save("02_server_ca", "server_ca", ServerCAKeyPair, ServerCA);
            Save("03_client_ca", "client_ca", ClientCAKeyPair, ClientCA);

            File.WriteAllText(
                Path.Combine(Directory, "ocpp_root_trust.pem"),
                RootTrustPEM
            );

            void Save(String SubDirectory, String Name, AsymmetricCipherKeyPair KeyPair, X509Certificate Certificate)
            {

                var directory = Path.Combine(Directory, SubDirectory);

                System.IO.Directory.CreateDirectory(directory);

                File.WriteAllText(
                    Path.Combine(directory, $"{Name}.cert.pem"),
                    Certificate.ToPEM()
                );

                OwnerOnlyFile.Write(
                    Path.Combine(directory, $"{Name}.key.pem"),
                    PemEncoding.WriteString(
                        "PRIVATE KEY",
                        PrivateKeyInfoFactory.CreatePrivateKeyInfo(KeyPair.Private).GetDerEncoded()
                    ) + Environment.NewLine
                );

            }

        }

        #endregion


        #region RemoveFromIntermediateStores()

        /// <summary>
        /// Take the root and the two CAs of this hierarchy out of the
        /// intermediate CA stores of the local machine and of the current user,
        /// wherever Windows' TLS put them, by thumbprint. Does nothing on other
        /// operating systems than Windows.
        /// </summary>
        /// <remarks>
        /// The local machine's store first: the current user's lists the
        /// machine's certificates as well, but cannot take them out. A store
        /// that cannot be opened or a certificate that cannot be removed is
        /// said, not thrown: what is left behind costs time, not correctness.
        /// </remarks>
        /// <returns>How many certificates were removed, and what went wrong.</returns>
        public (Int32 Removed, IReadOnlyList<String> Problems) RemoveFromIntermediateStores()
        {

            if (!OperatingSystem.IsWindows())
                return (0, []);

            var thumbprints  = new[] { RootCertificate, ServerCA, ClientCA }.
                                   Select(certificate => Convert.ToHexString(SHA1.HashData(certificate.GetEncoded()))).
                                   ToArray();

            var problems     = new List<String>();

            var removed      = RemoveFrom(DotNetX509.StoreLocation.LocalMachine, thumbprints, problems) +
                               RemoveFrom(DotNetX509.StoreLocation.CurrentUser,  thumbprints, problems);

            return (removed, problems);

        }

        private static Int32 RemoveFrom(DotNetX509.StoreLocation  Location,
                                        String[]                  Thumbprints,
                                        List<String>              Problems)
        {

            using var store = new DotNetX509.X509Store(DotNetX509.StoreName.CertificateAuthority, Location);

            try
            {
                store.Open(DotNetX509.OpenFlags.ReadWrite | DotNetX509.OpenFlags.OpenExistingOnly);
            }
            catch (CryptographicException e)
            {

                // The machine's store, unelevated, is the usual case and needs
                // no word: nothing can have been added there either.
                if (Location != DotNetX509.StoreLocation.LocalMachine)
                    Problems.Add($@"{Location}\CA could not be opened: {e.Message}");

                return 0;

            }

            var removed = 0;

            foreach (var thumbprint in Thumbprints)
                foreach (var installed in store.Certificates.Find(DotNetX509.X509FindType.FindByThumbprint, thumbprint, validOnly: false))
                {
                    try
                    {
                        store.Remove(installed);
                        removed++;
                    }
                    catch (CryptographicException e)
                    {
                        Problems.Add($@"{installed.Subject} could not be taken out of {Location}\CA: {e.Message}");
                    }
                    finally
                    {
                        installed.Dispose();
                    }
                }

            return removed;

        }

        #endregion


        #region SignServer(CSRPEM)

        /// <summary>
        /// Sign a server's signing request: the chain it may present, leaf
        /// first, as PEM.
        /// </summary>
        /// <remarks>
        /// The server CA is in the answer and the root is not. A store that was
        /// handed the root along with the leaf would serve it in the handshake,
        /// which is bytes on the wire that persuade nobody: the other side
        /// either has that root already or should not be believing it.
        /// </remarks>
        public String SignServer(String CSRPEM)

            => Pkcs12Bundle.ToPEM(
                   PKIFactory.SignServerCertificate(
                       ReadCSR(CSRPEM),
                       ServerCAKeyPair.Private,
                       ServerCA,
                       leafLifetime
                   ),
                   ServerCA
               );

        #endregion

        #region SignClient(CSRPEM)

        /// <summary>
        /// Sign a client's signing request - what a charging station or a local
        /// controller connects with under OCPP security profile 3.
        /// </summary>
        public String SignClient(String CSRPEM)

            => Pkcs12Bundle.ToPEM(
                   PKIFactory.SignClientCertificate(
                       ReadCSR(CSRPEM),
                       ClientCAKeyPair.Private,
                       ClientCA,
                       leafLifetime
                   ),
                   ClientCA
               );

        #endregion


        #region (private static) ReadCSR(PEM) / ReadKeyPair(Path) / ReadCertificate(Path)

        private static Pkcs10CertificationRequest ReadCSR(String PEM)
        {

            using var reader = new StringReader(PEM);

            return new PemReader(reader).ReadObject() as Pkcs10CertificationRequest
                       ?? throw new ArgumentException("This is not a PKCS#10 certificate signing request.", nameof(PEM));

        }

        private static AsymmetricCipherKeyPair? ReadKeyPair(String Path)
        {

            if (!File.Exists(Path))
                return null;

            // PKCS#8 comes back as a private key rather than as a pair, which
            // is what was written: the public half is derived rather than
            // stored, and BouncyCastle is content to do that for EC keys.
            using var reader = new PemReader(File.OpenText(Path));

            return reader.ReadObject() switch {
                       AsymmetricCipherKeyPair pair  => pair,
                       AsymmetricKeyParameter  key   => new AsymmetricCipherKeyPair(PKIFactory.PublicKeyOf(key), key),
                       _                             => null
                   };

        }

        private static X509Certificate? ReadCertificate(String Path)
        {

            if (!File.Exists(Path))
                return null;

            using var reader = new PemReader(File.OpenText(Path));

            return reader.ReadObject() as X509Certificate;

        }

        #endregion

    }

}
