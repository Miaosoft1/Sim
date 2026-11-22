namespace Miaosoft.Sim.Combat.Skill.Content.Dto;

public sealed class StatusAbilityDto
{
    public double? DurationSeconds { get; set; }
    public string? TimeBasis { get; set; }
    public string? StackPolicy { get; set; }
    public int? StackLimit { get; set; }
    public bool ProtectedUntilExpiry { get; set; }
    public List<PropertyModifierDto>? Modifiers { get; set; }
    public List<string>? RecalculateOn { get; set; }
    public List<StatusPeriodicEffectDto>? PeriodicEffects { get; set; }
    public AbilityGateDto? Gate { get; set; }
    public AbilitySelectorDto? Selector { get; set; }
}
