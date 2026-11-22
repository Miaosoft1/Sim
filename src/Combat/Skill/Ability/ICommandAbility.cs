namespace Miaosoft.Sim.Combat.Skill.Ability;

public interface ICommandAbility : IAbilityGate
{
    bool OccupiesCastingChannel => GateManager?.OccupiesWindup ?? false;
}
