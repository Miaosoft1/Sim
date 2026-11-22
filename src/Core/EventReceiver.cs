using System.Reflection;
using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Core
{
    public class EventReceiver
    {
        public Guid Guid { get; set; } = Guid.NewGuid();

        public List<IEvent> History { get; set; } = new List<IEvent>();

        internal Context? _context { get; set; }
        public Context Context => _context!;

        private sealed class HandlerRegistration(IHandler handler)
        {
            public IHandler Handler { get; } = handler;
            public HandlerInvokerMap? CapturedInvokers { get; set; }
            public HandlerInvokerMap? IndexedInvokers { get; set; }
        }

        private sealed class HandlerSnapshot(List<HandlerRegistration> ordered)
        {
            public List<HandlerRegistration> Ordered { get; } = ordered;
            public HashSet<HandlerRegistration> Active { get; } = new(ordered);
            public Dictionary<IHandler, int> Positions { get; } = CreatePositions(ordered);

            private static Dictionary<IHandler, int> CreatePositions(List<HandlerRegistration> registrations)
            {
                var positions = new Dictionary<IHandler, int>(ReferenceEqualityComparer.Instance);
                for (int i = 0; i < registrations.Count; i++)
                    positions.TryAdd(registrations[i].Handler, i);
                return positions;
            }
        }

        private readonly AtomicCell<HandlerSnapshot> handlers = new(new HandlerSnapshot([]));
        HandlerInvokerMap? dispatchRegistry;
        private long dispatchRegistryVersion = -1;

        public T? Get<T>() where T : class, IHandler
        {
            foreach (HandlerRegistration entry in handlers.Value.Ordered)
                if (entry.Handler.GetType() == typeof(T))
                    return (T)entry.Handler;
            return null;
        }

        public IReadOnlyList<T> GetHandlers<T>() where T : class, IHandler
        {
            var result = new List<T>();
            foreach (HandlerRegistration entry in handlers.Value.Ordered)
                if (entry.Handler is T match)
                    result.Add(match);
            return result;
        }

        public void AddHandler(params IHandler[] handlers)
        {
            var next = this.handlers.Value.Ordered.ToList();
            foreach (IHandler handler in handlers)
                next.Add(new HandlerRegistration(handler));
            this.handlers.Set(new HandlerSnapshot(next));
            dispatchRegistry = null;
        }

        public bool TryRemoveHandler(IHandler handler)
        {
            var next = handlers.Value.Ordered.ToList();
            int index = next.FindIndex(entry => ReferenceEquals(entry.Handler, handler));
            if (index < 0)
                return false;
            next.RemoveAt(index);
            handlers.Set(new HandlerSnapshot(next));
            dispatchRegistry = null;
            return true;
        }

        public int RemoveHandlers(Predicate<IHandler> predicate)
        {
            var next = handlers.Value.Ordered.ToList();
            int removed = next.RemoveAll(entry => predicate(entry.Handler));
            if (removed > 0)
            {
                handlers.Set(new HandlerSnapshot(next));
                dispatchRegistry = null;
            }

            return removed;
        }

        public IReadOnlyList<IHandler> StageHandlerChanges(
            AtomicTransaction transaction, IReadOnlyList<IHandler> remove, IReadOnlyList<IHandler> add)
        {
            var next = transaction.Read(handlers).Ordered.ToList();
            foreach (IHandler handler in remove)
            {
                int index = next.FindIndex(entry => ReferenceEquals(entry.Handler, handler));
                if (index < 0)
                    throw new InvalidOperationException("A removed handler is not currently registered.");
                next.RemoveAt(index);
            }
            foreach (IHandler handler in add)
            {
                if (next.Any(entry => ReferenceEquals(entry.Handler, handler)))
                    throw new InvalidOperationException("An added handler is already registered.");
                next.Add(new HandlerRegistration(handler));
            }
            transaction.Stage(handlers, new HandlerSnapshot(next));
            return next.Select(entry => entry.Handler).ToArray();
        }

        public bool UpdateHandler(IHandler handler)
        {
            if (this.TryRemoveHandler(handler))
            {
                this.AddHandler(handler);
                return true;
            }
            else
            {
                return false;
            }
        }

        public CapturedHandlerTarget CaptureTarget(ITargetableHandler handler)
        {
            HandlerRegistration registration = handlers.Value.Ordered.SingleOrDefault(
                entry => ReferenceEquals(entry.Handler, handler))
                ?? throw new InvalidOperationException("The handler is not registered on this receiver.");
            registration.CapturedInvokers ??= HandlerInvokerMap.Build([(handler, () => true)]);
            return new CapturedHandlerTarget(
                this, handler, registration.CapturedInvokers, () => handlers.Value.Active.Contains(registration));
        }

        public IEnumerable<IndexedHandlerInvocation> PlanIndexedHandler(Event ev, IIndexedEventHandler handler)
        {
            ArgumentNullException.ThrowIfNull(ev);
            ArgumentNullException.ThrowIfNull(handler);
            if (ev.CancellationToken.IsCancellationRequested)
                yield break;
            if (!ReferenceEquals(ev.Receiver, this))
                throw new InvalidOperationException("The indexed event belongs to a different receiver.");
            Context dispatchContext = ev.Context ?? _context
                ?? throw new InvalidOperationException("An indexed event requires a context-bound receiver.");
            if (!ReferenceEquals(dispatchContext, _context))
                throw new InvalidOperationException("The indexed event belongs to a different context.");
            HandlerRegistration registration = handlers.Value.Ordered.SingleOrDefault(
                entry => ReferenceEquals(entry.Handler, handler))
                ?? throw new InvalidOperationException("The indexed handler is not registered on this receiver.");
            registration.IndexedInvokers ??= HandlerInvokerMap.Build(
                [(handler, () => handlers.Value.Active.Contains(registration))]);
            foreach (HandlerInvocationPlan plan in registration.IndexedInvokers.GetInvocations(ev))
                yield return new IndexedHandlerInvocation(
                    this, handler, dispatchContext.Factory.NewHandlerInvocationEvent(ev, plan));
        }

        public IEnumerable<IEvent> Dispatch(IEvent ev, IReadOnlyList<IndexedHandlerInvocation>? indexed = null)
        {
            if (ev.CancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            Context dispatchContext = (ev as Event)?.Context ?? _context
                ?? throw new InvalidOperationException("Dispatch requires a context-bound event or receiver.");
            if (dispatchContext.RecordsEventHistory) this.History.Add(ev);
            if (dispatchRegistry == null || dispatchRegistryVersion != handlers.Version)
            {
                var registrations = handlers.Value.Ordered;
                dispatchRegistry = HandlerInvokerMap.Build(
                    registrations.Select(entry => (entry.Handler, (Func<bool>)(() => handlers.Value.Active.Contains(entry)))));
                dispatchRegistryVersion = handlers.Version;
            }

            CapturedHandlerTarget? target = (ev as ITargetedHandlerEvent)?.TargetedHandler;
            if (target != null && !ReferenceEquals(target.Receiver, this))
                throw new InvalidOperationException("The captured handler belongs to a different receiver.");
            HandlerSnapshot snapshot = handlers.Value;
            IndexedHandlerInvocation[] pending = indexed == null ? [] : indexed
                .Select(invocation => ReferenceEquals(invocation.Receiver, this)
                    ? invocation
                    : throw new InvalidOperationException("The indexed invocation belongs to a different receiver."))
                .Where(invocation => snapshot.Positions.ContainsKey(invocation.Handler))
                .OrderBy(invocation => snapshot.Positions[invocation.Handler])
                .ToArray();
            int pendingIndex = 0;
            foreach (var plan in dispatchRegistry.GetInvocations(ev, dispatchContext.HasIndexedEvent(ev.GetType())))
            {
                if (ev is IRuntimeTargetEvent { TargetRuntimeInstanceId: { } id } &&
                    plan.Handler is IRuntimeInstanceHandler instance && instance.RuntimeInstanceId != id)
                    continue;
                if (target != null && plan.Handler is ITargetableHandler &&
                    string.Equals(plan.Handler.Id, target.HandlerId, StringComparison.Ordinal))
                    continue;
                int position = snapshot.Positions[plan.Handler];
                while (pendingIndex < pending.Length &&
                    snapshot.Positions[pending[pendingIndex].Handler] <= position)
                    yield return pending[pendingIndex++].Invocation;
                yield return dispatchContext.Factory.NewHandlerInvocationEvent(ev, plan);
            }
            while (pendingIndex < pending.Length)
                yield return pending[pendingIndex++].Invocation;
            if (target != null)
                foreach (var plan in target.Invokers.GetInvocations(ev))
                    yield return dispatchContext.Factory.NewHandlerInvocationEvent(ev, plan);
        }
    }

    public sealed class IndexedHandlerInvocation
    {
        internal IndexedHandlerInvocation(EventReceiver receiver, IHandler handler, IEvent invocation)
        {
            Receiver = receiver;
            Handler = handler;
            Invocation = invocation;
        }

        public EventReceiver Receiver { get; }
        internal IHandler Handler { get; }
        public IEvent Invocation { get; }
    }

    public sealed class CapturedHandlerTarget
    {
        private readonly Func<bool> isCurrent;
        private readonly ITargetableHandler handler;
        internal EventReceiver Receiver { get; }
        internal string HandlerId { get; }
        internal HandlerInvokerMap Invokers { get; }
        internal CapturedHandlerTarget(EventReceiver receiver, ITargetableHandler handler, HandlerInvokerMap invokers, Func<bool> isCurrent)
        {
            Receiver = receiver;
            this.handler = handler;
            HandlerId = handler.Id;
            Invokers = invokers;
            this.isCurrent = isCurrent;
        }
        public bool IsCurrent => isCurrent();
        public bool Targets(EventReceiver receiver, ITargetableHandler candidate) =>
            ReferenceEquals(Receiver, receiver) && ReferenceEquals(handler, candidate);
    }

    internal sealed class HandlerInvokerMap
    {
        private readonly Dictionary<Type, List<HandlerInvocationPlan>> dispatchMap;
        private readonly Dictionary<Type, List<HandlerInvocationPlan>> nonIndexedDispatchMap;

        private HandlerInvokerMap(Dictionary<Type, List<HandlerInvocationPlan>> dispatchMap)
        {
            this.dispatchMap = dispatchMap;
            nonIndexedDispatchMap = dispatchMap.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Where(plan => plan.Handler is not IIndexedEventHandler).ToList());
        }

        public static HandlerInvokerMap Build(IEnumerable<(IHandler Handler, Func<bool> IsRegistered)> handlers)
        {
            var map = new Dictionary<Type, List<HandlerInvocationPlan>>();

            foreach (var registration in handlers)
            {
                IHandler handler = registration.Handler;
                var methods = handler.GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .Where(m => m.GetCustomAttribute<HandlerAttribute>() != null)
                    .Where(m => m.GetParameters().Length == 1)
                    .OrderBy(m => m.MetadataToken);

                foreach (var method in methods)
                {
                    var parameterType = method.GetParameters()[0].ParameterType;
                    if (!typeof(IEvent).IsAssignableFrom(parameterType))
                    {
                        continue;
                    }

                    Func<IEvent, IEnumerable<IEvent>> invoker = inputEvent =>
                    {
                        if (inputEvent.CancellationToken.IsCancellationRequested ||
                            handler is IRequiresCurrentRegistration guard &&
                            guard.RequiresCurrentRegistration(inputEvent) && !registration.IsRegistered())
                        {
                            return Enumerable.Empty<IEvent>();
                        }

                        var result = method.Invoke(handler, [inputEvent]);
                        return result as IEnumerable<IEvent> ?? Enumerable.Empty<IEvent>();
                    };

                    if (!map.TryGetValue(parameterType, out var invocations))
                    {
                        invocations = new List<HandlerInvocationPlan>();
                        map[parameterType] = invocations;
                    }

                    int attrPriority = method.GetCustomAttribute<HandlerAttribute>()?.Priority ?? 0;
                    int handlerPriority = 0;
                    var priorityProperty = handler.GetType().GetProperty("Priority", BindingFlags.Instance | BindingFlags.Public);
                    if (priorityProperty?.PropertyType == typeof(int))
                    {
                        handlerPriority = (int)(priorityProperty.GetValue(handler) ?? 0);
                    }

                    int priority = handlerPriority + attrPriority;
                    invocations.Add(new HandlerInvocationPlan(handler, priority, invoker));
                }
            }

            return new HandlerInvokerMap(map);
        }

        public IReadOnlyList<HandlerInvocationPlan> GetInvocations(IEvent ev, bool excludeIndexedHandlers = false)
        {
            var map = excludeIndexedHandlers ? nonIndexedDispatchMap : dispatchMap;
            if (!map.TryGetValue(ev.GetType(), out var invokers))
            {
                return Array.Empty<HandlerInvocationPlan>();
            }

            return invokers;
        }
    }

    internal sealed record HandlerInvocationPlan(
        IHandler Handler,
        int Priority,
        Func<IEvent, IEnumerable<IEvent>> Invoke);
}
