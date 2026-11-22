namespace Miaosoft.Sim.Combat.Skill.Content.Dto;

public sealed class HealAbilityDto
{
    public int? Amount { get; set; }
    public bool RestoreToMaximum { get; set; }
    public AbilityGateDto? Gate { get; set; }
    public AbilitySelectorDto? Selector { get; set; }
}
