namespace Miaosoft.Sim.Combat.Skill.EventData;

public static class CombatEventPriority
{
    public const int Death = -1_000_000;
    public const int DeathCommit = Death + 1;
    public const int DeathResolved = -3_000_000;
    public const int DeathEquipmentSettled = DeathResolved + 1;
    public const int DeathPublication = -2_000_001;
}
