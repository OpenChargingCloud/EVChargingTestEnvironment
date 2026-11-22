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

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Hermod.SSH;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.SunSpecModbusTLS.Common;

using cloud.charging.open.protocols.WWCP.Node.Modbus;
using cloud.charging.open.protocols.WWCP.Node.TestKit;
using cloud.charging.open.protocols.WWCP.Node.Connections;
using cloud.charging.open.protocols.WWCP.Node.Configuration;

using cloud.charging.open.EnergyMeters.ModbusTLS;
using cloud.charging.open.EnergyMeters.ModbusTLS.Certificates;
using cloud.charging.open.EnergyMeters.ModbusTLS.MeterReaders;

using WWCPNode       = cloud.charging.open.protocols.WWCP.Node.WWCPNode;
using NetIPAddress   = System.Net.IPAddress;

#endregion

namespace cloud.charging.open.TestEnvironment.Tests.Connections.HopByHop
{

    /// <summary>
    /// A node that reads an energy meter - a charging station, a local controller -
    /// connects to it over ws://, wss://, wss:// with a client certificate and
    /// ssh://, and reads and writes its registers over Modbus there: what it may do
    /// is the SunSpec role of the group of its login at the meter.
    /// </summary>
    /// <remarks>
    /// The same for every node that reads a meter: the node is the kind's to make
    /// (<see cref="NewReader"/>), and everything else is here.
    /// </remarks>
    public abstract class AToTheEnergyMeter : AHopByHopTest
    {

        #region Data

        /// <summary>
        /// The register of the meter mode: commanded - GridService and above may write it.
        /// </summary>
        protected const UInt16  MeterModeRegister   = 40094;

        /// <summary>
        /// The register of the unit address: protected - NetworkAdministrator and above may write it.
        /// </summary>
        protected const UInt16  UnitAddressRegister = 40068;

        /// <summary>
        /// Where a SunSpec device begins: "SunS".
        /// </summary>
        protected const UInt16  SunSAddress         = 40000;

        protected DNSClient?             dnsClient;
        protected WWCPNode?              reader;
        protected ModbusTLSEnergyMeter?  meter;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public async Task Start()
        {

            dnsClient  = new DNSClient(SearchForIPv6DNSServers: false);

            meter      = NewMeter();

            await meter.Start();

            reader     = await NewReader(dnsClient);

        }

        [TearDown]
        public async Task Stop()
        {

            if (reader is not null)
            {
                await reader.Stop();
                await reader.DisposeAsync();
            }

            if (meter is not null)
            {
                await meter.Stop();
                await meter.DisposeAsync();
            }

        }

        #endregion


        #region (protected abstract) NewReader(DNSClient) / ReaderName

        /// <summary>
        /// The node that reads the meter, started: a charging station, a local controller.
        /// </summary>
        protected abstract Task<WWCPNode> NewReader(DNSClient DNSClient);

        /// <summary>
        /// What the node that reads the meter is called in a description.
        /// </summary>
        protected abstract String ReaderName { get; }

        #endregion

        #region (private) NewMeter()

        /// <summary>
        /// An energy meter of its own directory: its Modbus/TLS port on a port of
        /// its own, its WebSocket port of Modbus switched on - over TLS, with a
        /// certificate of the test's CA, where the test connects over wss:// -
        /// and its SSH server, serving the subsystem of Modbus.
        /// </summary>
        private ModbusTLSEnergyMeter NewMeter()
        {

            var directory  = NewNodeDirectory(
                                 "em",
                                 new JProperty("webSocketServer", new JObject(
                                     new JProperty("enabled",               true),
                                     new JProperty("address",               "127.0.0.1"),
                                     new JProperty("port",                  TestPorts.Free().ToUInt16()),
                                     new JProperty("clientAuthentication",  new JArray(OverMTLS
                                                                                          ? "tlsClientCertificate"
                                                                                          : OverTLS
                                                                                                ? "httpAuthOverTLS"
                                                                                                : "httpAuthWithoutTLS")),
                                     new JProperty("ssh",                   new JObject(
                                         new JProperty("enabled",           true)
                                     ))
                                 )),
                                 new JProperty("ssh", new JObject(
                                     new JProperty("enabled",  true),
                                     new JProperty("port",     TestPorts.Free().ToUInt16())
                                 ))
                             );

            var dataPath   = Path.GetDirectoryName(directory.ConfigFile.Path)!;

            if (OverTLS)
                ACertificateOfTheTestCA(dataPath, MeterListeners.WebSocket);

            var (serverPfx, clientCA) = ModbusTLSFiles(dataPath);

            return new ModbusTLSEnergyMeter(
                       SerialNumber:       "EM-" + RandomExtensions.RandomString(6),
                       ServerPfxPath:      serverPfx,
                       ServerPfxPassword:  "test",
                       ClientCACertPath:   clientCA,
                       ListenAddress:      NetIPAddress.Loopback,
                       ListenPort:         TestPorts.Free(),
                       HTTPHostname:       IPv4Address.Localhost,
                       HTTPPort:           TestPorts.Free(),
                       DataPath:           dataPath,
                       ConfigFile:         directory.ConfigFile,
                       DNSClient:          dnsClient,
                       LogKeepDays:        0,
                       LogToConsole:       false,
                       BridgeDebugLog:     false
                   );

        }

        #endregion

        #region (private static) ModbusTLSFiles(Directory)

        /// <summary>
        /// What the Modbus/TLS port of a meter is started with: a certificate for
        /// 127.0.0.1 with its key, and a CA its clients are issued by - of a CA of
        /// its own, as nothing here speaks Modbus/TLS.
        /// </summary>
        private static (String ServerPfx, String ClientCA) ModbusTLSFiles(String Directory)
        {

            using var caKey       = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            var caRequest         = new CertificateRequest($"CN=The Modbus/TLS CA of a test {Guid.NewGuid()}", caKey, HashAlgorithmName.SHA256);
            caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));

            using var ca          = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

            using var serverKey   = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            var serverRequest     = new CertificateRequest("CN=127.0.0.1", serverKey, HashAlgorithmName.SHA256);
            serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ new Oid("1.3.6.1.5.5.7.3.1") ], false));

            var names             = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(NetIPAddress.Loopback);
            serverRequest.CertificateExtensions.Add(names.Build());

            using var server      = serverRequest.Create(ca.SubjectName,
                                                         X509SignatureGenerator.CreateForECDsa(caKey),
                                                         DateTimeOffset.UtcNow.AddDays(-1),
                                                         DateTimeOffset.UtcNow.AddDays(29),
                                                         Guid.NewGuid().ToByteArray());

            using var withKey     = server.CopyWithPrivateKey(serverKey);

            var pki               = Path.Combine(Directory, "pki");
            System.IO.Directory.CreateDirectory(pki);

            var serverPfx         = Path.Combine(pki, "server.pfx");
            var clientCA          = Path.Combine(pki, "clients-ca.crt");

            File.WriteAllBytes(serverPfx, withKey.Export(X509ContentType.Pkcs12, "test"));
            File.WriteAllText (clientCA,  ca.ExportCertificatePem());

            return (serverPfx, clientCA);

        }

        #endregion


        #region (protected) TheReaderConnects(Authentication, What, Role = ReadOnly)

        /// <summary>
        /// The meter writes the node down as what reads it, and the node connects
        /// to the meter as to an energy meter - over a WebSocket of Modbus with a
        /// password, a one-time password or a certificate, or over SSH with a
        /// password, a one-time password or its own key - and both say it is
        /// connected. Its login is in the group of the given role, or in none.
        /// </summary>
        /// <param name="Authentication">How the node signs in: "password", "totp", "publicKey" - or "mTLS".</param>
        /// <param name="Transport">"ws", "wss" or "ssh".</param>
        /// <param name="Role">The SunSpec role its login has at the meter; null for none.</param>
        /// <returns>The login, its connection at the node, and the connection the meter wrote down for it.</returns>
        protected async Task<(User_Id Login, String Connection, String Inbound)> TheReaderConnects(String   Authentication,
                                                                                                   String   Transport,
                                                                                                   String?  Role = SunSpecRoles.ReadOnly)
        {

            var reader       = this.reader!;
            var meter        = this.meter!;

            var login        = User_Id.Parse("reader-" + RandomExtensions.RandomString(8));
            var description  = I18NString.Create($"{ReaderName} --({Transport}/{Authentication})--> {meter.SerialNumber}");
            var group        = Role is not null ? SunSpecLoginGroups.GroupOf(Role) : null;
            var error        = "";

            var door         = Transport == "ssh"
                                   ? $"ssh://{login}@127.0.0.1:{meter.SSHPort!.Value.ToUInt16()}"
                                   : $"{Transport}://127.0.0.1:{meter.MeterReaderSettings.TCPPort!.Value.ToUInt16()}/{login}";

            if (Transport == "ssh")
                Assert.That(meter.ModbusOverSSHServed, Is.True, "Modbus over SSH is switched on in the file, and not served.");
            else
                Assert.That(meter.MeterReaderServerRunning, Is.True, "The WebSocket server of Modbus is switched on in the file, and not running.");

            if (Transport == "wss")
                Assert.That(meter.MeterReaderServerTLS, Is.True, "The WebSocket port of Modbus of the meter does not speak TLS.");

            BelievesTheTestCA(reader);

            // The meter: the node, written down as what reads it.
            Assert.That(
                meter.Connections.TryAddConnection(
                     I18NString.Create($"{ReaderName}, reading this meter"),
                     URL.Parse(door),
                     "MeterReader",
                     true,
                     null,
                     null,
                     out var inbound,
                     out error,
                     Inbound: true
                ),
                Is.True, error
            );

            // What the node signs in with: a password, the secret of one-time
            // passwords, its own key, or a certificate that names its login.
            String? credentials  = null;
            String? certificate  = null;

            switch (Authentication)
            {

                case authPublicKey:

                    var key = await reader.SshKey.MakeIfNeeded();

                    Assert.That(
                        meter.TryAddMeterReaderSSHKey(
                            Login:        login,
                            Line:         SshPublicKey.FromHostKey(key).ToAuthorizedKeyLine(),
                            Label:        "test",
                            GroupId:      group,
                            Description:  description,
                            CreatedBy:    "tests",
                            Key:          out _,
                            Error:        out error,
                            NotSaved:     out _
                        ),
                        Is.True, error
                    );

                    break;

                case authMTLS:

                    // A login of the name the certificate names, for its group: a
                    // certificate says who it is, and the group what it may do.
                    Assert.That(meter.WebSocketLogins.TrySetPasswordAuthentication(login, RandomExtensions.RandomString(32), group, description, out error), Is.True, error);

                    certificate = ClientCertificate(reader, meter, login.ToString());

                    break;

                default:

                    credentials = Credentials(reader, meter, login, Authentication, description);

                    if (group is not null)
                        Assert.That(meter.WebSocketLogins.TrySetGroup(login, group, out error), Is.True, error);

                    break;

            }

            // The node: the connection to the meter, and what it signs in with.
            Assert.That(
                reader.Connections.TryAddConnection(
                    Description:        description,
                    URL:                URL.Parse(door),
                    ConnectionType:     "EnergyMeter",
                    AutoConnect:        true,
                    AuthenticationId:   credentials,
                    CertificateId:      certificate,
                    Id:                 out var connection,
                    Error:              out error,
                    SshHostKey:         Transport == "ssh" ? meter.SSHHostKey!.Split(' ', 2)[1] : null
                ),
                Is.True, error
            );

            await Until(() => StatusOf(reader, connection!) == ConnectionStatus.Connected,  $"'{login}' connecting over {Transport}",  reader, meter);
            await Until(() => StatusOf(meter,  inbound!)    == ConnectionStatus.Connected,  $"'{login}' being let in by the meter",    reader, meter);

            return (login, connection!, inbound!);

        }

        #endregion

        #region (protected) AssertConnected(Login, Connection, Inbound, Transport)

        /// <summary>
        /// Both ends see the connection as up, over Modbus: the node connected
        /// over the given transport, and the meter let the login in - and the
        /// node reads "SunS" where a SunSpec device begins.
        /// </summary>
        protected async Task AssertConnected(User_Id  Login,
                                             String   Connection,
                                             String   Inbound,
                                             String   Transport)
        {

            var reader  = this.reader!;
            var meter   = this.meter!;

            var entry   = reader.Connections.Connections.First(connection => connection.Id == Connection);
            var suns    = await reader.ReadMeterRegisters(Connection, SunSAddress, 2);

            Assert.Multiple(() => {

                Assert.That(StatusOf(reader, Connection),            Is.EqualTo(ConnectionStatus.Connected),  $"The node does not see the connection as up.{Story(reader, meter)}");
                Assert.That(StatusOf(meter,  Inbound),               Is.EqualTo(ConnectionStatus.Connected),  $"The meter does not see the connection as up.{Story(reader, meter)}");
                Assert.That(IsConnectedAt(meter, Login),             Is.True,                                 $"The meter did not let '{Login}' in.{Story(reader, meter)}");

                Assert.That(entry.Use,                               Is.EqualTo(ConnectionUse.Modbus));
                Assert.That(reader.MeterTransportOf(Connection),     Is.EqualTo(Transport switch {
                                                                                    "ssh"  => "SSH",
                                                                                    "wss"  => "WebSocket TLS",
                                                                                    _      => "WebSocket"
                                                                                }));

                Assert.That(suns,                                    Is.EqualTo(new UInt16[] { 0x5375, 0x6e53 }), "The node did not read \"SunS\" where a SunSpec device begins.");

                Assert.That(meter.ModbusConnections.Any(connection => connection.Login == Login.ToString()),
                            Is.True,
                            "The meter does not list the connection among its Modbus connections.");

            });

        }

        #endregion

        #region (protected) ExceptionOf(Request)

        /// <summary>
        /// The Modbus exception the given request is answered with, or null where
        /// it is answered.
        /// </summary>
        protected static async Task<ModbusExceptionCode?> ExceptionOf(Func<Task> Request)
        {
            try
            {
                await Request();
                return null;
            }
            catch (ModbusException e)
            {
                return e.Code;
            }
        }

        #endregion


        #region WebSocket     (Authentication)

        /// <summary>
        /// The node connects to the meter over a WebSocket of Modbus, with a
        /// password or a one-time password, and reads it.
        /// </summary>
        [TestCase(authPassword)]
        [TestCase(authTOTP)]
        public async Task WebSocket(String Authentication)
        {

            var (login, connection, inbound) = await TheReaderConnects(Authentication, "ws");

            await AssertConnected(login, connection, inbound, "ws");

        }

        #endregion

        #region WebSocketTLS  (Authentication)

        /// <summary>
        /// The node connects to the meter over a WebSocket of Modbus over TLS,
        /// with a password or a one-time password, believing the meter by the
        /// test's CA, and reads it.
        /// </summary>
        [TestCase(authPassword)]
        [TestCase(authTOTP)]
        public async Task WebSocketTLS(String Authentication)
        {

            var (login, connection, inbound) = await TheReaderConnects(Authentication, "wss");

            await AssertConnected(login, connection, inbound, "wss");

        }

        #endregion

        #region WebSocketMTLS ()

        /// <summary>
        /// The node connects to the meter over a WebSocket of Modbus over mutual
        /// TLS: it signs in with a certificate of the test's CA that names its
        /// login, whose group says its role.
        /// </summary>
        [Test]
        public async Task WebSocketMTLS()
        {

            var (login, connection, inbound) = await TheReaderConnects(authMTLS, "wss");

            await AssertConnected(login, connection, inbound, "wss");

        }

        #endregion

        #region SSH           (Authentication)

        /// <summary>
        /// The node connects to the subsystem of Modbus of the meter's SSH server,
        /// pinning the key the server shows - with its password, with a one-time
        /// password, or with its own key - and reads it.
        /// </summary>
        [TestCase(authPassword)]
        [TestCase(authTOTP)]
        [TestCase(authPublicKey)]
        public async Task SSH(String Authentication)
        {

            var (login, connection, inbound) = await TheReaderConnects(Authentication, "ssh");

            await AssertConnected(login, connection, inbound, "ssh");

        }

        #endregion


        #region Modbus_EveryRoleReads(Transport, Role)

        /// <summary>
        /// Every SunSpec role reads the meter, over SSH as over a WebSocket.
        /// </summary>
        [TestCase("ssh", SunSpecRoles.ReadOnly)]
        [TestCase("ssh", SunSpecRoles.GridService)]
        [TestCase("ssh", SunSpecRoles.NetworkAdministrator)]
        [TestCase("ssh", SunSpecRoles.SuperAdministrator)]
        [TestCase("ws",  SunSpecRoles.ReadOnly)]
        [TestCase("ws",  SunSpecRoles.GridService)]
        [TestCase("ws",  SunSpecRoles.NetworkAdministrator)]
        [TestCase("ws",  SunSpecRoles.SuperAdministrator)]
        public async Task Modbus_EveryRoleReads(String  Transport,
                                                String  Role)
        {

            var (_, connection, _) = await TheReaderConnects(authPassword, Transport, Role);

            var registers = await reader!.ReadMeterRegisters(connection, MeterModeRegister, 1);

            Assert.That(registers, Has.Length.EqualTo(1), $"{Role} did not read the meter mode.");

        }

        #endregion

        #region Modbus_TheMeterMode_IsCommanded(Transport, Role, Allowed)

        /// <summary>
        /// The meter mode is commanded: GridService and above write it, and
        /// ReadOnly is answered with exception 01.
        /// </summary>
        [TestCase("ssh", SunSpecRoles.ReadOnly,              false)]
        [TestCase("ssh", SunSpecRoles.GridService,           true)]
        [TestCase("ssh", SunSpecRoles.NetworkAdministrator,  true)]
        [TestCase("ssh", SunSpecRoles.SuperAdministrator,    true)]
        [TestCase("ws",  SunSpecRoles.ReadOnly,              false)]
        [TestCase("ws",  SunSpecRoles.GridService,           true)]
        [TestCase("ws",  SunSpecRoles.NetworkAdministrator,  true)]
        [TestCase("ws",  SunSpecRoles.SuperAdministrator,    true)]
        public async Task Modbus_TheMeterMode_IsCommanded(String   Transport,
                                                          String   Role,
                                                          Boolean  Allowed)
        {

            var (_, connection, _) = await TheReaderConnects(authPassword, Transport, Role);

            var mode       = (await reader!.ReadMeterRegisters(connection, MeterModeRegister, 1))[0];
            var exception  = await ExceptionOf(() => reader!.WriteMeterRegisters(connection, MeterModeRegister, [ mode ]));

            Assert.That(exception,
                        Allowed ? Is.Null : Is.EqualTo(ModbusExceptionCode.IllegalFunction),
                        $"{Role} writing the meter mode.{Story(reader, meter)}");

        }

        #endregion

        #region Modbus_TheUnitAddress_IsProtected(Transport, Role, Allowed)

        /// <summary>
        /// The unit address is protected: NetworkAdministrator and
        /// SuperAdministrator write it, and GridService and ReadOnly are answered
        /// with exception 01.
        /// </summary>
        [TestCase("ssh", SunSpecRoles.ReadOnly,              false)]
        [TestCase("ssh", SunSpecRoles.GridService,           false)]
        [TestCase("ssh", SunSpecRoles.NetworkAdministrator,  true)]
        [TestCase("ssh", SunSpecRoles.SuperAdministrator,    true)]
        [TestCase("ws",  SunSpecRoles.ReadOnly,              false)]
        [TestCase("ws",  SunSpecRoles.GridService,           false)]
        [TestCase("ws",  SunSpecRoles.NetworkAdministrator,  true)]
        [TestCase("ws",  SunSpecRoles.SuperAdministrator,    true)]
        public async Task Modbus_TheUnitAddress_IsProtected(String   Transport,
                                                            String   Role,
                                                            Boolean  Allowed)
        {

            var (_, connection, _) = await TheReaderConnects(authPassword, Transport, Role);

            var unit       = (await reader!.ReadMeterRegisters(connection, UnitAddressRegister, 1))[0];
            var exception  = await ExceptionOf(() => reader!.WriteMeterRegisters(connection, UnitAddressRegister, [ unit ]));

            Assert.That(exception,
                        Allowed ? Is.Null : Is.EqualTo(ModbusExceptionCode.IllegalFunction),
                        $"{Role} writing the unit address.{Story(reader, meter)}");

        }

        #endregion

        #region Modbus_WithoutARole_NothingIsAnswered(Transport)

        /// <summary>
        /// A login in no group of a role - the default group - is let in, and
        /// answered with exception 01 to everything, as a certificate without a
        /// role is over Modbus/TLS.
        /// </summary>
        [TestCase("ssh")]
        [TestCase("ws")]
        public async Task Modbus_WithoutARole_NothingIsAnswered(String Transport)
        {

            var (_, connection, _) = await TheReaderConnects(authPassword, Transport, Role: null);

            var read   = await ExceptionOf(() => reader!.ReadMeterRegisters (connection, SunSAddress, 2));
            var write  = await ExceptionOf(() => reader!.WriteMeterRegisters(connection, MeterModeRegister, [ 0 ]));

            Assert.Multiple(() => {
                Assert.That(read,   Is.EqualTo(ModbusExceptionCode.IllegalFunction), "A login without a role read the meter.");
                Assert.That(write,  Is.EqualTo(ModbusExceptionCode.IllegalFunction), "A login without a role wrote the meter mode.");
            });

        }

        #endregion

        #region Modbus_AnotherGroup_IsAnotherRole_AtTheNextRequest(Transport)

        /// <summary>
        /// A login put into another group has the role of that one at its next
        /// request, on the connection it is in over already.
        /// </summary>
        [TestCase("ssh")]
        [TestCase("ws")]
        public async Task Modbus_AnotherGroup_IsAnotherRole_AtTheNextRequest(String Transport)
        {

            var (login, connection, _) = await TheReaderConnects(authPassword, Transport, SunSpecRoles.ReadOnly);

            var mode    = (await reader!.ReadMeterRegisters(connection, MeterModeRegister, 1))[0];
            var before  = await ExceptionOf(() => reader!.WriteMeterRegisters(connection, MeterModeRegister, [ mode ]));

            Assert.That(meter!.WebSocketLogins.TrySetGroup(login, SunSpecLoginGroups.GroupOf(SunSpecRoles.GridService), out var error), Is.True, error);

            var after   = await ExceptionOf(() => reader!.WriteMeterRegisters(connection, MeterModeRegister, [ mode ]));

            Assert.Multiple(() => {
                Assert.That(before,                             Is.EqualTo(ModbusExceptionCode.IllegalFunction), "ReadOnly wrote the meter mode.");
                Assert.That(after,                              Is.Null,                                          $"GridService did not write the meter mode.{Story(reader, meter)}");
                Assert.That(reader!.MeterConnectionIsUp(connection), Is.True,                                     "The connection did not stay up.");
                Assert.That(meter!.ModbusConnections.First(one => one.Login == login.ToString()).Role,
                            Is.EqualTo(SunSpecRoles.GridService),
                            "The meter does not say the connection has the role of its new group.");
            });

        }

        #endregion

    }

}
