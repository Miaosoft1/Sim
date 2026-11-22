using Miaosoft.Sim.Combat.Map.Vector;

namespace Miaosoft.Sim.Combat.Skill.Targeting
{
    /// <summary>
    /// Fixed-direction line (skillshot) selector: no player target, only a direction
    /// resolved from the command target point. Hits units along the segment from the
    /// source position towards the direction, up to <see cref="Range"/>.
    /// </summary>
    public sealed class LineSelector : ITargetSelector
    {
        public TargetingOrigin Origin { get; set; } = TargetingOrigin.Target;
        /// <summary>
        /// Line length.
        /// </summary>
        public double Range { get; set; }

        /// <summary>
        /// Full width of the line (perpendicular half-extent is Width / 2).
        /// </summary>
        public double Width { get; set; }

        /// <summary>
        /// Target filter (All, Enemy, Ally, Self). Defaults to All.
        /// </summary>
        public TargetFilter Filter { get; set; } = TargetFilter.All;

        /// <summary>
        /// When true, projectile collides with the first target unit along its path
        /// and stops, blocking subsequent units behind it.
        /// </summary>
        public bool StopAtFirstTarget { get; set; } = false;

        /// <summary>
        /// Optional maximum number of unit collisions along the path before projectile terminates.
        /// </summary>
        public int? MaxTargets { get; set; }

        public bool Validate(AbilityTargetingContext context)
        {
            EnsureSupportedOrigin();
            context = context.FromOrigin(Origin);
            if (!double.IsFinite(Range) || Range < 0 || !double.IsFinite(Width) || Width < 0)
                return false;
            if (context.SourceObject?.MapRuntime?.TargetingMetric is { } metric &&
                (context.TargetPoint is not { } point || !metric.Contains(point)))
                return false;
            return ResolveDirection(context) != null;
        }

        public IReadOnlyList<Unit> ResolveTargets(AbilityTargetingContext context, UnitSpatialRegistry registry)
        {
            EnsureSupportedOrigin();
            context = context.FromOrigin(Origin);
            Vector2D? direction = ResolveDirection(context);
            if (direction == null || context.SourceObject == null)
            {
                return Array.Empty<Unit>();
            }

            Vector2D sourcePos = context.SourceObject.Position;
            Unit? except = (Filter == TargetFilter.Enemy || Filter == TargetFilter.All) ? context.Source : null;
            var map = context.SourceObject.MapRuntime;
            if (map?.TargetingMetric is { } metric &&
                (context.TargetPoint is not { } point || !metric.Contains(point)))
                return Array.Empty<Unit>();
            double length = Range;
            double halfWidth = Width / 2d;
            if (map?.TargetingMetric is Map.IMapLineTargetingMetric lineMetric)
            {
                length = lineMetric.ToWorldLineLength(direction.Value, Range);
                halfWidth = lineMetric.ToWorldLineWidth(halfWidth);
            }
            var candidates = registry.QueryInLine(sourcePos, direction.Value, length, halfWidth, except, map)
                .Where(m => map?.TargetingMetric == null ||
                    map.TargetingDistance(sourcePos, context.ObjectFor(m)!.Position) <= Range)
                .Where(m => Filter.Matches(context.Source, m))
                .OrderBy(m => map?.TargetingMetric == null
                    ? context.ObjectFor(m)?.Position.DistanceTo(sourcePos) ?? double.MaxValue
                    : map.TargetingDistance(sourcePos, context.ObjectFor(m)!.Position));

            if (StopAtFirstTarget)
            {
                Unit? first = candidates.FirstOrDefault();
                return first != null ? new[] { first } : Array.Empty<Unit>();
            }

            if (MaxTargets.HasValue && MaxTargets.Value > 0)
            {
                return candidates.Take(MaxTargets.Value).ToList();
            }

            return candidates.ToList();
        }

        private void EnsureSupportedOrigin()
        {
            if (Origin is TargetingOrigin.EventSource or TargetingOrigin.EventTarget)
                throw new InvalidOperationException("Line selectors do not support event-role origins.");
        }

        private static Vector2D? ResolveDirection(AbilityTargetingContext context)
        {
            Vector2D? origin = context.SourceObject?.Position ?? context.Source.MapObject?.Position;
            Vector2D? targetPoint = context.TargetPoint;
            if (origin == null || targetPoint == null)
            {
                return null;
            }

            Vector2D direction = new Vector2D(targetPoint.Value.X - origin.Value.X, targetPoint.Value.Y - origin.Value.Y);
            return direction.DistanceTo(new Vector2D(0, 0)) > 0 ? direction : null;
        }
    }
}
