using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CryptoFinex.Common.Net.Internal
{
    internal class WebSocketsTransport
    {
        private readonly ClientWebSocket webSocket;
        private Channel<byte[], SendMessage> channel;
        private readonly CancellationTokenSource transportCts = new CancellationTokenSource();
        private readonly CancellationTokenSource receiveCts = new CancellationTokenSource();
        private readonly ILogger logger;
        private string connectionId;

        public Task Running { get; private set; } = Task.CompletedTask;

        public TransferMode? Mode { get; private set; }

        public WebSocketsTransport()
            : this(null, null)
        {
        }

        public WebSocketsTransport(HttpOptions httpOptions, ILoggerFactory loggerFactory)
        {
            webSocket = new ClientWebSocket();

            if (httpOptions?.Headers != null)
            {
                foreach (var header in httpOptions.Headers)
                {
                    webSocket.Options.SetRequestHeader(header.Key, header.Value);
                }
            }

            if (httpOptions?.JwtBearerTokenFactory != null)
            {
                webSocket.Options.SetRequestHeader("Authorization", $"Bearer {httpOptions.JwtBearerTokenFactory()}");
            }

            httpOptions?.WebSocketOptions?.Invoke(webSocket.Options);

            logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<WebSocketsTransport>();
        }

        public async Task StartAsync(Uri url, Channel<byte[], SendMessage> application, TransferMode requestedTransferMode, string connectionId)
        {
            if (url == null)
            {
                throw new ArgumentNullException(nameof(url));
            }

            if (application == null)
            {
                throw new ArgumentNullException(nameof(application));
            }

            if (requestedTransferMode != TransferMode.Binary && requestedTransferMode != TransferMode.Text)
            {
                throw new ArgumentException("Invalid transfer mode.", nameof(requestedTransferMode));
            }

            channel = application;
            Mode = requestedTransferMode;
            this.connectionId = connectionId;

            logger.StartTransport(this.connectionId, Mode.Value);

            await Connect(url);
            var sendTask = SendMessages(url);
            var receiveTask = ReceiveMessages(url);

            // TODO: Handle TCP connection errors
            // https://github.com/SignalR/SignalR/blob/1fba14fa3437e24c204dfaf8a18db3fce8acad3c/src/Microsoft.AspNet.SignalR.Core/Owin/WebSockets/WebSocketHandler.cs#L248-L251
            Running = Task.WhenAll(sendTask, receiveTask).ContinueWith(t =>
            {
                webSocket.Dispose();
                logger.TransportStopped(this.connectionId, t.Exception?.InnerException);
                channel.Writer.TryComplete(t.IsFaulted ? t.Exception.InnerException : null);
                return t;
            }).Unwrap();
        }

        private async Task ReceiveMessages(Uri pollUrl)
        {
            logger.StartReceive(connectionId);

            try
            {
                while (!receiveCts.Token.IsCancellationRequested)
                {
                    const int bufferSize = 4096;
                    var totalBytes = 0;
                    var incomingMessage = new List<ArraySegment<byte>>();
                    WebSocketReceiveResult receiveResult;
                    do
                    {
                        var buffer = new ArraySegment<byte>(new byte[bufferSize]);

                        //Exceptions are handled above where the send and receive tasks are being run.
                        receiveResult = await webSocket.ReceiveAsync(buffer, receiveCts.Token);
                        if (receiveResult.MessageType == WebSocketMessageType.Close)
                        {
                            logger.WebSocketClosed(connectionId, receiveResult.CloseStatus);

                            channel.Writer.Complete(
                                receiveResult.CloseStatus == WebSocketCloseStatus.NormalClosure
                                ? null
                                : new InvalidOperationException(
                                    $"Websocket closed with error: {receiveResult.CloseStatus}."));
                            return;
                        }

                        logger.MessageReceived(connectionId, receiveResult.MessageType, receiveResult.Count, receiveResult.EndOfMessage);

                        var truncBuffer = new ArraySegment<byte>(buffer.Array, 0, receiveResult.Count);
                        incomingMessage.Add(truncBuffer);
                        totalBytes += receiveResult.Count;
                    } while (!receiveResult.EndOfMessage);

                    //Making sure the message type is either text or binary
                    Debug.Assert((receiveResult.MessageType == WebSocketMessageType.Binary || receiveResult.MessageType == WebSocketMessageType.Text), "Unexpected message type");

                    var messageBuffer = new byte[totalBytes];
                    if (incomingMessage.Count > 1)
                    {
                        var offset = 0;
                        for (var i = 0; i < incomingMessage.Count; i++)
                        {
                            Buffer.BlockCopy(incomingMessage[i].Array, 0, messageBuffer, offset, incomingMessage[i].Count);
                            offset += incomingMessage[i].Count;
                        }
                    }
                    else
                    {
                        Buffer.BlockCopy(incomingMessage[0].Array, incomingMessage[0].Offset, messageBuffer, 0, incomingMessage[0].Count);
                    }

                    try
                    {
                        if (!transportCts.Token.IsCancellationRequested)
                        {
                            logger.MessageToApp(connectionId, messageBuffer.Length);
                            while (await channel.Writer.WaitToWriteAsync(transportCts.Token))
                            {
                                if (channel.Writer.TryWrite(messageBuffer))
                                {
                                    incomingMessage.Clear();
                                    break;
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        logger.CancelMessage(connectionId);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                logger.ReceiveCanceled(connectionId);
            }
            finally
            {
                logger.ReceiveStopped(connectionId);
                transportCts.Cancel();
            }
        }

        private async Task SendMessages(Uri sendUrl)
        {
            logger.SendStarted(connectionId);

            var webSocketMessageType =
                Mode == TransferMode.Binary
                    ? WebSocketMessageType.Binary
                    : WebSocketMessageType.Text;

            try
            {
                while (await channel.Reader.WaitToReadAsync(transportCts.Token))
                {
                    while (channel.Reader.TryRead(out SendMessage message))
                    {
                        try
                        {
                            logger.ReceivedFromApp(connectionId, message.Payload.Length);

                            await webSocket.SendAsync(new ArraySegment<byte>(message.Payload), webSocketMessageType, true, transportCts.Token);

                            message.SendResult.SetResult(null);
                        }
                        catch (OperationCanceledException)
                        {
                            logger.SendMessageCanceled(connectionId);
                            message.SendResult.SetCanceled();
                            await CloseWebSocket();
                            break;
                        }
                        catch (Exception ex)
                        {
                            logger.ErrorSendingMessage(connectionId, ex);
                            message.SendResult.SetException(ex);
                            await CloseWebSocket();
                            throw;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                logger.SendCanceled(connectionId);
            }
            finally
            {
                logger.SendStopped(connectionId);
                TriggerCancel();
            }
        }

        private async Task Connect(Uri url)
        {
            var uriBuilder = new UriBuilder(url);
            if (url.Scheme == "http")
            {
                uriBuilder.Scheme = "ws";
            }
            else if (url.Scheme == "https")
            {
                uriBuilder.Scheme = "wss";
            }

            await webSocket.ConnectAsync(uriBuilder.Uri, CancellationToken.None);
        }

        public async Task StopAsync()
        {
            logger.TransportStopping(connectionId);

            await CloseWebSocket();

            try
            {
                await Running;
            }
            catch
            {
                // exceptions have been handled in the Running task continuation by closing the channel with the exception
            }
        }

        private async Task CloseWebSocket()
        {
            try
            {
                // Best effort - it's still possible (but not likely) that the transport is being closed via StopAsync
                // while the webSocket is being closed due to an error.
                if (webSocket.State != WebSocketState.Closed)
                {
                    logger.ClosingWebSocket(connectionId);

                    // We intentionally don't pass _transportCts.Token to CloseOutputAsync. The token can be cancelled
                    // for reasons not related to webSocket in which case we would not close the websocket gracefully.
                    await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);

                    // shutdown the transport after a timeout in case the server does not send close frame
                    TriggerCancel();
                }
            }
            catch (Exception ex)
            {
                // This is benign - the exception can happen due to the race described above because we would
                // try closing the webSocket twice.
                logger.ClosingWebSocketFailed(connectionId, ex);
            }
        }

        private void TriggerCancel()
        {
            // Give server 5 seconds to respond with a close frame for graceful close.
            receiveCts.CancelAfter(TimeSpan.FromSeconds(5));
            transportCts.Cancel();
        }
    }
}
