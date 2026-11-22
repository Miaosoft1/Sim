using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Map.Vector;
using System.Text;

namespace Miaosoft.Sim.Combat.Skill.EventData
{
    public record class AbilityTriggerInvokeEvent : Event, IPropagateHandlerPriority, IRuntimeTargetEvent
    {
        public required Guid CommandEventId { get; set; }
        public required string AbilityId { get; set; }
        public Guid? TargetRuntimeInstanceId { get; init; }
        public long? TargetAuthorityVersion { get; init; }
        public required Event UpstreamEvent { get; set; }
        public bool IsDirectCommand { get; init; }
        public CommandEvent? DirectCommand
        {
            get
            {
                if (!IsDirectCommand) return null;
                if (UpstreamEvent is not CommandEvent command ||
                    !ReferenceEquals(Source, command.Source) ||
                    !ReferenceEquals(Receiver, command.Source.EventReceiver) ||
                    !ReferenceEquals(command.Receiver, command.Source.EventReceiver) ||
                    !string.Equals(command.AbilityId, AbilityId, StringComparison.Ordinal) ||
                    command.AbilityInstanceId.HasValue && command.AbilityInstanceId != TargetRuntimeInstanceId)
                    throw new InvalidOperationException("The direct command does not address this ability invocation.");
                return command;
            }
        }
        public Unit? Source { get; set; }
        public Unit? Target { get; set; }
        public EventReceiver? CompletionReceiver { get; init; }
        public Map.MapObject? SourceObject { get; init; }
        public Map.MapObject? TargetObject { get; init; }
        public Vector2D? HitPoint { get; set; }
        public AbilityTriggerChain TriggerChain
        {
            get => Causality as AbilityTriggerChain ??
                throw new InvalidOperationException("The ability invocation has no trigger chain.");
            set => Causality = value ?? throw new ArgumentNullException(nameof(value));
        }

        protected override bool PrintMembers(StringBuilder stringBuilder)
        {
            stringBuilder.Append($"{nameof(CommandEventId)} = {CommandEventId}, ");
            stringBuilder.Append($"{nameof(AbilityId)} = {AbilityId}, ");
            stringBuilder.Append($"Upstream = {UpstreamEvent.GetType().Name}, ");
            stringBuilder.Append($"{nameof(Source)} = {Source?.Name}, ");
            stringBuilder.Append($"{nameof(Target)} = {Target?.Name}, ");
            base.PrintMembers(stringBuilder);
            return true;
        }
    }
}
