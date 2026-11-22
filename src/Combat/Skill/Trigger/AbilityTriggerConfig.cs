using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Combat.Skill
{
    public class AbilityTriggerConfig
    {
        private readonly AtomicCell<bool> frozen = new(false);
        private TriggerType type = TriggerType.None;
        private string? signalId;
        private string? sourceAbilityId;
        private IReadOnlyList<string>? sourceAbilityIds;
        private TriggerSourceScope eventSourceScope = TriggerSourceScope.Self;
        private IReadOnlyList<ITriggerCondition> conditions = Array.Empty<ITriggerCondition>();
        private DeathTriggerPhase deathPhase = DeathTriggerPhase.Confirmed;

        internal bool IsFrozen => frozen.Value;
        internal void StageFreeze(AtomicTransaction transaction)
        {
            if (!transaction.Read(frozen)) transaction.Stage(frozen, true);
        }

        private void EnsureMutable()
        {
            if (IsFrozen)
                throw new InvalidOperationException("An installed trigger configuration cannot be changed.");
        }

        public TriggerType Type { get => type; set { EnsureMutable(); type = value; } }
        public string? SignalId { get => signalId; set { EnsureMutable(); signalId = value; } }
        public string? SourceAbilityId { get => sourceAbilityId; set { EnsureMutable(); sourceAbilityId = value; } }
        public IReadOnlyList<string>? SourceAbilityIds
        {
            get => sourceAbilityIds;
            set { EnsureMutable(); sourceAbilityIds = value == null ? null : Array.AsReadOnly(value.ToArray()); }
        }
        public TriggerSourceScope EventSourceScope
        {
            get => eventSourceScope;
            set
            {
                EnsureMutable();
                if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(EventSourceScope));
                eventSourceScope = value;
            }
        }
        public IReadOnlyList<ITriggerCondition> Conditions
        {
            get => conditions;
            set
            {
                EnsureMutable();
                ArgumentNullException.ThrowIfNull(value);
                if (value.Any(condition => condition == null))
                    throw new ArgumentException("Trigger conditions cannot contain null.", nameof(value));
                conditions = Array.AsReadOnly(value.ToArray());
            }
        }
        private double? probability;
        public double? Probability
        {
            get => probability;
            set
            {
                EnsureMutable();
                if (value is double number && (!double.IsFinite(number) || number < 0 || number > 1))
                    throw new ArgumentOutOfRangeException(nameof(Probability), "Probability must be between zero and one.");
                probability = value;
            }
        }
        public DeathTriggerPhase DeathPhase { get => deathPhase; set { EnsureMutable(); deathPhase = value; } }

        public bool MatchesEventSource(Unit? source, Unit holder)
        {
            ArgumentNullException.ThrowIfNull(holder);
            return EventSourceScope switch
            {
                TriggerSourceScope.Self => ReferenceEquals(source, holder),
                TriggerSourceScope.Ally => source != null && source.TeamId == holder.TeamId,
                TriggerSourceScope.Enemy => source != null && source.TeamId != holder.TeamId,
                TriggerSourceScope.Any => true,
                _ => throw new InvalidOperationException("The trigger source scope is invalid.")
            };
        }

        public bool MatchesSource(string? abilityId, string targetAbilityId, bool useTargetAsDefault = false)
        {
            IReadOnlyList<string>? sourceIds = SourceAbilityIds;
            if ((sourceIds == null || sourceIds.Count == 0) && !string.IsNullOrEmpty(SourceAbilityId))
            {
                sourceIds = new[] { SourceAbilityId };
            }

            if (sourceIds == null || sourceIds.Count == 0)
            {
                return !useTargetAsDefault || string.Equals(abilityId, targetAbilityId, StringComparison.Ordinal);
            }

            return abilityId != null && sourceIds.Contains(abilityId, StringComparer.Ordinal);
        }

        public bool ShouldTrigger(Random random)
        {
            ArgumentNullException.ThrowIfNull(random);
            if (!Probability.HasValue || Probability.Value >= 1.0)
            {
                return true;
            }

            if (Probability.Value <= 0.0)
            {
                return false;
            }

            double roll = random.NextDouble();
            if (!double.IsFinite(roll) || roll < 0 || roll >= 1)
                throw new InvalidOperationException("The random source returned a value outside [0, 1).");
            return roll < Probability.Value;
        }
    }
}
