namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record DamageLifeReduceTriggerObservationEvent : Event
{
    public required DamageLifeReduceEvent Reduction { get; init; }
    public required Unit Holder { get; init; }
}
