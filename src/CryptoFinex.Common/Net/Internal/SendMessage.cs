using System.Threading.Tasks;

namespace CryptoFinex.Common.Net.Internal
{
    internal struct SendMessage
    {
        public byte[] Payload { get; }
        public TaskCompletionSource<object> SendResult { get; }

        public SendMessage(byte[] payload, TaskCompletionSource<object> result)
        {
            Payload = payload;
            SendResult = result;
        }
    }
}
