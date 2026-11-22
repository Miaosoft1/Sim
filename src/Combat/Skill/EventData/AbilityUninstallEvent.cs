using Miaosoft.Sim.Combat.Skill;
using System.Text;

namespace Miaosoft.Sim.Combat.Skill.EventData
{
    /// <summary>
    /// Executes removal of the given abilities on the target unit.
    /// </summary>
    public record class AbilityUninstallEvent : Event, IPropagateHandlerPriority
    {
        public required Guid CommandEventId { get; set; }
        public required Unit? Source { get; set; }
        public required Unit Target { get; set; }
        public required IReadOnlyList<string> AbilityIds { get; set; }
        public Guid? LeaseInstanceId { get; set; }

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
