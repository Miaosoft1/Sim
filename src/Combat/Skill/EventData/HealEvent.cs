using System.Text;
using Miaosoft.Sim.Combat.Skill.Ability;

namespace Miaosoft.Sim.Combat.Skill.EventData
{
    /// <summary>
    /// Event representing a heal payload applied to a target unit.
    /// </summary>
    public record class HealEvent : Event
    {
        private readonly SemanticEventDeliveryState<HealEvent> publication = new();
        internal SemanticEventDeliveryState<HealEvent> Publication => publication;
        internal bool IsDelivery { get; init; }
        public required Guid CommandEventId { get; init; }
        public required HealProfile HealAbility { get; init; }
        public string? HealAbilityId => HealAbility.Ability?.DefinitionId;
        public required Unit? Source { get; init; }
        public required Unit Target { get; init; }
        public StatusApplicationOrigin? ApplicationOrigin { get; init; }
        public EventReceiver? CompletionReceiver { get; init; }
        public Map.MapObject? SourceObject { get; init; }
        public Map.MapObject? TargetObject { get; init; }
        public int HealAmount { get; init; }
        public bool RestoreToMaximum { get; init; }
        public AbilityTriggerChain? TriggerChain
        {
            get => Causality as AbilityTriggerChain;
            set => Causality = value;
        }

        public override IEnumerable<IEvent> Dispatch()
        {
            if (Context.HasIndexedEvent<HealEvent>() && !IsDelivery)
                throw new InvalidOperationException("A heal request must be published before it is delivered.");
            return IsDelivery ? publication.DispatchDelivery(this) : base.Dispatch();
        }

        protected override bool PrintMembers(StringBuilder stringBuilder)
        {
            stringBuilder.Append($"{nameof(CommandEventId)} = {CommandEventId}, ");
            stringBuilder.Append($"{nameof(HealAbilityId)} = {HealAbilityId}, ");
            stringBuilder.Append($"{nameof(Source)} = {Source?.Name}, ");
            stringBuilder.Append($"{nameof(Target)} = {Target?.Name}, ");
            stringBuilder.Append($"{nameof(HealAmount)} = {HealAmount}, ");
            base.PrintMembers(stringBuilder);
            return true;
        }
    }
}
