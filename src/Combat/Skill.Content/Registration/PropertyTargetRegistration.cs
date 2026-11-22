using Miaosoft.Sim.Content.Core;

namespace Miaosoft.Sim.Combat.Skill.Content.Registration;

public sealed class PropertyTargetRegistration
{
    private readonly Dictionary<string, PropertyModifierTarget> targets = new(StringComparer.Ordinal);
    private bool sealedRegistration;

    public void Register(PropertyModifierTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (sealedRegistration) throw new InvalidOperationException("Property target registration is sealed.");
        if (!targets.TryAdd(target.Id, target))
            throw new InvalidOperationException($"Property target '{target.Id}' is already registered.");
    }

    internal void Seal() => sealedRegistration = true;

    internal PropertyModifierTarget Require(string id) =>
        targets.TryGetValue(id, out PropertyModifierTarget? target) ? target :
        throw new InvalidDataException($"No selected content module provides property target '{id}'.");
}

public static class PropertyTargetRegistrationExtensions
{
    public static PropertyTargetRegistration PropertyTargets(this ContentSectionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.GetOrAddRegistration(
            static () => new PropertyTargetRegistration(), static registration => registration.Seal());
    }
}
