namespace Miaosoft.Sim.Combat.Skill.EventData;

public record class AbilitySimulationLeaseCanceledEvent : Event
{
    public required Unit Target { get; init; }
    public required Guid InstanceId { get; init; }
}

public record class AbilitySimulationLeasesRecheckEvent : Event
{
    public required Unit Target { get; init; }
}
