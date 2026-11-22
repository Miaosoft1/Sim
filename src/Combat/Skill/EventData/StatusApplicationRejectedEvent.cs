namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record StatusApplicationRejectedEvent : Event
{
    public required Guid CommandEventId { get; init; }
    public required Unit? Source { get; init; }
    public required Unit Target { get; init; }
    public required string StatusId { get; init; }
    public required string PeriodicEffectId { get; init; }
    public required PeriodicEffectRejectionReason Reason { get; init; }
}
