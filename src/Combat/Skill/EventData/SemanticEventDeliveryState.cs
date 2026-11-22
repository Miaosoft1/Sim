using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Combat.Skill.EventData;

internal sealed class SemanticEventDeliveryState<TEvent> where TEvent : Event
{
    private TEvent? first;
    private TEvent? second;
    private EventReceiver? firstReceiver;
    private EventReceiver? secondReceiver;
    private bool firstDelivered;
    private bool secondDelivered;
    private readonly List<IndexedHandlerInvocation> firstInvocations = [];
    private readonly List<IndexedHandlerInvocation> secondInvocations = [];

    public bool Claimed { get; set; }
    public bool Dispatched { get; set; }
    public List<IndexedHandlerInvocation> OtherInvocations { get; } = [];

    public void SetDeliveries(TEvent firstDelivery, TEvent? secondDelivery)
    {
        if (first != null)
            throw new InvalidOperationException("The semantic event already has deliveries.");
        first = firstDelivery;
        firstReceiver = firstDelivery.Receiver ??
            throw new InvalidOperationException("The first delivery requires a receiver.");
        if (secondDelivery != null)
        {
            second = secondDelivery;
            secondReceiver = secondDelivery.Receiver ??
                throw new InvalidOperationException("The second delivery requires a receiver.");
            if (ReferenceEquals(firstReceiver, secondReceiver))
                throw new InvalidOperationException("Semantic event deliveries must have distinct receivers.");
        }
    }

    public List<IndexedHandlerInvocation> InvocationsFor(EventReceiver receiver)
    {
        if (ReferenceEquals(receiver, firstReceiver)) return firstInvocations;
        if (ReferenceEquals(receiver, secondReceiver)) return secondInvocations;
        return OtherInvocations;
    }

    public IEnumerable<IEvent> DispatchDelivery(TEvent delivery)
    {
        EventReceiver receiver;
        IReadOnlyList<IndexedHandlerInvocation> invocations;
        if (ReferenceEquals(delivery, first) && ReferenceEquals(delivery.Receiver, firstReceiver) && !firstDelivered)
        {
            firstDelivered = true;
            receiver = firstReceiver!;
            invocations = firstInvocations;
        }
        else if (ReferenceEquals(delivery, second) && ReferenceEquals(delivery.Receiver, secondReceiver) && !secondDelivered)
        {
            secondDelivered = true;
            receiver = secondReceiver!;
            invocations = secondInvocations;
        }
        else
        {
            throw new InvalidOperationException("The semantic event delivery is invalid or has already been dispatched.");
        }
        return receiver.Dispatch(delivery, invocations);
    }
}

internal sealed record IndexedObservationDispatchEvent<TEvent> : Event where TEvent : Event
{
    public IndexedObservationDispatchEvent() { }
    public required SemanticEventDeliveryState<TEvent> Publication { get; init; }

    public override IEnumerable<IEvent> Dispatch()
    {
        foreach (IndexedHandlerInvocation invocation in Publication.OtherInvocations)
            yield return invocation.Invocation;
    }
}
