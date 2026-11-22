using System.Collections.ObjectModel;
using Miaosoft.Sim.Loadout.Talents.Model;
using Miaosoft.Sim.Loadout.Talents.Policy;

namespace Miaosoft.Sim.Loadout.Content.Talents.Model;

public sealed class EquipmentTalentCatalog
{
    internal EquipmentTalentCatalog(IReadOnlyDictionary<string, IReadOnlyList<TalentTreeDefinition>> bindings)
    {
        Bindings = new ReadOnlyDictionary<string, IReadOnlyList<TalentTreeDefinition>>(
            bindings.ToDictionary(pair => pair.Key,
                pair => (IReadOnlyList<TalentTreeDefinition>)Array.AsReadOnly(pair.Value.ToArray()), StringComparer.Ordinal));
    }

    public IReadOnlyDictionary<string, IReadOnlyList<TalentTreeDefinition>> Bindings { get; }
    public EquipmentTalentGrantPreparation CreatePreparation(UnitTalents talents) => new(talents, Bindings);
}
