using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Inventory.EventData;
using Miaosoft.Sim.Loadout.Inventory.Model;
using Miaosoft.Sim.Loadout.Inventory.Policy;

namespace Miaosoft.Sim.Loadout.Inventory.Handler;

public sealed class InventoryHandler(UnitInventory inventory) : Miaosoft.Sim.Combat.Skill.Handler.Handler
{
    public UnitInventory Inventory { get; } = inventory ?? throw new ArgumentNullException(nameof(inventory));
    public override string Id { get; set; } = nameof(InventoryHandler);

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<Event> OnCreate(ItemCreateRequestEvent ev)
    {
        if (ev.CancellationToken.IsCancellationRequested) yield break;
        if (!ReferenceEquals(ev.Coordinator, Inventory.Owner) || ev.Receiver != ev.Coordinator.EventReceiver)
            throw new InvalidOperationException("Item creation does not address its coordinator.");
        yield return ev.Context.Factory.NewEvent<AbilitySourcesChangeRequestEvent>() with
        {
            Coordinator = ev.Coordinator, OperationId = ev.OperationId,
            Preparation = new ItemCreationPreparation(ev, Inventory),
            Receiver = ev.Receiver, CancellationTokenSource = ev.CancellationTokenSource
        };
    }

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<Event> OnChange(InventoryChangeRequestEvent ev)
    {
        if (ev.CancellationToken.IsCancellationRequested) yield break;
        if (!ReferenceEquals(ev.Owner, Inventory.Owner) || ev.Receiver != ev.Owner.EventReceiver)
            throw new InvalidOperationException("The inventory request does not address its owner.");
        yield return ev.Context.Factory.NewEvent<AbilitySourcesChangeRequestEvent>() with
        {
            Coordinator = ev.Owner, OperationId = ev.OperationId,
            Preparation = new InventoryChangePreparation(ev, Inventory),
            Receiver = ev.Owner.EventReceiver, CancellationTokenSource = ev.CancellationTokenSource
        };
    }
}
