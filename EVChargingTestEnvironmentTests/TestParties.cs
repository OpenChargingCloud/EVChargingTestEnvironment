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

using System.Net;
using System.Net.Sockets;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod;

#endregion

namespace cloud.charging.open.TestEnvironment.Tests
{

    /// <summary>
    /// Building the two ends of a roaming agreement: a CSMS that is a charge
    /// point operator, and an EMSP.
    /// </summary>
    /// <remarks>
    /// Both are the real thing - the same classes their own repositories ship
    /// and their own tests exercise - built here because this is the only
    /// checkout that has both.
    /// </remarks>
    internal static class TestParties
    {

        #region NewCPO(Directory, CountryCode, PartyId, Name)

        /// <summary>
        /// A CSMS with an OCPI identity, built and not started.
        /// </summary>
        /// <remarks>
        /// The time client is switched off in the file, because it is read at
        /// construction and it is Start() that would otherwise put a timer on
        /// the network; the charging station server is switched off too,
        /// because nothing here is about OCPP and a second socket per test is
        /// a second thing to go wrong.
        /// </remarks>
        public static CSMS.CSMS NewCPO(String  Directory,
                                       String  CountryCode   = "DE",
                                       String  PartyId       = "GEF",
                                       String  Name          = "GraphDefined CPO")
        {

            System.IO.Directory.CreateDirectory(Directory);

            var configFile = Path.Combine(Directory, "configuration.json");

            File.WriteAllText(
                configFile,
                new JObject(
                    new JProperty("nts",         new JObject(
                        new JProperty("enabled",      false)
                    )),
                    new JProperty("ocppServer",  new JObject(
                        new JProperty("enabled",      false)
                    )),
                    new JProperty("ocpi",        new JObject(
                        new JProperty("countryCode",  CountryCode),
                        new JProperty("partyId",      PartyId),
                        new JProperty("name",         Name),
                        new JProperty("versions",     new JArray("2.2.1"))
                    ))
                ).ToString()
            );

            return new CSMS.CSMS(
                       HTTPPort:         IPPort.Parse(FreePort()),
                       AccountsPath:     Path.Combine(Directory, "accounts"),
                       ConfigFile:       new CSMS.Configuration.CSMSConfigFile(configFile),
                       LogToConsole:     false,
                       BridgeDebugLog:   false
                   );

        }

        #endregion

        #region NewEMSP(Directory, CountryCode, PartyId, Name)

        /// <summary>
        /// An EMSP with an OCPI identity, built and not started.
        /// </summary>
        public static EMSP.EMSP NewEMSP(String  Directory,
                                        String  CountryCode   = "DE",
                                        String  PartyId       = "GDF",
                                        String  Name          = "GraphDefined EMSP")
        {

            System.IO.Directory.CreateDirectory(Directory);

            var configFile = Path.Combine(Directory, "configuration.json");

            File.WriteAllText(
                configFile,
                new JObject(
                    new JProperty("nts",   new JObject(
                        new JProperty("enabled",      false)
                    )),
                    new JProperty("ocpi",  new JObject(
                        new JProperty("countryCode",  CountryCode),
                        new JProperty("partyId",      PartyId),
                        new JProperty("name",         Name),
                        new JProperty("versions",     new JArray("2.2.1"))
                    ))
                ).ToString()
            );

            return new EMSP.EMSP(
                       HTTPPort:         IPPort.Parse(FreePort()),
                       AccountsPath:     Path.Combine(Directory, "accounts"),
                       ConfigFile:       new EMSP.Configuration.EMSPConfigFile(configFile),
                       LogToConsole:     false,
                       BridgeDebugLog:   false
                   );

        }

        #endregion

        #region FreePort()

        /// <summary>
        /// A TCP port nobody was listening on a moment ago.
        /// </summary>
        public static UInt16 FreePort()
        {

            var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);

            listener.Start();

            try
            {
                return (UInt16) ((IPEndPoint) listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }

        }

        #endregion

        #region TemporaryDirectory(Purpose)

        /// <summary>
        /// A directory of its own for one test, so that no two of them read
        /// each other's accounts or configuration.
        /// </summary>
        public static String TemporaryDirectory(String Purpose)

            => Path.Combine(
                   Path.GetTempPath(),
                   $"env-{Purpose}-{Guid.NewGuid().ToString("N")[..12]}"
               );

        #endregion

        #region Remove(Directory)

        /// <summary>
        /// Take a test's directory away again; never throws, because failing a
        /// teardown would hide the failure that actually matters.
        /// </summary>
        public static void Remove(String? Directory)
        {

            try
            {
                if (Directory is not null && System.IO.Directory.Exists(Directory))
                    System.IO.Directory.Delete(Directory, true);
            }
            catch (IOException)
            { }
            catch (UnauthorizedAccessException)
            { }

        }

        #endregion

    }

}
