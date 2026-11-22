using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Core
{
    public class ContextFactory(Context context)
    {
        private T InitializeEvent<T>(T ev) where T : class, IEvent
        {
            if (ev is Event battleEvent)
            {
                battleEvent._context = context;
            }

            ev.Create = context.ContextTimeSpan;
            ev.Execute = null;
            return ev;
        }

        public T NewEvent<T>() where T : Event
        {
            var ev = Activator.CreateInstance<T>();
            return InitializeEvent(ev);
        }

        public T NewCoreEvent<T>() where T : class, IEvent, new()
        {
            T ev = new T();
            return InitializeEvent(ev);
        }

        public EventReceiver NewEventReceiver()
        {
            return new EventReceiver { _context = context };
        }

        internal HandlerInvocationEvent NewHandlerInvocationEvent(IEvent originalEvent, HandlerInvocationPlan plan)
        {
            return InitializeEvent(new HandlerInvocationEvent(originalEvent, plan));
        }
    }
}
