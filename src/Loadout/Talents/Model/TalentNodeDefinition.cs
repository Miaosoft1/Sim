using Miaosoft.Sim.Combat.Skill.Ability;

namespace Miaosoft.Sim.Loadout.Talents.Model;

public sealed class TalentNodeRequirement
{
    public TalentNodeRequirement(
        string? dependsOnNodeId = null, int requiredNodeRank = 1,
        int requiredBranchPoints = 0, int requiredTreePoints = 0,
        IEnumerable<string>? exclusiveNodeIds = null)
    {
        if (dependsOnNodeId != null &&
            (string.IsNullOrWhiteSpace(dependsOnNodeId) || dependsOnNodeId != dependsOnNodeId.Trim()) ||
            requiredNodeRank < 1 || requiredBranchPoints < 0 || requiredTreePoints < 0)
            throw new ArgumentException("Talent requirements need normalized IDs and nonnegative point thresholds.");
        string[] exclusive = (exclusiveNodeIds ?? []).ToArray();
        if (exclusive.Any(id => string.IsNullOrWhiteSpace(id) || id != id.Trim()) ||
            exclusive.Distinct(StringComparer.Ordinal).Count() != exclusive.Length)
            throw new ArgumentException("Exclusive talent IDs must be normalized and distinct.", nameof(exclusiveNodeIds));
        DependsOnNodeId = dependsOnNodeId;
        RequiredNodeRank = requiredNodeRank;
        RequiredBranchPoints = requiredBranchPoints;
        RequiredTreePoints = requiredTreePoints;
        ExclusiveNodeIds = Array.AsReadOnly(exclusive);
    }

    public string? DependsOnNodeId { get; }
    public int RequiredNodeRank { get; }
    public int RequiredBranchPoints { get; }
    public int RequiredTreePoints { get; }
    public IReadOnlyList<string> ExclusiveNodeIds { get; }
}

public sealed class TalentNodeDefinition
{
    public TalentNodeDefinition(
        string id, string name, int maximumRank, IEnumerable<AbilitySourceMember> abilities,
        bool growAbilitiesWithRank = false, int tier = 1, TalentNodeRequirement? requirement = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(abilities);
        if (id != id.Trim() || maximumRank < 1 || tier < 1)
            throw new ArgumentException("Talent nodes require a normalized ID and positive rank/tier.");
        AbilitySourceMember[] members = abilities.ToArray();
        if (members.Any(member => member == null || string.IsNullOrWhiteSpace(member.AbilityId) ||
            member.AbilityId != member.AbilityId.Trim() || member.Level < 1 ||
            growAbilitiesWithRank && member.Level > int.MaxValue - (maximumRank - 1)))
            throw new ArgumentException("Talent abilities require valid IDs and representable positive levels.", nameof(abilities));
        Id = id;
        Name = name;
        MaximumRank = maximumRank;
        Abilities = Array.AsReadOnly(members);
        GrowAbilitiesWithRank = growAbilitiesWithRank;
        Tier = tier;
        Requirement = requirement ?? new();
    }

    public string Id { get; }
    public string Name { get; }
    public int MaximumRank { get; }
    public int Tier { get; }
    public bool GrowAbilitiesWithRank { get; }
    public IReadOnlyList<AbilitySourceMember> Abilities { get; }
    public TalentNodeRequirement Requirement { get; }
}
