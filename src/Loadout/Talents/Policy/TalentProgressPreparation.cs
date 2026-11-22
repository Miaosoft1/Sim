using System.Collections.ObjectModel;
using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Talents.EventData;
using Miaosoft.Sim.Loadout.Talents.Model;

namespace Miaosoft.Sim.Loadout.Talents.Policy;

internal sealed class TalentProgressPreparation(TalentTreeRequestEvent request, UnitTalents talents)
    : IAbilitySourceChangePreparation
{
    public AbilitySourceChangePlan Prepare(Context context, Unit coordinator, Guid operationId, AtomicTransaction transaction)
    {
        TalentTreeRequestEvent ev = request;
        TalentTreeInstance tree = ev.Tree;
        if (!ReferenceEquals(ev.Context, context) || !ReferenceEquals(coordinator, tree.Owner) ||
            operationId != ev.OperationId || operationId == Guid.Empty)
            throw new InvalidOperationException("Talent progress requires its original request and owner.");
        TalentAccess.Validate(context, talents, coordinator, ev.Receiver);
        TalentAccess.ValidateTree(context, talents, tree);
        TalentChangeOperation operation = ev switch
        {
            TalentNodeAllocateRequestEvent => TalentChangeOperation.Allocate,
            TalentPointsAwardRequestEvent => TalentChangeOperation.AwardPoints,
            TalentGrowthObservationEvent => TalentChangeOperation.ObserveGrowth,
            _ => throw new InvalidOperationException("Unknown talent operation.")
        };
        AbilitySourceChangePlan Result(TalentChangeFailure failure) => new()
        {
            ShouldCommit = failure == TalentChangeFailure.None,
            Completions = [TalentAccess.Result(ev, operationId, coordinator, tree.Definition.Id, operation, failure)]
        };
        TalentTreeInstance.State state = transaction.Read(tree.Runtime);
        if (state.Grants.Length == 0) return Result(TalentChangeFailure.Inactive);
        if (state.AuthorityVersion != ev.ExpectedAuthorityVersion) return Result(TalentChangeFailure.StaleAuthority);
        if (ev.ExpectedRevision is { } revision && revision != tree.Revision) return Result(TalentChangeFailure.StaleRevision);
        if (ev is TalentPointsAwardRequestEvent award)
        {
            if (award.Points < 1) throw new ArgumentOutOfRangeException(nameof(award.Points));
            if (state.AvailablePoints > int.MaxValue - award.Points) return Result(TalentChangeFailure.PointLimitExceeded);
            transaction.Stage(tree.Runtime, state with { AvailablePoints = state.AvailablePoints + award.Points });
            return Result(TalentChangeFailure.None);
        }
        if (ev is TalentGrowthObservationEvent growth)
        {
            if (!ReferenceEquals(growth.UpstreamEvent.Context, context) ||
                !growth.UpstreamEvent.Execute.HasValue || string.IsNullOrWhiteSpace(growth.RuleId) || growth.Progress < 1)
                throw new InvalidOperationException("Growth must retain its real observed event.");
            TalentGrowthRuleDefinition rule = tree.Definition.GrowthRules.SingleOrDefault(rule => rule.Id == growth.RuleId)
                ?? throw new InvalidOperationException("The growth rule does not belong to this tree.");
            if (transaction.Read(growth.Processed)) return Result(TalentChangeFailure.AlreadyObserved);
            TalentGrowthProgress progress = state.Growth.GetValueOrDefault(rule.Id) ?? new(0, 0);
            if (rule.MaximumRewards is { } limit && progress.Rewards >= limit)
                return Result(TalentChangeFailure.GrowthLimitReached);
            long total = (long)progress.PendingProgress + growth.Progress;
            long rewards = total / rule.ProgressPerReward;
            if (rule.MaximumRewards is { } maximum)
                rewards = Math.Min(rewards, maximum - progress.Rewards);
            long points = checked(rewards * rule.PointsPerReward);
            if (points > int.MaxValue - state.AvailablePoints)
                return Result(TalentChangeFailure.PointLimitExceeded);
            var after = state.Growth.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            after[rule.Id] = new((int)(total % rule.ProgressPerReward), checked(progress.Rewards + rewards));
            transaction.Stage(tree.Runtime, state with
            {
                AvailablePoints = state.AvailablePoints + (int)points,
                Growth = new ReadOnlyDictionary<string, TalentGrowthProgress>(after)
            });
            transaction.Stage(growth.Processed, true);
            return Result(TalentChangeFailure.None);
        }
        var allocate = (TalentNodeAllocateRequestEvent)ev;
        if (!tree.Definition.Nodes.TryGetValue(allocate.NodeId, out TalentNodeDefinition? node))
            return Result(TalentChangeFailure.NodeNotFound);
        int rank = state.Ranks.GetValueOrDefault(node.Id);
        if (rank == node.MaximumRank) return Result(TalentChangeFailure.MaximumRank);
        if (state.AvailablePoints == 0) return Result(TalentChangeFailure.InsufficientPoints);
        TalentNodeRequirement requirement = node.Requirement;
        TalentBranchDefinition branch = tree.Definition.Branches.Single(branch => branch.Nodes.Contains(node));
        if (requirement.DependsOnNodeId is { } parent && state.Ranks.GetValueOrDefault(parent) < requirement.RequiredNodeRank ||
            branch.Nodes.Sum(node => (long)state.Ranks.GetValueOrDefault(node.Id)) < requirement.RequiredBranchPoints ||
            state.Ranks.Values.Sum(rank => (long)rank) < requirement.RequiredTreePoints ||
            tree.Definition.Nodes.Values.Any(other => state.Ranks.GetValueOrDefault(other.Id) > 0 &&
                (requirement.ExclusiveNodeIds.Contains(other.Id) || other.Requirement.ExclusiveNodeIds.Contains(node.Id))))
            return Result(TalentChangeFailure.RequirementsNotMet);
        var ranks = state.Ranks.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        ranks[node.Id] = rank + 1;
        transaction.Stage(tree.Runtime, state with
        {
            AvailablePoints = state.AvailablePoints - 1, Ranks = new ReadOnlyDictionary<string, int>(ranks)
        });
        AbilitySourceInstance source = tree.NodeSources[node.Id];
        return Result(TalentChangeFailure.None) with
        {
            Assignments = rank == 0 ? [new(source, coordinator, source.Revision)] : [],
            LevelChanges = rank > 0 && node.GrowAbilitiesWithRank
                ? source.Abilities.Select((ability, index) => new AbilitySourceLevelChange(
                    source, ability.RuntimeInstanceId, checked(node.Abilities[index].Level + rank))).ToArray()
                : []
        };
    }
}
