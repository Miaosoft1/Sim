using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.Vector;

namespace Miaosoft.Sim.Combat.Skill.EventData;

public record class ProjectileEndedEvent : Event
{
    public required Guid ProjectileId { get; init; }
    public required Vector2D Position { get; init; }
    public required MovementEndReason Reason { get; init; }
    public MapRuntime? Map { get; init; }
}
