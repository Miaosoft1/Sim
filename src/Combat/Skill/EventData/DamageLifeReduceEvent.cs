using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Core;
using System.Text;

namespace Miaosoft.Sim.Combat.Skill.EventData
{
    public record class DamageLifeReduceEvent : Event
    {
        private readonly SemanticEventDeliveryState<DamageLifeReduceEvent> publication = new();
        internal SemanticEventDeliveryState<DamageLifeReduceEvent> Publication => publication;
        internal bool IsDelivery { get; init; }
        public required Guid CommandEventId { get; init; }
        public required DamageProfile DamageAbility { get; init; }
        public required Damage Damage { get; init; }
        public Unit? Source { get; init; }
        public Unit? Target { get; init; }
        public StatusApplicationOrigin? ApplicationOrigin { get; init; }
        public Map.MapObject? SourceObject { get; init; }
        public Map.MapObject? TargetObject { get; init; }
        public int LifePointReduce { get; init; }
        public int MitigatedDamage { get; init; }
        public int? TargetLifePoint { get; init; }
        public AbilityTriggerChain? TriggerChain
        {
            get => Causality as AbilityTriggerChain;
            set => Causality = value;
        }

        public override IEnumerable<IEvent> Dispatch()
        {
            if (Context.HasIndexedEvent<DamageLifeReduceEvent>() && !IsDelivery)
                throw new InvalidOperationException("A life reduction must be published before it is delivered.");
            return IsDelivery ? publication.DispatchDelivery(this) : base.Dispatch();
        }

        protected override bool PrintMembers(StringBuilder stringBuilder)
        {
            stringBuilder.Append($"{nameof(CommandEventId)} = {CommandEventId}, ");
            foreach (string type in Damage.StoredKeys)
            {
                int scaledDamage = Damage.GetValueOrDefault(type);
                decimal actualDamage = scaledDamage / (decimal)Damage.ZOOM;
                stringBuilder.Append($"{type}Scaled = {scaledDamage}, ");
                stringBuilder.Append($"{type} = {actualDamage:0.##}, ");
            }
            stringBuilder.Append($"Skill = {DamageAbility.Name ?? DamageAbility.Id}, ");
            stringBuilder.Append($"{nameof(Source)} = {Source?.Name}, ");
            stringBuilder.Append($"{nameof(Target)} = {Target?.Name}, ");
            stringBuilder.Append($"{nameof(LifePointReduce)} = {LifePointReduce}, ");
            stringBuilder.Append($"{nameof(TargetLifePoint)} = {TargetLifePoint}, ");
            base.PrintMembers(stringBuilder);
            return true;
        }
    }
}
