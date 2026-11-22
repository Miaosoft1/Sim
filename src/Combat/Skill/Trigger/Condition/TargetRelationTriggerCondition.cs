namespace Miaosoft.Sim.Combat.Skill;

public sealed class TargetRelationTriggerCondition : ITriggerCondition
{
    public TargetRelationTriggerCondition(TriggerSourceScope relation)
    {
        if (!Enum.IsDefined(relation)) throw new ArgumentOutOfRangeException(nameof(relation));
        Relation = relation;
    }

    public TriggerSourceScope Relation { get; }

    public bool? Evaluate(TriggerConditionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Unit? target = context.EventTarget;
        return target != null && Relation switch
        {
            TriggerSourceScope.Self => ReferenceEquals(target, context.Holder),
            TriggerSourceScope.Ally => target.TeamId == context.Holder.TeamId,
            TriggerSourceScope.Enemy => target.TeamId != context.Holder.TeamId,
            TriggerSourceScope.Any => true,
            _ => throw new InvalidOperationException("The target relation is invalid.")
        };
    }
}
