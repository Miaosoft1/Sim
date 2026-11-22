using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Core.Tests
{
    [TestClass]
    public class DispatchRoutingTest
    {
        [TestMethod]
        public void ClearingPendingEventsAllowsNewWorkWithoutResumingOldWork()
        {
            var context = new Context();
            var receiver = context.Factory.NewEventReceiver();
            context.AddEventReceiver(receiver);
            var trace = new List<string>();
            receiver.AddHandler(new TraceHandler { Id = "clear", Name = "executed", Trace = trace });
            context.Dispatch(context.Factory.NewEvent<PingEvent>() with
            { Receiver = receiver, TimeSpan = TimeSpan.FromSeconds(5) }).ToList();
            context.Inbox.Post(context.Factory.NewEvent<PingEvent>() with { Receiver = receiver });
            context.RequestStop();
            context.ClearPendingEvents();
            Assert.IsFalse(context.HasPendingEvents);
            context.Dispatch(context.Factory.NewEvent<PingEvent>() with
            { Receiver = receiver, TimeSpan = TimeSpan.Zero }).ToList();
            Assert.IsFalse(context.RunUntilQueueEmpty().Terminated);
            Assert.HasCount(1, trace);
            Assert.AreEqual(TimeSpan.Zero, context.ContextTimeSpan);
        }

        [TestMethod]
        public void RestrictedInboxRejectsPreviouslyQueuedAndLaterWorkWithoutStoppingTheOwner()
        {
            var context = new Context();
            context.Logger.Output = null;
            var receiver = context.Factory.NewEventReceiver();
            context.AddEventReceiver(receiver);
            var trace = new List<string>();
            receiver.AddHandler(new TraceHandler { Id = "restricted", Name = "accepted", Trace = trace });
            var accepted = context.Factory.NewEvent<PingEvent>() with { Receiver = receiver };
            var queuedBefore = context.Factory.NewEvent<PingEvent>() with { Receiver = receiver };
            var queuedAfter = context.Factory.NewEvent<PingEvent>() with { Receiver = receiver };

            context.Inbox.Post(queuedBefore);
            context.Inbox.RequestStop();
            context.Inbox.RestrictPostedEvents(ev => ReferenceEquals(ev, accepted));
            context.Inbox.Post(accepted);
            context.Inbox.Post(queuedAfter);
            context.Inbox.RequestTimeScale(2);
            Assert.IsFalse(context.RunUntilQueueEmpty().Terminated);

            CollectionAssert.AreEqual(new[] { "accepted" }, trace);
            Assert.IsNull(queuedBefore.Execute);
            Assert.IsNull(queuedAfter.Execute);
            Assert.AreEqual(1d, context.TimeScale);
            Assert.AreEqual(4, context.Logger.Log.Count(line =>
                line.ToString().Contains("context inbox no longer accepts", StringComparison.Ordinal)));
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void PostDispatchObserverSeesCompletedHandlersAndSkipsCancellation(bool cancel)
        {
            var context = new Context();
            var receiver = context.Factory.NewEventReceiver();
            context.AddEventReceiver(receiver);
            var trace = new List<string>();
            receiver.AddHandler(new TraceHandler { Id = "first", Name = "first", Trace = trace },
                new TraceHandler { Id = "second", Name = "second", Trace = trace });
            var observer = new PostDispatchObserver(trace);
            context.AddObserver(observer);
            using var cancellation = new CancellationTokenSource();
            var ping = context.Factory.NewEvent<PingEvent>() with
            {
                Receiver = receiver, TimeSpan = TimeSpan.Zero, CancellationTokenSource = cancellation
            };
            context.Dispatch(ping).ToList();
            if (cancel) cancellation.Cancel();
            context.RunUntilQueueEmpty();
            Assert.IsFalse(context.IsProcessingEvent);
            CollectionAssert.AreEqual(cancel ? Array.Empty<string>() :
                new[] { "first", "after:first", "second", "after:second" }, trace);
            Assert.HasCount(cancel ? 0 : 2, observer.Events);
            Assert.IsTrue(observer.Events.All(ev => ReferenceEquals(ev, ping)));
        }

        private sealed class PostDispatchObserver(List<string> trace) : IContextObserver
        {
            public List<IEvent> Events { get; } = [];
            public IEnumerable<IEvent> OnQueued(Context context, IEvent ev) => [];
            public IEnumerable<IEvent> OnProcessed(Context context, IEvent ev) => [];
            public IEnumerable<IEvent> OnDispatched(Context context, IEvent ev, bool handlerInvocation)
            {
                Assert.IsTrue(context.IsProcessingEvent);
                if (handlerInvocation)
                {
                    Events.Add(ev);
                    trace.Add("after:" + trace[^1]);
                }
                return [];
            }
        }

        [TestMethod]
        public void AdvanceTo_IdleClockDoesNotExecuteFutureEventsEarly()
        {
            var context = new Context();
            var receiver = context.Factory.NewEventReceiver();
            context.AddEventReceiver(receiver);
            var trace = new List<string>();
            receiver.AddHandler(new TraceHandler { Id = "clock", Name = "executed", Trace = trace });
            context.AdvanceTo(TimeSpan.FromSeconds(3));
            var ev = context.Factory.NewEvent<PingEvent>() with
            {
                Receiver = receiver,
                TimeSpan = TimeSpan.FromSeconds(0.5)
            };
            context.Dispatch(ev).ToList();
            context.AdvanceTo(TimeSpan.FromSeconds(3.4));
            Assert.AreEqual(TimeSpan.FromSeconds(3.4), context.ContextTimeSpan);
            Assert.IsEmpty(trace);
            context.AdvanceTo(TimeSpan.FromSeconds(3.5));
            Assert.AreEqual(TimeSpan.FromSeconds(3.5), ev.Execute);
            CollectionAssert.AreEqual(new[] { "executed" }, trace);
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => context.AdvanceTo(TimeSpan.Zero));
        }

        [TestMethod]
        public async Task InboxPost_QueuesConcurrentWorkOnTheExecutionOwner()
        {
            var context = new Context();
            context.Logger.Output = null;
            var receiver = context.Factory.NewEventReceiver();
            context.AddEventReceiver(receiver);
            var trace = new List<string>();
            receiver.AddHandler(new TraceHandler { Id = "posted", Name = "executed", Trace = trace });
            var future = context.Factory.NewEvent<PingEvent>() with
            {
                Receiver = receiver, TimeSpan = TimeSpan.FromSeconds(10)
            };
            context.Dispatch(future).ToList();
            var posted = Enumerable.Range(0, 100)
                .Select(_ => context.Factory.NewEvent<PingEvent>() with { Receiver = receiver })
                .ToArray();
            context.AdvanceTo(TimeSpan.FromSeconds(3));
            int wakeups = 0;
            int queued = 0;
            ContextEventInbox inbox = context.Inbox;
            inbox.EventPosted += () => Interlocked.Increment(ref wakeups);
            context.EventQueued += _ => queued++;

            await Task.WhenAll(posted.Select(ev => Task.Run(() => inbox.Post(ev))));

            Assert.AreEqual(100, wakeups);
            Assert.AreEqual(0, queued);
            Assert.IsTrue(context.HasPendingEvents);
            Assert.AreEqual(TimeSpan.FromSeconds(3), context.NextEventTime);
            context.AdvanceTo(TimeSpan.FromSeconds(3.1));
            Assert.HasCount(100, trace);
            Assert.AreEqual(200, queued);
            Assert.IsTrue(posted.All(ev => ev.Create == TimeSpan.FromSeconds(3)
                && ev.Execute == TimeSpan.FromSeconds(3)));
            Assert.IsNull(future.Execute);
        }

        [TestMethod]
        public void InboxPost_DoesNotInheritTheCurrentlyProcessedEventCorrelation()
        {
            var context = new Context();
            context.Logger.Output = null;
            var receiver = context.Factory.NewEventReceiver();
            context.AddEventReceiver(receiver);
            var first = context.Factory.NewEvent<PingEvent>() with
            {
                Receiver = receiver, TimeSpan = TimeSpan.Zero, CorrelationId = Guid.NewGuid()
            };
            var posted = context.Factory.NewEvent<PingEvent>() with { Receiver = receiver };
            bool queued = false;
            context.EventQueued += ev =>
            {
                if (ReferenceEquals(ev, posted)) queued = true;
            };
            context.EventProcessed += ev =>
            {
                if (!ReferenceEquals(ev, first)) return;
                context.Inbox.Post(posted);
                Assert.IsTrue(context.HasEventsDueNow);
                Assert.IsTrue(context.HasEventsDueNow);
                Assert.IsFalse(queued);
            };
            context.Dispatch(first).ToList();

            context.RunUntilQueueEmpty();

            Assert.IsTrue(queued);
            Assert.IsNull(posted.CorrelationId);
            Assert.AreEqual(TimeSpan.Zero, posted.Execute);
        }

        [TestMethod]
        public void CancelledEventDoesNotNotifyPresentationObservers()
        {
            var context = new Context();
            var receiver = context.Factory.NewEventReceiver();
            context.AddEventReceiver(receiver);
            int processed = 0;
            context.EventProcessed += _ => processed++;
            var token = new CancellationTokenSource();
            context.Dispatch(context.Factory.NewEvent<PingEvent>() with
            {
                Receiver = receiver,
                TimeSpan = TimeSpan.FromSeconds(1),
                CancellationTokenSource = token
            }).ToList();
            token.Cancel();
            context.AdvanceTo(TimeSpan.FromSeconds(1));
            Assert.AreEqual(0, processed);
        }

        [TestMethod]
        public void Dispatch_InvokesMatchingHandlersInRegistrationOrder()
        {
            Context context = new Context();
            EventReceiver taker = context.Factory.NewEventReceiver();
            context.AddEventReceiver(taker);

            List<string> trace = new List<string>();
            taker.AddHandler(new TraceHandler { Id = "h1", Name = "first", Trace = trace });
            taker.AddHandler(new TraceHandler { Id = "h2", Name = "second", Trace = trace });

            PingEvent ev = context.Factory.NewEvent<PingEvent>() with
            {
                Receiver = taker,
                TimeSpan = TimeSpan.Zero
            };

            context.Dispatch(ev).ToList();
            context.RunUntilQueueEmpty();

            CollectionAssert.AreEqual(new[] { "first", "second" }, trace);
        }

        [TestMethod]
        public void Dispatch_IgnoresUnmatchedEventHandlers()
        {
            Context context = new Context();
            EventReceiver taker = context.Factory.NewEventReceiver();
            context.AddEventReceiver(taker);

            List<string> trace = new List<string>();
            taker.AddHandler(new PongOnlyHandler { Id = "p", Trace = trace });

            PingEvent ev = context.Factory.NewEvent<PingEvent>() with
            {
                Receiver = taker,
                TimeSpan = TimeSpan.Zero
            };

            context.Dispatch(ev).ToList();
            context.Next(out _);

            Assert.IsEmpty(trace);
        }

        [TestMethod]
        public void Context_SkipsCancelledEventBeforeDispatch()
        {
            Context context = new Context();
            EventReceiver taker = context.Factory.NewEventReceiver();
            context.AddEventReceiver(taker);

            List<string> trace = new List<string>();
            taker.AddHandler(new TraceHandler { Id = "h", Name = "should-not-run", Trace = trace });

            PingEvent ev = context.Factory.NewEvent<PingEvent>() with
            {
                Receiver = taker,
                TimeSpan = TimeSpan.Zero,
                CancellationTokenSource = new CancellationTokenSource()
            };

            context.Dispatch(ev).ToList();
            ev.CancellationTokenSource.Cancel();
            bool keepRunning = context.Next(out IEvent? processed);

            Assert.IsTrue(keepRunning);
            Assert.IsNull(processed);
            Assert.IsEmpty(trace);
        }

        [TestMethod]
        public void Context_RemainsRunningWhenQueueIsEmpty()
        {
            Context context = new Context();

            bool keepRunning = context.Next(out IEvent? ev);

            Assert.IsTrue(keepRunning);
            Assert.IsNull(ev);
            Assert.IsFalse(context.HasPendingEvents);
        }

        [TestMethod]
        public void Context_StopsWhenExitEventIsDequeued()
        {
            Context context = new Context();

            ExitEvent exit = context.Factory.NewCoreEvent<ExitEvent>();
            exit.Delay = TimeSpan.FromSeconds(2);
            context.Dispatch(exit).ToList();

            bool keepRunning = context.Next(out IEvent? ev);

            Assert.IsFalse(keepRunning);
            Assert.IsNotNull(ev);
            Assert.IsInstanceOfType<ExitEvent>(ev);
            Assert.AreEqual(TimeSpan.FromSeconds(2), context.ContextTimeSpan);
            Assert.AreEqual(StopReason.ExitEvent, ((ExitEvent)ev).Reason);
        }

        [TestMethod]
        public void Context_RunUntilTime_StopsWithTimeLimitReason()
        {
            Context context = new Context();
            EventReceiver taker = context.Factory.NewEventReceiver();
            context.AddEventReceiver(taker);

            taker.AddHandler(new TraceHandler { Id = "h1", Name = "first", Trace = new List<string>() });

            context.Dispatch(context.Factory.NewEvent<PingEvent>() with
            {
                Receiver = taker,
                TimeSpan = TimeSpan.FromSeconds(1)
            }).ToList();
            context.Dispatch(context.Factory.NewEvent<PingEvent>() with
            {
                Receiver = taker,
                TimeSpan = TimeSpan.FromSeconds(3)
            }).ToList();

            ContextRunResult result = context.RunUntil(TimeSpan.FromSeconds(1));

            Assert.AreEqual(StopReason.TimeLimitReached, result.Reason);
            Assert.AreEqual(StopReason.TimeLimitReached, result.ExitEvent.Reason);
            Assert.IsNotNull(result.LastEvent);
            Assert.AreEqual(TimeSpan.FromSeconds(1), result.CurrentTime);
            Assert.AreEqual(2, result.ProcessedCount, "The scheduled PingEvent and its handler invocation are both processed events.");
            Assert.IsTrue(context.HasPendingEvents);
        }

        [TestMethod]
        public void Context_RequestStop_StopsSubsequentRun()
        {
            Context context = new Context();
            EventReceiver taker = context.Factory.NewEventReceiver();
            context.AddEventReceiver(taker);

            context.Dispatch(context.Factory.NewEvent<PingEvent>() with
            {
                Receiver = taker,
                TimeSpan = TimeSpan.Zero
            }).ToList();

            context.RequestStop();

            ContextRunResult result = context.Run();

            Assert.AreEqual(StopReason.StopRequested, result.Reason);
            Assert.IsTrue(result.Terminated);
            Assert.IsNull(result.LastEvent);
            Assert.AreEqual(0, result.ProcessedCount);
            Assert.IsTrue(context.HasPendingEvents);
        }

        [TestMethod]
        public void Context_TerminationPreemptsEqualTimeHighestPriorityEvent()
        {
            var context = new Context();
            var receiver = context.Factory.NewEventReceiver();
            context.AddEventReceiver(receiver);
            var pending = context.Factory.NewEvent<PingEvent>() with
            {
                Receiver = receiver, TimeSpan = TimeSpan.Zero, Priority = int.MinValue
            };
            context.Dispatch(pending).ToList();
            var termination = new ExitEvent { Reason = StopReason.StopRequested };
            context.ScheduleTermination(termination);

            bool keepRunning = context.Next(out IEvent? current);

            Assert.IsFalse(keepRunning);
            Assert.AreSame(termination, current);
            Assert.IsNull(pending.Execute);
            Assert.IsTrue(context.HasPendingEvents);
        }

        [TestMethod]
        public void AdvanceTo_DoesNotAdvancePastTermination()
        {
            var context = new Context();
            context.RequestStop();

            Assert.ThrowsExactly<InvalidOperationException>(() => context.AdvanceTo(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(TimeSpan.Zero, context.ContextTimeSpan);
        }

        [TestMethod]
        public void Context_ProcessesEqualScheduleEventsInEnqueueOrder()
        {
            var context = new Context();
            var receiver = context.Factory.NewEventReceiver();
            context.AddEventReceiver(receiver);
            var first = context.Factory.NewEvent<PingEvent>() with { Receiver = receiver, TimeSpan = TimeSpan.Zero };
            var second = context.Factory.NewEvent<PingEvent>() with { Receiver = receiver, TimeSpan = TimeSpan.Zero };
            var third = context.Factory.NewEvent<PingEvent>() with { Receiver = receiver, TimeSpan = TimeSpan.Zero };
            context.Dispatch(first).ToList();
            context.Dispatch(second).ToList();
            context.Dispatch(third).ToList();

            Assert.IsTrue(context.Next(out IEvent? one));
            Assert.IsTrue(context.Next(out IEvent? two));
            Assert.IsTrue(context.Next(out IEvent? three));

            Assert.AreSame(first, one);
            Assert.AreSame(second, two);
            Assert.AreSame(third, three);
        }

        [TestMethod]
        public void PriorityPropagatingEventRunsItsHandlerBeforeOrdinaryEvents()
        {
            var context = new Context();
            var receiver = context.Factory.NewEventReceiver();
            context.AddEventReceiver(receiver);
            var trace = new List<string>();
            receiver.AddHandler(new PriorityHandler { Trace = trace });
            context.Dispatch(context.Factory.NewEvent<PriorityEvent>() with
            {
                Receiver = receiver, TimeSpan = TimeSpan.Zero, Priority = -10
            }).ToList();
            context.Dispatch(context.Factory.NewEvent<PingEvent>() with
            {
                Receiver = receiver, TimeSpan = TimeSpan.Zero, Priority = -5
            }).ToList();
            context.EventProcessed += ev =>
            {
                if (ev is PingEvent) trace.Add("ordinary");
            };

            context.RunUntilQueueEmpty();

            CollectionAssert.AreEqual(new[] { "handler", "ordinary" }, trace);
        }

        [TestMethod]
        public void Context_RunUntilQueueEmpty_StopsOnlyWhenExplicitlyConfigured()
        {
            Context context = new Context();

            ContextRunResult result = context.RunUntilQueueEmpty();

            Assert.AreEqual(StopReason.QueueEmpty, result.Reason);
            Assert.IsNull(result.LastEvent);
            Assert.AreEqual(0, result.ProcessedCount);
        }

        [TestMethod]
        public void Context_RaisesQueueAndProcessNotifications()
        {
            Context context = new Context();
            EventReceiver taker = context.Factory.NewEventReceiver();
            context.AddEventReceiver(taker);

            List<string> trace = new();
            context.EventQueued += ev => trace.Add($"queued:{ev.GetType().Name}");
            context.EventProcessed += ev => trace.Add($"processed:{ev.GetType().Name}");

            context.Dispatch(context.Factory.NewEvent<PingEvent>() with
            {
                Receiver = taker,
                TimeSpan = TimeSpan.Zero
            }).ToList();

            context.Next(out _);

            CollectionAssert.AreEqual(
                new[] { "queued:PingEvent", "processed:PingEvent" },
                trace);
        }

        [TestMethod]
        public void Context_ObserverCanEmitEvents_AndEventsAreAutoQueued()
        {
            Context context = new Context();
            EventReceiver taker = context.Factory.NewEventReceiver();
            context.AddEventReceiver(taker);

            List<string> trace = new List<string>();
            taker.AddHandler(new TraceHandler { Id = "h1", Name = "first", Trace = trace });

            context.AddObserver(new EmitPongObserver(context, taker));

            context.Dispatch(context.Factory.NewEvent<PingEvent>() with
            {
                Receiver = taker,
                TimeSpan = TimeSpan.Zero
            }).ToList();

            ContextRunResult result = context.RunFor(2);

            Assert.AreEqual(StopReason.EventLimitReached, result.Reason);
            Assert.AreEqual(2, result.ProcessedCount);
            Assert.IsTrue(context.History.OfType<PingEvent>().Any());
            Assert.IsTrue(context.History.OfType<PongEvent>().Any());
        }


        public record class PingEvent : Event;
        public record class PongEvent : Event;
        public record class PriorityEvent : Event, IPropagateHandlerPriority;

        private class PriorityHandler : IHandler
        {
            public string Id => nameof(PriorityHandler);
            public required List<string> Trace { get; init; }

            [Handler]
            public IEnumerable<Event> Handle(PriorityEvent ev)
            {
                Trace.Add("handler");
                yield break;
            }
        }

        private class TraceHandler : IHandler
        {
            public required string Id { get; init; }
            public required string Name { get; init; }
            public required List<string> Trace { get; init; }

            [Handler]
            public IEnumerable<Event> OnPing(PingEvent ev)
            {
                Trace.Add(Name);
                yield break;
            }
        }

        private class PongOnlyHandler : IHandler
        {
            public required string Id { get; init; }
            public required List<string> Trace { get; init; }

            [Handler]
            public IEnumerable<Event> OnPong(PongEvent ev)
            {
                Trace.Add("pong");
                yield break;
            }
        }

        private class EmitPongObserver : IContextObserver
        {
            private readonly Context context;
            private readonly EventReceiver receiver;

            public EmitPongObserver(Context context, EventReceiver receiver)
            {
                this.context = context;
                this.receiver = receiver;
            }

            public IEnumerable<IEvent> OnQueued(Context context, IEvent ev)
            {
                yield break;
            }

            public IEnumerable<IEvent> OnProcessed(Context context, IEvent ev)
            {
                if (ev is PingEvent ping)
                {
                    yield return this.context.Factory.NewEvent<PongEvent>() with
                    {
                        Receiver = receiver,
                        TimeSpan = ping.TimeSpan
                    };
                }
            }
        }

    }
}
