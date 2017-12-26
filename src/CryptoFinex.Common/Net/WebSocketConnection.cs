using System;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CryptoFinex.Common.Net.Internal;
using CryptoFinex.Common.Threading;

namespace CryptoFinex.Common.Net
{
    public class WebSocketConnection : IConnection
    {
        private readonly ILogger logger;
        private readonly ILoggerFactory loggerFactory;

        private volatile ConnectionState connectionState = ConnectionState.Disconnected;
        private readonly object stateChangeLock = new object();

        private TaskCompletionSource<object> startTcs;
        private TaskCompletionSource<object> closeTcs;
        private Exception abortException;

        private volatile ChannelConnection<byte[], SendMessage> transportChannel;
        private volatile Task receiveLoopTask;
        private TaskQueue eventQueue;
        private readonly TimeSpan _eventQueueDrainTimeout = TimeSpan.FromSeconds(5);
        private ChannelReader<byte[]> Input => transportChannel.Input;
        private ChannelWriter<SendMessage> Output => transportChannel.Output;
        private readonly WebSocketsTransport webSosketTransport;
        private readonly HttpOptions httpOptions;
        private readonly List<ReceiveCallback> callbacks = new List<ReceiveCallback>();
        private readonly string connectionId;

        public Uri Url { get; }

        public WebSocketConnection(Uri url, ILoggerFactory loggerFactory, HttpOptions httpOptions)
        {
            Url = url ?? throw new ArgumentNullException(nameof(url));
            this.loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
            this.logger = this.loggerFactory.CreateLogger<WebSocketConnection>();
            this.httpOptions = httpOptions;
            this.webSosketTransport = new WebSocketsTransport(this.httpOptions, this.loggerFactory);
            this.connectionId = Guid.NewGuid().ToString();
        }

        public async Task StartAsync() => await StartAsyncCore().ForceAsync();

        private Task StartAsyncCore()
        {
            if (ChangeState(from: ConnectionState.Disconnected, to: ConnectionState.Connecting) != ConnectionState.Disconnected)
            {
                return Task.FromException(new InvalidOperationException($"Cannot start a connection that is not in the {nameof(ConnectionState.Disconnected)} state."));
            }

            startTcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            eventQueue = new TaskQueue();

            StartAsyncInternal()
                .ContinueWith(t =>
                {
                    var abortException = this.abortException;
                    if (t.IsFaulted || abortException != null)
                    {
                        startTcs.SetException(abortException ?? t.Exception.InnerException);
                    }
                    else if (t.IsCanceled)
                    {
                        startTcs.SetCanceled();
                    }
                    else
                    {
                        startTcs.SetResult(null);
                    }
                });

            return startTcs.Task;
        }

        private async Task StartAsyncInternal()
        {
            logger.HttpConnectionStarting();

            try
            {
                var connectUrl = Url;
                
                logger.StartingTransport(connectionId, "WebSocket", connectUrl);

                await StartTransport(connectUrl);
            }
            catch
            {
                // The connection can now be either in the Connecting or Disposed state - only change the state to
                // Disconnected if the connection was in the Connecting state to not resurrect a Disposed connection
                ChangeState(from: ConnectionState.Connecting, to: ConnectionState.Disconnected);
                throw;
            }

            // if the connection is not in the Connecting state here it means the user called DisposeAsync while
            // the connection was starting
            if (ChangeState(from: ConnectionState.Connecting, to: ConnectionState.Connected) == ConnectionState.Connecting)
            {
                closeTcs = new TaskCompletionSource<object>();

                _ = Input.Completion.ContinueWith(async t =>
                {
                    // Grab the exception and then clear it.
                    // See comment at AbortAsync for more discussion on the thread-safety
                    // StartAsync can't be called until the ChangeState below, so we're OK.
                    var abortException = this.abortException;
                    this.abortException = null;

                    // There is an inherent race between receive and close. Removing the last message from the channel
                    // makes Input.Completion task completed and runs this continuation. We need to await _receiveLoopTask
                    // to make sure that the message removed from the channel is processed before we drain the queue.
                    // There is a short window between we start the channel and assign the _receiveLoopTask a value.
                    // To make sure that _receiveLoopTask can be awaited (i.e. is not null) we need to await _startTask.
                    logger.ProcessRemainingMessages(connectionId);

                    await startTcs.Task;
                    await receiveLoopTask;

                    logger.DrainEvents(connectionId);

                    await Task.WhenAny(eventQueue.Drain().NoThrow(), Task.Delay(_eventQueueDrainTimeout));

                    logger.CompleteClosed(connectionId);

                    // At this point the connection can be either in the Connected or Disposed state. The state should be changed
                    // to the Disconnected state only if it was in the Connected state.
                    // From this point on, StartAsync can be called at any time.
                    ChangeState(from: ConnectionState.Connected, to: ConnectionState.Disconnected);

                    closeTcs.SetResult(null);

                    try
                    {
                        if (t.IsFaulted)
                        {
                            Closed?.Invoke(t.Exception.InnerException);
                        }
                        else
                        {
                            // Call the closed event. If there was an abort exception, it will be flowed forward
                            // However, if there wasn't, this will just be null and we're good
                            Closed?.Invoke(abortException);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Suppress (but log) the exception, this is user code
                        logger.ErrorDuringClosedEvent(ex);
                    }
                });

                receiveLoopTask = ReceiveAsync();
            }
        }

        private async Task StartTransport(Uri connectUrl)
        {
            var applicationToTransport = Channel.CreateUnbounded<SendMessage>();
            var transportToApplication = Channel.CreateUnbounded<byte[]>();
            var applicationSide = ChannelConnection.Create(applicationToTransport, transportToApplication);
            transportChannel = ChannelConnection.Create(transportToApplication, applicationToTransport);

            // Start the transport, giving it one end of the pipeline
            try
            {
                await webSosketTransport.StartAsync(connectUrl, applicationSide, GetTransferMode(), connectionId);

                // actual transfer mode can differ from the one that was requested so set it on the feature
                if (!webSosketTransport.Mode.HasValue)
                {
                    // This can happen with custom transports so it should be an exception, not an assert.
                    throw new InvalidOperationException("Transport was expected to set the Mode property after StartAsync, but it has not been set.");
                }
                SetTransferMode(webSosketTransport.Mode.Value);
            }
            catch (Exception ex)
            {
                logger.ErrorStartingTransport(connectionId, webSosketTransport.GetType().Name, ex);
                throw;
            }
        }

        private TransferMode GetTransferMode()
        {
            return TransferMode.Text;
        }

        private void SetTransferMode(TransferMode transferMode)
        {
            
        }

        private async Task ReceiveAsync()
        {
            try
            {
                logger.HttpReceiveStarted(connectionId);

                while (await Input.WaitToReadAsync())
                {
                    if (connectionState != ConnectionState.Connected)
                    {
                        logger.SkipRaisingReceiveEvent(connectionId);
                        // drain
                        Input.TryRead(out _);
                        continue;
                    }

                    if (Input.TryRead(out var buffer))
                    {
                        logger.ScheduleReceiveEvent(connectionId);
                        _ = eventQueue.Enqueue(async () =>
                        {
                            logger.RaiseReceiveEvent(connectionId);

                            // Copying the callbacks to avoid concurrency issues
                            ReceiveCallback[] callbackCopies;
                            lock (callbacks)
                            {
                                callbackCopies = new ReceiveCallback[callbacks.Count];
                                callbacks.CopyTo(callbackCopies);
                            }

                            foreach (var callbackObject in callbackCopies)
                            {
                                try
                                {
                                    await callbackObject.InvokeAsync(buffer);
                                }
                                catch (Exception ex)
                                {
                                    logger.ExceptionThrownFromCallback(connectionId, nameof(OnReceived), ex);
                                }
                            }
                        });
                    }
                    else
                    {
                        logger.FailedReadingMessage(connectionId);
                    }
                }

                await Input.Completion;
            }
            catch (Exception ex)
            {
                Output.TryComplete(ex);
                logger.ErrorReceiving(connectionId, ex);
            }

            logger.EndReceive(connectionId);
        }

        public async Task SendAsync(byte[] data, CancellationToken cancellationToken = default) =>
            await SendAsyncCore(data, cancellationToken).ForceAsync();

        private async Task SendAsyncCore(byte[] data, CancellationToken cancellationToken)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            if (connectionState != ConnectionState.Connected)
            {
                throw new InvalidOperationException("Cannot send messages when the connection is not in the Connected state.");
            }

            // TaskCreationOptions.RunContinuationsAsynchronously ensures that continuations awaiting
            // SendAsync (i.e. user's code) are not running on the same thread as the code that sets
            // TaskCompletionSource result. This way we prevent from user's code blocking our channel
            // send loop.
            var sendTcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            var message = new SendMessage(data, sendTcs);

            logger.SendingMessage(connectionId);

            while (await Output.WaitToWriteAsync(cancellationToken))
            {
                if (Output.TryWrite(message))
                {
                    await sendTcs.Task;
                    break;
                }
            }
        }

        // AbortAsync creates a few thread-safety races that we are OK with.
        //  1. If the transport shuts down gracefully after AbortAsync is called but BEFORE _abortException is called, then the
        //     Closed event will not receive the Abort exception. This is OK because technically the transport was shut down gracefully
        //     before it was aborted
        //  2. If the transport is closed gracefully and then AbortAsync is called before it captures the _abortException value
        //     the graceful shutdown could be turned into an abort. However, again, this is an inherent race between two different conditions:
        //     The transport shutting down because the server went away, and the user requesting the Abort
        //  3. Finally, because this is an instance field, there is a possible race around accidentally re-using _abortException in the restarted
        //     connection. The scenario here is: AbortAsync(someException); StartAsync(); CloseAsync(); Where the _abortException value from the
        //     first AbortAsync call is still set at the time CloseAsync gets to calling the Closed event. However, this can't happen because the
        //     StartAsync method can't be called until the connection state is changed to Disconnected, which happens AFTER the close code
        //     captures and resets _abortException.
        public async Task AbortAsync(Exception exception) => await StopAsyncCore(exception ?? throw new ArgumentNullException(nameof(exception))).ForceAsync();

        public async Task StopAsync() => await StopAsyncCore(exception: null).ForceAsync();

        private async Task StopAsyncCore(Exception exception)
        {
            lock (stateChangeLock)
            {
                if (!(connectionState == ConnectionState.Connecting || connectionState == ConnectionState.Connected))
                {
                    logger.SkippingStop(connectionId);
                    return;
                }
            }

            // Note that this method can be called at the same time when the connection is being closed from the server
            // side due to an error. We are resilient to this since we merely try to close the channel here and the
            // channel can be closed only once. As a result the continuation that does actual job and raises the Closed
            // event runs always only once.

            // See comment at AbortAsync for more discussion on the thread-safety of this.
            abortException = exception;

            logger.StoppingClient(connectionId);

            try
            {
                await startTcs.Task;
            }
            catch
            {
                // We only await the start task to make sure that StartAsync completed. The
                // _startTask is returned to the user and they should handle exceptions.
            }

            if (transportChannel != null)
            {
                Output.TryComplete();
            }

            if (webSosketTransport != null)
            {
                await webSosketTransport.StopAsync();
            }

            if (receiveLoopTask != null)
            {
                await receiveLoopTask;
            }

            if (closeTcs != null)
            {
                await closeTcs.Task;
            }
        }

        public async Task DisposeAsync() => await DisposeAsyncCore().ForceAsync();

        private async Task DisposeAsyncCore()
        {
            // This will no-op if we're already stopped
            await StopAsyncCore(exception: null);

            if (ChangeState(to: ConnectionState.Disposed) == ConnectionState.Disposed)
            {
                logger.SkippingDispose(connectionId);

                // the connection was already disposed
                return;
            }
        }

        public event Func<Task> Connected;
        public event Func<byte[], Task> Received;
        public event Func<Exception, Task> Closed;

        private ConnectionState ChangeState(ConnectionState from, ConnectionState to)
        {
            lock (stateChangeLock)
            {
                var state = connectionState;
                if (connectionState == from)
                {
                    connectionState = to;
                }

                logger.ConnectionStateChanged(connectionId, state, to);
                return state;
            }
        }

        private ConnectionState ChangeState(ConnectionState to)
        {
            lock (stateChangeLock)
            {
                var state = connectionState;
                connectionState = to;
                logger.ConnectionStateChanged(connectionId, state, to);
                return state;
            }
        }

        public IDisposable OnReceived(Func<byte[], object, Task> callback, object state)
        {
            var receiveCallback = new ReceiveCallback(callback, state);
            lock (callbacks)
            {
                callbacks.Add(receiveCallback);
            }
            return new Subscription(receiveCallback, callbacks);
        }

        private class ReceiveCallback
        {
            private readonly Func<byte[], object, Task> callback;
            private readonly object state;

            public ReceiveCallback(Func<byte[], object, Task> callback, object state)
            {
                this.callback = callback;
                this.state = state;
            }

            public Task InvokeAsync(byte[] data)
            {
                return callback(data, state);
            }
        }

        private class Subscription : IDisposable
        {
            private readonly ReceiveCallback receiveCallback;
            private readonly List<ReceiveCallback> callbacks;
            public Subscription(ReceiveCallback callback, List<ReceiveCallback> callbacks)
            {
                this.receiveCallback = callback;
                this.callbacks = callbacks;
            }

            public void Dispose()
            {
                lock (callbacks)
                {
                    callbacks.Remove(receiveCallback);
                }
            }
        }

        internal enum ConnectionState
        {
            Disconnected,
            Connecting,
            Connected,
            Disposed
        }
    }
}
