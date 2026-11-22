using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Core;
using System.Text;

namespace Miaosoft.Sim.Combat.Skill.EventData
{
    public record class DamageEvent : Event
    {
        private sealed record PendingDamage(DamageProfile Profile, Unit? Source, Unit Target, TimeSpan TimeSpan);
        private sealed class DamageState
        {
            public readonly List<Damage> Damages = [];
            public readonly Damage Total = new()
            {
                Handler = null!,
                Source = null,
                Target = null!,
                TimeSpan = TimeSpan.Zero
            };
            public PendingDamage? Pending;
            public EventScope? PublicationScope;
            public object? RequestIdentity;
            public object? PublicationIdentity;
        }

        private readonly DamageState state = new();
        private readonly SemanticEventDeliveryState<DamageEvent> publication = new();
        internal SemanticEventDeliveryState<DamageEvent> Publication => publication;
        internal object? RequestIdentity { get => state.RequestIdentity; set => state.RequestIdentity = value; }
        internal object? PublicationIdentity { get => state.PublicationIdentity; set => state.PublicationIdentity = value; }
        internal bool IsDelivery { get; init; }
        internal Guid? SettlementReplayId { get; init; }
        internal EventScope? PublicationScope
        {
            get => state.PublicationScope;
            set => state.PublicationScope = value;
        }
        internal bool IsPublicationCanceled =>
            CancellationToken.IsCancellationRequested || PublicationScope?.IsCancellationRequested == true;

        public required Guid CommandEventId { get; init; }
        public string? DamageAbilityId => AbilitySnapshot?.DefinitionId;
        public AbilityRuntimeSnapshot? AbilitySnapshot => state.Pending?.Profile.Ability ??
            (state.Damages.Count == 0 ? null : state.Total.Handler.Ability);
        public Unit? Target { get; init; }
        public Unit? Source { get; init; }
        public StatusApplicationOrigin? ApplicationOrigin { get; init; }
        public Map.MapObject? SourceObject { get; init; }
        public Map.MapObject? TargetObject { get; init; }
        public double? Distance { get; init; }
        public double? ProjectileSpeed { get; init; }
        public double ProjectileTravelDistance { get; init; }
        public AbilityTriggerChain? TriggerChain
        {
            get => Causality as AbilityTriggerChain;
            set => Causality = value;
        }

        public bool HasDamage => state.Pending != null || state.Damages.Count > 0;
        public bool IsDamageCalculated => state.Pending == null;

        public override IEnumerable<IEvent> Dispatch()
        {
            if (!IsDelivery)
                throw new InvalidOperationException("A damage request must be published before it is delivered.");
            return publication.DispatchDelivery(this);
        }

        /// <summary>Damage values are available after the target has settled the event.</summary>
        public IReadOnlyList<Damage> Damages
        {
            get
            {
                EnsureCalculated();
                return state.Damages;
            }
        }

        /// <summary>Damage values are available after the target has settled the event.</summary>
        public Damage TotalDamage
        {
            get
            {
                EnsureCalculated();
                return state.Total;
            }
        }

        internal void SetPendingDamage(DamageProfile profile, Unit? source, Unit target, TimeSpan timeSpan)
        {
            ArgumentNullException.ThrowIfNull(profile);
            ArgumentNullException.ThrowIfNull(target);
            if (HasDamage)
                throw new InvalidOperationException("The damage event already has an effect.");
            if (!ReferenceEquals(Source, source) || !ReferenceEquals(Target, target))
                throw new InvalidOperationException("Damage attribution must match the event source and target.");
            state.Pending = new PendingDamage(profile, source, target, timeSpan);
        }

        internal void CalculatePendingDamage()
        {
            PendingDamage? pending = state.Pending;
            if (pending == null) return;
            if (!ReferenceEquals(Source, pending.Source) || !ReferenceEquals(Target, pending.Target))
                throw new InvalidOperationException("Damage attribution changed before settlement.");
            Damage damage = pending.Profile.Calculate(pending.Source, pending.Target, pending.TimeSpan);
            state.Pending = null;
            AppendDamageCore(damage, settlement: true);
        }

        public void AppendDamage(Damage damage)
            => AppendDamageCore(damage, settlement: false);

        private void AppendDamageCore(Damage damage, bool settlement)
        {
            ArgumentNullException.ThrowIfNull(damage);
            if (publication.Claimed && !settlement)
                throw new InvalidOperationException("A published damage request cannot be modified.");
            if (state.Pending != null)
                throw new InvalidOperationException("Cannot append calculated damage before the pending effect settles.");
            if (!ReferenceEquals(Source, damage.Source) || Target != null && !ReferenceEquals(Target, damage.Target))
                throw new InvalidOperationException("Damage attribution must match the event source and target.");
            if (state.Damages.Count > 0 && !ReferenceEquals(state.Total.Handler, damage.Handler))
                throw new InvalidOperationException("A damage event cannot combine different effect profiles.");

            var totals = new List<(string Type, int Value)>();
            foreach (string type in damage.StoredKeys)
            {
                int previous = state.Total.GetValueOrDefault(type);
                totals.Add((type, checked(previous + damage.GetValueOrDefault(type))));
            }
            if (state.Damages.Count == 0)
            {
                // TotalDamage metadata is anchored to the first appended damage.
                state.Total.Handler = damage.Handler;
                state.Total.Source = damage.Source;
                state.Total.Target = Target ?? damage.Target;
                state.Total.TimeSpan = TimeSpan != default ? TimeSpan : damage.TimeSpan;
            }

            state.Damages.Add(damage);
            state.Total.UsedFormulaFallback |= damage.UsedFormulaFallback;
            foreach ((string type, int value) in totals)
                state.Total[type] = value;
        }

        private void EnsureCalculated()
        {
            if (state.Pending != null)
                throw new InvalidOperationException("Damage values are not available before settlement.");
        }

        protected override bool PrintMembers(StringBuilder stringBuilder)
        {
            stringBuilder.Append($"{nameof(CommandEventId)} = {CommandEventId}, ");
            stringBuilder.Append($"{nameof(DamageAbilityId)} = {DamageAbilityId ?? "<null>"}, ");
            stringBuilder.Append($"{nameof(Damages)} = {(state.Pending == null ? FormatDamageValues(state.Total) : "<pending>")}, ");
            stringBuilder.Append($"{nameof(Source)} = {Source?.Name}, ");
            stringBuilder.Append($"{nameof(Target)} = {Target?.Name}, ");
            stringBuilder.Append($"{nameof(Distance)} = {Distance}, ");
            stringBuilder.Append($"{nameof(ProjectileSpeed)} = {ProjectileSpeed}, ");
            stringBuilder.Append($"{nameof(ProjectileTravelDistance)} = {ProjectileTravelDistance}, ");
            stringBuilder.Append($"{nameof(TimeSpan)} = {TimeSpan}, ");
            base.PrintMembers(stringBuilder);
            return true;
        }

        private static string FormatDamageValues(Damage damage)
        {
            if (damage.StoredKeys.Count == 0)
            {
                return "<none>";
            }

            StringBuilder builder = new StringBuilder();
            for (int index = 0; index < damage.StoredKeys.Count; index++)
            {
                string type = damage.StoredKeys[index];
                if (index > 0)
                {
                    builder.Append("|");
                }

                builder.Append(type);
                builder.Append(":");
                builder.Append(damage.GetValueOrDefault(type));
            }

            return builder.ToString();
        }
    }
}
