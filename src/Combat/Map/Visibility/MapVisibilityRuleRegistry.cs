namespace Miaosoft.Sim.Combat.Map.Visibility;

internal sealed class MapVisibilityRuleRegistry
{
    private readonly Dictionary<IMapVisibilityRule, int> rules = new(ReferenceEqualityComparer.Instance);
    internal event Action? Changed;

    public IReadOnlyCollection<IMapVisibilityRule> Rules => rules.Keys.ToArray();

    public IDisposable Register(IMapVisibilityRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        rules[rule] = rules.GetValueOrDefault(rule) + 1;
        Changed?.Invoke();
        return new Lease(this, rule);
    }

    private sealed class Lease(MapVisibilityRuleRegistry registry, IMapVisibilityRule rule) : IDisposable
    {
        private MapVisibilityRuleRegistry? owner = registry;

        public void Dispose()
        {
            MapVisibilityRuleRegistry? current = Interlocked.Exchange(ref owner, null);
            if (current == null) return;
            int count = current.rules[rule] - 1;
            if (count == 0) current.rules.Remove(rule);
            else current.rules[rule] = count;
            current.Changed?.Invoke();
        }
    }
}
