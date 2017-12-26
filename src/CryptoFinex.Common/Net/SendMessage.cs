using System.Threading.Tasks;

namespace CryptoFinex.Common.Net
{
    public struct SendMessage
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
