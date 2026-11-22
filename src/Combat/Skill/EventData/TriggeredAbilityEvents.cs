namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record TriggeredAbilityReleaseEvent : Event, ITargetedHandlerEvent
{
    public required UnitActionGateRequestEvent Request { get; init; }
    public CapturedHandlerTarget? TargetedHandler => Request.AbilityTarget;
}

public sealed record AbilityActivationResultEvent : Event
{
    public required Unit Source { get; init; }
    public required string AbilityId { get; init; }
    public required Guid AbilityInstanceId { get; init; }
    public required Guid ActivationId { get; init; }
    public required Guid CommandEventId { get; init; }
    public required CommandExecutionStatus Status { get; init; }
    public required CommandExecutionFailureReason FailureReason { get; init; }
    public TimeSpan? NextAvailableTime { get; init; }
}
