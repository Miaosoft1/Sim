namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record AtomicUnitAbilityChange(
    Unit Target, IReadOnlyList<IHandler> Remove, IReadOnlyList<IHandler> Add);

public sealed record AtomicAbilityChangeEvent : Event
{
    public required Unit Target { get; init; }
    public required Guid OperationId { get; init; }
    public required AtomicTransaction Transaction { get; init; }
    public required IReadOnlyList<AtomicUnitAbilityChange> Changes { get; init; }
    public required Event Completion { get; init; }
    internal IReadOnlyList<AbilityBase> UnregisteredAbilities { get; init; } = [];
    internal IReadOnlyList<Event> AdditionalCompletions { get; init; } = [];
}
