using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.Vector;
using Miaosoft.Sim.Combat.Skill.Targeting;

namespace Miaosoft.Sim.Combat.Skill;

public enum TriggerDistanceAnchor
{
    Source,
    Target,
    HitPoint
}

public sealed class DistanceTriggerCondition : ITriggerCondition
{
    public DistanceTriggerCondition(TriggerDistanceAnchor anchor, double maximum)
    {
        if (!Enum.IsDefined(anchor)) throw new ArgumentOutOfRangeException(nameof(anchor));
        if (!double.IsFinite(maximum) || maximum < 0)
            throw new ArgumentOutOfRangeException(nameof(maximum));
        Anchor = anchor;
        Maximum = maximum;
    }

    public TriggerDistanceAnchor Anchor { get; }
    public double Maximum { get; }

    public bool? Evaluate(TriggerConditionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        MapObject? anchorObject = Anchor switch
        {
            TriggerDistanceAnchor.Source => context.SourceObject,
            TriggerDistanceAnchor.Target => context.TargetObject,
            TriggerDistanceAnchor.HitPoint => null,
            _ => throw new InvalidOperationException("The distance anchor is invalid.")
        };
        MapRuntime? map = Anchor == TriggerDistanceAnchor.HitPoint ? context.HitMap : anchorObject?.MapRuntime;
        Vector2D? point = Anchor == TriggerDistanceAnchor.HitPoint ? context.HitPoint : anchorObject?.Position;
        if (map == null || !point.HasValue ||
            UnitSpatialRegistry.ObjectIn(context.Holder, map) is not { } holderObject)
            return false;
        return map.TargetingDistance(holderObject.Position, point.Value) <= Maximum;
    }
}
