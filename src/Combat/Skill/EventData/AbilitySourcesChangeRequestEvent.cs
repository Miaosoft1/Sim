namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record AbilitySourceAssignment(AbilitySourceInstance Source, Unit? Holder, long? ExpectedRevision = null);
public sealed record AbilitySourceLevelChange(AbilitySourceInstance Source, Guid AbilityInstanceId, int Level);

public sealed record AbilitySourcesChangeRequestEvent : Event
{
    public required Unit Coordinator { get; init; }
    public Guid OperationId { get; init; } = Guid.NewGuid();
    public AtomicTransaction Transaction { get; init; } = new();
    public IAbilitySourceChangePreparation? Preparation { get; init; }
    public IReadOnlyList<AbilitySourceInstance> Materialize { get; init; } = [];
    public IReadOnlyList<AbilitySourceAssignment> Assignments { get; init; } = [];
    public IReadOnlyList<AbilitySourceLevelChange> LevelChanges { get; init; } = [];
    public IReadOnlyList<AtomicUnitAbilityChange> RegistrationChanges { get; init; } = [];
    public IReadOnlyList<Event> Completions { get; init; } = [];
}

public sealed record AbilitySourcesChangedEvent : Event
{
    public required Guid OperationId { get; init; }
    public required IReadOnlyList<AbilitySourceInstance> Sources { get; init; }
}
