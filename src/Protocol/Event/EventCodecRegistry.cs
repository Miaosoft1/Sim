using System.Collections.ObjectModel;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Serialization.Yaml;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Miaosoft.Sim.Protocol.Event;

public sealed class EventCodecRegistry
{
    private readonly IReadOnlyDictionary<string, EventContract> byId;
    private readonly IReadOnlyDictionary<Type, EventContract> byType;
    private readonly object sync = new();
    private readonly ISerializer serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .EnsureRoundtrip().DisableAliases().Build();
    private readonly IDeserializer deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithDuplicateKeyChecking()
        .WithTypeConverter(new ReadOnlyListYamlTypeConverter()).Build();

    internal EventCodecRegistry(
        IDictionary<string, EventContract> contractsById, IDictionary<Type, EventContract> contractsByType)
    {
        byId = new ReadOnlyDictionary<string, EventContract>(
            new Dictionary<string, EventContract>(contractsById, StringComparer.Ordinal));
        byType = new ReadOnlyDictionary<Type, EventContract>(new Dictionary<Type, EventContract>(contractsByType));
    }

    public IReadOnlyCollection<Type> GetRegisteredTypes(EventDirection direction) =>
        byType.Values.Where(contract => contract.Direction == direction).Select(contract => contract.Type).ToArray();

    public void ValidateMessage(IEventMessage message, EventDirection direction)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!byType.TryGetValue(message.GetType(), out EventContract? contract) || contract.Direction != direction)
            throw new ArgumentException("Unregistered event or forbidden event direction.", nameof(message));
    }

    public EventEnvelope Encode(IEventMessage message, EventDirection direction)
    {
        ValidateMessage(message, direction);
        EventContract contract = byType[message.GetType()];
        lock (sync)
            return new()
            {
                EventId = contract.Id,
                CorrelationId = direction == EventDirection.ClientToServer ? Guid.NewGuid() : null,
                Payload = serializer.Serialize(message, contract.Type)
            };
    }

    public IEventMessage Decode(EventEnvelope envelope, EventDirection direction)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Version != EventEnvelope.CurrentVersion)
            throw new ArgumentException($"Unsupported event version {envelope.Version}.");
        if (string.IsNullOrWhiteSpace(envelope.EventId) ||
            !byId.TryGetValue(envelope.EventId, out EventContract? contract) || contract.Direction != direction)
            throw new ArgumentException("Unknown event identifier or forbidden event direction.");
        if (string.IsNullOrWhiteSpace(envelope.Payload))
            throw new ArgumentException("An event payload is required.");
        lock (sync)
            return deserializer.Deserialize(envelope.Payload, contract.Type) as IEventMessage
                ?? throw new ArgumentException("The event payload was null or had an invalid type.");
    }
}
