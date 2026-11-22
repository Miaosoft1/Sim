namespace Miaosoft.Sim.Combat.Skill.Content.Dto;

internal sealed class DamageTypeDto
{
    public DamageTypeDto() { }
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string? Description { get; set; }
    public DamageFormulaDto? Mitigation { get; set; }
}
