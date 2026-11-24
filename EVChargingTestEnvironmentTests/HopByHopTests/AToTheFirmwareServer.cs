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

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.protocols.WWCP;
using cloud.charging.open.protocols.WWCP.NetworkingNode;
using cloud.charging.open.protocols.WWCP.Node;
using cloud.charging.open.protocols.WWCP.Node.SecureShell;
using cloud.charging.open.protocols.OCPP.Node;
using cloud.charging.open.protocols.OCPPv2_1;
using cloud.charging.open.protocols.OCPPv2_1.CS;
using cloud.charging.open.protocols.OCPPv2_1.CSMS;

#endregion

namespace cloud.charging.open.TestEnvironment.Tests.Connections.HopByHop
{

    /// <summary>
    /// A node that updates its firmware - a charging station, a local
    /// controller - is told to by its CSMS with an UpdateFirmware, and fetches
    /// the image from a firmware server with a GET over HTTP or HTTPS. It goes
    /// only to a server its own configuration names for downloads - never to
    /// one the request introduces, and never where a redirect would lead -,
    /// signs in as the URL of the request says, believes an HTTPS server by
    /// the roots it keeps for its connections, takes up with a Range where the
    /// attempt before stopped, tries again only what may be better a moment
    /// later, and tells the CSMS how it went with FirmwareStatusNotifications -
    /// up to Downloaded, which is as far as the transport goes.
    /// </summary>
    /// <remarks>
    /// The firmware server is one of the test's own (<see cref="TestFileServer"/>):
    /// it serves ranges, breaks off a body, or does not take ranges, as a test
    /// asks. The node is the kind's to make (<see cref="AFileTransferTest.NewNode"/>).
    /// </remarks>
    public abstract class AToTheFirmwareServer : AFileTransferTest
    {

        #region Data

        /// <summary>
        /// Where the image lies at the firmware server.
        /// </summary>
        protected const String  ImagePath   = "/firmware/2.1.0/image.bin";

        /// <summary>
        /// More than one buffer of a download.
        /// </summary>
        protected static readonly Byte[]  Image  = RandomNumberGenerator.GetBytes(300_000);

        private readonly ConcurrentQueue<(Int64? RequestId, FirmwareStatus Status)>  told        = new();
        private          Int32                                                       requestIds  = 200;

        #endregion

        #region HearsOf(CSMS)

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

        #endregion


        #region (protected) AFirmwareServer(Secure = false, User = null, Password = null)

        /// <summary>
        /// A firmware server of the test's own with the image at <see cref="ImagePath"/>,
        /// and the node's configuration naming its "/firmware/" for downloads.
        /// </summary>
        protected TestFileServer AFirmwareServer(Boolean  Secure    = false,
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
                        node, csms);

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
                            $"{RequestId}-{Path.GetFileName(ImagePath)}");

        #endregion


        #region HTTP_DownloadsAFirmwareImage()

        /// <summary>
        /// The CSMS tells the node to update its firmware with an image at a
        /// firmware server over HTTP that the node's configuration names for
        /// downloads. The node accepts, fetches the image with one GET - asking
        /// for no range, signing in with nothing -, tells the CSMS Downloading
        /// and Downloaded, and keeps the image in its staging directory, byte
        /// for byte.
        /// </summary>
        [Test]
        public async Task HTTP_DownloadsAFirmwareImage()
        {

            var server                 = AFirmwareServer();

            var (response, requestId)  = await UpdateFirmware(server.URL(ImagePath));

            Accepted(response);

            Assert.That(await UntilTold(requestId), Is.EqualTo(FirmwareStatus.Downloaded), $"The image was not downloaded.{Story(node, csms)}");

            var requests = server.Requests.ToArray();

            Assert.Multiple(() => {
                Assert.That(Told(requestId),                                   Is.EqualTo(new[] { FirmwareStatus.Downloading, FirmwareStatus.Downloaded }), "The CSMS was told something else.");
                Assert.That(File.ReadAllBytes(Staged(requestId)),              Is.EqualTo(Image),                                                          "The image the node keeps is not the one served.");
                Assert.That(requests,                                          Has.Length.EqualTo(1),                                                      "The image was not fetched with one request.");
                Assert.That(requests[0].Method,                                Is.EqualTo("GET"));
                Assert.That(requests[0].Headers.ContainsKey("Range"),          Is.False,                                                                   "A first attempt asked for a range.");
                Assert.That(requests[0].Headers.ContainsKey("Authorization"),  Is.False,                                                                   "The node signed in with something nobody said.");
            });

        }

        #endregion

        #region HTTPS_DownloadsAFirmwareImage_SigningInAsTheURLSays()

        /// <summary>
        /// The same over HTTPS, from a firmware server that asks for a login:
        /// the node believes it by the test's CA - a root it keeps for its
        /// connections - and signs in with the login and the password of the
        /// URL, as the CSMS gives them, never writing the password to its log.
        /// </summary>
        [Test]
        public async Task HTTPS_DownloadsAFirmwareImage_SigningInAsTheURLSays()
        {

            var password               = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
            var server                 = AFirmwareServer(Secure: true, User: "station-firmware", Password: password);

            TrustsTheTestCA(node!);

            var (response, requestId)  = await UpdateFirmware($"https://station-firmware:{password}@127.0.0.1:{server.Port}{ImagePath}");

            Accepted(response);

            Assert.That(await UntilTold(requestId), Is.EqualTo(FirmwareStatus.Downloaded), $"The image was not downloaded over HTTPS.{Story(node, csms)}");

            Assert.Multiple(() => {
                Assert.That(File.ReadAllBytes(Staged(requestId)),                Is.EqualTo(Image));
                Assert.That(server.Requests.Single().Headers["Authorization"],   Does.StartWith("Basic "),  "The node did not sign in.");
                Assert.That(node!.Log.Recent(500).Select(entry => entry.Message), Has.None.Contains(password), "The node logged the password of the URL.");
            });

        }

        #endregion

        #region HTTP_TakesUpWhereTheAttemptBeforeStopped()

        /// <summary>
        /// The firmware server breaks off the first GET after 100000 bytes. The
        /// node says DownloadPaused, waits, and asks for the rest with a Range -
        /// "bytes=100000-" -, which the server answers 206. The image it keeps
        /// is whole, and nothing is in it twice.
        /// </summary>
        [Test]
        public async Task HTTP_TakesUpWhereTheAttemptBeforeStopped()
        {

            var server                 = AFirmwareServer();

            server.BreakNextAfter(100_000);

            var (response, requestId)  = await UpdateFirmware(server.URL(ImagePath), Retries: 2);

            Accepted(response);

            Assert.That(await UntilTold(requestId), Is.EqualTo(FirmwareStatus.Downloaded), $"The image was not downloaded at last.{Story(node, csms)}");

            var requests = server.Requests.ToArray();

            Assert.Multiple(() => {
                Assert.That(requests.Select(request => request.Answered),   Is.EqualTo(new[] { 200, 206 }),  "The node did not take up where it stopped.");
                Assert.That(requests[1].Headers["Range"],                    Is.EqualTo("bytes=100000-"),     "The node did not ask for the rest.");
                Assert.That(Told(requestId),                                 Is.EqualTo(new[] { FirmwareStatus.Downloading, FirmwareStatus.DownloadPaused, FirmwareStatus.Downloading, FirmwareStatus.Downloaded }),
                                                                                                              "The CSMS was told something else.");
                Assert.That(File.ReadAllBytes(Staged(requestId)),            Is.EqualTo(Image),               "The image the node keeps is not whole.");
            });

        }

        #endregion

        #region HTTP_AServerWithoutRanges_IsReadFromWhereItStopped()

        /// <summary>
        /// A firmware server that does not take ranges, and breaks off the first
        /// GET: the second attempt is answered with the whole image - 200 -, and
        /// the node passes over what it has already. The image it keeps is
        /// whole, and nothing is in it twice.
        /// </summary>
        [Test]
        public async Task HTTP_AServerWithoutRanges_IsReadFromWhereItStopped()
        {

            var server                 = AFirmwareServer();

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
        /// The firmware server fails once - 503 -, which may be better a moment
        /// later: the node says DownloadPaused, tries again, and the image is
        /// there.
        /// </summary>
        [Test]
        public async Task HTTP_TriesAgain_AfterTheServerFailed()
        {

            var server                 = AFirmwareServer();

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

        #region HTTP_AMissingImage_IsDownloadFailed()

        /// <summary>
        /// The image is not there - 404 -: DownloadFailed at once, as asking
        /// again would only be answered the same.
        /// </summary>
        [Test]
        public async Task HTTP_AMissingImage_IsDownloadFailed()
        {

            var server                 = AFirmwareServer();

            var (response, requestId)  = await UpdateFirmware(server.URL("/firmware/2.1.0/missing.bin"), Retries: 3);

            Accepted(response);

            Assert.That(await UntilTold(requestId), Is.EqualTo(FirmwareStatus.DownloadFailed), $"A missing image was told as something else.{Story(node, csms)}");

            Assert.Multiple(() => {
                Assert.That(server.Requests.Select(request => request.Answered),  Is.EqualTo(new[] { 404 }),                                                  "A missing image was asked for again.");
                Assert.That(Told(requestId),                                       Is.EqualTo(new[] { FirmwareStatus.Downloading, FirmwareStatus.DownloadFailed }));
            });

        }

        #endregion

        #region HTTP_ARedirect_IsNotFollowed()

        /// <summary>
        /// The firmware server answers with a redirect - 302 to another host -:
        /// it is not followed, as it would lead to a server the node's
        /// configuration does not name. DownloadFailed, not tried again, and the
        /// node's log says why.
        /// </summary>
        [Test]
        public async Task HTTP_ARedirect_IsNotFollowed()
        {

            var server                 = AFirmwareServer();

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

        #region HTTPS_FetchesNothing_FromAServerItDoesNotBelieve()

        /// <summary>
        /// A firmware server over HTTPS that the node's configuration names, but
        /// whose certificate nothing the node keeps vouches for: the
        /// UpdateFirmware is accepted, the TLS handshake ends the download
        /// before a request is sent, it is not tried again, and the CSMS is told
        /// DownloadFailed.
        /// </summary>
        [Test]
        public async Task HTTPS_FetchesNothing_FromAServerItDoesNotBelieve()
        {

            var server                 = AFirmwareServer(Secure: true);

            var (response, requestId)  = await UpdateFirmware(server.URL(ImagePath), Retries: 3);

            Accepted(response);

            Assert.That(await UntilTold(requestId), Is.EqualTo(FirmwareStatus.DownloadFailed), $"The download did not fail.{Story(node, csms)}");

            Assert.Multiple(() => {
                Assert.That(server.Requests,            Is.Empty,          "A request was sent to a server the node does not believe.");
                Assert.That(server.Connections,         Is.EqualTo(1),     "A server the node does not believe was asked again.");
                Assert.That(File.Exists(Staged(requestId)), Is.False,      "The node keeps something of a server it does not believe.");
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

            var server    = AFirmwareServer();
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
            await IsRefused(server.  URL("/firmware/2.1.0/"),               FileTransfers.InvalidURL,              "A URL that names no file");
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

    }

}
