using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Core
{
    /// <summary>
    /// Executes events under one logical owner. Cross-thread requests must use <see cref="Inbox"/>.
    /// </summary>
    public class Context : IContext, IDisposable
    {
        private readonly TimeProvider timeProvider;
        private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(1d / 60);
        private double timeScale;
        private double requestedTimeScale;
        private bool clockStarted;
        private bool terminated;
        private long previousTimestamp;
        private long lastIdleTimestamp;
        private TimeSpan horizon;
        private bool asynchronousStep;
        private bool disposed;
        private bool recordsEventHistory = true;
        public bool Realtime { get; }
        public bool IsDisposed => disposed;
        public bool RecordsEventHistory => recordsEventHistory;
        public TimeProvider TimeProvider => timeProvider;
        public Random Random { get; }
        public bool UsesCausalityFactory(IEventCausalityFactory factory)
        {
            ArgumentNullException.ThrowIfNull(factory);
            return ReferenceEquals(causalityFactory, factory);
        }
        /// <summary>The effective scale; an owner-set value takes effect at the next timestamp boundary.</summary>
        public double TimeScale
        {
            get => timeScale;
            set
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                ValidateTimeScale(value);
                requestedTimeScale = value;
            }
        }
        public event Action<double>? TimeScaleChanged;
        private List<EventReceiver> receivers = new List<EventReceiver>();
        private readonly List<IContextObserver> observers = new List<IContextObserver>();
        private readonly HashSet<Type> indexedEventTypes = [];
        private readonly HashSet<EventScope> scopes = [];
        private IContextObserver[] observerSnapshot = [];
        private Guid? currentCorrelationId;
        private Guid? currentCommittedOperationId;
        private EventScope? currentScope;
        private IEventCausality? currentCausality;
        private readonly IEventCausalityFactory? causalityFactory;
        public bool IsProcessingEvent { get; private set; }
        private readonly PriorityQueue<IEvent, (IEvent Event, long Sequence)> queue;
        private ExitEvent? pendingTermination;
        private long nextSequence;
        public Logger Logger { get; }
        public List<IEvent> History => receivers
            .SelectMany(m => m.History)
            .OrderBy(m => m.Execute.HasValue ? 0 : 1)
            .ThenBy(m => m.Priority)
            .ToList();

        public TimeSpan ContextTimeSpan { get; protected set; } = TimeSpan.Zero;
        public ContextFactory Factory { get; }
        public ContextEventInbox Inbox { get; } = new();
        public bool HasPendingEvents
        {
            get
            {
                DiscardClosedScopeEvents();
                return pendingTermination != null || Inbox.HasPending || queue.Count > 0;
            }
        }
        public TimeSpan? NextEventTime => PeekNext() is { } next ? next.Create + next.Delay : null;
        public bool HasEventsDueNow
        {
            get
            {
                if (IsProcessingEvent && Inbox.HasPending) return true;
                IEvent? next = PeekNext();
                return next != null &&
                    next.Create.Add(next.Delay) <= ContextTimeSpan;
            }
        }

        /// <summary>Checks for work at the current timestamp other than a specified event kind.</summary>
        public bool HasEventsDueNowExcept<TEvent>() where TEvent : IEvent
        {
            if (IsProcessingEvent && Inbox.HasPending) return true;
            if (!IsProcessingEvent) DrainInbox();
            if (pendingTermination != null && pendingTermination is not TEvent) return true;
            return queue.UnorderedItems.Any(item =>
            {
                IEvent candidate = item.Element;
                IEvent original = candidate is HandlerInvocationEvent invocation
                    ? invocation.OriginalEvent : candidate;
                return original is not TEvent &&
                    GetScope(candidate)?.IsCancellationRequested != true &&
                    candidate.Create.Add(candidate.Delay) <= ContextTimeSpan;
            });
        }

        public event Action<IEvent>? EventQueued;
        public event Action<IEvent>? EventProcessed;

        public void MarkCurrentOperationCommitted(Guid operationId)
        {
            if (!IsProcessingEvent || operationId == Guid.Empty || currentCommittedOperationId.HasValue)
                throw new InvalidOperationException("The current event cannot mark this operation committed.");
            currentCommittedOperationId = operationId;
        }
        public Context(bool realtime = false, double timeScale = 1, TimeProvider? timeProvider = null,
            IEventCausalityFactory? causalityFactory = null, Random? random = null)
        {
            ValidateTimeScale(timeScale);
            Realtime = realtime;
            this.timeScale = requestedTimeScale = timeScale;
            this.timeProvider = timeProvider ?? TimeProvider.System;
            this.causalityFactory = causalityFactory;
            Random = random ?? new Random();
            var comparer = Comparer<(IEvent Event, long Sequence)>.Create((left, right) =>
            {
                IEvent x = left.Event;
                IEvent y = right.Event;
                int timeCompare = (x.Create + x.Delay).CompareTo(y.Create + y.Delay);
                if (timeCompare != 0)
                {
                    return timeCompare;
                }

                int priorityCompare = x.Priority.CompareTo(y.Priority);
                if (priorityCompare != 0)
                {
                    return priorityCompare;
                }

                int createCompare = x.Create.CompareTo(y.Create);
                return createCompare != 0 ? createCompare : left.Sequence.CompareTo(right.Sequence);
            });
            queue = new PriorityQueue<IEvent, (IEvent Event, long Sequence)>(comparer);
            Logger = new Logger(this);
            Factory = new ContextFactory(this);
        }

        public EventScope CreateEventScope()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var scope = new EventScope(this);
            scopes.Add(scope);
            return scope;
        }

        internal void ScopeClosed(EventScope scope) => scopes.Remove(scope);

        public async IAsyncEnumerable<IEvent> GetEvents()
        {
            foreach (var item in this.History)
            {
                yield return await Task.FromResult(item);
            }
        }

        public virtual bool Next(out IEvent? ev)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            EnsureSynchronousExecutionAllowed();
            if (!HasEventsDueNow) ApplyTimeScale();
            ContextStepResult result = ExecuteStep();
            ev = result.Event;
            return result.KeepRunning;
        }

        public async ValueTask<ContextStepResult> NextAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (IsProcessingEvent || asynchronousStep)
                throw new InvalidOperationException("Context already has an active execution step.");
            asynchronousStep = true;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Realtime && !clockStarted)
                {
                    previousTimestamp = timeProvider.GetTimestamp();
                    lastIdleTimestamp = previousTimestamp;
                    horizon = ContextTimeSpan;
                    clockStarted = true;
                }

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Finish the timestamp, including work produced by handlers and observers,
                    // before consulting the wall clock or applying a new time scale.
                    if (HasEventsDueNow) return ExecuteStep();
                    if (Inbox.ConsumeSignal()) return new(true, null);

                    if (Realtime)
                    {
                        long timestamp = timeProvider.GetTimestamp();
                        horizon += timeProvider.GetElapsedTime(previousTimestamp, timestamp) * TimeScale;
                        previousTimestamp = timestamp;
                    }
                    bool scaleChanged = ApplyTimeScale();
                    if (HasEventsDueNow) return ExecuteStep();
                    if (scaleChanged) return new(true, null);

                    TimeSpan delay;
                    if (!Realtime)
                    {
                        if (HasPendingEvents) return ExecuteStep();
                        delay = Timeout.InfiniteTimeSpan;
                    }
                    else
                    {
                        TimeSpan? next = NextEventTime;
                        if (next.HasValue && next.Value <= horizon) return ExecuteStep();
                        TimeSpan idleElapsed = timeProvider.GetElapsedTime(lastIdleTimestamp, previousTimestamp);
                        if (horizon > ContextTimeSpan && idleElapsed >= IdleInterval)
                        {
                            ContextTimeSpan = horizon;
                            lastIdleTimestamp = previousTimestamp;
                            return new(true, null);
                        }

                        delay = idleElapsed < IdleInterval ? IdleInterval - idleElapsed : IdleInterval;
                        if (next.HasValue && TimeScale > 0)
                        {
                            TimeSpan remaining = (next.Value - horizon) / TimeScale;
                            if (remaining < delay)
                                delay = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromTicks(1);
                        }
                    }

                    Task wakeTask = Inbox.WakeTask;
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    Task delayTask = Task.Delay(delay, timeProvider, wait.Token);
                    if (await Task.WhenAny(delayTask, wakeTask).ConfigureAwait(false) == wakeTask)
                    {
                        wait.Cancel();
                    }
                    else
                    {
                        await delayTask.ConfigureAwait(false);
                    }
                }
            }
            finally { asynchronousStep = false; }
        }

        internal static void ValidateTimeScale(double value)
        {
            if (!double.IsFinite(value) || value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Time scale must be finite and nonnegative.");
        }

        private bool ApplyTimeScale()
        {
            double requested = requestedTimeScale;
            if (requested == TimeScale) return false;
            timeScale = requested;
            if (Realtime && requested == 0) horizon = ContextTimeSpan;
            TimeScaleChanged?.Invoke(requested);
            return true;
        }

        internal void EnsureSynchronousExecutionAllowed()
        {
            if (asynchronousStep || (Realtime && clockStarted && !terminated))
                throw new InvalidOperationException("Use NextAsync to advance an active realtime context.");
        }

        private ContextStepResult ExecuteStep()
        {
            bool keepRunning = NextCore(out IEvent? ev);
            return new(keepRunning, ev);
        }

        private bool NextCore(out IEvent? ev)
        {
            if (!IsProcessingEvent) DrainInbox();
            DiscardClosedScopeEvents();
            if (pendingTermination != null)
            {
                ev = pendingTermination;
                pendingTermination = null;
            }
            else
            {
                if (!queue.TryDequeue(out ev, out _))
                {
                    ev = null;
                    return true;
                }
            }

            TimeSpan scheduledAt = ev.Create + ev.Delay;
            if (scheduledAt < this.ContextTimeSpan)
            {
                Logger.RecordQueueFailed(ev, "Scheduled before current context time");
                return true;
            }

            this.ContextTimeSpan = scheduledAt;

            Guid? previousCorrelationId = currentCorrelationId;
            Guid? previousCommittedOperationId = currentCommittedOperationId;
            EventScope? previousScope = currentScope;
            IEventCausality? previousCausality = currentCausality;
            bool previouslyProcessing = IsProcessingEvent;
            IsProcessingEvent = true;
            currentCorrelationId = GetCorrelationId(ev);
            currentCommittedOperationId = GetCommittedOperationId(ev);
            currentScope = GetScope(ev);
            currentCausality = ev.Causality;
            try
            {
                ev.Execute = this.ContextTimeSpan;
                Logger.RecordProcess(ev);
                if (!ev.CancellationToken.IsCancellationRequested)
                {
                    EventProcessed?.Invoke(ev);
                }

                foreach (IContextObserver observer in observerSnapshot)
                {
                    if (currentScope?.IsCancellationRequested == true) break;
                    foreach (IEvent next in observer.OnProcessed(this, ev))
                    {
                        Enqueue(next);
                    }
                }

                if (ev is ExitEvent)
                {
                    terminated = true;
                    return false;
                }

                if (ev.CancellationToken.IsCancellationRequested)
                {
                    ev = null;
                }
                else
                {
                    foreach (var item in ev.Dispatch())
                    {
                        if (item.CancellationToken.IsCancellationRequested)
                        {
                            continue;
                        }

                        Enqueue(item);
                    }
                    IEvent dispatched = ev is HandlerInvocationEvent invocation ? invocation.OriginalEvent : ev;
                    foreach (IContextObserver observer in observerSnapshot)
                    {
                        if (currentScope?.IsCancellationRequested == true) break;
                        foreach (IEvent next in observer.OnDispatched(this, dispatched, ev is HandlerInvocationEvent))
                        {
                            Enqueue(next);
                        }
                    }
                }
                return true;
            }
            catch (Exception error)
            {
                IEvent? original = ev is HandlerInvocationEvent invocation ? invocation.OriginalEvent : ev;
                Exception? cleanupError = null;
                if (original is IEventProcessingFailure failure)
                {
                    try { failure.OnProcessingFailure(this); }
                    catch (Exception exception) { cleanupError = exception; }
                }
                Exception result = cleanupError == null ? error :
                    new AggregateException("Event processing and cleanup failed.", error, cleanupError);
                if (currentCommittedOperationId.HasValue && result is not CommittedOperationException)
                    throw new CommittedOperationException(currentCommittedOperationId.Value, result);
                if (cleanupError != null) throw result;
                throw;
            }
            finally
            {
                currentCorrelationId = previousCorrelationId;
                currentCommittedOperationId = previousCommittedOperationId;
                currentScope = previousScope;
                currentCausality = previousCausality;
                IsProcessingEvent = previouslyProcessing;
            }
        }

        private static Guid? GetCorrelationId(IEvent ev) => ev switch
        {
            Event value => value.CorrelationId,
            HandlerInvocationEvent invocation => GetCorrelationId(invocation.OriginalEvent),
            _ => null
        };

        private static Guid? GetCommittedOperationId(IEvent ev) => ev switch
        {
            Event value => value.CommittedOperationId,
            HandlerInvocationEvent invocation => GetCommittedOperationId(invocation.OriginalEvent),
            _ => null
        };

        private static EventScope? GetScope(IEvent ev) => ev switch
        {
            Event value => value.Scope,
            HandlerInvocationEvent invocation => GetScope(invocation.OriginalEvent),
            _ => null
        };

        private void DiscardClosedScopeEvents()
        {
            while (queue.TryPeek(out IEvent? next, out _) &&
                GetScope(next)?.IsCancellationRequested == true)
                queue.Dequeue();
        }

        internal void DiscardScopeEvents(EventScope scope)
        {
            var remaining = queue.UnorderedItems
                .Where(item => !ReferenceEquals(GetScope(item.Element), scope))
                .ToArray();
            if (remaining.Length == queue.Count) return;
            queue.Clear();
            queue.EnqueueRange(remaining);
        }

        public void RequestStop()
        {
            ScheduleTermination(new ExitEvent { Reason = StopReason.StopRequested });
        }

        public void ClearPendingEvents()
        {
            if (IsProcessingEvent || asynchronousStep)
                throw new InvalidOperationException("Cannot clear events during an active step.");
            queue.Clear();
            Inbox.Clear();
            pendingTermination = null;
            foreach (IContextObserver observer in observerSnapshot)
                observer.OnPendingEventsCleared(this);
        }

        public void ScheduleTermination(ExitEvent ev)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ArgumentNullException.ThrowIfNull(ev);
            if (pendingTermination != null)
            {
                throw new InvalidOperationException("A termination event is already scheduled.");
            }

            ev.Create = ContextTimeSpan;
            ev.Delay = TimeSpan.Zero;
            AttachCausality(ev);
            pendingTermination = ev;
            NotifyQueued(ev);
        }

        internal IEvent? PeekNext()
        {
            if (!IsProcessingEvent) DrainInbox();
            if (pendingTermination != null)
            {
                return pendingTermination;
            }

            DiscardClosedScopeEvents();
            return queue.TryPeek(out IEvent? ev, out _) ? ev : null;
        }

        internal void SetTime(TimeSpan time)
        {
            EnsureSynchronousExecutionAllowed();
            ContextTimeSpan = time;
        }

        private void DrainInbox()
        {
            while (Inbox.TryDequeue(out ContextEventInbox.Work? work))
            {
                switch (work)
                {
                    case ContextEventInbox.RejectedEvent rejected:
                        Logger.RecordQueueFailed(rejected.Event, "The context inbox no longer accepts this event");
                        break;
                    case ContextEventInbox.RejectedRequest rejected:
                        Logger.RecordInboxRequestRejected(rejected.Name);
                        break;
                    case ContextEventInbox.PostedEvent posted:
                        if (posted.Event is Event value)
                        {
                            if (value._context != null && !ReferenceEquals(value._context, this))
                                throw new InvalidOperationException("The posted event belongs to another context.");
                            value._context = this;
                        }
                        posted.Event.Create = ContextTimeSpan;
                        posted.Event.Delay = TimeSpan.Zero;
                        Enqueue(posted.Event);
                        break;
                    case ContextEventInbox.StopRequest:
                        RequestStop();
                        break;
                    case ContextEventInbox.TimeScaleRequest scale:
                        TimeScale = scale.Value;
                        break;
                    default:
                        throw new InvalidOperationException("The context inbox contains an unknown request.");
                }
            }
        }

        public IEnumerable<IEvent> Dispatch(IEvent ev)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            AttachScope(ev);
            if (ev.CancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            TimeSpan executeAt = ev.Create + ev.Delay;
            if (executeAt < ContextTimeSpan)
            {
                Logger.RecordQueueFailed(ev, "Scheduled before current context time");
                yield break;
            }
            Enqueue(ev);
            yield return ev;
        }

        private void Enqueue(IEvent ev)
        {
            AttachScope(ev);
            if (GetScope(ev)?.IsCancellationRequested == true) return;
            AttachCausality(ev);
            if (ev is Event value && value.CorrelationId == null)
            {
                value.CorrelationId = currentCorrelationId;
            }
            if (ev.Create == default)
            {
                ev.Create = ContextTimeSpan;
            }

            TimeSpan executeAt = ev.Create + ev.Delay;
            if (executeAt < ContextTimeSpan)
            {
                Logger.RecordQueueFailed(ev, "Scheduled before current context time");
                return;
            }

            long sequence = nextSequence++;
            if (ev is Event queuedEvent)
            {
                queuedEvent.EnqueueSequence = sequence;
            }
            queue.Enqueue(ev, (ev, sequence));
            NotifyQueued(ev);
        }

        private void NotifyQueued(IEvent ev)
        {
            EventScope? previousScope = currentScope;
            IEventCausality? previousCausality = currentCausality;
            currentScope = GetScope(ev);
            currentCausality = ev.Causality;
            try
            {
                Logger.RecordQueue(ev);
                EventQueued?.Invoke(ev);

                foreach (IContextObserver observer in observerSnapshot)
                {
                    if (currentScope?.IsCancellationRequested == true) break;
                    foreach (IEvent next in observer.OnQueued(this, ev))
                    {
                        Enqueue(next);
                    }
                }
            }
            finally
            {
                currentScope = previousScope;
                currentCausality = previousCausality;
            }
        }

        private void AttachCausality(IEvent ev)
        {
            if (ev is Event { IsCausalityDetached: true } detached)
            {
                if (detached.Causality != null)
                    throw new InvalidOperationException("A causality-detached event cannot declare a causality.");
                if (causalityFactory != null)
                    detached.Causality = causalityFactory.Create(this) ??
                        throw new InvalidOperationException("The causality factory returned no causality.");
            }
            else if (ev.Causality == null)
            {
                ev.Causality = currentCausality;
                if (ev.Causality == null && causalityFactory != null)
                    ev.Causality = causalityFactory.Create(this) ??
                        throw new InvalidOperationException("The causality factory returned no causality.");
            }
            if (ev.Causality != null && !ReferenceEquals(ev.Causality.Context, this))
                throw new InvalidOperationException("The event causality belongs to another context.");
        }

        private void AttachScope(IEvent ev)
        {
            if (ev is Event value)
            {
                if (value.IsScopeDetached && value.Scope != null)
                    throw new InvalidOperationException("A detached event cannot declare an event scope.");
                if (value.Scope == null && !value.IsScopeDetached) value.InheritScope(currentScope);
                if (value.Scope != null && !ReferenceEquals(value.Scope.Context, this))
                    throw new InvalidOperationException("The event scope belongs to another context.");
            }
            else if (currentScope != null && ev is not HandlerInvocationEvent and not IDetachedCoreEvent)
            {
                throw new InvalidOperationException("Scoped work must derive from Event.");
            }
        }

        public void AddObserver(IContextObserver observer)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            observers.Add(observer);
            observerSnapshot = observers.ToArray();

            if (observer is IContextAttachObserver attachObserver)
            {
                foreach (IEvent next in attachObserver.OnAttached(this))
                {
                    Enqueue(next);
                }
            }
        }

        public void RegisterIndexedEvent<TEvent>() where TEvent : IEvent
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!indexedEventTypes.Add(typeof(TEvent)))
                throw new InvalidOperationException($"Event '{typeof(TEvent).Name}' already has an indexed router.");
        }

        public bool HasIndexedEvent<TEvent>() where TEvent : IEvent =>
            indexedEventTypes.Contains(typeof(TEvent));

        internal bool HasIndexedEvent(Type type) => indexedEventTypes.Contains(type);

        public bool RemoveObserver(IContextObserver observer)
        {
            if (!observers.Remove(observer)) return false;
            observerSnapshot = observers.ToArray();
            return true;
        }

        public void AddEventReceiver(EventReceiver receiver)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            receiver._context = this;
            this.receivers.Add(receiver);
        }

        public IReadOnlyList<EventReceiver> FindHandlerReceivers(IHandler handler) =>
            receivers.Where(receiver => receiver.GetHandlers<IHandler>()
                .Any(current => ReferenceEquals(current, handler))).ToArray();

        public bool ContainsReceiver(EventReceiver receiver) => receivers.Contains(receiver);

        public void RemoveEventReceiver(EventReceiver receiver)
        {
            this.receivers.Remove(receiver);
        }

        /// <summary>Stops retaining event objects for the lifetime of this context.</summary>
        public void DisableEventHistory()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (IsProcessingEvent || asynchronousStep)
                throw new InvalidOperationException("Cannot change history recording during an active step.");
            if (!recordsEventHistory) return;
            recordsEventHistory = false;
            foreach (EventReceiver receiver in receivers) receiver.History.Clear();
        }

        /// <summary>Releases queued work, recorded events, observers and receivers after execution has stopped.</summary>
        public void Dispose()
        {
            if (disposed) return;
            if (IsProcessingEvent || asynchronousStep)
                throw new InvalidOperationException("Cannot dispose a context during an active step.");
            disposed = true;
            Inbox.Complete();
            List<Exception>? failures = null;
            foreach (EventScope scope in scopes.ToArray())
            {
                try { scope.Cancel(); }
                catch (Exception error) { (failures ??= []).Add(error); }
            }
            scopes.Clear();
            queue.Clear();
            pendingTermination = null;
            foreach (IContextObserver observer in observerSnapshot)
            {
                try { observer.OnPendingEventsCleared(this); }
                catch (Exception error) { (failures ??= []).Add(error); }
            }
            foreach (EventReceiver receiver in receivers) receiver.History.Clear();
            receivers.Clear();
            observers.Clear();
            observerSnapshot = [];
            EventQueued = null;
            EventProcessed = null;
            TimeScaleChanged = null;
            if (failures is { Count: > 0 })
                throw new AggregateException("One or more event scopes failed to close.", failures);
        }
    }
}
