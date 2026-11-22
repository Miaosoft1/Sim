using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Inventory.Model;
using Miaosoft.Sim.Loadout.Inventory.Policy;
using Miaosoft.Sim.Loadout.Trading.Handler;
using Miaosoft.Sim.Loadout.Trading.Model;

namespace Miaosoft.Sim.Loadout.Trading.Policy;

internal static class TradeAccess
{
    public static void Validate(Context context, TradeSession session)
    {
        foreach (UnitInventory inventory in new[] { session.First, session.Second })
        {
            InventoryAccess.Validate(context, inventory.Owner, inventory, inventory.Owner.EventReceiver);
            if (inventory.Owner.EventReceiver.GetHandlers<TradeHandler>() is not [var handler] ||
                !ReferenceEquals(handler.Inventory, inventory))
                throw new InvalidOperationException("Each trade participant requires its unique current inventory-bound trade feature.");
        }
    }
}
