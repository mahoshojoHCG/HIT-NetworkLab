using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Lab23
{
    public class StopAndWaitServer : IUdpProtoServer
    {
        private class StopAndWaitProtoClientClientServerInstance : IUdpProtoClient
        {
            public bool Connected => true;
            private StopAndWaitServer Source { get; }
            private IPEndPoint EndPoint { get; }
            public async ValueTask ConnectAsync(IPEndPoint endPoint)
            {
                await Task.FromResult(0);
            }

            public StopAndWaitProtoClientClientServerInstance(StopAndWaitServer source, IPEndPoint endPoint)
            {
                Source = source;
                EndPoint = endPoint;
            }
            public async Task<byte[]> ReceiveAsync()
            {
                var received = Source.PendingReceive[EndPoint];

                while (true)
                {
                    if (received.TryDequeue(out var result))
                    {
                        return result;
                    }

                    //Wait until content received
                    await Task.Delay(10);
                }
            }

            public async ValueTask SendAsync(byte[] buffer)
            {
                //Other thread is sending and not completed
                while (Source.HavePendingSend[EndPoint])
                {
                    await Task.Delay(10);
                }

                Source.HavePendingSend[EndPoint] = true;
                Source.PendingSend[EndPoint].Enqueue(buffer);

                while (Source.HavePendingSend[EndPoint])
                {
                    await Task.Delay(10);
                }
            }
        }

        public bool Connected => UdpClient != null;
        private UdpClient UdpClient { get; set; }
        private Queue<Task> Tasks { get; } = new Queue<Task>();

        internal ConcurrentDictionary<IPEndPoint, ConcurrentQueue<byte[]>> PendingReceive { get; } =
            new ConcurrentDictionary<IPEndPoint, ConcurrentQueue<byte[]>>();

        internal ConcurrentDictionary<IPEndPoint, ConcurrentQueue<byte[]>> PendingSend { get; } =
            new ConcurrentDictionary<IPEndPoint, ConcurrentQueue<byte[]>>();

        //If Endpoint have pending content true have false don't
        internal ConcurrentDictionary<IPEndPoint, bool> HavePendingSend { get; } =
            new ConcurrentDictionary<IPEndPoint, bool>();

        private ConcurrentQueue<IPEndPoint> PendingConnection { get; } = new ConcurrentQueue<IPEndPoint>();
        private ConcurrentQueue<IPEndPoint> ConfirmedReceived { get; } = new ConcurrentQueue<IPEndPoint>();
        private readonly ILogger _logger;
        public StopAndWaitServer(ILogger<StopAndWaitServer> logger)
        {
            _logger = logger;
        }
        public async ValueTask BindAsync(IPEndPoint endPoint)
        {
            if (!Connected)
            {
                UdpClient = new UdpClient(endPoint);
                _logger.LogInformation($"Starting listening on {endPoint}.");
                //Create a new thread to receive and process data.
                Tasks.Enqueue(Task.Run(async () =>
                {
                    while (true)
                    {
                        var data = await UdpClient.ReceiveAsync();

                        if (!PendingReceive.ContainsKey(data.RemoteEndPoint))
                        {
                            PendingReceive.AddOrUpdate(data.RemoteEndPoint, new ConcurrentQueue<byte[]>(),
                                (_, queue) => queue);
                            PendingConnection.Enqueue(data.RemoteEndPoint);
                            HavePendingSend.AddOrUpdate(data.RemoteEndPoint, false, (_, b) => b);
                            PendingSend.AddOrUpdate(data.RemoteEndPoint, new ConcurrentQueue<byte[]>(),
                                (_, queue) => queue);
                        }

                        // 0 for normal content
                        if (data.Buffer[0] == 0)
                        {
                            _logger.LogInformation($"Received a standard packet from {data.RemoteEndPoint}.");
                            var pendingReceive = PendingReceive[data.RemoteEndPoint];
                            //Add data to pending receive
                            pendingReceive.Enqueue(data.Buffer[4..]);

                            //Reply received
                            await UdpClient.SendAsync(new byte[] { 2, 0, 0, 0 }, 4, data.RemoteEndPoint);
                        }

                        // 1 for receive feedback
                        if (data.Buffer[0] == 1)
                        {
                            _logger.LogInformation($"Received a confirm packet from {data.RemoteEndPoint}.");

                            //Enqueue confirmed info
                            ConfirmedReceived.Enqueue(data.RemoteEndPoint);
                        }
                    }
                }));

                //Create a new thread to check confirm packet
                Tasks.Enqueue(Task.Run(() =>
                {

                    while (true)
                    {
                        if (ConfirmedReceived.TryDequeue(out var enp))
                        {
                            //Mark the send successful
                            HavePendingSend[enp] = false;
                            Thread.Sleep(10);
                        }
                    }
                }));

                Tasks.Enqueue(Task.Run(async () =>
                {
                    while (true)
                    {
                        foreach (var (enp, pendingSend) in PendingSend)
                        {
                            if (pendingSend.TryDequeue(out var buffer))
                            {
                                //Start a new thread to send.
                                Tasks.Enqueue(Task.Run(async () =>
                                {
                                    await using var ms = new MemoryStream();
                                    await ms.WriteAsync(new byte[] { 0, 0, 0, 0 });
                                    await ms.WriteAsync(buffer);
                                    var data = ms.ToArray();
                                    await UdpClient.SendAsync(data, data.Length, enp);
                                    while (true)
                                    {
                                        //Wait 500 ms to retry.
                                        for (var i = 0; i < 1000; i++)
                                        {
                                            await Task.Delay(1);
                                            if (!HavePendingSend[enp])
                                                return;
                                        }
                                        
                                        _logger.LogInformation($"Packet to {enp} lost, retrying.");

                                        //Retry
                                        await UdpClient.SendAsync(data, data.Length, enp);
                                    }

                                }));
                            }

                            await Task.Delay(10);
                        }

                    }
                }));

                await Task.FromResult(0);
            }
        }

        public async Task<IUdpProtoClient> ListenAsync()
        {
            while (true)
            {
                if (PendingConnection.TryDequeue(out var enp))
                    return new StopAndWaitProtoClientClientServerInstance(this, enp);

                //Wait until new connection received
                await Task.Delay(10);
            }
        }
    }
}
