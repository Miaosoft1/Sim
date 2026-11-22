using Miaosoft.Sim.Combat.Skill.Expression;

namespace Miaosoft.Sim.Combat.Skill;

public sealed class PropertyModifier
{
    public PropertyModifier(PropertyModifierTarget target, NumericFormula<PropertyFormulaContext> formula)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(formula);
        Target = target;
        Formula = formula;
    }

    public PropertyModifierTarget Target { get; }
    public NumericFormula<PropertyFormulaContext> Formula { get; }
}
