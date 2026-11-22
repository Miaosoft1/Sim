using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Inventory.Handler;
using Miaosoft.Sim.Loadout.Inventory.Model;

namespace Miaosoft.Sim.Loadout.Inventory.Policy;

internal static class InventoryAccess
{
    public static void Validate(Context context, Unit owner, UnitInventory inventory, EventReceiver? receiver)
    {
        if (receiver == null || !ReferenceEquals(owner, inventory.Owner) || !ReferenceEquals(receiver, owner.EventReceiver) ||
            !ReferenceEquals(owner.EventReceiver.Context, context) || !context.ContainsReceiver(receiver) ||
            owner.EventReceiver.GetHandlers<InventoryHandler>() is not [var handler] ||
            !ReferenceEquals(handler.Inventory, inventory))
            throw new InvalidOperationException("An inventory operation requires its unique owner-bound handler.");
    }
}
