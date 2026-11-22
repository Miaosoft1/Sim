namespace Miaosoft.Sim.Core.Tests;

[TestClass]
public sealed class ContextTimingTest
{
    public TestContext TestContext { get; set; } = null!;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public void DueWorkQueryCanExcludePublicationMarkersWithoutIgnoringOtherWork()
    {
        using var context = new Context();
        context.Logger.Output = null;
        var receiver = context.Factory.NewEventReceiver();
        receiver.AddHandler(new BoundaryHandler());
        context.AddEventReceiver(receiver);
        context.Dispatch(context.Factory.NewEvent<BoundaryEvent>() with { Receiver = receiver }).ToList();
        context.Dispatch(context.Factory.NewEvent<ProbeEvent>() with
        {
            Receiver = receiver, Delay = TimeSpan.FromSeconds(1)
        }).ToList();
        Assert.IsTrue(context.HasEventsDueNow);
        Assert.IsFalse(context.HasEventsDueNowExcept<BoundaryEvent>());
        Assert.IsTrue(context.Next(out _));
        Assert.IsTrue(context.HasEventsDueNow);
        Assert.IsFalse(context.HasEventsDueNowExcept<BoundaryEvent>());

        var work = context.Factory.NewEvent<ProbeEvent>() with { Receiver = receiver };
        context.Dispatch(work).ToList();
        Assert.IsTrue(context.HasEventsDueNowExcept<BoundaryEvent>());
        while (context.HasEventsDueNowExcept<BoundaryEvent>())
            Assert.IsTrue(context.Next(out _));
        Assert.IsFalse(context.HasEventsDueNowExcept<BoundaryEvent>());
        Assert.IsNull(context.History.OfType<ProbeEvent>().SingleOrDefault(ev => ev.Delay > TimeSpan.Zero));

        context.RequestStop();
        Assert.IsTrue(context.HasEventsDueNowExcept<BoundaryEvent>());
        TestContext.WriteLine("The due-work query excludes only boundary events, not ordinary work or termination.");
    }

    [TestMethod]
    public async Task EntireTimestampIncludingHandlersAndObserversRunsWithoutReadingClock()
    {
        var clock = new ManualTimeProvider();
        var context = new Context(realtime: true, timeProvider: clock);
        context.Logger.Output = null;
        var receiver = context.Factory.NewEventReceiver();
        receiver.AddHandler(new ChainHandler(), new TraceHandler());
        context.AddEventReceiver(receiver);
        context.AddObserver(new ChainObserver(receiver));
        context.Dispatch(context.Factory.NewEvent<ProbeEvent>() with { Receiver = receiver }).ToList();
        var future = context.Factory.NewEvent<ProbeEvent>() with
        { Receiver = receiver, Generation = 3, Delay = TimeSpan.FromSeconds(20) };
        context.Dispatch(future).ToList();

        int processed = 0;
        while (context.HasEventsDueNow)
        {
            ContextStepResult step = await context.NextAsync();
            Assert.IsTrue(step.KeepRunning);
            Assert.IsNotNull(step.Event);
            Assert.AreEqual(TimeSpan.Zero, step.Event.Execute);
            Assert.AreEqual(1, clock.TimestampReadCount);
            Assert.AreEqual(0, clock.PendingTimerCount);
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.IsLessThan(20, ++processed);
        }
        Assert.AreEqual(18, processed);
        Assert.IsNull(future.Execute);
        ContextStepResult idle = await context.NextAsync();
        Assert.IsNull(idle.Event);
        Assert.AreEqual(TimeSpan.FromSeconds(18), context.ContextTimeSpan);
        Assert.AreEqual(2, clock.TimestampReadCount);
        Assert.IsNull(future.Execute);
        TestContext.WriteLine($"{processed} same-timestamp steps, one startup clock read; first boundary advanced to {context.ContextTimeSpan}.");
    }

    [TestMethod]
    [DataRow(0.5)]
    [DataRow(1d)]
    [DataRow(2d)]
    public async Task RealtimeWaitAndCatchUpPreserveEveryIntermediateTimestamp(double scale)
    {
        var clock = new ManualTimeProvider();
        var context = new Context(realtime: true, timeScale: scale, timeProvider: clock);
        context.Logger.Output = null;
        var receiver = context.Factory.NewEventReceiver();
        context.AddEventReceiver(receiver);
        var events = Enumerable.Range(1, 4).Select(i => context.Factory.NewEvent<ProbeEvent>() with
        { Receiver = receiver, Delay = TimeSpan.FromSeconds(i / 4d * scale) }).ToArray();
        foreach (var ev in events) context.Dispatch(ev).ToList();
        Task<ContextStepResult> first = context.NextAsync().AsTask();
        await WaitForTimer(clock);
        Assert.IsFalse(first.IsCompleted);
        Assert.IsTrue(events.All(ev => ev.Execute == null));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreSame(events[0], (await first.WaitAsync(Timeout)).Event);
        for (int i = 1; i < events.Length; i++)
            Assert.AreSame(events[i], (await context.NextAsync()).Event);
        CollectionAssert.AreEqual(events.Select(ev => ev.Create + ev.Delay).ToArray(),
            events.Select(ev => ev.Execute!.Value).ToArray());
        Assert.AreEqual(TimeSpan.FromSeconds(scale), context.ContextTimeSpan);
        Assert.AreEqual(0, clock.PendingTimerCount);
        TestContext.WriteLine($"Scale {scale}: {string.Join(", ", events.Select(ev => ev.Execute))}");
    }

    [TestMethod]
    public async Task ScaleChangesOnlyAfterTimestampAndPauseDoesNotAccumulateTime()
    {
        var clock = new ManualTimeProvider();
        var context = new Context(realtime: true, timeProvider: clock);
        ContextEventInbox inbox = context.Inbox;
        context.Logger.Output = null;
        var receiver = context.Factory.NewEventReceiver();
        context.AddEventReceiver(receiver);
        var now = context.Factory.NewEvent<ProbeEvent>() with { Receiver = receiver };
        context.Dispatch(now).ToList();
        await context.NextAsync();
        context.TimeScale = 0;
        var immediate = context.Factory.NewEvent<ProbeEvent>() with { Receiver = receiver };
        context.Dispatch(immediate).ToList();
        Assert.AreSame(immediate, (await context.NextAsync()).Event);
        Assert.AreEqual(1d, context.TimeScale);
        Assert.AreEqual(1, clock.TimestampReadCount);
        await ApplyScale(context, 0);

        var future = context.Factory.NewEvent<ProbeEvent>() with
        { Receiver = receiver, Delay = TimeSpan.FromSeconds(2) };
        context.Dispatch(future).ToList();
        Task<ContextStepResult> waiting = context.NextAsync().AsTask();
        await WaitForTimer(clock);
        clock.Advance(TimeSpan.FromHours(1));
        inbox.RequestTimeScale(2);
        await waiting.WaitAsync(Timeout);
        await ApplyScale(context, 2);
        Assert.AreEqual(TimeSpan.Zero, context.ContextTimeSpan);
        Assert.IsNull(future.Execute);
        Task<ContextStepResult> resumed = context.NextAsync().AsTask();
        await WaitForTimer(clock);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreSame(future, (await resumed.WaitAsync(Timeout)).Event);
        Assert.AreEqual(TimeSpan.FromSeconds(2), future.Execute);
        TestContext.WriteLine("Paused for one wall hour, resumed at scale 2, advanced exactly two simulation seconds.");
    }

    [TestMethod]
    public async Task CancellationDoesNotDequeueFutureWorkAndImmediatePostsWakeWaitingStep()
    {
        var clock = new ManualTimeProvider();
        var context = new Context(realtime: true, timeProvider: clock);
        ContextEventInbox inbox = context.Inbox;
        context.Logger.Output = null;
        var receiver = context.Factory.NewEventReceiver();
        context.AddEventReceiver(receiver);
        var future = context.Factory.NewEvent<ProbeEvent>() with
        { Receiver = receiver, Delay = TimeSpan.FromDays(1) };
        context.Dispatch(future).ToList();
        using var cancellation = new CancellationTokenSource();
        Task<ContextStepResult> pending = context.NextAsync(cancellation.Token).AsTask();
        await WaitForTimer(clock);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => context.NextAsync().AsTask());
        Assert.ThrowsExactly<InvalidOperationException>(() => context.Next(out _));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        Assert.IsNull(future.Execute);
        Assert.AreEqual(0, clock.PendingTimerCount);

        var posted = context.Factory.NewEvent<ProbeEvent>() with { Receiver = receiver };
        Task<ContextStepResult> next = context.NextAsync().AsTask();
        await WaitForTimer(clock);
        await Task.Run(() => inbox.Post(posted));
        Assert.AreSame(posted, (await next.WaitAsync(Timeout)).Event);
        Assert.AreEqual(TimeSpan.Zero, posted.Execute);
        Assert.IsNull(future.Execute);
        Assert.AreEqual(0, clock.PendingTimerCount);
        context.RequestStop();
        var stopped = await context.NextAsync();
        Assert.IsFalse(stopped.KeepRunning);
        Assert.IsInstanceOfType<ExitEvent>(stopped.Event);
        TestContext.WriteLine("Canceled wait retained its future event; cross-thread post woke immediately and termination preempted the queue.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task IdleContextWakesForExternalWorkAndTermination(bool realtime)
    {
        var clock = new ManualTimeProvider();
        var context = new Context(realtime: realtime, timeProvider: clock);
        ContextEventInbox inbox = context.Inbox;
        context.Logger.Output = null;
        Task<ContextStepResult> waiting = context.NextAsync().AsTask();
        Assert.IsFalse(waiting.IsCompleted);
        inbox.Signal();
        Assert.IsNull((await waiting.WaitAsync(Timeout)).Event);
        Task<ContextStepResult> stopped = context.NextAsync().AsTask();
        inbox.RequestStop();
        Assert.IsFalse((await stopped.WaitAsync(Timeout)).KeepRunning);
        Assert.AreEqual(0, clock.PendingTimerCount);
    }

    [TestMethod]
    public async Task IdleTimePublicationIsPacedRatherThanProducingContinuousFrames()
    {
        var clock = new ManualTimeProvider();
        var context = new Context(realtime: true, timeProvider: clock);
        context.Logger.Output = null;
        Task<ContextStepResult> first = context.NextAsync().AsTask();
        await WaitForTimer(clock);
        clock.Advance(TimeSpan.FromMilliseconds(20));
        Assert.IsNull((await first.WaitAsync(Timeout)).Event);
        Assert.AreEqual(TimeSpan.FromMilliseconds(20), context.ContextTimeSpan);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Task<ContextStepResult> second = context.NextAsync().AsTask();
        await WaitForTimer(clock);
        Assert.IsFalse(second.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(19));
        Assert.IsNull((await second.WaitAsync(Timeout)).Event);
        Assert.AreEqual(TimeSpan.FromMilliseconds(40), context.ContextTimeSpan);
        TestContext.WriteLine("Idle publication is paced inside Context; sub-interval wall time does not create another frame.");
    }

    [TestMethod]
    public async Task InboxScaleRequestDoesNotAdvanceFutureEventsBeforeTheyAreDue()
    {
        var clock = new ManualTimeProvider();
        var context = new Context(realtime: true, timeProvider: clock);
        context.Logger.Output = null;
        var receiver = context.Factory.NewEventReceiver();
        context.AddEventReceiver(receiver);
        var future = context.Factory.NewEvent<ProbeEvent>() with
        { Receiver = receiver, Delay = TimeSpan.FromSeconds(1) };
        context.Dispatch(future).ToList();
        context.Inbox.RequestTimeScale(2);

        Assert.IsNull((await context.NextAsync()).Event);
        Assert.IsNull((await context.NextAsync()).Event);
        Assert.AreEqual(2d, context.TimeScale);
        Assert.AreEqual(TimeSpan.Zero, context.ContextTimeSpan);
        Assert.IsNull(future.Execute);
        Task<ContextStepResult> waiting = context.NextAsync().AsTask();
        await WaitForTimer(clock);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.AreSame(future, (await waiting.WaitAsync(Timeout)).Event);
        Assert.AreEqual(TimeSpan.FromSeconds(1), future.Execute);
    }

    [TestMethod]
    public async Task InboxRepeatedlyWakesIdleOwnerWithoutLosingSignals()
    {
        var context = new Context();
        ContextEventInbox inbox = context.Inbox;
        context.Logger.Output = null;
        for (int i = 0; i < 100; i++)
        {
            Task<ContextStepResult> waiting = context.NextAsync().AsTask();
            await Task.Run(inbox.Signal);
            Assert.IsNull((await waiting.WaitAsync(Timeout)).Event);
        }
        TestContext.WriteLine("One hundred cross-thread signals woke the single execution owner.");
    }

    [TestMethod]
    public async Task PostingDoesNotHoldTheInboxLockAcrossEventExecution()
    {
        var context = new Context();
        context.Logger.Output = null;
        var receiver = context.Factory.NewEventReceiver();
        context.AddEventReceiver(receiver);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        context.EventProcessed += _ =>
        {
            entered.TrySetResult();
            if (!release.Wait(Timeout))
                throw new TimeoutException("Test did not release the active event.");
        };
        Task<ContextStepResult> step = context.NextAsync().AsTask();
        ContextEventInbox inbox = context.Inbox;
        Task posting = Task.Run(() => inbox.Post(new ProbeEvent { Receiver = receiver }));
        try
        {
            await entered.Task.WaitAsync(Timeout);
            await posting.WaitAsync(Timeout);
        }
        finally { release.Set(); }
        Assert.IsNotNull((await step.WaitAsync(Timeout)).Event);
    }

    [TestMethod]
    public async Task WakingDoesNotHoldTheInboxLockWhileDisposingTheTimer()
    {
        using var clock = new BlockingTimerProvider();
        var context = new Context(realtime: true, timeProvider: clock);
        context.Logger.Output = null;
        ContextEventInbox inbox = context.Inbox;
        Task<ContextStepResult> waiting = context.NextAsync().AsTask();
        await clock.Created.Task.WaitAsync(Timeout);
        Task first = Task.Run(inbox.Signal);
        try
        {
            await clock.Disposing.Task.WaitAsync(Timeout);
            Task second = Task.Run(inbox.Signal);
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { clock.Release(); }
        Assert.IsNull((await waiting.WaitAsync(Timeout)).Event);
    }

    [TestMethod]
    public async Task ConcurrentInboxPostsAreDrainedByOneExecutionOwner()
    {
        var context = new Context();
        context.Logger.Output = null;
        var receiver = context.Factory.NewEventReceiver();
        context.AddEventReceiver(receiver);
        var posted = Enumerable.Range(0, 200).Select(_ => new ProbeEvent { Receiver = receiver }).ToArray();
        ContextEventInbox inbox = context.Inbox;
        using var cancellation = new CancellationTokenSource(Timeout);
        Task owner = Task.Run(async () =>
        {
            int processed = 0;
            while (processed < posted.Length)
            {
                ContextStepResult step = await context.NextAsync(cancellation.Token);
                Assert.IsTrue(step.KeepRunning);
                if (step.Event is ProbeEvent) processed++;
            }
        });
        await Task.WhenAll(posted.Select(ev => Task.Run(() => inbox.Post(ev)))).WaitAsync(Timeout);
        await owner.WaitAsync(Timeout);
        Assert.IsTrue(posted.All(ev => ev.Execute == TimeSpan.Zero));
        TestContext.WriteLine($"One owner processed {posted.Length} concurrently posted events at the current timestamp.");
    }

    [TestMethod]
    public async Task InboxTerminationPreemptsImmediateAndFutureEvents()
    {
        var context = new Context();
        context.Logger.Output = null;
        var receiver = context.Factory.NewEventReceiver();
        context.AddEventReceiver(receiver);
        var immediate = context.Factory.NewEvent<ProbeEvent>() with { Receiver = receiver, Priority = int.MinValue };
        var future = context.Factory.NewEvent<ProbeEvent>() with { Receiver = receiver, Delay = TimeSpan.FromSeconds(1) };
        context.Dispatch(future).ToList();
        context.Inbox.Post(immediate);
        context.Inbox.RequestStop();

        ContextStepResult result = await context.NextAsync();
        Assert.IsFalse(result.KeepRunning);
        Assert.IsInstanceOfType<ExitEvent>(result.Event);
        Assert.IsNull(immediate.Execute);
        Assert.IsNull(future.Execute);
        Assert.IsTrue(context.HasPendingEvents);
    }

    [TestMethod]
    public void InboxBindsNewEventsAndRejectsEventsFromAnotherContext()
    {
        var context = new Context();
        context.Logger.Output = null;
        var receiver = context.Factory.NewEventReceiver();
        context.AddEventReceiver(receiver);
        var unbound = new ProbeEvent { Receiver = receiver };
        context.Inbox.Post(unbound);
        Assert.IsTrue(context.Next(out IEvent? processed));
        Assert.AreSame(unbound, processed);
        Assert.AreSame(context, unbound.Context);
        Assert.AreEqual(TimeSpan.Zero, unbound.Execute);

        var other = new Context();
        other.Logger.Output = null;
        other.Inbox.Post(context.Factory.NewEvent<ProbeEvent>() with { Receiver = receiver });
        Assert.ThrowsExactly<InvalidOperationException>(() => other.Next(out _));
    }

    [TestMethod]
    public void DefaultDelayIsZeroAndRunsBeforeFutureEvents()
    {
        var context = new Context();
        context.Logger.Output = null;
        var receiver = context.Factory.NewEventReceiver();
        context.AddEventReceiver(receiver);
        context.Dispatch(context.Factory.NewEvent<ProbeEvent>() with
        { Receiver = receiver, Delay = TimeSpan.FromSeconds(1) }).ToList();
        var immediate = context.Factory.NewEvent<ProbeEvent>() with { Receiver = receiver };
        context.Dispatch(immediate).ToList();
        Assert.AreEqual(typeof(TimeSpan), typeof(IEvent).GetProperty(nameof(IEvent.Delay))!.PropertyType);
        Assert.AreEqual(TimeSpan.Zero, immediate.Delay);
        Assert.IsTrue(context.Next(out var processed));
        Assert.AreSame(immediate, processed);
        Assert.AreEqual(TimeSpan.Zero, context.ContextTimeSpan);
    }

    [TestMethod]
    public void InvalidTimeScalesAreRejectedAtConstructionAndRuntime()
    {
        foreach (double scale in new[] { -1d, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Context(timeScale: scale));
            var context = new Context();
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => context.TimeScale = scale);
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => context.Inbox.RequestTimeScale(scale));
        }
    }

    private static async Task ApplyScale(Context context, double expected)
    {
        for (int attempts = 0; context.TimeScale != expected && attempts < 3; attempts++)
            await context.NextAsync().AsTask().WaitAsync(Timeout);
        Assert.AreEqual(expected, context.TimeScale);
    }

    private static async Task WaitForTimer(ManualTimeProvider clock)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (clock.PendingTimerCount == 0) await Task.Delay(1, timeout.Token);
    }

    public sealed record ProbeEvent : Event
    {
        public int Generation { get; init; }
    }

    public sealed record BoundaryEvent : Event;

    private sealed class BoundaryHandler : IHandler
    {
        public string Id => nameof(BoundaryHandler);
        [Handler]
        public IEnumerable<Event> Handle(BoundaryEvent ev) => [];
    }

    private sealed class BlockingTimerProvider : TimeProvider, IDisposable
    {
        private readonly ManualResetEventSlim release = new();
        public TaskCompletionSource Created { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => 0;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Created.TrySetResult();
            return new BlockingTimer(this);
        }
        public void Release() => release.Set();
        public void Dispose() => release.Dispose();

        private sealed class BlockingTimer(BlockingTimerProvider owner) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose()
            {
                owner.Disposing.TrySetResult();
                if (!owner.release.Wait(Timeout))
                    throw new TimeoutException("Test did not release timer disposal.");
            }
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class ChainHandler : IHandler
    {
        public string Id => "chain";
        [Handler]
        public IEnumerable<Event> Handle(ProbeEvent ev)
        {
            if (ev.Generation < 2)
                yield return ev.Context.Factory.NewEvent<ProbeEvent>() with
                { Receiver = ev.Receiver, Generation = ev.Generation + 1 };
        }
    }

    private sealed class TraceHandler : IHandler
    {
        public string Id => "trace";
        [Handler]
        public IEnumerable<Event> Handle(ProbeEvent ev)
        {
            return [];
        }
    }

    private sealed class ChainObserver(EventReceiver receiver) : IContextObserver
    {
        public IEnumerable<IEvent> OnQueued(Context context, IEvent ev) => [];
        public IEnumerable<IEvent> OnProcessed(Context context, IEvent ev)
        {
            if (ev is ProbeEvent { Generation: 0 })
                yield return context.Factory.NewEvent<ProbeEvent>() with { Receiver = receiver, Generation = 3 };
        }

        public IEnumerable<IEvent> OnDispatched(Context context, IEvent ev, bool handlerInvocation)
        {
            if (handlerInvocation && ev is ProbeEvent { Generation: 0 })
                yield return context.Factory.NewEvent<ProbeEvent>() with { Receiver = receiver, Generation = 4 };
        }
    }
}
