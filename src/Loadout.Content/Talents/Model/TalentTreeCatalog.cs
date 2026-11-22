using System.Collections.ObjectModel;
using Miaosoft.Sim.Loadout.Talents.Model;

namespace Miaosoft.Sim.Loadout.Content.Talents.Model;

public sealed class TalentTreeCatalog
{
    public TalentTreeCatalog(IEnumerable<TalentTreeDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        Definitions = new ReadOnlyDictionary<string, TalentTreeDefinition>(
            definitions.ToDictionary(definition => definition.Id, StringComparer.Ordinal));
    }

    public IReadOnlyDictionary<string, TalentTreeDefinition> Definitions { get; }
    public TalentTreeDefinition Require(string id) => Definitions.TryGetValue(id, out TalentTreeDefinition? definition)
        ? definition : throw new KeyNotFoundException($"Unknown talent tree '{id}'.");
}
