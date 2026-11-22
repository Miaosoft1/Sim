using Miaosoft.Sim.Content.Core;

namespace Miaosoft.Sim.Combat.Skill.Content.Registration;

public static class TriggerConditionRegistration
{
    public static TriggerConditionSchemas TriggerConditions(this ContentSectionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.GetOrAddRegistration(
            static () => new TriggerConditionSchemas(), static schemas => schemas.Seal());
    }
}
