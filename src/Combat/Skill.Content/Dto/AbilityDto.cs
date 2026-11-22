namespace Miaosoft.Sim.Combat.Skill.Content.Dto;

public sealed class AbilityDto
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Type { get; set; }
    public bool? AllowMultiple { get; set; }
    public int Priority { get; set; }
    public AbilityTriggerDto? Trigger { get; set; }
}

public sealed class AbilityTriggerDto
{
    public string? Type { get; set; }
    public string? EventSourceScope { get; set; }
    public string? DeathPhase { get; set; }
    public string? SignalId { get; set; }
    public List<string>? SourceAbilityIds { get; set; }
    public double? Probability { get; set; }
    public List<TriggerConditionDto>? Conditions { get; set; }
}
