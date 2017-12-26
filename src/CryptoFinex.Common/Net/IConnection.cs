using System;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoFinex.Common.Net
{
    public interface IConnection
    {
        Task StartAsync();
        Task SendAsync(byte[] data, CancellationToken cancellationToken);
        Task DisposeAsync();

        event Func<Task> Connected;
        event Func<byte[], Task> Received;
        event Func<Exception, Task> Closed;
    }
}
