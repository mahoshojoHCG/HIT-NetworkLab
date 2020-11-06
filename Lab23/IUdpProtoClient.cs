using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Lab23
{
    public interface IUdpProtoClient
    {
        public bool Connected { get; }
        public ValueTask ConnectAsync(IPEndPoint endPoint);
        public Task<byte[]> ReceiveAsync();
        public ValueTask SendAsync(byte[] buffer);
        public bool TransferCompleted { get; }
    }

    public interface IUdpProtoServer
    {
        public bool Connected { get; }
        public ValueTask BindAsync(IPEndPoint endPoint);
        public Task<IUdpProtoClient> ListenAsync();
        
    }
}
