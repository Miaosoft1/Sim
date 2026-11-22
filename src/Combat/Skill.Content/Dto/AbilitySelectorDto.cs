namespace Miaosoft.Sim.Combat.Skill.Content.Dto;

public sealed class AbilitySelectorDto
{
    public string? Type { get; set; }
    public string? Origin { get; set; }
    public string? Filter { get; set; }
    public string? Group { get; set; }
    public double? Range { get; set; }
    public double? Width { get; set; }
    public double? Radius { get; set; }
    public double? CastRange { get; set; }
    public bool? StopAtFirstTarget { get; set; }
    public int? MaxTargets { get; set; }
}
