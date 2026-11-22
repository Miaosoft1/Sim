
using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Core
{
    public enum StopReason
    {
        ExitEvent,
        QueueEmpty,
        StopRequested,
        TimeLimitReached,
        EventLimitReached
    }

    public class ExitEvent : IEvent
    {
        public EventCancellation CancellationToken => default;
        public IEventCausality? Causality { get; set; }

        public TimeSpan Create { get; set; }
        public TimeSpan Delay { get; set; }
        public TimeSpan? Execute { get; set; }
        public int Priority { get; set; }
        public StopReason Reason { get; set; } = StopReason.ExitEvent;

        public IEnumerable<IEvent> Dispatch()
        {
            return Enumerable.Empty<IEvent>();
        }
    }
}
