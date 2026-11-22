using Miaosoft.Sim.Combat.Skill.Content.Dto;
using Miaosoft.Sim.Combat.Skill.Expression;

namespace Miaosoft.Sim.Combat.Skill.Content.Parser;

internal static class DamageComponentCompiler
{
    public static DamageDefinition[] Compile(List<DamageComponentDto>? components, AbilityParserContext context)
    {
        if (components is not { Count: > 0 })
            throw new InvalidDataException("Damage requires at least one component.");
        DamageTypeRegistry types = context.RequireSection<DamageTypeRegistry>("damageType");
        return components.Select(component =>
        {
            if (component == null || string.IsNullOrWhiteSpace(component.Type) ||
                string.IsNullOrWhiteSpace(component.Formula))
                throw new InvalidDataException("Every damage component requires a type and formula.");
            return new DamageDefinition(types, component.Type,
                NumericFormula<CombatFormulaContext>.Compile(
                    component.Formula, context.CombatFormulas, component.Fallback), component.Priority);
        }).ToArray();
    }
}
