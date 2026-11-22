namespace Miaosoft.Sim.Combat.Skill.EventData;

public readonly record struct AbilityLeaseReference(string AbilityId, Guid InstanceId);

public record class AbilityLeaseInvalidatedEvent : Event, IPropagateHandlerPriority
{
    public required Unit Target { get; init; }
    public required IReadOnlyList<AbilityLeaseReference> Leases { get; init; }
    public IReadOnlyList<Guid> CompletedSimulationInstances { get; init; } = [];
}
