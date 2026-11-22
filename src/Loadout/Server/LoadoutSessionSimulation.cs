using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Inventory.EventData;
using Miaosoft.Sim.Loadout.Protocol.Command;
using Miaosoft.Sim.Loadout.Talents.EventData;
using Miaosoft.Sim.Loadout.Trading.EventData;
using Miaosoft.Sim.Loadout.Trading.Model;
using Miaosoft.Sim.Protocol.Model;
using Miaosoft.Sim.Protocol.Event;
using Miaosoft.Sim.Runtime;
using Miaosoft.Sim.Runtime.EventData;

namespace Miaosoft.Sim.Loadout.Server;

public sealed class LoadoutSessionSimulation : ISessionSimulation
{
    private readonly ISessionSimulation inner;
    private readonly Func<LoadoutSessionFeatures> features;
    private readonly EventReceiver receiver;
    private readonly Dictionary<Guid, (SessionInput Input, EventReceiver Receiver)> operations = [];

    public LoadoutSessionSimulation(
        ISessionSimulation inner, IEnumerable<LoadoutParticipant> participants,
        IEnumerable<TradeSession>? exchanges = null, IEnumerable<DeathTradeDirectory>? deaths = null)
        : this(inner, FixedFeatures(inner, participants, exchanges, deaths))
    {
    }

    public LoadoutSessionSimulation(ISessionSimulation inner, Func<LoadoutSessionFeatures> features)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(features);
        this.inner = inner;
        this.features = features;
        _ = CurrentFeatures();
        receiver = Context.Factory.NewEventReceiver();
        Context.AddEventReceiver(receiver);
        Observation = new(CurrentFeatures);
        Context.EventProcessed += OnProcessed;
    }

    private static Func<LoadoutSessionFeatures> FixedFeatures(
        ISessionSimulation inner, IEnumerable<LoadoutParticipant> participants,
        IEnumerable<TradeSession>? exchanges, IEnumerable<DeathTradeDirectory>? deaths)
    {
        ArgumentNullException.ThrowIfNull(inner);
        var snapshot = new LoadoutSessionFeatures(inner.Context, participants, exchanges, deaths);
        return () => snapshot;
    }

    private LoadoutSessionFeatures CurrentFeatures()
    {
        LoadoutSessionFeatures snapshot = features();
        if (snapshot == null || !ReferenceEquals(snapshot.Context, Context))
            throw new InvalidOperationException("The Loadout feature provider must return a snapshot for this context.");
        return snapshot;
    }

    public LoadoutObservationModule Observation { get; }
    public Context Context => inner.Context;
    public IReadOnlyList<Guid> ParticipantUnitIds => inner.ParticipantUnitIds;
    public bool IsCompleted => inner.IsCompleted;
    public bool CanAcceptCompletedInput(IEventMessage message) =>
        message is DeathInspectCommand or DeathClaimCommand || inner.CanAcceptCompletedInput(message);
    public bool CanAcceptCompletedPostedEvent(IEvent ev) => inner.CanAcceptCompletedPostedEvent(ev);
    public IEnumerable<IEvent> CreateStartedEvents() => inner.CreateStartedEvents();
    public IEnumerable<IEvent> CreateCompletionEvents() => inner.CreateCompletionEvents();
    public IEnumerable<IEvent> CreateCompletedEvents() => inner.CreateCompletedEvents();
    public WorldSnapshot CaptureWorld(Guid? unitId) => inner.CaptureWorld(unitId);
    public IReadOnlyList<ITransientEventMessage> CaptureTransientEvents(IEvent ev, Guid? unitId) =>
        inner.CaptureTransientEvents(ev, unitId);

    public SessionInputAssessment AssessInput(Guid unitId, IEventMessage message) =>
        message is LoadoutCommand command ? AssessInput(CurrentFeatures(), unitId, command) :
            inner.AssessInput(unitId, message);

    private SessionInputAssessment AssessInput(LoadoutSessionFeatures snapshot, Guid unitId, LoadoutCommand command)
    {
        if (command.UnitId != unitId || !ParticipantUnitIds.Contains(unitId) ||
            !snapshot.Units.TryGetValue(unitId, out LoadoutParticipant? participant) ||
            participant.Unit.IsRetired || !Context.ContainsReceiver(participant.Unit.EventReceiver))
            return Denied("The Loadout command does not belong to this participant.");
        bool inventory = LoadoutFeatureAccess.Inventory(participant.Inventory);
        bool trading = LoadoutFeatureAccess.Trading(participant.Inventory);
        switch (command)
        {
            case InventoryCommand change:
                if (!inventory || !participant.Inventory!.Items.Any(item => item.Item.InstanceId == change.ItemInstanceId))
                    return Denied("The item is not currently owned by this participant.");
                if (change.ExpectedRevision < 0 || !Enum.IsDefined(change.Action) || change.Action == InventoryAction.Unspecified ||
                    change.Action == InventoryAction.Equip && string.IsNullOrWhiteSpace(change.SlotId) ||
                    change.Action != InventoryAction.Equip && change.SlotId != null)
                    return Invalid("The inventory operation or version is invalid.");
                break;
            case TalentAllocateCommand allocate:
                if (!LoadoutFeatureAccess.Talents(participant.Talents) ||
                    !participant.Talents!.Trees.ContainsKey(allocate.TreeId ?? ""))
                    return Denied("The talent tree does not belong to this participant.");
                if (string.IsNullOrWhiteSpace(allocate.NodeId) || allocate.ExpectedRevision < 0 || allocate.ExpectedAuthorityVersion < 1)
                    return Invalid("The talent allocation identity or version is invalid.");
                break;
            case ExchangeCommand exchange:
                if (!trading || !snapshot.Exchanges.TryGetValue(exchange.ExchangeId, out TradeSession? session) ||
                    !ReferenceEquals(session.First, participant.Inventory) && !ReferenceEquals(session.Second, participant.Inventory) ||
                    !LoadoutFeatureAccess.Trading(session.First) || !LoadoutFeatureAccess.Trading(session.Second))
                    return Denied("The participant is not a member of this exchange.");
                if (!Enum.IsDefined(exchange.Action) || exchange.Action == ExchangeAction.Unspecified ||
                    exchange.ExpectedTermsRevision < 0 || !ValidOffers(exchange.Offers) ||
                    exchange.Action != ExchangeAction.Offer && exchange.Offers.Count != 0)
                    return Invalid("The exchange operation, terms or offers are invalid.");
                break;
            case DeathInspectCommand inspect:
                if (!trading || Authorization(snapshot, unitId, inspect.AuthorizationId) == null)
                    return Denied("The death authorization does not belong to this beneficiary.");
                break;
            case DeathClaimCommand claim:
                if (!trading || Authorization(snapshot, unitId, claim.AuthorizationId) == null)
                    return Denied("The death authorization does not belong to this beneficiary.");
                if (claim.ExpectedInspectionRevision < 1 || !ValidOffers(claim.Items) || claim.Items.Count == 0)
                    return Invalid("A versioned nonempty inspected selection is required.");
                break;
            default:
                return Invalid("This Loadout command is unsupported.");
        }
        return new(true, OperationQueueStatus.Enqueued);
    }

    public IEnumerable<IEvent> CreateInputEvents(SessionInput input)
    {
        if (input.Message is not LoadoutCommand command)
        {
            foreach (IEvent ev in inner.CreateInputEvents(input)) yield return ev;
            yield break;
        }
        LoadoutSessionFeatures snapshot = CurrentFeatures();
        SessionInputAssessment assessment = AssessInput(snapshot, input.UnitId, command);
        if (!assessment.Allowed)
        {
            yield return Outcome(input, false, assessment.Reason);
            yield break;
        }
        LoadoutParticipant participant = snapshot.Units[input.UnitId];
        operations.Add(input.ExecutionId, (input, participant.Unit.EventReceiver));
        var receiver = participant.Unit.EventReceiver;
        yield return command switch
        {
            InventoryCommand change => Context.Factory.NewEvent<InventoryChangeRequestEvent>() with
            {
                Owner = participant.Unit,
                Item = participant.Inventory!.Items.Single(item => item.Item.InstanceId == change.ItemInstanceId).Item,
                Operation = change.Action switch
                {
                    InventoryAction.Equip => InventoryOperation.Equip,
                    InventoryAction.Unequip => InventoryOperation.Unequip,
                    InventoryAction.Discard => InventoryOperation.Discard,
                    _ => throw new InvalidOperationException("The inventory action was not assessed.")
                },
                ExpectedRevision = change.ExpectedRevision, SlotId = change.SlotId,
                OperationId = input.ExecutionId, Receiver = receiver
            },
            TalentAllocateCommand allocate => Context.Factory.NewEvent<TalentNodeAllocateRequestEvent>() with
            {
                Tree = participant.Talents!.Trees[allocate.TreeId], NodeId = allocate.NodeId,
                ExpectedRevision = allocate.ExpectedRevision, ExpectedAuthorityVersion = allocate.ExpectedAuthorityVersion,
                OperationId = input.ExecutionId, Receiver = receiver
            },
            ExchangeCommand exchange => Context.Factory.NewEvent<TradeRequestEvent>() with
            {
                Actor = participant.Unit, Session = snapshot.Exchanges[exchange.ExchangeId],
                Action = exchange.Action switch
                {
                    ExchangeAction.Offer => TradeAction.Offer,
                    ExchangeAction.Confirm => TradeAction.Confirm,
                    ExchangeAction.Cancel => TradeAction.Cancel,
                    _ => throw new InvalidOperationException("The exchange action was not assessed.")
                },
                ExpectedTermsRevision = exchange.ExpectedTermsRevision,
                Offers = Array.AsReadOnly(exchange.Offers.Select(Offer).ToArray()),
                OperationId = input.ExecutionId, Receiver = receiver
            },
            DeathInspectCommand inspect => Context.Factory.NewEvent<DeathTradeInspectRequestEvent>() with
            {
                Actor = participant.Unit, Authorization = Authorization(snapshot, input.UnitId, inspect.AuthorizationId)!,
                OperationId = input.ExecutionId, Receiver = receiver
            },
            DeathClaimCommand claim => Context.Factory.NewEvent<DeathTradeClaimRequestEvent>() with
            {
                Actor = participant.Unit, Authorization = Authorization(snapshot, input.UnitId, claim.AuthorizationId)!,
                ExpectedInspectionRevision = claim.ExpectedInspectionRevision,
                Items = Array.AsReadOnly(claim.Items.Select(Offer).ToArray()),
                OperationId = input.ExecutionId, Receiver = receiver
            },
            _ => throw new InvalidOperationException("The Loadout command was not assessed.")
        };
    }

    private static DeathTradeAuthorization? Authorization(LoadoutSessionFeatures snapshot, Guid unitId, Guid id) =>
        snapshot.Deaths.Where(LoadoutFeatureAccess.Death).Select(directory => directory.Authorizations.GetValueOrDefault(unitId))
            .SingleOrDefault(authorization => authorization?.InstanceId == id);
    private static TradeItemOffer Offer(ItemOffer offer) => new(offer.ItemInstanceId, offer.DestinationSlotId);
    private static bool ValidOffers(IReadOnlyList<ItemOffer>? offers) => offers != null &&
        offers.All(offer => offer != null && offer.ItemInstanceId != Guid.Empty &&
            (offer.DestinationSlotId == null || !string.IsNullOrWhiteSpace(offer.DestinationSlotId))) &&
        offers.Select(offer => offer.ItemInstanceId).Distinct().Count() == offers.Count;
    private static SessionInputAssessment Denied(string reason) => new(false, OperationQueueStatus.Unauthorized, reason);
    private static SessionInputAssessment Invalid(string reason) => new(false, OperationQueueStatus.Invalid, reason);
    private SessionOperationCompletedEvent Outcome(SessionInput input, bool success, string? reason) =>
        Context.Factory.NewEvent<SessionOperationCompletedEvent>() with
        {
            ExecutionId = input.ExecutionId,
            Status = success ? OperationExecutionStatus.Succeeded : OperationExecutionStatus.Rejected,
            Reason = reason, Receiver = receiver
        };

    private void OnProcessed(IEvent ev)
    {
        if (ev is SessionOperationCompletedEvent complete) { operations.Remove(complete.ExecutionId); return; }
        (Guid Id, bool Success, string? Reason) result = ev switch
        {
            InventoryChangeResultEvent change => (change.OperationId, change.Success, change.Failure.ToString()),
            TalentChangeResultEvent change => (change.OperationId, change.Success, change.Failure.ToString()),
            TradeResultEvent trade => (trade.OperationId, trade.Failure == TradeFailure.None, trade.Failure.ToString()),
            DeathTradeResultEvent trade => (trade.OperationId, trade.Success, trade.Failure.ToString()),
            _ => default
        };
        if (operations.TryGetValue(result.Id, out var operation))
        {
            if (ev is not Event resultEvent || resultEvent.Receiver != operation.Receiver)
                throw new InvalidOperationException("The Loadout result does not address the submitting participant.");
            Context.Dispatch(Outcome(operation.Input, result.Success, result.Success ? null : result.Reason)).ToList();
        }
    }
}
