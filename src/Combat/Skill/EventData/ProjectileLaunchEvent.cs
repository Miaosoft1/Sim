namespace Miaosoft.Sim.Combat.Skill.EventData;

public record class ProjectileLaunchEvent : Event, ITargetedHandlerEvent
{
    public required string AbilityId { get; init; }
    public required CommandEvent Command { get; init; }
    public required UnitActionGateRequestEvent Request { get; init; }
    public AbilityTriggerChain? TriggerChain
    {
        get => Causality as AbilityTriggerChain;
        init => Causality = value;
    }
    public CapturedHandlerTarget? TargetedHandler => Request.AbilityTarget;
}
