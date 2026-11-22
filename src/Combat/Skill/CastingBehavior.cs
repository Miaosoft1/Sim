namespace Miaosoft.Sim.Combat.Skill;

public sealed record CastingBehavior(
    bool CanMove,
    bool CanInterrupt,
    TimeSpan Duration,
    bool RefundCooldownOnInterrupt = false,
    bool RefundCostOnInterrupt = false,
    bool InterruptOnDamage = false)
{
    public TimeSpan ChannelDuration { get; init; }
}

public enum CastPhase { Windup, Channeling }

public sealed record ActiveCast(
    Guid CommandId,
    string AbilityId,
    TimeSpan StartedAt,
    TimeSpan FinishesAt,
    CastingBehavior Behavior,
    CancellationTokenSource Cancellation)
{
    public CastPhase Phase { get; init; } = CastPhase.Windup;
    public required Guid ActivationId { get; init; }
    public CommandGateManager? AbilityGate { get; init; }
    public Unit? Source { get; init; }
    public long ControlVersion { get; init; }

    public void Cancel()
    {
        Cancellation.Cancel();
        if (Phase == CastPhase.Channeling && Behavior.RefundCooldownOnInterrupt)
            AbilityGate?.RefundCooldown(ActivationId);
    }
}
