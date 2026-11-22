using Miaosoft.Sim.Combat.Map.Vector;

namespace Miaosoft.Sim.Combat.Skill.EventData;

public record class UnitMoveRequestEvent : Event
{
    public required Unit Source { get; init; }
    public required Vector2D Destination { get; init; }
    public long? ControlVersion { get; init; }
}

public record class UnitMoveAuthorizedEvent : UnitMoveRequestEvent;

public record class UnitMoveStopEvent : Event
{
    public required Unit Source { get; init; }
    public long? ControlVersion { get; init; }
}
