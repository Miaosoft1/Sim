namespace Miaosoft.Sim.Combat.Skill
{
    public enum TriggerType
    {
        None = 0,
        Command = 1,
        DamageSkillHit = 2,
        TargetLifeReduce = 3,
        ProjectileHit = 4,
        Heal = 5,
        BeingDamaged = 6,
        Death = 7,
        Kill = 8,
        ProjectileExplosion = 9,
        Signal = 10,
        ScheduledDamageTick = 12,
        ScheduledHealTick = 13,
        UnitLevelUp = 14,
    }
}
