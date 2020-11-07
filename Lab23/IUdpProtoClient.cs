using System.Net;
using System.Threading.Tasks;

namespace Lab23
{
    public interface IUdpProtoClient
    {
        public bool Connected { get; }
        public bool TransferCompleted { get; }
        public ValueTask ConnectAsync(IPEndPoint endPoint);
        public Task<byte[]> ReceiveAsync();
        public ValueTask SendAsync(byte[] buffer);
    }

    public interface IUdpProtoServer
    {
        public bool Connected { get; }
        public ValueTask BindAsync(IPEndPoint endPoint);
        public Task<IUdpProtoClient> ListenAsync();
    }
}