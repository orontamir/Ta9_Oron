using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Ta9.ProcessorService.Application;

namespace Ta9.ProcessorService.Infrastructure;

/// <summary>Bounded <see cref="Channel{T}"/>: async producer/consumer with built-in backpressure.</summary>
public sealed class ChannelJobQueue(IOptions<ProcessingOptions> options) : IJobQueue
{
    private readonly Channel<ProcessingJob> _channel = Channel.CreateBounded<ProcessingJob>(
        new BoundedChannelOptions(options.Value.QueueCapacity)
        {
            // With Wait, TryWrite returns false when full instead of dropping anything.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });

    public bool TryEnqueue(ProcessingJob job) => _channel.Writer.TryWrite(job);

    public IAsyncEnumerable<ProcessingJob> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
