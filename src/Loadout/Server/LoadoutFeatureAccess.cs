using Miaosoft.Sim.Loadout.Inventory.Handler;
using Miaosoft.Sim.Loadout.Inventory.Model;
using Miaosoft.Sim.Loadout.Talents.Handler;
using Miaosoft.Sim.Loadout.Talents.Model;
using Miaosoft.Sim.Loadout.Trading.Handler;
using Miaosoft.Sim.Loadout.Trading.Model;

namespace Miaosoft.Sim.Loadout.Server;

internal static class LoadoutFeatureAccess
{
    public static bool Inventory(UnitInventory? inventory) => inventory != null &&
        inventory.Owner.EventReceiver.GetHandlers<InventoryHandler>() is [var handler] &&
        ReferenceEquals(handler.Inventory, inventory);

    public static bool Trading(UnitInventory? inventory) => Inventory(inventory) &&
        inventory!.Owner.EventReceiver.GetHandlers<TradeHandler>() is [var handler] &&
        ReferenceEquals(handler.Inventory, inventory);

    public static bool Talents(UnitTalents? talents) => talents != null &&
        talents.Owner.EventReceiver.GetHandlers<TalentDirectoryHandler>() is [var handler] &&
        ReferenceEquals(handler.Talents, talents);

    public static bool Death(DeathTradeDirectory directory) => Inventory(directory.Inventory) &&
        directory.Inventory.Owner.EventReceiver.GetHandlers<DeathTradeHandler>() is [var handler] &&
        ReferenceEquals(handler.Directory, directory);
}
