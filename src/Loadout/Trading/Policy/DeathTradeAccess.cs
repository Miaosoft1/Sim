using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Inventory.Model;
using Miaosoft.Sim.Loadout.Inventory.Policy;
using Miaosoft.Sim.Loadout.Trading.EventData;
using Miaosoft.Sim.Loadout.Trading.Handler;
using Miaosoft.Sim.Loadout.Trading.Model;

namespace Miaosoft.Sim.Loadout.Trading.Policy;

internal static class DeathTradeAccess
{
    public static void ValidateDirectory(Context context, DeathTradeDirectory directory)
    {
        Unit owner = directory.Inventory.Owner;
        InventoryAccess.Validate(context, owner, directory.Inventory, owner.EventReceiver);
        DeathTradeHandler[] handlers = owner.EventReceiver.GetHandlers<DeathTradeHandler>().ToArray();
        if (!context.IsProcessingEvent || handlers.Length != 1 || !ReferenceEquals(handlers[0].Directory, directory))
            throw new InvalidOperationException("Death trading requires its current unique directory feature.");
    }

    public static DeathTradeFailure Validate(
        DeathTradeRequestEvent request, UnitInventory beneficiary, Unit coordinator)
    {
        DeathTradeAuthorization authorization = request.Authorization;
        DeathTradeDirectory directory = authorization.Directory;
        Context context = request.Context;
        if (!ReferenceEquals(request.Actor, coordinator) || !ReferenceEquals(beneficiary.Owner, coordinator) ||
            !ReferenceEquals(authorization.Context, context) || request.Receiver != coordinator.EventReceiver ||
            !ReferenceEquals(authorization.Beneficiary, coordinator))
            throw new InvalidOperationException("Death trading requires its original beneficiary in the executing context.");
        InventoryAccess.Validate(context, coordinator, beneficiary, request.Receiver);
        if (coordinator.EventReceiver.GetHandlers<TradeHandler>() is not [var handler] ||
            !ReferenceEquals(handler.Inventory, beneficiary))
            throw new InvalidOperationException("Death trading requires the beneficiary's unique current trading feature.");
        ValidateDirectory(context, directory);
        return AssessAuthorization(authorization);
    }

    internal static DeathTradeFailure AssessAuthorization(DeathTradeAuthorization authorization)
    {
        DeathTradeDirectory directory = authorization.Directory;
        Unit coordinator = authorization.Beneficiary;
        if (directory.IsClosed) return DeathTradeFailure.Closed;
        if (directory.DeathId != authorization.DeathId ||
            !directory.Authorizations.TryGetValue(coordinator.Id, out var current) ||
            !ReferenceEquals(current, authorization) || !coordinator.CanParticipate ||
            authorization.Owner.IsAlive || authorization.Owner.IsRetired ||
            authorization.Owner.CurrentDeathId != authorization.DeathId)
            return DeathTradeFailure.NotAuthorized;
        var corpse = authorization.Corpse;
        if (corpse == null || corpse.EventReceiver != authorization.Owner.EventReceiver ||
            corpse.Manager != authorization.Owner.MapObjects || corpse.MapRuntime == null ||
            corpse.MapRuntime.IsClosing || !ReferenceEquals(corpse.MapRuntime.FindObject(corpse.Id), corpse))
            return DeathTradeFailure.CorpseUnavailable;
        return directory.CanInteract(authorization) ? DeathTradeFailure.None : DeathTradeFailure.PolicyRejected;
    }

    public static DeathTradeResultEvent Result(
        Event ev, Guid operationId, DeathTradeOperation operation, Guid deathId, DeathTradeFailure failure,
        Unit recipient, DeathTradeAuthorization? authorization = null, long inspectionRevision = 0,
        IReadOnlyList<DeathTradeItemView>? items = null) =>
        ev.Context.Factory.NewEvent<DeathTradeResultEvent>() with
        {
            OperationId = operationId, Operation = operation, DeathId = deathId, Failure = failure,
            AuthorizationId = authorization?.InstanceId, InspectionRevision = inspectionRevision, Items = items ?? [],
            Receiver = recipient.EventReceiver, Priority = ev.Priority, IsScopeDetached = true
        };
}
