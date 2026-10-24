using System.Threading.Channels;

namespace Logister.AspNetCore;

/// <summary>
/// A bounded in-memory queue of prepared events. Request middleware writes without waiting, and the
/// hosted delivery service drains it in batches. When the queue is full, new events are dropped.
/// </summary>
public sealed class LogisterEventQueue
{
    private readonly Channel<PreparedEvent> _channel;
    private long _dropped;

    public LogisterEventQueue(LogisterAspNetCoreOptions options)
        : this(options?.QueueCapacity ?? throw new ArgumentNullException(nameof(options)))
    {
    }

    public LogisterEventQueue(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _channel = Channel.CreateBounded<PreparedEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    /// <summary>Number of events dropped because the queue was full or already completed.</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>Number of events currently waiting for delivery.</summary>
    public int Count => _channel.Reader.Count;

    /// <summary>Adds an event without blocking. Returns false when the event was dropped.</summary>
    public bool TryEnqueue(PreparedEvent prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (_channel.Writer.TryWrite(prepared)) return true;
        Interlocked.Increment(ref _dropped);
        return false;
    }

    internal ChannelReader<PreparedEvent> Reader => _channel.Reader;
}
