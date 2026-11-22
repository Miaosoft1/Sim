using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record PendingDeathTriggerObservationEvent : Event, IPropagateHandlerPriority
{
    public required DeathEvent Attempt { get; init; }
    public required Unit Holder { get; init; }
}
