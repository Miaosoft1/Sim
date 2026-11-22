namespace Miaosoft.Sim.Combat.Skill.Content.Dto;

public sealed class PropertyModifierDto
{
    public string? Target { get; set; }
    public string? Formula { get; set; }
    public string? Fallback { get; set; }
}

public sealed class PropertyAbilityDto
{
    public List<PropertyModifierDto>? Modifiers { get; set; }
    public List<string>? RecalculateOn { get; set; }
}
