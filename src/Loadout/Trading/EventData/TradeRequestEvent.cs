using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Trading.Model;

namespace Miaosoft.Sim.Loadout.Trading.EventData;

public enum TradeAction { Offer, Confirm, Cancel }
public enum TradeStatus { Offered, AwaitingConsent, Completed, Canceled, Rejected }
public enum TradeFailure { None, Closed, StaleTerms, InventoryChanged, InvalidOffer, InvalidLayout, PolicyRejected }

public sealed record TradeRequestEvent : Event
{
    public required Unit Actor { get; init; }
    public required TradeSession Session { get; init; }
    public required TradeAction Action { get; init; }
    public required long ExpectedTermsRevision { get; init; }
    public IReadOnlyList<TradeItemOffer> Offers { get; init; } = [];
    public Guid OperationId { get; init; } = Guid.NewGuid();
}

public sealed record TradeResultEvent : Event
{
    public required Guid OperationId { get; init; }
    public required Guid SessionInstanceId { get; init; }
    public required TradeStatus Status { get; init; }
    public required TradeFailure Failure { get; init; }
    public required long TermsRevision { get; init; }

    internal static TradeResultEvent FromRequest(
        TradeRequestEvent request, TradeStatus status, TradeFailure failure, long termsRevision) =>
        request.Context.Factory.NewEvent<TradeResultEvent>() with
        {
            OperationId = request.OperationId, SessionInstanceId = request.Session.InstanceId,
            Status = status, Failure = failure, TermsRevision = termsRevision,
            Receiver = request.Actor.EventReceiver, IsScopeDetached = true
        };
}
