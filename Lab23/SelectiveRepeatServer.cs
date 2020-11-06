using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Lab23
{
    public class SelectiveRepeatServer : IUdpProtoServer
    {
        private class SelectiveRepeatProtoClientClientServerInstance : IUdpProtoClient
        {
            internal ConcurrentQueue<byte[]> PendingSend { get; } = new ConcurrentQueue<byte[]>();
            internal ConcurrentQueue<byte[]> PendingReceive { get; } = new ConcurrentQueue<byte[]>();

            /// <summary>
            /// Marks to stop accept more window.
            /// </summary>
            internal bool StopAccept { get; set; }

            /// <summary>
            /// If the buffer is full and window can't be larger.
            /// </summary>
            private bool BufferFull => PendingSend.Count >= 254 || StopAccept;

            /// <summary>
            /// Marks if this instance is processed by the handler, not process again until this is finished.
            /// </summary>
            internal bool OnTheWay { get; set; }
            //Default namespace is 0
            internal int CurrentSendNamespace { get; set; }
            internal int CurrentReceiveNamespace { get; set; }
            internal byte CurrentReceiveWindowSize { get; set; }
            internal byte CurrentSendWindowSize { get; set; }
            internal ConcurrentDictionary<byte, byte[]> WaitingForConfirmReceive { get; } =
                new ConcurrentDictionary<byte, byte[]>();
            internal ConcurrentDictionary<byte, bool> PendingAck { get; } =
                new ConcurrentDictionary<byte, bool>();
            public bool Connected => true;

            private readonly ILogger _logger;
            public SelectiveRepeatProtoClientClientServerInstance(ILogger logger)
            {
                _logger = logger;
            }
            public async ValueTask ConnectAsync(IPEndPoint endPoint)
            {
                await Task.FromResult(0);
            }

            public async Task<byte[]> ReceiveAsync()
            {
                byte[] result;
                while (!PendingReceive.TryDequeue(out result))
                {
                    //Wait 1 ms to check receive result
                    await Task.Delay(10);
                }

                return result;
            }

            public async ValueTask SendAsync(byte[] buffer)
            {
                //Wait until the queue can accept more
                while (BufferFull)
                {
                    _logger.LogInformation("The buffer is full, waiting...");
                    await Task.Delay(10);
                }
                _logger.LogInformation("Adding to queue.");
                PendingSend.Enqueue(buffer);
            }

            public bool TransferCompleted => PendingSend.IsEmpty;
        }
        private UdpClient UdpClient { get; set; }
        public bool Connected => UdpClient != null;
        private ConcurrentQueue<Task> Tasks { get; } = new ConcurrentQueue<Task>();
        private ConcurrentDictionary<IPEndPoint, SelectiveRepeatProtoClientClientServerInstance> Route { get; } =
            new ConcurrentDictionary<IPEndPoint, SelectiveRepeatProtoClientClientServerInstance>();

        private ConcurrentQueue<SelectiveRepeatProtoClientClientServerInstance> PendingInstance { get; } =
            new ConcurrentQueue<SelectiveRepeatProtoClientClientServerInstance>();

        private ILogger _logger;
        public SelectiveRepeatServer(ILogger<SelectiveRepeatServer> logger)
        {
            _logger = logger;
        }

        public async ValueTask BindAsync(IPEndPoint endPoint)
        {
            await Task.FromResult(0);
            if (!Connected)
                UdpClient = new UdpClient(endPoint);
            _logger.LogInformation($"Start listening on {endPoint}");
            //Open a thread to listen incoming packets.
            Tasks.Enqueue(Task.Run(async () =>
            {
                while (true)
                {
                    var receive = await UdpClient.ReceiveAsync();
                    _logger.LogInformation($"Incoming from {receive.RemoteEndPoint}");
                    //Add remote to route
                    if (!Route.ContainsKey(receive.RemoteEndPoint))
                    {
                        _logger.LogInformation($"Adding {receive.RemoteEndPoint} to route");

                        Route.AddOrUpdate(receive.RemoteEndPoint, _ =>
                        {
                            var instance = new SelectiveRepeatProtoClientClientServerInstance(_logger);
                            PendingInstance.Enqueue(instance);
                            return instance;
                        }, (_, instance) => instance);

                        var r = Route[receive.RemoteEndPoint];

                        //Open a new thread to process pending sends.
                        Tasks.Enqueue(Task.Run(async () =>
                        {
                            while (true)
                            {
                                //Wait until new window comes out
                                while (r.CurrentReceiveWindowSize == 0)
                                {
                                    await Task.Delay(10);
                                }

                                //Process the received data
                                for (byte i = 1; i <= r.CurrentReceiveWindowSize; i++)
                                {
                                    byte[] value;
                                    while (!r.WaitingForConfirmReceive.TryGetValue(i,out value))
                                    {
                                        await Task.Delay(10);
                                    }
                                    r.PendingReceive.Enqueue(value[4..]);
                                }
                                //Switch window
                                _logger.LogInformation("Receive window switched.");
                                r.CurrentReceiveNamespace = r.CurrentReceiveNamespace == 0 ? 1 : 0;
                                r.WaitingForConfirmReceive.Clear();
                                r.CurrentReceiveWindowSize = 0;
                            }
                        }));
                    }

                    var buffer = receive.Buffer;
                    //Bad packet, throw
                    if (buffer.Length < 4)
                        continue;

                    var route = Route[receive.RemoteEndPoint];

                    /*
                     * Packet
                     * | Byte 0 | Byte 1 | Byte 2 |   Byte 3    |   ...   |
                     * |  Type  |  Id0   |  Id1   | Window size | Content |
                     * Id0 and Id1 is used for different namespace, and max window size is 255.
                     */

                    //Standard packet
                    if (buffer[0] == 0)
                    {
                        //This is a resend of last namespace
                        if (buffer[route.CurrentReceiveNamespace + 1] == 0)
                        {
                            var ack = new byte[] { 1, 0, 0, buffer[3] };
                            //Resend ack is from the other namespace
                            if (route.CurrentReceiveNamespace == 0)
                                ack[2] = buffer[2];
                            else
                                ack[1] = ack[1];
                            _logger.LogInformation(
                                $"Received a resent packet of last namespace from {receive.RemoteEndPoint}");
                            //Directly resend ack
                            await UdpClient.SendAsync(ack, 4, receive.RemoteEndPoint);
                        }
                        //Not resend, just normal packet
                        else
                        {
                            var groupNumber = buffer[route.CurrentReceiveNamespace + 1];
                            //Set window size
                            if (route.CurrentReceiveWindowSize == 0)
                                route.CurrentReceiveWindowSize = buffer[3];
                            //Not check, if a resend get, cover the last one
                            route.WaitingForConfirmReceive[groupNumber] = buffer;
                            _logger.LogInformation(
                                $"Received a packet #{groupNumber} of last namespace from " +
                                $"{receive.RemoteEndPoint}, widow size is {route.CurrentReceiveWindowSize}");
                            //Send ack
                            var ack = new byte[] { 1, 0, 0, buffer[3] };
                            ack[route.CurrentReceiveNamespace + 1] = groupNumber;
                            await UdpClient.SendAsync(ack, 4, receive.RemoteEndPoint);
                        }

                    }

                    //Ack
                    if (buffer[0] == 1)
                    {
                        //Not sure ack in which namespace
                        if (buffer[1] != 0)
                        {
                            route.PendingAck[buffer[1]] = true;
                            _logger.LogInformation($"Ack #{buffer[1]} received.");
                        }

                        if (buffer[2] != 0)
                        {
                            route.PendingAck[buffer[2]] = true;
                            _logger.LogInformation($"Ack #{buffer[2]} received.");
                        }
                    }
                }
            }));

            //Open a new thread to send packets.
            Tasks.Enqueue(Task.Run(async () =>
            {
                while (true)
                {
                    foreach (var (enp, route)
                        in Route.SkipWhile(p =>
                            p.Value.OnTheWay || p.Value.PendingSend.IsEmpty))
                    {
                        //Prevent new thread created before this window is completed.
                        route.OnTheWay = true;

                        //Create a thread to process pending send at this time.
                        Tasks.Enqueue(Task.Run(async () =>
                        {
                            //Sleep 10 ms to make the window larger
                            await Task.Delay(10);
                            //Stop to accept 
                            route.StopAccept = true;
                            //Mark the window size
                            route.CurrentSendWindowSize = (byte)route.PendingSend.Count;
                            _logger.LogInformation($"New window with size {route.CurrentSendWindowSize}");
                            //Constructs 4 byte header
                            var header = new byte[] { 0, 0, 0, route.CurrentSendWindowSize };
                            //The number of the packet, starting from 1
                            byte number = 1;
                            var sendTasks = new Queue<Task>();
                            var resendTasks = new Queue<Task>();
                            while (route.PendingSend.TryDequeue(out var result))
                            {
                                await using var ms = new MemoryStream();
                                //Update header
                                header[route.CurrentSendNamespace + 1] = number;
                                await ms.WriteAsync(header);
                                await ms.WriteAsync(result);
                                var data = ms.ToArray();
                                //Send data without waiting.
                                sendTasks.Enqueue(UdpClient.SendAsync(data, data.Length, enp));
                                //Create a new thread to watch and process resend
                                var num = number;
                                resendTasks.Enqueue(Task.Run(async () =>
                                {
                                    while (!route.PendingAck.ContainsKey(num))
                                    {
                                        //While not ack, waiting
                                        for (var i = 0; i < 500; i++)
                                        {
                                            await Task.Delay(1);
                                            //Stop waiting, ack is received
                                            if(route.PendingAck.ContainsKey(num))
                                                return;
                                        }
                                        //Resend
                                        await UdpClient.SendAsync(data, data.Length, enp);
                                        _logger.LogError($"Packet #{num} sent error, resending.");
                                    }
                                }));
                                number++;
                            }

                            //Wait all first send task completed.
                            await Task.WhenAll(sendTasks);
                            _logger.LogInformation("All packet sent for the first time.");
                            //Make the pending send acceptable again
                            route.StopAccept = false;

                            //Wait the whole window is send
                            await Task.WhenAll(resendTasks);

                            //Make the sending area clean
                            //Switch namespace
                            route.CurrentSendNamespace = route.CurrentSendNamespace == 0 ? 1 : 0;
                            //Cleanup pending ack
                            route.PendingAck.Clear();
                            _logger.LogInformation("All packet sent for sure.");
                            //Make this could run again to send next window.
                            route.OnTheWay = false;
                        }));
                    }

                    await Task.Delay(10);
                }
            }));
        }

        public async Task<IUdpProtoClient> ListenAsync()
        {
            SelectiveRepeatProtoClientClientServerInstance instance;
            while (!PendingInstance.TryDequeue(out instance))
            {
                await Task.Delay(10);
            }

            return instance;
        }
    }
}
