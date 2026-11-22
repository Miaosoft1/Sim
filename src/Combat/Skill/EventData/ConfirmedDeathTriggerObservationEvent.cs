using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record ConfirmedDeathTriggerObservationEvent : Event, IPropagateHandlerPriority
{
    public required DeathResolvedEvent Death { get; init; }
    public required Unit Holder { get; init; }
}
