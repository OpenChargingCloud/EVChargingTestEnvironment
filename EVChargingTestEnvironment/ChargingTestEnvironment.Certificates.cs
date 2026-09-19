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

using System.Security.Cryptography;

using Newtonsoft.Json.Linq;

using cloud.charging.open.EV.Certificates;

using Vehicle       = cloud.charging.open.EV.EV;
using CSMSStore     = cloud.charging.open.CSMS.OCPP;
using ControlStore  = cloud.charging.open.LocalController.OCPP;

#endregion

namespace cloud.charging.open.TestEnvironment
{

    public sealed partial class ChargingTestEnvironment
    {

        #region Data

        /// <summary>
        /// What the charging station calls itself in OCPP 2.1, which is what
        /// the thing above it knows it by.
        /// </summary>
        /// <remarks>
        /// Not chosen here: it is what <c>ChargingStation</c> names its OCPP
        /// 2.1 node, and a login written for anything else is a login the
        /// station never presents.
        /// </remarks>
        public const String  StationNodeId     = "test02";

        /// <summary>What the local controller calls itself upwards.</summary>
        public const String  ControllerNodeId  = "LC001";

        /// <summary>The group a station's login belongs to, where the store keeps groups.</summary>
        private const String LoginGroup        = "default";

        #endregion


        #region (private) HandOutCertificates()

        /// <summary>
        /// Give each of the four what it needs to prove who it is, and tell
        /// each of them whom to believe.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Between construction and starting, and it has to be exactly there:
        /// the key stores are the components' own and do not exist before they
        /// are built, and starting is the moment each of them dials out with
        /// whatever it is holding.
        /// </para>
        /// <para>
        /// Everything here is written to the stores these components already
        /// keep, through the same methods their web interfaces call. Nothing is
        /// put anywhere they would not have looked - so a certificate handed
        /// out here appears under <em>Server certificates</em> or
        /// <em>Client trust</em> in the browser, with the same warnings beside
        /// it, and can be replaced from there.
        /// </para>
        /// <para>
        /// Idempotent, because this runs at every start and the stores keep
        /// what they were given: a component that already holds a certificate
        /// from an earlier start is left alone, which is also what makes the
        /// OCPP root worth keeping between starts.
        /// </para>
        /// </remarks>
        private async Task HandOutCertificates()
        {

            await Task.Yield();

            #region The CSMS: something to listen with, and whom it believes

            var csmsServes = EnsureServerCertificate(
                                 CSMS.ServerCertificates,
                                 $"CN=csms.test.local, O=EV Charging Test Environment, C=DE",
                                 [ options.Host, "localhost", "csms.test.local" ],
                                 "CSMS"
                             );

            EnsureTrustAnchor(CSMS.ClientTrust, "CSMS");

            #endregion

            #region The local controller: the same, one level down

            var controllerServes = false;

            if (Controller is not null)
            {

                controllerServes = EnsureServerCertificate(
                                       Controller.ServerCertificates,
                                       $"CN=lc.test.local, O=EV Charging Test Environment, C=DE",
                                       [ options.Host, "localhost", "lc.test.local" ],
                                       "LC"
                                   );

                EnsureTrustAnchor(Controller.ClientTrust, "LC");

            }

            #endregion

            #region The station: something to dial out with

            EnsureClientCertificate();

            #endregion

            #region Who may sign in where, and with what

            // One password per line of the chain, made up here and written to
            // both ends of it at once - which is the one thing a site made of
            // four separate programs cannot do for itself, and the reason
            // somebody setting this up by hand spends an afternoon on it.
            var stationSecret     = NewSecret();
            var controllerSecret  = NewSecret();

            if (Controller is not null)
            {

                // The station dials the controller...
                AllowStation(Controller.StationLogins, StationNodeId, stationSecret, "LC");

                // ...and the controller dials the CSMS.
                AllowStation(CSMS.StationLogins, ControllerNodeId, controllerSecret, "CSMS");

                if (!Controller.CSMSLogin.TrySetPassword(ControllerNodeId, controllerSecret, out var loginError))
                    throw new InvalidOperationException($"The local controller could not be given its CSMS login: {loginError}");

            }

            else
                // No controller in the middle: the station dials the CSMS itself.
                AllowStation(CSMS.StationLogins, StationNodeId, stationSecret, "CSMS");

            DialUpwards(stationSecret);

            #endregion

            #region The vehicle: its four certificates and the roots it believes, into its store

            // Through the vehicle's own store, the way its Certificates page
            // does it: the four PKCS#12 bundles of this environment's PKI are
            // opened here with the PKI's one password and kept in the store
            // without one, which is that store's rule, and the session is then
            // told the handles they became. Idempotent, because a handle is
            // the head of a fingerprint: the same bundle imported at every
            // start is the same entry every time.
            //
            // Both V2G roots are believed, because this environment presents
            // material from both hierarchies - the -2 one between its own two
            // ends, the -20 one when the vehicle is pointed at the reference
            // station. Nothing is imported as an MO or OEM root: each
            // hierarchy has one root above everything, and one certificate
            // has one purpose in that store. The vehicle says at a session
            // that it holds no root of those kinds, and proceeds.
            if (EV is { } vehicle)
                HandOutVehicleCertificates(vehicle);

            #endregion

            #region What this machine can actually do with those certificates

            // Asked now rather than found out in a handshake. .NET builds the
            // chain of a server's own certificate before it will present one,
            // and refuses where it cannot reach a root this machine has - what
            // a charging station then sees is a TLS handshake reset with
            // nothing said, at a port reporting itself as running and
            // encrypted.
            var serves = csmsServes && (Controller is null || controllerServes);

            if (options.OCPPTLS)
            {

                // Both ends of an encrypted OCPP port need the same thing from
                // this machine, and neither of them can be given it from
                // inside this process. The server will not present a
                // certificate whose chain it cannot build here; the client
                // validates the one it is shown the ordinary way, against the
                // roots this machine has. A private root that is in no store
                // fails both - the server silently, the client as a bare "400
                // Bad Request" with the real reason wrapped inside it.
                //
                // Putting a root into somebody's certificate store is not
                // something a test environment should do while they are
                // looking the other way, so it is said rather than done.
                Console.Say(
                    "env",
                    $"OCPP: the two ports are encrypted (security profile {options.OCPPSecurityProfile}). Both ends need this " +
                    $"machine to know the OCPP root, and it is not installed by starting this: {InstallRootCommand()}"
                );

                if (!serves)
                    Console.Say(
                        "env",
                        "Until then the ports cannot even present their certificates here, so nothing below them will " +
                        "get through the handshake at all. Leave --ocpp-tls off for a bench that does not need TLS."
                    );

            }

            else
                Console.Say(
                    "env",
                    $"OCPP: security profile 1, so the two OCPP ports are unencrypted. The certificates for profile 2 " +
                    $"are minted and in place; --ocpp-tls turns them on, and needs the OCPP root in this machine's " +
                    $"certificate store to work."
                );

            #endregion

        }

        #endregion


        #region (private) HandOutVehicleCertificates(Vehicle)

        /// <summary>
        /// The vehicle's store filled from the PKI, and its session told which
        /// entries to use.
        /// </summary>
        private void HandOutVehicleCertificates(Vehicle Vehicle)
        {

            foreach (var root in Certificates.V2GRootCertificateFiles)
                Import(Vehicle, root, CertificateKind.V2GRoot, null, "V2G root");

            var chosen = new JObject(
                             new JProperty("vehicleCertificate",   Import(Vehicle, Certificates.VehicleBundleFile,  CertificateKind.Vehicle,            Certificates.Password, "Vehicle certificate")),
                             new JProperty("contractCertificate",  Import(Vehicle, Certificates.ContractBundleFile, CertificateKind.Contract,           Certificates.Password, "contract certificate")),
                             new JProperty("oemCertificate",       Import(Vehicle, Certificates.OEMBundleFile,      CertificateKind.OEMProvisioning,    Certificates.Password, "OEM provisioning certificate")),
                             new JProperty("tariffCertificate",    Import(Vehicle, Certificates.TariffBundleFile,   CertificateKind.TariffVerification, Certificates.Password, "tariff verification key"))
                         );

            if (!Vehicle.TryUpdateSessionConfiguration(chosen, out var error))
                throw new InvalidOperationException($"The vehicle could not be told which certificates to use: {error}");

            Console.Say(
                "env",
                $"Vehicle: {Vehicle.Certificates.Entries.Count} certificates in its store at {Vehicle.Certificates.Directory} - " +
                 "both V2G roots, and the Vehicle, contract, OEM and tariff certificates of this PKI, chosen by handle. " +
                 "No MO or OEM root: each hierarchy here has one root above everything, and a certificate has one purpose there."
            );

        }

        /// <summary>
        /// One file into the vehicle's store, as the handle it became.
        /// </summary>
        private static String Import(Vehicle          Vehicle,
                                     String           File,
                                     CertificateKind  Kind,
                                     String?          Password,
                                     String           What)
        {

            Byte[] content;

            try
            {
                content = System.IO.File.ReadAllBytes(File);
            }
            catch (Exception e)
            {
                throw new InvalidOperationException($"The vehicle's {What} could not be read from '{File}': {e.Message}", e);
            }

            if (!Vehicle.Certificates.Import(content, Kind, Password, null, out var entry, out var error))
                throw new InvalidOperationException($"The vehicle would not take its {What} from '{File}': {error}");

            return entry.Id;

        }

        #endregion

        #region (private) EnsureServerCertificate(Store, Subject, ReachableAs, Whose)

        /// <summary>
        /// A key, a signing request, a certificate signed by this environment's
        /// OCPP server CA, and the answer to the one question that decides
        /// whether the port can be encrypted at all.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The question is whether .NET can build the chain of this certificate
        /// on <em>this</em> machine. It insists on doing that before a server
        /// may present one, and a private authority whose root is in no machine
        /// store fails it - which shows up as a TLS handshake reset with
        /// nothing said, at every charging station, with the port reporting
        /// itself as running and encrypted.
        /// </para>
        /// <para>
        /// So it is asked here rather than found out in a handshake, and the
        /// answer is what the console says and what the OCPP security profile
        /// is chosen for. Putting a root into the machine's certificate store
        /// is not something a test environment should do to somebody's
        /// computer while they are looking the other way.
        /// </para>
        /// </remarks>
        private Boolean EnsureServerCertificate(CSMSStore.ServerCertificateStore  Store,
                                                String                            Subject,
                                                IEnumerable<String>               ReachableAs,
                                                String                            Whose)
        {

            if (!Store.HasCertificate)
            {

                if (!Store.TryCreateKey(Subject, ReachableAs, null, out _, out var csr, out var keyError))
                    throw new InvalidOperationException($"{Whose}: a server key could not be made: {keyError}");

                if (!Store.TryAddCertificate(Certificates.OCPP.SignServer(csr), ReachableAs, out _, out var warnings, out var certError))
                    throw new InvalidOperationException($"{Whose}: the signed server certificate was refused: {certError}");

                foreach (var warning in warnings)
                    Console.Say(Whose, $"Server certificate: {warning}");

            }

            return Store.Select()?.TryCreateContext(out _, out _) == true;

        }

        #endregion

        #region (private) EnsureServerCertificate(Store, ...) - the local controller's store, which is a different type

        /// <summary>
        /// The same again for the local controller, whose certificate store is
        /// the same forty lines in a namespace of its own.
        /// </summary>
        private Boolean EnsureServerCertificate(ControlStore.ServerCertificateStore  Store,
                                                String                               Subject,
                                                IEnumerable<String>                  ReachableAs,
                                                String                               Whose)
        {

            if (!Store.HasCertificate)
            {

                if (!Store.TryCreateKey(Subject, ReachableAs, null, out _, out var csr, out var keyError))
                    throw new InvalidOperationException($"{Whose}: a server key could not be made: {keyError}");

                if (!Store.TryAddCertificate(Certificates.OCPP.SignServer(csr), ReachableAs, out _, out var warnings, out var certError))
                    throw new InvalidOperationException($"{Whose}: the signed server certificate was refused: {certError}");

                foreach (var warning in warnings)
                    Console.Say(Whose, $"Server certificate: {warning}");

            }

            return Store.Select()?.TryCreateContext(out _, out _) == true;

        }

        #endregion

        #region (private) EnsureTrustAnchor(Store, Whose)

        /// <summary>
        /// Whose client certificates this end may believe: this environment's
        /// OCPP root, and nothing else.
        /// </summary>
        private void EnsureTrustAnchor(CSMSStore.ClientTrustStore Store, String Whose)
        {

            if (Store.Entries.Count > 0)
                return;

            if (!Store.TryAdd(Certificates.OCPP.RootTrustPEM, "EV Charging Test Environment OCPP Root", out _, out var warnings, out var error))
                throw new InvalidOperationException($"{Whose}: the OCPP root was refused as a trust anchor: {error}");

            foreach (var warning in warnings)
                Console.Say(Whose, $"Client trust: {warning}");

        }

        /// <summary>The same again for the local controller's store.</summary>
        private void EnsureTrustAnchor(ControlStore.ClientTrustStore Store, String Whose)
        {

            if (Store.Entries.Count > 0)
                return;

            if (!Store.TryAdd(Certificates.OCPP.RootTrustPEM, "EV Charging Test Environment OCPP Root", out _, out var warnings, out var error))
                throw new InvalidOperationException($"{Whose}: the OCPP root was refused as a trust anchor: {error}");

            foreach (var warning in warnings)
                Console.Say(Whose, $"Client trust: {warning}");

        }

        #endregion

        #region (private) EnsureClientCertificate()

        /// <summary>
        /// What the station dials out with under OCPP security profile 3.
        /// </summary>
        /// <remarks>
        /// Minted whether or not the connection below is going to use it. It
        /// costs one signature at the first start, it appears in the station's
        /// web interface where somebody can look at it, and it means that
        /// turning security profile 3 on is a change to one field rather than
        /// an afternoon with a certificate authority.
        /// </remarks>
        private void EnsureClientCertificate()
        {

            if (Station.ClientCertificates.InUse is not null)
                return;

            if (!Station.ClientCertificates.TryCreateKey(
                     $"CN={StationNodeId}, O=EV Charging Test Environment, C=DE",
                     null,
                     out _,
                     out var csr,
                     out var keyError))
            {
                throw new InvalidOperationException($"The station's client key could not be made: {keyError}");
            }

            if (!Station.ClientCertificates.TryAddCertificate(
                     Certificates.OCPP.SignClient(csr),
                     out _,
                     out var warnings,
                     out var certError))
            {
                throw new InvalidOperationException($"The station's signed client certificate was refused: {certError}");
            }

            foreach (var warning in warnings)
                Console.Say("station", $"Client certificate: {warning}");

        }

        #endregion

        #region (private) AllowStation(Logins, Id, Secret, Whose)

        /// <summary>
        /// Write down who may sign in at an OCPP port, and with what.
        /// </summary>
        private void AllowStation(CSMSStore.ChargingStationLogins  Logins,
                                  String                           Id,
                                  String                           Secret,
                                  String                           Whose)
        {

            // Every method and every security profile, which is what a test
            // environment is for: what is actually used is decided by the
            // connection at the other end, and a group that allowed only what
            // is used today would have to be edited before anything else could
            // be tried.
            if (!Logins.TryAddOrUpdateGroup(
                     LoginGroup,
                     "Everything in this test environment",
                     Enabled:           true,
                     AuthMethods:       [ CSMSStore.AuthMethod.Basic, CSMSStore.AuthMethod.TOTP, CSMSStore.AuthMethod.Certificate ],
                     SecurityProfiles:  [ (Byte) 1, (Byte) 2, (Byte) 3 ],
                     Note:              "Made by the test environment",
                     out var groupError))
            {
                throw new InvalidOperationException($"{Whose}: the login group could not be made: {groupError}");
            }

            if (!Logins.TrySetPassword(Id, Secret, LoginGroup, "Set up by the test environment", out _, out var error))
                throw new InvalidOperationException($"{Whose}: '{Id}' could not be allowed in: {error}");

        }

        /// <summary>The same again for the local controller's store.</summary>
        private void AllowStation(ControlStore.ChargingStationLogins  Logins,
                                  String                              Id,
                                  String                              Secret,
                                  String                              Whose)
        {

            if (!Logins.TryAddOrUpdateGroup(
                     LoginGroup,
                     "Everything in this test environment",
                     Enabled:           true,
                     AuthMethods:       [ ControlStore.AuthMethod.Basic, ControlStore.AuthMethod.TOTP, ControlStore.AuthMethod.Certificate ],
                     SecurityProfiles:  [ (Byte) 1, (Byte) 2, (Byte) 3 ],
                     Note:              "Made by the test environment",
                     out var groupError))
            {
                throw new InvalidOperationException($"{Whose}: the login group could not be made: {groupError}");
            }

            if (!Logins.TrySetPassword(Id, Secret, LoginGroup, "Set up by the test environment", out _, out var error))
                throw new InvalidOperationException($"{Whose}: '{Id}' could not be allowed in: {error}");

        }

        #endregion

        #region (private) DialUpwards(Secret)

        /// <summary>
        /// The one connection the station makes by itself when it starts: to
        /// the local controller where there is one, and to the CSMS where
        /// there is not.
        /// </summary>
        /// <remarks>
        /// Replaced rather than added to at every start. The station keeps its
        /// connections between starts, and a second environment started with
        /// <c>--base-port</c> would otherwise leave the station dialling a port
        /// that is no longer anybody's.
        /// </remarks>
        private void DialUpwards(String Secret)
        {

            var description = Controller is not null
                                  ? "The local controller of this test environment"
                                  : "The CSMS of this test environment";

            var url         = Controller is not null
                                  ? $"{options.OCPPScheme}://{options.Host}:{options.ControllerOCPPPort}"
                                  : $"{options.OCPPScheme}://{options.Host}:{options.CSMSOCPPPort}";

            foreach (var existing in Station.Connections.Connections.
                                             Where(one => one.Description == description).
                                             ToArray())
            {
                Station.Connections.TryRemoveConnection(existing.Id, out _);
            }

            foreach (var existing in Station.Connections.Authentications.
                                             Where(one => one.Description == description).
                                             ToArray())
            {
                Station.Connections.TryRemoveAuthentication(existing.Id, out _);
            }

            if (!Station.Connections.TryAddAuthentication(
                     description,
                     "basic",
                     StationNodeId,
                     Secret,
                     out var authenticationId,
                     out var authenticationError))
            {
                throw new InvalidOperationException($"The station's credentials could not be written down: {authenticationError}");
            }

            if (!Station.Connections.TryAddConnection(
                     description,
                     url,
                     Controller is not null ? "localcontroller" : "csms",
                     AutoConnect:       true,
                     AuthenticationId:  authenticationId,
                     CertificateId:     null,
                     out _,
                     out var connectionError))
            {
                throw new InvalidOperationException($"The station could not be told where to dial: {connectionError}");
            }

        }

        #endregion

        #region (private) InstallRootCommand()

        /// <summary>
        /// What somebody has to type, on this operating system, to make this
        /// machine believe the OCPP root.
        /// </summary>
        /// <remarks>
        /// The user's own store on Windows rather than the machine's, because
        /// it needs no administrator and .NET's chain building reads both. The
        /// other two want a privileged copy into a system directory, so those
        /// commands are given with what they need rather than without.
        /// </remarks>
        private String InstallRootCommand()
        {

            var root = Certificates.OCPPRootTrustFile;

            if (OperatingSystem.IsWindows())
                return $"certutil -addstore -user Root \"{root}\"";

            if (OperatingSystem.IsMacOS())
                return $"sudo security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain \"{root}\"";

            return $"sudo cp \"{root}\" /usr/local/share/ca-certificates/ocpp-test-root.crt && sudo update-ca-certificates";

        }

        #endregion

        #region (private static) NewSecret()

        /// <summary>
        /// One OCPP password: 32 characters of Base64Url, which is what the
        /// specification asks for and what the stores below generate when they
        /// are left to make one up themselves.
        /// </summary>
        private static String NewSecret()

            => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).
                       Replace('+', '-').
                       Replace('/', '_').
                       TrimEnd('=');

        #endregion

    }

}
