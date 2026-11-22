namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record SignalTriggerObservationEvent : Event
{
    public required AbilitySignalEvent Signal { get; init; }
    public required Unit Holder { get; init; }
}
