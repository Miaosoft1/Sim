namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record UnitExperienceChangedEvent : Event, IPropagateHandlerPriority
{
    public required Unit Unit { get; init; }
    public required int PreviousExperience { get; init; }
    public required int Experience { get; init; }
    public required int ExperienceMax { get; init; }
}
