﻿using Pipelines.Sockets.Unofficial;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Threading.Tasks;

namespace SimplPipelines
{
    public class SimplPipelineClient : SimplPipeline
    {
        public SimplPipelineClient(IDuplexPipe pipe) : base(pipe)
            => StartReceiveLoopAsync().ContinueWith( // fire and forget
                t => GC.KeepAlive(t.Exception), TaskContinuationOptions.OnlyOnFaulted);

        public static async Task<SimplPipelineClient> ConnectAsync(EndPoint endpoint)
            => new SimplPipelineClient(await SocketConnection.ConnectAsync(endpoint));

        private readonly Dictionary<int, TaskCompletionSource<IMemoryOwner<byte>>> _awaitingResponses
            = new Dictionary<int, TaskCompletionSource<IMemoryOwner<byte>>>();

        private int _nextMessageId;
        public ValueTask SendAsync(ReadOnlyMemory<byte> message)
            => WriteAsync(message, 0);

        public async Task<IMemoryOwner<byte>> SendReciveAsync(IMemoryOwner<byte> message)
        {
            using (message)
            {
                return await SendReciveAsync(message.Memory);
            }
        }
        public Task<IMemoryOwner<byte>> SendReciveAsync(ReadOnlyMemory<byte> message)
        {
            async Task<IMemoryOwner<byte>> Awaited(ValueTask pendingWrite, Task<IMemoryOwner<byte>> response)
            {
                await pendingWrite;
                return await response;
            }

            var tcs = new TaskCompletionSource<IMemoryOwner<byte>>();
            int messageId;
            lock (_awaitingResponses)
            {
                messageId = ++_nextMessageId;
                if (messageId == 0) messageId = 1; // wrap around avoiding zero
                _awaitingResponses.Add(messageId, tcs);
            }
            var write = WriteAsync(message, messageId);
            return write.IsCompletedSuccessfully ? tcs.Task : Awaited(write, tcs.Task);
        }

        protected override ValueTask OnReceiveAsync(ReadOnlySequence<byte> payload, int messageId)
        {
            if (messageId != 0)
            {
                // request/response
                TaskCompletionSource<IMemoryOwner<byte>> tcs;
                lock (_awaitingResponses)
                {
                    if (_awaitingResponses.TryGetValue(messageId, out tcs))
                    {
                        _awaitingResponses.Remove(messageId);
                    }
                    else
                    {   // didn't find a twin, but... meh
                        tcs = null;
                        messageId = 0; // treat as MessageReceived
                    }
                }
                tcs?.TrySetResult(payload.Lease());
            }
            if (messageId == 0)
            {
                // unsolicited
                MessageReceived?.Invoke(payload.Lease());
            }
            return default;
        }
        public event Action<IMemoryOwner<byte>> MessageReceived;
    }
}
