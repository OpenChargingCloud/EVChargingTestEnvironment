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

using System.Globalization;
using System.Net.Sockets;

using Newtonsoft.Json.Linq;

using cloud.charging.open.EV.Logging;

#endregion

namespace cloud.charging.open.TestEnvironment
{

    /// <summary>
    /// A vehicle, a charging station, a local controller, a CSMS and an EMSP,
    /// with the certificates they need and pointed at each other, until Ctrl+C.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The switches here are about the <em>site</em> and nothing else: how many
    /// of the five there are, which ports they take, where their certificates
    /// come from. What each of the five <em>is</em> - what the vehicle's
    /// battery holds, how many EVSEs the station has, what the CSMS calls
    /// itself in OCPP, whom the EMSP roams with - is said in that component's
    /// own configuration file and on its own Configuration page, exactly as it
    /// would be if it were running on its own. This program does not repeat
    /// their vocabularies.
    /// </para>
    /// <para>
    /// That is deliberate. Five programs' worth of switches on one command line
    /// would be several hundred of them, and every one would be a second place
    /// where a setting can be written down and come to disagree with the first.
    /// </para>
    /// </remarks>
    public static class Program
    {

        #region (private static) TryTakeValue(Arguments, ref Index, out Value)

        private static Boolean TryTakeValue(String[]     Arguments,
                                            ref Int32    Index,
                                            out String?  Value)
        {

            if (Index + 1 < Arguments.Length && !Arguments[Index + 1].StartsWith("--"))
            {
                Value = Arguments[++Index];
                return true;
            }

            Value = null;
            return false;

        }

        #endregion

        #region (private static) TryTakePort(Arguments, ref Index, Flag, out Value)

        private static Boolean TryTakePort(String[]    Arguments,
                                           ref Int32   Index,
                                           String      Flag,
                                           out UInt16  Value)
        {

            if (TryTakeValue(Arguments, ref Index, out var text) &&
                UInt16.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out Value) &&
                Value > 0)
            {
                return true;
            }

            Console.Error.WriteLine($"Missing or invalid port number after {Flag}!");
            Value = 0;
            return false;

        }

        #endregion

        #region (private static) RepositoryRoot()

        /// <summary>
        /// The directory holding EVChargingTestEnvironment.slnx, looked up from
        /// the binary and from the current directory; the current directory
        /// when neither leads to it.
        /// </summary>
        /// <remarks>
        /// Everything this environment writes defaults to a place below it, so
        /// that none of it ends up in bin/ - where the next "dotnet clean"
        /// would take the site's accounts and its private keys with them.
        /// </remarks>
        private static String RepositoryRoot()
        {

            foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            {

                var directory = new DirectoryInfo(start);

                while (directory is not null)
                {

                    if (File.Exists(Path.Combine(directory.FullName, "EVChargingTestEnvironment.slnx")))
                        return directory.FullName;

                    directory = directory.Parent;

                }

            }

            return Environment.CurrentDirectory;

        }

        #endregion

        #region (private static) PrintUsage()

        private static void PrintUsage()
        {
            Console.WriteLine("Usage: EVChargingTestEnvironment [--base-port <number>] [--any] [--shared]");
            Console.WriteLine("                                 [--data <dir>] [--pki <dir>] [--fresh-pki] [--evil-certs]");
            Console.WriteLine("                                 [--no-lc] [--no-vehicle] [--no-emsp] [--no-kiosk] [--no-v2g]");
            Console.WriteLine("                                 [--ocpp-tls]");
            Console.WriteLine("                                 [--mcs [--mcs-sensors <n>] [--mcs-current <A>]]");
            Console.WriteLine("                                 [--interface <name>] [--frontend-from-disk]");
            Console.WriteLine("                                 [--verbose | --quiet] [--no-trace]");
            Console.WriteLine("                                 [--sdp] [--charge]");
            Console.WriteLine();
            Console.WriteLine("A vehicle, a charging station, a local controller, a CSMS and an EMSP in one");
            Console.WriteLine("process, with an ISO 15118 PKI and an OCPP PKI built first and handed out to them.");
            Console.WriteLine();
            Console.WriteLine("What each of the five is - its battery, its EVSEs, its OCPP identity, its OCPI");
            Console.WriteLine("party - is said in its own configuration file below --data and on its own");
            Console.WriteLine("Configuration page, the same as when it runs alone. The switches here are about");
            Console.WriteLine("the site.");
            Console.WriteLine();
            Console.WriteLine("Where they listen:");
            Console.WriteLine($"  --base-port <n>   the first port taken (default: {TestEnvironmentOptions.DefaultBasePort}); everything counts up from it:");
            Console.WriteLine("                    n+0 vehicle, n+1 station, n+2 display, n+3 local controller,");
            Console.WriteLine("                    n+4 CSMS, n+5 the CSMS's OCPP port, n+6 the controller's,");
            Console.WriteLine("                    n+7 the coupler's bus under --mcs, n+8 EMSP - its OCPI");
            Console.WriteLine("                    endpoints are below /ext on that same port");
            Console.WriteLine("  --any             listen on all addresses instead of 127.0.0.1");
            Console.WriteLine("  --shared          one HTTP server on one port instead of five, the five web");
            Console.WriteLine("                    interfaces told apart by the first path segment - /EV/...,");
            Console.WriteLine("                    /ChargingStation/..., /LocalController/..., /CSMS/..., /EMSP/...");
            Console.WriteLine("                    - and one set of accounts below /ext that all five sign in");
            Console.WriteLine("                    against, so that one sign-in opens all five");
            Console.WriteLine();
            Console.WriteLine("What is written down:");
            Console.WriteLine($"  --data <dir>      where the five keep their configuration, accounts and keys");
            Console.WriteLine($"                    (default: {TestEnvironmentOptions.DefaultDataPath}/ below the repository root), one directory each");
            Console.WriteLine("  --pki <dir>       where the certificates live (default: below --data)");
            Console.WriteLine("  --fresh-pki       build them again even where they are intact. Every OCPP");
            Console.WriteLine("                    certificate signed for an earlier start stops being believed");
            Console.WriteLine("  --evil-certs      also write the deliberately malformed ISO 15118 certificates,");
            Console.WriteLine("                    for pointing at either side on purpose");
            Console.WriteLine();
            Console.WriteLine("How much of a site:");
            Console.WriteLine("  --no-lc           leave the local controller out; the station dials the CSMS");
            Console.WriteLine("  --no-vehicle      leave the vehicle out");
            Console.WriteLine("  --no-emsp         leave the EMSP out. Nothing here dials it anyway: it is the");
            Console.WriteLine("                    other end of an OCPI roaming agreement, there for a CPO to");
            Console.WriteLine("                    be pointed at");
            Console.WriteLine("  --no-kiosk        leave the station's display out - it is a second server");
            Console.WriteLine("  --no-v2g          put nothing on the wire below the charging cable: no SLAC,");
            Console.WriteLine("                    no SDP, no V2G endpoint. The OCPP half still runs");
            Console.WriteLine();
            Console.WriteLine("OCPP:");
            Console.WriteLine("  --ocpp-tls        encrypt the two OCPP ports and dial them with wss:// under");
            Console.WriteLine("                    security profile 2. Off by default, because both ends need this");
            Console.WriteLine("                    machine to know the OCPP root and neither can be given it from");
            Console.WriteLine("                    inside this process: the server will not present a certificate");
            Console.WriteLine("                    whose chain it cannot build here, and the client checks the one");
            Console.WriteLine("                    it is shown against the roots this machine has. The certificates");
            Console.WriteLine("                    are minted and in place either way; the console prints the one");
            Console.WriteLine("                    command that installs the root");
            Console.WriteLine();
            Console.WriteLine("A Megawatt Charging System coupler instead of a CCS one:");
            Console.WriteLine("  --mcs             a 10BASE-T1S bus below the cable, emulated over UDP multicast");
            Console.WriteLine("                    on port n+7: the station coordinates it (PLCA), the vehicle joins");
            Console.WriteLine("                    it with the highest priority, and temperature sensors in the");
            Console.WriteLine("                    coupler's pins are asked every cycle. SDP and the V2G endpoint");
            Console.WriteLine("                    stay as they are; there is no SLAC on a bus");
            Console.WriteLine("  --mcs-sensors <n> how many sensors sit in the pins (default: 2, DC+ and DC-)");
            Console.WriteLine("  --mcs-current <A> the current the pins carry, which is what heats them. The pins");
            Console.WriteLine("                    are rated 500 A for a 40 K rise; 800 A is an overload within a");
            Console.WriteLine("                    minute, and the station says so at critical level and on its");
            Console.WriteLine("                    V2G page. 0 - the default - is a cold coupler");
            Console.WriteLine();
            Console.WriteLine("The wire below the cable:");
            Console.WriteLine("  --interface <name>");
            Console.WriteLine("                    the interface the vehicle and the station expect each other");
            Console.WriteLine("                    on. Without one each takes the first candidate with an IPv6");
            Console.WriteLine("                    link-local address, and the console says which");
            Console.WriteLine();
            Console.WriteLine("Doing something at a start. Everything else only configures:");
            Console.WriteLine("  --sdp             let the vehicle look for a station once, and print what");
            Console.WriteLine("                    answered");
            Console.WriteLine("  --charge          run one charging session once everything is up, and print how");
            Console.WriteLine("                    it went. The whole exchange is in the logs while it happens");
            Console.WriteLine();
            Console.WriteLine("Log:");
            Console.WriteLine("  -v, --verbose     write every entry to the console, down to the debug ones");
            Console.WriteLine("  -q, --quiet       write only warnings and worse");
            Console.WriteLine("      --no-trace    do not pick up what the libraries below write with DebugX");
            Console.WriteLine();
            Console.WriteLine("While working on a web interface:");
            Console.WriteLine("  --frontend-from-disk");
            Console.WriteLine("                    serve each component's web interface from its Frontend/dist");
            Console.WriteLine("                    directory instead of the bundle in its assembly, so that");
            Console.WriteLine("                    'npm run watch' and a reload in the browser are enough");
            Console.WriteLine();
            Console.WriteLine("Whatever the console shows, each web interface shows that component's whole log");
            Console.WriteLine("under 'Logs'.");
        }

        #endregion


        public static async Task<Int32> Main(String[] Arguments)
        {

            Console.OutputEncoding = System.Text.Encoding.UTF8;

            #region Arguments

            UInt16?  basePort          = null;
            var      anyAddress        = false;
            var      shared            = false;

            String?  dataPath          = null;
            String?  pkiPath           = null;
            var      freshPKI          = false;
            var      evilCertificates  = false;

            var      noLocalController = false;
            var      noVehicle         = false;
            var      noEMSP            = false;
            var      noKiosk           = false;
            var      noV2G             = false;
            var      ocppTLS           = false;
            var      mcs               = false;
            var      mcsSensors        = 2;
            var      mcsCurrent        = 0.0;
            String?  interfaceName     = null;

            var      frontendFromDisk  = false;
            var      verbose           = false;
            var      quiet             = false;
            var      noTrace           = false;

            var      discoverAtStart   = false;
            var      chargeAtStart     = false;

            for (var i = 0; i < Arguments.Length; i++)
            {
                switch (Arguments[i])
                {

                    case "--help":
                    case "-h":
                        PrintUsage();
                        return 0;

                    case "--base-port":
                        if (!TryTakePort(Arguments, ref i, "--base-port", out var port))
                            return 1;
                        basePort = port;
                        break;

                    case "--any":
                        anyAddress = true;
                        break;

                    case "--shared":
                        shared = true;
                        break;

                    case "--data":
                        if (!TryTakeValue(Arguments, ref i, out dataPath))
                        {
                            Console.Error.WriteLine("Missing directory after --data!");
                            return 1;
                        }
                        break;

                    case "--pki":
                        if (!TryTakeValue(Arguments, ref i, out pkiPath))
                        {
                            Console.Error.WriteLine("Missing directory after --pki!");
                            return 1;
                        }
                        break;

                    case "--fresh-pki":
                        freshPKI = true;
                        break;

                    case "--evil-certs":
                        evilCertificates = true;
                        break;

                    case "--no-lc":
                        noLocalController = true;
                        break;

                    case "--no-vehicle":
                        noVehicle = true;
                        break;

                    case "--no-emsp":
                        noEMSP = true;
                        break;

                    case "--no-kiosk":
                        noKiosk = true;
                        break;

                    case "--no-v2g":
                        noV2G = true;
                        break;

                    case "--ocpp-tls":
                        ocppTLS = true;
                        break;

                    case "--mcs":
                        mcs = true;
                        break;

                    case "--mcs-sensors":
                        if (!TryTakeValue(Arguments, ref i, out var sensorsText) ||
                            !Int32.TryParse(sensorsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out mcsSensors) ||
                            mcsSensors < 0 || mcsSensors > 6)
                        {
                            Console.Error.WriteLine("Missing or invalid number after --mcs-sensors! Between 0 and 6: a bus carries eight nodes, and two of them are the station and the vehicle.");
                            return 1;
                        }
                        mcs = true;
                        break;

                    case "--mcs-current":
                        if (!TryTakeValue(Arguments, ref i, out var currentText) ||
                            !Double.TryParse(currentText, NumberStyles.Float, CultureInfo.InvariantCulture, out mcsCurrent) ||
                            mcsCurrent < 0 || mcsCurrent > 5000)
                        {
                            Console.Error.WriteLine("Missing or invalid current after --mcs-current! Amperes, between 0 and 5000.");
                            return 1;
                        }
                        mcs = true;
                        break;

                    case "--interface":
                        if (!TryTakeValue(Arguments, ref i, out interfaceName))
                        {
                            Console.Error.WriteLine("Missing name after --interface!");
                            return 1;
                        }
                        break;

                    case "--frontend-from-disk":
                        frontendFromDisk = true;
                        break;

                    case "-v":
                    case "--verbose":
                        verbose = true;
                        break;

                    case "-q":
                    case "--quiet":
                        quiet = true;
                        break;

                    case "--no-trace":
                        noTrace = true;
                        break;

                    case "--sdp":
                        discoverAtStart = true;
                        break;

                    case "--charge":
                        chargeAtStart = true;
                        break;

                    default:
                        Console.Error.WriteLine($"'{Arguments[i]}' is not a switch this program knows. --help lists them.");
                        return 1;

                }
            }

            if (verbose && quiet)
            {
                Console.Error.WriteLine("--verbose and --quiet are opposites; give one of them.");
                return 1;
            }

            if ((discoverAtStart || chargeAtStart) && noVehicle)
            {
                Console.Error.WriteLine("--sdp and --charge are things the vehicle does, and --no-vehicle leaves it out.");
                return 1;
            }

            if (mcs && noV2G)
            {
                Console.Error.WriteLine("--mcs is a bus below the charging cable, and --no-v2g puts nothing there.");
                return 1;
            }

            #endregion

            #region What all of that adds up to

            var root     = RepositoryRoot();

            var options  = new TestEnvironmentOptions {

                               DataPath          = Path.IsPathRooted(dataPath ?? "")
                                                       ? dataPath!
                                                       : Path.Combine(root, dataPath ?? TestEnvironmentOptions.DefaultDataPath),

                               PKIPath           = pkiPath is null
                                                       ? null
                                                       : Path.IsPathRooted(pkiPath)
                                                             ? pkiPath
                                                             : Path.Combine(root, pkiPath),

                               FreshPKI          = freshPKI,
                               EvilCertificates  = evilCertificates,

                               BasePort          = basePort ?? TestEnvironmentOptions.DefaultBasePort,
                               AnyAddress        = anyAddress,
                               Layout            = shared
                                                       ? WebInterfaceLayout.SharedServer
                                                       : WebInterfaceLayout.OwnPorts,

                               NoLocalController = noLocalController,
                               NoVehicle         = noVehicle,
                               NoEMSP            = noEMSP,
                               NoKiosk           = noKiosk,
                               NoV2G             = noV2G,
                               OCPPTLS           = ocppTLS,
                               MCS               = mcs,
                               MCSSensors        = mcsSensors,
                               MCSCurrent_A      = mcsCurrent,
                               InterfaceName     = interfaceName,

                               FrontendFromDisk  = frontendFromDisk,
                               ConsoleLogLevel   = verbose ? LogLevel.Debug
                                                       : quiet ? LogLevel.Warning
                                                       : LogLevel.Info,
                               BridgeDebugLog    = !noTrace,

                               DiscoverAtStart   = discoverAtStart,
                               ChargeAtStart     = chargeAtStart

                           };

            #endregion

            #region Building it

            ChargingTestEnvironment environment;

            try
            {
                environment = ChargingTestEnvironment.Create(
                                  options,
                                  Say: line => Console.WriteLine($"  {line}")
                              );
            }
            catch (Exception e)
            {

                Console.Error.WriteLine($"The test environment could not be set up: {e.Message}");

                // An environment that does not come up at all is the one moment
                // the stack trace is worth more than a tidy console.
                if (verbose)
                    Console.Error.WriteLine(e);

                return 1;

            }

            #endregion

            await using (environment)
            {

                #region Starting it

                try
                {
                    await environment.Start();
                }
                catch (Exception e)
                {

                    Console.Error.WriteLine($"The test environment could not start: {e.Message}");

                    // Said only when it is about a socket. Every failure used
                    // to be answered with this, so a configuration mistake -
                    // "the user group 'cpo' could not be made" - came with a
                    // paragraph about ports that sent whoever read it looking
                    // for a second copy that was never running.
                    if (LooksLikeAPortInUse(e))
                        Console.Error.WriteLine("Another copy of it already running is the usual answer - eight ports are taken here. " +
                                                "Stop it, or move this one out of the way with --base-port <number>.");

                    if (verbose)
                        Console.Error.WriteLine(e);

                    return 1;

                }

                #endregion

                #region What somebody who just started this needs to know

                Console.WriteLine();

                foreach (var (name, url) in environment.WebInterfaces)
                    Console.WriteLine($"  {name,-17} {url}");

                // The one URL a roaming partner is given; everything else of
                // OCPI is found from it.
                if (environment.EMSP is not null)
                    Console.WriteLine($"  {"OCPI versions",-17} {environment.EMSP.OCPIVersionsURL}");

                Console.WriteLine();

                foreach (var line in environment.Certificates.Describe())
                    Console.WriteLine($"  {line}");

                Console.WriteLine();

                foreach (var url in environment.SignInURLs)
                    Console.WriteLine($"  sign in at     {url}");

                foreach (var (who, password) in environment.GeneratedPasswords())
                {
                    Console.WriteLine();
                    Console.WriteLine("  ┌─ First start: there were no accounts, so one was made up for you ─────────");
                    Console.WriteLine($"  │  where     {who}");
                    Console.WriteLine( "  │  user      root");
                    Console.WriteLine($"  │  password  {password}");
                    Console.WriteLine( "  │  It is shown here once and kept only as a hash. Write it down.");
                    Console.WriteLine( "  └───────────────────────────────────────────────────────────────────────────");
                }

                Console.WriteLine();

                #endregion

                #region Whatever the command line asked the vehicle to actually do

                // After every web interface is up, so that a browser opened on a
                // Logs page while these run sees the exchange happen rather than
                // finding it already over.

                if (options.DiscoverAtStart && environment.EV is not null)
                {
                    var found = await environment.EV.DiscoverAsync();
                    Console.WriteLine($"  SDP            {DiscoveryOutcome(found)}");
                    Console.WriteLine();
                }

                if (options.ChargeAtStart && environment.EV is not null)
                {

                    var charged = await environment.EV.RunSessionAsync();

                    Console.WriteLine($"  session        {SessionOutcome(charged)}");

                    if (charged["battery"] is JObject pack && pack["describe"]?.Type == JTokenType.String)
                        Console.WriteLine($"  battery        {pack.Value<String>("describe")}");

                    Console.WriteLine();

                }

                #endregion

                Console.WriteLine("Press Ctrl+C to stop.");
                Console.WriteLine();

                #region Wait for Ctrl+C

                var stopped = new TaskCompletionSource();

                Console.CancelKeyPress += (_, e) => {
                    e.Cancel = true;
                    stopped.TrySetResult();
                };

                await stopped.Task;

                #endregion

            }

            return 0;

        }


        #region (private static) LooksLikeAPortInUse(Exception)

        /// <summary>
        /// Whether a start failed because something already had one of these
        /// ports.
        /// </summary>
        /// <remarks>
        /// Asked of the whole chain, because the five components wrap it
        /// differently on the way up: one of them has an exception of its own
        /// for a port it could not take, and the others let the socket error
        /// through.
        /// </remarks>
        private static Boolean LooksLikeAPortInUse(Exception Exception)
        {

            for (var e = Exception; e is not null; e = e.InnerException!)
            {

                if (e is SocketException socket &&
                    socket.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
                {
                    return true;
                }

                // The charging station, the local controller and the vehicle
                // each carry a PortUnavailableException of their own, in a
                // namespace of their own - three types with one meaning, and
                // the name is the only thing they have in common here.
                if (e.GetType().Name == "PortUnavailableException")
                    return true;

                if (e.InnerException is null)
                    break;

            }

            return false;

        }

        #endregion

        #region (private static) DiscoveryOutcome(Discovery) / SessionOutcome(Session)

        /// <summary>
        /// How a discovery went, in one line for the console. The whole of it
        /// is in the vehicle's log either way.
        /// </summary>
        private static String DiscoveryOutcome(JObject Discovery)
        {

            var outcome = Discovery.Value<String>("outcome");

            if (outcome == "found" && Discovery["secc"] is JObject secc)
                return $"a station at [{secc.Value<String>("address")}]:{secc.Value<Int32>("port")} " +
                       $"({(secc.Value<String>("security") == "tls" ? "TLS" : "no TLS")}), " +
                       $"after {Discovery.Value<Int32>("attempts")} request(s) in {Discovery.Value<Double>("elapsed_ms"):F0} ms";

            return outcome switch {
                       "rejected"     => "something answered, and none of the answers was usable - see the log",
                       "timeout"      => $"nothing answered after {Discovery.Value<Int32>("attempts")} request(s)",
                       "noInterface"  => Discovery.Value<String>("error") ?? "there was nothing to broadcast on",
                       "cancelled"    => "cancelled",
                       _              => Discovery.Value<String>("error") ?? "the discovery failed - see the log"
                   };

        }

        /// <summary>
        /// How a session went, in one line.
        /// </summary>
        private static String SessionOutcome(JObject Session)
        {

            if (Session.Value<String>("outcome") != "completed")
                return Session.Value<String>("error") ?? "the session failed - see the log";

            return $"ISO 15118{Session.Value<String>("protocol")} {Session.Value<String>("mode")} " +
                   $"with {Session.Value<String>("station")}: " +
                   $"{Session.Value<Int32>("exchanges")} exchanges, " +
                   $"{Session.Value<Int64>("bytesOnWire")} bytes on the wire (request side), " +
                   $"auth {Session.Value<String>("authorization")}, " +
                   $"setup {Session.Value<String>("sessionSetup")}, " +
                   $"in {Session.Value<Double>("elapsed_ms") / 1000:F1} s";

        }

        #endregion

    }

}
