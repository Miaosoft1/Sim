using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Core
{
    public interface IContextObserver
    {
        IEnumerable<IEvent> OnQueued(Context context, IEvent ev);
        IEnumerable<IEvent> OnProcessed(Context context, IEvent ev);
        IEnumerable<IEvent> OnDispatched(Context context, IEvent ev, bool handlerInvocation) => [];
        void OnPendingEventsCleared(Context context) { }
    }
}