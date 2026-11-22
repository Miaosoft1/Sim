using System.Collections.Immutable;
using Miaosoft.Sim.Loadout.Inventory.Model;

namespace Miaosoft.Sim.Loadout.Content.Inventory.Model;

public sealed class ItemCatalog
{
    internal ItemCatalog(IEnumerable<ItemDefinition> definitions) =>
        Definitions = definitions.ToImmutableDictionary(definition => definition.Id, StringComparer.Ordinal);

    public IReadOnlyDictionary<string, ItemDefinition> Definitions { get; }

    public ItemDefinition Require(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Definitions.TryGetValue(id, out ItemDefinition? definition)
            ? definition : throw new KeyNotFoundException($"Item '{id}' is not declared.");
    }
}
