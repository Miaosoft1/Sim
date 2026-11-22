using System.Collections.ObjectModel;

namespace Miaosoft.Sim.Loadout.Talents.Model;

public sealed class TalentBranchDefinition
{
    public TalentBranchDefinition(string id, string name, IEnumerable<TalentNodeDefinition> nodes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(nodes);
        TalentNodeDefinition[] definitions = nodes.ToArray();
        if (id != id.Trim() || definitions.Any(node => node == null))
            throw new ArgumentException("Talent branches need a normalized ID and non-null nodes.");
        Id = id;
        Name = name;
        Nodes = Array.AsReadOnly(definitions);
    }

    public string Id { get; }
    public string Name { get; }
    public IReadOnlyList<TalentNodeDefinition> Nodes { get; }
}

public sealed record TalentGrowthRuleDefinition
{
    public TalentGrowthRuleDefinition(
        string id, string abilityId, int progressPerReward, int pointsPerReward = 1, long? maximumRewards = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(abilityId);
        if (id != id.Trim() || abilityId != abilityId.Trim() ||
            progressPerReward < 1 || pointsPerReward < 1 || maximumRewards is <= 0)
            throw new ArgumentException("Talent growth rules require normalized IDs and positive thresholds/rewards.");
        Id = id;
        AbilityId = abilityId;
        ProgressPerReward = progressPerReward;
        PointsPerReward = pointsPerReward;
        MaximumRewards = maximumRewards;
    }

    public string Id { get; }
    public string AbilityId { get; }
    public int ProgressPerReward { get; }
    public int PointsPerReward { get; }
    public long? MaximumRewards { get; }
}

public sealed class TalentTreeDefinition
{
    public TalentTreeDefinition(
        string id, string name, int initialPoints, IEnumerable<TalentBranchDefinition> branches,
        IEnumerable<TalentGrowthRuleDefinition>? growthRules = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(branches);
        TalentBranchDefinition[] branchList = branches.ToArray();
        TalentGrowthRuleDefinition[] rules = (growthRules ?? []).ToArray();
        if (id != id.Trim() || initialPoints < 0 || branchList.Any(branch => branch == null) ||
            branchList.Select(branch => branch.Id).Distinct(StringComparer.Ordinal).Count() != branchList.Length ||
            rules.Any(rule => rule == null) ||
            rules.Select(rule => rule.Id).Distinct(StringComparer.Ordinal).Count() != rules.Length ||
            rules.Select(rule => rule.AbilityId).Distinct(StringComparer.Ordinal).Count() != rules.Length)
            throw new ArgumentException("Talent trees require distinct branches/rules, normalized IDs and nonnegative initial points.");
        TalentNodeDefinition[] nodeList = branchList.SelectMany(branch => branch.Nodes).ToArray();
        if (nodeList.Select(node => node.Id).Distinct(StringComparer.Ordinal).Count() != nodeList.Length)
            throw new ArgumentException("Talent node IDs must be distinct across the entire tree.", nameof(branches));
        Dictionary<string, TalentNodeDefinition> nodes = nodeList.ToDictionary(node => node.Id, StringComparer.Ordinal);
        foreach (TalentNodeDefinition node in nodeList)
        {
            TalentNodeRequirement requirement = node.Requirement;
            if (requirement.DependsOnNodeId is { } parent &&
                (!nodes.TryGetValue(parent, out TalentNodeDefinition? dependency) ||
                 parent == node.Id || requirement.RequiredNodeRank > dependency.MaximumRank) ||
                requirement.ExclusiveNodeIds.Any(exclusive => exclusive == node.Id || !nodes.ContainsKey(exclusive)))
                throw new ArgumentException($"Talent '{node.Id}' has an invalid dependency or exclusion.", nameof(branches));
            var path = new HashSet<string>(StringComparer.Ordinal) { node.Id };
            string? next = requirement.DependsOnNodeId;
            while (next != null)
            {
                if (!path.Add(next))
                    throw new ArgumentException($"Talent '{node.Id}' belongs to a dependency cycle.", nameof(branches));
                next = nodes.TryGetValue(next, out TalentNodeDefinition? ancestor)
                    ? ancestor.Requirement.DependsOnNodeId : null;
            }
        }
        Id = id;
        Name = name;
        InitialPoints = initialPoints;
        Branches = Array.AsReadOnly(branchList);
        Nodes = new ReadOnlyDictionary<string, TalentNodeDefinition>(nodes);
        GrowthRules = Array.AsReadOnly(rules);
    }

    public string Id { get; }
    public string Name { get; }
    public int InitialPoints { get; }
    public IReadOnlyList<TalentBranchDefinition> Branches { get; }
    public IReadOnlyDictionary<string, TalentNodeDefinition> Nodes { get; }
    public IReadOnlyList<TalentGrowthRuleDefinition> GrowthRules { get; }
}
