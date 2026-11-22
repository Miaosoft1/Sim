using Miaosoft.Sim.Core;
using Miaosoft.Sim.Protocol.Model;

namespace Miaosoft.Sim.Protocol.Event;

public interface ITransientEventMessage : IEventMessage { }

public enum ProjectileFactKind { Emitted = 1, Hit, Explosion }

public sealed record ProjectileFact : ITransientEventMessage
{
    public required long EventSequence { get; init; }
    public required TimeSpan Time { get; init; }
    public required ProjectileFactKind Kind { get; init; }
    public required Guid ProjectileId { get; init; }
    public required Guid MapInstanceId { get; init; }
    public required Guid SourceUnitId { get; init; }
    public Guid? TargetUnitId { get; init; }
    public required string AbilityId { get; init; }
    public Guid? AbilityInstanceId { get; init; }
    public required WorldPosition Position { get; init; }
    public WorldPosition? Destination { get; init; }
}
