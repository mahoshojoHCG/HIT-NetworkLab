using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Lab1
{
    public class HttpProxyRequest
    {
        private readonly IConfiguration _configuration;
        private readonly UserInfo _currentUser;
        private readonly ILogger _logger;

        public HttpProxyRequest(ILogger logger, IConfiguration configuration, UserInfo currentUser)
        {
            _logger = logger;
            _configuration = configuration;
            _currentUser = currentUser;
        }

        public Dictionary<string, string> Header { get; } = new Dictionary<string, string>();
        public Uri RequestUri { get; private set; }
        public HttpMethod Method { get; private set; }
        public string HttpVersion { get; private set; }
        public byte[] Body { get; private set; }
        private HttpResponseMessage Response { get; set; }
        private static HttpClient Client { get; } = new HttpClient();
        private CacheContext Context { get; } = new CacheContext();

        private byte[] Generate302(string forward)
        {
            //Quick CRLF
            Span<byte> crlf = stackalloc byte[2];
            crlf[0] = (byte) '\r';
            crlf[1] = (byte) '\n';
            using var ms = new MemoryStream();
            ms.Write(Encoding.ASCII.GetBytes("HTTP/1.1 302 Moved Temporarily"));
            ms.Write(crlf);
            ms.Write(Encoding.ASCII.GetBytes("Content-length: 0"));
            ms.Write(crlf);
            ms.Write(Encoding.UTF8.GetBytes($"Location: {forward}"));
            ms.Write(crlf);
            ms.Write(crlf);
            return ms.ToArray();
        }

        public byte[] Perform(IPAddress address)
        {
            //Redirect if not login
            if (_currentUser == null && RequestUri.Host != _configuration["ApiBase"])
            {
                _logger.LogInformation($"Connection to {RequestUri.Host} blocked because not login.");
                return Generate302(_configuration["LoginPage"]);
            }

            //Redirect if blocked
            if (_currentUser.BlockList.Any(blocked => new Regex(blocked)
                                                          .IsMatch(RequestUri.ToString()) &&
                                                      !_currentUser.AllowList.Any(a =>
                                                          new Regex(a).IsMatch(RequestUri.ToString()))))
            {
                _logger.LogInformation($"Connection to {RequestUri} blocked because it was on blocklist.");
                return Generate302(_configuration["BlockedPage"]);
            }

            //Quick CRLF
            Span<byte> crlf = stackalloc byte[2];
            crlf[0] = (byte) '\r';
            crlf[1] = (byte) '\n';
            using var cacheHash = SHA256.Create();
            var key = BitConverter.ToString(cacheHash.ComputeHash(Encoding.UTF8.GetBytes(RequestUri.ToString())))
                .Replace("-", string.Empty);
            var ms = new MemoryStream();
            var message = new HttpRequestMessage(Method, RequestUri)
            {
                Content = new ByteArrayContent(Body)
            };
            message.Content.Headers.Clear();
            message.Headers.Clear();
            foreach (var (k, v) in Header)
                if (k.StartsWith("Content"))
                    message.Content.Headers.Add(k, v);
                else
                    message.Headers.Add(k, v);
            //Add X-Forwarded-For
            message.Headers.Add("X-Forwarded-For", address.ToString());
            //Check if no cache allowed
            if (message.Headers.CacheControl?.NoStore != true)
            {
                _logger.LogInformation("Cache allowed, now verifying fresh.");
                var cached =
                    from cache
                        in Context.Caches
                    where cache.CacheId == key && cache.ExpireTime > DateTime.Now
                    select cache;

                if (cached.Any())
                {
                    //Cache presented
                    var cache = cached.First();
                    //Verify first
                    var verifyMessage = new HttpRequestMessage(Method, RequestUri);
                    verifyMessage.Headers.IfModifiedSince = cache.CachedTime;
                    verifyMessage.Headers.UserAgent.Add(new ProductInfoHeaderValue(
                        "HCGProxy",
                        Assembly.GetExecutingAssembly().GetName().Version.ToString()));
                    var verify = Client.Send(verifyMessage);
                    //304 and 200 is both OK in standard
                    if (verify.StatusCode == HttpStatusCode.NotModified ||
                        verify.StatusCode == HttpStatusCode.OK)
                    {
                        _logger.LogInformation("Cache verified successful, using cache.");
                        //Use cache
                        ms.Write(cache.Content);
                        return ms.ToArray();
                    }

                    _logger.LogWarning("Database content is not refresh, removing.");
                    Context.Caches.Remove(cache);
                    Context.SaveChanges();
                }
                else
                {
                    _logger.LogInformation("No fresh cache present.");
                }
            }

            //No cache used
            Response = Client.Send(message);

            var responseHeader = Encoding.ASCII.GetBytes(Response.Headers.ToString());
            var contentHeader = Encoding.ASCII.GetBytes(Response.Content.Headers.ToString());
            //Write status
            var status =
                Encoding.ASCII.GetBytes(
                    $"HTTP/{Response.Version.ToString(2)} {(int) Response.StatusCode} {Response.ReasonPhrase}");
            ms.Write(status);
            ms.Write(crlf);
            //Write header
            ms.Write(responseHeader);
            ms.Write(contentHeader);
            ms.Write(crlf);
            //Write body
            Response.Content.CopyTo(ms, null, default);

            //Check if cache allowed and ContentLength
            if (Response.Headers.CacheControl?.NoStore != true &&
                Response.Content.Headers.ContentLength < 40960)
                //Only cache 200, 404 and 301
                if (Response.StatusCode == HttpStatusCode.OK ||
                    Response.StatusCode == HttpStatusCode.NotFound ||
                    Response.StatusCode == HttpStatusCode.MovedPermanently)
                    //Create a new thread to write cache
                    Task.Run(async () =>
                    {
                        var cached =
                            from c
                                in Context.Caches
                            where c.CacheId == key
                            select c;
                        //Check and remove old expired
                        if (cached.Any())
                            Context.Caches.Remove(cached.First());
                        var cache = new Cache
                        {
                            CacheId = key,
                            CachedTime = DateTime.Now,
                            Content = ms.ToArray()
                        };
                        cache.ExpireTime = Response.Headers.CacheControl?.MaxAge != null
                            ? cache.CachedTime.Add((TimeSpan) Response.Headers.CacheControl.MaxAge)
                            : cache.CachedTime.AddDays(7);

                        await Context.AddAsync(cache);
                        await Context.SaveChangesAsync();
                    });
            return ms.ToArray();
        }

        public void ParseProxyRequest(byte[] raw)
        {
            using var ms = new MemoryStream(raw);
            var location = 0;
            var sr = new StreamReader(ms);
            var firstLine = sr.ReadLine();
            _logger.LogInformation(firstLine);
            var firstLineSplit = firstLine.Split(' ');
            location += Encoding.ASCII.GetByteCount(firstLine) + 2;
            if (firstLineSplit.Length != 3)
                throw new ArgumentOutOfRangeException(nameof(raw));
            Method = new HttpMethod(firstLineSplit[0]);
            RequestUri = new Uri(firstLineSplit[1]);
            HttpVersion = firstLineSplit[2];
            Header.Clear();
            while (true)
            {
                var line = sr.ReadLine();
                if (string.IsNullOrEmpty(line))
                    break;
                location += Encoding.ASCII.GetByteCount(line) + 2;
                var parts = line.Split(": ");
                if (parts.Length != 2)
                    throw new ArgumentOutOfRangeException(nameof(raw));
                Header.Add(parts[0], parts[1]);
            }

            location += 2;
            using var newStream = new MemoryStream(raw, location, raw.Length - location, false);
            Body = newStream.ToArray();
        }
    }
}