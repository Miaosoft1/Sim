namespace Miaosoft.Sim.Combat.Skill
{
    [Flags]
    public enum AbilityLabel
    {
        None = 0,
        /// <summary>
        /// Marks an ability to be presented as a status in game UI.
        /// </summary>
        Status = 1 << 17,
        FromEquipment = 1 << 0,
        FromPassiveSkill = 1 << 1,
        Buff = 1 << 2,
        Debuff = 1 << 3,
        WeakDispellable = 1 << 4,
        StrongDispellable = 1 << 5,
        Casting = 1 << 6,
        Stunned = 1 << 7,
        BaseProperty = 1 << 8,
        Immobilize = 1 << 9,
        Blind = 1 << 10,
        Disarm = 1 << 11,
        Ground = 1 << 12,
        Nearsight = 1 << 13,
        Polymorph = 1 << 14,
        Silence = 1 << 15,
        Slow = 1 << 16,
        /// <summary>
        /// Marks an ability that can be cast actively (e.g. triggered by Command).
        /// </summary>
        ActiveCast = 1 << 18,
        /// <summary>
        /// Marks an ability originating from the character/hero archetype or build.
        /// </summary>
        FromCharacter = 1 << 19,
        /// <summary>
        /// Marks an ability originating from a talent tree.
        /// </summary>
        FromTalent = 1 << 20,
    }
}