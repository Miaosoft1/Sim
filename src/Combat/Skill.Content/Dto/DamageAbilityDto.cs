namespace Miaosoft.Sim.Combat.Skill.Content.Dto;

public sealed class DamageAbilityDto
{
    public List<DamageComponentDto>? Damage { get; set; }
    public AbilityGateDto? Gate { get; set; }
    public DamageTickDto? Tick { get; set; }
    public AbilitySelectorDto? Selector { get; set; }
}

public sealed class DamageTickDto
{
    public int? TickCount { get; set; }
    public double? TickIntervalSeconds { get; set; }
    public double? FirstTickDelaySeconds { get; set; }
}

public sealed class DamageComponentDto
{
    public string? Type { get; set; }
    public string? Formula { get; set; }
    public string? Fallback { get; set; }
    public int Priority { get; set; }
}

public sealed class BeingDamageAbilityDto
{
    public List<string>? MitigationTypes { get; set; }
}
