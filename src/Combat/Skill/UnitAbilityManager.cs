using Miaosoft.Sim.Core;
using Miaosoft.Sim.Combat.Map.Visibility;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.Expression;
using Miaosoft.Sim.Combat.Skill.Handler;

namespace Miaosoft.Sim.Combat.Skill
{
    /// <summary>
    /// Unified runtime ability management for a unit. There is no separate status management:
    /// applied abilities are tracked directly on the unit's EventReceiver, and presentation
    /// filters them by <see cref="AbilityLabel.Status"/>.
    /// </summary>
    public class UnitAbilityManager
    {
        public UnitProperty BaseProperty { get; set; } = new UnitProperty();
        private sealed record Projection(UnitProperty Property, bool LifeInitialized, bool UsedFormulaFallback);
        private readonly AtomicCell<Projection> projection = new(new(new UnitProperty(), false, false));
        public UnitProperty Property
        {
            get => projection.Value.Property;
            set => projection.Update(current => new(value, current.LifeInitialized, current.UsedFormulaFallback));
        }
        public bool UsedFormulaFallback => projection.Value.UsedFormulaFallback;
        internal Unit? Owner { get; set; }
        public EventReceiver? EventReceiver { get; set; }
        public UnitVisibilityStats? VisibilityStats { get; set; }
        internal AbilityInstallHandler.LeaseState Leases { get; } = new();
        internal AtomicCell<AbilitySourceInstance[]> SourceRegistrations { get; } = new([]);
        public IReadOnlyList<AbilitySourceInstance> Sources => Array.AsReadOnly(SourceRegistrations.Value);

        public IHandler? GetInstance(IHandler handler)
        {
            return EventReceiver?.GetHandlers<IHandler>().FirstOrDefault(m => string.Equals(m.Id, handler.Id, StringComparison.Ordinal));
        }

        public IReadOnlyList<IHandler> GetInstances(IHandler handler)
        {
            return EventReceiver?.GetHandlers<IHandler>().Where(m => string.Equals(m.Id, handler.Id, StringComparison.Ordinal)).ToList() ?? new List<IHandler>();
        }

        public IReadOnlyList<IHandler> GetInstances()
        {
            return EventReceiver?.GetHandlers<IHandler>().ToList() ?? new List<IHandler>();
        }

        /// <summary>
        /// Abilities marked with the given label, e.g. <see cref="AbilityLabel.Status"/> for game presentation.
        /// </summary>
        public IReadOnlyList<AbilityBase> GetAbilitiesByLabel(AbilityLabel label)
        {
            if (EventReceiver == null)
            {
                return Array.Empty<AbilityBase>();
            }

            return EventReceiver.GetHandlers<AbilityBase>()
                .Where(m => m.Labels.HasFlag(label)
                    && (m is not StatusAbility status || !status.IsTriggerListener))
                .ToList();
        }

        public int CountInstances(IHandler handler)
        {
            return EventReceiver?.GetHandlers<IHandler>().Count(m => string.Equals(m.Id, handler.Id, StringComparison.Ordinal)) ?? 0;
        }

        public int RemoveByAbilityIds(IEnumerable<string> abilityIds)
        {
            HashSet<string> idSet = abilityIds.ToHashSet(StringComparer.Ordinal);
            if (idSet.Count == 0 || EventReceiver == null)
            {
                return 0;
            }
            if (Sources.SelectMany(source => source.Abilities).Any(ability => idSet.Contains(ability.Id)))
                throw new InvalidOperationException("Managed source definitions must change through a source change event.");

            return EventReceiver.RemoveHandlers(m => idSet.Contains(m.Id));
        }

        public void ReApply(int? levelOverride = null, TimeSpan? now = null)
        {
            var transaction = new AtomicTransaction();
            StageReApply(transaction, EventReceiver?.GetHandlers<IHandler>() ?? [], levelOverride);
            transaction.Commit();
            PublishProjection();
        }

        internal void StageEnergyCost(AtomicTransaction transaction, int cost)
        {
            if (cost < 0) throw new ArgumentOutOfRangeException(nameof(cost));
            if (cost == 0) return;
            transaction.Update(projection, current =>
            {
                if (current.Property.Energy < cost)
                    throw new InvalidOperationException("The unit no longer has enough energy for this release.");
                UnitProperty next = current.Property.Copy();
                next.Energy -= cost;
                return current with { Property = next };
            });
        }

        internal void StageReApply(AtomicTransaction transaction, IReadOnlyList<IHandler> handlers, int? levelOverride = null)
        {
            List<AbilityBase> propertyAbilities = handlers.OfType<AbilityBase>()
                .Where(m => m is PropertyAbility or StatusAbility { IsTriggerListener: false })
                .OrderBy(m => m.Priority)
                .ToList();
            List<StatusAbility> statusAbilities = propertyAbilities.OfType<StatusAbility>().ToList();

            Projection current = transaction.Read(projection);
            int nextLevel = Math.Max(1, levelOverride ?? current.Property.Level);
            int? oldLife = current.Property.LifePoint;
            decimal oldEnergy = current.Property.Energy;
            int oldExperience = Math.Max(0, current.Property.Experience);
            bool lifeInitialized = current.LifeInitialized;

            UnitVisibilityStats? stagedVisibility = VisibilityStats?.CreateDetached();
            UnitProperty next = BaseProperty.Copy();
            next.Level = nextLevel;
            next.LifePoint = oldLife ?? next.LifePoint;
            next.Energy = oldEnergy;
            next.Experience = oldExperience;

            bool usedFallback = false;
            IReadOnlyList<AbilityBase> mounted = EventReceiver?.GetHandlers<AbilityBase>() ?? [];
            foreach (AbilityBase ability in propertyAbilities)
            {
                Unit? source = ability is StatusAbility sourceStatus ? sourceStatus.ReadApplicationSource(transaction) :
                    Owner ?? throw new InvalidOperationException("A passive property ability requires an initialized owner.");
                var context = new PropertyFormulaContext(source, next, stagedVisibility, ability.ReadLevel(transaction), Owner);
                usedFallback |= ability switch
                {
                    StatusAbility status => status.ApplyModifiers(
                        transaction, context, status.ReadStack(transaction), !mounted.Contains(status)),
                    PropertyAbility property => property.ApplyModifiers(transaction, context, !mounted.Contains(property)),
                    _ => throw new InvalidOperationException("Unsupported property ability.")
                };
            }

            decimal lifeMaximum = Math.Max(0, next.LifePointMax);
            decimal energyMaximum = Math.Max(0, next.EnergyMax);
            foreach (StatusAbility status in statusAbilities)
            {
                for (int stack = 0; stack < status.ReadStack(transaction); stack++)
                {
                    if (status.LifePointMaxMultiplier is { } lifeMultiplier)
                        lifeMaximum *= lifeMultiplier;
                    if (status.EnergyMaxMultiplier is { } energyMultiplier)
                        energyMaximum *= energyMultiplier;
                }
            }
            next.LifePointMax = (int)decimal.Floor(lifeMaximum);
            next.EnergyMax = (int)decimal.Floor(energyMaximum);

            if (!lifeInitialized && next.LifePointMax > 0)
            {
                next.LifePoint = Math.Clamp(oldLife ?? next.LifePoint ?? next.LifePointMax,
                    0, next.LifePointMax);
                lifeInitialized = true;
            }
            else if (lifeInitialized)
            {
                next.LifePointMax = Math.Max(1, next.LifePointMax);
                next.LifePoint = Math.Clamp(oldLife ?? next.LifePointMax, 0, next.LifePointMax);
            }
            else
            {
                next.LifePoint = oldLife ?? next.LifePoint;
            }
            next.EnergyMax = Math.Max(0, next.EnergyMax);
            next.Energy = Math.Clamp(oldEnergy, 0, next.EnergyMax);
            next.Experience = Math.Clamp(oldExperience, 0, Math.Max(0, next.ExperienceMax));

            transaction.Update(projection, _ => new Projection(next, lifeInitialized, usedFallback));
            if (VisibilityStats != null && stagedVisibility != null)
                VisibilityStats.StageModifiers(transaction, stagedVisibility);
        }

        internal void PublishProjection() => VisibilityStats?.PublishModifiersChanged();
    }
}
