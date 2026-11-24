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

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod.DNS;

using cloud.charging.open.protocols.WWCP.Node.TestKit;

using OCPPServingNode  = cloud.charging.open.protocols.OCPP.Node.OCPPServingNode;

#endregion

namespace cloud.charging.open.TestEnvironment.Tests.Connections.HopByHop
{

    /// <summary>
    /// A local controller fetches a firmware image for itself from the
    /// download server when its CSMS tells it - and not a station behind it -
    /// with an UpdateFirmware: with a GET over HTTP or HTTPS, or over SFTP -
    /// only from a server its configuration names for downloads, taking up
    /// where an attempt stopped, taking no image larger than it may hold, and
    /// telling the CSMS how it went.
    /// </summary>
    [TestFixture]
    public class LC_2_Download : AToTheDownloadServer
    {

        #region (protected override) NewNode(DNSClient) / NodeName / MaxDownloadSize / BeforeConnecting(CSMS, Login)

        /// <summary>
        /// A local controller of its own directory, reporting upwards, known to
        /// its CSMS by its id.
        /// </summary>
        protected override async Task<(OCPPServingNode Node, User_Id Login)> NewNode(DNSClient DNSClient)
        {

            var directory   = NewNodeDirectory("lc", ReportsUpwards());

            var controller  = new LocalController.LocalController(
                                  DNSClient:       DNSClient,
                                  HTTPPort:        TestPorts.Free(),
                                  AccountsPath:    directory.Accounts,
                                  ConfigFile:      directory.ConfigFile,
                                  LogToConsole:    false,
                                  BridgeDebugLog:  false
                              );

            await controller.Start();

            return (controller, User_Id.Parse(controller.Node.Id.ToString()));

        }

        protected override String NodeName
            => "The local controller";

        protected override Int64 MaxDownloadSize
            => LocalController.LocalController.MaxDownloadSize;

        /// <summary>
        /// In the overlay network what the local controller sends in its own name
        /// is taken only from a login marked as one of its kind.
        /// </summary>
        protected override void BeforeConnecting(CSMS.CSMS  CSMS,
                                                 User_Id    Login)
        {
            Assert.That(CSMS.WebSocketLogins.TrySetLocalController(Login, true, out var error), Is.True, error);
        }

        #endregion

    }

}
