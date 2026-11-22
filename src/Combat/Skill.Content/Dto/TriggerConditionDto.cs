namespace Miaosoft.Sim.Combat.Skill.Content.Dto;

public sealed class TriggerConditionDto
{
    public string? Type { get; set; }
    public string? Expression { get; set; }
    public string? Relation { get; set; }
    public string? Anchor { get; set; }
    public double? Maximum { get; set; }
}
