using Miaosoft.Sim.Combat.Skill.Content.Dto;
using Miaosoft.Sim.Combat.Skill.Expression;

namespace Miaosoft.Sim.Combat.Skill.Content.Parser;

internal static class PropertyModifierCompiler
{
    public static PropertyModifier[] Compile(IEnumerable<PropertyModifierDto> modifiers, AbilityParserContext context)
    {
        var result = new List<PropertyModifier>();
        foreach (PropertyModifierDto modifier in modifiers)
        {
            if (modifier == null || string.IsNullOrWhiteSpace(modifier.Target) ||
                string.IsNullOrWhiteSpace(modifier.Formula))
                throw new InvalidDataException("Every property modifier requires a target and formula.");
            result.Add(new PropertyModifier(
                context.PropertyTargets.Require(modifier.Target),
                NumericFormula<PropertyFormulaContext>.Compile(
                    modifier.Formula, context.PropertyFormulas, modifier.Fallback)));
        }
        return result.ToArray();
    }
}
