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
    public class SelectiveRepeatClient : IUdpProtoClient
    {
        public bool Connected { get; private set; }
        private UdpClient UdpClient { get; } = new UdpClient();
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

        //Default namespace is 0
        private int CurrentSendNamespace { get; set; }
        private int CurrentReceiveNamespace { get; set; }
        private byte CurrentReceiveWindowSize { get; set; }
        private byte CurrentSendWindowSize { get; set; }
        private ConcurrentDictionary<byte, byte[]> WaitingForConfirmReceive { get; } =
            new ConcurrentDictionary<byte, byte[]>();
        private ConcurrentDictionary<byte, bool> PendingAck { get; } =
            new ConcurrentDictionary<byte, bool>();
        private ConcurrentBag<Task> Tasks { get; } = new ConcurrentBag<Task>();
        private ILogger _logger;
        public SelectiveRepeatClient(ILogger<SelectiveRepeatClient> logger)
        {
            _logger = logger;
        }
        public async ValueTask ConnectAsync(IPEndPoint endPoint)
        {
            await Task.FromResult(0);
            if (!Connected)
            {
                UdpClient.Connect(endPoint);
                Connected = true;
                //Open a thread to receive packets
                Tasks.Add(Task.Run(async () =>
                {
                    while (true)
                    {
                        var receive = await UdpClient.ReceiveAsync();
                        var buffer = receive.Buffer;
                        //Bad packet, throw
                        if (buffer.Length < 4)
                            continue;
                        //Standard packet
                        if (buffer[0] == 0)
                        {
                            //This is a resend of last namespace
                            if (buffer[CurrentReceiveNamespace + 1] == 0)
                            {
                                var ack = new byte[] { 1, 0, 0, buffer[3] };
                                //Resend ack is from the other namespace
                                if (CurrentReceiveNamespace == 0)
                                    ack[2] = buffer[2];
                                else
                                    ack[1] = ack[1];
                                _logger.LogInformation(
                                    $"Received a resent packet of last namespace from {receive.RemoteEndPoint}");
                                //Directly resend ack
                                await UdpClient.SendAsync(ack, 4);
                            }
                            //Not resend, just normal packet
                            else
                            {
                                var groupNumber = buffer[CurrentReceiveNamespace + 1];
                                //Set window size
                                if (CurrentReceiveWindowSize == 0)
                                    CurrentReceiveWindowSize = buffer[3];
                                //Not check, if a resend get, cover the last one
                                WaitingForConfirmReceive[groupNumber] = buffer;
                                _logger.LogInformation(
                                    $"Received a packet #{groupNumber} of last namespace from " +
                                    $"{receive.RemoteEndPoint}, widow size is {CurrentReceiveWindowSize}");
                                //Send ack
                                var ack = new byte[] { 1, 0, 0, buffer[3] };
                                ack[CurrentReceiveNamespace + 1] = groupNumber;
                                await UdpClient.SendAsync(ack, 4);
                            }

                        }

                        //Ack
                        if (buffer[0] == 1)
                        {
                            //Not sure ack in which namespace
                            if (buffer[1] != 0)
                            {
                                PendingAck[buffer[1]] = true;
                                _logger.LogInformation($"Ack #{buffer[1]} received.");
                            }

                            if (buffer[2] != 0)
                            {
                                PendingAck[buffer[2]] = true;
                                _logger.LogInformation($"Ack #{buffer[2]} received.");
                            }
                        }
                    }
                }));

                //Open a new thread to send packets.
                Tasks.Add(Task.Run(async () =>
                {
                    while (true)
                    {
                        while (PendingSend.IsEmpty)
                        {
                            await Task.Delay(10);
                        }

                        TransferCompleted = false;
                        //Sleep 10 ms to make the window larger
                        await Task.Delay(10);
                        //Stop to accept 
                        StopAccept = true;
                        //Mark the window size
                        CurrentSendWindowSize = (byte)PendingSend.Count;
                        _logger.LogInformation($"New window with size {CurrentSendWindowSize}");
                        //Constructs 4 byte header
                        var header = new byte[] { 0, 0, 0, CurrentSendWindowSize };
                        //The number of the packet, starting from 1
                        byte number = 1;
                        var sendTasks = new Queue<Task>();
                        var resendTasks = new Queue<Task>();
                        while (PendingSend.TryDequeue(out var result))
                        {
                            await using var ms = new MemoryStream();
                            //Update header
                            header[CurrentSendNamespace + 1] = number;
                            await ms.WriteAsync(header);
                            await ms.WriteAsync(result);
                            var data = ms.ToArray();
                            //Send data without waiting.
                            sendTasks.Enqueue(UdpClient.SendAsync(data, data.Length));
                            //Create a new thread to watch and process resend
                            var num = number;
                            resendTasks.Enqueue(Task.Run(async () =>
                            {
                                while (!PendingAck.ContainsKey(num))
                                {
                                    //While not ack, waiting
                                    for (var i = 0; i < 500; i++)
                                    {
                                        await Task.Delay(1);
                                        //Stop waiting, ack is received
                                        if (PendingAck.ContainsKey(num))
                                            return;
                                    }
                                    //Resend
                                    await UdpClient.SendAsync(data, data.Length);
                                    _logger.LogError($"Packet #{num} sent error, resending.");
                                }
                            }));
                            number++;
                        }

                        //Wait all first send task completed.
                        await Task.WhenAll(sendTasks);
                        _logger.LogInformation("All packet sent for the first time.");
                        //Make the pending send acceptable again
                        StopAccept = false;

                        //Wait the whole window is send
                        await Task.WhenAll(resendTasks);

                        //Make the sending area clean
                        //Switch namespace
                        CurrentSendNamespace = CurrentSendNamespace == 0 ? 1 : 0;
                        //Cleanup pending ack
                        PendingAck.Clear();
                        _logger.LogInformation("All packet sent for sure.");
                        TransferCompleted = true;
                    }
                }));

                //Opens a new thread to send packets
                Tasks.Add(Task.Run(async () =>
                {

                    while (true)
                    {
                        //Wait until new window comes out
                        while (CurrentReceiveWindowSize == 0)
                        {
                            await Task.Delay(10);
                        }

                        //Process the received data
                        for (byte i = 1; i <= CurrentReceiveWindowSize; i++)
                        {
                            byte[] value;
                            while (!WaitingForConfirmReceive.TryGetValue(i, out value))
                            {
                                await Task.Delay(10);
                            }
                            PendingReceive.Enqueue(value[4..]);
                        }
                        //Switch window
                        CurrentReceiveNamespace = CurrentReceiveNamespace == 0 ? 1 : 0;
                        WaitingForConfirmReceive.Clear();
                        CurrentReceiveWindowSize = 0;
                        _logger.LogInformation("Receive window switched.");
                    }
                }));
            }
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

                await Task.Delay(10);
            }

            TransferCompleted = false;
            PendingSend.Enqueue(buffer);
        }

        public bool TransferCompleted { get; private set; } = true;
    }
}
