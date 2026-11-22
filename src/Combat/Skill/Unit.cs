using Miaosoft.Sim.Core;
using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.Visibility;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.Handler;

namespace Miaosoft.Sim.Combat.Skill
{
    public class Unit
    {
        public const int DefaultMaximumLevel = 10;
        private int maximumLevel = DefaultMaximumLevel;
        public int MaximumLevel
        {
            get => maximumLevel;
            init
            {
                if (value < 1) throw new ArgumentOutOfRangeException(nameof(value));
                maximumLevel = value;
            }
        }
        private UnitProperty property = new UnitProperty();
        private TimeSpan lastEnergyUpdate;
        private bool energyClockInitialized;

        public Guid Id { get; init; } = Guid.NewGuid();
        public required string Name { get; set; }
        public UnitProperty Property
        {
            get
            {
                if (EventReceiver.Context is { } context)
                    SynchronizeEnergy(context.ContextTimeSpan);
                return Abilities.Property;
            }
            set => property = value;
        }
        public required string ArchetypeId { get; set; }
        public IReadOnlySet<string> Tags { get; init; } = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>
        /// Team or faction identifier. Units with the same TeamId are allies;
        /// different TeamIds are enemies. Defaults to 0.
        /// </summary>
        private int teamId;
        public event Action<Unit>? TeamChanged;
        public int TeamId
        {
            get => teamId;
            set
            {
                if (teamId == value) return;
                teamId = value;
                MapObject?.NotifyVisibilityChanged(refreshRules: true);
                TeamChanged?.Invoke(this);
            }
        }
        /// <summary>
        /// Map presence of this unit; provides the world position used by selectors.
        /// Null means the unit has no coordinates (position-based selectors cannot target it).
        /// </summary>
        private UnitMapObjectManager? mapObjects;
        public UnitMapObjectManager MapObjects => mapObjects ??= new(Id.ToString("N"));
        public MapObject? MapObject => MapObjects.ControlledObject;
        /// <summary>
        /// Spatial registry this unit is registered in; selectors use it (via the command source)
        /// to resolve area/direction targets. Assigned by the assembly layer at creation.
        /// </summary>
        public Miaosoft.Sim.Combat.Skill.Targeting.UnitSpatialRegistry? Registry { get; set; }
        public Func<IMapVisibilityQuery>? VisibilityQuery { get; set; }
        /// <summary>
        /// Unit runtime ability management, will change during battle.
        /// All calculation should be based on applied ability instances, not the property. The property is just for init and record.
        /// </summary>
        public required UnitAbilityManager Abilities { get; init; }
        public UnitActionGateHandler? ActionGate => EventReceiver.Get<UnitActionGateHandler>();
        public bool IsAlive => ActionGate != null;
        private Guid? lastDeathId;
        public Guid? CurrentDeathId => IsAlive ? null : lastDeathId;
        public bool IsRetired { get; private set; }
        public bool CanParticipate => IsAlive && !IsRetired;
        public AbilityLifecycleDeathPolicy LifecycleOnDeath { get; set; } = AbilityLifecycleDeathPolicy.Stop;
        public UnitStateCollection States { get; } = new();
        public DeferredCombatImpacts? DeferredImpacts { get; internal set; }
        public required EventReceiver EventReceiver { get; init; }

        public Unit()
        {
        }

        public void Initialize()
        {
            if (property.Level < 1 || property.Level > MaximumLevel)
                throw new InvalidOperationException("The initial unit level must be within its configured range.");
            if (Abilities.Owner != null && !ReferenceEquals(Abilities.Owner, this))
                throw new InvalidOperationException("An ability manager cannot be shared between units.");
            if (EventReceiver.Get<HealingHandler>() == null)
                EventReceiver.AddHandler(new HealingHandler());
            if (EventReceiver.Get<AbilityLifecycleHandler>() == null)
                EventReceiver.AddHandler(new AbilityLifecycleHandler());
            if (EventReceiver.GetHandlers<AbilityChargeRestoreHandler>().Count > 1)
                throw new InvalidOperationException("A unit cannot have duplicate charge restore handlers.");
            if (EventReceiver.Get<AbilityChargeRestoreHandler>() == null)
                EventReceiver.AddHandler(new AbilityChargeRestoreHandler());
            if (EventReceiver.GetHandlers<UnitLevelUpHandler>().Count > 1)
                throw new InvalidOperationException("A unit cannot have duplicate level-up handlers.");
            if (EventReceiver.Get<UnitLevelUpHandler>() == null)
                EventReceiver.AddHandler(new UnitLevelUpHandler());
            var damageHandlers = EventReceiver.GetHandlers<ScheduledDamageHandler>();
            var healHandlers = EventReceiver.GetHandlers<ScheduledHealHandler>();
            if (damageHandlers.Count > 1 || healHandlers.Count > 1)
                throw new InvalidOperationException("A unit cannot have duplicate periodic settlement handlers.");
            ScheduledDamageHandler damage = damageHandlers.SingleOrDefault() ?? new ScheduledDamageHandler();
            ScheduledHealHandler heal = healHandlers.SingleOrDefault() ?? new ScheduledHealHandler();
            if (damageHandlers.Count == 0) EventReceiver.AddHandler(damage);
            if (healHandlers.Count == 0) EventReceiver.AddHandler(heal);
            EnsurePeriodicTrigger(TriggerType.ScheduledDamageTick, damage.RuntimeInstanceId);
            EnsurePeriodicTrigger(TriggerType.ScheduledHealTick, heal.RuntimeInstanceId);
            Abilities.Owner = this;
            Abilities.BaseProperty = property.Copy();
            Abilities.Property = property.Copy();
            Abilities.EventReceiver = EventReceiver;
            Abilities.VisibilityStats = MapObjects.VisibilityStats;
            EventReceiver.Get<AbilityInstallHandler>()?.UseLeaseState(Abilities.Leases);
            lastEnergyUpdate = EventReceiver.Context?.ContextTimeSpan ?? TimeSpan.Zero;
            energyClockInitialized = true;
            Abilities.ReApply(now: TimeSpan.Zero);
        }

        private void EnsurePeriodicTrigger(TriggerType type, Guid targetId)
        {
            TriggerHandler[] routers = EventReceiver.GetHandlers<TriggerHandler>()
                .Where(handler => handler.IsPeriodicSettlement && handler.TriggerType == type).ToArray();
            if (routers.Length > 1 || routers.Length == 1 && routers[0].TargetInstanceId != targetId)
                throw new InvalidOperationException("The periodic settlement router is inconsistent.");
            if (routers.Length != 0) return;
            var trigger = new TriggerHandler
            {
                Id = $"system.{type}",
                TargetAbilityId = type.ToString(),
                TriggerType = type,
                IsPeriodicSettlement = true
            };
            var transaction = new AtomicTransaction();
            trigger.StageBindTarget(transaction, targetId);
            trigger.Config.StageFreeze(transaction);
            transaction.Commit();
            EventReceiver.AddHandler(trigger);
        }

        public void SetLevel(int level)
        {
            if (level < 1 || level > MaximumLevel) throw new ArgumentOutOfRangeException(nameof(level));
            _ = Property;
            this.Abilities.ReApply(level);
        }

        public void LevelUp(int amount = 1)
        {
            _ = Property;
            int next = (int)Math.Clamp((long)this.Abilities.Property.Level + amount, 1, MaximumLevel);
            this.Abilities.ReApply(next);
        }

        public void Killed() => Killed(CurrentDeathId ?? Guid.NewGuid());

        internal void Killed(Guid deathId)
        {
            if (deathId == Guid.Empty)
                throw new ArgumentOutOfRangeException(nameof(deathId));
            if (!IsAlive && CurrentDeathId.HasValue && deathId != CurrentDeathId)
                throw new InvalidOperationException("An already dead unit cannot enter another death episode.");
            if (!Enum.IsDefined(LifecycleOnDeath))
                throw new InvalidOperationException("The unit's ability lifecycle death policy is invalid.");
            _ = Property;
            if (IsAlive || lastDeathId == null) lastDeathId = deathId;
            MapObjects.Deactivate();
            UnitActionGateHandler? actionGate = ActionGate;
            if (actionGate != null)
            {
                actionGate.Dispose();
                EventReceiver.TryRemoveHandler(actionGate);
            }
            AbilityLifecycleHandler? lifecycle = EventReceiver.Get<AbilityLifecycleHandler>();
            if (lifecycle != null && LifecycleOnDeath == AbilityLifecycleDeathPolicy.Stop)
            {
                EventReceiver.TryRemoveHandler(lifecycle);
            }
            MapObject?.NotifyVisibilityChanged();
        }

        internal void Retire()
        {
            if (IsRetired) return;
            _ = Property;
            IsRetired = true;
            ActionGate?.CurrentCast?.Cancel();
            MapObjects.Clear();
            Registry?.Unregister(this);
            Registry = null;
            VisibilityQuery = null;
        }

        public void SynchronizeEnergy(TimeSpan now)
        {
            if (!energyClockInitialized)
            {
                lastEnergyUpdate = now;
                energyClockInitialized = true;
            }
            if (now < lastEnergyUpdate)
                throw new InvalidOperationException("Energy cannot be synchronized to an earlier simulation time.");

            UnitProperty current = Abilities.Property;
            int maximum = Math.Max(0, current.EnergyMax);
            decimal energy = Math.Clamp(current.Energy, 0, maximum);
            if (IsAlive && current.EnergyRecoveryPerSecond > 0 && energy < maximum)
            {
                decimal elapsed = (decimal)(now - lastEnergyUpdate).Ticks / TimeSpan.TicksPerSecond;
                energy = Math.Min(maximum, energy + elapsed * current.EnergyRecoveryPerSecond);
            }
            current.Energy = energy;
            lastEnergyUpdate = now;
        }

        public override string ToString()
        {
            return this.Name;
        }
    }
}
