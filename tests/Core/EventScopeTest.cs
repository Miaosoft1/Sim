namespace Miaosoft.Sim.Core.Tests;

[TestClass]
public sealed class EventScopeTest
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void CoreWorkFromScopedObserversRequiresExplicitDetachedLifetime(bool detached)
    {
        using var context = new Context();
        context.Logger.Output = null;
        var receiver = context.Factory.NewEventReceiver();
        context.AddEventReceiver(receiver);
        using EventScope scope = context.CreateEventScope();
        CoreProbeEvent core = detached ? new DetachedCoreProbeEvent() : new CoreProbeEvent();
        core.Delay = TimeSpan.FromSeconds(1);
        context.AddObserver(new CoreProbeObserver(core));
        context.Dispatch(context.Factory.NewEvent<StartEvent>() with
        { Name = "scoped", Receiver = receiver, Scope = scope, CorrelationId = Guid.NewGuid() }).ToList();

        if (!detached)
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => context.RunUntil(TimeSpan.Zero));
            TestContext.WriteLine("Unmarked core work cannot silently escape its originating scope.");
            return;
        }

        context.RunUntil(TimeSpan.Zero);
        scope.Cancel();
        context.AdvanceTo(TimeSpan.FromSeconds(1));
        Assert.AreEqual(TimeSpan.FromSeconds(1), core.Execute);
        Assert.IsTrue(core.Dispatched);
        TestContext.WriteLine("Explicitly detached core publication ran after its originating scope closed.");
    }

    [TestMethod]
    public void ClosingOneScopeCancelsDerivedWorkButNotDetachedOrOtherScopeWork()
    {
        var context = new Context();
        context.Logger.Output = null;
        var receiver = context.Factory.NewEventReceiver();
        context.AddEventReceiver(receiver);
        var handler = new ProbeHandler();
        var observer = new ProbeObserver(receiver);
        receiver.AddHandler(handler);
        context.AddObserver(observer);
        using EventScope first = context.CreateEventScope();
        using EventScope second = context.CreateEventScope();

        context.Dispatch(context.Factory.NewEvent<StartEvent>() with
        { Name = "first", Receiver = receiver, Scope = first }).ToList();
        context.Dispatch(context.Factory.NewEvent<StartEvent>() with
        { Name = "second", Receiver = receiver, Scope = second }).ToList();
        context.RunUntil(TimeSpan.Zero);
        WorkEvent firstWork = observer.Queued.OfType<WorkEvent>().Single(ev => ev.Name == "first");
        WorkEvent secondWork = observer.Queued.OfType<WorkEvent>().Single(ev => ev.Name == "second");
        Assert.AreSame(first, firstWork.Scope);
        Assert.AreSame(second, secondWork.Scope);
        Assert.AreSame(first, observer.Queued.OfType<ObserverWorkEvent>().Single(ev => ev.Name == "first").Scope);
        Assert.AreSame(second, observer.Queued.OfType<ObserverWorkEvent>().Single(ev => ev.Name == "second").Scope);
        Assert.IsNull(observer.Queued.OfType<DetachedWorkEvent>().Single(ev => ev.Name == "first").Scope);
        bool canceled = false;
        using CancellationTokenRegistration registration = firstWork.CancellationToken.Register(() => canceled = true);
        first.Cancel();
        first.Cancel();
        Assert.IsTrue(canceled);
        Assert.IsFalse(firstWork.CancellationTokenSource!.IsCancellationRequested);
        Assert.IsFalse(secondWork.CancellationToken.IsCancellationRequested);

        context.AdvanceTo(TimeSpan.FromSeconds(8));
        CollectionAssert.AreEquivalent(
            new[] { "detached:first", "detached:second", "work:second", "observer:second" },
            handler.Results);
        Assert.IsNull(firstWork.Execute);
        Assert.DoesNotContain("work:first", observer.Processed);
        Assert.DoesNotContain("work:first", observer.Dispatched);
        Assert.Contains("work:second", observer.Processed);
        Assert.IsEmpty(context.Dispatch(context.Factory.NewEvent<WorkEvent>() with
        { Name = "closed", Receiver = receiver, Scope = first }));
        TestContext.WriteLine("Closing the first scope canceled its derived work and observer events; detached work and the second scope continued.");
    }

    [TestMethod]
    public void ClosedWorkDoesNotAdvanceTheClockAndLocalCancellationDoesNotCloseTheScope()
    {
        var context = new Context();
        context.Logger.Output = null;
        var receiver = context.Factory.NewEventReceiver();
        context.AddEventReceiver(receiver);
        using EventScope scope = context.CreateEventScope();
        using var local = new CancellationTokenSource();
        var canceledLocally = context.Factory.NewEvent<WorkEvent>() with
        { Name = "local", Receiver = receiver, Scope = scope, CancellationTokenSource = local };
        context.Dispatch(canceledLocally).ToList();
        local.Cancel();
        Assert.IsTrue(canceledLocally.CancellationToken.IsCancellationRequested);
        Assert.IsFalse(scope.IsCancellationRequested);
        context.RunUntil(TimeSpan.Zero);
        var future = context.Factory.NewEvent<WorkEvent>() with
        { Name = "future", Receiver = receiver, Scope = scope, TimeSpan = TimeSpan.FromDays(1) };
        context.Dispatch(future).ToList();
        scope.Cancel();
        Assert.IsFalse(context.HasPendingEvents);
        Assert.IsNull(context.NextEventTime);
        context.RunUntilQueueEmpty();
        Assert.AreEqual(TimeSpan.Zero, context.ContextTimeSpan);
        Assert.IsNull(future.Execute);
        TestContext.WriteLine("Local cancellation left the scope open; closing the scope discarded future work without advancing simulation time.");
    }

    [TestMethod]
    public void ClosingDuringAnObserverSkipsRemainingObserversAndAllowsDetachedCleanup()
    {
        var context = new Context();
        context.Logger.Output = null;
        var receiver = context.Factory.NewEventReceiver();
        context.AddEventReceiver(receiver);
        var handler = new ProbeHandler();
        receiver.AddHandler(handler);
        using EventScope scope = context.CreateEventScope();
        var canceling = new CancelingObserver(receiver, scope);
        var later = new ProbeObserver(receiver);
        context.AddObserver(canceling);
        context.AddObserver(later);
        var start = context.Factory.NewEvent<StartEvent>() with
        { Name = "stopped", Receiver = receiver, Scope = scope };
        context.Dispatch(start).ToList();
        context.RunUntilQueueEmpty();
        CollectionAssert.AreEqual(new[] { "detached:cleanup" }, handler.Results);
        Assert.DoesNotContain("start:stopped", later.Processed);
        Assert.DoesNotContain("start:stopped", later.Dispatched);
        Assert.HasCount(0, receiver.History.OfType<StartEvent>());
        TestContext.WriteLine("Cancellation during observation prevented the original dispatch and later observers while detached cleanup ran.");
    }

    [TestMethod]
    public void ScopeMustBelongToTheContextAndCannotAlsoBeDetached()
    {
        var first = new Context();
        var second = new Context();
        first.Logger.Output = second.Logger.Output = null;
        using EventScope scope = first.CreateEventScope();
        EventReceiver receiver = second.Factory.NewEventReceiver();
        Assert.ThrowsExactly<InvalidOperationException>(() => second.Dispatch(second.Factory.NewEvent<WorkEvent>() with
        { Name = "wrong", Receiver = receiver, Scope = scope }).ToList());
        Assert.ThrowsExactly<InvalidOperationException>(() => first.Dispatch(first.Factory.NewEvent<WorkEvent>() with
        { Name = "ambiguous", Receiver = receiver, Scope = scope, IsScopeDetached = true }).ToList());
        TestContext.WriteLine("Foreign scopes and contradictory detached scope declarations were rejected.");
    }

    [TestMethod]
    public void ClosingReleasesCanceledFutureWorkBehindOtherScopes()
    {
        var context = new Context();
        context.Logger.Output = null;
        var receiver = context.Factory.NewEventReceiver();
        context.AddEventReceiver(receiver);
        using EventScope closed = context.CreateEventScope();
        using EventScope live = context.CreateEventScope();
        context.Dispatch(context.Factory.NewEvent<WorkEvent>() with
        { Name = "live", Receiver = receiver, Scope = live, TimeSpan = TimeSpan.FromSeconds(1) }).ToList();
        WeakReference payload = QueueFuturePayload(context, receiver, closed);
        closed.Cancel();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.IsFalse(payload.IsAlive);
        Assert.AreEqual(TimeSpan.FromSeconds(1), context.NextEventTime);
        TestContext.WriteLine("A closed scope released its future queued payload without disturbing earlier work in another scope.");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference QueueFuturePayload(Context context, EventReceiver receiver, EventScope scope)
    {
        var payload = new object();
        var reference = new WeakReference(payload);
        context.Dispatch(context.Factory.NewEvent<PayloadEvent>() with
        {
            Payload = payload, Receiver = receiver, Scope = scope, TimeSpan = TimeSpan.FromDays(1)
        }).ToList();
        return reference;
    }

    private sealed record StartEvent : Event { public required string Name { get; init; } }
    private sealed record WorkEvent : Event { public required string Name { get; init; } }
    private sealed record PayloadEvent : Event { public required object Payload { get; init; } }
    private sealed record DetachedWorkEvent : Event { public required string Name { get; init; } }
    private sealed record ObserverWorkEvent : Event { public required string Name { get; init; } }

    private class CoreProbeEvent : IEvent
    {
        public TimeSpan Create { get; set; }
        public TimeSpan Delay { get; set; }
        public TimeSpan? Execute { get; set; }
        public int Priority => int.MaxValue;
        public EventCancellation CancellationToken => default;
        public IEventCausality? Causality { get; set; }
        public bool Dispatched { get; private set; }
        public IEnumerable<IEvent> Dispatch() { Dispatched = true; return []; }
    }

    private sealed class DetachedCoreProbeEvent : CoreProbeEvent, IDetachedCoreEvent;

    private sealed class CoreProbeObserver(IEvent core) : IContextObserver
    {
        public IEnumerable<IEvent> OnQueued(Context context, IEvent ev) => [];
        public IEnumerable<IEvent> OnProcessed(Context context, IEvent ev) => [];
        public IEnumerable<IEvent> OnDispatched(Context context, IEvent ev, bool handlerInvocation) =>
            ev is StartEvent && !handlerInvocation ? [core] : [];
    }

    private sealed class ProbeHandler : IHandler
    {
        public string Id => nameof(ProbeHandler);
        public List<string> Results { get; } = [];

        [Handler]
        public IEnumerable<IEvent> OnStart(StartEvent ev)
        {
            yield return ev.Context.Factory.NewEvent<WorkEvent>() with
            {
                Name = ev.Name, Receiver = ev.Receiver, TimeSpan = TimeSpan.FromSeconds(5),
                CancellationTokenSource = new CancellationTokenSource()
            };
            yield return ev.Context.Factory.NewEvent<DetachedWorkEvent>() with
            {
                Name = ev.Name, Receiver = ev.Receiver, TimeSpan = TimeSpan.FromSeconds(2),
                IsScopeDetached = true
            };
        }

        [Handler]
        public IEnumerable<IEvent> OnWork(WorkEvent ev) { Results.Add($"work:{ev.Name}"); return []; }
        [Handler]
        public IEnumerable<IEvent> OnDetached(DetachedWorkEvent ev) { Results.Add($"detached:{ev.Name}"); return []; }
        [Handler]
        public IEnumerable<IEvent> OnObserver(ObserverWorkEvent ev) { Results.Add($"observer:{ev.Name}"); return []; }
    }

    private sealed class ProbeObserver(EventReceiver receiver) : IContextObserver
    {
        public List<IEvent> Queued { get; } = [];
        public List<string> Processed { get; } = [];
        public List<string> Dispatched { get; } = [];

        public IEnumerable<IEvent> OnQueued(Context context, IEvent ev)
        {
            Queued.Add(ev);
            if (ev is WorkEvent work)
                yield return context.Factory.NewEvent<ObserverWorkEvent>() with
                { Name = work.Name, Receiver = receiver, TimeSpan = TimeSpan.FromSeconds(7) };
        }

        public IEnumerable<IEvent> OnProcessed(Context context, IEvent ev)
        {
            if (ev is StartEvent start) Processed.Add($"start:{start.Name}");
            if (ev is WorkEvent work) Processed.Add($"work:{work.Name}");
            return [];
        }

        public IEnumerable<IEvent> OnDispatched(Context context, IEvent ev, bool handlerInvocation)
        {
            if (ev is StartEvent start) Dispatched.Add($"start:{start.Name}");
            if (ev is WorkEvent work) Dispatched.Add($"work:{work.Name}");
            return [];
        }
    }

    private sealed class CancelingObserver(EventReceiver receiver, EventScope scope) : IContextObserver
    {
        public IEnumerable<IEvent> OnQueued(Context context, IEvent ev) => [];
        public IEnumerable<IEvent> OnProcessed(Context context, IEvent ev)
        {
            if (ev is StartEvent)
            {
                scope.Cancel();
                yield return context.Factory.NewEvent<DetachedWorkEvent>() with
                { Name = "cleanup", Receiver = receiver, IsScopeDetached = true };
            }
        }
    }
}
