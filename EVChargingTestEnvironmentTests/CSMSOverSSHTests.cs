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

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.SSH;

using cloud.charging.open.protocols.WWCP;
using cloud.charging.open.protocols.WWCP.NetworkingNode;
using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.TestKit;
using cloud.charging.open.protocols.OCPP;
using cloud.charging.open.protocols.OCPP.SSH;
using cloud.charging.open.protocols.OCPPv2_1;
using cloud.charging.open.protocols.OCPPv2_1.CS;
using cloud.charging.open.protocols.OCPPv2_1.CSMS;

using cloud.charging.open.protocols.OCPP.Node.Connections;

using WWCPNode    = cloud.charging.open.protocols.WWCP.Node.WWCPNode;
using OCPPv21Node = cloud.charging.open.protocols.OCPPv2_1.NetworkingNode.INetworkingNode;
using AuthMethod  = cloud.charging.open.CSMS.OCPP.AuthMethod;
using Management  = cloud.charging.open.CSMS.CSMS;

#endregion

namespace cloud.charging.open.TestEnvironment.Tests
{

    /// <summary>
    /// OCPP over SSH from end to end, with a real CSMS as the server: a
    /// charging station that dials its CSMS at "ssh://" itself, with no local
    /// controller in between (plan 5.7) - the tests of
    /// <see cref="OCPPOverSSHTests"/>, and what only the CSMS's station logins
    /// decide.
    /// </summary>
    /// <remarks>
    /// The CSMS answers the station itself: its OCPP node accepts every
    /// BootNotification, answers a heartbeat with the time and a binary data
    /// transfer with its data reversed.
    /// </remarks>
    [TestFixture]
    public class CSMSOverSSHTests : OCPPOverSSHTests
    {

        #region Data

        private Management?  csms;
        private String       csmsDirectory  = "";
        private IPPort       csmsHTTPPort;

        #endregion

        #region SetUp

        [SetUp]
        public async Task StartTheCSMS()
        {

            csmsDirectory = TestParties.TemporaryDirectory("csms-ssh");
            directories.Add(csmsDirectory);

            // Made again, on fresh ports, where another test run on this
            // machine took one before the CSMS could bind it.
            csms = await TestPorts.StartedOnFreshPorts(() => NewCSMS(csmsDirectory, CSMSConfiguration()));

            Assert.That(csms.OCPPOverSSHServed, Is.True, "OCPP over SSH is switched on in the file, and not served.");

        }

        #endregion


        #region The server, as OCPPOverSSHTests asks for it

        protected override String         TheServer
            => "the CSMS";

        protected override String         ConnectionType
            => "csms";

        protected override WWCPNode       Server
            => CSMS;

        protected override OCPPv21Node    OCPPNode
            => CSMS.Node;

        protected override OCPPSshServer  StationSSHServer
            => CSMS.StationSSHServer!;

        protected override UInt16         WebSocketPort
            => CSMS.OCPPServerSettings.TCPPort!.Value.ToUInt16();

        protected override IEnumerable<NetworkingNode_Id> WebSocketStations
            => CSMS.StationServer!.ConnectedNetworkingNodeIds;

        protected override void GiveTheKey(String Login, ISshHostKey Key, String? Group = null)
        {
            Assert.That(CSMS.TryAddStationSSHKey(Login, LineOf(Key), "test", Group, null, "tests", out _, out var error, out _),
                        Is.True, error);
        }

        protected override void TakeAwayTheKey(String Login, String Fingerprint)
        {
            Assert.That(CSMS.StationLogins.TryRemoveSSHKey(Login, Fingerprint, out var error), Is.True, error);
        }

        protected override void GiveThePassword(String Login, String Password)
        {
            Assert.That(CSMS.StationLogins.TrySetPassword(Login, Password, null, "tests", out _, out var error), Is.True, error);
        }

        protected override Task<TriggerMessageResponse> AskForABootNotification(String Login)

            => CSMS.Node.TriggerMessage(
                   Destination:       SourceRouting.To(NetworkingNode_Id.Parse(Login)),
                   RequestedMessage:  MessageTrigger.BootNotification
               );

        protected override async Task TheServerStops()
        {
            await CSMS.DisposeAsync();
            csms = null;
        }

        protected override async Task TheServerStartsAgain()
        {
            csms = NewCSMS(csmsDirectory, null, csmsHTTPPort);
            await csms.Start();
        }

        protected override async Task StopTheServers()
        {

            if (csms is not null)
                await csms.DisposeAsync();

            csms = null;

        }

        protected override IEnumerable<(String Who, WWCPNode? Node)> Speakers
            => [ ("CSMS", csms) ];

        #endregion

        #region (private) The CSMS

        /// <summary>
        /// A CSMS whose station server listens on the loopback address, over a
        /// WebSocket and over SSH, with an SSH server of its own.
        /// </summary>
        private static JObject CSMSConfiguration()

            => new (
                   new JProperty("nts",         TestParties.NoTimeServers),
                   new JProperty("ocppServer",  new JObject(
                       new JProperty("enabled",           true),
                       new JProperty("address",           "127.0.0.1"),
                       new JProperty("port",              TestPorts.Free()),
                       new JProperty("securityProfiles",  new JArray(1)),
                       new JProperty("subprotocols",      new JArray("ocpp2.1", "ocpp2.0.1")),
                       new JProperty("ssh",               new JObject(
                           new JProperty("enabled",         true),
                           new JProperty("maxMessageSize",  MaxMessageSize)
                       ))
                   )),
                   new JProperty("ssh",         new JObject(
                       new JProperty("enabled",           true),
                       new JProperty("port",              TestPorts.Free()),

                       // Served, so that a station refused SFTP is refused
                       // for being a station, and not because nobody gets it.
                       new JProperty("sftp",              true)
                   ))
               );

        /// <summary>
        /// A CSMS in the given directory, built and not started: with the
        /// given configuration written first, or with what is there.
        /// </summary>
        private Management NewCSMS(String    Directory,
                                   JObject?  Configuration,
                                   IPPort?   HTTPPort  = null)
        {

            System.IO.Directory.CreateDirectory(Directory);

            var configFile = Path.Combine(Directory, "configuration.json");

            if (Configuration is not null)
                File.WriteAllText(configFile, Configuration.ToString());

            csmsHTTPPort = HTTPPort ?? IPPort.Parse(TestPorts.Free());

            return new Management(
                       DNSClient:       Resolver(),
                       HTTPPort:        csmsHTTPPort,
                       AccountsPath:    Path.Combine(Directory, "accounts"),
                       ConfigFile:      new WWCPConfigFile(configFile),
                       LogToConsole:    false,
                       BridgeDebugLog:  false
                   );

        }

        private Management CSMS
            => csms ?? throw new InvalidOperationException("No CSMS is running.");

        #endregion


        // What the CSMS's station logins decide

        #region AStationSwitchedOff_IsSignedOutAtOnce_AndTurnedAway()

        /// <summary>
        /// A station connected over SSH is switched off in the CSMS's station
        /// logins: its session ends at once, not at the node's next round of
        /// checks, and the station is turned away when it comes back. Another
        /// station is not touched by it.
        /// </summary>
        [Test]
        public async Task AStationSwitchedOff_IsSignedOutAtOnce_AndTurnedAway()
        {

            // Longer than the test, so that only the store's own news can do it.
            CSMS.SSHSessionRecheckEvery = TimeSpan.FromMinutes(10);

            var (cs001, connection001) = await AStationConnected("cs001");
            var (cs002, connection002) = await AStationConnected("cs002");

            Assert.That(CSMS.StationLogins.TrySetEnabled("cs001", false, out var error), Is.True, error);

            await Until(() => !IsConnectedOverSSH("cs001") && !HasAnSSHSessionOf("cs001"), "The session of a station switched off ending", cs001);
            await Until(() => StatusOf(cs001, connection001) == ConnectionStatus.Refused,  "The station being turned away when it comes back", cs001);

            Assert.Multiple(() => {
                // The SSH connection is the station's and outlives the session: coming
                // back is a new channel of it, and that is what is refused.
                Assert.That(cs001.DialledConnections[connection001],  Does.Contain("refused it the OCPP subsystem"));
                Assert.That(StatusOf(cs002, connection002),           Is.EqualTo(ConnectionStatus.Connected), "Switching cs001 off ended cs002's session.");
                Assert.That(IsConnectedOverSSH("cs002"),              Is.True);
            });

        }

        #endregion

        #region AGroupThatTakesNoSSHKeys_SignsItsStationsOut_AndTurnsThemAway()

        /// <summary>
        /// The login group of a station connected over SSH stops taking SSH
        /// keys: the station's session ends at once, and it is turned away
        /// when it comes back. A station of another group is not touched.
        /// </summary>
        [Test]
        public async Task AGroupThatTakesNoSSHKeys_SignsItsStationsOut_AndTurnsThemAway()
        {

            // Longer than the test, so that only the store's own news can do it.
            CSMS.SSHSessionRecheckEvery = TimeSpan.FromMinutes(10);

            Assert.That(CSMS.StationLogins.TryAddOrUpdateGroup("field-test", "Field test", true, [ AuthMethod.Basic, AuthMethod.SSHKey ], [ 1 ], null, out var error),
                        Is.True, error);

            var (inTheGroup, connection) = await AStationConnected("cs001", Group: "field-test");
            var (elsewhere,  other)      = await AStationConnected("cs002");

            Assert.That(CSMS.StationLogins.TryAddOrUpdateGroup("field-test", "Field test", true, [ AuthMethod.Basic ], [ 1 ], null, out error),
                        Is.True, error);

            await Until(() => !IsConnectedOverSSH("cs001") && !HasAnSSHSessionOf("cs001"), "The session of a station whose group takes no SSH keys ending", inTheGroup);
            await Until(() => StatusOf(inTheGroup, connection) == ConnectionStatus.Refused, "The station being turned away when it comes back", inTheGroup);

            Assert.Multiple(() => {
                // The SSH connection is the station's and outlives the session: coming
                // back is a new channel of it, and that is what is refused.
                Assert.That(inTheGroup.DialledConnections[connection],  Does.Contain("refused it the OCPP subsystem"));
                Assert.That(StatusOf(elsewhere, other),                 Is.EqualTo(ConnectionStatus.Connected), "A change to another group ended cs002's session.");
                Assert.That(IsConnectedOverSSH("cs002"),                Is.True);
            });

        }

        #endregion

        #region OCPPOverSSH_SwitchedOff_TurnsTheStationsAway_AndOn_LetsThemIn_WithoutARestart()

        /// <summary>
        /// OCPP over SSH switched off in the CSMS's configuration, while it
        /// runs: the station's session ends, and the station is not let in at
        /// all when it comes back - not merely refused the subsystem. Switched
        /// on again, a station is let in, without the CSMS being started again.
        /// </summary>
        [Test]
        public async Task OCPPOverSSH_SwitchedOff_TurnsTheStationsAway_AndOn_LetsThemIn_WithoutARestart()
        {

            var (station, connection) = await AStationConnected("cs001");

            Assert.That(CSMS.TryUpdateOCPPServerConfiguration(JObject.Parse("""{ "ssh": { "enabled": false } }"""), out var error),
                        Is.True, error);

            await Until(() => !IsConnectedOverSSH("cs001") && !HasAnSSHSessionOf("cs001"), "The session ending with OCPP over SSH switched off", station);
            await Until(() => StatusOf(station, connection) == ConnectionStatus.Refused,    "The station being turned away when it comes back", station);

            Assert.Multiple(() => {
                // The SSH connection is the station's and outlives the session: coming
                // back is a new channel of it, and that is what is refused.
                Assert.That(station.DialledConnections[connection],  Does.Contain("refused it the OCPP subsystem"));
                Assert.That(CSMS.OCPPOverSSHServed,                  Is.False);
            });

            Assert.That(CSMS.TryUpdateOCPPServerConfiguration(JObject.Parse("""{ "ssh": { "enabled": true } }"""), out error),
                        Is.True, error);

            await AStationConnected("cs002");

        }

        #endregion

    }

}
