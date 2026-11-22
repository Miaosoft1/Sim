namespace Miaosoft.Sim.Core.Tests;

[TestClass]
public sealed class EventMessageBufferTest
{
    [TestMethod]
    public void AdjacentControlInputsKeepTheLatestAndPreserveBarriers()
    {
        var buffer = new EventMessageBuffer();
        buffer.Enqueue("one", new Control("move", 1));
        buffer.Enqueue("one", new Control("cast", 2));
        buffer.Enqueue("one", new Discrete("equip"));
        buffer.Enqueue("one", new Control("cast", 3));
        buffer.Enqueue("one", new Control("move", 4));
        buffer.Enqueue("two", new Control("move", 5));
        buffer.Enqueue("one", new Control("move", 6));

        var entries = buffer.Drain();
        CollectionAssert.AreEqual(
            new IEventMessage[]
            {
                new Control("cast", 2), new Discrete("equip"),
                new Control("move", 4), new Control("move", 5), new Control("move", 6)
            },
            entries.Select(entry => entry.Message).ToArray());
        Assert.AreEqual(2, buffer.MergedCount);
        Assert.AreEqual(0, buffer.Count);
    }

    [TestMethod]
    public void InvalidQueuePolicyLeavesExistingInputUnchanged()
    {
        var buffer = new EventMessageBuffer(1);
        buffer.Enqueue("one", new Control("move", 1));
        Assert.ThrowsExactly<ArgumentException>(() => buffer.Enqueue("one", new Unclassified()));
        Assert.ThrowsExactly<ArgumentException>(() => buffer.Enqueue("one", new Conflicting()));
        Assert.ThrowsExactly<ArgumentException>(() => buffer.Enqueue("one", new EmptyKey()));
        Assert.ThrowsExactly<InvalidOperationException>(() => buffer.Enqueue("one", new Discrete("equip")));

        buffer.Enqueue("one", new Control("move", 2));
        Assert.AreEqual(new Control("move", 2), buffer.Drain().Single().Message);
    }

    [TestMethod]
    public void AdmissionRejectionDoesNotOverwritePriorInputAndIsABarrier()
    {
        var context = new Context();
        context.Logger.Output = null;
        var receiver = context.Factory.NewEventReceiver();
        context.AddEventReceiver(receiver);
        var handler = new AdmissionHandler();
        receiver.AddHandler(handler);
        var processor = new ContextInputProcessor(context, receiver);

        processor.Process(
        [
            new("one", new Control("first", 1)),
            new("one", new Control("reject", 2)),
            new("one", new Control("last", 3)),
            new("one", new Discrete("equip")),
            new("one", new Control("after", 4))
        ]);

        CollectionAssert.AreEqual(
            new[] { "first", "rejected", "last", "equip", "after" },
            handler.Trace);
    }

    [TestMethod]
    public void InputRunsAtCurrentTimeWithoutAdvancingFutureEvents()
    {
        var context = new Context();
        context.Logger.Output = null;
        var receiver = context.Factory.NewEventReceiver();
        context.AddEventReceiver(receiver);
        var handler = new AdmissionHandler();
        receiver.AddHandler(handler);
        context.Dispatch(context.Factory.NewEvent<DelayedEvent>() with
        {
            Receiver = receiver,
            TimeSpan = TimeSpan.FromSeconds(1)
        }).ToList();

        new ContextInputProcessor(context, receiver).Process(
            [new("one", new Control("now", 1))]);

        CollectionAssert.AreEqual(new[] { "now" }, handler.Trace);
        Assert.AreEqual(TimeSpan.Zero, context.ContextTimeSpan);
        context.AdvanceTo(TimeSpan.FromSeconds(1));
        CollectionAssert.AreEqual(new[] { "now", "later" }, handler.Trace);
    }

    [TestMethod]
    public void CorrelationFollowsSurvivingInputAndItsDerivedEvents()
    {
        var context = new Context();
        context.Logger.Output = null;
        var receiver = context.Factory.NewEventReceiver();
        context.AddEventReceiver(receiver);
        receiver.AddHandler(new CorrelationHandler());
        var processed = new List<Guid?>();
        context.EventProcessed += ev =>
        {
            if (ev is DerivedEvent derived) processed.Add(derived.CorrelationId);
        };
        Guid replaced = Guid.NewGuid();
        Guid survivor = Guid.NewGuid();
        Guid discrete = Guid.NewGuid();

        new ContextInputProcessor(context, receiver).Process(
        [
            new("one", new Control("old", 1), replaced),
            new("one", new Control("new", 2), survivor),
            new("one", new Discrete("barrier"), discrete)
        ]);
        context.Dispatch(context.Factory.NewEvent<DerivedEvent>() with
        {
            Receiver = receiver, TimeSpan = TimeSpan.Zero
        }).ToList();
        context.RunUntil(context.ContextTimeSpan);

        CollectionAssert.AreEqual(new Guid?[] { survivor, survivor, discrete, discrete, null }, processed);
    }

    [TestMethod]
    public void InputEventsCanBeDrivenOneStepAtATimeWithoutConsumingFutureWork()
    {
        var context = new Context();
        context.Logger.Output = null;
        var receiver = context.Factory.NewEventReceiver();
        context.AddEventReceiver(receiver);
        var handler = new AdmissionHandler();
        receiver.AddHandler(handler);
        var processor = new ContextInputProcessor(context, receiver);
        context.Dispatch(context.Factory.NewEvent<DelayedEvent>() with
        {
            Receiver = receiver, TimeSpan = TimeSpan.FromSeconds(1)
        }).ToList();

        using var events = processor.CreateEvents(
        [
            new("one", new Control("first", 1)),
            new("one", new Control("reject", 2)),
            new("one", new Control("last", 3))
        ]).GetEnumerator();
        var observed = new List<IEvent>();
        while (events.MoveNext())
        {
            context.Dispatch(events.Current).ToList();
            while (context.HasEventsDueNow)
            {
                Assert.IsTrue(context.Next(out IEvent? ev));
                if (ev != null) observed.Add(ev);
            }
        }

        CollectionAssert.AreEqual(new[] { "first", "rejected", "last" }, handler.Trace);
        Assert.HasCount(3, observed.OfType<MessageAdmissionEvent>());
        Assert.AreEqual(TimeSpan.Zero, context.ContextTimeSpan);
        Assert.IsTrue(context.HasPendingEvents);
    }

    private sealed record Control(string Name, int Value) : ICoalescibleEvent
    {
        public string CoalescingKey => "control";
    }

    private sealed record Discrete(string Name) : IDiscreteEvent;
    private sealed record Unclassified : IEventMessage;
    private sealed record Conflicting : IDiscreteEvent, ICoalescibleEvent
    {
        public string CoalescingKey => "control";
    }
    private sealed record EmptyKey : ICoalescibleEvent
    {
        public string CoalescingKey => "";
    }
    private sealed record Rejection : IEventMessage;
    private sealed record DelayedEvent : Event;
    private sealed record DerivedEvent : Event;

    private sealed class CorrelationHandler : IHandler
    {
        public string Id => nameof(CorrelationHandler);

        [Handler]
        public IEnumerable<IEvent> Admit(MessageAdmissionEvent ev)
        {
            ev.Accepted = true;
            yield break;
        }

        [Handler]
        public IEnumerable<IEvent> Execute(MessageEvent<IEventMessage> ev)
        {
            ev.Context.Dispatch(ev.Context.Factory.NewEvent<DerivedEvent>() with
            {
                Receiver = ev.Receiver, TimeSpan = TimeSpan.Zero
            }).ToList();
            yield return ev.Context.Factory.NewEvent<DerivedEvent>() with
            {
                Receiver = ev.Receiver, TimeSpan = TimeSpan.Zero
            };
        }
    }

    private sealed class AdmissionHandler : IHandler
    {
        public string Id => nameof(AdmissionHandler);
        public List<string> Trace { get; } = [];

        [Handler]
        public IEnumerable<IEvent> Admit(MessageAdmissionEvent ev)
        {
            ev.Accepted = ev.Message is not Control { Name: "reject" };
            if (!ev.Accepted.Value) ev.Rejection = new Rejection();
            yield break;
        }

        [Handler]
        public IEnumerable<IEvent> Execute(MessageEvent<IEventMessage> ev)
        {
            Trace.Add(ev.Message switch
            {
                Control control => control.Name,
                Discrete discrete => discrete.Name,
                _ => throw new InvalidOperationException()
            });
            yield break;
        }

        [Handler]
        public IEnumerable<IEvent> Publish(MessagePublicationEvent ev)
        {
            Trace.Add("rejected");
            yield break;
        }

        [Handler]
        public IEnumerable<IEvent> Delayed(DelayedEvent ev)
        {
            Trace.Add("later");
            yield break;
        }
    }
}
