using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Lab23;
using Microsoft.Extensions.Logging;

namespace Lab23Client
{
    public class FileTransferClient
    {
        private readonly IUdpProtoClient _client;
        private readonly ILogger _logger;
        public FileTransferClient(IUdpProtoClient client, ILogger<FileTransferClient> logger)
        {
            _client = client;
            _logger = logger;
        }

        public async ValueTask ConnectAsync(IPEndPoint endPoint)
        {
            if (!_client.Connected)
                await _client.ConnectAsync(endPoint);
        }

        public async ValueTask SendFileAsync(FileInfo file)
        {
            await using var stream = file.OpenRead();
            await _client.SendAsync(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new FileHeader
                {
                    Method = "PUT",
                    FileName = file.Name,
                    ContentLength = file.Length
                })));
            _logger.LogInformation("File confirmed, transfer start.");
            var sent = 0L;
            var buffer = new byte[20480];
            while (sent < file.Length)
            {
                var read = await stream.ReadAsync(buffer);
                if (read < buffer.Length)
                    await _client.SendAsync(buffer[..read]);
                else
                    await _client.SendAsync(buffer);
                _logger.LogInformation($"{read} bytes sent, total {file.Length}.");
                sent += read;
            }
        }

        public async ValueTask GetFileAsync(string requireFileName)
        {
            await _client.SendAsync(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new FileHeader
                {
                    Method = "GET",
                    FileName = requireFileName
                })));
            var result = JsonSerializer.Deserialize<FileHeader>(await _client.ReceiveAsync());
            if (result.ResultCode != 200)
            {
                _logger.LogError($"Server return error value {result.ResultCode}");
                return;
            }
            _logger.LogInformation("File confirmed, transfer start.");
            var byteReceived = 0L;
            await using var ms = new MemoryStream();
            while (byteReceived < result.ContentLength)
            {
                var buffer = await _client.ReceiveAsync();
                await ms.WriteAsync(buffer);
                byteReceived += buffer.Length;
                _logger.LogInformation($"{byteReceived} bytes received, total {result.ContentLength}.");
            }
            //await File.WriteAllBytesAsync(result.FileName, ms.ToArray());

            await using var file = new FileStream(requireFileName, FileMode.Create);
            //Seek to begin of the stream to read buffer and write to disk.
            ms.Seek(0, SeekOrigin.Begin);
            await ms.CopyToAsync(file);
            await file.FlushAsync();
        }
    }
}
