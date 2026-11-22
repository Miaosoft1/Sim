using Miaosoft.Sim.Combat.Map.Vector;
using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Combat.Map.EventData;

public record class MapMovementUpdatedEvent : Event
{
    public required MovementState Movement { get; init; }
    public required Vector2D From { get; init; }
    public required Vector2D Position { get; init; }
    public required TimeSpan At { get; init; }
    public MovementEndReason? EndReason { get; init; }
}
