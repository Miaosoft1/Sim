using Miaosoft.Sim.Combat.Skill.EventData;

namespace Miaosoft.Sim.Combat.Skill;

public sealed class DeferredCombatImpacts : IDisposable
{
    private readonly List<(Event Impact, long Sequence)> impacts = [];
    private readonly HashSet<Guid> attempts = [];
    private readonly Dictionary<DamageEvent, EventReceiver> damageEvents = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<DamageEvent> readyDamage = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<DamageEvent> replayedDamage = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<DamageEvent> inFlightDamage = new(ReferenceEqualityComparer.Instance);

    public DeferredCombatImpacts(Unit target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.DeferredImpacts != null)
            throw new InvalidOperationException("The unit already has an active combat settlement.");
        Target = target;
        target.DeferredImpacts = this;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public Unit Target { get; }
    public bool IsReplaying { get; private set; }
    public bool IsCompleted { get; private set; }

    public bool TryDeferDeath(DeathEvent death)
    {
        if (death.Victim != Target || IsCompleted || death.SettlementReplayId == Id) return false;
        if (attempts.Add(death.AttemptId)) impacts.Add((death, death.EnqueueSequence));
        return true;
    }

    internal bool TryDeferDamage(DamageEvent damage, long sequence)
    {
        if (damage.Target != Target || IsCompleted ||
            damage.Receiver != Target.EventReceiver && damage.Receiver != damage.Source?.EventReceiver)
            return false;
        EventReceiver receiver = damage.Receiver!;
        if (damageEvents.TryGetValue(damage, out EventReceiver? original))
        {
            if (!ReferenceEquals(receiver, original))
                throw new InvalidOperationException("The deferred damage receiver changed.");
        }
        else
        {
            damageEvents.Add(damage, receiver);
            impacts.Add((damage, sequence));
        }
        return true;
    }

    public void BeginReplay()
    {
        if (IsReplaying || IsCompleted) throw new InvalidOperationException("The combat settlement cannot begin replay.");
        IsReplaying = true;
    }

    public Event? TakeNext()
    {
        if (!IsReplaying || IsCompleted) throw new InvalidOperationException("The combat settlement is not replaying.");
        if (impacts.Count == 0) return null;
        var entry = impacts.MinBy(impact => impact.Sequence);
        impacts.Remove(entry);
        Event next = entry.Impact;
        if (next is DamageEvent damage)
            readyDamage.Add(damage);
        return next;
    }

    public void Complete()
    {
        if (!IsReplaying || IsCompleted || impacts.Count != 0 || Target.DeferredImpacts != this ||
            readyDamage.Any(damage => !damage.IsPublicationCanceled) ||
            inFlightDamage.Count != 0)
            throw new InvalidOperationException("The combat settlement has not drained.");
        IsCompleted = true;
        attempts.Clear();
        damageEvents.Clear();
        readyDamage.Clear();
        replayedDamage.Clear();
        inFlightDamage.Clear();
        Target.DeferredImpacts = null;
    }

    public bool CanReplay(DamageEvent damage, EventReceiver? receiver) =>
        !damage.IsPublicationCanceled && replayedDamage.Contains(damage) &&
        damageEvents.TryGetValue(damage, out EventReceiver? original) &&
        ReferenceEquals(receiver, original) && ReferenceEquals(damage.Receiver, original) &&
        damage.Target == Target && Target.DeferredImpacts == this && IsReplaying && !IsCompleted;

    internal void ClaimReplay(DeferredDamageEvent replay)
    {
        DamageEvent damage = replay.Damage;
        if (!IsReplaying || IsCompleted || !ReferenceEquals(Target.DeferredImpacts, this) ||
            !ReferenceEquals(replay.Context, Target.EventReceiver.Context) ||
            !ReferenceEquals(damage.Context, replay.Context) || !ReferenceEquals(damage.Target, Target) ||
            replay.CancellationTokenSource != null &&
                !ReferenceEquals(replay.CancellationTokenSource, damage.CancellationTokenSource) ||
            replay.Scope != null && damage.PublicationScope != null &&
                !ReferenceEquals(replay.Scope, damage.PublicationScope) ||
            replay.CompletionReceiver != null &&
                (!ReferenceEquals(replay.CompletionReceiver.Context, replay.Context) ||
                 !replay.Context.ContainsReceiver(replay.CompletionReceiver)) ||
            !damageEvents.TryGetValue(damage, out EventReceiver? receiver) ||
            !ReferenceEquals(damage.Receiver, receiver) || !ReferenceEquals(replay.Receiver, receiver) ||
            !readyDamage.Contains(damage))
            throw new InvalidOperationException("The damage has not been taken for replay from this settlement.");
        readyDamage.Remove(damage);
        replayedDamage.Add(damage);
        inFlightDamage.Add(damage);
    }

    internal void FinishReplay(DeferredDamageEvent replay)
    {
        if (IsCompleted) return;
        DamageEvent damage = replay.Damage;
        if (replay.CancellationToken.IsCancellationRequested && !damage.IsPublicationCanceled)
            throw new InvalidOperationException("The deferred damage replay was canceled independently of the original damage.");
        if (!inFlightDamage.Remove(damage))
            throw new InvalidOperationException("The deferred damage replay is not in flight.");
    }

    public void Dispose()
    {
        impacts.Clear();
        attempts.Clear();
        damageEvents.Clear();
        readyDamage.Clear();
        replayedDamage.Clear();
        inFlightDamage.Clear();
        IsCompleted = true;
        if (Target.DeferredImpacts == this) Target.DeferredImpacts = null;
    }
}
