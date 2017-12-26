using System;
using System.Threading.Tasks;
using NUnit.Framework;
using CryptoFinex.Common.Net;
using System.Threading;

namespace CryptoFinex.BitFinexClient.Tests
{
    [TestFixture]
    public class Class1
    {
        [Test]
        public void Foo()
        {
            var connection = new WebSocketConnection(new Uri("wss://api.bitfinex.com/ws/2"), null, new HttpOptions());
            connection.OnReceived((data, state) => Connection_Received(data), null);
            connection.StartAsync().Wait();
            Thread.Sleep(10 * 1000);
        }

        private Task Connection_Received(byte[] arg)
        {
            string result = System.Text.Encoding.UTF8.GetString(arg);
            throw new NotImplementedException();
        }

        [Test]
        public void Foo2()
        {

        }
    }
}
