using Miaosoft.Sim.Combat.Map.Vector;

namespace Miaosoft.Sim.Combat.Skill.Targeting
{
    /// <summary>
    /// Circular area selector: hits every unit within <see cref="Radius"/> of the
    /// command target point (a target unit's position when present, otherwise the
    /// explicit point).
    /// </summary>
    public sealed class CircleSelector : ITargetSelector
    {
        public TargetingOrigin Origin { get; set; } = TargetingOrigin.Target;
        public double Radius { get; set; }

        /// <summary>
        /// Optional maximum cast range (source position to target point). Ignored when null.
        /// </summary>
        public double? CastRange { get; set; }

        /// <summary>
        /// Target filter (All, Enemy, Ally, Self). Defaults to All.
        /// </summary>
        public TargetFilter Filter { get; set; } = TargetFilter.All;

        public bool Validate(AbilityTargetingContext context)
        {
            EnsureSupportedOrigin();
            context = context.FromOrigin(Origin);
            Vector2D? targetPoint = context.TargetPoint;
            if (targetPoint == null || !double.IsFinite(targetPoint.Value.X) || !double.IsFinite(targetPoint.Value.Y))
            {
                return false;
            }

            if (context.SourceObject?.MapRuntime?.TargetingMetric is { } metric && !metric.Contains(targetPoint.Value))
                return false;
            if (CastRange is double range)
            {
                if (!double.IsFinite(range) || range < 0 || context.SourceObject == null)
                    return false;
                var source = context.SourceObject;
                double distance = source.MapRuntime?.TargetingDistance(source.Position, targetPoint.Value)
                    ?? Map.MapSpace.Planar.Distance(source.Position, targetPoint.Value);
                return distance <= range;
            }

            return true;
        }

        public IReadOnlyList<Unit> ResolveTargets(AbilityTargetingContext context, UnitSpatialRegistry registry)
        {
            EnsureSupportedOrigin();
            context = context.FromOrigin(Origin);
            Vector2D? targetPoint = context.TargetPoint;
            if (targetPoint == null)
            {
                return Array.Empty<Unit>();
            }

            if (context.SourceObject?.MapRuntime?.TargetingMetric is { } metric && !metric.Contains(targetPoint.Value))
                return Array.Empty<Unit>();
            Unit? except = (Filter == TargetFilter.Enemy || Filter == TargetFilter.All) ? context.Source : null;
            return registry.QueryInRadius(targetPoint.Value, Radius, except, context.SourceObject?.MapRuntime)
                .Where(m => Filter.Matches(context.Source, m))
                .ToList();
        }

        private void EnsureSupportedOrigin()
        {
            if (Origin is TargetingOrigin.EventSource or TargetingOrigin.EventTarget)
                throw new InvalidOperationException("Circle selectors do not support event-role origins.");
        }
    }
}
