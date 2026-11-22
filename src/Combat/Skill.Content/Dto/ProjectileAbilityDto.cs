using Miaosoft.Sim.Combat.Skill;

namespace Miaosoft.Sim.Combat.Skill.Content.Dto;

public sealed class ProjectileAbilityDto
{
    public double? Speed { get; set; }
    public double? DelaySeconds { get; set; }
    public ProjectileExplosionTrigger ExplosionTrigger { get; set; }
    public AbilityGateDto? Gate { get; set; }
    public AbilitySelectorDto? Selector { get; set; }
}
