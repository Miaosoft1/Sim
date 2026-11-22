using Miaosoft.Sim.Combat.Skill.Content.Dto;

namespace Miaosoft.Sim.Combat.Skill.Content.Parser;

internal static class StatusPeriodicEffectCompiler
{
    public static IReadOnlyList<StatusPeriodicEffectDefinition> Compile(
        string id, List<StatusPeriodicEffectDto>? effects, AbilityParserContext context)
    {
        if (effects == null) return [];
        if (effects.Count == 0)
            throw new InvalidDataException("periodicEffects requires at least one effect.");
        return Array.AsReadOnly(effects.Select(effect =>
        {
            if (effect?.Tick?.TickCount is not > 0)
                throw new InvalidDataException("A status periodic effect requires a positive tick count.");
            TimeSpan interval = AbilityDuration.ParseSeconds(
                effect.Tick.TickIntervalSeconds, "periodicEffects.tick.tickIntervalSeconds", allowZero: false)
                ?? throw new InvalidDataException("A status periodic effect requires a tick interval.");
            TimeSpan first = AbilityDuration.ParseSeconds(
                effect.Tick.FirstTickDelaySeconds, "periodicEffects.tick.firstTickDelaySeconds", allowZero: true)
                ?? TimeSpan.Zero;
            if (string.Equals(effect.Type, "Damage", StringComparison.OrdinalIgnoreCase))
            {
                if (effect.Amount.HasValue || effect.RestoreToMaximum.HasValue)
                    throw new InvalidDataException("Periodic damage cannot declare healing fields.");
                return StatusPeriodicEffectDefinition.ForDamage(id, effect.Tick.TickCount.Value, interval, first,
                    DamageComponentCompiler.Compile(effect.Damage, context));
            }
            if (string.Equals(effect.Type, "Heal", StringComparison.OrdinalIgnoreCase))
            {
                if (effect.Damage != null || effect.Amount is < 0 ||
                    (effect.RestoreToMaximum == true) == effect.Amount.HasValue)
                    throw new InvalidDataException("Periodic healing requires either a non-negative amount or restoreToMaximum.");
                return StatusPeriodicEffectDefinition.ForHeal(id, effect.Tick.TickCount.Value, interval, first,
                    effect.Amount ?? 0, effect.RestoreToMaximum == true);
            }
            throw new InvalidDataException($"Unknown status periodic effect type '{effect.Type}'.");
        }).ToArray());
    }
}
