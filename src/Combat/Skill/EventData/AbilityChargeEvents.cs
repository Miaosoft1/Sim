using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record AbilityChargeTimerEvent : Event
{
    public required Guid AbilityInstanceId { get; init; }
    public required long Generation { get; init; }
}

public sealed record AbilityChargesChangedEvent : Event
{
    public required string AbilityId { get; init; }
    public required Guid AbilityInstanceId { get; init; }
    public Guid? OwnerId { get; init; }
    public required AbilityChargeSnapshot Charges { get; init; }
}

public sealed record AbilityChargeRestoreRequestEvent : Event
{
    public required Unit Owner { get; init; }
    public required Guid AbilityInstanceId { get; init; }
    public required int Amount { get; init; }
    internal CapturedHandlerTarget? TriggerRegistration { get; init; }
    internal CapturedHandlerTarget? AbilityRegistration { get; init; }
    internal long? AuthorityVersion { get; init; }
    internal Guid? TriggerInstanceId { get; init; }
    internal long? TriggerAuthorityVersion { get; init; }
}

public sealed record AbilityChargeRestoreInvokeEvent : AbilityTriggerInvokeEvent
{
    public required int Amount { get; init; }
    internal CapturedHandlerTarget? TriggerRegistration { get; init; }
    internal CapturedHandlerTarget? AbilityRegistration { get; init; }
    internal long? AuthorityVersion { get; init; }
    internal Guid? TriggerInstanceId { get; init; }
    internal long? TriggerAuthorityVersion { get; init; }
}

public enum AbilityChargeRestoreFailureReason
{
    None,
    AbilityNotAvailable,
    ChargesNotConfigured
}

public sealed record AbilityChargeRestoreResultEvent : Event
{
    public required Unit Owner { get; init; }
    public required Guid AbilityInstanceId { get; init; }
    public required int Restored { get; init; }
    public required AbilityChargeRestoreFailureReason FailureReason { get; init; }
}
