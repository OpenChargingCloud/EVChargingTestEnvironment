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

using System.Text;
using System.Collections.Concurrent;

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
using cloud.charging.open.protocols.OCPPv2_1.CSMS;
using cloud.charging.open.protocols.OCPPv2_1.CS;

using cloud.charging.open.LogServer;


using OCPPServingNode  = cloud.charging.open.protocols.OCPP.Node.OCPPServingNode;

#endregion

namespace cloud.charging.open.TestEnvironment.Tests.Connections.HopByHop
{

    /// <summary>
    /// A node that uploads its log - a charging station, a local controller -
    /// is asked for it by its CSMS with a GetLog, and puts it on the log
    /// server: with a PUT over HTTP or HTTPS, or over SFTP. It goes only to a
    /// log server its own configuration names - never to one the request
    /// introduces -, signs in with the login the URL of the request carries,
    /// or else with the one its configuration says, believes an HTTPS server
    /// by the roots it keeps for its connections and an SFTP server by its
    /// pinned host key, writes nothing over, tries again only what may be
    /// better a moment later, and tells the CSMS how it went with
    /// LogStatusNotifications.
    /// </summary>
    /// <remarks>
    /// The same for every node that uploads its log: the node is the kind's to
    /// make (<see cref="AFileTransferTest.NewNode"/>), and everything else is here. What a
    /// log server does not do on purpose - fail, or have the file already - a
    /// log server of the test's own does (<see cref="TestFileServer"/>).
    /// </remarks>
    public abstract class AToTheLogServer : AFileTransferTest
    {

        #region Data

        protected LogServerNode?    logServer;
        protected String            uploaderPassword  = "";

        private readonly ConcurrentQueue<(Int32? RequestId, UploadLogStatus Status)>  told         = new();
        private          Int32                                                        requestIds   = 100;

        /// <summary>
        /// The node that uploads its log.
        /// </summary>
        protected OCPPServingNode? uploader
            => node;

        #endregion

        #region HearsOf(CSMS) / TearDown

        /// <summary>
        /// What the node tells of its uploads.
        /// </summary>
        protected override void HearsOf(CSMS.CSMS CSMS)
        {

            told.Clear();

            CSMS.Node.OCPP.IN.OnLogStatusNotificationRequestReceived += (timestamp, sender, connection, request, cancellationToken) => {
                told.Enqueue((request.LogRequestId, request.Status));
                return Task.CompletedTask;
            };

        }

        [TearDown]
        public async Task StopTheLogServer()
        {

            if (logServer is not null)
            {
                await logServer.Stop();
                await logServer.DisposeAsync();
                logServer = null;
            }

        }

        #endregion


        #region (protected) ALogServer(Secure = false)

        /// <summary>
        /// The log server, of its own directory: HTTP uploads on 127.0.0.1 and a
        /// port of its own - over TLS, with a certificate of the test's CA, where
        /// so -, SFTP uploads on its SSH server, and an uploader named as the
        /// node's identity, whose password <see cref="uploaderPassword"/> is.
        /// </summary>
        protected async Task<LogServerNode> ALogServer(Boolean Secure = false)
        {

            var directory  = NewNodeDirectory("log");
            var home       = Path.GetDirectoryName(directory.ConfigFile.Path)!;

            JProperty[] tls = [];

            if (Secure)
            {

                var (fullChain, privateKey, withKey) = AServerCertificateOfTheTestCAAsPEM();
                withKey.Dispose();

                File.WriteAllText(Path.Combine(home, "fullchain.pem"), fullChain);
                File.WriteAllText(Path.Combine(home, "privkey.pem"),   privateKey);

                // The upload port's own certificate, written as the web interface's is.
                tls = [ new JProperty("tls", new JObject(
                            new JProperty("certificate", new JObject(
                                new JProperty("fullchain",   Path.Combine(home, "fullchain.pem")),
                                new JProperty("privateKey",  Path.Combine(home, "privkey.pem"))
                            ))
                        )) ];

            }

            logServer      = await TestPorts.StartedOnFreshPorts(() => {

                                 File.WriteAllText(
                                     directory.ConfigFile.Path,
                                     new JObject(
                                         [
                                             new JProperty("nts",        TestParties.NoTimeServers),
                                             new JProperty(LogServerSettings.SectionName, new JObject(
                                                 [
                                                     new JProperty("httpListen",  "127.0.0.1"),
                                                     new JProperty("httpPort",    TestPorts.Free().ToUInt16()),
                                                     .. tls
                                                 ]
                                             )),
                                             new JProperty("ssh",        new JObject(
                                                 new JProperty("enabled",     true),
                                                 new JProperty("port",        TestPorts.Free().ToUInt16()),
                                                 new JProperty("sftp",        true)
                                             ))
                                         ]
                                     ).ToString()
                                 );

                                 return new LogServerNode(HTTPPort:          TestPorts.Free(),
                                                          AccountsPath:      directory.Accounts,
                                                          ConfigFile:        directory.ConfigFile,
                                                          CertificatesPath:  Path.Combine(home, "certificates"),
                                                          LogToConsole:      false,
                                                          BridgeDebugLog:    false);

                             });

            Assert.That(logServer.UploadsOverHTTPS, Is.EqualTo(Secure), $"The upload port of the log server does {(Secure ? "not " : "")}speak TLS.{Story(logServer)}");

            var made       = await logServer.AddUploader(Identity, null, "the test");

            Assert.That(made.Password, Is.Not.Null, made.Said);

            uploaderPassword = made.Password!;

            return logServer;

        }

        #endregion

        #region (protected) UploadPort / LogServerURL(...) / TheLogServer(...)

        /// <summary>
        /// The port the log server takes HTTP uploads on.
        /// </summary>
        protected UInt16 UploadPort
            => logServer!.Settings.HTTPPort.ToUInt16();

        /// <summary>
        /// The URL of the node's directory at the log server, as a CSMS gives it
        /// in a GetLog - with the login and the password of its uploader where
        /// they are given.
        /// </summary>
        protected String LogServerURL(String?  User      = null,
                                      String?  Password  = null)

            => $"{(logServer!.UploadsOverHTTPS ? "https" : "http")}://" +
               (User is not null ? $"{Uri.EscapeDataString(User)}:{Uri.EscapeDataString(Password ?? "")}@" : "") +
               $"127.0.0.1:{UploadPort}/{Uri.EscapeDataString(Identity)}/";

        /// <summary>
        /// The log server as the node's configuration holds it: every node's own
        /// directory there - and the login it signs in with, where it is given.
        /// </summary>
        protected HTTPFileServer TheLogServer(String?  User      = null,
                                              String?  Password  = null)

            => new ("logs",
                    new Uri($"{(logServer!.UploadsOverHTTPS ? "https" : "http")}://127.0.0.1:{UploadPort}/{{id}}/"),
                    User,
                    Password);

        #endregion


        #region (protected) GetLog(Location, LogType = null, Retries = null, RetryInterval = null)

        /// <summary>
        /// The CSMS asks the node for its log, to be put at the given location:
        /// the response, and the id of the request its notifications carry.
        /// </summary>
        protected async Task<(GetLogResponse Response, Int32 RequestId)> GetLog(String           Location,
                                                                                 LogType?         LogType        = null,
                                                                                 Byte?            Retries        = null,
                                                                                 TimeSpan?        RetryInterval  = null,
                                                                                 DateTimeOffset?  Oldest         = null,
                                                                                 DateTimeOffset?  Latest         = null)
        {

            var requestId = Interlocked.Increment(ref requestIds);

            var response  = await csms!.Node.GetLog(
                                      SourceRouting.To(NetworkingNode_Id.Parse(login.ToString())),
                                      requestId,
                                      LogType ?? protocols.OCPPv2_1.LogType.DiagnosticsLog,
                                      new LogParameters(URL.Parse(Location), Oldest, Latest),
                                      Retries,
                                      RetryInterval ?? TimeSpan.FromMilliseconds(500)
                                  );

            return (response, requestId);

        }

        #endregion

        #region (protected) Told(RequestId) / UntilTold(RequestId)

        /// <summary>
        /// What the node told its CSMS of the upload of the given request, in order.
        /// </summary>
        protected UploadLogStatus[] Told(Int32 RequestId)

            => [.. told.Where (notification => notification.RequestId == RequestId).
                        Select(notification => notification.Status)];

        /// <summary>
        /// Wait until the node told its CSMS how the upload of the given request
        /// ended - anything but Uploading -, and say how.
        /// </summary>
        protected async Task<UploadLogStatus> UntilTold(Int32 RequestId)
        {

            await Until(() => Told(RequestId).Any(status => status != UploadLogStatus.Uploading),
                        $"The CSMS being told how the upload of request {RequestId} ended",
                        uploader, logServer, csms);

            return Told(RequestId).Last();

        }

        #endregion

        #region (protected) Accepted(Response) / Rejected(Response, ReasonCode, Why)

        protected void Accepted(GetLogResponse Response)
        {

            Assert.That(Response.Status,    Is.EqualTo(LogStatus.Accepted), $"The GetLog was not accepted: {Response.StatusInfo?.ReasonCode} {Response.StatusInfo?.AdditionalInfo}{Story(uploader)}");
            Assert.That(Response.Filename,  Is.Not.Null.And.EndsWith(NodeLogFiles.Extension), "The response does not say the name the log gets.");

        }

        protected void Rejected(GetLogResponse  Response,
                                String          ReasonCode,
                                String          Why)
        {

            Assert.That(Response.Status,                 Is.EqualTo(LogStatus.Rejected), $"{Why}: the GetLog was not rejected.{Story(uploader)}");
            Assert.That(Response.StatusInfo?.ReasonCode, Is.EqualTo(ReasonCode),         $"{Why}: the GetLog was rejected for something else - {Response.StatusInfo?.AdditionalInfo}");

        }

        #endregion

        #region (protected) TheUpload(Response) / LinesOf(Upload) / LinesOf(File)

        /// <summary>
        /// The upload the log server kept of the file a response named.
        /// </summary>
        protected UploadEvent TheUpload(GetLogResponse Response)
        {

            var uploads = logServer!.Uploads.All.Where(upload => upload.FileName == Response.Filename).ToArray();

            Assert.That(uploads, Has.Length.EqualTo(1), $"The log server does not keep the log '{Response.Filename}', or keeps it more than once.{Story(logServer)}");

            return uploads[0];

        }

        /// <summary>
        /// The entries of an uploaded log: one JSON object per line.
        /// </summary>
        protected JObject[] LinesOf(UploadEvent Upload)
            => LinesOf(File.ReadAllBytes(logServer!.Uploads.FileOf(Upload)));

        protected static JObject[] LinesOf(Byte[] File)

            => [.. Encoding.UTF8.GetString(File).
                       Split('\n', StringSplitOptions.RemoveEmptyEntries).
                       Select(JObject.Parse)];

        #endregion

        #region (protected) Directory

        /// <summary>
        /// The node's own directory at a log server of the test's own.
        /// </summary>
        protected String Directory
            => $"/{Uri.EscapeDataString(Identity)}/";

        #endregion


        #region HTTP_UploadsItsLog()

        /// <summary>
        /// The CSMS asks for the node's DiagnosticsLog, to be put in its own
        /// directory at the log server over HTTP, with the login and the
        /// password of its uploader in the URL, as a CSMS gives them. The log
        /// server is one the node's configuration names. The node accepts, says
        /// the name the log gets, puts it there with one PUT - signed in as the
        /// URL says, and saying in its log that the password crossed the network
        /// in the clear -, and tells the CSMS Uploading and then Uploaded. The
        /// log server keeps it as the node's, by HTTP; it is the node's log, one
        /// entry per line, among them that it was asked for it.
        /// </summary>
        [Test]
        public async Task HTTP_UploadsItsLog()
        {

            await ALogServer();

            HoldsTheHTTPFileServers(TheLogServer());

            var (response, requestId) = await GetLog(LogServerURL(Identity, uploaderPassword));

            Accepted(response);

            Assert.That(await UntilTold(requestId), Is.EqualTo(UploadLogStatus.Uploaded), $"The log was not uploaded.{Story(uploader, logServer, csms)}");

            var upload = TheUpload(response);
            var lines  = LinesOf(upload);

            Assert.Multiple(() => {

                Assert.That(Told(requestId),          Is.EqualTo(new[] { UploadLogStatus.Uploading, UploadLogStatus.Uploaded }), "The CSMS was told something else.");

                Assert.That(upload.Protocol,          Is.EqualTo(UploadProtocol.HTTP));
                Assert.That(upload.Uploader,          Is.EqualTo(Identity),                    "The log server took it from another uploader.");
                Assert.That(upload.ChargingStation,   Is.EqualTo(Identity),                    "The log server keeps it as another one's.");
                Assert.That(upload.Size,              Is.Positive);

                Assert.That(lines.Select(line => line["message"]?.Value<String>()),
                            Has.Some.Contains("DiagnosticsLog"),                                "The log does not say that the node was asked for it.");

                Assert.That(uploader!.Log.Recent(200).Select(entry => entry.Message),
                            Has.Some.Contains("crosses the network in the clear"),              "The node does not say that its password went over http://.");
                Assert.That(uploader!.Log.Recent(500).Select(entry => entry.Message),
                            Has.None.Contains(uploaderPassword),                                "The node logged the password of the URL.");

            });

        }

        #endregion

        #region HTTP_UploadsItsLogFiles_OfTheTimeAskedFor()

        /// <summary>
        /// The node writes its log to files, one a day: a GetLog uploads them,
        /// and not only what its event log keeps in memory - a day before this
        /// start as well, where the time asked for reaches back to it, and not
        /// where it does not.
        /// </summary>
        [Test]
        public async Task HTTP_UploadsItsLogFiles_OfTheTimeAskedFor()
        {

            await ALogServer();

            HoldsTheHTTPFileServers(TheLogServer());

            Assert.That(uploader!.LogPath, Is.Not.Null, "The node writes no log files.");

            File.WriteAllLines(Path.Combine(uploader.LogPath!, $"{uploader.Kind.LogFilePrefix}-2026-11-20.log"),
                               [ "2026-11-20T08:00:00.000Z info    [boot] Started four days before this test was written.",
                                 "2026-11-20T09:15:00.000Z warning [security auth] A login refused that day." ]);

            // One after the other: a GetLog takes the place of one still running.
            async Task<GetLogResponse> Uploaded(LogType? Kind = null, DateTimeOffset? Oldest = null, DateTimeOffset? Latest = null)
            {

                var (response, requestId) = await GetLog(LogServerURL(Identity, uploaderPassword), LogType: Kind, Oldest: Oldest, Latest: Latest);

                Accepted(response);

                Assert.That(await UntilTold(requestId), Is.EqualTo(UploadLogStatus.Uploaded), $"The log was not uploaded.{Story(uploader, logServer, csms)}");

                return response;

            }

            var whole    = await Uploaded();
            var recent   = await Uploaded(Oldest: DateTimeOffset.UtcNow.AddHours(-1));
            var thatDay  = await Uploaded(protocols.OCPPv2_1.LogType.SecurityLog,
                                          Oldest: new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero),
                                          Latest: new DateTimeOffset(2026, 11, 20, 23, 59, 59, TimeSpan.Zero));

            var wholeLines    = LinesOf(TheUpload(whole)). Select(line => line["message"]?.Value<String>()).ToArray();
            var recentLines   = LinesOf(TheUpload(recent)).Select(line => line["message"]?.Value<String>()).ToArray();
            var thatDayLines  = LinesOf(TheUpload(thatDay)).Select(line => line["message"]?.Value<String>()).ToArray();

            Assert.Multiple(() => {

                Assert.That(wholeLines.Take(2),    Is.EqualTo(new[] { "Started four days before this test was written.", "A login refused that day." }),
                                                                                                "The day before this start is not where the log begins.");
                Assert.That(wholeLines,            Has.Some.Contains("DiagnosticsLog"),          "What the node logged now is not in it.");

                Assert.That(recentLines,           Has.None.Contains("four days before"),        "A day before the time asked for is in it.");
                Assert.That(recentLines,           Has.Some.Contains("DiagnosticsLog"));

                Assert.That(thatDayLines,          Is.EqualTo(new[] { "A login refused that day." }),
                                                                                                "Not the security entries of the day asked for, and nothing else.");

            });

        }

        #endregion

        #region HTTPS_UploadsItsLog_SigningInAsItsConfigurationSays()

        /// <summary>
        /// The same over HTTPS, with a URL that carries no login: the node
        /// believes the log server by the test's CA - a root it keeps for its
        /// connections - and signs in with the login and the password its
        /// configuration names for that log server.
        /// </summary>
        [Test]
        public async Task HTTPS_UploadsItsLog_SigningInAsItsConfigurationSays()
        {

            await ALogServer(Secure: true);

            TrustsTheTestCA(uploader!);

            HoldsTheHTTPFileServers(TheLogServer(Identity, uploaderPassword));

            var (response, requestId) = await GetLog(LogServerURL());

            Accepted(response);

            Assert.That(await UntilTold(requestId), Is.EqualTo(UploadLogStatus.Uploaded), $"The log was not uploaded over HTTPS.{Story(uploader, logServer, csms)}");

            var upload = TheUpload(response);

            Assert.Multiple(() => {
                Assert.That(upload.Protocol,  Is.EqualTo(UploadProtocol.HTTP));
                Assert.That(upload.Uploader,  Is.EqualTo(Identity));
                Assert.That(uploader!.Log.Recent(200).Select(entry => entry.Message),
                            Has.None.Contains("in the clear"),  "The node says its password went in the clear, over TLS.");
            });

        }

        #endregion

        #region HTTP_TheLoginOfTheURL_GoesBeforeThatOfItsConfiguration()

        /// <summary>
        /// The node's configuration names the log server with a password that
        /// is no longer right. A GetLog whose URL carries the right one is
        /// uploaded - the URL's goes before the configuration's -; one whose URL
        /// carries none signs in with the configuration's, is refused by the log
        /// server - 401 -, which the CSMS is told as PermissionDenied, and is not
        /// tried again.
        /// </summary>
        [Test]
        public async Task HTTP_TheLoginOfTheURL_GoesBeforeThatOfItsConfiguration()
        {

            await ALogServer();

            HoldsTheHTTPFileServers(TheLogServer(Identity, "a password of before"));

            var (withLogin, uploaded) = await GetLog(LogServerURL(Identity, uploaderPassword));

            Accepted(withLogin);

            Assert.That(await UntilTold(uploaded), Is.EqualTo(UploadLogStatus.Uploaded), $"The login of the URL did not go before that of the configuration.{Story(uploader, logServer, csms)}");

            var refusedBefore = logServer!.HTTPUploadsRefused;

            var (withoutLogin, refused) = await GetLog(LogServerURL(), Retries: 3);

            Accepted(withoutLogin);

            Assert.That(await UntilTold(refused), Is.EqualTo(UploadLogStatus.PermissionDenied), $"A login the log server refused was told as something else.{Story(uploader, logServer, csms)}");

            Assert.Multiple(() => {
                Assert.That(logServer.HTTPUploadsRefused - refusedBefore,  Is.EqualTo(1),  "A login the log server refused was tried again.");
                Assert.That(logServer.Uploads.Count,                       Is.EqualTo(1),  "The log server keeps more than the one upload.");
            });

        }

        #endregion

        #region HTTPS_SendsNothing_ToALogServerItDoesNotBelieve()

        /// <summary>
        /// The log server over HTTPS, named by the node's configuration, but its
        /// certificate is of a CA nothing the node keeps vouches for: the GetLog
        /// is accepted - nothing of the network is known before the answer -,
        /// the TLS handshake ends the upload before the password or a byte of
        /// the log is sent, and it is not tried again, however often the CSMS
        /// allowed. The CSMS is told UploadFailure, and the node's log says why.
        /// </summary>
        [Test]
        public async Task HTTPS_SendsNothing_ToALogServerItDoesNotBelieve()
        {

            await ALogServer(Secure: true);

            HoldsTheHTTPFileServers(TheLogServer());

            var (response, requestId) = await GetLog(LogServerURL(Identity, uploaderPassword), Retries: 3);

            Accepted(response);

            Assert.That(await UntilTold(requestId), Is.EqualTo(UploadLogStatus.UploadFailure), $"The upload did not fail.{Story(uploader, logServer, csms)}");

            Assert.Multiple(() => {
                Assert.That(logServer!.Uploads.Count,  Is.Zero,  "The log server took a log of a node that does not believe it.");
                Assert.That(uploader!.Log.Recent(200).Where(entry => entry.Message.Contains("ended as CertificateRefused")),
                            Has.Exactly(1).Items,                 "A log server the node does not believe was not refused, or was asked again.");
            });

        }

        #endregion

        #region HTTP_RefusesWhatItsConfigurationDoesNotName()

        /// <summary>
        /// A request chooses among the log servers the node holds, and never
        /// introduces one. Another directory of the log server, the directory of
        /// another node, the same directory at another port or over another
        /// scheme: FileServerNotPermitted, one reason code for all of them,
        /// before anything is sent - and the response does not say what the
        /// node holds. A URL with an empty login or a query: InvalidURL. FTP:
        /// NotSupported. Nothing reaches a server, and nothing is told of an
        /// upload that never started.
        /// </summary>
        [Test]
        public async Task HTTP_RefusesWhatItsConfigurationDoesNotName()
        {

            await ALogServer();

            var stranger = ATestServer();

            HoldsTheHTTPFileServers(TheLogServer());

            var requests = new List<Int32>();
            var port     = UploadPort;
            var user     = $"{Uri.EscapeDataString(Identity)}:{Uri.EscapeDataString(uploaderPassword)}@";

            async Task IsRefused(String Location, String ReasonCode, String Why)
            {

                var (response, requestId) = await GetLog(Location);

                Rejected(response, ReasonCode, Why);

                Assert.That(response.StatusInfo?.AdditionalInfo ?? "", Does.Not.Contain(port.ToString()).And.Not.Contain("logs"),
                            $"{Why}: the response says which file servers the node holds.");

                requests.Add(requestId);

            }

            await IsRefused($"http://{user}127.0.0.1:{port}/",                                 FileTransfers.FileServerNotPermitted,  "The root of the log server");
            await IsRefused($"http://{user}127.0.0.1:{port}/somebody-else/",                   FileTransfers.FileServerNotPermitted,  "The directory of another node");
            await IsRefused($"http://{user}127.0.0.1:{stranger.Port}{Directory}",              FileTransfers.FileServerNotPermitted,  "A server at another port");
            await IsRefused($"https://{user}127.0.0.1:{port}{Directory}",                      FileTransfers.FileServerNotPermitted,  "The log server over another scheme");
            await IsRefused($"http://:{Uri.EscapeDataString(uploaderPassword)}@127.0.0.1:{port}{Directory}",
                                                                                               FileTransfers.InvalidURL,              "A URL with an empty login");
            await IsRefused($"http://{user}127.0.0.1:{port}{Directory}?overwrite=true",        FileTransfers.InvalidURL,              "A URL with a query");
            await IsRefused($"ftp://{user}127.0.0.1:{port}{Directory}",                        FileTransfers.NotSupported,            "A URL of FTP");

            // The notifications of the request before would be here by now.
            await Task.Delay(500);

            Assert.Multiple(() => {
                Assert.That(logServer!.Uploads.Count,                 Is.Zero,   "Something reached the log server.");
                Assert.That(logServer.HTTPUploadsRefused,             Is.Zero,   "Something was sent to the log server.");
                Assert.That(stranger.Connections,                     Is.Zero,   "Something reached a server the node's configuration does not name.");
                Assert.That(requests.SelectMany(id => Told(id)),      Is.Empty,  "The CSMS was told of an upload that never started.");
            });

        }

        #endregion

        #region HTTP_UploadsItsSecurityLog()

        /// <summary>
        /// The CSMS asks for the SecurityLog: what the node logged as a matter
        /// of security, and nothing else, under a name that says so.
        /// </summary>
        [Test]
        public async Task HTTP_UploadsItsSecurityLog()
        {

            await ALogServer();

            HoldsTheHTTPFileServers(TheLogServer());

            // Something of security: a request for an upload to a server nobody named.
            _ = await GetLog("http://127.0.0.1:1/nowhere/");

            var (response, requestId) = await GetLog(LogServerURL(Identity, uploaderPassword), LogType: LogType.SecurityLog);

            Accepted(response);

            Assert.That(await UntilTold(requestId), Is.EqualTo(UploadLogStatus.Uploaded), $"The security log was not uploaded.{Story(uploader, logServer, csms)}");

            var lines = LinesOf(TheUpload(response));

            Assert.Multiple(() => {
                Assert.That(response.Filename,  Does.Contain("SecurityLog"),  "The name does not say what it is.");
                Assert.That(lines,              Is.Not.Empty,                 "The security log is empty.");
                Assert.That(lines.Select(line => line["tags"]?.Values<String>() ?? []),
                            Has.All.Contains("security"),                      "The security log has entries that are not of security.");
                Assert.That(lines.Select(line => line["message"]?.Value<String>()),
                            Has.Some.Contains("is refused"),                   "The security log does not have the refused request.");
            });

        }

        #endregion

        #region HTTP_WritesNothingOver()

        /// <summary>
        /// A log server that has a file of that name already, and says so - 412
        /// to "If-None-Match: *": nothing is written over, and as asking again
        /// would only be answered the same, it is not tried again.
        /// </summary>
        [Test]
        public async Task HTTP_WritesNothingOver()
        {

            var server = ATestServer();

            HoldsTheHTTPFileServers(new HTTPFileServer("logs", new Uri(server.URL("/{id}/"))));

            server.AnswerNext(412, 412, 412, 412);

            var (response, requestId) = await GetLog(server.URL(Directory), Retries: 3);

            Accepted(response);

            Assert.That(await UntilTold(requestId), Is.EqualTo(UploadLogStatus.UploadFailure), $"The upload did not fail.{Story(uploader, csms)}");

            Assert.Multiple(() => {
                Assert.That(server.Requests,                                Has.Count.EqualTo(1),  "A file that is there was asked to be written over again.");
                Assert.That(server.Requests.Single().Headers["If-None-Match"], Is.EqualTo("*"),    "The PUT may write over a file that is there.");
                Assert.That(server.Files,                                   Is.Empty);
            });

        }

        #endregion

        #region HTTP_TriesAgain_AfterTheServerFailed()

        /// <summary>
        /// A log server that fails twice - 503 and 500 -, which may be better a
        /// moment later: the node tries again, as often as the CSMS allowed, and
        /// the third attempt puts the log there - the same log. The CSMS is told
        /// Uploading once, and Uploaded.
        /// </summary>
        [Test]
        public async Task HTTP_TriesAgain_AfterTheServerFailed()
        {

            var server = ATestServer();

            HoldsTheHTTPFileServers(new HTTPFileServer("logs", new Uri(server.URL("/{id}/"))));

            server.AnswerNext(503, 500);

            var (response, requestId) = await GetLog(server.URL(Directory), Retries: 3, RetryInterval: TimeSpan.FromMilliseconds(200));

            Accepted(response);

            Assert.That(await UntilTold(requestId), Is.EqualTo(UploadLogStatus.Uploaded), $"The log was not uploaded at last.{Story(uploader, csms)}");

            var requests = server.Requests.ToArray();

            Assert.Multiple(() => {
                Assert.That(requests.Select(request => request.Answered),               Is.EqualTo(new[] { 503, 500, 201 }),                                      "The node did not try again, or tried more often.");
                Assert.That(requests.Select(request => Convert.ToHexString(request.Body)).Distinct(), Has.Exactly(1).Items,                                       "An attempt sent another log than the one before.");
                Assert.That(Told(requestId),                                             Is.EqualTo(new[] { UploadLogStatus.Uploading, UploadLogStatus.Uploaded }), "The CSMS was told something else.");
                Assert.That(server.FilesIn(Directory).Keys,                              Is.EqualTo(new[] { response.Filename }),                                  "The log is not where it was to go.");
            });

        }

        #endregion

        #region HTTP_AServerThatTakesNoPUT_IsNotSupported()

        /// <summary>
        /// A server that takes no PUT there - 405 -: NotSupportedOperation, and
        /// not tried again.
        /// </summary>
        [Test]
        public async Task HTTP_AServerThatTakesNoPUT_IsNotSupported()
        {

            var server = ATestServer();

            HoldsTheHTTPFileServers(new HTTPFileServer("logs", new Uri(server.URL("/{id}/"))));

            server.AnswerNext(405, 405);

            var (response, requestId) = await GetLog(server.URL(Directory), Retries: 1);

            Accepted(response);

            Assert.That(await UntilTold(requestId),  Is.EqualTo(UploadLogStatus.NotSupportedOperation), $"A server without PUT was told as something else.{Story(uploader, csms)}");
            Assert.That(server.Requests,             Has.Count.EqualTo(1), "A server without PUT was asked again.");

        }

        #endregion


        #region (private) HeldOverSFTP(Pin = null, Confirmed = true)

        /// <summary>
        /// The log server held by the node as a file server over SFTP, pinned to
        /// its host key: with a key of the node's own to sign in with as its
        /// uploader, enrolled at the log server and - where so - confirmed as
        /// enrolled. The URL of the log server's "/upload".
        /// </summary>
        /// <param name="Pin">The host key the node is held to there; the log server's own where none is given.</param>
        /// <param name="Confirmed">Whether the key the node signs in with is confirmed as enrolled.</param>
        private async Task<String> HeldOverSFTP(String?  Pin        = null,
                                            Boolean  Confirmed  = true)
        {

            await ALogServer();

            var port   = logServer!.SSHPort!.Value;
            var user   = User_Id.Parse(Identity);

            // The log server is an SSH peer of the node's configuration that says what it may be used for.
            var held   = await uploader!.TryUpdateSSHPeers(peers => new JArray(
                                   new SSHPeerConfiguration("logs",
                                                            DomainName.Parse("127.0.0.1"),
                                                            user,
                                                            new NTSServerSSH(port, HostKeyPins: [ Pin ?? logServer.SSHHostKey!.Split(' ')[1] ]),
                                                            new SSHFileTransfers(FileTransferDirections.Upload,
                                                                                 UploadPaths: [ $"/{LogServerNode.SftpUploadArea}/" ])).ToJSON()
                               ));

            Assert.That(held.Error, Is.Null, held.Error);

            // Its key, enrolled at the log server for its uploader.
            var line   = uploader.FileTransferPublicKey("logs", user);

            Assert.That(line, Is.Not.Null, $"The node made no key to sign in with at the log server.{Story(uploader)}");

            var added  = await logServer.AuthorizeSSHKey(user, line!);

            Assert.That(added.IsAdded, Is.True, added.Reason);

            if (Confirmed)
                Assert.That(uploader.ConfirmFileTransferKey("logs", user, line), Is.Null);

            return $"sftp://{user}@127.0.0.1:{port}/{LogServerNode.SftpUploadArea}/";

        }

        #endregion

        #region SFTP_UploadsItsLog()

        /// <summary>
        /// The CSMS asks for the node's DiagnosticsLog, to be put in "/upload" of
        /// the log server over SFTP, which the node's configuration holds: the
        /// node connects to it on a connection of its own, believes it by the
        /// host key it is pinned to, signs in as its uploader with its own key
        /// enrolled there, and the log server keeps the log as the node's, over
        /// SFTP, under the name the response said. The CSMS is told Uploading
        /// and Uploaded.
        /// </summary>
        [Test]
        public async Task SFTP_UploadsItsLog()
        {

            var location               = await HeldOverSFTP();

            var (response, requestId)  = await GetLog(location);

            Accepted(response);

            Assert.That(await UntilTold(requestId), Is.EqualTo(UploadLogStatus.Uploaded), $"The log was not uploaded over SFTP.{Story(uploader, logServer, csms)}");

            var upload = TheUpload(response);

            Assert.Multiple(() => {
                Assert.That(Told(requestId),          Is.EqualTo(new[] { UploadLogStatus.Uploading, UploadLogStatus.Uploaded }), "The CSMS was told something else.");
                Assert.That(upload.Protocol,          Is.EqualTo(UploadProtocol.SFTP));
                Assert.That(upload.Uploader,          Is.EqualTo(Identity),                     "The log server took it from another uploader.");
                Assert.That(logServer!.Uploads.Count, Is.EqualTo(1),                            "The log server keeps more: what an unfinished upload leaves, say.");
                Assert.That(LinesOf(upload).Select(line => line["message"]?.Value<String>()),
                            Has.Some.Contains("DiagnosticsLog"),                                 "The log does not say that the node was asked for it.");
            });

        }

        #endregion

        #region SFTP_RefusesWhatItsConfigurationDoesNotName()

        /// <summary>
        /// Over SFTP the same: a directory the log server is not allowed uploads
        /// to, another port, another login - FileServerNotPermitted before a
        /// packet leaves the node; a password in the URL - InvalidURL.
        /// </summary>
        [Test]
        public async Task SFTP_RefusesWhatItsConfigurationDoesNotName()
        {

            await HeldOverSFTP();

            var port = logServer!.SSHPort!.Value;

            async Task IsRefused(String Location, String ReasonCode, String Why)
            {
                var (response, _) = await GetLog(Location);
                Rejected(response, ReasonCode, Why);
            }

            await IsRefused($"sftp://{Identity}@127.0.0.1:{port}/firmware/",              FileTransfers.FileServerNotPermitted,  "A directory the log server is not allowed uploads to");
            await IsRefused($"sftp://{Identity}@127.0.0.1:{TestPorts.Free()}/upload/",    FileTransfers.FileServerNotPermitted,  "Another port");
            await IsRefused($"sftp://somebody@127.0.0.1:{port}/upload/",                  FileTransfers.FileServerNotPermitted,  "Another login");
            await IsRefused($"sftp://{Identity}:secret@127.0.0.1:{port}/upload/",         FileTransfers.InvalidURL,              "A password in the URL");

            Assert.That(logServer.Uploads.Count, Is.Zero, "Something reached the log server.");

        }

        #endregion

        #region SFTP_SendsNothing_ToALogServerThatShowsAnotherHostKey()

        /// <summary>
        /// The node is pinned to another host key than the one the log server
        /// shows: the GetLog is accepted, the handshake ends the upload, nothing
        /// is sent, it is not tried again, and the CSMS is told UploadFailure.
        /// </summary>
        [Test]
        public async Task SFTP_SendsNothing_ToALogServerThatShowsAnotherHostKey()
        {

            var another                = SshFingerprint.Sha256(SshHostKey.GenerateEd25519().PublicKeyBlob);

            var location               = await HeldOverSFTP(Pin: another);

            var (response, requestId)  = await GetLog(location, Retries: 2);

            Accepted(response);

            Assert.That(await UntilTold(requestId), Is.EqualTo(UploadLogStatus.UploadFailure), $"The upload to a server of another host key did not fail.{Story(uploader, logServer, csms)}");

            Assert.Multiple(() => {
                Assert.That(logServer!.Uploads.Count,  Is.Zero,  "Something reached a server of another host key.");
                Assert.That(uploader!.Log.Recent(200).Where(entry => entry.Message.Contains("ended as HostKeyRefused")),
                            Has.Exactly(1).Items,                 "A server of another host key was not refused, or was asked again.");
            });

        }

        #endregion

        #region SFTP_RefusesAnUpload_UntilItsKeyIsConfirmed()

        /// <summary>
        /// The node made a key to sign in with at the log server, but nobody
        /// confirmed that the log server holds it: FileServerNotPermitted, as the
        /// server would refuse the login anyway. Once it is confirmed, the log
        /// is uploaded.
        /// </summary>
        [Test]
        public async Task SFTP_RefusesAnUpload_UntilItsKeyIsConfirmed()
        {

            var location               = await HeldOverSFTP(Confirmed: false);

            var (refused, _)           = await GetLog(location);

            Rejected(refused, FileTransfers.FileServerNotPermitted, "A key not confirmed as enrolled");

            Assert.That(uploader!.ConfirmFileTransferKey("logs", User_Id.Parse(Identity)), Is.Null);

            var (accepted, requestId)  = await GetLog(location);

            Accepted(accepted);

            Assert.That(await UntilTold(requestId), Is.EqualTo(UploadLogStatus.Uploaded), $"The log was not uploaded once the key was confirmed.{Story(uploader, logServer, csms)}");

            Assert.That(TheUpload(accepted).Protocol, Is.EqualTo(UploadProtocol.SFTP));

        }

        #endregion

    }

}
