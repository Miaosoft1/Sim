using Miaosoft.Sim.Combat.Skill.Expression;
using Miaosoft.Sim.Content.Core;

namespace Miaosoft.Sim.Combat.Skill.Content.Registration;

public static class PropertyFormulaRegistration
{
    public static ExpressionSchema<PropertyFormulaContext> PropertyFormulas(this ContentSectionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.GetOrAddRegistration(
            static () => new ExpressionSchema<PropertyFormulaContext>(),
            static schema => schema.Seal());
    }
}
