using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Runtime.EventData;

public sealed record SessionCompletionRequestedEvent : Event
{
    public override int Priority { get; set; } = int.MaxValue;
}

public sealed class SessionCompletedEvent : ExitEvent, IDetachedCoreEvent;
