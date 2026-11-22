using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Inventory.EventData;
using Miaosoft.Sim.Loadout.Inventory.Model;
using Miaosoft.Sim.Loadout.Inventory.Policy;
using Miaosoft.Sim.Loadout.Trading.EventData;
using Miaosoft.Sim.Loadout.Trading.Model;

namespace Miaosoft.Sim.Loadout.Trading.Policy;

internal sealed class TradeExchangePreparation(TradeRequestEvent request)
    : IAbilitySourceChangePreparation
{
    public AbilitySourceChangePlan Prepare(
        Context context, Unit coordinator, Guid operationId, AtomicTransaction transaction)
    {
        if (!ReferenceEquals(context, request.Context) || !ReferenceEquals(coordinator, request.Actor) ||
            operationId != request.OperationId)
            throw new InvalidOperationException("The exchange preparation does not match its trade request.");
        TradeSession session = request.Session;
        TradeAccess.Validate(context, session);
        TradeSession.State state = transaction.Read(session.Runtime);
        bool first = ReferenceEquals(request.Actor, session.First.Owner);
        if (!first && !ReferenceEquals(request.Actor, session.Second.Owner))
            throw new InvalidOperationException("The exchange actor is not a participant.");
        TradeFailure failure = state.Closed ? TradeFailure.Closed :
            state.TermsRevision != request.ExpectedTermsRevision ? TradeFailure.StaleTerms :
            state.FirstInventoryRevision != session.First.Revision ||
            state.SecondInventoryRevision != session.Second.Revision ? TradeFailure.InventoryChanged :
            !(first ? state.SecondConfirmed : state.FirstConfirmed) ? TradeFailure.StaleTerms :
            !session.CanTrade(session.First.Owner, session.Second.Owner) ? TradeFailure.PolicyRejected : TradeFailure.None;
        if (failure != TradeFailure.None) return Rejected(failure, state.TermsRevision);

        ItemPlacement[] firstBefore = transaction.Read(session.First.Contents);
        ItemPlacement[] secondBefore = transaction.Read(session.Second.Contents);
        var firstItems = firstBefore.ToDictionary(placement => placement.Item.InstanceId);
        var secondItems = secondBefore.ToDictionary(placement => placement.Item.InstanceId);
        if (state.FirstOffer.Any(offer => !firstItems.ContainsKey(offer.ItemInstanceId)) ||
            state.SecondOffer.Any(offer => !secondItems.ContainsKey(offer.ItemInstanceId)) ||
            state.FirstOffer.Length + state.SecondOffer.Length == 0)
            return Rejected(TradeFailure.InvalidOffer, state.TermsRevision);
        HashSet<Guid> outgoingFirst = state.FirstOffer.Select(offer => offer.ItemInstanceId).ToHashSet();
        HashSet<Guid> outgoingSecond = state.SecondOffer.Select(offer => offer.ItemInstanceId).ToHashSet();
        var firstAfter = firstBefore.Where(placement => !outgoingFirst.Contains(placement.Item.InstanceId)).ToList();
        var secondAfter = secondBefore.Where(placement => !outgoingSecond.Contains(placement.Item.InstanceId)).ToList();
        firstAfter.AddRange(state.SecondOffer.Select(offer =>
            new ItemPlacement(secondItems[offer.ItemInstanceId].Item, offer.DestinationSlotId)));
        secondAfter.AddRange(state.FirstOffer.Select(offer =>
            new ItemPlacement(firstItems[offer.ItemInstanceId].Item, offer.DestinationSlotId)));
        if (InventoryLayout.Validate(firstAfter, session.First.Configuration) != InventoryChangeFailure.None ||
            InventoryLayout.Validate(secondAfter, session.Second.Configuration) != InventoryChangeFailure.None)
            return Rejected(TradeFailure.InvalidLayout, state.TermsRevision);
        var assignments = new List<AbilitySourceAssignment>();
        void Transfer(TradeItemOffer offer, Dictionary<Guid, ItemPlacement> source, Unit oldOwner, Unit newOwner)
        {
            assignments.Add(InventoryTransfer.Stage(
                context, transaction, source[offer.ItemInstanceId], oldOwner, newOwner, offer.DestinationSlotId));
        }
        foreach (TradeItemOffer offer in state.FirstOffer)
            Transfer(offer, firstItems, session.First.Owner, session.Second.Owner);
        foreach (TradeItemOffer offer in state.SecondOffer)
            Transfer(offer, secondItems, session.Second.Owner, session.First.Owner);
        transaction.Stage(session.First.Contents, firstAfter.ToArray());
        transaction.Stage(session.Second.Contents, secondAfter.ToArray());
        transaction.Stage(session.Runtime, state with { FirstConfirmed = true, SecondConfirmed = true, Closed = true });
        AbilitySourceChangePlan firstEquipment = EquipmentPreparation.Prepare(
            request, operationId, session.First, firstBefore, firstAfter, transaction);
        AbilitySourceChangePlan secondEquipment = EquipmentPreparation.Prepare(
            request, operationId, session.Second, secondBefore, secondAfter, transaction);
        return EquipmentPreparation.Compose(new()
        {
            Assignments = assignments,
            Completions = [TradeResultEvent.FromRequest(request, TradeStatus.Completed, TradeFailure.None, state.TermsRevision)]
        }, firstEquipment, secondEquipment);
    }

    private AbilitySourceChangePlan Rejected(TradeFailure failure, long revision) => new()
    {
        ShouldCommit = false,
        Completions = [TradeResultEvent.FromRequest(request, TradeStatus.Rejected, failure, revision)]
    };
}
