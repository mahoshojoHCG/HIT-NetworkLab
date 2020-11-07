using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Lab23
{
    public class StopAndWaitClient : IUdpProtoClient
    {
        private UdpClient UdpClient { get; } = new UdpClient();
        private ConcurrentQueue<Task> Tasks { get; } = new ConcurrentQueue<Task>();
        private ConcurrentQueue<byte[]> PendingBuffers { get; } = new ConcurrentQueue<byte[]>();
        private ConcurrentBag<bool> OKs { get; } = new ConcurrentBag<bool>();
        public bool Connected { get; private set; }

        private readonly ILogger _logger;
        public StopAndWaitClient(ILogger<StopAndWaitClient> logger)
        {
            _logger = logger;
        }
        public async ValueTask ConnectAsync(IPEndPoint endPoint)
        {
            if (!Connected)
            {
                UdpClient.Connect(endPoint);
                Connected = true;
            }

            Tasks.Enqueue(Task.Run(async () =>
            {
                while (true)
                {
                    var buffer = (await UdpClient.ReceiveAsync()).Buffer;
                    if (buffer[0] == 0)
                        //Standard packet
                        PendingBuffers.Enqueue(buffer[4..]);
                    if (buffer[0] == 2)
                        //Client confirm packet
                        OKs.Add(true);
                }
            }));
            await Task.FromResult(0);
        }

        public async Task<byte[]> ReceiveAsync()
        {
            //Return confirm packet
            var buffer = await Task.Run(async () =>
            {
                while (true)
                {
                    if (PendingBuffers.TryDequeue(out var result)) return result;
                    await Task.Delay(1);
                }
            });
            await UdpClient.SendAsync(new byte[] {1, 0, 0, 0}, 4);
            return buffer;
        }

        public async ValueTask SendAsync(byte[] buffer)
        {
            await using var ms = new MemoryStream();
            await ms.WriteAsync(new byte[] {0, 0, 0, 0});
            await ms.WriteAsync(buffer);
            var data = ms.ToArray();
            await UdpClient.SendAsync(data, data.Length);
            var receive = Task.Run(async () =>
            {
                while (true)
                {
                    if (OKs.TryTake(out var result)) return result;
                    await Task.Delay(1);
                }
            }).GetAwaiter();
            for (var i = 0; i < 1000; i++)
                if (!receive.IsCompleted)
                    await Task.Delay(1);
            while (!receive.IsCompleted)
            {
                //Re transfer
                await UdpClient.SendAsync(data, data.Length);
                _logger.LogError("Packet lost, resending.");
                await Task.Delay(500);
            }
        }

        public bool TransferCompleted => true;
    }
}