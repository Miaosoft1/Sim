using System.Text;

namespace Miaosoft.Sim.Combat.Skill.EventData
{
    public enum AbilityLifecycleStage
    {
        Start = 0,
        Tick = 1,
        Unapply = 2,
    }

    public record class AbilityLifecycleEvent : Event
    {
        public required Guid CommandEventId { get; set; }
        public required Unit? Source { get; set; }
        public required Unit Target { get; set; }
        public AbilityLifecycleStage Stage { get; set; } = AbilityLifecycleStage.Start;
        public string? LeaseId { get; set; }
        public Guid? LeaseInstanceId { get; set; }
        public IReadOnlyList<string>? LeaseAbilityIds { get; set; }
        public TimeSpan? LeaseExpireAt { get; set; }
        public AbilityTriggerChain? TriggerChain
        {
            get => Causality as AbilityTriggerChain;
            set => Causality = value;
        }
        public int RemainingTicks { get; set; }
        public TimeSpan TickInterval { get; set; } = TimeSpan.Zero;
        public DamageProfile? TickDamage { get; set; }
        public HealProfile? TickHeal { get; set; }
        internal PeriodicEffectHandle? PeriodicEffect { get; init; }

        protected override bool PrintMembers(StringBuilder stringBuilder)
        {
            stringBuilder.Append($"{nameof(CommandEventId)} = {CommandEventId}, ");
            stringBuilder.Append($"{nameof(Source)} = {Source?.Name}, ");
            stringBuilder.Append($"{nameof(Target)} = {Target?.Name}, ");
            stringBuilder.Append($"{nameof(Stage)} = {Stage}, ");
            stringBuilder.Append($"{nameof(LeaseId)} = {LeaseId ?? "<null>"}, ");
            stringBuilder.Append($"{nameof(LeaseInstanceId)} = {LeaseInstanceId}, ");
            stringBuilder.Append($"{nameof(LeaseAbilityIds)} = [{string.Join(",", LeaseAbilityIds ?? Array.Empty<string>())}], ");
            stringBuilder.Append($"{nameof(LeaseExpireAt)} = {LeaseExpireAt}, ");
            stringBuilder.Append($"{nameof(RemainingTicks)} = {RemainingTicks}, ");
            stringBuilder.Append($"{nameof(TickInterval)} = {TickInterval}, ");
            base.PrintMembers(stringBuilder);
            return true;
        }
    }
}
