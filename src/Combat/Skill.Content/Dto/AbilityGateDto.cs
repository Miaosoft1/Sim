namespace Miaosoft.Sim.Combat.Skill.Content.Dto;

public sealed class AbilityGateDto
{
    public double? WindupSeconds { get; set; }
    public AbilityCastingDto? Casting { get; set; }
    public double? CooldownSeconds { get; set; }
    public int? ResourceCost { get; set; }
    public bool? OccupiesWindup { get; set; }
    public AbilityChargeDto? Charges { get; set; }
}

public sealed class AbilityChargeDto
{
    public int? Maximum { get; set; }
    public int? Initial { get; set; }
    public AbilityChargeRecoveryDto? Recovery { get; set; }
    public List<AbilityChargeConditionalRecoveryDto>? ConditionalRecovery { get; set; }
}

public sealed class AbilityCastingDto
{
    public bool? CanMove { get; set; }
    public bool? CanInterrupt { get; set; }
    public double? DurationSeconds { get; set; }
    public double? ChannelDurationSeconds { get; set; }
    public bool RefundCooldownOnInterrupt { get; set; }
    public bool RefundCostOnInterrupt { get; set; }
    public bool? InterruptOnDamage { get; set; }
}

public sealed class AbilityChargeConditionalRecoveryDto
{
    public int? Amount { get; set; }
    public AbilityTriggerDto? Trigger { get; set; }
}

public sealed class AbilityChargeRecoveryDto
{
    public double? IntervalSeconds { get; set; }
    public string? Mode { get; set; }
}
