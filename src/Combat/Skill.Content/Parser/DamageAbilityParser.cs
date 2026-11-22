using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.Content.Dto;
using Miaosoft.Sim.Combat.Skill.Content.Model;

namespace Miaosoft.Sim.Combat.Skill.Content.Parser;

internal sealed class DamageAbilityParser : IHandlerParser
{
    public string Type => "Damage";

    public AbilityHandlerDefinition Compile(AbilityContentDefinition definition, string? yaml, AbilityParserContext context)
    {
        if (definition.Trigger.Type != TriggerType.ProjectileHit)
            throw new InvalidDataException("Damage requires a ProjectileHit trigger.");
        DamageAbilityDto data = context.Deserialize<DamageAbilityDto>(
            yaml ?? throw new InvalidDataException("Damage requires data."))
            ?? throw new InvalidDataException("Damage requires data.");
        DamageDefinition[] compiled = DamageComponentCompiler.Compile(data.Damage, context);
        AbilityGateDefinition? gate = AbilityGateCompiler.Compile(data.Gate, definition.Id, context);
        TargetSelectorDefinition? selector = TargetSelectorCompiler.Compile(data.Selector);
        int? tickCount = data.Tick?.TickCount;
        TimeSpan? tickInterval = AbilityDuration.ParseSeconds(
            data.Tick?.TickIntervalSeconds, "tick.tickIntervalSeconds", allowZero: false);
        TimeSpan? firstTickDelay = AbilityDuration.ParseSeconds(
            data.Tick?.FirstTickDelaySeconds, "tick.firstTickDelaySeconds", allowZero: true);
        if (data.Tick != null && (tickCount is not > 0 || !tickInterval.HasValue))
            throw new InvalidDataException("Periodic damage requires a positive tick count and interval.");
        return new AbilityHandlerDefinition(definition, Type, (_, policy) =>
            AbilityHandlerFactory.WithTrigger(new DamageAbility
            {
                Id = definition.Id,
                Name = definition.Name,
                Priority = definition.Priority,
                FormulaPolicy = policy,
                Casting = gate?.Casting,
                Windup = gate?.Windup,
                OccupiesWindup = gate?.OccupiesWindup ?? true,
                GateManager = gate?.Create(),
                Selector = selector?.Create(),
                Tick = tickCount.HasValue ? new DamageTick
                {
                    TickCount = tickCount.Value,
                    TickInterval = tickInterval!.Value,
                    FirstTickDelay = firstTickDelay
                } : null,
                Damages = compiled.ToList()
            }, definition, gate));
    }
}
