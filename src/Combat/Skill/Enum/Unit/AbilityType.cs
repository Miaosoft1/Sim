namespace Miaosoft.Sim.Combat.Skill
{
    /// <summary>
    /// Ability flags are composed by trigger categories and execution mode.
    /// Trigger categories: Attack/Cast/Rune.
    /// Execution mode: Standalone.
    /// </summary>
    [Flags]
    public enum AbilityType
    {
        None = 0,
        Attack = 1 << 0,
        Cast = 1 << 1,
        Rune = 1 << 2,
        Standalone = 1 << 3,
    }
}