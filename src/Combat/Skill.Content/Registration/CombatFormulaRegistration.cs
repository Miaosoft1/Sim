using Miaosoft.Sim.Combat.Skill.Expression;
using Miaosoft.Sim.Content.Core;

namespace Miaosoft.Sim.Combat.Skill.Content.Registration;

public static class CombatFormulaRegistration
{
    public static ExpressionSchema<CombatFormulaContext> CombatFormulas(this ContentSectionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.GetOrAddRegistration(
            static () => new ExpressionSchema<CombatFormulaContext>(),
            static schema => schema.Seal());
    }
}
