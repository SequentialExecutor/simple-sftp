using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace FxSsh.Services.Sftp
{
    // One consumer owns the byte stream. Producers wait when the bounded queue
    // fills; cancellation always releases them during channel/session teardown.
    internal sealed class PacketSender : IDisposable
    {
        internal const int Capacity = 32;
        private readonly Channel<byte[]> queue = System.Threading.Channels.Channel.CreateBounded<byte[]>(new BoundedChannelOptions(Capacity)
        {
            SingleReader = true, FullMode = BoundedChannelFullMode.Wait
        });
        private readonly CancellationTokenSource cancellation = new();
        private int disposed;
        internal Task Completion { get; }

        internal PacketSender(Func<byte[], CancellationToken, Task> send, Action<Exception> failed, Action finished)
        {
            Completion = Task.Run(async () =>
            {
                try
                {
                    await foreach (var packet in queue.Reader.ReadAllAsync(cancellation.Token))
                        await send(packet, cancellation.Token).ConfigureAwait(false);
                    if (!cancellation.IsCancellationRequested) finished();
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    cancellation.Cancel();
                    queue.Writer.TryComplete(ex);
                    failed(ex);
                }
                finally
                {
                    while (queue.Reader.TryRead(out _)) { }
                }
            });
        }

        internal void Send(byte[] packet) => queue.Writer.WriteAsync(packet, cancellation.Token).AsTask().GetAwaiter().GetResult();
        internal void Complete() => queue.Writer.TryComplete();
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            cancellation.Cancel();
            queue.Writer.TryComplete();
            // The token remains valid until every blocked producer has unwound.
        }
    }
}
