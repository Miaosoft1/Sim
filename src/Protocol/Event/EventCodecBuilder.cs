using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Protocol.Event;

internal sealed record EventContract(string Id, Type Type, EventDirection Direction);

public enum EventDirection { ClientToServer, ServerToClient }

public sealed class EventCodecBuilder
{
    private readonly Dictionary<string, EventContract> byId = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, EventContract> byType = [];
    private bool built;

    public EventCodecBuilder Register<T>(string id, EventDirection direction) where T : IEventMessage
    {
        if (built) throw new InvalidOperationException("The event catalog has already been built.");
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!Enum.IsDefined(direction))
            throw new ArgumentOutOfRangeException(nameof(direction));
        if (byId.ContainsKey(id))
            throw new InvalidOperationException($"Event identifier '{id}' is already registered.");
        if (byType.ContainsKey(typeof(T)))
            throw new InvalidOperationException($"Event type '{typeof(T).FullName}' is already registered.");
        var contract = new EventContract(id, typeof(T), direction);
        byId.Add(id, contract);
        byType.Add(typeof(T), contract);
        return this;
    }

    public EventCodecRegistry Build()
    {
        if (built) throw new InvalidOperationException("The event catalog has already been built.");
        built = true;
        return new EventCodecRegistry(byId, byType);
    }
}
