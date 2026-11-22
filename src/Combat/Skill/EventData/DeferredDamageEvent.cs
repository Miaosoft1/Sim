namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record DeferredDamageEvent : Event
{
    private sealed record ReplayCancellationCheckEvent : Event
    {
        public required DeferredDamageEvent Replay { get; init; }

        public override IEnumerable<IEvent> Dispatch()
        {
            if (Replay.Settlement.IsCompleted || Replay.Damage.Publication.Dispatched)
                yield break;
            if (!Replay.CancellationToken.IsCancellationRequested && !Replay.Damage.IsPublicationCanceled)
                throw new InvalidOperationException("The deferred damage publication was not dispatched.");
            yield return Replay.CreateCompletionBarrier();
        }
    }

    private sealed record ReplayFinishedEvent : Event
    {
        public required DeferredDamageEvent Replay { get; init; }
        public bool Final { get; init; }

        public override IEnumerable<IEvent> Dispatch()
        {
            if (!Final)
            {
                yield return Context.Factory.NewEvent<ReplayFinishedEvent>() with
                {
                    Replay = Replay, Final = true, Receiver = Receiver,
                    Priority = int.MaxValue, IsScopeDetached = true
                };
                yield break;
            }
            if (!Replay.Settlement.IsCompleted && Replay.CompletionReceiver is { } completion &&
                !Context.ContainsReceiver(completion))
                throw new InvalidOperationException("The deferred damage completion receiver is no longer registered.");
            Replay.Settlement.FinishReplay(Replay);
            if (Replay.CompletionReceiver is { } receiver && !Replay.Settlement.IsCompleted)
                yield return Context.Factory.NewEvent<DeferredDamageReplayCompletedEvent>() with
                {
                    Settlement = Replay.Settlement, Damage = Replay.Damage, Receiver = receiver,
                    Priority = int.MaxValue, Causality = Replay.Damage.Causality, IsScopeDetached = true
                };
        }
    }

    public required DeferredCombatImpacts Settlement { get; init; }
    public required DamageEvent Damage { get; init; }
    public EventReceiver? CompletionReceiver { get; init; }

    public override IEnumerable<IEvent> Dispatch()
    {
        ArgumentNullException.ThrowIfNull(Settlement);
        ArgumentNullException.ThrowIfNull(Damage);
        Settlement.ClaimReplay(this);
        yield return DamagePublicationEvent.ForReplay(this);
        yield return Context.Factory.NewEvent<ReplayCancellationCheckEvent>() with
        {
            Replay = this, Receiver = Receiver, Priority = int.MaxValue, IsScopeDetached = true
        };
    }

    internal Event CreateCompletionBarrier() =>
        Context.Factory.NewEvent<ReplayFinishedEvent>() with
        {
            Replay = this, Receiver = Receiver,
            Priority = int.MaxValue, IsScopeDetached = true
        };
}
