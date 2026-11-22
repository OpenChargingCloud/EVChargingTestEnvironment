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

using org.GraphDefined.Vanaheimr.Hermod.DNS;

using cloud.charging.open.protocols.WWCP.Node.TestKit;

using WWCPNode  = cloud.charging.open.protocols.WWCP.Node.WWCPNode;

#endregion

namespace cloud.charging.open.TestEnvironment.Tests.Connections.HopByHop
{

    /// <summary>
    /// A local controller reads an energy meter over a WebSocket of Modbus or over
    /// the SSH subsystem of Modbus, as what the SunSpec role of its login lets it -
    /// without reporting to any CSMS: a meter is no back end.
    /// </summary>
    [TestFixture]
    public class LC_2_EM : AToTheEnergyMeter
    {

        #region (protected override) NewReader(DNSClient) / ReaderName

        /// <summary>
        /// A local controller of its own directory, reporting to no CSMS.
        /// </summary>
        protected override async Task<WWCPNode> NewReader(DNSClient DNSClient)
        {

            var directory   = NewNodeDirectory("lc");

            var controller  = new LocalController.LocalController(
                                  DNSClient:       DNSClient,
                                  HTTPPort:        TestPorts.Free(),
                                  AccountsPath:    directory.Accounts,
                                  ConfigFile:      directory.ConfigFile,
                                  LogToConsole:    false,
                                  BridgeDebugLog:  false
                              );

            await controller.Start();

            return controller;

        }

        protected override String ReaderName
            => "The local controller";

        #endregion

    }

}
