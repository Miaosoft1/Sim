using Miaosoft.Sim.Protocol.Event;

namespace Miaosoft.Sim.Protocol.Model;

public sealed record ModuleVersion(string Id, int Version);
public enum ModuleUpdateKind { Full, Delta, Removed }

public sealed record ModuleSnapshot
{
    public required string Id { get; init; }
    public required int Version { get; init; }
    public required long StateVersion { get; init; }
    public required ModuleUpdateKind Kind { get; init; }
    public string Payload { get; init; } = "";
}

public sealed record ObservationFrame
{
    public required long StateVersion { get; init; }
    public required long PublicationSequence { get; init; }
    public required long ConnectionVersion { get; init; }
    public Guid? ControlledUnitId { get; init; }
    public required bool IsFull { get; init; }
    public required WorldSnapshot World { get; init; }
    public IReadOnlyList<ModuleSnapshot> Modules { get; init; } = [];
    public IReadOnlyList<EventEnvelope> Events { get; init; } = [];
}
