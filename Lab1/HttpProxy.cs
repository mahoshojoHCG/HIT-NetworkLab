using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Lab1
{
    public class HttpProxy
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<HttpProxy> _logger;

        public HttpProxy(ILogger<HttpProxy> logger, IConfiguration configuration)
        {
            _configuration = configuration;
            _logger = logger;
            if (!int.TryParse(configuration["Port"], out var port))
            {
                _logger.LogWarning("No port present, using default port 8080.");
                port = 8080;
            }

            BindPort = port;
            foreach (var ip in configuration.GetSection("BindAddress").Get<string[]>())
                if (IPAddress.TryParse(ip, out var ipAddress))
                    BindIpAddresses.Add(ipAddress);
                else
                    _logger.LogError($"{ip} is not a valid IP Address.");

            //Add loopback IP if no ip were set.
            if (BindIpAddresses.Count == 0)
            {
                _logger.LogWarning("No valid ip present, now using loopback IPs.");
                //::1
                BindIpAddresses.Add(IPAddress.IPv6Loopback);
                //127.0.0.1
                BindIpAddresses.Add(IPAddress.Loopback);
            }


            //User Policy
            foreach (var userInfo in configuration.GetSection("Users").Get<UserInfo[]>())
                Users.Add(userInfo.UserName, userInfo);
        }

        private HttpClient Client { get; set; }
        public List<IPAddress> BindIpAddresses { get; init; } = new List<IPAddress>();
        public int BindPort { get; init; }
        private Queue<Task> Connections { get; } = new Queue<Task>();
        public Dictionary<string, UserInfo> Users { get; } = new Dictionary<string, UserInfo>();
        public Dictionary<IPAddress, string> Auth { get; } = new Dictionary<IPAddress, string>();

        public async ValueTask RunProxyAsync(CancellationToken token = default)
        {
            //Upload users and passwords
            Client = new HttpClient {BaseAddress = new Uri(_configuration["ApiBase"])};
            foreach (var (_, user) in Users)
                await Client.GetAsync(
                    $"/api/Login/PushPassword?userName={user.UserName}&token={user.Token}",
                    token);
            //Check database
            await using var context = new CacheContext();
            await context.Database.EnsureCreatedAsync(token);

            _logger.LogInformation("Proxy started.");
            foreach (var bindIpAddress in BindIpAddresses)
            {
                var ipe = new IPEndPoint(bindIpAddress, BindPort);
                var socket = new Socket(bindIpAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                socket.Bind(ipe);
                socket.Listen();
                _logger.LogInformation($"Start listening {ipe}");
                Connections.Enqueue(Task.Run(() => StartListen(socket), token));
            }

            await Task.Delay(int.MaxValue, token);
        }

        private void StartListen(Socket socket)
        {
            try
            {
                Span<byte> buffer = stackalloc byte[4096];

                var accept = socket.Accept();

                //After accept, open a new thread to accept new
                Connections.Enqueue(Task.Run(() => StartListen(socket)));

                _logger.LogInformation($"Incoming connection {accept.RemoteEndPoint}.");

                var remoteIp = ((IPEndPoint) accept.RemoteEndPoint).Address;
                UserInfo user = null;
                if (Auth.TryGetValue(remoteIp, out var name))
                {
                    Users.TryGetValue(name, out user);
                }
                else
                {
                    //Check login status
                    var response =
                        Client.Send(new HttpRequestMessage(HttpMethod.Get, $"/api/Login/CheckAuth?ip={remoteIp}"));
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        var awaiter = response.Content.ReadAsStringAsync().GetAwaiter();
                        while (!awaiter.IsCompleted) Thread.Sleep(2);

                        var userName = awaiter.GetResult();
                        Auth[remoteIp] = userName;
                        user = Users[userName];
                        user.Login = true;
                    }
                }

                var request = new HttpProxyRequest(_logger, _configuration, user);

                while (accept.Connected)
                {
                    using var bytes = new MemoryStream();
                    //First receive
                    var retryTime = 0;
                    while (bytes.Length == 0)
                    {
                        while (true)
                        {
                            var received = accept.Receive(buffer, SocketFlags.None);
                            //All data received
                            if (received < buffer.Length)
                            {
                                bytes.Write(buffer.ToArray(), 0, received);
                                break;
                            }

                            bytes.Write(buffer);
                        }

                        if (bytes.Length == 0)
                        {
                            retryTime++;
                            _logger.LogInformation(
                                $"Received empty data from {accept.RemoteEndPoint}, assuming not read. Try again.");
                            Thread.Sleep(20);
                            if (retryTime > 20)
                            {
                                _logger.LogInformation(
                                    $"No data from {accept.RemoteEndPoint}, retrying too many times, close connection.");
                                accept.Close();
                                return;
                            }
                        }
                    }

                    _logger.LogInformation($"Request from {accept.RemoteEndPoint} received. Preforming request.");
                    request.ParseProxyRequest(bytes.ToArray());
                    var response = request.Perform(remoteIp);
                    var str = Encoding.UTF8.GetString(response);
                    accept.Send(response);
                    _logger.LogInformation($"Request from {accept.RemoteEndPoint} performed and sent.");
                    accept.Close();
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e.Message);
            }
        }
    }
}