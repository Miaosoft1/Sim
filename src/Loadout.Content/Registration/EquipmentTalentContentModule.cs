using Miaosoft.Sim.Content.Core;
using Miaosoft.Sim.Loadout.Content.Inventory.Model;
using Miaosoft.Sim.Loadout.Content.Talents.Dto;
using Miaosoft.Sim.Loadout.Content.Talents.Model;
using Miaosoft.Sim.Loadout.Talents.Model;

namespace Miaosoft.Sim.Loadout.Content.Registration;

internal sealed class EquipmentTalentContentModule : IContentModule
{
    public string Id => LoadoutContentServiceCollectionExtensions.EquipmentTalentModuleId;

    public void Register(ContentSectionRegistry registry)
    {
        var authored = new Dictionary<string, (EquipmentTalentBindingDto Dto, ContentReadContext Source)>(StringComparer.Ordinal);
        registry.RegisterCollection("equipmentTalent", (yaml, source) =>
        {
            List<EquipmentTalentBindingDto> bindings = source.Deserialize<List<EquipmentTalentBindingDto>>(yaml)
                ?? throw new InvalidDataException($"{source.SourceFile}: equipmentTalent must be a list.");
            foreach (EquipmentTalentBindingDto dto in bindings)
                if (dto == null || string.IsNullOrWhiteSpace(dto.ItemId) ||
                    !authored.TryAdd(dto.ItemId, (dto, source)))
                    throw new InvalidDataException($"{source.SourceFile}: equipmentTalent requires distinct item IDs.");
        }, () =>
        {
            var bindings = new Dictionary<string, IReadOnlyList<TalentTreeDefinition>>(StringComparer.Ordinal);
            ItemCatalog items = registry.Get<ItemCatalog>("item");
            TalentTreeCatalog trees = registry.Get<TalentTreeCatalog>("talentTree");
            foreach ((EquipmentTalentBindingDto dto, ContentReadContext source) in authored.Values)
            {
                if (!items.Definitions.ContainsKey(dto.ItemId!) || dto.TreeIds is not { Count: > 0 } ||
                    dto.TreeIds.Any(id => string.IsNullOrWhiteSpace(id) || !trees.Definitions.ContainsKey(id)) ||
                    dto.TreeIds.Distinct(StringComparer.Ordinal).Count() != dto.TreeIds.Count)
                    throw new InvalidDataException($"{source.SourceFile}: item '{dto.ItemId}' needs an existing item and distinct existing trees.");
                bindings.Add(dto.ItemId!, Array.AsReadOnly(dto.TreeIds.Select(trees.Require).ToArray()));
            }
            return new EquipmentTalentCatalog(bindings);
        }, "item", "talentTree");
    }
}
