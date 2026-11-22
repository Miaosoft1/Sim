using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Combat.Skill.Expression;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Talents.EventData;
using Miaosoft.Sim.Loadout.Talents.Handler;

namespace Miaosoft.Sim.Loadout.Talents.Ability;

public sealed class TalentGrowthAbility : AbilityBase
{
    public required string TreeId { get; init; }
    public required string RuleId { get; init; }
    public required CompiledExpression<TriggerConditionContext> Progress { get; init; }

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<Event> OnTriggerInvoke(AbilityTriggerInvokeEvent ev)
    {
        if (!string.Equals(ev.AbilityId, Id, StringComparison.Ordinal)) yield break;
        if (ev.Source == null || ev.Receiver != ev.Source.EventReceiver ||
            ev.TargetRuntimeInstanceId != RuntimeInstanceId || ev.TargetAuthorityVersion == null)
            throw new InvalidOperationException("Talent growth requires a managed, instance-bound trigger invocation.");
        TalentDirectoryHandler directory = ev.Source.EventReceiver.GetHandlers<TalentDirectoryHandler>().Single();
        if (!directory.Talents.Trees.TryGetValue(TreeId, out var tree) ||
            !ReferenceEquals(tree.GrowthSource, Binding.Source) ||
            tree.Definition.GrowthRules.SingleOrDefault(rule => rule.Id == RuleId)?.AbilityId != Id)
            throw new InvalidOperationException("The growth ability is not owned by its declared tree and rule.");
        if (!tree.IsAuthorized || !Binding.IsAuthorized(ev.Source.Id) ||
            ev.TargetAuthorityVersion != Binding.AuthorityVersion)
        {
            yield return ev.Context.Factory.NewEvent<TalentChangeResultEvent>() with
            {
                OperationId = ev.CommandEventId, Owner = ev.Source, TreeId = TreeId,
                Operation = TalentChangeOperation.ObserveGrowth, Failure = TalentChangeFailure.StaleAuthority,
                Receiver = ev.Receiver, Priority = ev.Priority
            };
            yield break;
        }
        double? progress = Progress.EvaluateNumber(new TriggerConditionContext(
            ev.UpstreamEvent, ev.Source, AbilityRuntimeSnapshot.Capture(this)));
        if (progress is not { } amount || !double.IsFinite(amount) ||
            amount < 1 || amount > int.MaxValue || amount != Math.Truncate(amount))
            throw new ExpressionEvaluationException("Talent growth progress must be a positive, representable whole number.");
        yield return ev.Context.Factory.NewEvent<TalentGrowthObservationEvent>() with
        {
            Tree = tree, ExpectedAuthorityVersion = tree.AuthorityVersion, RuleId = RuleId,
            UpstreamEvent = ev.UpstreamEvent, Receiver = ev.Receiver, Priority = ev.Priority,
            Progress = checked((int)amount),
            CancellationTokenSource = ev.CancellationTokenSource
        };
    }
}
