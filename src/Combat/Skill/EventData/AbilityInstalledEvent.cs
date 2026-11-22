using Miaosoft.Sim.Combat.Skill;
using System.Text;

namespace Miaosoft.Sim.Combat.Skill.EventData
{
    /// <summary>
    /// Completion marker for the install flow; no downstream handler is required.
    /// </summary>
    public record class AbilityInstalledEvent : Event, Map.Visibility.IVisibilityInvalidatingEvent
    {
        public required Guid CommandEventId { get; set; }
        public required Unit? Source { get; set; }
        public required Unit Target { get; set; }
        public required IReadOnlyList<string> AbilityIds { get; set; }
        public bool ProjectionAlreadyApplied { get; init; }

        protected override bool PrintMembers(StringBuilder stringBuilder)
        {
            stringBuilder.Append($"{nameof(CommandEventId)} = {CommandEventId}, ");
            stringBuilder.Append($"{nameof(Source)} = {Source?.Name}, ");
            stringBuilder.Append($"{nameof(Target)} = {Target?.Name}, ");
            stringBuilder.Append($"{nameof(AbilityIds)} = [{string.Join(",", AbilityIds)}], ");
            base.PrintMembers(stringBuilder);
            return true;
        }
    }
}
