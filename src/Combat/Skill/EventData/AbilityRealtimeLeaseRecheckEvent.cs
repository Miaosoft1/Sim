namespace Miaosoft.Sim.Combat.Skill.EventData;

public record class AbilityRealtimeLeaseRecheckEvent : Event
{
    public required Unit Target { get; init; }
}
