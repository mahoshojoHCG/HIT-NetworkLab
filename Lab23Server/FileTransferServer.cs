using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Lab23;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Lab23Server
{
    public class FileTransferServer
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger _logger;
        private readonly IUdpProtoServer _server;

        public FileTransferServer(ILogger<FileTransferServer> logger, IConfiguration configuration,
            IUdpProtoServer server)
        {
            _logger = logger;
            _configuration = configuration;
            _server = server;
            if (!IPEndPoint.TryParse(configuration["EndPoint"], out var endPoint))
            {
                _logger.LogInformation("No EndPoint in config, using default.");
                endPoint = new IPEndPoint(IPAddress.Loopback, 2000);
            }

            BaseDirectory = Path.GetFullPath(configuration["BaseDirectory"]);
            EndPoint = endPoint;
        }

        public IPEndPoint EndPoint { get; }
        private Queue<Task> Tasks { get; } = new Queue<Task>();
        public string BaseDirectory { get; }

        public async ValueTask RunAsync()
        {
            await _server.BindAsync(EndPoint);
            _logger.LogInformation("FTS server starts working.");
            while (true)
            {
                var client = await _server.ListenAsync();
                Tasks.Enqueue(Task.Run(async () =>
                {
                    while (true)
                    {
                        var header = JsonSerializer.Deserialize<FileHeader>
                            (Encoding.UTF8.GetString(await client.ReceiveAsync()));
                        _logger.LogInformation($"Incoming file request {header.Method} {header.FileName}.");
                        //GetFile
                        if (header.Method == "GET")
                        {
                            //Get file
                            if (!File.Exists(Path.Combine(BaseDirectory, header.FileName)))
                            {
                                _logger.LogInformation($"File {header.FileName} not found.");
                                await client.SendAsync(Encoding.UTF8.GetBytes(
                                    JsonSerializer.Serialize(header with
                                        {
                                        Method = "RESULT",
                                        ContentLength = 0,
                                        ResultCode = 404
                                        })));
                                continue;
                            }

                            //Send file by part
                            var file = new FileStream(Path.Combine(BaseDirectory, header.FileName), FileMode.Open);
                            await client.SendAsync(Encoding.UTF8.GetBytes(
                                JsonSerializer.Serialize(header with
                                    {
                                    Method = "RESULT",
                                    ContentLength = file.Length,
                                    ResultCode = 200
                                    })));
                            _logger.LogInformation($"File {header.FileName} begin transfer.");
                            var buffer = new byte[2048];
                            var read = 0L;
                            while (read < file.Length)
                            {
                                var result = await file.ReadAsync(buffer.AsMemory(0, buffer.Length));
                                if (result == 20480)
                                    await client.SendAsync(buffer);
                                else if (result > 0)
                                    await client.SendAsync(buffer[..result]);
                                read += result;
                            }

                            _logger.LogInformation($"File {header.FileName} transfer completed.");
                        }
                        //Upload file
                        else if (header.Method == "PUT")
                        {
                            var read = 0L;
                            await using var ms = new MemoryStream();
                            while (read < header.ContentLength)
                            {
                                var received = await client.ReceiveAsync();
                                await ms.WriteAsync(received);
                                read += received.LongLength;
                            }

                            //Seek to receive
                            ms.Seek(0, SeekOrigin.Begin);
                            await using var file = new FileStream(Path.Combine(BaseDirectory, header.FileName),
                                FileMode.Create);
                            await ms.CopyToAsync(file);
                            await file.FlushAsync();
                        }
                    }
                }));
            }
        }
    }
}