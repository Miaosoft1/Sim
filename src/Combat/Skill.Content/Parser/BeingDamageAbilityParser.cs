using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.Content.Dto;
using Miaosoft.Sim.Combat.Skill.Content.Model;
using Miaosoft.Sim.Combat.Skill.Expression;

namespace Miaosoft.Sim.Combat.Skill.Content.Parser;

internal sealed class BeingDamageAbilityParser : IHandlerParser
{
    public string Type => "BeingDamage";

    public AbilityHandlerDefinition Compile(AbilityContentDefinition definition, string? yaml, AbilityParserContext context)
    {
        if (definition.Trigger.Type != TriggerType.None)
            throw new InvalidDataException("BeingDamage requires a None trigger.");
        BeingDamageAbilityDto data = yaml == null
            ? new BeingDamageAbilityDto()
            : context.Deserialize<BeingDamageAbilityDto>(yaml)
                ?? throw new InvalidDataException("BeingDamage data cannot be null.");
        DamageTypeRegistry types = context.RequireSection<DamageTypeRegistry>("damageType");
        var formulas = new Dictionary<string, NumericFormula<CombatFormulaContext>>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in data.MitigationTypes ?? [])
        {
            DamageTypeDefinition damageType = types.Require(id);
            NumericFormula<CombatFormulaContext> formula = damageType.Formula
                ?? throw new InvalidDataException($"Damage type '{damageType.Id}' has no mitigation formula.");
            if (!formulas.TryAdd(damageType.Id, formula))
                throw new InvalidDataException($"Duplicate mitigation type '{damageType.Id}'.");
        }
        var compiled = formulas.ToArray();
        return new AbilityHandlerDefinition(definition, Type, (_, policy) =>
        {
            var ability = new BeingDamageAbility
            {
                Id = definition.Id,
                Name = definition.Name,
                Priority = definition.Priority,
                FormulaPolicy = policy,
                DamageTypes = types
            };
            foreach ((string id, NumericFormula<CombatFormulaContext> formula) in compiled)
                ability.SetMitigationFormula(id, formula);
            return [ability];
        });
    }
}
