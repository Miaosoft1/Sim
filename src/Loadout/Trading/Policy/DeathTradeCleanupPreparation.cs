using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Inventory.Model;
using Miaosoft.Sim.Loadout.Inventory.Policy;
using Miaosoft.Sim.Loadout.Trading.EventData;
using Miaosoft.Sim.Loadout.Trading.Model;

namespace Miaosoft.Sim.Loadout.Trading.Policy;

internal sealed class DeathTradeCleanupPreparation(DeathTradeCleanupRequestEvent request) : IAbilitySourceChangePreparation
{
    public AbilitySourceChangePlan Prepare(Context context, Unit coordinator, Guid operationId, AtomicTransaction transaction)
    {
        DeathTradeDirectory directory = request.Directory;
        UnitInventory inventory = directory.Inventory;
        DeathTradeAccess.ValidateDirectory(context, directory);
        if (!ReferenceEquals(context, request.Context) || !ReferenceEquals(coordinator, inventory.Owner) ||
            request.Receiver != coordinator.EventReceiver || operationId != request.OperationId || operationId == Guid.Empty)
            throw new InvalidOperationException("Cleanup must address its death directory and owner.");
        DeathTradeDirectory.State state = transaction.Read(directory.Runtime);
        DeathTradeFailure failure = state.Closed ? DeathTradeFailure.Closed :
            state.DeathId != request.ExpectedDeathId || coordinator.CurrentDeathId != request.ExpectedDeathId ||
            coordinator.IsAlive ? DeathTradeFailure.NotAuthorized : DeathTradeFailure.None;
        Event result = DeathTradeAccess.Result(request, operationId, DeathTradeOperation.Cleanup,
            request.ExpectedDeathId, failure, coordinator);
        if (failure != DeathTradeFailure.None)
            return new() { ShouldCommit = false, Completions = [result] };
        ItemPlacement[] before = transaction.Read(inventory.Contents);
        foreach (ItemPlacement placement in before)
        {
            ItemInstance.State item = transaction.Read(placement.Item.Runtime);
            if (!ReferenceEquals(item.Owner, coordinator) || item.Destroyed || !ReferenceEquals(item.Context, context) ||
                !ReferenceEquals(placement.Item.Source.Holder, placement.SlotId != null ? coordinator : null))
                throw new InvalidOperationException("Cleanup found an inventory item without matching ownership.");
            transaction.Stage(placement.Item.Runtime, item with { Owner = null, Destroyed = true });
        }
        transaction.Stage(inventory.Contents, []);
        transaction.Stage(directory.Runtime, state with { Closed = true });
        return EquipmentPreparation.Compose(new()
        {
            Assignments = before.Select(placement => new AbilitySourceAssignment(
                placement.Item.Source, null, placement.Item.Source.Revision)).ToArray(),
            Completions = [result]
        }, EquipmentPreparation.Prepare(request, operationId, inventory, before, [], transaction));
    }
}
