using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Inventory.Model;
using Miaosoft.Sim.Loadout.Trading.EventData;
using Miaosoft.Sim.Loadout.Trading.Model;
using Miaosoft.Sim.Loadout.Trading.Policy;

namespace Miaosoft.Sim.Loadout.Trading.Handler;

public sealed partial class TradeHandler(UnitInventory inventory)
    : Miaosoft.Sim.Combat.Skill.Handler.Handler
{
    public UnitInventory Inventory { get; } = inventory ?? throw new ArgumentNullException(nameof(inventory));
    public override string Id { get; set; } = nameof(TradeHandler);

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<Event> OnRequest(TradeRequestEvent ev)
    {
        if (ev.CancellationToken.IsCancellationRequested) yield break;
        if (!ReferenceEquals(ev.Actor, Inventory.Owner) || ev.Receiver != ev.Actor.EventReceiver ||
            !Enum.IsDefined(ev.Action) || ev.OperationId == Guid.Empty ||
            ev.Action != TradeAction.Offer && ev.Offers.Count != 0 ||
            ev.Actor.EventReceiver.GetHandlers<TradeHandler>().Count != 1 ||
            !ReferenceEquals(Inventory, ev.Session.First) && !ReferenceEquals(Inventory, ev.Session.Second))
            throw new InvalidOperationException("A trade request requires its participant and both inventories in one context.");
        TradeAccess.Validate(ev.Context, ev.Session);
        var transaction = new AtomicTransaction();
        TradeSession.State state = transaction.Read(ev.Session.Runtime);
        TradeFailure failure = state.Closed ? TradeFailure.Closed :
            state.TermsRevision != ev.ExpectedTermsRevision ? TradeFailure.StaleTerms : TradeFailure.None;
        if (failure == TradeFailure.None && ev.Action != TradeAction.Cancel &&
            !ev.Session.CanTrade(ev.Session.First.Owner, ev.Session.Second.Owner))
            failure = TradeFailure.PolicyRejected;
        bool first = ReferenceEquals(Inventory, ev.Session.First);
        TradeSession.State next = state;
        TradeStatus status = TradeStatus.Rejected;
        if (failure == TradeFailure.None && ev.Action == TradeAction.Offer)
        {
            TradeItemOffer[] offers = ev.Offers.ToArray();
            if (offers.Any(offer => offer == null || offer.ItemInstanceId == Guid.Empty) ||
                offers.Select(offer => offer.ItemInstanceId).Distinct().Count() != offers.Length ||
                offers.Any(offer => !Inventory.Items.Any(placement => placement.Item.InstanceId == offer.ItemInstanceId)))
                failure = TradeFailure.InvalidOffer;
            else
            {
                next = state with
                {
                    TermsRevision = checked(state.TermsRevision + 1),
                    FirstOffer = first ? offers : state.FirstOffer,
                    SecondOffer = first ? state.SecondOffer : offers,
                    FirstInventoryRevision = ev.Session.First.Revision,
                    SecondInventoryRevision = ev.Session.Second.Revision,
                    FirstConfirmed = false, SecondConfirmed = false
                };
                status = TradeStatus.Offered;
            }
        }
        else if (failure == TradeFailure.None && ev.Action == TradeAction.Cancel)
        {
            next = state with { Closed = true, FirstConfirmed = false, SecondConfirmed = false };
            status = TradeStatus.Canceled;
        }
        else if (failure == TradeFailure.None)
        {
            if (state.FirstInventoryRevision != ev.Session.First.Revision ||
                state.SecondInventoryRevision != ev.Session.Second.Revision)
                failure = TradeFailure.InventoryChanged;
            else if (state.FirstOffer.Length + state.SecondOffer.Length == 0)
                failure = TradeFailure.InvalidOffer;
            else
            {
                next = state with
                {
                    FirstConfirmed = first || state.FirstConfirmed,
                    SecondConfirmed = !first || state.SecondConfirmed
                };
                status = TradeStatus.AwaitingConsent;
                if (next.FirstConfirmed && next.SecondConfirmed)
                {
                    yield return ev.Context.Factory.NewEvent<AbilitySourcesChangeRequestEvent>() with
                    {
                        Coordinator = ev.Actor, OperationId = ev.OperationId,
                        Preparation = new TradeExchangePreparation(ev),
                        Receiver = ev.Actor.EventReceiver, CancellationTokenSource = ev.CancellationTokenSource
                    };
                    yield break;
                }
            }
        }
        if (failure != TradeFailure.None)
        {
            yield return TradeResultEvent.FromRequest(ev, TradeStatus.Rejected, failure, state.TermsRevision);
            yield break;
        }
        transaction.Stage(ev.Session.Runtime, next);
        transaction.Commit();
        ev.Context.MarkCurrentOperationCommitted(ev.OperationId);
        yield return TradeResultEvent.FromRequest(ev, status, TradeFailure.None, next.TermsRevision)
            with { CommittedOperationId = ev.OperationId };
    }
}
