using Miaosoft.Sim.Combat.Skill.EventData;

namespace Miaosoft.Sim.Combat.Skill;

public interface IAbilitySourceChangePreparation
{
    /// <summary>Stage domain state during source execution; do not create/mount handlers, commit, or publish effects.</summary>
    AbilitySourceChangePlan Prepare(Context context, Unit coordinator, Guid operationId, AtomicTransaction transaction);
}

public sealed record AbilitySourceChangePlan
{
    public bool ShouldCommit { get; init; } = true;
    public IReadOnlyList<AbilitySourceInstance> Materialize { get; init; } = [];
    public IReadOnlyList<AbilitySourceAssignment> Assignments { get; init; } = [];
    public IReadOnlyList<AbilitySourceLevelChange> LevelChanges { get; init; } = [];
    public IReadOnlyList<AtomicUnitAbilityChange> RegistrationChanges { get; init; } = [];
    public IReadOnlyList<Event> Completions { get; init; } = [];
}
