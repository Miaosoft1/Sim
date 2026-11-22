using System.Runtime.CompilerServices;

namespace Miaosoft.Sim.Combat.Skill.Ability;

internal static class AbilitySourceRegistry
{
    private sealed record Registration(Context Context, Guid InstanceId, string Category);
    private sealed class ContextSources
    {
        public Dictionary<Guid, AtomicCell<IAbilitySource?>> Slots { get; } = new();
    }

    private static readonly ConditionalWeakTable<IAbilitySource, AtomicCell<Registration?>> sources = new();
    private static readonly ConditionalWeakTable<Context, ContextSources> contexts = new();

    public static void Stage(
        AtomicTransaction transaction, Context context, IAbilitySource source, Guid instanceId, string category)
    {
        AtomicCell<Registration?> sourceCell = sources.GetValue(source, _ => new(null));
        Registration? registration = transaction.Read(sourceCell);
        if (registration != null && (!ReferenceEquals(registration.Context, context) ||
            registration.InstanceId != instanceId || registration.Category != category))
            throw new InvalidOperationException("The ability source belongs to another context or changed its identity.");

        ContextSources registry = contexts.GetValue(context, _ => new());
        if (!registry.Slots.TryGetValue(instanceId, out AtomicCell<IAbilitySource?>? slot))
            registry.Slots.Add(instanceId, slot = new(null));
        IAbilitySource? existing = transaction.Read(slot);
        if (existing != null && !ReferenceEquals(existing, source))
            throw new InvalidOperationException("The ability source ID is already registered to another object in this context.");

        if (registration == null)
            transaction.Stage(sourceCell, new Registration(context, instanceId, category));
        if (existing == null)
            transaction.Stage(slot, source);
    }
}
