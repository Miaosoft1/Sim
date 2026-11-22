using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.Vector;

namespace Miaosoft.Sim.Combat.Skill.EventData;

public record class UnitGridStepEvent : Event
{
    public required Guid MovementId { get; init; }
}

public record class UnitGridMovementUpdatedEvent : Event
{
    public required Unit Source { get; init; }
    public required MapObject Body { get; init; }
    public required Guid MovementId { get; init; }
    public required Vector2D From { get; init; }
    public required Vector2D Position { get; init; }
    public required Vector2D Destination { get; init; }
    public required double Speed { get; init; }
    public required TimeSpan At { get; init; }
    public MovementEndReason? EndReason { get; init; }
}
