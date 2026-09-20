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

using System.Net;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.EV.Logging;

#endregion

namespace cloud.charging.open.TestEnvironment
{

    #region (enum) WebInterfaceLayout

    /// <summary>
    /// How the five web interfaces are put in front of a browser.
    /// </summary>
    public enum WebInterfaceLayout
    {

        /// <summary>
        /// Five HTTP servers on five ports, each component its own, each with
        /// accounts of its own. What each of these programs does when it is
        /// started on its own, and the default here for the same reason: it is
        /// the arrangement a bug found here is also a bug in.
        /// </summary>
        OwnPorts,

        /// <summary>
        /// One HTTP server on one port, the five of them told apart by the
        /// first path segment - <c>/EV/…</c>, <c>/ChargingStation/…</c>,
        /// <c>/LocalController/…</c>, <c>/CSMS/…</c>, <c>/EMSP/…</c> - and one set of accounts
        /// below <c>/ext</c> that all five sign in against.
        /// </summary>
        SharedServer

    }

    #endregion


    /// <summary>
    /// Everything the command line can say about this environment, in one
    /// value that is settled before anything is built.
    /// </summary>
    /// <remarks>
    /// Unlike the five programs this one stands in for, almost nothing here is
    /// written to a configuration file. What a <em>vehicle</em> is belongs in
    /// the vehicle's configuration and is written there; how many ports a test
    /// bench opened this morning does not, and a test environment that
    /// remembered its own arrangement between starts would be one more thing
    /// that can disagree with what was typed.
    /// </remarks>
    public sealed record TestEnvironmentOptions
    {

        #region Data

        /// <summary>
        /// The first of the ports taken when nothing says otherwise.
        /// </summary>
        /// <remarks>
        /// The web interface of the vehicle, which is what it is when the
        /// vehicle is started on its own. Everything else counts up from here,
        /// so that <c>--base-port</c> moves the whole block and a second
        /// environment on one machine is one switch away.
        /// </remarks>
        public const UInt16  DefaultBasePort   = 2347;

        /// <summary>
        /// Where the five components keep what they write down, below the
        /// repository root unless another place is named.
        /// </summary>
        /// <remarks>
        /// One directory per component below it, because all five call their
        /// configuration file <c>configuration.json</c> and every other file
        /// each of them keeps - accounts, keys, trust stores, logins - lives
        /// beside that one.
        /// </remarks>
        public const String  DefaultDataPath   = "data";

        #endregion

        #region Properties

        /// <summary>Where the five components keep what they write down.</summary>
        public String              DataPath            { get; init; } = DefaultDataPath;

        /// <summary>Where the certificates live.</summary>
        public String?             PKIPath             { get; init; }

        /// <summary>Build the certificates again even where they are intact.</summary>
        public Boolean             FreshPKI            { get; init; }

        /// <summary>Also write the deliberately malformed certificates.</summary>
        public Boolean             EvilCertificates    { get; init; }


        /// <summary>The first port taken; everything else counts up from it.</summary>
        public UInt16              BasePort            { get; init; } = DefaultBasePort;

        /// <summary>Listen on every address rather than on the loopback alone.</summary>
        public Boolean             AnyAddress          { get; init; }

        /// <summary>Five servers and five sets of accounts, or one of each.</summary>
        public WebInterfaceLayout  Layout              { get; init; } = WebInterfaceLayout.OwnPorts;


        /// <summary>Leave the local controller out, and let the station talk to the CSMS directly.</summary>
        public Boolean             NoLocalController   { get; init; }

        /// <summary>Leave the vehicle out.</summary>
        public Boolean             NoVehicle           { get; init; }

        /// <summary>
        /// Leave the EMSP out.
        /// </summary>
        /// <remarks>
        /// The one of the five that nothing else here dials: an EMSP is the
        /// other end of an OCPI roaming agreement, and there is no CPO
        /// speaking OCPI on this bench yet. It runs all the same - its web
        /// interface, its OCPI endpoints and its log - so that a partner can
        /// be pointed at it, which is what the switch is for when none will.
        /// </remarks>
        public Boolean             NoEMSP              { get; init; }

        /// <summary>Leave the station's display out - it is a second HTTP server on a port of its own.</summary>
        public Boolean             NoKiosk             { get; init; }

        /// <summary>Put nothing on the wire below the charging cable: no SLAC, no SDP, no V2G endpoint.</summary>
        public Boolean             NoV2G               { get; init; }

        /// <summary>The interface the vehicle and the station expect each other on.</summary>
        public String?             InterfaceName       { get; init; }


        /// <summary>
        /// Whether the two OCPP ports are encrypted, and the two things below
        /// them dial <c>wss://</c> under security profile 2.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Off, and off is the honest default rather than the lazy one. .NET
        /// builds the chain of a server's own certificate before it will
        /// present it, and refuses where it cannot reach a root the machine
        /// has - so an encrypted OCPP port whose private root is in no machine
        /// store turns every charging station away during the handshake, while
        /// reporting itself as running and encrypted. Whether this machine can
        /// do it is said on the console at every start.
        /// </para>
        /// <para>
        /// Security profile 1 - a password over an unencrypted WebSocket - is
        /// what OCPP itself calls the unsecured transport, and on a loopback
        /// bench that is what it is. Nothing about the messages differs.
        /// </para>
        /// </remarks>
        public Boolean             OCPPTLS             { get; init; }


        /// <summary>
        /// A Megawatt Charging System coupler below the cable instead of a
        /// CCS one: a 10BASE-T1S bus the station coordinates, the vehicle
        /// joins, and temperature sensors in the pins report on.
        /// </summary>
        /// <remarks>
        /// SDP and the V2G endpoint stay as they are - MCS runs ISO 15118-20
        /// over IPv6 like CCS does. What changes is the link below them: no
        /// powerline, no SLAC, and a bus with more on it than the vehicle.
        /// </remarks>
        public Boolean             MCS                 { get; init; }

        /// <summary>How many temperature sensors sit in the coupler's pins.</summary>
        public Int32               MCSSensors          { get; init; } = 2;

        /// <summary>
        /// The current the pins are carrying, in A - what heats them. Nothing
        /// here measures a current, so it is told; 800 A through pins rated
        /// for 500 A is an overload within a minute.
        /// </summary>
        public Double              MCSCurrent_A        { get; init; }


        /// <summary>Serve the web interfaces from directories on disk instead of the bundles in the assemblies.</summary>
        public Boolean             FrontendFromDisk    { get; init; }

        /// <summary>How much of the log reaches the console.</summary>
        public LogLevel            ConsoleLogLevel     { get; init; } = LogLevel.Info;

        /// <summary>Whether what the libraries below write with DebugX is picked up.</summary>
        public Boolean             BridgeDebugLog      { get; init; } = true;


        /// <summary>Run one charging session once everything is up, and print how it went.</summary>
        public Boolean             ChargeAtStart       { get; init; }

        /// <summary>Look for a station once the vehicle is up, and print what answered.</summary>
        public Boolean             DiscoverAtStart     { get; init; }

        #endregion

        #region The ports, counted off the base port

        /// <summary>
        /// The vehicle's web interface - or the one port they all share.
        /// </summary>
        /// <remarks>
        /// The five web-interface ports collapse onto one where the five share
        /// a server, and they have to: each component builds the URL it reports
        /// on its Configuration page and logs at a start out of the port it was
        /// given, so one that kept its own would name a port nothing is
        /// listening on.
        /// </remarks>
        public IPPort  VehiclePort           => Shared ? SharedPort : IPPort.Parse((UInt16) (BasePort + 0));

        /// <summary>The station's web interface.</summary>
        public IPPort  StationPort           => Shared ? SharedPort : IPPort.Parse((UInt16) (BasePort + 1));

        /// <summary>The station's display, which is a second server with no sign-in on it.</summary>
        public IPPort  KioskPort             => IPPort.Parse((UInt16) (BasePort + 2));

        /// <summary>The local controller's web interface.</summary>
        public IPPort  ControllerPort        => Shared ? SharedPort : IPPort.Parse((UInt16) (BasePort + 3));

        /// <summary>The CSMS's web interface.</summary>
        public IPPort  CSMSPort              => Shared ? SharedPort : IPPort.Parse((UInt16) (BasePort + 4));

        /// <summary>What the two things that dial out are told to speak.</summary>
        public String  OCPPScheme            => OCPPTLS ? "wss" : "ws";

        /// <summary>
        /// The OCPP security profile the two things that dial out use: a
        /// password over TLS, or a password on its own.
        /// </summary>
        public Byte    OCPPSecurityProfile   => (Byte) (OCPPTLS ? 2 : 1);

        /// <summary>
        /// Which profiles the two OCPP ports accept.
        /// </summary>
        /// <remarks>
        /// One of them and not a generous list, because this list is what
        /// decides whether the port is encrypted at all: anything above 1 in
        /// it turns TLS on, and a port that is encrypted while the things
        /// below it dial <c>ws://</c> answers them with a corrupted frame.
        /// </remarks>
        public IEnumerable<Int32> OCPPSecurityProfiles
            => OCPPTLS ? [ 2, 3 ] : [ 1 ];

        /// <summary>Where charging stations and local controllers dial the CSMS.</summary>
        public IPPort  CSMSOCPPPort          => IPPort.Parse((UInt16) (BasePort + 5));

        /// <summary>Where charging stations dial the local controller.</summary>
        public IPPort  ControllerOCPPPort    => IPPort.Parse((UInt16) (BasePort + 6));

        /// <summary>
        /// The port of the emulated 10BASE-T1S bus, where there is one.
        /// </summary>
        /// <remarks>
        /// Counted off the base port like the others, so that two environments
        /// on one machine are two buses: multicast groups are shared by every
        /// socket that joins them, and two coordinators on one group are two
        /// coordinators on one wire.
        /// </remarks>
        public IPPort  T1SPort               => IPPort.Parse((UInt16) (BasePort + 7));

        /// <summary>
        /// The multicast group and port that are the coupler's bus.
        /// </summary>
        public IPEndPoint  T1SBus
            => new (
                   protocols.ISO15118.T1S.T1SConstants.DefaultMulticastEndpoint.Address,
                   T1SPort.ToUInt16()
               );

        /// <summary>
        /// The EMSP's web interface - and its OCPI endpoints, which are on
        /// the same server below <c>/ext</c>.
        /// </summary>
        /// <remarks>
        /// Beyond the bus rather than before it, so that the seven ports the
        /// four original components take keep the numbers they always had.
        /// </remarks>
        public IPPort  EMSPPort              => Shared ? SharedPort : IPPort.Parse((UInt16) (BasePort + 8));

        /// <summary>
        /// The one port everything is on when they share a server.
        /// </summary>
        /// <remarks>
        /// The vehicle's, which is the address somebody who has used any of
        /// these before already has in their history - and the five web
        /// interfaces are then one path segment apart rather than one port.
        /// </remarks>
        public IPPort  SharedPort            => IPPort.Parse(BasePort);

        #endregion

        #region The base paths, where they share a server

        /// <summary>What the vehicle's web interface sits below.</summary>
        public static readonly HTTPPath  VehicleBasePath     = HTTPPath.Parse("/EV");

        /// <summary>What the station's web interface sits below.</summary>
        public static readonly HTTPPath  StationBasePath     = HTTPPath.Parse("/ChargingStation");

        /// <summary>What the local controller's web interface sits below.</summary>
        public static readonly HTTPPath  ControllerBasePath  = HTTPPath.Parse("/LocalController");

        /// <summary>What the CSMS's web interface sits below.</summary>
        public static readonly HTTPPath  CSMSBasePath        = HTTPPath.Parse("/CSMS");

        /// <summary>What the EMSP's web interface sits below.</summary>
        public static readonly HTTPPath  EMSPBasePath        = HTTPPath.Parse("/EMSP");

        /// <summary>Where the accounts all five sign in against live.</summary>
        public static readonly HTTPPath  SharedExtAPIPath    = HTTPPath.Parse("/ext");

        #endregion

        #region Whether they share

        /// <summary>Whether the five run on one HTTP server.</summary>
        public Boolean Shared
            => Layout == WebInterfaceLayout.SharedServer;

        /// <summary>The address everything listens on.</summary>
        public IIPAddress Address
            => AnyAddress
                   ? IPvXAddress.Any
                   : IPv4Address.Localhost;

        /// <summary>
        /// The host a program on this machine reaches the others at.
        /// </summary>
        /// <remarks>
        /// The loopback address even when every address is being listened on:
        /// these components are talking to each other inside one process, and a URL
        /// naming <c>0.0.0.0</c> is a URL that connects nowhere.
        /// </remarks>
        public String Host
            => "127.0.0.1";

        #endregion

        #region Where each component keeps what it writes down

        /// <summary>The vehicle's directory.</summary>
        public String VehicleDirectory     => Path.Combine(DataPath, "EV");

        /// <summary>The station's directory.</summary>
        public String StationDirectory     => Path.Combine(DataPath, "ChargingStation");

        /// <summary>The local controller's directory.</summary>
        public String ControllerDirectory  => Path.Combine(DataPath, "LocalController");

        /// <summary>The CSMS's directory.</summary>
        public String CSMSDirectory        => Path.Combine(DataPath, "CSMS");

        /// <summary>The EMSP's directory.</summary>
        public String EMSPDirectory        => Path.Combine(DataPath, "EMSP");

        /// <summary>
        /// The accounts all five share, where they share a server.
        /// </summary>
        public String SharedAccountsPath   => Path.Combine(DataPath, "accounts");

        #endregion

    }

}
