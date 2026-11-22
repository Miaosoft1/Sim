using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Core;
using System.Text;

namespace Miaosoft.Sim.Combat.Skill.EventData
{
    public record class DeathEvent : Event, IPropagateHandlerPriority, Map.Visibility.IVisibilityInvalidatingEvent
    {
        private readonly SemanticEventDeliveryState<DeathEvent> publication = new();
        internal SemanticEventDeliveryState<DeathEvent> Publication => publication;
        internal bool IsDelivery { get; init; }

        public DeathEvent() => CancellationTokenSource = new CancellationTokenSource();

        public override int Priority { get; set; } = CombatEventPriority.Death;
        public Guid AttemptId { get; init; } = Guid.NewGuid();
        public required Guid CommandEventId { get; init; }
        public required DamageProfile DamageAbility { get; init; }
        public Unit? Killer { get; init; }
        public required Unit Victim { get; init; }
        public StatusApplicationOrigin? ApplicationOrigin { get; init; }
        public Map.MapObject? SourceObject { get; init; }
        public Map.MapObject? TargetObject { get; init; }
        public Guid? SettlementReplayId { get; init; }
        public required int LifePointReduce { get; init; }

        public override IEnumerable<IEvent> Dispatch()
        {
            if (Context.HasIndexedEvent<DeathEvent>() && !IsDelivery)
                throw new InvalidOperationException("A pending death must be published before it is delivered.");
            return IsDelivery ? publication.DispatchDelivery(this) : base.Dispatch();
        }

        protected override bool PrintMembers(StringBuilder stringBuilder)
        {
            stringBuilder.Append($"{nameof(CommandEventId)} = {CommandEventId}, ");
            stringBuilder.Append($"{nameof(DamageAbility)} = {DamageAbility.Name ?? DamageAbility.Id}, ");
                stringBuilder.Append($"{nameof(Victim)} = {Victim.Name}, ");
                stringBuilder.Append($"{nameof(Killer)} = {Killer?.Name}, ");
            stringBuilder.Append($"{nameof(LifePointReduce)} = {LifePointReduce}, ");
            base.PrintMembers(stringBuilder);
            return true;
        }
    }
}
