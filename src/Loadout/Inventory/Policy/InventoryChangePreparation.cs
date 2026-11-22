using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Inventory.EventData;
using Miaosoft.Sim.Loadout.Inventory.Model;

namespace Miaosoft.Sim.Loadout.Inventory.Policy;

internal sealed class InventoryChangePreparation(InventoryChangeRequestEvent request, UnitInventory inventory)
    : IAbilitySourceChangePreparation
{
    public AbilitySourceChangePlan Prepare(
        Context context, Unit coordinator, Guid operationId, AtomicTransaction transaction)
    {
        InventoryChangeRequestEvent ev = request;
        if (!ReferenceEquals(context, ev.Context) || !ReferenceEquals(coordinator, ev.Owner) ||
            operationId != ev.OperationId || ev.OperationId == Guid.Empty || !Enum.IsDefined(ev.Operation))
            throw new InvalidOperationException("An inventory operation requires its original request and owner.");
        InventoryAccess.Validate(context, ev.Owner, inventory, ev.Receiver);
        if (ev.ExpectedRevision is { } revision && revision != inventory.Revision)
            return new() { ShouldCommit = false, Completions = [Result(ev, InventoryChangeFailure.StaleRevision)] };

        ItemPlacement[] before = transaction.Read(inventory.Contents);
        ItemInstance.State item = transaction.Read(ev.Item.Runtime);
        if (item.Context != null && !ReferenceEquals(item.Context, ev.Context))
            throw new InvalidOperationException("An item cannot move between contexts.");
        ItemPlacement? current = before.SingleOrDefault(placement => ReferenceEquals(placement.Item, ev.Item));
        var after = before.ToList();
        var assignments = new List<AbilitySourceAssignment>();
        InventoryChangeFailure failure = InventoryChangeFailure.None;
        if (ev.Operation == InventoryOperation.Acquire)
        {
            if (item.Destroyed || item.Owner != null || current != null)
                failure = InventoryChangeFailure.AlreadyOwnedOrDestroyed;
            else
                after.Add(new(ev.Item, ev.SlotId));
        }
        else if (!ReferenceEquals(item.Owner, ev.Owner) || current == null || item.Destroyed)
            failure = InventoryChangeFailure.NotOwned;
        else
        {
            after.Remove(current);
            if (ev.Operation == InventoryOperation.Equip)
            {
                ItemPlacement? replaced = after.SingleOrDefault(placement =>
                    ev.SlotId != null && placement.SlotId == ev.SlotId);
                if (replaced != null)
                {
                    after.Remove(replaced);
                    after.Add(replaced with { SlotId = null });
                    assignments.Add(new(replaced.Item.Source, null, replaced.Item.Source.Revision));
                    ItemInstance.State replacedState = transaction.Read(replaced.Item.Runtime);
                    transaction.Stage(replaced.Item.Runtime, replacedState);
                }
                after.Add(new(ev.Item, ev.SlotId));
                if (ev.SlotId == null) failure = InventoryChangeFailure.InvalidSlot;
            }
            else if (ev.Operation == InventoryOperation.Unequip)
            {
                if (ev.SlotId != null) failure = InventoryChangeFailure.InvalidSlot;
                after.Add(new(ev.Item, null));
            }
            else if (ev.SlotId != null)
                failure = InventoryChangeFailure.InvalidSlot;
        }
        if (failure == InventoryChangeFailure.None)
            failure = InventoryLayout.Validate(after, inventory.Configuration);
        if (failure != InventoryChangeFailure.None)
            return new() { ShouldCommit = false, Completions = [Result(ev, failure)] };

        ItemPlacement? next = after.SingleOrDefault(placement => ReferenceEquals(placement.Item, ev.Item));
        assignments.Add(new(ev.Item.Source, next?.SlotId != null ? ev.Owner : null, ev.Item.Source.Revision));
        transaction.Stage(inventory.Contents, after.ToArray());
        transaction.Stage(ev.Item.Runtime, item with
        {
            Context = ev.Context,
            Owner = ev.Operation == InventoryOperation.Discard ? null : ev.Owner,
            Destroyed = ev.Operation == InventoryOperation.Discard
        });
        AbilitySourceChangePlan equipment = EquipmentPreparation.Prepare(ev, operationId, inventory, before, after, transaction);
        return EquipmentPreparation.Compose(new()
        {
            Assignments = assignments, Completions = [Result(ev, InventoryChangeFailure.None)]
        }, equipment);
    }

    private static InventoryChangeResultEvent Result(InventoryChangeRequestEvent ev, InventoryChangeFailure failure) =>
        ev.Context.Factory.NewEvent<InventoryChangeResultEvent>() with
        {
            OperationId = ev.OperationId, Owner = ev.Owner, Operation = ev.Operation,
            ItemInstanceId = ev.Item.InstanceId, Failure = failure,
            Receiver = ev.Owner.EventReceiver, IsScopeDetached = true
        };
}
