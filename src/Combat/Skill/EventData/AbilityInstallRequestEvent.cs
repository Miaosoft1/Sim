using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.Ability;
using System.Text;

namespace Miaosoft.Sim.Combat.Skill.EventData
{
    public enum AbilityInstallPurpose
    {
        Definition,
        StatusApplication
    }

    public enum AbilityLeaseCancellationPolicy
    {
        FollowRequest,
        ContinueAfterCommit
    }

    /// <summary>
    /// Requests installation of abilities on a unit. Handled by the preset
    /// <see cref="Miaosoft.Sim.Combat.Skill.Handler.AbilityInstallHandler"/>,
    /// which materializes handlers and emits <see cref="AbilityInstallEvent"/>.
    /// </summary>
    public record class AbilityInstallRequestEvent : Event, IEventProcessingFailure
    {
        public required Guid CommandEventId { get; set; }
        public required Unit? Source { get; set; }
        public required Unit Target { get; set; }
        public required IReadOnlyList<string> AbilityIds { get; set; }
        public int? Level { get; set; }
        public AbilityInstallPurpose Purpose { get; set; }
        public StatusApplicationOrigin? ApplicationOrigin { get; set; }
        public IReadOnlyList<PeriodicEffectApplication> PeriodicEffects { get; set; } = [];
        public AbilityLeaseCancellationPolicy LeaseCancellationPolicy { get; set; }
        public TimeSpan? ExpireAt { get; set; }
        public AbilityLabel? AdditionalLabels { get; set; }
        public EventReceiver? CompletionReceiver { get; set; }
        internal StatusReleaseBatch? StatusBatch { get; init; }

        void IEventProcessingFailure.OnProcessingFailure(Context context) => StatusBatch?.Abort(context);

        protected override bool PrintMembers(StringBuilder stringBuilder)
        {
            stringBuilder.Append($"{nameof(CommandEventId)} = {CommandEventId}, ");
            stringBuilder.Append($"{nameof(Source)} = {Source?.Name}, ");
            stringBuilder.Append($"{nameof(Target)} = {Target?.Name}, ");
            stringBuilder.Append($"{nameof(AbilityIds)} = [{string.Join(",", AbilityIds)}], ");
            stringBuilder.Append($"{nameof(Level)} = {Level}, ");
            stringBuilder.Append($"{nameof(Purpose)} = {Purpose}, ");
            stringBuilder.Append($"{nameof(LeaseCancellationPolicy)} = {LeaseCancellationPolicy}, ");
            stringBuilder.Append($"{nameof(ExpireAt)} = {ExpireAt}, ");
            stringBuilder.Append($"{nameof(AdditionalLabels)} = {AdditionalLabels}, ");
            base.PrintMembers(stringBuilder);
            return true;
        }
    }
}
