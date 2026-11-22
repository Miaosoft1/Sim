using Miaosoft.Sim.Combat.Skill.Content.Dto;
using Miaosoft.Sim.Combat.Skill.Expression;

namespace Miaosoft.Sim.Combat.Skill.Content.Parser;

internal sealed class DamageTypeParser
{
    public DamageTypeDefinition Compile(DamageTypeDto dto, ExpressionSchema<CombatFormulaContext> schema)
    {
        ArgumentNullException.ThrowIfNull(dto);
        ArgumentNullException.ThrowIfNull(schema);
        NumericFormula<CombatFormulaContext>? formula = dto.Mitigation == null
            ? null
            : NumericFormula<CombatFormulaContext>.Compile(
                dto.Mitigation.Formula ?? throw new ArgumentException("Mitigation requires a formula."),
                schema, dto.Mitigation.Fallback);
        return new DamageTypeDefinition(dto.Id, formula, dto.Name, dto.Description);
    }
}
