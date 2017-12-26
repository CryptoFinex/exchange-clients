﻿using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.WebSockets;

namespace CryptoFinex.Common.Net
{
    public class HttpOptions
    {
        public HttpMessageHandler HttpMessageHandler { get; set; }
        public IReadOnlyCollection<KeyValuePair<string, string>> Headers { get; set; }
        public Func<string> JwtBearerTokenFactory { get; set; }

        /// <summary>
        /// Gets or sets a delegate that will be invoked with the <see cref="ClientWebSocketOptions"/> object used
        /// by the <see cref="WebSocketsTransport"/> to configure the WebSocket.
        /// </summary>
        /// <remarks>
        /// This delegate is invoked after headers from <see cref="Headers"/> and the JWT bearer token from <see cref="JwtBearerTokenFactory"/>
        /// has been applied.
        /// </remarks>
        public Action<ClientWebSocketOptions> WebSocketOptions { get; set; }
    }
}
