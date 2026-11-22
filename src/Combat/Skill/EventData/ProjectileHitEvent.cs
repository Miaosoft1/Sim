using Miaosoft.Sim.Combat.Map.Vector;
using Miaosoft.Sim.Core;
using System.Text;

namespace Miaosoft.Sim.Combat.Skill.EventData
{
    /// <summary>
    /// Event emitted when a projectile hits its target or destination point.
    /// Triggers downstream payloads (damage, heal, buff/debuff install).
    /// </summary>
    public record class ProjectileHitEvent : Event
    {
        private readonly SemanticEventDeliveryState<ProjectileHitEvent> publication = new();
        internal SemanticEventDeliveryState<ProjectileHitEvent> Publication => publication;
        internal bool IsDelivery { get; init; }
        public required Guid CommandEventId { get; init; }
        public Guid ProjectileId { get; init; }
        public required string AbilityId { get; init; }
        public AbilityRuntimeSnapshot? AbilitySnapshot { get; init; }
        public required Unit Source { get; init; }
        public Unit? Target { get; init; }
        public EventReceiver? CompletionReceiver { get; init; }
        public Map.MapObject? SourceObject { get; init; }
        public Map.MapObject? TargetObject { get; init; }
        public Vector2D? HitPoint { get; init; }
        public Map.MapRuntime? HitMap { get; init; }
        public AbilityTriggerChain? TriggerChain
        {
            get => Causality as AbilityTriggerChain;
            set => Causality = value;
        }

        public override IEnumerable<IEvent> Dispatch()
        {
            if (GetType() == typeof(ProjectileHitEvent) &&
                Context.HasIndexedEvent<ProjectileHitEvent>() && !IsDelivery)
                throw new InvalidOperationException("A projectile hit must be published before it is delivered.");
            if (IsDelivery)
                return publication.DispatchDelivery(this);
            return base.Dispatch();
        }

        protected override bool PrintMembers(StringBuilder stringBuilder)
        {
            stringBuilder.Append($"{nameof(CommandEventId)} = {CommandEventId}, ");
            stringBuilder.Append($"{nameof(AbilityId)} = {AbilityId}, ");
            stringBuilder.Append($"{nameof(Source)} = {Source?.Name}, ");
            stringBuilder.Append($"{nameof(Target)} = {Target?.Name}, ");
            stringBuilder.Append($"{nameof(HitPoint)} = {HitPoint}, ");
            base.PrintMembers(stringBuilder);
            return true;
        }
    }
}
