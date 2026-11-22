using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Talents.Model;

namespace Miaosoft.Sim.Loadout.Talents.EventData;

public enum TalentChangeOperation { Grant, Revoke, Allocate, AwardPoints, ObserveGrowth }

public enum TalentChangeFailure
{
    None, StaleRevision, Inactive, StaleAuthority, AlreadyGranted, GrantNotFound,
    NodeNotFound, MaximumRank, InsufficientPoints, RequirementsNotMet,
    PointLimitExceeded, GrowthLimitReached, AlreadyObserved
}

public sealed record TalentAuthorizationRequestEvent : Event
{
    public required Unit Owner { get; init; }
    public required TalentTreeDefinition Definition { get; init; }
    public required IAbilitySource Grant { get; init; }
    public required bool Authorize { get; init; }
    public long? ExpectedRevision { get; init; }
    public Guid OperationId { get; init; } = Guid.NewGuid();
}

public abstract record TalentTreeRequestEvent : Event
{
    public required TalentTreeInstance Tree { get; init; }
    public required long ExpectedAuthorityVersion { get; init; }
    public long? ExpectedRevision { get; init; }
    public Guid OperationId { get; init; } = Guid.NewGuid();
}

public sealed record TalentNodeAllocateRequestEvent : TalentTreeRequestEvent
{
    public required string NodeId { get; init; }
}

public sealed record TalentPointsAwardRequestEvent : TalentTreeRequestEvent
{
    public required int Points { get; init; }
}

public sealed record TalentGrowthObservationEvent : TalentTreeRequestEvent, IRuntimeTargetEvent
{
    internal AtomicCell<bool> Processed { get; } = new(false);
    public required string RuleId { get; init; }
    public required Event UpstreamEvent { get; init; }
    public int Progress { get; init; } = 1;
    public Guid? TargetRuntimeInstanceId => Tree.InstanceId;
}

public sealed record TalentChangeResultEvent : Event
{
    public required Guid OperationId { get; init; }
    public required Unit Owner { get; init; }
    public required string TreeId { get; init; }
    public required TalentChangeOperation Operation { get; init; }
    public required TalentChangeFailure Failure { get; init; }
    public bool Success => Failure == TalentChangeFailure.None;
}
