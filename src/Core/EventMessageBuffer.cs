namespace Miaosoft.Sim.Core;

public sealed record BufferedEventMessage(string ActorId, IEventMessage Message, Guid? CorrelationId = null);

public sealed class EventMessageBuffer(int capacity = 1024)
{
    private readonly object sync = new();
    private readonly List<BufferedEventMessage> pending = [];
    private readonly int capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    public int Count { get { lock (sync) return pending.Count; } }
    public int MergedCount { get; private set; }

    public static void Validate(IEventMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if ((message is IDiscreteEvent) == (message is ICoalescibleEvent))
            throw new ArgumentException("An input must declare exactly one queue policy.", nameof(message));
        if (message is ICoalescibleEvent c && string.IsNullOrWhiteSpace(c.CoalescingKey))
            throw new ArgumentException("A coalescible input needs a nonempty key.", nameof(message));
    }

    public static bool CanMerge(BufferedEventMessage older, BufferedEventMessage newer) =>
        older.ActorId == newer.ActorId &&
        older.Message is ICoalescibleEvent a && newer.Message is ICoalescibleEvent b &&
        string.Equals(a.CoalescingKey, b.CoalescingKey, StringComparison.Ordinal);

    public void Enqueue(string actorId, IEventMessage message, Guid? correlationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        Validate(message);
        var entry = new BufferedEventMessage(actorId, message, correlationId);
        lock (sync)
        {
            if (pending.Count != 0 && CanMerge(pending[^1], entry))
            {
                pending[^1] = entry;
                MergedCount++;
                return;
            }
            if (pending.Count == capacity)
                throw new InvalidOperationException($"The event queue is full ({capacity}).");
            pending.Add(entry);
        }
    }

    public IReadOnlyList<BufferedEventMessage> Drain()
    {
        lock (sync)
        {
            var result = pending.ToArray();
            pending.Clear();
            return result;
        }
    }

    public int RemoveWhere(Func<BufferedEventMessage, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        lock (sync) return pending.RemoveAll(entry => predicate(entry));
    }

    public int Clear()
    {
        lock (sync)
        {
            int count = pending.Count;
            pending.Clear();
            return count;
        }
    }
}
