namespace Miaosoft.Sim.Core
{
    public interface IEvent
    {
        public TimeSpan Create { get; set; }
        public TimeSpan Delay { get; set; }
        public TimeSpan? Execute { get; set; }
        public int Priority { get; }
        public EventCancellation CancellationToken { get; }
        public IEventCausality? Causality { get; set; }
        public IEnumerable<IEvent> Dispatch();
    }
}
