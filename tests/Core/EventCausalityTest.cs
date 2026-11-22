namespace Miaosoft.Sim.Core.Tests;

[TestClass]
public sealed class EventCausalityTest
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void DerivedEventsAndQueueObserversShareTheirRootWhileInboxWorkStartsANewRoot()
    {
        using var context = new Context(causalityFactory: new ProbeFactory());
        context.Logger.Output = null;
        EventReceiver receiver = context.Factory.NewEventReceiver();
        context.AddEventReceiver(receiver);
        var handler = new ProbeHandler();
        var observer = new ProbeObserver(receiver);
        receiver.AddHandler(handler);
        context.AddObserver(observer);
        var root = context.Factory.NewEvent<RootEvent>() with { Receiver = receiver };
        context.Dispatch(root).ToList();
        context.RunUntilQueueEmpty();

        Assert.IsNotNull(root.Causality);
        Assert.AreSame(root.Causality, observer.QueuedChild!.Causality);
        Assert.AreSame(root.Causality, handler.Derived!.Causality);
        Assert.AreSame(root.Causality, handler.CoreChild!.Causality);
        Assert.IsNotNull(handler.InboxChild!.Causality);
        Assert.AreNotSame(root.Causality, handler.InboxChild.Causality);
        TestContext.WriteLine("Handler, observer and custom core events inherited the root; separately posted inbox work received a new root.");
    }

    [TestMethod]
    public void AForeignCausalityIsRejectedBeforeEnqueue()
    {
        using var first = new Context(causalityFactory: new ProbeFactory());
        using var second = new Context(causalityFactory: new ProbeFactory());
        first.Logger.Output = second.Logger.Output = null;
        var root = first.Factory.NewEvent<RootEvent>() with { Receiver = first.Factory.NewEventReceiver() };
        first.Dispatch(root).ToList();
        var foreign = second.Factory.NewEvent<DerivedEvent>() with
        { Receiver = second.Factory.NewEventReceiver(), Causality = root.Causality };

        Assert.ThrowsExactly<InvalidOperationException>(() => second.Dispatch(foreign).ToList());
        Assert.IsFalse(second.HasPendingEvents);
        TestContext.WriteLine("An event could not carry another context's causality into the queue.");
    }

    [TestMethod]
    public void DetachedScheduledWorkStartsANewCausalityWithoutChangingItsParent()
    {
        using var context = new Context(causalityFactory: new ProbeFactory());
        context.Logger.Output = null;
        EventReceiver receiver = context.Factory.NewEventReceiver();
        context.AddEventReceiver(receiver);
        var handler = new ProbeHandler();
        receiver.AddHandler(handler);
        var root = context.Factory.NewEvent<RootEvent>() with { Receiver = receiver };
        context.Dispatch(root).ToList();
        context.AdvanceTo(TimeSpan.FromSeconds(1));

        Assert.IsNotNull(root.Causality);
        Assert.IsNotNull(handler.Detached!.Causality);
        Assert.AreNotSame(root.Causality, handler.Detached.Causality);
        Assert.AreEqual(TimeSpan.FromSeconds(1), handler.Detached.Execute);
        var invalid = context.Factory.NewEvent<DerivedEvent>() with
        { Receiver = receiver, IsCausalityDetached = true, Causality = root.Causality };
        Assert.ThrowsExactly<InvalidOperationException>(() => context.Dispatch(invalid).ToList());
        TestContext.WriteLine("A detached timer received a new same-context root and rejected an explicitly supplied causality.");
    }

    [TestMethod]
    public void FactoryCannotAttachForeignCausalityToDetachedWork()
    {
        using var foreign = new Context();
        using var context = new Context(causalityFactory: new ForeignFactory(foreign));
        context.Logger.Output = null;
        var detached = context.Factory.NewEvent<DerivedEvent>() with
        { Receiver = context.Factory.NewEventReceiver(), IsCausalityDetached = true };
        var ordinary = context.Factory.NewEvent<DerivedEvent>() with
        { Receiver = context.Factory.NewEventReceiver() };

        Assert.ThrowsExactly<InvalidOperationException>(() => context.Dispatch(detached).ToList());
        Assert.ThrowsExactly<InvalidOperationException>(() => context.Dispatch(ordinary).ToList());
        Assert.IsFalse(context.HasPendingEvents);
        TestContext.WriteLine("Both detached and ordinary roots rejected a factory-created causality owned by another context.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ProcessingFailureRunsCleanupAndPreservesOriginalError(bool failCleanup)
    {
        using var context = new Context();
        context.Logger.Output = null;
        var state = new FailureState { FailCleanup = failCleanup };
        var ev = context.Factory.NewEvent<FailureEvent>() with
        {
            Receiver = context.Factory.NewEventReceiver(), State = state
        };
        context.EventProcessed += _ => throw new InvalidOperationException("observer failed");
        context.Dispatch(ev).ToList();
        Exception error = failCleanup
            ? Assert.ThrowsExactly<AggregateException>(() => context.AdvanceTo(TimeSpan.Zero))
            : Assert.ThrowsExactly<InvalidOperationException>(() => context.AdvanceTo(TimeSpan.Zero));
        StringAssert.Contains(error.ToString(), "observer failed");
        if (failCleanup) StringAssert.Contains(error.ToString(), "cleanup failed");
        Assert.AreEqual(1, state.Calls);
        Assert.IsFalse(context.IsProcessingEvent);
        TestContext.WriteLine($"Observer failure invoked cleanup; cleanup failure={failCleanup} remained visible.");
    }

    [TestMethod]
    public void HandlerInvocationFailureRunsOriginalEventsCleanup()
    {
        using var context = new Context();
        context.Logger.Output = null;
        EventReceiver receiver = context.Factory.NewEventReceiver();
        context.AddEventReceiver(receiver);
        receiver.AddHandler(new FailingHandler());
        var state = new FailureState();
        context.Dispatch(context.Factory.NewEvent<FailureEvent>() with
        {
            Receiver = receiver, State = state
        }).ToList();
        var error = Assert.ThrowsExactly<System.Reflection.TargetInvocationException>(() => context.AdvanceTo(TimeSpan.Zero));
        StringAssert.Contains(error.InnerException!.Message, "handler failed");
        Assert.AreEqual(1, state.Calls);
        TestContext.WriteLine("A handler invocation failure invoked cleanup on its original event.");
    }

    [TestMethod]
    public void CommittedEventFailurePreservesBothProcessingAndCleanupErrors()
    {
        using var context = new Context();
        context.Logger.Output = null;
        var state = new FailureState { FailCleanup = true };
        Guid operation = Guid.NewGuid();
        context.EventProcessed += _ => throw new InvalidOperationException("observer failed");
        context.Dispatch(context.Factory.NewEvent<FailureEvent>() with
        {
            Receiver = context.Factory.NewEventReceiver(), State = state, CommittedOperationId = operation
        }).ToList();
        var error = Assert.ThrowsExactly<CommittedOperationException>(() => context.AdvanceTo(TimeSpan.Zero));
        Assert.AreEqual(operation, error.OperationId);
        StringAssert.Contains(error.ToString(), "observer failed");
        StringAssert.Contains(error.ToString(), "cleanup failed");
        Assert.AreEqual(1, state.Calls);
        TestContext.WriteLine("A committed failure retained its operation identity and both underlying errors.");
    }

    private sealed class ProbeFactory : IEventCausalityFactory
    {
        public IEventCausality Create(Context context) => new ProbeCausality(context);
    }

    private sealed class ForeignFactory(Context foreign) : IEventCausalityFactory
    {
        public IEventCausality Create(Context context) => new ProbeCausality(foreign);
    }

    private sealed record ProbeCausality(Context Context) : IEventCausality;
    private sealed record RootEvent : Event;
    private sealed record DerivedEvent : Event;
    private sealed record ObserverEvent : Event;
    private sealed record InboxEvent : Event;
    private sealed class FailureState
    {
        public int Calls { get; set; }
        public bool FailCleanup { get; init; }
    }
    private sealed record FailureEvent : Event, IEventProcessingFailure
    {
        public FailureState State { get; init; } = null!;
        public void OnProcessingFailure(Context context)
        {
            State.Calls++;
            if (State.FailCleanup) throw new InvalidOperationException("cleanup failed");
        }
    }
    private sealed class FailingHandler : IHandler
    {
        public string Id => nameof(FailingHandler);
        [Handler]
        public IEnumerable<IEvent> OnFailure(FailureEvent ev) =>
            throw new InvalidOperationException("handler failed");
    }

    private sealed class CoreChildEvent : IEvent
    {
        public TimeSpan Create { get; set; }
        public TimeSpan Delay { get; set; }
        public TimeSpan? Execute { get; set; }
        public int Priority => 0;
        public EventCancellation CancellationToken => default;
        public IEventCausality? Causality { get; set; }
        public IEnumerable<IEvent> Dispatch() => [];
    }

    private sealed class ProbeHandler : IHandler
    {
        public string Id => nameof(ProbeHandler);
        public DerivedEvent? Derived { get; private set; }
        public CoreChildEvent? CoreChild { get; private set; }
        public InboxEvent? InboxChild { get; private set; }
        public DerivedEvent? Detached { get; private set; }

        [Handler]
        public IEnumerable<IEvent> OnRoot(RootEvent ev)
        {
            Derived = ev.Context.Factory.NewEvent<DerivedEvent>() with { Receiver = ev.Receiver };
            CoreChild = new CoreChildEvent();
            InboxChild = ev.Context.Factory.NewEvent<InboxEvent>() with { Receiver = ev.Receiver };
            Detached = ev.Context.Factory.NewEvent<DerivedEvent>() with
            { Receiver = ev.Receiver, Delay = TimeSpan.FromSeconds(1), IsCausalityDetached = true };
            ev.Context.Inbox.Post(InboxChild);
            yield return Derived;
            yield return CoreChild;
            yield return Detached;
        }
    }

    private sealed class ProbeObserver(EventReceiver receiver) : IContextObserver
    {
        public ObserverEvent? QueuedChild { get; private set; }
        public IEnumerable<IEvent> OnQueued(Context context, IEvent ev)
        {
            if (ev is RootEvent)
            {
                QueuedChild = context.Factory.NewEvent<ObserverEvent>() with { Receiver = receiver };
                yield return QueuedChild;
            }
        }
        public IEnumerable<IEvent> OnProcessed(Context context, IEvent ev) => [];
        public IEnumerable<IEvent> OnDispatched(Context context, IEvent ev, bool handlerInvocation) => [];
    }
}
