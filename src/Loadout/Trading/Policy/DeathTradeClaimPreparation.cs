using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Inventory.EventData;
using Miaosoft.Sim.Loadout.Inventory.Model;
using Miaosoft.Sim.Loadout.Inventory.Policy;
using Miaosoft.Sim.Loadout.Trading.EventData;
using Miaosoft.Sim.Loadout.Trading.Model;

namespace Miaosoft.Sim.Loadout.Trading.Policy;

internal sealed class DeathTradeClaimPreparation(DeathTradeRequestEvent request, UnitInventory beneficiary)
    : IAbilitySourceChangePreparation
{
    public AbilitySourceChangePlan Prepare(Context context, Unit coordinator, Guid operationId, AtomicTransaction transaction)
    {
        if (!ReferenceEquals(context, request.Context) || operationId != request.OperationId || operationId == Guid.Empty)
            throw new InvalidOperationException("Death trading requires its original request.");
        DeathTradeAuthorization authorization = request.Authorization;
        UnitInventory corpse = authorization.Directory.Inventory;
        bool inspect = request is DeathTradeInspectRequestEvent;
        if (!inspect && request is not DeathTradeClaimRequestEvent)
            throw new InvalidOperationException("Unknown death trade operation.");
        AbilitySourceChangePlan Result(
            DeathTradeFailure failure, IReadOnlyList<DeathTradeItemView>? items = null, long revision = 0) => new()
        {
            ShouldCommit = failure == DeathTradeFailure.None,
            Completions = [DeathTradeAccess.Result(request, operationId,
                inspect ? DeathTradeOperation.Inspect : DeathTradeOperation.Claim,
                authorization.DeathId, failure, coordinator, authorization, revision, items)]
        };
        DeathTradeFailure access = DeathTradeAccess.Validate(request, beneficiary, coordinator);
        if (access != DeathTradeFailure.None) return Result(access);
        DeathTradeAuthorization.Inspection inspection = transaction.Read(authorization.Runtime);
        ItemPlacement[] before = transaction.Read(corpse.Contents);
        if (inspect)
        {
            transaction.Stage(authorization.Runtime, new(true, corpse.Revision, beneficiary.Revision));
            return Result(DeathTradeFailure.None, Array.AsReadOnly(before
                .Where(placement => authorization.ItemIds.Contains(placement.Item.InstanceId))
                .Select(View).ToArray()), checked(authorization.Revision + 1));
        }
        var claim = (DeathTradeClaimRequestEvent)request;
        if (claim.ExpectedInspectionRevision != authorization.Revision) return Result(DeathTradeFailure.StaleInspection);
        if (!inspection.Inspected) return Result(DeathTradeFailure.NotInspected);
        if (inspection.CorpseRevision != corpse.Revision || inspection.BeneficiaryRevision != beneficiary.Revision)
            return Result(DeathTradeFailure.InventoryChanged);
        TradeItemOffer[] selection = claim.Items.ToArray();
        Dictionary<Guid, ItemPlacement> owned = before.ToDictionary(placement => placement.Item.InstanceId);
        if (selection.Length == 0 || selection.Any(item => item == null || item.ItemInstanceId == Guid.Empty ||
                !authorization.ItemIds.Contains(item.ItemInstanceId) || !owned.ContainsKey(item.ItemInstanceId)) ||
            selection.Select(item => item.ItemInstanceId).Distinct().Count() != selection.Length)
            return Result(DeathTradeFailure.InvalidSelection);
        HashSet<Guid> selected = selection.Select(item => item.ItemInstanceId).ToHashSet();
        ItemPlacement[] corpseAfter = before.Where(placement => !selected.Contains(placement.Item.InstanceId)).ToArray();
        ItemPlacement[] beneficiaryBefore = transaction.Read(beneficiary.Contents);
        ItemPlacement[] beneficiaryAfter = beneficiaryBefore.Concat(selection.Select(item =>
            new ItemPlacement(owned[item.ItemInstanceId].Item, item.DestinationSlotId))).ToArray();
        if (InventoryLayout.Validate(corpseAfter, corpse.Configuration) != InventoryChangeFailure.None ||
            InventoryLayout.Validate(beneficiaryAfter, beneficiary.Configuration) != InventoryChangeFailure.None)
            return Result(DeathTradeFailure.InvalidLayout);
        AbilitySourceAssignment[] assignments = selection.Select(item => InventoryTransfer.Stage(
            context, transaction, owned[item.ItemInstanceId], corpse.Owner, coordinator, item.DestinationSlotId)).ToArray();
        transaction.Stage(corpse.Contents, corpseAfter);
        transaction.Stage(beneficiary.Contents, beneficiaryAfter);
        transaction.Stage(authorization.Runtime, new(false, 0, 0));
        return EquipmentPreparation.Compose(Result(DeathTradeFailure.None,
            Array.AsReadOnly(selection.Select(item => View(new(owned[item.ItemInstanceId].Item, item.DestinationSlotId))).ToArray()),
            checked(authorization.Revision + 1)) with { Assignments = assignments },
            EquipmentPreparation.Prepare(request, operationId, corpse, before, corpseAfter, transaction),
            EquipmentPreparation.Prepare(request, operationId, beneficiary, beneficiaryBefore, beneficiaryAfter, transaction));
    }

    private static DeathTradeItemView View(ItemPlacement placement) => new(
        placement.Item.InstanceId, placement.Item.Definition.Id, placement.Item.Definition.Name, placement.SlotId);
}
