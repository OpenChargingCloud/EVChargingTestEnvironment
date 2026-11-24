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

using System.Collections.Concurrent;
using System.Security.Cryptography;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Hermod.SSH;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.protocols.WWCP;
using cloud.charging.open.protocols.WWCP.NetworkingNode;
using cloud.charging.open.protocols.WWCP.Node;
using cloud.charging.open.protocols.WWCP.Node.TestKit;
using cloud.charging.open.protocols.WWCP.Node.SecureShell;
using cloud.charging.open.protocols.WWCP.Node.Connections;
using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.OCPP.Node;
using cloud.charging.open.protocols.OCPPv2_1;
using cloud.charging.open.protocols.OCPPv2_1.CS;
using cloud.charging.open.protocols.OCPPv2_1.CSMS;

using cloud.charging.open.DownloadServer;

#endregion

namespace cloud.charging.open.TestEnvironment.Tests.Connections.HopByHop
{

    /// <summary>
    /// A node that updates its firmware - a charging station, a local
    /// controller - is told to by its CSMS with an UpdateFirmware, and fetches
    /// the image from the download server: with a GET over HTTP or HTTPS, or
    /// over SFTP. It goes only to a server its own configuration names for
    /// downloads - never to one the request introduces, and never where a
    /// redirect would lead -, signs in with the login the URL of the request
    /// carries, or else with the one its configuration says, believes an
    /// HTTPS server by the roots it keeps for its connections and an SFTP
    /// server by its pinned host key, takes up with a Range where the attempt
    /// before stopped, tries again only what may be better a moment later,
    /// takes no image larger than it may hold, and tells the CSMS how it went
    /// with FirmwareStatusNotifications - up to Downloaded, which is as far as
    /// the transport goes.
    /// </summary>
    /// <remarks>
    /// The same for every node that updates its firmware: the node is the
    /// kind's to make (<see cref="AFileTransferTest.NewNode"/>), and
    /// everything else is here. What a download server does not do on purpose
    /// - break a body off, fail, redirect, claim a size it does not have, or
    /// take no ranges - a file server of the test's own does
    /// (<see cref="TestFileServer"/>).
    /// </remarks>
    public abstract class AToTheDownloadServer : AFileTransferTest
    {

        #region Data

        /// <summary>
        /// The name of the image.
        /// </summary>
        protected const String  ImageName   = "station-2.1.0.bin";

        /// <summary>
        /// Where the image lies at a firmware server: in the download server's
        /// area "firmware".
        /// </summary>
        protected const String  ImagePath   = "/firmware/" + ImageName;

        /// <summary>
        /// More than one buffer of a download.
        /// </summary>
        protected static readonly Byte[]  Image  = RandomNumberGenerator.GetBytes(300_000);

        protected DownloadServerNode?  downloadServer;
        protected String               downloaderPassword  = "";

        private readonly ConcurrentQueue<(Int64? RequestId, FirmwareStatus Status)>  told        = new();
        private          Int32                                                       requestIds  = 200;

        #endregion

        #region (protected abstract) MaxDownloadSize

        /// <summary>
        /// The most an image may have that the kind of node takes, where its
        /// configuration file does not say otherwise.
        /// </summary>
        protected abstract Int64 MaxDownloadSize { get; }

        #endregion

        #region HearsOf(CSMS) / TearDown

        /// <summary>
        /// What the node tells of its downloads.
        /// </summary>
        protected override void HearsOf(CSMS.CSMS CSMS)
        {

            told.Clear();

            CSMS.Node.OCPP.IN.OnFirmwareStatusNotificationRequestReceived += (timestamp, sender, connection, request, cancellationToken) => {
                told.Enqueue((request.UpdateFirmwareRequestId, request.Status));
                return Task.CompletedTask;
            };

        }

        [TearDown]
        public async Task StopTheDownloadServer()
        {

            if (downloadServer is not null)
            {
                await downloadServer.Stop();
                await downloadServer.DisposeAsync();
                downloadServer = null;
            }

        }

        #endregion


        #region (protected) ADownloadServer(Secure = false, Anonymous = false)

        /// <summary>
        /// The download server, of its own directory: HTTP downloads on
        /// 127.0.0.1 and a port of its own - over TLS, with a certificate of the
        /// test's CA, where so; without credentials as well, where so -, SFTP on
        /// its SSH server, the image published in its area "firmware", and a
        /// downloader named as the node's identity, whose password
        /// <see cref="downloaderPassword"/> is.
        /// </summary>
        protected async Task<DownloadServerNode> ADownloadServer(Boolean  Secure     = false,
                                                                 Boolean  Anonymous  = false)
        {

            var directory  = NewNodeDirectory("downloads");
            var home       = Path.GetDirectoryName(directory.ConfigFile.Path)!;

            JProperty[] web = [];

            if (Secure)
            {

                var (fullChain, privateKey, withKey) = AServerCertificateOfTheTestCAAsPEM();
                withKey.Dispose();

                File.WriteAllText(Path.Combine(home, "fullchain.pem"), fullChain);
                File.WriteAllText(Path.Combine(home, "privkey.pem"),   privateKey);

                // The web interface's certificate, which the download port shows as well.
                web = [ new JProperty("web", new JObject(
                            new JProperty("tls", new JObject(
                                new JProperty("certificate", new JObject(
                                    new JProperty("fullchain",   Path.Combine(home, "fullchain.pem")),
                                    new JProperty("privateKey",  Path.Combine(home, "privkey.pem"))
                                ))
                            ))
                        )) ];

            }

            downloadServer = await TestPorts.StartedOnFreshPorts(() => {

                                 File.WriteAllText(
                                     directory.ConfigFile.Path,
                                     new JObject(
                                         [
                                             new JProperty("nts",        TestParties.NoTimeServers),
                                             new JProperty(DownloadServerSettings.SectionName, new JObject(
                                                 new JProperty("httpListen",     "127.0.0.1"),
                                                 new JProperty("httpPort",       TestPorts.Free().ToUInt16()),
                                                 new JProperty("anonymousHTTP",  Anonymous)
                                             )),
                                             new JProperty("ssh",        new JObject(
                                                 new JProperty("enabled",        true),
                                                 new JProperty("port",           TestPorts.Free().ToUInt16()),
                                                 new JProperty("sftp",           true)
                                             )),
                                             .. web
                                         ]
                                     ).ToString()
                                 );

                                 return new DownloadServerNode(HTTPPort:          TestPorts.Free(),
                                                               AccountsPath:      directory.Accounts,
                                                               ConfigFile:        directory.ConfigFile,
                                                               CertificatesPath:  Path.Combine(home, "certificates"),
                                                               LogToConsole:      false,
                                                               BridgeDebugLog:    false);

                             });

            Assert.That(downloadServer.DownloadsOverHTTPS, Is.EqualTo(Secure), $"The download port of the download server does {(Secure ? "not " : "")}speak TLS.{Story(downloadServer)}");

            using (var image = new MemoryStream(Image))
            {
                var (published, refused) = await downloadServer.PublishAsync("firmware", ImageName, image, "the test");
                Assert.That(published, Is.Not.Null, refused);
            }

            var made = await downloadServer.AddDownloader(Identity, null, "the test");

            Assert.That(made.Password, Is.Not.Null, made.Said);

            downloaderPassword = made.Password!;

            return downloadServer;

        }

        #endregion

        #region (protected) DownloadPort / DownloadServerURL(...) / TheDownloadServer(...)

        /// <summary>
        /// The port the download server serves files on by HTTP.
        /// </summary>
        protected UInt16 DownloadPort
            => downloadServer!.Settings.HTTPPort.ToUInt16();

        /// <summary>
        /// The URL of a file at the download server, as a CSMS gives it in an
        /// UpdateFirmware - with the login and the password of its downloader
        /// where they are given.
        /// </summary>
        protected String DownloadServerURL(String   Path      = ImagePath,
                                           String?  User      = null,
                                           String?  Password  = null)

            => $"{(downloadServer!.DownloadsOverHTTPS ? "https" : "http")}://" +
               (User is not null ? $"{Uri.EscapeDataString(User)}:{Uri.EscapeDataString(Password ?? "")}@" : "") +
               $"127.0.0.1:{DownloadPort}{Path}";

        /// <summary>
        /// The download server as the node's configuration holds it: its area
        /// "firmware", for downloads - and the login it signs in with, where it
        /// is given.
        /// </summary>
        protected HTTPFileServer TheDownloadServer(String?  User      = null,
                                                   String?  Password  = null)

            => new ("downloads",
                    new Uri($"{(downloadServer!.DownloadsOverHTTPS ? "https" : "http")}://127.0.0.1:{DownloadPort}/firmware/"),
                    User,
                    Password,
                    Directions: FileTransferDirections.Download);

        /// <summary>
        /// What the download server recorded of its downloads, the oldest first.
        /// </summary>
        protected IReadOnlyList<DownloadEvent> Recorded
            => downloadServer!.Downloads.All;

        #endregion

        #region (protected) ATestServerWithTheImage(Secure = false, User = null, Password = null)

        /// <summary>
        /// A firmware server of the test's own with the image at <see cref="ImagePath"/>,
        /// and the node's configuration naming its "/firmware/" for downloads -
        /// for what a download server does not do on purpose.
        /// </summary>
        protected TestFileServer ATestServerWithTheImage(Boolean  Secure    = false,
                                                         String?  User      = null,
                                                         String?  Password  = null)
        {

            var server = ATestServer(Secure, User, Password);

            server.Files[ImagePath] = Image;

            HoldsTheHTTPFileServers(new HTTPFileServer("firmware",
                                                       new Uri(server.URL("/firmware/")),
                                                       Directions: FileTransferDirections.Download));

            return server;

        }

        #endregion

        #region (protected) UpdateFirmware(Location, Retries = null, RetryInterval = null)

        /// <summary>
        /// The CSMS tells the node to update its firmware with the image at the
        /// given location, now: the response, and the id of the request its
        /// notifications carry.
        /// </summary>
        protected async Task<(UpdateFirmwareResponse Response, Int32 RequestId)> UpdateFirmware(String     Location,
                                                                                                 Byte?      Retries        = null,
                                                                                                 TimeSpan?  RetryInterval  = null)
        {

            var requestId = Interlocked.Increment(ref requestIds);

            var response  = await csms!.Node.UpdateFirmware(
                                      SourceRouting.To(NetworkingNode_Id.Parse(login.ToString())),
                                      new Firmware(URL.Parse(Location),
                                                   DateTimeOffset.UtcNow.AddMinutes(-1)),
                                      requestId,
                                      Retries,
                                      RetryInterval ?? TimeSpan.FromMilliseconds(200)
                                  );

            return (response, requestId);

        }

        #endregion

        #region (protected) Told(RequestId) / UntilTold(RequestId)

        /// <summary>
        /// What the node told its CSMS of the download of the given request, in order.
        /// </summary>
        protected FirmwareStatus[] Told(Int32 RequestId)

            => [.. told.Where (notification => notification.RequestId == RequestId).
                        Select(notification => notification.Status)];

        /// <summary>
        /// Wait until the node told its CSMS how the download of the given
        /// request ended - Downloaded or DownloadFailed -, and say how.
        /// </summary>
        protected async Task<FirmwareStatus> UntilTold(Int32 RequestId)
        {

            await Until(() => Told(RequestId).Any(status => status == FirmwareStatus.Downloaded || status == FirmwareStatus.DownloadFailed),
                        $"The CSMS being told how the download of request {RequestId} ended",
                        node, csms, downloadServer);

            return Told(RequestId).Last();

        }

        #endregion

        #region (protected) Accepted(Response) / Rejected(Response, ReasonCode, Why) / Staged(RequestId)

        protected void Accepted(UpdateFirmwareResponse Response)
            => Assert.That(Response.Status, Is.EqualTo(UpdateFirmwareStatus.Accepted), $"The UpdateFirmware was not accepted: {Response.StatusInfo?.ReasonCode} {Response.StatusInfo?.AdditionalInfo}{Story(node)}");

        protected void Rejected(UpdateFirmwareResponse  Response,
                                String                  ReasonCode,
                                String                  Why)
        {

            Assert.That(Response.Status,                 Is.EqualTo(UpdateFirmwareStatus.Rejected), $"{Why}: the UpdateFirmware was not rejected.{Story(node)}");
            Assert.That(Response.StatusInfo?.ReasonCode, Is.EqualTo(ReasonCode),                    $"{Why}: the UpdateFirmware was rejected for something else - {Response.StatusInfo?.AdditionalInfo}");

        }

        /// <summary>
        /// Where the node keeps the image of the given request once it is
        /// downloaded: its staging directory, beside its configuration.
        /// </summary>
        protected String Staged(Int32 RequestId)

            => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(node!.ConfigFile.Path))!,
                            "downloads",
                            $"{RequestId}-{ImageName}");

        #endregion


        #region HTTP_DownloadsAFirmwareImage_SigningInAsItsConfigurationSays()

        /// <summary>
        /// The CSMS tells the node to update its firmware with the image at the
        /// download server, over HTTP, by a URL without a login; the node's
        /// configuration names the download server for downloads, with the
        /// login of its downloader. The node accepts, fetches the image with one
        /// GET - asking for no range -, tells the CSMS Downloading and
        /// Downloaded, and keeps the image in its staging directory, byte for
        /// byte. The download server recorded it: by the node's downloader, by
        /// HTTP, the whole image from its first byte on.
        /// </summary>
        [Test]
        public async Task HTTP_DownloadsAFirmwareImage_SigningInAsItsConfigurationSays()
        {

            await ADownloadServer();

            HoldsTheHTTPFileServers(TheDownloadServer(Identity, downloaderPassword));

            var (response, requestId)  = await UpdateFirmware(DownloadServerURL());

            Accepted(response);

            Assert.That(await UntilTold(requestId), Is.EqualTo(FirmwareStatus.Downloaded), $"The image was not downloaded.{Story(node, downloadServer, csms)}");

            Assert.Multiple(() => {
                Assert.That(Told(requestId),                       Is.EqualTo(new[] { FirmwareStatus.Downloading, FirmwareStatus.Downloaded }), "The CSMS was told something else.");
                Assert.That(File.ReadAllBytes(Staged(requestId)),  Is.EqualTo(Image),                                                          "The image the node keeps is not the one offered.");
                Assert.That(Recorded.Select(download => (download.Protocol, download.Downloader, download.Path, download.Status, download.FirstByte, download.Bytes)),
                            Is.EqualTo(new[] { (DownloadProtocol.HTTP, Identity, ImagePath, 200, 0L, (Int64) Image.Length) }),
                            "The download server recorded something else: not one GET of the whole image by the node's downloader.");
            });

        }

        #endregion

        #region HTTPS_DownloadsAFirmwareImage_SigningInAsTheURLSays()

        /// <summary>
        /// The same over HTTPS, signed in as the URL says - the login and the
        /// password of the node's downloader, as the CSMS gives them -, where
        /// the configuration names the download server without a login: the
        /// node believes it by the test's CA, a root it keeps for its
        /// connections, and never writes the password to its log.
        /// </summary>
        [Test]
        public async Task HTTPS_DownloadsAFirmwareImage_SigningInAsTheURLSays()
        {

            await ADownloadServer(Secure: true);

            HoldsTheHTTPFileServers(TheDownloadServer());

            TrustsTheTestCA(node!);

            var (response, requestId)  = await UpdateFirmware(DownloadServerURL(User: Identity, Password: downloaderPassword));

            Accepted(response);

            Assert.That(await UntilTold(requestId), Is.EqualTo(FirmwareStatus.Downloaded), $"The image was not downloaded over HTTPS.{Story(node, downloadServer, csms)}");

            Assert.Multiple(() => {
                Assert.That(File.ReadAllBytes(Staged(requestId)),                   Is.EqualTo(Image));
                Assert.That(Recorded.Single().Downloader,                           Is.EqualTo(Identity),         "The download server took it for another downloader.");
                Assert.That(node!.Log.Recent(500).Select(entry => entry.Message),   Has.None.Contains(downloaderPassword), "The node logged the password of the URL.");
            });

        }

        #endregion

        #region HTTP_DownloadsWithoutALogin_WhereTheDownloadServerTakesNone()

        /// <summary>
        /// A download server that serves its files without credentials as well
        /// ("anonymousHTTP"): the node, given no login anywhere, fetches the
        /// image, and the download server records it as anonymous.
        /// </summary>
        [Test]
        public async Task HTTP_DownloadsWithoutALogin_WhereTheDownloadServerTakesNone()
        {

            await ADownloadServer(Anonymous: true);

            HoldsTheHTTPFileServers(TheDownloadServer());

            var (response, requestId)  = await UpdateFirmware(DownloadServerURL());

            Accepted(response);

            Assert.That(await UntilTold(requestId), Is.EqualTo(FirmwareStatus.Downloaded), $"The image was not downloaded without a login.{Story(node, downloadServer, csms)}");

            Assert.Multiple(() => {
                Assert.That(File.ReadAllBytes(Staged(requestId)),  Is.EqualTo(Image));
                Assert.That(Recorded.Single().Downloader,          Is.EqualTo(DownloadServerNode.AnonymousDownloader));
            });

        }

        #endregion

        #region HTTP_AWrongPassword_IsDownloadFailed()

        /// <summary>
        /// A login the download server refuses - 401 -: DownloadFailed at once,
        /// as asking again would only be refused the same, and nothing is
        /// downloaded.
        /// </summary>
        [Test]
        public async Task HTTP_AWrongPassword_IsDownloadFailed()
        {

            await ADownloadServer();

            HoldsTheHTTPFileServers(TheDownloadServer(Identity, "not-" + downloaderPassword));

            var (response, requestId)  = await UpdateFirmware(DownloadServerURL(), Retries: 3);

            Accepted(response);

            Assert.That(await UntilTold(requestId), Is.EqualTo(FirmwareStatus.DownloadFailed), $"A refused login was told as something else.{Story(node, downloadServer, csms)}");

            Assert.Multiple(() => {
                Assert.That(Told(requestId),                       Is.EqualTo(new[] { FirmwareStatus.Downloading, FirmwareStatus.DownloadFailed }), "A refused login was tried again.");
                Assert.That(Recorded,                              Is.Empty,                     "Something was downloaded with a wrong password.");
                Assert.That(downloadServer!.HTTPDownloadsRefused,  Is.EqualTo(1),                "The download server did not refuse exactly one request.");
                Assert.That(File.Exists(Staged(requestId)),        Is.False);
            });

        }

        #endregion

        #region HTTP_AMissingImage_IsDownloadFailed()

        /// <summary>
        /// The image is not offered - 404 -: DownloadFailed at once, as asking
        /// again would only be answered the same.
        /// </summary>
        [Test]
        public async Task HTTP_AMissingImage_IsDownloadFailed()
        {

            await ADownloadServer();

            HoldsTheHTTPFileServers(TheDownloadServer(Identity, downloaderPassword));

            var (response, requestId)  = await UpdateFirmware(DownloadServerURL("/firmware/station-9.9.9.bin"), Retries: 3);

            Accepted(response);

            Assert.That(await UntilTold(requestId), Is.EqualTo(FirmwareStatus.DownloadFailed), $"A missing image was told as something else.{Story(node, downloadServer, csms)}");

            Assert.Multiple(() => {
                Assert.That(Told(requestId),                       Is.EqualTo(new[] { FirmwareStatus.Downloading, FirmwareStatus.DownloadFailed }));
                Assert.That(downloadServer!.HTTPDownloadsRefused,  Is.EqualTo(1),  "A missing image was asked for again.");
                Assert.That(Recorded,                              Is.Empty);
            });

        }

        #endregion

        #region HTTPS_FetchesNothing_FromAServerItDoesNotBelieve()

        /// <summary>
        /// The download server over HTTPS, which the node's configuration
        /// names, but whose certificate nothing the node keeps vouches for: the
        /// UpdateFirmware is accepted, the TLS handshake ends the download
        /// before a request is sent, it is not tried again, and the CSMS is told
        /// DownloadFailed.
        /// </summary>
        [Test]
        public async Task HTTPS_FetchesNothing_FromAServerItDoesNotBelieve()
        {

            await ADownloadServer(Secure: true);

            HoldsTheHTTPFileServers(TheDownloadServer(Identity, downloaderPassword));

            var (response, requestId)  = await UpdateFirmware(DownloadServerURL(), Retries: 3);

            Accepted(response);

            Assert.That(await UntilTold(requestId), Is.EqualTo(FirmwareStatus.DownloadFailed), $"The download did not fail.{Story(node, downloadServer, csms)}");

            Assert.Multiple(() => {
                Assert.That(Recorded,                              Is.Empty,  "Something was fetched from a server the node does not believe.");
                Assert.That(downloadServer!.HTTPDownloadsRefused,  Is.Zero,   "A request reached a server the node does not believe.");
                Assert.That(node!.Log.Recent(200).Where(entry => entry.Message.Contains("ended as CertificateRefused")),
                            Has.Exactly(1).Items,                             "A server the node does not believe was not refused, or was asked again.");
                Assert.That(File.Exists(Staged(requestId)),        Is.False,  "The node keeps something of a server it does not believe.");
            });

        }

        #endregion

        #region HTTP_RefusesWhatItsConfigurationDoesNotName()

        /// <summary>
        /// A request chooses among the firmware servers the node holds, and never
        /// introduces one. Another directory, a server at another port, a server
        /// the configuration names for uploads only: FileServerNotPermitted,
        /// before anything is sent. A URL that names a directory and no file, or
        /// carries a query: InvalidURL. FTP: NotSupported. Nothing reaches a
        /// server, and nothing is told of a download that never started.
        /// </summary>
        [Test]
        public async Task HTTP_RefusesWhatItsConfigurationDoesNotName()
        {

            var server    = ATestServerWithTheImage();
            var uploads   = ATestServer();
            var stranger  = ATestServer();

            HoldsTheHTTPFileServers(new HTTPFileServer("firmware", new Uri(server. URL("/firmware/")), Directions: FileTransferDirections.Download),
                                    new HTTPFileServer("logs",     new Uri(uploads.URL("/firmware/")), Directions: FileTransferDirections.Upload));

            var requests  = new List<Int32>();

            async Task IsRefused(String Location, String ReasonCode, String Why)
            {

                var (response, requestId) = await UpdateFirmware(Location);

                Rejected(response, ReasonCode, Why);

                requests.Add(requestId);

            }

            await IsRefused(server.  URL("/elsewhere/image.bin"),           FileTransfers.FileServerNotPermitted,  "Another directory of the firmware server");
            await IsRefused(stranger.URL(ImagePath),                         FileTransfers.FileServerNotPermitted,  "A server at another port");
            await IsRefused(uploads. URL(ImagePath),                         FileTransfers.FileServerNotPermitted,  "A server named for uploads only");
            await IsRefused(server.  URL("/firmware/"),                      FileTransfers.InvalidURL,              "A URL that names no file");
            await IsRefused(server.  URL(ImagePath + "?version=latest"),     FileTransfers.InvalidURL,              "A URL with a query");
            await IsRefused($"ftp://127.0.0.1:{server.Port}{ImagePath}",     FileTransfers.NotSupported,            "A URL of FTP");

            // The notifications of the request before would be here by now.
            await Task.Delay(500);

            Assert.Multiple(() => {
                Assert.That(server.  Connections,                 Is.Zero,   "Something reached the firmware server.");
                Assert.That(uploads. Connections,                 Is.Zero,   "Something reached a server named for uploads only.");
                Assert.That(stranger.Connections,                 Is.Zero,   "Something reached a server the node's configuration does not name.");
                Assert.That(requests.SelectMany(id => Told(id)),  Is.Empty,  "The CSMS was told of a download that never started.");
            });

        }

        #endregion

        #region HTTP_ItsConfigurationSaysHowLargeAnImageMayBe()

        /// <summary>
        /// "maxDownloadSize" of the node's configuration file goes before what
        /// the kind of node takes, from the next download on: an image of just
        /// as many bytes is downloaded, and with one byte less allowed the same
        /// image is DownloadFailed - refused by what the download server says
        /// of its length, after a single GET.
        /// </summary>
        [Test]
        public async Task HTTP_ItsConfigurationSaysHowLargeAnImageMayBe()
        {

            await ADownloadServer();

            HoldsTheHTTPFileServers(TheDownloadServer(Identity, downloaderPassword));

            Assert.That(node!.ConfigFile.TryReplaceSection(FileTransfers.MaxDownloadSizeKey, new JValue(Image.Length), out var error), Is.True, error);

            var (justAsLarge, justAsLargeId) = await UpdateFirmware(DownloadServerURL());

            Accepted(justAsLarge);

            Assert.That(await UntilTold(justAsLargeId), Is.EqualTo(FirmwareStatus.Downloaded), $"An image just as large as allowed was not downloaded.{Story(node, downloadServer, csms)}");
            Assert.That(File.ReadAllBytes(Staged(justAsLargeId)), Is.EqualTo(Image));

            Assert.That(node.ConfigFile.TryReplaceSection(FileTransfers.MaxDownloadSizeKey, new JValue(Image.Length - 1), out error), Is.True, error);

            var (tooLarge, tooLargeId) = await UpdateFirmware(DownloadServerURL(), Retries: 2);

            Accepted(tooLarge);

            Assert.That(await UntilTold(tooLargeId), Is.EqualTo(FirmwareStatus.DownloadFailed), $"An image larger than allowed was told as something else.{Story(node, downloadServer, csms)}");

            Assert.Multiple(() => {
                Assert.That(Recorded,                         Has.Count.EqualTo(2),  "An image larger than allowed was asked for again.");
                Assert.That(File.Exists(Staged(tooLargeId)),  Is.False,              "The node keeps something of an image larger than allowed.");
            });

        }

        #endregion


        #region HTTP_AnImageLargerThanTheNodeTakes_IsDownloadFailed()

        /// <summary>
        /// A firmware server of the test's own says the image has one byte more
        /// than the kind of node takes (<see cref="MaxDownloadSize"/>) - its
        /// configuration file says nothing of it. The node refuses it by
        /// "Content-Length", before a byte is kept: DownloadFailed, not tried
        /// again, as the image would be no smaller a moment later, and the
        /// node's log says how much it takes.
        /// </summary>
        [Test]
        public async Task HTTP_AnImageLargerThanTheNodeTakes_IsDownloadFailed()
        {

            var server                 = ATestServerWithTheImage();

            server.ClaimNextAs(MaxDownloadSize + 1, MaxDownloadSize + 1, MaxDownloadSize + 1);

            var (response, requestId)  = await UpdateFirmware(server.URL(ImagePath), Retries: 2);

            Accepted(response);

            Assert.That(await UntilTold(requestId), Is.EqualTo(FirmwareStatus.DownloadFailed), $"An image too large was told as something else.{Story(node, csms)}");

            Assert.Multiple(() => {
                Assert.That(server.Requests,                  Has.Count.EqualTo(1),  "An image too large was asked for again.");
                Assert.That(Told(requestId),                  Is.EqualTo(new[] { FirmwareStatus.Downloading, FirmwareStatus.DownloadFailed }));
                Assert.That(File.Exists(Staged(requestId)),   Is.False,              "The node keeps something of an image too large.");
                Assert.That(node!.Log.Recent(200).Select(entry => entry.Message),
                            Has.Some.Contains($"at most {MaxDownloadSize} are taken"), "The node's log does not say how much it takes.");
            });

        }

        #endregion

        #region HTTP_TakesUpWhereTheAttemptBeforeStopped()

        /// <summary>
        /// A firmware server of the test's own breaks off the first GET after
        /// 100000 bytes. The node says DownloadPaused, waits, and asks for the
        /// rest with a Range - "bytes=100000-" - of the same file only: with an
        /// If-Range of the ETag the first answer said. The server answers 206.
        /// The image it keeps is whole, and nothing is in it twice.
        /// </summary>
        [Test]
        public async Task HTTP_TakesUpWhereTheAttemptBeforeStopped()
        {

            var server                 = ATestServerWithTheImage();

            server.BreakNextAfter(100_000);

            var (response, requestId)  = await UpdateFirmware(server.URL(ImagePath), Retries: 2);

            Accepted(response);

            Assert.That(await UntilTold(requestId), Is.EqualTo(FirmwareStatus.Downloaded), $"The image was not downloaded at last.{Story(node, csms)}");

            var requests = server.Requests.ToArray();

            Assert.Multiple(() => {
                Assert.That(requests.Select(request => request.Answered),   Is.EqualTo(new[] { 200, 206 }),  "The node did not take up where it stopped.");
                Assert.That(requests[1].Headers["Range"],                    Is.EqualTo("bytes=100000-"),     "The node did not ask for the rest.");
                Assert.That(requests[1].Headers.GetValueOrDefault("If-Range"), Is.EqualTo(TestFileServer.ETagOf(Image)), "The node did not ask for the rest of the same file only.");
                Assert.That(Told(requestId),                                 Is.EqualTo(new[] { FirmwareStatus.Downloading, FirmwareStatus.DownloadPaused, FirmwareStatus.Downloading, FirmwareStatus.Downloaded }),
                                                                                                              "The CSMS was told something else.");
                Assert.That(File.ReadAllBytes(Staged(requestId)),            Is.EqualTo(Image),               "The image the node keeps is not whole.");
            });

        }

        #endregion

        #region HTTP_AServerWithoutRanges_IsReadFromWhereItStopped()

        /// <summary>
        /// A firmware server of the test's own that does not take ranges, and
        /// breaks off the first GET: the second attempt is answered with the
        /// whole image - 200 -, and the node passes over what it has already.
        /// The image it keeps is whole, and nothing is in it twice.
        /// </summary>
        [Test]
        public async Task HTTP_AServerWithoutRanges_IsReadFromWhereItStopped()
        {

            var server                 = ATestServerWithTheImage();

            server.IgnoresRange        = true;
            server.BreakNextAfter(100_000);

            var (response, requestId)  = await UpdateFirmware(server.URL(ImagePath), Retries: 2);

            Accepted(response);

            Assert.That(await UntilTold(requestId), Is.EqualTo(FirmwareStatus.Downloaded), $"The image was not downloaded at last.{Story(node, csms)}");

            Assert.Multiple(() => {
                Assert.That(server.Requests.Select(request => request.Answered),  Is.EqualTo(new[] { 200, 200 }));
                Assert.That(File.ReadAllBytes(Staged(requestId)),                  Is.EqualTo(Image), "The image the node keeps is not whole, or has a part twice.");
            });

        }

        #endregion

        #region HTTP_TriesAgain_AfterTheServerFailed()

        /// <summary>
        /// A firmware server of the test's own fails once - 503 -, which may be
        /// better a moment later: the node says DownloadPaused, tries again,
        /// and the image is there.
        /// </summary>
        [Test]
        public async Task HTTP_TriesAgain_AfterTheServerFailed()
        {

            var server                 = ATestServerWithTheImage();

            server.AnswerNext(503);

            var (response, requestId)  = await UpdateFirmware(server.URL(ImagePath), Retries: 2);

            Accepted(response);

            Assert.That(await UntilTold(requestId), Is.EqualTo(FirmwareStatus.Downloaded), $"The image was not downloaded at last.{Story(node, csms)}");

            Assert.Multiple(() => {
                Assert.That(server.Requests.Select(request => request.Answered),  Is.EqualTo(new[] { 503, 200 }));
                Assert.That(Told(requestId),                                       Is.EqualTo(new[] { FirmwareStatus.Downloading, FirmwareStatus.DownloadPaused, FirmwareStatus.Downloading, FirmwareStatus.Downloaded }));
                Assert.That(File.ReadAllBytes(Staged(requestId)),                  Is.EqualTo(Image));
            });

        }

        #endregion

        #region HTTP_AnImageChangedMeanwhile_IsFetchedAnewFromItsStart()

        /// <summary>
        /// A firmware server of the test's own breaks off the first GET after
        /// 100000 bytes, and then has another image in the place of the first.
        /// The node asks for the rest of the first one - If-Range of its ETag -,
        /// the server sends the new one whole, and the node cuts away what it
        /// had of the first and keeps the new one, byte for byte: no image of
        /// two halves.
        /// </summary>
        [Test]
        public async Task HTTP_AnImageChangedMeanwhile_IsFetchedAnewFromItsStart()
        {

            var newer                  = RandomNumberGenerator.GetBytes(250_000);
            var server                 = ATestServerWithTheImage();

            server.BreakNextAfter(100_000);
            server.ReplaceAfterNext(ImagePath, newer);

            var (response, requestId)  = await UpdateFirmware(server.URL(ImagePath), Retries: 2);

            Accepted(response);

            Assert.That(await UntilTold(requestId), Is.EqualTo(FirmwareStatus.Downloaded), $"The image was not downloaded at last.{Story(node, csms)}");

            var requests = server.Requests.ToArray();

            Assert.Multiple(() => {
                Assert.That(requests.Select(request => request.Answered),     Is.EqualTo(new[] { 200, 200 }),        "The new image was not sent whole.");
                Assert.That(requests[1].Headers.GetValueOrDefault("If-Range"), Is.EqualTo(TestFileServer.ETagOf(Image)), "The node did not name the first image.");
                Assert.That(File.ReadAllBytes(Staged(requestId)),              Is.EqualTo(newer),                    "The image the node keeps is not the new one, whole - or has a part of the first.");
                Assert.That(node!.Log.Recent(200).Select(entry => entry.Message),
                            Has.Some.Contains("changed since the attempt before"),                                   "The node's log does not say that the image changed.");
            });

        }

        #endregion

        #region HTTP_ARedirectToAServerItsConfigurationNames_IsFollowed_SigningInThereAsItsConfigurationSays()

        /// <summary>
        /// A firmware server of the test's own - signed in to with the login of
        /// the URL - sends the download on to the download server, with a query
        /// as a content delivery network signs a URL with. The node's
        /// configuration names both for downloads, the download server with the
        /// login of its downloader: the redirect is followed, signed in there
        /// as the configuration says - the login of the URL goes to the server
        /// the request named, and nowhere else -, and the image is downloaded.
        /// </summary>
        [Test]
        public async Task HTTP_ARedirectToAServerItsConfigurationNames_IsFollowed_SigningInThereAsItsConfigurationSays()
        {

            await ADownloadServer();

            var origin = ATestServer(User: "origin-login", Password: "origin-password");

            HoldsTheHTTPFileServers(new HTTPFileServer("origin", new Uri(origin.URL("/firmware/")), Directions: FileTransferDirections.Download),
                                    TheDownloadServer(Identity, downloaderPassword));

            origin.RedirectNextTo(302, DownloadServerURL() + "?expires=2000000000&signature=0123456789abcdef");

            var (response, requestId)  = await UpdateFirmware($"http://origin-login:origin-password@127.0.0.1:{origin.Port}/firmware/{ImageName}");

            Accepted(response);

            Assert.That(await UntilTold(requestId), Is.EqualTo(FirmwareStatus.Downloaded), $"The image was not downloaded where the redirect leads.{Story(node, downloadServer, csms)}");

            Assert.Multiple(() => {
                Assert.That(File.ReadAllBytes(Staged(requestId)),     Is.EqualTo(Image),             "The image the node keeps is not the one offered.");
                Assert.That(origin.Requests.Single().Answered,        Is.EqualTo(302));
                Assert.That(origin.Requests.Single().Headers.ContainsKey("Authorization"), Is.True, "The node did not sign in where the request named.");
                Assert.That(Recorded.Single().Downloader,             Is.EqualTo(Identity),          "The download server was not signed in to as the configuration says.");
                Assert.That(downloadServer!.HTTPDownloadsRefused,     Is.Zero,                       "The login of the URL went to the download server as well.");
                Assert.That(node!.Log.Recent(200).Select(entry => entry.Message),
                            Has.Some.Contains("is sent on to"),                                     "The node's log does not say where the download was sent on to.");
            });

        }

        #endregion

        #region HTTP_ARedirectFromHTTPSToHTTP_IsNotFollowed()

        /// <summary>
        /// A firmware server over HTTPS sends the download on to the download
        /// server over plain HTTP, which the configuration names as well: not
        /// followed - the image would come over a connection nobody vouches
        /// for. DownloadFailed, and nothing is fetched.
        /// </summary>
        [Test]
        public async Task HTTP_ARedirectFromHTTPSToHTTP_IsNotFollowed()
        {

            await ADownloadServer();

            var origin = ATestServer(Secure: true);

            TrustsTheTestCA(node!);

            HoldsTheHTTPFileServers(new HTTPFileServer("origin", new Uri(origin.URL("/firmware/")), Directions: FileTransferDirections.Download),
                                    TheDownloadServer(Identity, downloaderPassword));

            origin.RedirectNextTo(302, DownloadServerURL());

            var (response, requestId)  = await UpdateFirmware(origin.URL(ImagePath), Retries: 2);

            Accepted(response);

            Assert.That(await UntilTold(requestId), Is.EqualTo(FirmwareStatus.DownloadFailed), $"A redirect from https to http was told as something else.{Story(node, downloadServer, csms)}");

            Assert.Multiple(() => {
                Assert.That(origin.Requests,                          Has.Count.EqualTo(1),          "A redirect from https to http was asked again.");
                Assert.That(Recorded,                                 Is.Empty,                      "Something was fetched over http after https.");
                Assert.That(downloadServer!.HTTPDownloadsRefused,     Is.Zero,                       "A request reached the download server.");
                Assert.That(node!.Log.Recent(200).Select(entry => entry.Message),
                            Has.Some.Contains("from https:// to"),                                  "The node's log does not say why.");
            });

        }

        #endregion

        #region HTTP_RedirectsWithoutEnd_AreFollowedFiveTimes()

        /// <summary>
        /// A firmware server of the test's own sends the download on to itself
        /// again and again: five redirects are followed, the sixth is not.
        /// DownloadFailed, not tried again.
        /// </summary>
        [Test]
        public async Task HTTP_RedirectsWithoutEnd_AreFollowedFiveTimes()
        {

            var server = ATestServerWithTheImage();

            server.RedirectNextTo(302, [ .. Enumerable.Range(1, 8).Select(number => server.URL($"/firmware/hop-{number}.bin")) ]);

            var (response, requestId)  = await UpdateFirmware(server.URL(ImagePath), Retries: 2);

            Accepted(response);

            Assert.That(await UntilTold(requestId), Is.EqualTo(FirmwareStatus.DownloadFailed), $"Redirects without end were told as something else.{Story(node, csms)}");

            Assert.Multiple(() => {
                Assert.That(server.Requests,                          Has.Count.EqualTo(1 + WWCPNode.MaxHTTPRedirects), "Not five redirects followed, and then no more.");
                Assert.That(node!.Log.Recent(200).Select(entry => entry.Message),
                            Has.Some.Contains($"after {WWCPNode.MaxHTTPRedirects} already"),        "The node's log does not say why.");
            });

        }

        #endregion

        #region HTTP_ARedirectToAServerItsConfigurationDoesNotName_IsNotFollowed()

        /// <summary>
        /// A firmware server of the test's own answers with a redirect - 302 to
        /// another host -: it is not followed, as it would lead to a server the
        /// node's configuration does not name. DownloadFailed, not tried again,
        /// and the node's log says why.
        /// </summary>
        [Test]
        public async Task HTTP_ARedirectToAServerItsConfigurationDoesNotName_IsNotFollowed()
        {

            var server                 = ATestServerWithTheImage();

            server.AnswerNext(302, 302);

            var (response, requestId)  = await UpdateFirmware(server.URL(ImagePath), Retries: 1);

            Accepted(response);

            Assert.That(await UntilTold(requestId), Is.EqualTo(FirmwareStatus.DownloadFailed), $"A redirect was told as something else.{Story(node, csms)}");

            Assert.Multiple(() => {
                Assert.That(server.Requests,  Has.Count.EqualTo(1),  "A redirect was asked again.");
                Assert.That(node!.Log.Recent(200).Select(entry => entry.Message),
                            Has.Some.Contains("is not followed"),     "The node's log does not say why.");
            });

        }

        #endregion


        #region (private) HeldOverSFTP(Pin = null, Confirmed = true)

        /// <summary>
        /// The download server held by the node as a file server over SFTP,
        /// pinned to its host key: with a key of the node's own to sign in with
        /// as its downloader, enrolled at the download server and - where so -
        /// confirmed as enrolled. The URL of the image.
        /// </summary>
        /// <param name="Pin">The host key the node is held to there; the download server's own where none is given.</param>
        /// <param name="Confirmed">Whether the key the node signs in with is confirmed as enrolled.</param>
        private async Task<String> HeldOverSFTP(String?  Pin        = null,
                                                Boolean  Confirmed  = true)
        {

            await ADownloadServer();

            var port   = downloadServer!.SSHPort!.Value;
            var user   = User_Id.Parse(Identity);

            // The download server is an SSH peer of the node's configuration that says what it may be used for.
            var held   = await node!.TryUpdateSSHPeers(peers => new JArray(
                                   new SSHPeerConfiguration("downloads",
                                                            DomainName.Parse("127.0.0.1"),
                                                            user,
                                                            new NTSServerSSH(port, HostKeyPins: [ Pin ?? downloadServer.SSHHostKey!.Split(' ')[1] ]),
                                                            new SSHFileTransfers(FileTransferDirections.Download,
                                                                                 DownloadPaths: [ "/firmware/" ])).ToJSON()
                               ));

            Assert.That(held.Error, Is.Null, held.Error);

            // Its key, enrolled at the download server for its downloader.
            var line   = node.FileTransferPublicKey("downloads", user);

            Assert.That(line, Is.Not.Null, $"The node made no key to sign in with at the download server.{Story(node)}");

            var added  = await downloadServer.AuthorizeSSHKey(user, line!);

            Assert.That(added.IsAdded, Is.True, added.Reason);

            if (Confirmed)
                Assert.That(node.ConfirmFileTransferKey("downloads", user, line), Is.Null);

            return $"sftp://{user}@127.0.0.1:{port}{ImagePath}";

        }

        #endregion

        #region SFTP_DownloadsAFirmwareImage()

        /// <summary>
        /// The CSMS tells the node to update its firmware with the image in the
        /// area "firmware" of the download server over SFTP, which the node's
        /// configuration holds for downloads from "/firmware/": the node
        /// connects to it on a connection of its own, believes it by the host
        /// key it is pinned to, signs in as its downloader with its own key
        /// enrolled there, and keeps the image byte for byte. The download
        /// server recorded it: by the node's downloader, over SFTP, the whole
        /// image from its first byte on.
        /// </summary>
        [Test]
        public async Task SFTP_DownloadsAFirmwareImage()
        {

            var location               = await HeldOverSFTP();

            var (response, requestId)  = await UpdateFirmware(location);

            Accepted(response);

            Assert.That(await UntilTold(requestId), Is.EqualTo(FirmwareStatus.Downloaded), $"The image was not downloaded over SFTP.{Story(node, downloadServer, csms)}");

            Assert.Multiple(() => {
                Assert.That(Told(requestId),                       Is.EqualTo(new[] { FirmwareStatus.Downloading, FirmwareStatus.Downloaded }), "The CSMS was told something else.");
                Assert.That(File.ReadAllBytes(Staged(requestId)),  Is.EqualTo(Image),                                                          "The image the node keeps is not the one offered.");
                Assert.That(Recorded.Select(download => (download.Protocol, download.Downloader, download.Path, download.FirstByte, download.Bytes)),
                            Is.EqualTo(new[] { (DownloadProtocol.SFTP, Identity, ImagePath, 0L, (Int64) Image.Length) }),
                            "The download server recorded something else: not one read of the whole image by the node's downloader.");
            });

        }

        #endregion

        #region SFTP_RefusesWhatItsConfigurationDoesNotName()

        /// <summary>
        /// Over SFTP the same: a directory the download server is not allowed
        /// downloads from, another port, another login - FileServerNotPermitted
        /// before a packet leaves the node; a password in the URL - InvalidURL.
        /// </summary>
        [Test]
        public async Task SFTP_RefusesWhatItsConfigurationDoesNotName()
        {

            await HeldOverSFTP();

            var port = downloadServer!.SSHPort!.Value;

            async Task IsRefused(String Location, String ReasonCode, String Why)
            {
                var (response, _) = await UpdateFirmware(Location);
                Rejected(response, ReasonCode, Why);
            }

            await IsRefused($"sftp://{Identity}@127.0.0.1:{port}/logs/{ImageName}",               FileTransfers.FileServerNotPermitted,  "A directory the download server is not allowed downloads from");
            await IsRefused($"sftp://{Identity}@127.0.0.1:{TestPorts.Free()}{ImagePath}",         FileTransfers.FileServerNotPermitted,  "Another port");
            await IsRefused($"sftp://somebody@127.0.0.1:{port}{ImagePath}",                       FileTransfers.FileServerNotPermitted,  "Another login");
            await IsRefused($"sftp://{Identity}:secret@127.0.0.1:{port}{ImagePath}",              FileTransfers.InvalidURL,              "A password in the URL");

            Assert.That(Recorded, Is.Empty, "Something was fetched from the download server.");

        }

        #endregion

        #region SFTP_FetchesNothing_FromADownloadServerThatShowsAnotherHostKey()

        /// <summary>
        /// The node is pinned to another host key than the one the download
        /// server shows: the UpdateFirmware is accepted, the handshake ends the
        /// download, nothing is fetched, it is not tried again, and the CSMS is
        /// told DownloadFailed.
        /// </summary>
        [Test]
        public async Task SFTP_FetchesNothing_FromADownloadServerThatShowsAnotherHostKey()
        {

            var another                = SshFingerprint.Sha256(SshHostKey.GenerateEd25519().PublicKeyBlob);

            var location               = await HeldOverSFTP(Pin: another);

            var (response, requestId)  = await UpdateFirmware(location, Retries: 2);

            Accepted(response);

            Assert.That(await UntilTold(requestId), Is.EqualTo(FirmwareStatus.DownloadFailed), $"The download from a server of another host key did not fail.{Story(node, downloadServer, csms)}");

            Assert.Multiple(() => {
                Assert.That(Recorded,                              Is.Empty,  "Something was fetched from a server of another host key.");
                Assert.That(File.Exists(Staged(requestId)),        Is.False);
                Assert.That(node!.Log.Recent(200).Where(entry => entry.Message.Contains("ended as HostKeyRefused")),
                            Has.Exactly(1).Items,                             "A server of another host key was not refused, or was asked again.");
            });

        }

        #endregion

        #region SFTP_RefusesADownload_UntilItsKeyIsConfirmed()

        /// <summary>
        /// The node made a key to sign in with at the download server, but
        /// nobody confirmed that the download server holds it:
        /// FileServerNotPermitted, as the server would refuse the login anyway.
        /// Once it is confirmed, the image is downloaded.
        /// </summary>
        [Test]
        public async Task SFTP_RefusesADownload_UntilItsKeyIsConfirmed()
        {

            var location               = await HeldOverSFTP(Confirmed: false);

            var (refused, _)           = await UpdateFirmware(location);

            Rejected(refused, FileTransfers.FileServerNotPermitted, "A key not confirmed as enrolled");

            Assert.That(node!.ConfirmFileTransferKey("downloads", User_Id.Parse(Identity)), Is.Null);

            var (accepted, requestId)  = await UpdateFirmware(location);

            Accepted(accepted);

            Assert.That(await UntilTold(requestId), Is.EqualTo(FirmwareStatus.Downloaded), $"The image was not downloaded once the key was confirmed.{Story(node, downloadServer, csms)}");

            Assert.That(Recorded.Single().Protocol, Is.EqualTo(DownloadProtocol.SFTP));

        }

        #endregion

    }

}
