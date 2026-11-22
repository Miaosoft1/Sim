namespace Miaosoft.Sim.Loadout.Content.Talents.Dto;

internal sealed class TalentTreeDto
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public int? InitialPoints { get; set; }
    public List<TalentBranchDto>? Branches { get; set; } = [];
    public List<TalentGrowthRuleDto>? GrowthRules { get; set; } = [];
}

internal sealed class TalentBranchDto
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public List<TalentNodeDto>? Nodes { get; set; } = [];
}

internal sealed class TalentNodeDto
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public int MaximumRank { get; set; } = 1;
    public int Tier { get; set; } = 1;
    public bool GrowAbilitiesWithRank { get; set; }
    public List<TalentAbilityDto>? Abilities { get; set; } = [];
    public TalentNodeRequirementDto? Requirement { get; set; }
}

internal sealed class TalentNodeRequirementDto
{
    public string? DependsOnNodeId { get; set; }
    public int RequiredNodeRank { get; set; } = 1;
    public int RequiredBranchPoints { get; set; }
    public int RequiredTreePoints { get; set; }
    public List<string>? ExclusiveNodeIds { get; set; } = [];
}

internal sealed class TalentGrowthRuleDto
{
    public string? Id { get; set; }
    public string? AbilityId { get; set; }
    public int ProgressPerReward { get; set; } = 1;
    public int PointsPerReward { get; set; } = 1;
    public long? MaximumRewards { get; set; }
}

internal sealed class TalentGrowthAbilityDto
{
    public string? TreeId { get; set; }
    public string? RuleId { get; set; }
    public string? Progress { get; set; } = "1";
}

internal sealed class TalentAbilityDto
{
    public string? Id { get; set; }
    public int Level { get; set; } = 1;
}
