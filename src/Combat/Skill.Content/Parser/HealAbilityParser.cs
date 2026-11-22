using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.Content.Dto;
using Miaosoft.Sim.Combat.Skill.Content.Model;

namespace Miaosoft.Sim.Combat.Skill.Content.Parser;

internal sealed class HealAbilityParser : IHandlerParser
{
    public string Type => "Heal";

    public AbilityHandlerDefinition Compile(AbilityContentDefinition definition, string? yaml, AbilityParserContext context)
    {
        if (definition.Trigger.Type != TriggerType.ProjectileHit)
            throw new InvalidDataException("Heal requires a ProjectileHit trigger.");
        HealAbilityDto data = context.Deserialize<HealAbilityDto>(
            yaml ?? throw new InvalidDataException("Heal requires data."))
            ?? throw new InvalidDataException("Heal requires data.");
        if (data.Amount is < 0 || data.RestoreToMaximum == data.Amount.HasValue)
            throw new InvalidDataException("Heal requires either a non-negative amount or restoreToMaximum.");

        int amount = data.Amount ?? 0;
        bool restore = data.RestoreToMaximum;
        AbilityGateDefinition? gate = AbilityGateCompiler.Compile(data.Gate, definition.Id, context);
        TargetSelectorDefinition? selector = TargetSelectorCompiler.Compile(data.Selector);
        return new AbilityHandlerDefinition(definition, Type, (_, policy) =>
            AbilityHandlerFactory.WithTrigger(new HealAbility
            {
                Id = definition.Id,
                Name = definition.Name,
                Priority = definition.Priority,
                FormulaPolicy = policy,
                BaseHeal = amount,
                RestoreToMaximum = restore,
                Windup = gate?.Windup,
                GateManager = gate?.Create(),
                Selector = selector?.Create()
            }, definition, gate));
    }
}
