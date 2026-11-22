using Miaosoft.Sim.Combat.Skill.Handler;
using Miaosoft.Sim.Combat.Skill.EventData;

namespace Miaosoft.Sim.Combat.Skill.Ability
{
    public abstract class AbilityBase : Miaosoft.Sim.Combat.Skill.Handler.Handler,
        IAbilityRuntimePart, IRequiresCurrentRegistration, ITargetableHandler, IRuntimeInstanceHandler, ILevelAbility
    {
        private readonly AtomicCell<int> level = new(1);
        public AbilitySourceBinding Binding { get; } = new();
        public Guid RuntimeInstanceId => Binding.InstanceId;
        public int Level
        {
            get => level.Value;
            set
            {
                if (Binding.Source is AbilitySourceInstance { IsMaterialized: true })
                    throw new InvalidOperationException("Change a materialized source ability's level through a source change event.");
                level.Set(value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value)));
            }
        }
        private bool allowMultiple;
        public bool AllowMultiple
        {
            get => allowMultiple;
            set
            {
                if (Binding.Source is AbilitySourceInstance { IsMaterialized: true })
                    throw new InvalidOperationException("A materialized source's coexistence policy cannot change.");
                allowMultiple = value;
            }
        }
        internal int ReadLevel(AtomicTransaction transaction) => transaction.Read(level);
        internal void StageLevel(AtomicTransaction transaction, int value) =>
            transaction.Update(level, _ => value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value)));
        public bool RequiresCurrentRegistration(IEvent ev) => ev is AbilityTriggerInvokeEvent;
        /// <summary>
        /// Definition-level labels. Game presentation filters applied ability instances
        /// by <see cref="AbilityLabel.Status"/> to show them as statuses.
        /// </summary>
        public virtual AbilityLabel Labels { get; set; } = AbilityLabel.None;
        public CastingBehavior? Casting { get; set; }
        public FormulaEvaluationPolicy FormulaPolicy { get; set; } = FormulaEvaluationPolicy.Strict;
    }
}