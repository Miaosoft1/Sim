namespace Miaosoft.Sim.Combat.Skill.Ability
{
    public class DamageTick
    {
        public int TickCount { get; set; }
        public TimeSpan TickInterval { get; set; } = TimeSpan.Zero;
        public TimeSpan? FirstTickDelay { get; set; }
    }
}