namespace Miaosoft.Sim.Combat.Skill.Ability;

public interface IAbilitySource
{
    Guid InstanceId { get; }
    string Category { get; }
}
