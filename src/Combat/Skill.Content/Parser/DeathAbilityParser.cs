using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.Content.Model;

namespace Miaosoft.Sim.Combat.Skill.Content.Parser;

internal sealed class DeathAbilityParser : IHandlerParser
{
    public string Type => "Death";

    public AbilityHandlerDefinition Compile(AbilityContentDefinition definition, string? yaml, AbilityParserContext context)
    {
        if (definition.Trigger.Type != TriggerType.None)
            throw new InvalidDataException("Death requires a None trigger.");
        if (yaml != null && context.Deserialize<Dictionary<string, object?>>(yaml) is not { Count: 0 })
            throw new InvalidDataException("Death data must be an empty mapping.");
        return new AbilityHandlerDefinition(definition, Type, (_, policy) =>
            [new DeathAbility
            {
                Id = definition.Id, Name = definition.Name, Priority = definition.Priority, FormulaPolicy = policy
            }]);
    }
}
