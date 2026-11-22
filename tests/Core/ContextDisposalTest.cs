namespace Miaosoft.Sim.Core.Tests;

[TestClass]
public sealed class ContextDisposalTest
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void DisposalCancelsScopesAndReleasesHistoryQueuedWorkAndSubscribers()
    {
        var context = new Context();
        context.Logger.Output = null;
        var receiver = context.Factory.NewEventReceiver();
        context.AddEventReceiver(receiver);
        EventScope scope = context.CreateEventScope();
        bool canceled = false;
        using CancellationTokenRegistration registration = scope.Token.Register(() => canceled = true);
        WeakReference payload = TrackPayload(context, receiver, scope);
        Assert.IsTrue(context.HasPendingEvents);

        context.Dispose();
        context.Dispose();
        Assert.IsTrue(context.IsDisposed);
        Assert.IsTrue(scope.IsCancellationRequested);
        Assert.IsTrue(canceled);
        Assert.IsFalse(context.HasPendingEvents);
        Assert.IsEmpty(context.History);
        Assert.IsEmpty(receiver.History);
        Assert.IsFalse(context.Inbox.TryPost(new PayloadEvent { Payload = new object(), Receiver = receiver }));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            context.Inbox.Post(new PayloadEvent { Payload = new object(), Receiver = receiver }));
        Assert.ThrowsExactly<InvalidOperationException>(() => context.Inbox.RequestStop());
        Assert.ThrowsExactly<InvalidOperationException>(() => context.Inbox.RequestTimeScale(2));
        Assert.ThrowsExactly<ObjectDisposedException>(() =>
            context.Dispatch(context.Factory.NewEvent<PayloadEvent>() with { Payload = new object(), Receiver = receiver }).ToList());
        Assert.ThrowsExactly<ObjectDisposedException>(() => context.CreateEventScope());

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.IsFalse(payload.IsAlive);
        GC.KeepAlive(context);
        TestContext.WriteLine("Context shutdown canceled scopes and released queued events, receiver history and event subscribers.");
    }

    [TestMethod]
    public void AFailingCancellationCallbackDoesNotPreventOtherScopesOrReferencesFromClosing()
    {
        var context = new Context();
        context.Logger.Output = null;
        EventScope first = context.CreateEventScope();
        EventScope second = context.CreateEventScope();
        using CancellationTokenRegistration registration =
            first.Token.Register(() => throw new InvalidOperationException("expected cancellation failure"));
        var failure = Assert.ThrowsExactly<AggregateException>(() => context.Dispose());
        StringAssert.Contains(failure.ToString(), "expected cancellation failure");
        Assert.IsTrue(context.IsDisposed);
        Assert.IsTrue(first.IsCancellationRequested);
        Assert.IsTrue(second.IsCancellationRequested);
        Assert.IsFalse(context.HasPendingEvents);
        context.Dispose();
        TestContext.WriteLine("A failing scope cancellation was reported after all scopes were closed.");
    }

    [TestMethod]
    public void DisposalIsRejectedDuringAnEventAndCanBeRetriedAfterward()
    {
        var context = new Context();
        context.Logger.Output = null;
        var receiver = context.Factory.NewEventReceiver();
        context.AddEventReceiver(receiver);
        bool observed = false;
        context.EventProcessed += ev =>
        {
            if (ev is not PayloadEvent) return;
            Assert.ThrowsExactly<InvalidOperationException>(context.Dispose);
            Assert.IsFalse(context.IsDisposed);
            observed = true;
        };
        context.Dispatch(context.Factory.NewEvent<PayloadEvent>() with
        { Payload = new object(), Receiver = receiver }).ToList();

        context.RunUntil(TimeSpan.Zero);
        Assert.IsTrue(observed);
        context.Dispose();
        Assert.IsTrue(context.IsDisposed);
        TestContext.WriteLine("Disposal during an event was rejected without closing the active context; retry succeeded afterward.");
    }

    [TestMethod]
    public async Task DisposalIsRejectedWhileAnAsyncStepIsWaiting()
    {
        var context = new Context();
        context.Logger.Output = null;
        using var cancellation = new CancellationTokenSource();
        Task<ContextStepResult> waiting = context.NextAsync(cancellation.Token).AsTask();
        Assert.IsFalse(waiting.IsCompleted);
        Assert.ThrowsExactly<InvalidOperationException>(context.Dispose);
        Assert.IsFalse(context.IsDisposed);

        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () =>
            await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        context.Dispose();
        Assert.IsTrue(context.IsDisposed);
        TestContext.WriteLine("Disposal was rejected until the pending async owner's execution step stopped.");
    }

    [TestMethod]
    public async Task ClosingTheInboxWhileAPostIsBeingNotifiedDiscardsTheAcceptedWork()
    {
        var context = new Context();
        context.Logger.Output = null;
        var receiver = context.Factory.NewEventReceiver();
        context.AddEventReceiver(receiver);
        using var notifying = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        context.Inbox.EventPosted += () =>
        {
            notifying.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The inbox post notification was not released.");
        };
        Task<bool> posting = Task.Run(() =>
            context.Inbox.TryPost(new PayloadEvent { Payload = new object(), Receiver = receiver }));
        try
        {
            Assert.IsTrue(notifying.Wait(TimeSpan.FromSeconds(5)));
            context.Dispose();
            Assert.IsFalse(context.HasPendingEvents);
            Assert.IsFalse(context.Inbox.TryPost(new PayloadEvent { Payload = new object(), Receiver = receiver }));
        }
        finally
        {
            resume.Set();
        }
        Assert.IsTrue(await posting.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsFalse(context.HasPendingEvents);
        TestContext.WriteLine("An accepted post racing shutdown was discarded, and subsequent posts were rejected.");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void UnregisteredPublicationReceiverUsesTheEventContextsHistoryPolicy(bool recordHistory)
    {
        using var context = new Context();
        context.Logger.Output = null;
        if (!recordHistory) context.DisableEventHistory();
        var receiver = new EventReceiver();
        var ev = context.Factory.NewEvent<PayloadEvent>() with
        { Payload = new object(), Receiver = receiver };
        context.Dispatch(ev).ToList();
        context.RunUntilQueueEmpty();
        Assert.AreEqual(TimeSpan.Zero, ev.Execute);
        Assert.HasCount(recordHistory ? 1 : 0, receiver.History);
        TestContext.WriteLine($"Unregistered publication receiver dispatched with event history={recordHistory}.");
    }

    [TestMethod]
    public void EventHistoryCanBeDisabledWithoutStoppingDispatchOrTextLogging()
    {
        using var context = new Context();
        context.Logger.Output = null;
        var receiver = context.Factory.NewEventReceiver();
        context.AddEventReceiver(receiver);
        int processed = 0;
        context.EventProcessed += ev =>
        {
            if (ev is not PayloadEvent) return;
            processed++;
            Assert.ThrowsExactly<InvalidOperationException>(context.DisableEventHistory);
        };
        context.Dispatch(context.Factory.NewEvent<PayloadEvent>() with
        { Payload = new object(), Receiver = receiver }).ToList();
        context.RunUntil(TimeSpan.Zero);
        Assert.IsNotEmpty(context.History);

        context.DisableEventHistory();
        context.DisableEventHistory();
        Assert.IsFalse(context.RecordsEventHistory);
        Assert.IsEmpty(context.History);
        Assert.IsEmpty(receiver.History);
        context.Dispatch(context.Factory.NewEvent<PayloadEvent>() with
        { Payload = new object(), Receiver = receiver }).ToList();
        context.RunUntil(TimeSpan.Zero);
        Assert.AreEqual(2, processed);
        Assert.IsEmpty(context.History);
        Assert.IsEmpty(receiver.History);
        Assert.IsNotEmpty(context.Logger.Log);
        TestContext.WriteLine("Disabling object history cleared past events but preserved dispatch, observers and textual logs.");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference TrackPayload(Context context, EventReceiver receiver, EventScope scope)
    {
        var payload = new object();
        var weak = new WeakReference(payload);
        context.EventProcessed += _ => GC.KeepAlive(payload);
        context.Dispatch(context.Factory.NewEvent<PayloadEvent>() with
        { Payload = payload, Receiver = receiver }).ToList();
        context.RunUntil(TimeSpan.Zero);
        Assert.IsNotEmpty(context.History);
        context.Dispatch(context.Factory.NewEvent<PayloadEvent>() with
        { Payload = payload, Receiver = receiver, Scope = scope, TimeSpan = TimeSpan.FromDays(1) }).ToList();
        return weak;
    }

    private sealed record PayloadEvent : Event
    {
        public required object Payload { get; init; }
    }
}
