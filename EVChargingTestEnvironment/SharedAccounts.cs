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

using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.Mail;

using NullMailer     = org.GraphDefined.Vanaheimr.Hermod.SMTP.NullMailer;

using EVRoles        = cloud.charging.open.EV.Web;
using StationRoles   = cloud.charging.open.ChargingStation.Web;
using ControlRoles   = cloud.charging.open.LocalController.Web;
using CSMSRoles      = cloud.charging.open.CSMS.Web;
using EMSPRoles      = cloud.charging.open.EMSP.Web;

#endregion

namespace cloud.charging.open.TestEnvironment
{

    /// <summary>
    /// The one set of accounts all five components sign in against when they
    /// share an HTTP server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On its own, each of these five builds an <c>HTTPExtAPI</c> of its own at
    /// <c>/ext</c>, with a database of its own. Five of those on one server
    /// cannot work - the second one to register at <c>/ext</c> is refused - and
    /// would not be wanted if it could: five sign-ins for five pages of one
    /// site is five passwords to keep, and the rights somebody has at the CSMS
    /// would say nothing about what they may do at the station.
    /// </para>
    /// <para>
    /// So in the shared arrangement exactly one of these is built, here, and
    /// handed to all five. The groups a role is carried by are made by each
    /// component as it starts, as they always are, and they land in this one
    /// database: the names overlap on purpose - all five have
    /// <c>systemadmin</c> and <c>viewer</c>, three of them have <c>cpo</c>
    /// and the EMSP has <c>emsp</c> where those have <c>cpo</c> -
    /// so an account in <c>systemadmin</c> is an administrator of all five at
    /// once. That is the single sign-on, and it is a consequence of the roles
    /// rather than a mechanism bolted beside them.
    /// </para>
    /// <para>
    /// The cookie is issued for <c>/</c> and not for <c>/ext</c>, which is what
    /// makes it reach <c>/EV/api</c> and <c>/CSMS/api</c> alike - the same
    /// reason each component gives for the same setting when it is alone.
    /// </para>
    /// </remarks>
    public static class SharedAccounts
    {

        #region Build(HTTPServer, Path, AccountsPath)

        /// <summary>
        /// One HTTPExt API for the five of them.
        /// </summary>
        /// <param name="HTTPServer">The one server they all register within.</param>
        /// <param name="Path">Where it sits - "/ext", beside the five components' own paths rather than below any of them.</param>
        /// <param name="AccountsPath">Where the accounts live between starts.</param>
        public static HTTPExtAPI Build(HTTPServer  HTTPServer,
                                       HTTPPath    Path,
                                       String      AccountsPath)
        {

            // Ending in a separator, because the HTTPExt API builds the paths
            // of its files by putting strings together rather than with
            // Path.Combine: a directory that does not end in one would give it
            // "...accountsUsersAPI" and not "...accounts/UsersAPI".
            var accountsPath = AccountsPath.EndsWith(System.IO.Path.DirectorySeparatorChar)
                                   ? AccountsPath
                                   : AccountsPath + System.IO.Path.DirectorySeparatorChar;

            return new HTTPExtAPI(

                       HTTPServer:             HTTPServer,
                       RootPath:               Path,
                       HTTPServerName:         "OpenChargingCloud EV Charging Test Environment",
                       HTTPServiceName:        "OpenChargingCloud EV Charging Test Environment",
                       APIRobotEMailAddress:   EMailAddress.Parse("OpenChargingCloud Test Environment Robot <robot@charging.cloud>"),
                       APIRobotGPGPassphrase:  "",

                       // Nothing here sends mail, for the same reason none of
                       // the five does on its own: a test bench has no
                       // submission client, and a mailer that swallows what it
                       // is given is better than one quietly retrying against a
                       // host nobody configured.
                       SMTPSubmissionClient:   new NullMailer(),
                       DisableNotifications:   true,

                       // "/" and not this API's own root path: the cookie has
                       // to reach /EV/api, /ChargingStation/api, /LocalController/api,
                       // /CSMS/api and /EMSP/api, and a cookie scoped to /ext would be
                       // sent to none of them - a browser signed in at
                       // /ext/login would look signed out on all five pages.
                       HTTPCookiePath:         "/",

                       // A secure cookie is dropped by a browser over plain
                       // HTTP, and a test bench is reached over plain HTTP.
                       UseSecureCookies:       false,

                       // The shortest group identification any of the five
                       // needs, across all five rather than one of them: the
                       // local controller's "cpo" is three characters, and
                       // Hermod's own floor is four. A group that is refused
                       // leaves a role nobody can ever hold, and the refusal
                       // is a returned result rather than an exception -
                       // which is a refusal nobody is obliged to notice.
                       MinUserGroupIdLength:   ShortestRoleName,

                       LoggingPath:            accountsPath,
                       DatabaseFileName:       "users.db",

                       // Left on, and that is what makes the directory above:
                       // switching it off skips the CreateDirectory that the
                       // accounts file is written into, and the first account
                       // created would fail on a path that was never made.
                       DisableLogging:         false

                   );

        }

        #endregion

        #region ShortestRoleName

        /// <summary>
        /// The shortest name any role of any of the five components has.
        /// </summary>
        /// <remarks>
        /// Asked of all five rather than written down as a number, so that a
        /// role added in any of those five repositories cannot quietly become a
        /// role nobody here can hold.
        /// </remarks>
        public static Byte ShortestRoleName

            => (Byte) new[] {
                          EVRoles.     UserRole.All.Min(role => role.Name.Length),
                          StationRoles.UserRole.All.Min(role => role.Name.Length),
                          ControlRoles.UserRole.All.Min(role => role.Name.Length),
                          CSMSRoles.   UserRole.All.Min(role => role.Name.Length),
                          EMSPRoles.   UserRole.All.Min(role => role.Name.Length)
                      }.Min();

        #endregion

    }

}
