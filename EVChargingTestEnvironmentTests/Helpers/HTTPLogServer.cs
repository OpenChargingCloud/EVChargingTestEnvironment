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

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Globalization;
using System.Collections.Concurrent;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

#endregion

namespace cloud.charging.open.TestEnvironment.Tests
{

    /// <summary>
    /// A log server of the test's own: HTTP/1.1, or HTTPS with a certificate
    /// it is given, that takes a file with a PUT - and writes nothing over:
    /// with "If-None-Match: *" a file that is there is answered 412. It may
    /// ask for HTTP Basic authentication, and may be told to answer the next
    /// requests with a status of the test's choosing.
    /// </summary>
    /// <remarks>
    /// One request per connection, as the node's uploads make one PUT per
    /// attempt: what arrived and what was answered is kept for the test.
    /// </remarks>
    public sealed class HTTPLogServer : IAsyncDisposable
    {

        #region Received

        /// <summary>
        /// A request as it arrived, and what it was answered.
        /// </summary>
        public sealed record Received(String                               Method,
                                      String                               Path,
                                      IReadOnlyDictionary<String, String>  Headers,
                                      Byte[]                               Body,
                                      Int32                                Answered);

        #endregion

        #region Data

        private readonly TcpListener                      listener;
        private readonly X509Certificate2?                certificate;
        private readonly CancellationTokenSource          stop       = new();
        private readonly Task                             accepting;
        private readonly ConcurrentQueue<Int32>           answers    = new();

        #endregion

        #region Properties

        /// <summary>
        /// The port it listens on, on 127.0.0.1.
        /// </summary>
        public Int32                                   Port       { get; }

        /// <summary>
        /// Whether it speaks TLS.
        /// </summary>
        public Boolean                                 IsSecure
            => certificate is not null;

        /// <summary>
        /// The login of HTTP Basic authentication it asks for, or null.
        /// </summary>
        public String?                                 User       { get; init; }

        /// <summary>
        /// Its password.
        /// </summary>
        public String?                                 Password   { get; init; }

        /// <summary>
        /// The files it took, by their path.
        /// </summary>
        public ConcurrentDictionary<String, Byte[]>    Files      { get; } = new();

        /// <summary>
        /// Every request that arrived, in order.
        /// </summary>
        public ConcurrentQueue<Received>               Requests   { get; } = new();

        /// <summary>
        /// How many connections were made to it: also those that ended in the
        /// TLS handshake.
        /// </summary>
        public Int32                                   Connections
            => connections;

        private Int32 connections;

        #endregion

        #region Constructor(s)

        private HTTPLogServer(X509Certificate2? Certificate)
        {

            certificate  = Certificate;
            listener     = new TcpListener(IPAddress.Loopback, 0);

            listener.Start();

            Port         = ((IPEndPoint) listener.LocalEndpoint).Port;
            accepting    = Task.Run(Accepting);

        }

        /// <summary>
        /// A log server over HTTP, listening.
        /// </summary>
        public static HTTPLogServer OverHTTP(String? User = null, String? Password = null)
            => new (null) { User = User, Password = Password };

        /// <summary>
        /// A log server over HTTPS with the given certificate, listening.
        /// </summary>
        public static HTTPLogServer OverHTTPS(X509Certificate2 Certificate, String? User = null, String? Password = null)
            => new (Certificate) { User = User, Password = Password };

        #endregion


        #region URL(Path)

        /// <summary>
        /// The URL of the given path at this server.
        /// </summary>
        public String URL(String Path)
            => $"{(IsSecure ? "https" : "http")}://127.0.0.1:{Port}{Path}";

        #endregion

        #region AnswerNext(params Statuses)

        /// <summary>
        /// Answer the next requests with the given statuses, one each - whatever
        /// they are - and the requests after them as usual.
        /// </summary>
        public void AnswerNext(params Int32[] Statuses)
        {
            foreach (var status in Statuses)
                answers.Enqueue(status);
        }

        #endregion

        #region FilesIn(Directory)

        /// <summary>
        /// The files it took in the given directory, by their names.
        /// </summary>
        public IReadOnlyDictionary<String, Byte[]> FilesIn(String Directory)
        {

            var prefix = Directory.EndsWith('/') ? Directory : Directory + "/";

            return Files.Where (file => file.Key.StartsWith(prefix, StringComparison.Ordinal) && !file.Key[prefix.Length..].Contains('/')).
                         ToDictionary(file => Uri.UnescapeDataString(file.Key[prefix.Length..]), file => file.Value);

        }

        #endregion


        #region (private) Accepting()

        private async Task Accepting()
        {
            while (!stop.IsCancellationRequested)
            {

                TcpClient client;

                try
                {
                    client = await listener.AcceptTcpClientAsync(stop.Token);
                }
                catch (Exception)
                {
                    return;
                }

                Interlocked.Increment(ref connections);

                _ = Task.Run(async () => {
                    using (client)
                    {
                        try
                        {
                            await Serve(client);
                        }
                        catch (Exception)
                        {
                            // A handshake that failed, a client that went away: nothing to answer.
                        }
                    }
                });

            }
        }

        #endregion

        #region (private) Serve(Client)

        private async Task Serve(TcpClient Client)
        {

            Stream stream = Client.GetStream();

            if (certificate is not null)
            {

                var tls = new SslStream(stream, leaveInnerStreamOpen: false);

                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                                                        ServerCertificate          = certificate,
                                                        ClientCertificateRequired  = false,
                                                        EnabledSslProtocols        = SslProtocols.Tls12 | SslProtocols.Tls13
                                                    },
                                                    stop.Token);

                stream = tls;

            }

            await using (stream)
            {

                var reader      = new BufferedReader(stream);

                var requestLine = await reader.ReadLine();

                if (requestLine is null)
                    return;

                var parts       = requestLine.Split(' ');
                var method      = parts.Length > 0 ? parts[0] : "";
                var path        = parts.Length > 1 ? parts[1] : "/";

                var headers     = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase);

                while (await reader.ReadLine() is String line && line.Length > 0)
                {
                    var colon = line.IndexOf(':');
                    if (colon > 0)
                        headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }

                // "Expect: 100-continue": go ahead.
                if (headers.TryGetValue("Expect", out var expect) && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
                    await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), stop.Token);

                var body        = await ReadBody(reader, headers);

                var status      = Answer(method, path, headers, body);

                Requests.Enqueue(new Received(method, path, headers, body, status));

                var response    = $"HTTP/1.1 {status} {Reason(status)}\r\n" +
                                  (status == 401 ? "WWW-Authenticate: Basic realm=\"logs\"\r\n" : "") +
                                  "Content-Length: 0\r\n" +
                                  "Connection: close\r\n\r\n";

                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), stop.Token);
                await stream.FlushAsync(stop.Token);

            }

        }

        #endregion

        #region (private) Answer(Method, Path, Headers, Body)

        private Int32 Answer(String                        Method,
                             String                        Path,
                             Dictionary<String, String>    Headers,
                             Byte[]                        Body)
        {

            if (answers.TryDequeue(out var told))
                return told;

            if (Method != "PUT")
                return 405;

            if (User is not null)
            {

                var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Password}"));

                if (!Headers.TryGetValue("Authorization", out var authorization) || authorization != expected)
                    return 401;

            }

            if (Headers.TryGetValue("If-None-Match", out var ifNoneMatch) && ifNoneMatch.Trim() == "*" && Files.ContainsKey(Path))
                return 412;

            return Files.TryAdd(Path, Body)
                       ? 201
                       : 412;

        }

        private static String Reason(Int32 Status)

            => Status switch {
                   201 => "Created",
                   401 => "Unauthorized",
                   403 => "Forbidden",
                   404 => "Not Found",
                   405 => "Method Not Allowed",
                   412 => "Precondition Failed",
                   500 => "Internal Server Error",
                   503 => "Service Unavailable",
                   _   => "Status"
               };

        #endregion

        #region (private) ReadBody(Reader, Headers)

        private static async Task<Byte[]> ReadBody(BufferedReader               Reader,
                                                   Dictionary<String, String>   Headers)
        {

            if (Headers.TryGetValue("Transfer-Encoding", out var encoding) && encoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {

                var body = new MemoryStream();

                while (true)
                {

                    var sizeLine = await Reader.ReadLine() ?? "0";
                    var size     = Int32.Parse(sizeLine.Split(';')[0].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

                    if (size == 0)
                    {
                        // The trailers, up to the empty line.
                        while (await Reader.ReadLine() is String trailer && trailer.Length > 0) { }
                        break;
                    }

                    body.Write(await Reader.ReadExactly(size));
                    await Reader.ReadLine();

                }

                return body.ToArray();

            }

            return Headers.TryGetValue("Content-Length", out var length) && Int32.TryParse(length, out var count) && count > 0
                       ? await Reader.ReadExactly(count)
                       : [];

        }

        #endregion

        #region (private) BufferedReader

        /// <summary>
        /// Lines and bytes of a stream, as HTTP/1.1 mixes them.
        /// </summary>
        private sealed class BufferedReader(Stream Stream)
        {

            private readonly Byte[]  buffer  = new Byte[8192];
            private          Int32   start;
            private          Int32   end;

            private async Task<Boolean> Fill()
            {

                if (start > 0)
                {
                    Array.Copy(buffer, start, buffer, 0, end - start);
                    end   -= start;
                    start  = 0;
                }

                var read = await Stream.ReadAsync(buffer.AsMemory(end));

                end += read;

                return read > 0;

            }

            public async Task<String?> ReadLine()
            {

                var line = new MemoryStream();

                while (true)
                {

                    for (var i = start; i < end; i++)
                    {
                        if (buffer[i] == '\n')
                        {

                            line.Write(buffer, start, i - start);
                            start = i + 1;

                            var text = Encoding.ASCII.GetString(line.ToArray());
                            return text.EndsWith('\r') ? text[..^1] : text;

                        }
                    }

                    line.Write(buffer, start, end - start);
                    start = end;

                    if (!await Fill())
                        return line.Length > 0 ? Encoding.ASCII.GetString(line.ToArray()) : null;

                }

            }

            public async Task<Byte[]> ReadExactly(Int32 Count)
            {

                var result = new Byte[Count];
                var have   = 0;

                while (have < Count)
                {

                    if (start == end && !await Fill())
                        throw new EndOfStreamException();

                    var take = Math.Min(Count - have, end - start);
                    Array.Copy(buffer, start, result, have, take);

                    start += take;
                    have  += take;

                }

                return result;

            }

        }

        #endregion


        #region DisposeAsync()

        public async ValueTask DisposeAsync()
        {

            stop.Cancel();
            listener.Stop();

            try { await accepting; } catch (Exception) { }

            stop.Dispose();
            certificate?.Dispose();

        }

        #endregion

    }

}
