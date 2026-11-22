using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Inventory.Model;

namespace Miaosoft.Sim.Loadout.Inventory.Policy;

internal static class InventoryTransfer
{
    public static AbilitySourceAssignment Stage(
        Context context, AtomicTransaction transaction, ItemPlacement placement,
        Unit oldOwner, Unit newOwner, string? destinationSlotId)
    {
        ItemInstance item = placement.Item;
        ItemInstance.State current = transaction.Read(item.Runtime);
        if (!ReferenceEquals(current.Owner, oldOwner) || current.Destroyed ||
            !ReferenceEquals(current.Context, context) ||
            !ReferenceEquals(item.Source.Holder, placement.SlotId != null ? oldOwner : null))
            throw new InvalidOperationException("The item's physical owner and source authority disagree.");
        transaction.Stage(item.Runtime, current with { Owner = newOwner });
        return new(item.Source, destinationSlotId != null ? newOwner : null, item.Source.Revision);
    }
}
