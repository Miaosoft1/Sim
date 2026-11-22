namespace Miaosoft.Sim.Combat.Skill.EventData;

public record class AbilityLeaseActivatedEvent : Event, IPropagateHandlerPriority
{
    public required Unit Target { get; init; }
    public required IReadOnlyList<string> AbilityIds { get; init; }
    public required Guid InstanceId { get; init; }
    public required TimeSpan ExpiresAt { get; init; }
    public required EventCancellation LeaseCancellation { get; init; }
}
