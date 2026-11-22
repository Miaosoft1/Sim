namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record CommandTriggerObservationEvent : Event
{
    public required CommandEvent Command { get; init; }
    public required Unit Holder { get; init; }
}
