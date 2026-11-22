namespace Miaosoft.Sim.Combat.Skill.EventData;

public record class DamageReleaseEvent : Event, ITargetedHandlerEvent
{
    public required UnitActionGateRequestEvent Request { get; init; }
    public CapturedHandlerTarget? TargetedHandler => Request.AbilityTarget;
}
