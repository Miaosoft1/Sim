using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Loadout.Content.Talents.Dto;
using Miaosoft.Sim.Loadout.Talents.Model;

namespace Miaosoft.Sim.Loadout.Content.Talents.Parser;

internal sealed class TalentTreeParser
{
    public TalentTreeDefinition Compile(TalentTreeDto dto)
    {
        if (dto.Id == null || dto.Name == null || dto.Branches == null || dto.GrowthRules == null ||
            dto.Branches.Any(branch => branch == null) || dto.GrowthRules.Any(rule => rule == null))
            throw new InvalidDataException("A talent tree requires ID, name and non-null branch/growth collections.");
        return new(dto.Id, dto.Name, dto.InitialPoints ?? 0, dto.Branches.Select(CompileBranch),
            dto.GrowthRules.Select(rule => new TalentGrowthRuleDefinition(
                rule.Id ?? throw new InvalidDataException("A growth rule requires an ID."),
                rule.AbilityId ?? throw new InvalidDataException("A growth rule requires an ability ID."),
                rule.ProgressPerReward, rule.PointsPerReward, rule.MaximumRewards)));
    }

    private static TalentBranchDefinition CompileBranch(TalentBranchDto branch)
    {
        if (branch.Id == null || branch.Name == null || branch.Nodes == null || branch.Nodes.Any(node => node == null))
            throw new InvalidDataException("A talent branch requires ID, name and non-null nodes.");
        return new(branch.Id, branch.Name, branch.Nodes.Select(CompileNode));
    }

    private static TalentNodeDefinition CompileNode(TalentNodeDto node)
    {
        if (node.Id == null || node.Name == null || node.Abilities == null || node.Abilities.Any(ability => ability == null))
            throw new InvalidDataException("A talent node requires ID, name and non-null abilities.");
        TalentNodeRequirementDto? requirement = node.Requirement;
        if (requirement != null && requirement.ExclusiveNodeIds == null)
            throw new InvalidDataException("Talent exclusions must be a non-null collection.");
        return new(node.Id, node.Name, node.MaximumRank, node.Abilities.Select(ability => new AbilitySourceMember(
            ability.Id ?? throw new InvalidDataException("A talent ability requires an ID."), ability.Level)),
            node.GrowAbilitiesWithRank, node.Tier, requirement == null ? null : new TalentNodeRequirement(
                requirement.DependsOnNodeId, requirement.RequiredNodeRank,
                requirement.RequiredBranchPoints, requirement.RequiredTreePoints, requirement.ExclusiveNodeIds));
    }
}
