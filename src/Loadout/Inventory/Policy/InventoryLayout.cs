using Miaosoft.Sim.Loadout.Inventory.EventData;
using Miaosoft.Sim.Loadout.Inventory.Model;

namespace Miaosoft.Sim.Loadout.Inventory.Policy;

internal static class InventoryLayout
{
    internal static InventoryChangeFailure Validate(
        IReadOnlyList<ItemPlacement> items, InventoryConfiguration configuration)
    {
        if (items.Any(placement => placement.SlotId is { } slot &&
            (!configuration.Slots.TryGetValue(slot, out string? type) || type != placement.Item.Definition.SlotType)) ||
            items.Where(placement => placement.SlotId != null).Select(placement => placement.SlotId)
                .Distinct(StringComparer.Ordinal).Count() != items.Count(placement => placement.SlotId != null))
            return InventoryChangeFailure.InvalidSlot;
        return items.Count > configuration.CarryCapacity ||
            items.Count(placement => placement.SlotId == null) > configuration.BackpackCapacity
                ? InventoryChangeFailure.CapacityExceeded : InventoryChangeFailure.None;
    }
}
