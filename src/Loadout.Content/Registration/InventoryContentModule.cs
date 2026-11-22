using Miaosoft.Sim.Content.Core;
using Miaosoft.Sim.Loadout.Content.Inventory.Dto;
using Miaosoft.Sim.Loadout.Content.Inventory.Model;
using Miaosoft.Sim.Loadout.Content.Inventory.Parser;
using Miaosoft.Sim.Loadout.Inventory.Model;

namespace Miaosoft.Sim.Loadout.Content.Registration;

internal sealed class InventoryContentModule(ItemParser parser) : IContentModule
{
    public string Id => LoadoutContentServiceCollectionExtensions.InventoryModuleId;

    public void Register(ContentSectionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var authored = new Dictionary<string, (ItemDto Dto, ContentReadContext Source)>(StringComparer.Ordinal);
        registry.RegisterReferenceResolver("item", id => registry.Get<ItemCatalog>("item").Definitions.ContainsKey(id));
        registry.RegisterCollection("item", (yaml, source) =>
        {
            List<ItemDto> items = source.Deserialize<List<ItemDto>>(yaml)
                ?? throw new InvalidDataException($"{source.SourceFile}: item section must be a list.");
            foreach (ItemDto dto in items)
            {
                if (dto == null || string.IsNullOrWhiteSpace(dto.Id))
                    throw new InvalidDataException($"{source.SourceFile}: every item requires an ID.");
                if (!authored.TryAdd(dto.Id, (dto, source)))
                    throw new InvalidDataException($"{source.SourceFile}: duplicate item '{dto.Id}'.");
            }
        }, () =>
        {
            var definitions = new List<ItemDefinition>(authored.Count);
            foreach ((ItemDto dto, ContentReadContext source) in authored.Values)
            {
                try
                {
                    ItemDefinition definition = parser.Compile(dto);
                    foreach (var member in definition.Abilities)
                        source.RequireReference("ability", member.AbilityId);
                    definitions.Add(definition);
                }
                catch (Exception error) when (error is ArgumentException or InvalidDataException)
                {
                    throw new InvalidDataException($"{source.SourceFile}: item '{dto.Id}': {error.Message}", error);
                }
            }
            return new ItemCatalog(definitions);
        });
    }
}
