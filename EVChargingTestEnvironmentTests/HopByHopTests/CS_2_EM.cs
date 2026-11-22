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

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Hermod.SunSpecModbusTLS.Common;

using cloud.charging.open.protocols.WWCP.Node.TestKit;

using WWCPNode  = cloud.charging.open.protocols.WWCP.Node.WWCPNode;

#endregion

namespace cloud.charging.open.TestEnvironment.Tests.Connections.HopByHop
{

    /// <summary>
    /// A charging station reads an energy meter over a WebSocket of Modbus or over
    /// the SSH subsystem of Modbus - its own connection to it, and the meter of an
    /// EVSE over that connection - as what the SunSpec role of its login lets it.
    /// </summary>
    [TestFixture]
    public class CS_2_EM : AToTheEnergyMeter
    {

        #region (protected override) NewReader(DNSClient) / ReaderName

        /// <summary>
        /// A charging station of its own directory, whose EVSE 1 has a meter read
        /// over the URL its test names, where it names one.
        /// </summary>
        protected override async Task<WWCPNode> NewReader(DNSClient DNSClient)
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

            return station;

        }

        protected override String ReaderName
            => "The charging station";

        #endregion


        #region TheMeterOfAnEVSE_IsReadOverTheConnection(Transport)

        /// <summary>
        /// The meter of an EVSE whose entry names the URL of the station's
        /// connection to an energy meter is read over that connection, over SSH
        /// or over a WebSocket over TLS, as the role of the station's login lets it.
        /// </summary>
        [TestCase("ssh")]
        [TestCase("wss")]
        public async Task TheMeterOfAnEVSE_IsReadOverTheConnection(String Transport)
        {

            var station = (ChargingStation.ChargingStation) reader!;

            var (_, connection, _) = await TheReaderConnects(authPassword, Transport, SunSpecRoles.ReadOnly);

            var url = station.Connections.Connections.First(entry => entry.Id == connection).URL.ToString();

            var (success, error, _) = await station.UpdateMeterConfiguration(
                                                new JObject(
                                                    new JProperty("evse",  1),
                                                    new JProperty("url",   url),
                                                    new JProperty("unit",  1)
                                                )
                                            );

            Assert.That(success, Is.True, error);

            await Until(() => station.MeterOf(1)?.Reading is not null, $"the meter of EVSE 1 being read over {Transport}", station, meter);

            var reading = station.MeterOf(1)!.Reading!;

            Assert.Multiple(() => {
                Assert.That(station.MeterOf(1)!.Endpoint.Over?.ToString(),  Is.EqualTo(url),                "The meter of EVSE 1 is not read over the connection.");
                Assert.That(reading.SerialNumber,                            Is.EqualTo(meter!.SerialNumber), "The meter of EVSE 1 is another meter.");
            });

        }

        #endregion

    }

}
