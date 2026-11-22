using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.Vector;

namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record ProjectileEmittedEvent : Event
{
    public required ProjectileEvent Launch { get; init; }
    public MapRuntime? Map { get; init; }
    public required Vector2D Position { get; init; }
    public required Vector2D Destination { get; init; }
}
