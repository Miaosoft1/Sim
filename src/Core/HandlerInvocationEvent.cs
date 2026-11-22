using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Core
{
	internal sealed class HandlerInvocationEvent : IEvent
	{
		private readonly HandlerInvocationPlan plan;

		public IEvent OriginalEvent { get; }
		public TimeSpan Create { get; set; }
		public TimeSpan Delay { get; set; }
		public TimeSpan? Execute { get; set; }
		public int Priority => OriginalEvent is IPropagateHandlerPriority
			? Math.Min(OriginalEvent.Priority, plan.Priority)
			: plan.Priority;
		public EventCancellation CancellationToken => OriginalEvent.CancellationToken;
		public IEventCausality? Causality
		{
			get => OriginalEvent.Causality;
			set => OriginalEvent.Causality = value;
		}

		public HandlerInvocationEvent(
			IEvent originalEvent,
			HandlerInvocationPlan plan)
		{
			OriginalEvent = originalEvent;
			this.plan = plan;
			Create = originalEvent.Execute ?? originalEvent.Create;
			Delay = TimeSpan.Zero;
		}

		public IEnumerable<IEvent> Dispatch()
		{
			if (OriginalEvent.CancellationToken.IsCancellationRequested)
			{
				yield break;
			}

			foreach (var ev in plan.Invoke(OriginalEvent))
			{
				yield return ev;
			}
		}
	}
}
