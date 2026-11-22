using System.Text;
using Miaosoft.Sim.Combat.Skill.Ability;

namespace Miaosoft.Sim.Combat.Skill.EventData
{
    /// <summary>
    /// Event emitted after a unit receives a life gain from healing.
    /// </summary>
    public record class HealLifeGainEvent : Event
    {
        public required Guid CommandEventId { get; set; }
        public required HealProfile HealAbility { get; init; }
        public string? HealAbilityId => HealAbility.Ability?.DefinitionId;
        public required Unit? Source { get; set; }
        public required Unit Target { get; set; }
        public StatusApplicationOrigin? ApplicationOrigin { get; init; }
        public Map.MapObject? SourceObject { get; init; }
        public Map.MapObject? TargetObject { get; init; }
        public int LifeGain { get; set; }
        public int TargetLifePoint { get; set; }

        protected override bool PrintMembers(StringBuilder stringBuilder)
        {
            stringBuilder.Append($"{nameof(CommandEventId)} = {CommandEventId}, ");
            stringBuilder.Append($"{nameof(HealAbilityId)} = {HealAbilityId}, ");
            stringBuilder.Append($"{nameof(Source)} = {Source?.Name}, ");
            stringBuilder.Append($"{nameof(Target)} = {Target?.Name}, ");
            stringBuilder.Append($"{nameof(LifeGain)} = {LifeGain}, ");
            stringBuilder.Append($"{nameof(TargetLifePoint)} = {TargetLifePoint}, ");
            base.PrintMembers(stringBuilder);
            return true;
        }
    }
}
