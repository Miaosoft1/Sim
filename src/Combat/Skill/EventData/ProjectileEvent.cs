using Miaosoft.Sim.Combat.Map.Vector;
using System.Text;

namespace Miaosoft.Sim.Combat.Skill.EventData
{
    /// <summary>
    /// Carrier event representing a projectile/bullet launched by an emitter.
    /// Can have a trajectory (speed > 0) or be pathless (instantaneous).
    /// </summary>
    public record class ProjectileEvent : Event, Map.Visibility.IVisibilityInvalidatingEvent
    {
        public required Guid CommandEventId { get; set; }
        public Guid ProjectileId { get; init; } = Guid.NewGuid();
        public required string AbilityId { get; set; }
        public AbilityRuntimeSnapshot? AbilitySnapshot { get; init; }
        public required Unit Source { get; set; }
        public Unit? Target { get; set; }
        public EventReceiver? CompletionReceiver { get; init; }
        public Map.MapObject? SourceObject { get; init; }
        public Map.MapObject? TargetObject { get; init; }
        public Vector2D? TargetPoint { get; set; }
        public double? Distance { get; set; }
        public double? ProjectileSpeed { get; set; }
        public double ProjectileTravelDistance { get; set; }
        public AbilityTriggerChain? TriggerChain
        {
            get => Causality as AbilityTriggerChain;
            set => Causality = value;
        }
        public Map.MovementMode Mode { get; init; } = Map.MovementMode.Linear;
        public ProjectileHitResponse HitResponse { get; init; } = ProjectileHitResponse.Stop;
        public ProjectileExplosionTrigger ExplosionTrigger { get; init; }
        public int? MaxTargets { get; init; }
        public IReadOnlyList<Unit>? SelectedTargets { get; init; }

        protected override bool PrintMembers(StringBuilder stringBuilder)
        {
            stringBuilder.Append($"{nameof(CommandEventId)} = {CommandEventId}, ");
            stringBuilder.Append($"{nameof(AbilityId)} = {AbilityId}, ");
            stringBuilder.Append($"{nameof(Source)} = {Source?.Name}, ");
            stringBuilder.Append($"{nameof(Target)} = {Target?.Name}, ");
            stringBuilder.Append($"{nameof(Distance)} = {Distance}, ");
            stringBuilder.Append($"{nameof(ProjectileSpeed)} = {ProjectileSpeed}, ");
            stringBuilder.Append($"{nameof(ProjectileTravelDistance)} = {ProjectileTravelDistance}, ");
            stringBuilder.Append($"{nameof(TimeSpan)} = {TimeSpan}, ");
            base.PrintMembers(stringBuilder);
            return true;
        }
    }
}
