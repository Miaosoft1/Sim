using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Inventory.Handler;
using Miaosoft.Sim.Loadout.Trading.EventData;
using Miaosoft.Sim.Loadout.Trading.Handler;
using Miaosoft.Sim.Loadout.Trading.Policy;

namespace Miaosoft.Sim.Loadout.Trading.Model;

public sealed class DeathTradeAuthorization
{
    internal sealed record Inspection(bool Inspected, long CorpseRevision, long BeneficiaryRevision);
    internal AtomicCell<Inspection> Runtime { get; } = new(new(false, 0, 0));
    internal IReadOnlySet<Guid> ItemIds { get; }

    internal DeathTradeAuthorization(
        DeathTradeDirectory directory, DeathResolvedEvent death, Unit beneficiary, IEnumerable<Guid> itemIds)
    {
        Directory = directory;
        DeathId = death.DeathId;
        Context = death.Context;
        Corpse = death.TargetObject;
        Beneficiary = beneficiary;
        ItemIds = new HashSet<Guid>(itemIds);
    }

    public Guid InstanceId { get; } = Guid.NewGuid();
    public DeathTradeDirectory Directory { get; }
    public Guid DeathId { get; }
    public Context Context { get; }
    public Unit Owner => Directory.Inventory.Owner;
    public Unit Beneficiary { get; }
    public MapObject? Corpse { get; }
    public long Revision => Runtime.Version;
    public bool IsInspected => Runtime.Value.Inspected;

    public DeathTradeObservation Observe()
    {
        DeathTradeFailure access = DeathTradeAccess.AssessAuthorization(this);
        if (access == DeathTradeFailure.None &&
            (Owner.EventReceiver.GetHandlers<DeathTradeHandler>() is not [var death] ||
                !ReferenceEquals(death.Directory, Directory) ||
                Owner.EventReceiver.GetHandlers<InventoryHandler>() is not [var ownerInventory] ||
                !ReferenceEquals(ownerInventory.Inventory, Directory.Inventory) ||
                Beneficiary.EventReceiver.GetHandlers<TradeHandler>() is not [var trade] ||
                Beneficiary.EventReceiver.GetHandlers<InventoryHandler>() is not [var inventory] ||
                !ReferenceEquals(trade.Inventory, inventory.Inventory)))
            access = DeathTradeFailure.NotAuthorized;
        Inspection inspection = Runtime.Value;
        if (access == DeathTradeFailure.None)
        {
            if (!inspection.Inspected) access = DeathTradeFailure.NotInspected;
            else if (inspection.CorpseRevision != Directory.Inventory.Revision ||
                inspection.BeneficiaryRevision != Beneficiary.EventReceiver.GetHandlers<InventoryHandler>().Single().Inventory.Revision)
                access = DeathTradeFailure.InventoryChanged;
        }
        return new(access, Revision, access == DeathTradeFailure.None
            ? Array.AsReadOnly(Directory.Inventory.Items.Where(placement => ItemIds.Contains(placement.Item.InstanceId))
                .Select(placement => new DeathTradeItemView(placement.Item.InstanceId, placement.Item.Definition.Id,
                    placement.Item.Definition.Name, placement.SlotId)).ToArray())
            : []);
    }
}
