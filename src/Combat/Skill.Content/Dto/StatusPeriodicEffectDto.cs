namespace Miaosoft.Sim.Combat.Skill.Content.Dto;

public sealed class StatusPeriodicEffectDto
{
    public string? Type { get; set; }
    public DamageTickDto? Tick { get; set; }
    public List<DamageComponentDto>? Damage { get; set; }
    public int? Amount { get; set; }
    public bool? RestoreToMaximum { get; set; }
}
