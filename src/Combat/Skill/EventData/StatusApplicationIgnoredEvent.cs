namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record StatusApplicationIgnoredEvent : Event
{
    public required Guid CommandEventId { get; init; }
    public required Unit? Source { get; init; }
    public required Unit Target { get; init; }
    public required string StatusId { get; init; }
    public required int AttemptedLevel { get; init; }
    public required int ExistingLevel { get; init; }
}
