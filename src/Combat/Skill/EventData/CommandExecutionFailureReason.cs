namespace Miaosoft.Sim.Combat.Skill.EventData
{
    public enum CommandExecutionFailureReason
    {
        None,
        SourceNotExist,
        TargetNotExist,
        CastForbidden,
        Casting,
        CoolingDown,
        /// <summary>
        /// The command target (unit or point) does not satisfy the ability selector;
        /// rejected before entering the cast action queue.
        /// </summary>
        SelectorRejected,
        /// <summary>
        /// The caster does not have sufficient resource (mana/energy) to cast the ability.
        /// </summary>
        ResourceInsufficient,
        AbilityNotAvailable,
        PeriodicEffectLimit,
        ChargesDepleted,
        AmbiguousAbility
    }
}