using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Inventory.EventData;
using Miaosoft.Sim.Loadout.Inventory.Model;

namespace Miaosoft.Sim.Loadout.Inventory.Policy;

internal sealed class ItemCreationPreparation(ItemCreateRequestEvent request, UnitInventory inventory)
    : IAbilitySourceChangePreparation
{
    public AbilitySourceChangePlan Prepare(
        Context context, Unit coordinator, Guid operationId, AtomicTransaction transaction)
    {
        if (!ReferenceEquals(context, request.Context) || !ReferenceEquals(coordinator, request.Coordinator) ||
            operationId != request.OperationId || operationId == Guid.Empty)
            throw new InvalidOperationException("Item creation requires its original request and coordinator.");
        InventoryAccess.Validate(context, coordinator, inventory, request.Receiver);
        var item = new ItemInstance(request.Definition);
        transaction.Stage(item.Runtime, new ItemInstance.State(context, null, false));
        return new()
        {
            Materialize = [item.Source],
            Completions =
            [
                context.Factory.NewEvent<ItemCreatedEvent>() with
                {
                    OperationId = operationId, Item = item,
                    Receiver = coordinator.EventReceiver, IsScopeDetached = true
                }
            ]
        };
    }
}
