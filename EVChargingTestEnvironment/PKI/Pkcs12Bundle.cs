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

using System.Security.Cryptography.X509Certificates;

using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;

using cloud.charging.open.protocols.ISO15118.PKI;

// Both namespaces have an X509Certificate, and this file is one of the few
// places where both kinds are in the room at once - the whole job here is
// getting from one to the other.
using BCx509 = Org.BouncyCastle.X509;

#endregion

namespace cloud.charging.open.TestEnvironment.PKI
{

    /// <summary>
    /// A leaf, its private key and the Sub-CAs above it, in the one shape the
    /// programs that open certificates here can read: PKCS#12.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The PKI builder writes PEM and DER, one file per certificate, which is
    /// the right form for reading a hierarchy and the wrong one for handing a
    /// chain to a TLS stack. Both the vehicle and the station open PKCS#12 -
    /// <c>X509CertificateLoader.LoadPkcs12CollectionFromFile</c> on one side,
    /// <see cref="X509Certificate2"/> on the other - so the bundles are made
    /// here, once, from the hierarchy that was just built.
    /// </para>
    /// <para>
    /// The root is deliberately <em>not</em> in the bundle. A chain that
    /// carries its own root invites the other side to trust it because it is
    /// there, and the whole point of the trust file beside it is that the root
    /// arrives by another route.
    /// </para>
    /// </remarks>
    public static class Pkcs12Bundle
    {

        #region Build(Leaf, Password, Chain)

        /// <summary>
        /// One PKCS#12 file: the leaf with its private key, and the Sub-CAs
        /// between it and the root.
        /// </summary>
        /// <param name="Leaf">The certificate with the private key.</param>
        /// <param name="Password">What opens it.</param>
        /// <param name="Chain">The Sub-CAs, ordered from the one that issued the leaf upwards. Without the root.</param>
        public static Byte[] Build(V2GIssued            Leaf,
                                   String               Password,
                                   params V2GIssued[]   Chain)
        {

            var store  = new Pkcs12StoreBuilder().Build();

            // The friendly name is what the leaf is called in the file, and it
            // is what a Windows certificate dialogue shows. The slug rather
            // than the common name, so that it is the same string as the
            // directory the same certificate was written to.
            var alias  = Leaf.Slug;

            var entries = new List<X509CertificateEntry> {
                              new (Leaf.Certificate)
                          };

            foreach (var issuer in Chain)
            {

                var entry = new X509CertificateEntry(issuer.Certificate);

                entries.Add(entry);

                // Beside the chain as well as in it: a Sub-CA that is only in
                // the key entry's chain is invisible to a reader that walks the
                // certificate bag, which is what several tools do.
                store.SetCertificateEntry(issuer.Slug, entry);

            }

            store.SetKeyEntry(
                alias,
                new AsymmetricKeyEntry(Leaf.KeyPair.Private),
                [.. entries]
            );

            using var memory = new MemoryStream();

            store.Save(memory, Password.ToCharArray(), new SecureRandom());

            return memory.ToArray();

        }

        #endregion

        #region Write(Path, Leaf, Password, Chain)

        /// <summary>
        /// The same, written to disk with the private key's file mode.
        /// </summary>
        public static void Write(String              Path,
                                 V2GIssued           Leaf,
                                 String              Password,
                                 params V2GIssued[]  Chain)
        {

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);

            OwnerOnlyFile.WriteBytes(
                Path,
                Build(Leaf, Password, Chain)
            );

        }

        #endregion

        #region WritePublicOnly(Path, Leaf, Password)

        /// <summary>
        /// A PKCS#12 with a certificate and no private key in it, for the one
        /// place a program is handed a <em>public</em> key in this shape: the
        /// key a signed tariff is checked against.
        /// </summary>
        /// <remarks>
        /// The vehicle reads that one with
        /// <c>LoadEcdsaKey(..., wantPrivate: false, ...)</c> and asks the leaf
        /// for its public key, so the private half has no business being in the
        /// file - and a test environment that shipped the signing key to the
        /// side doing the verifying would be checking its own signature.
        /// </remarks>
        public static void WritePublicOnly(String     Path,
                                           V2GIssued  Leaf,
                                           String     Password)
        {

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);

            var store = new Pkcs12StoreBuilder().Build();

            store.SetCertificateEntry(
                Leaf.Slug,
                new X509CertificateEntry(Leaf.Certificate)
            );

            using var memory = new MemoryStream();

            store.Save(memory, Password.ToCharArray(), new SecureRandom());

            File.WriteAllBytes(Path, memory.ToArray());

        }

        #endregion

        #region AsCertificate(Leaf, Password, Chain)

        /// <summary>
        /// The leaf as a <see cref="X509Certificate2"/> carrying its private
        /// key, which is what the station's TLS endpoint is handed.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The detour through PKCS#12 is not a detour: it is the only way to
        /// put a BouncyCastle key pair and a BouncyCastle certificate together
        /// into one .NET certificate object.
        /// </para>
        /// <para>
        /// <b>The key set is not the same on Windows, and it cannot be.</b>
        /// Everywhere else this is loaded with an ephemeral key set, which
        /// keeps the private key out of the machine's key store - where a test
        /// environment has no business leaving anything behind. On Windows a
        /// TLS server cannot use an ephemeral key at all: SChannel wants a key
        /// it can name, and a handshake with one fails with "the platform does
        /// not support ephemeral keys", from inside the handshake, where the
        /// station reports it as a connection that failed.
        /// </para>
        /// <para>
        /// So on Windows the key is written to the user's key store. It is a
        /// key this environment minted for a certificate only this environment
        /// trusts, and it stays there after the process ends; <c>certutil
        /// -user -store My</c> is where to look for it.
        /// </para>
        /// </remarks>
        public static X509Certificate2 AsCertificate(V2GIssued           Leaf,
                                                     String              Password,
                                                     params V2GIssued[]  Chain)

            => X509CertificateLoader.LoadPkcs12(
                   Build(Leaf, Password, Chain),
                   Password,
                   OperatingSystem.IsWindows()
                       ? X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet
                       : X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet
               );

        #endregion

        #region ToPEM(Certificates)

        /// <summary>
        /// One or more certificates as PEM, leaf first - what the OCPP
        /// certificate stores are given, and what a trust file holds.
        /// </summary>
        public static String ToPEM(params BCx509.X509Certificate[] Certificates)
        {

            var writer = new StringWriter();

            foreach (var certificate in Certificates)
            {

                var base64 = Convert.ToBase64String(certificate.GetEncoded());

                writer.WriteLine("-----BEGIN CERTIFICATE-----");

                for (var i = 0; i < base64.Length; i += 64)
                    writer.WriteLine(base64.Substring(i, Math.Min(64, base64.Length - i)));

                writer.WriteLine("-----END CERTIFICATE-----");

            }

            return writer.ToString();

        }

        #endregion

    }

}
