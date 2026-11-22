namespace Miaosoft.Sim.Combat.Skill.EventData;

public record class ProjectileExpiredEvent : Event
{
    public Guid ProjectileId { get; init; }
}
