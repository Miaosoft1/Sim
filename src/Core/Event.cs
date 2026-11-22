using System.Text;
using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Core
{
    public record class Event : IEvent
    {
        internal Context? _context { get; set; }
        public Context Context => _context!;

        public virtual EventReceiver? Receiver { get; set; }
        public virtual TimeSpan Create { get; set; }
        public virtual TimeSpan Delay { get; set; }
        public virtual TimeSpan? Execute { get; set; }
        public long EnqueueSequence { get; internal set; } = -1;
        public Guid? CorrelationId { get; set; }
        public Guid? CommittedOperationId { get; set; }
        public IEventCausality? Causality { get; set; }
        public bool IsCausalityDetached { get; init; }
        public virtual TimeSpan TimeSpan
        {
            get => Delay;
            set => Delay = value;
        }
        public virtual int Priority { get; set; }
        public CancellationTokenSource? CancellationTokenSource { get; set; }
        private EventScope? scope;
        public EventScope? Scope { get => scope; init => scope = value; }
        public bool IsScopeDetached { get; init; }
        public EventCancellation CancellationToken => new(scope, CancellationTokenSource?.Token ?? default);

        internal void InheritScope(EventScope? value) => scope = value;

        public virtual IEnumerable<IEvent> Dispatch()
        {
            if (CancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            if (Receiver == null)
            {
                var ex = new Exception("Receiver cannot be null");
                ex.Data.Add("ev", this);
                throw ex;
            }

            foreach (var item in Receiver.Dispatch(this))
            {
                yield return item;
            }

        }

        protected virtual bool PrintMembers(StringBuilder stringBuilder)
        {
            stringBuilder.Append($"{nameof(Receiver)} = {Receiver?.Guid}, ");
            stringBuilder.Append($"{nameof(Create)} = {Create}, ");
            stringBuilder.Append($"{nameof(Delay)} = {Delay}, ");
            stringBuilder.Append($"{nameof(Execute)} = {Execute}");
            return true;
        }
    }
}
