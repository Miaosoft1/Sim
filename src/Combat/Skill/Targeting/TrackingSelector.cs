namespace Miaosoft.Sim.Combat.Skill.Targeting
{
    /// <summary>
    /// Tracking selector: requires an explicit target unit.
    /// Single-target by default; when <see cref="Group"/> is set, every registered
    /// unit whose <see cref="Unit.ArchetypeId"/> belongs to the group id is hit
    /// (tracking group strike).
    /// </summary>
    public sealed class TrackingSelector : ITargetSelector
    {
        public TargetingOrigin Origin { get; set; } = TargetingOrigin.Target;
        /// <summary>
        /// Optional group id: hit every unit of this archetype group instead of only the target.
        /// </summary>
        public string? Group { get; set; }

        /// <summary>
        /// Optional maximum cast range (source position to target position). Ignored when null.
        /// </summary>
        public double? Range { get; set; }

        /// <summary>
        /// Target filter (All, Enemy, Ally, Self). Defaults to All.
        /// </summary>
        public TargetFilter Filter { get; set; } = TargetFilter.All;

        public bool Validate(AbilityTargetingContext context)
        {
            context = context.FromOrigin(Origin);
            if (context.Target == null || context.Target.IsRetired)
            {
                return false;
            }

            if (!Filter.Matches(context.Source, context.Target))
            {
                return false;
            }
            if (context.SourceObject != null && (context.TargetObject is not { IsActive: true, IsInteractable: true } body ||
                context.RequireSelectable && !body.IsSelectable ||
                !ReferenceEquals(context.SourceObject.MapRuntime, body.MapRuntime)))
                return false;

            if (Range is double range)
            {
                if (!double.IsFinite(range) || range < 0 ||
                    context.SourceObject == null || context.TargetObject == null)
                    return false;
                var source = context.SourceObject;
                double distance = source.MapRuntime?.TargetingDistance(source.Position, context.TargetObject.Position)
                    ?? Map.MapSpace.Planar.Distance(source.Position, context.TargetObject.Position);
                return distance <= range;
            }

            return true;
        }

        public IReadOnlyList<Unit> ResolveTargets(AbilityTargetingContext context, UnitSpatialRegistry registry)
        {
            context = context.FromOrigin(Origin);
            Unit? target = context.Target;
            if (target == null)
            {
                return Array.Empty<Unit>();
            }

            if (string.IsNullOrEmpty(Group))
            {
                return Validate(context) ? new[] { target } : Array.Empty<Unit>();
            }

            return registry.Units
                .Where(m => m.CanParticipate && string.Equals(m.ArchetypeId, Group, StringComparison.Ordinal) &&
                    context.ObjectFor(m) is { IsActive: true, IsInteractable: true, IsSelectable: true } &&
                    Filter.Matches(context.Source, m))
                .ToList();
        }
    }
}
