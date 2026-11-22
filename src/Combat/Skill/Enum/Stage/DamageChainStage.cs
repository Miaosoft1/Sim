namespace Miaosoft.Sim.Combat.Skill
{
    /// <summary>
    /// Damage chain execution stage.
    /// Lower value runs earlier in the chain.
    /// </summary>
    public enum DamageChainStage
    {
        Command = 0,
        Cast = 100,
        ProjectileTravel = 200,
        AttachedAbility = 300,
        EffectAmplification = 400,
        PassiveAmplification = 500,
        AdditionalAbility = 600,
        BeforeSettlement = 700,
        Settlement = 800,
        AfterSettlement = 900,
    }
}
