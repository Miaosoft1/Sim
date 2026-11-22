using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.Content.Dto;
using Miaosoft.Sim.Combat.Skill.Content.Model;

namespace Miaosoft.Sim.Combat.Skill.Content.Parser;

internal static class AbilityGateCompiler
{
    public static AbilityGateDefinition? Compile(AbilityGateDto? data, string abilityId, AbilityParserContext context)
    {
        if (data == null) return null;
        TimeSpan? windup = AbilityDuration.ParseSeconds(data.WindupSeconds, "windupSeconds", allowZero: true);
        TimeSpan? cooldown = AbilityDuration.ParseSeconds(data.CooldownSeconds, "cooldownSeconds", allowZero: true);
        if (data.ResourceCost < 0) throw new InvalidDataException("A resource cost cannot be negative.");
        CastingBehavior? casting = CompileCasting(data.Casting, windup);
        AbilityChargeConfiguration? charges = CompileCharges(data.Charges);
        var conditional = new List<AbilityChargeConditionalRecoveryDefinition>();
        foreach (AbilityChargeConditionalRecoveryDto entry in data.Charges?.ConditionalRecovery ?? [])
        {
            if (entry?.Amount is not > 0 || entry.Trigger == null)
                throw new InvalidDataException("A conditional charge recovery requires a positive amount and trigger.");
            AbilityTriggerDefinition trigger = context.CompileTrigger(entry.Trigger, abilityId);
            if (trigger.Type == TriggerType.None)
                throw new InvalidDataException("A conditional charge recovery requires an event trigger.");
            conditional.Add(new AbilityChargeConditionalRecoveryDefinition(entry.Amount.Value, trigger));
        }
        return new AbilityGateDefinition(
            windup, cooldown, data.ResourceCost, data.OccupiesWindup ?? true, charges, conditional.AsReadOnly(), casting);
    }

    private static CastingBehavior? CompileCasting(AbilityCastingDto? data, TimeSpan? windup)
    {
        if (data == null) return null;
        TimeSpan? duration = AbilityDuration.ParseSeconds(data.DurationSeconds, "casting.durationSeconds", allowZero: true);
        if (data.CanMove == null || data.CanInterrupt == null || duration == null && windup == null ||
            duration.HasValue && windup.HasValue && duration != windup)
            throw new InvalidDataException(
                "Casting requires canMove, canInterrupt and a non-negative duration matching windupSeconds.");
        TimeSpan castDuration = duration ?? windup!.Value;
        TimeSpan? channel = AbilityDuration.ParseSeconds(
            data.ChannelDurationSeconds, "casting.channelDurationSeconds", allowZero: true);
        return new CastingBehavior(data.CanMove.Value, data.CanInterrupt.Value, castDuration,
            data.RefundCooldownOnInterrupt, data.RefundCostOnInterrupt, data.InterruptOnDamage ?? false)
        {
            ChannelDuration = channel ?? TimeSpan.Zero
        };
    }

    private static AbilityChargeConfiguration? CompileCharges(AbilityChargeDto? data)
    {
        if (data == null) return null;
        if (data.Maximum is not { } maximum || maximum <= 0 ||
            data.Initial is not { } initial || initial < 0 || initial > maximum)
            throw new InvalidDataException("Finite charges require a positive maximum and an initial count within the maximum.");
        if (data.Recovery == null) return new AbilityChargeConfiguration(maximum, initial);
        TimeSpan? interval = AbilityDuration.ParseSeconds(data.Recovery.IntervalSeconds, "recovery.intervalSeconds", allowZero: false);
        if (!interval.HasValue || !Enum.TryParse(data.Recovery.Mode, true, out AbilityChargeRecoveryMode mode) ||
            !Enum.IsDefined(mode) || !string.Equals(data.Recovery.Mode, mode.ToString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Charge recovery requires an interval and an explicit Serial or Parallel mode.");
        return new AbilityChargeConfiguration(maximum, initial, interval, mode);
    }
}
