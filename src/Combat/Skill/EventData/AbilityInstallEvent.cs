using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.Ability;
using System.Text;

namespace Miaosoft.Sim.Combat.Skill.EventData
{
    /// <summary>
    /// Carries materialized handlers to be applied on the target unit by the preset install handler.
    /// </summary>
    public record class AbilityInstallEvent : Event, IEventProcessingFailure
    {
        public required Guid CommandEventId { get; set; }
        public required Unit? Source { get; set; }
        public required Unit Target { get; set; }
        public required IReadOnlyList<IHandler> Handlers { get; set; }
        public AbilityInstallPurpose Purpose { get; set; }
        public StatusApplicationOrigin? ApplicationOrigin { get; set; }
        public IReadOnlyList<PeriodicEffectApplication> PeriodicEffects { get; set; } = [];
        public AbilityLeaseCancellationPolicy LeaseCancellationPolicy { get; set; }
        public TimeSpan? ExpireAt { get; set; }
        public EventReceiver? CompletionReceiver { get; set; }
        internal StatusReleaseBatch? StatusBatch { get; init; }

        void IEventProcessingFailure.OnProcessingFailure(Context context) => StatusBatch?.Abort(context);

        protected override bool PrintMembers(StringBuilder stringBuilder)
        {
            stringBuilder.Append($"{nameof(CommandEventId)} = {CommandEventId}, ");
            stringBuilder.Append($"{nameof(Source)} = {Source?.Name}, ");
            stringBuilder.Append($"{nameof(Target)} = {Target?.Name}, ");
            stringBuilder.Append($"{nameof(Handlers)} = [{string.Join(",", Handlers.Select(m => m.Id))}], ");
            stringBuilder.Append($"{nameof(LeaseCancellationPolicy)} = {LeaseCancellationPolicy}, ");
            stringBuilder.Append($"{nameof(ExpireAt)} = {ExpireAt}, ");
            base.PrintMembers(stringBuilder);
            return true;
        }
    }
}
