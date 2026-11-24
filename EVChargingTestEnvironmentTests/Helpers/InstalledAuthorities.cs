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

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using NUnit.Framework;

#endregion

namespace cloud.charging.open.TestEnvironment.Tests
{

    /// <summary>
    /// Takes a test's certificate authorities out of the intermediate CA
    /// stores again, where Windows' TLS has put them.
    /// </summary>
    /// <remarks>
    /// On Windows, SslStreamCertificateContext checks whether the operating
    /// system can build the chain of a certificate a node serves. When it
    /// cannot - and a test's CA is trusted nowhere - it adds the chain's
    /// authorities to "Intermediate Certification Authorities", of the local
    /// machine if it may write there, else of the current user, and the last
    /// one as well while the chain is still partial: a self-signed root
    /// included. Nothing ever takes them out again. Every test whose nodes
    /// spoke TLS left its "The CA of a basic test", under a name of its own:
    /// 629 of them between 2026-11-20 and 2026-11-24, and every load of that
    /// store costs every TLS handshake on the machine about 0.14 ms of CPU per
    /// certificate in it.
    /// </remarks>
    public static class InstalledAuthorities
    {

        #region Remove(Authorities)

        /// <summary>
        /// Remove the given certificates from the intermediate CA stores of the
        /// local machine and of the current user, wherever they are found, by
        /// thumbprint. Does nothing on other operating systems than Windows.
        /// </summary>
        /// <remarks>
        /// The local machine's store first: a process that may write to it, as
        /// the elevated one on a CI runner does, is where .NET puts them, and
        /// the current user's store lists the machine's certificates as well
        /// but cannot take them out - "Access is denied". A removal that is
        /// refused is said in the test's output and does not fail the test:
        /// what it leaves behind costs time, not correctness.
        /// </remarks>
        /// <param name="Authorities">The CA certificates. Nulls are skipped.</param>
        /// <returns>How many certificates were removed.</returns>
        public static Int32 Remove(params IEnumerable<X509Certificate2?> Authorities)
        {

            if (!OperatingSystem.IsWindows())
                return 0;

            var thumbprints = Authorities.
                                  OfType<X509Certificate2>().
                                  Select(authority => authority.Thumbprint).
                                  Distinct(StringComparer.OrdinalIgnoreCase).
                                  ToArray();

            if (thumbprints.Length == 0)
                return 0;

            return RemoveFrom(StoreLocation.LocalMachine, thumbprints) +
                   RemoveFrom(StoreLocation.CurrentUser,  thumbprints);

        }

        #endregion

        #region (private static) RemoveFrom(Location, Thumbprints)

        private static Int32 RemoveFrom(StoreLocation  Location,
                                        String[]       Thumbprints)
        {

            using var store = new X509Store(StoreName.CertificateAuthority, Location);

            try
            {
                store.Open(OpenFlags.ReadWrite | OpenFlags.OpenExistingOnly);
            }
            catch (CryptographicException e)
            {

                // The machine's store, unelevated, is the usual case and needs
                // no word: nothing can have been added there either.
                if (Location != StoreLocation.LocalMachine)
                    TestContext.Out.WriteLine($"Could not open {Location}\\CA to take test CAs out of it: {e.Message}");

                return 0;

            }

            var removed = 0;

            foreach (var thumbprint in Thumbprints)
                foreach (var installed in store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false))
                {
                    try
                    {
                        store.Remove(installed);
                        removed++;
                    }
                    catch (CryptographicException e)
                    {
                        TestContext.Out.WriteLine($"Could not take the test CA {thumbprint} out of {Location}\\CA: {e.Message}");
                    }
                    finally
                    {
                        installed.Dispose();
                    }
                }

            return removed;

        }

        #endregion

    }

}
