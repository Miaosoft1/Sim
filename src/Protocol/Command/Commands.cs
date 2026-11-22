using Miaosoft.Sim.Core;
using Miaosoft.Sim.Protocol.Model;

namespace Miaosoft.Sim.Protocol.Command;

public interface IUnitCommand : IEventMessage
{
    Guid UnitId { get; }
}

public abstract record SpatialCommand : IUnitCommand, IDiscreteEvent
{
    public required Guid UnitId { get; init; }
    public required Guid MapInstanceId { get; init; }
    public required long ControlVersion { get; init; }
}

public sealed record MoveCommand : SpatialCommand
{
    public required WorldPosition Destination { get; init; }
}

public sealed record StopMoveCommand : SpatialCommand;

public sealed record CastAbilityCommand : SpatialCommand
{
    public required Guid AbilityInstanceId { get; init; }
    public Guid? TargetUnitId { get; init; }
    public WorldPosition? TargetPoint { get; init; }
}
