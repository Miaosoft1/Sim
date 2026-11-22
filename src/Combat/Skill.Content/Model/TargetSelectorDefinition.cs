using Miaosoft.Sim.Combat.Skill.Targeting;

namespace Miaosoft.Sim.Combat.Skill.Content.Model;

internal sealed record TargetSelectorDefinition(
    string Type, TargetingOrigin Origin, TargetFilter Filter, string? Group,
    double? Range, double? Width, double? Radius, double? CastRange,
    bool StopAtFirstTarget, int? MaxTargets)
{
    public ITargetSelector Create() => Type switch
    {
        "tracking" => new TrackingSelector { Origin = Origin, Filter = Filter, Group = Group, Range = Range },
        "circle" => new CircleSelector
        {
            Origin = Origin, Filter = Filter,
            Radius = Radius ?? throw new InvalidOperationException("The compiled circle has no radius."),
            CastRange = CastRange
        },
        "line" => new LineSelector
        {
            Origin = Origin, Filter = Filter,
            Range = Range ?? throw new InvalidOperationException("The compiled line has no range."),
            Width = Width ?? throw new InvalidOperationException("The compiled line has no width."),
            StopAtFirstTarget = StopAtFirstTarget, MaxTargets = MaxTargets
        },
        _ => throw new InvalidOperationException("The compiled selector type is invalid.")
    };
}
