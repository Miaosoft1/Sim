using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.Content.Dto;
using Miaosoft.Sim.Combat.Skill.Content.Model;

namespace Miaosoft.Sim.Combat.Skill.Content.Parser;

internal sealed class StatusAbilityParser : IHandlerParser
{
    public string Type => "Status";

    public AbilityHandlerDefinition Compile(AbilityContentDefinition definition, string? yaml, AbilityParserContext context)
    {
        StatusAbilityDto data = context.Deserialize<StatusAbilityDto>(
            yaml ?? throw new InvalidDataException("Status requires data."))
            ?? throw new InvalidDataException("Status requires data.");
        AbilityStackPolicy stackPolicy = ParseEnum(data.StackPolicy, AbilityStackPolicy.ExtendAndStack, "stackPolicy");
        AbilityTimeBasis timeBasis = ParseEnum(data.TimeBasis, AbilityTimeBasis.Simulation, "timeBasis");
        TimeSpan duration = AbilityDuration.ParseSeconds(data.DurationSeconds, "durationSeconds", allowZero: false)
            ?? TimeSpan.Zero;
        if (data.StackLimit is <= 0 ||
            data.StackLimit.HasValue && stackPolicy is not (AbilityStackPolicy.StackOnly or AbilityStackPolicy.ExtendAndStack))
            throw new InvalidDataException("stackLimit requires a positive value and a stacking policy.");
        if (timeBasis == AbilityTimeBasis.RealTime &&
            (duration == TimeSpan.Zero || stackPolicy != AbilityStackPolicy.Independent) ||
            data.ProtectedUntilExpiry && timeBasis != AbilityTimeBasis.RealTime)
            throw new InvalidDataException("A real-time or protected status requires an independent real-time lease with positive duration.");
        PropertyModifier[] compiled = PropertyModifierCompiler.Compile(data.Modifiers ?? [], context);
        PropertyRecalculationPolicy recalculateOn = PropertyRecalculationCompiler.Compile(data.RecalculateOn);
        IReadOnlyList<StatusPeriodicEffectDefinition> periodic =
            StatusPeriodicEffectCompiler.Compile(definition.Id, data.PeriodicEffects, context);
        int? stackLimit = data.StackLimit;
        bool protectedUntilExpiry = data.ProtectedUntilExpiry;
        AbilityGateDefinition? gate = AbilityGateCompiler.Compile(data.Gate, definition.Id, context);
        TargetSelectorDefinition? selector = TargetSelectorCompiler.Compile(data.Selector);
        return new AbilityHandlerDefinition(definition, Type, (_, policy) =>
            AbilityHandlerFactory.WithTrigger(new StatusAbility
            {
                Id = definition.Id,
                Name = definition.Name,
                Priority = definition.Priority,
                FormulaPolicy = policy,
                Labels = AbilityLabel.Status,
                IsTriggerListener = true,
                PayloadId = definition.Id,
                Duration = duration,
                TimeBasis = timeBasis,
                StackPolicy = stackPolicy,
                Stack = stackLimit,
                ProtectedUntilExpiry = protectedUntilExpiry,
                Modifiers = compiled.ToList(),
                RecalculateOn = recalculateOn,
                PeriodicEffectDefinitions = periodic,
                Windup = gate?.Windup,
                GateManager = gate?.Create(),
                Selector = selector?.Create()
            }, definition, gate));
    }

    private static T ParseEnum<T>(string? text, T defaultValue, string field) where T : struct, Enum
    {
        if (text == null) return defaultValue;
        if (!Enum.TryParse(text, true, out T parsed) || !Enum.IsDefined(parsed) ||
            !string.Equals(text, parsed.ToString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Invalid {field} '{text}'.");
        return parsed;
    }
}
