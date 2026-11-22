namespace Miaosoft.Sim.Loadout.Content.Inventory.Dto;

public sealed class ItemDto
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? SlotType { get; set; }
    public List<ItemAbilityDto>? Abilities { get; set; } = [];
}
