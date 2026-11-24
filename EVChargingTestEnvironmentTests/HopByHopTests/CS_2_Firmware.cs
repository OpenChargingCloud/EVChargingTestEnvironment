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
    /// A charging station fetches a firmware image from a firmware server when
    /// its CSMS tells it to with an UpdateFirmware: with a GET over HTTP or
    /// HTTPS - only from a server its configuration names for downloads,
    /// taking up where an attempt stopped, and telling the CSMS how it went.
    /// </summary>
    [TestFixture]
    public class CS_2_Firmware : AToTheFirmwareServer
    {

        #region (protected override) NewNode(DNSClient) / NodeName

        /// <summary>
        /// A charging station of its own directory, known to its CSMS by its id.
        /// </summary>
        protected override async Task<(OCPPServingNode Node, User_Id Login)> NewNode(DNSClient DNSClient)
        {

            var directory  = NewNodeDirectory("cs");

            var station    = new ChargingStation.ChargingStation(
                                 DNSClient:       DNSClient,
                                 HTTPPort:        TestPorts.Free(),
                                 NoKiosk:         true,
                                 AccountsPath:    directory.Accounts,
                                 ConfigFile:      directory.ConfigFile,
                                 LogToConsole:    false,
                                 BridgeDebugLog:  false
                             );

            await station.Start();

            return (station, User_Id.Parse(station.Id?.ToString() ?? ChargingStation.ChargingStation.DefaultOCPP21NodeId));

        }

        protected override String NodeName
            => "The charging station";

        protected override Int64 MaxDownloadSize
            => ChargingStation.ChargingStation.MaxDownloadSize;

        #endregion

    }

}
