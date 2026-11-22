namespace Miaosoft.Sim.Combat.Skill.Ability;

public sealed record AbilityRuntimeSnapshot
{
    private AbilityRuntimeSnapshot(
        string definitionId, Guid runtimeInstanceId, int level, Guid? grantSourceInstanceId, string? grantSourceCategory)
    {
        DefinitionId = definitionId;
        RuntimeInstanceId = runtimeInstanceId;
        Level = level;
        GrantSourceInstanceId = grantSourceInstanceId;
        GrantSourceCategory = grantSourceCategory;
    }

    public string DefinitionId { get; }
    public Guid RuntimeInstanceId { get; }
    public int Level { get; }
    public Guid? GrantSourceInstanceId { get; }
    public string? GrantSourceCategory { get; }

    public static AbilityRuntimeSnapshot Capture(AbilityBase ability)
    {
        ArgumentNullException.ThrowIfNull(ability);
        ArgumentException.ThrowIfNullOrWhiteSpace(ability.Id);
        return new AbilityRuntimeSnapshot(
            ability.Id, ability.RuntimeInstanceId, ability.Level, ability.Binding.SourceInstanceId, ability.Binding.SourceCategory);
    }
}
