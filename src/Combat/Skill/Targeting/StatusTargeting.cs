using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.EventData;

namespace Miaosoft.Sim.Combat.Skill.Targeting;

internal static class StatusTargeting
{
    public static CommandExecutionFailureReason Prepare(
        AbilityTriggerInvokeEvent ev, AbilitySourceBinding binding, ITargetSelector? selector,
        out IReadOnlyList<Unit> targets)
    {
        Unit source = ev.Source ?? throw new InvalidOperationException("A triggered status requires a source.");
        targets = Array.Empty<Unit>();
        if (!ReferenceEquals(source.EventReceiver.Context, ev.Context) ||
            !ev.Context.ContainsReceiver(source.EventReceiver))
            throw new InvalidOperationException("The status source must belong to the same context.");
        if (!source.CanParticipate || source.MapObject is { CanCast: false })
            return CommandExecutionFailureReason.SourceNotExist;
        if (binding.SourceInstanceId != null && !binding.IsAuthorized(source.Id))
            return CommandExecutionFailureReason.AbilityNotAvailable;

        AbilityTargetingContext context = AbilityTargetingContext.FromTrigger(ev);
        IReadOnlyList<Unit> selected;
        if (selector == null)
            selected = context.Target == null ? [] : [context.Target];
        else
        {
            if (source.Registry == null || !selector.Validate(context))
                return CommandExecutionFailureReason.SelectorRejected;
            selected = selector.ResolveTargets(context, source.Registry);
        }
        if (selected.Any(target => !ReferenceEquals(target.EventReceiver.Context, ev.Context) ||
            !ev.Context.ContainsReceiver(target.EventReceiver)))
            throw new InvalidOperationException("Triggered status targets must belong to the same context.");
        targets = selected.Where(target => target.CanParticipate).ToArray();
        return targets.Count == 0 ? CommandExecutionFailureReason.TargetNotExist : CommandExecutionFailureReason.None;
    }
}
