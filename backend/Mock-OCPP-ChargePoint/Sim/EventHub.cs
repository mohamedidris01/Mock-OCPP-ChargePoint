using System.Threading.Channels;

namespace MockOcpp.Sim;

/// <param name="Kind">"wire-out", "wire-in" or "log".</param>
public sealed record FleetEvent(long Seq, DateTimeOffset Time, string Node, string Kind, string Text);

/// <summary>Keeps the most recent log / wire events and fans new ones out to
/// live subscribers (the UI's server-sent-event streams).</summary>
public sealed class EventHub
{
    private const int Capacity = 2000;

    private readonly object _lock = new();
    private readonly Queue<FleetEvent> _recent = new();
    private readonly List<Channel<FleetEvent>> _subscribers = new();
    private long _seq;

    public void Publish(string node, string kind, string text)
    {
        lock (_lock)
        {
            var e = new FleetEvent(++_seq, DateTimeOffset.UtcNow, node, kind, text);
            _recent.Enqueue(e);
            while (_recent.Count > Capacity) _recent.Dequeue();
            foreach (var s in _subscribers) s.Writer.TryWrite(e);
        }
    }

    public IReadOnlyList<FleetEvent> Recent(long afterSeq = 0, int limit = 500)
    {
        lock (_lock)
        {
            var items = _recent.Where(e => e.Seq > afterSeq).ToList();
            return items.Count > limit ? items.GetRange(items.Count - limit, limit) : items;
        }
    }

    public void Clear()
    {
        lock (_lock) _recent.Clear();
    }

    /// <summary>New events only; the subscription ends when <paramref name="ct"/> is cancelled.</summary>
    public ChannelReader<FleetEvent> Subscribe(CancellationToken ct)
    {
        var channel = Channel.CreateBounded<FleetEvent>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        lock (_lock) _subscribers.Add(channel);
        ct.Register(() =>
        {
            lock (_lock) _subscribers.Remove(channel);
            channel.Writer.TryComplete();
        });
        return channel.Reader;
    }
}
