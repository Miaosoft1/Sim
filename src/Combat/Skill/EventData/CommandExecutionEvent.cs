using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.EventData;
using System.Text;

namespace Miaosoft.Sim.Combat.Skill.EventData
{
    public record class CommandExecutionEvent : Event
    {
        public required CommandEvent Command { get; set; }
        public string? HandlerId { get; set; }
        public CommandExecutionStatus Status { get; set; }
        public CommandExecutionFailureReason FailureReason { get; set; } = CommandExecutionFailureReason.None;
        public TimeSpan? NextAvailableTime { get; set; }

        protected override bool PrintMembers(StringBuilder stringBuilder)
        {
            stringBuilder.Append($"{nameof(Command)} = {Command.CommandInstanceId}, ");
            stringBuilder.Append($"{nameof(HandlerId)} = {HandlerId}, ");
            stringBuilder.Append($"{nameof(Status)} = {Status}, ");
            stringBuilder.Append($"{nameof(FailureReason)} = {FailureReason}, ");
            stringBuilder.Append($"{nameof(NextAvailableTime)} = {NextAvailableTime}, ");
            base.PrintMembers(stringBuilder);
            return true;
        }
    }
}
