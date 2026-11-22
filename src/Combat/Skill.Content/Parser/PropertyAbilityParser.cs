using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.Content.Dto;
using Miaosoft.Sim.Combat.Skill.Content.Model;

namespace Miaosoft.Sim.Combat.Skill.Content.Parser;

internal sealed class PropertyAbilityParser : IHandlerParser
{
    public string Type => "BaseProperty";

    public AbilityHandlerDefinition Compile(AbilityContentDefinition definition, string? yaml, AbilityParserContext context)
    {
        if (definition.Trigger.Type != TriggerType.None)
            throw new InvalidDataException("BaseProperty requires a None trigger.");
        PropertyAbilityDto data = context.Deserialize<PropertyAbilityDto>(
            yaml ?? throw new InvalidDataException("BaseProperty requires data."))
            ?? throw new InvalidDataException("BaseProperty requires data.");
        if (data.Modifiers is not { Count: > 0 })
            throw new InvalidDataException("BaseProperty requires at least one modifier.");
        PropertyModifier[] modifiers = PropertyModifierCompiler.Compile(data.Modifiers, context);
        PropertyRecalculationPolicy recalculateOn = PropertyRecalculationCompiler.Compile(data.RecalculateOn);
        return new AbilityHandlerDefinition(definition, Type, (_, policy) =>
            AbilityHandlerFactory.WithTrigger(new PropertyAbility
            {
                Id = definition.Id,
                Name = definition.Name,
                Priority = definition.Priority,
                FormulaPolicy = policy,
                Modifiers = modifiers.ToList(),
                RecalculateOn = recalculateOn
            }, definition));
    }
}
