/*
 * Copyright (c) 2014-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of EV Charging Test Environment <https://github.com/OpenChargingCloud/EVChargingTestEnvironment>
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

using cloud.charging.open.protocols.OCPP.Node.Connections;
using cloud.charging.open.protocols.OCPP.Node.KeyPins;

using Controller = cloud.charging.open.LocalController.LocalController;

#endregion

namespace cloud.charging.open.TestEnvironment.Tests
{

    /// <summary>
    /// The line up to the CSMS of a local controller under test.
    /// </summary>
    /// <remarks>
    /// Since LocalController 589ca51 that line is a connection of the
    /// controller's store, as a charging station's are, and what it signs in
    /// with and whom it believes are the connection's. A controller built
    /// from a configuration file whose section "csms" names a URL takes that
    /// line over into its store once, when the store is empty - but only
    /// with what it signed in with before, and a ws:// line without a
    /// password is refused. So the line is made here, from that URL, with
    /// the password the test gives it.
    /// </remarks>
    internal static class ControllerUplinks
    {

        /// <summary>
        /// The line up to the CSMS: the first connection of the store.
        /// </summary>
        public static ConnectionEntry? Uplink(this Controller Controller)
            => Controller.Connections.Connections.FirstOrDefault();

        /// <summary>
        /// The line signs in with a password.
        /// </summary>
        public static Boolean TrySetUplinkPassword(this Controller  Controller,
                                                   String           Login,
                                                   String           Password,
                                                   out String?      Error)
        {

            if (!Controller.Connections.TryAddAuthentication("The password of the line up", "basic", Login, Password, out var id, out Error))
                return false;

            if (Controller.Uplink() is not ConnectionEntry line)
            {

                var url = JObject.Parse(File.ReadAllText(Controller.ConfigFile.Path))["csms"]?.Value<String>("url");

                // A controller that reports to no CSMS keeps the credentials
                // all the same, as it kept its login before.
                if (url is null)
                    return true;

                return Controller.Connections.TryAddConnection("CSMS", url, "CSMS", true, id, null, out _, out Error);

            }

            return Controller.Connections.TryUpdateConnection(
                       line.Id, line.Description, line.URL.ToString(), line.ConnectionType.ToString(), line.AutoConnect,
                       id,
                       line.CertificateId,
                       out Error
                   );

        }

        /// <summary>
        /// How the line believes its server beyond a CA.
        /// </summary>
        public static Boolean TrySetUplinkKeyPinTrust(this Controller       Controller,
                                                      ServerKeyPinSettings  Settings,
                                                      out String?           Error)
        {

            if (Controller.Uplink() is not ConnectionEntry line)
            {
                Error = "This controller has no line up to a CSMS to believe anybody on.";
                return false;
            }

            return Controller.Connections.TryChangeKeyPinTrust(line.Id, Settings, out _, out _, out Error);

        }

        /// <summary>
        /// Somebody whose signed pin sets this controller takes, for all of
        /// its connections.
        /// </summary>
        public static Boolean TryAddUplinkPinSetSigner(this Controller  Controller,
                                                       String           PublicKey,
                                                       String           Description,
                                                       out String?      Error)

            => Controller.Connections.TryAddPinSetSigner(PublicKey, Description, out Error, out _);

    }

}
