using Miaosoft.Sim.Combat.Skill.EventData;

namespace Miaosoft.Sim.Combat.Skill.Handler
{
    public sealed class AbilityLifecycleHandler : Handler
    {
        private readonly Dictionary<string, Guid> leaseById = new(StringComparer.Ordinal);
        public int ActiveLeaseCount => leaseById.Count;

        public AbilityLifecycleHandler()
        {
            Id = nameof(AbilityLifecycleHandler);
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnPeriodicDamageRequest(PeriodicDamageRequestEvent ev)
        {
            if (ev.CancellationToken.IsCancellationRequested)
                yield break;
            if (ev.Receiver != ev.Target.EventReceiver)
                throw new InvalidOperationException("The periodic effect must be requested on its target.");
            AbilityTriggerChain chain = ev.Causality as AbilityTriggerChain
                ?? throw new InvalidOperationException("The context must use a TriggerChainFactory for periodic effects.");
            PeriodicEffectHandle? effect = chain.PeriodicEffects.TryReserve(
                chain, ev.Source, ev.Target, ev.Damage, ev.TickCount, ev.TickInterval,
                ev.FirstTickDelay, ev.ExpiresAt, ev.CancellationToken, ev.Replaces,
                out var rejection, out PeriodicEffectHandle? retired);
            if (effect == null)
            {
                yield return ev.Context.Factory.NewEvent<PeriodicEffectRejectedEvent>() with
                {
                    CommandEventId = ev.CommandEventId, Target = ev.Target, EffectId = ev.Damage.Id,
                    Reason = rejection, Receiver = ev.CompletionReceiver ?? ev.Target.EventReceiver,
                    IsScopeDetached = true
                };
                yield break;
            }

            Guid operationId = ev.CommittedOperationId ?? effect.Id;
            if (!ev.CommittedOperationId.HasValue)
                ev.Context.MarkCurrentOperationCommitted(operationId);
            retired?.Close(cancel: true);
            var application = new PeriodicDamageApplication
            {
                Damage = ev.Damage, TickCount = ev.TickCount, TickInterval = ev.TickInterval,
                FirstTickDelay = ev.FirstTickDelay, ExpiresAt = ev.ExpiresAt, Replaces = ev.Replaces
            };
            foreach (Event next in PeriodicEffectRegistry.CreateStartEvents(
                ev.Context, ev.CommandEventId, ev.Source, ev.Target, ev.CompletionReceiver,
                application, effect, operationId))
                yield return next;
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnLifecycle(AbilityLifecycleEvent ev)
        {
            if (ev.CancellationToken.IsCancellationRequested)
            {
                yield break;
            }
            if (ev.RemainingTicks < 0 || ev.RemainingTicks > 1 && ev.TickInterval <= TimeSpan.Zero)
                throw new InvalidOperationException("Repeated damage ticks require a positive interval.");
            if (ev.RemainingTicks > 0)
            {
                PeriodicEffectHandle effect = ev.PeriodicEffect
                    ?? throw new InvalidOperationException("A periodic effect must reserve capacity before it starts.");
                if (!ReferenceEquals(effect.Target, ev.Target) ||
                    !ReferenceEquals(effect.Chain, ev.TriggerChain) ||
                    !ReferenceEquals(effect.Cancellation, ev.CancellationTokenSource))
                    throw new InvalidOperationException("The periodic event does not match its reservation.");
                if (!effect.Registry.IsCurrent(effect))
                    yield break;
                if (effect.ExpiresAt is { } expiry && expiry <= ev.Context.ContextTimeSpan)
                {
                    effect.Registry.Finish(effect, cancel: true);
                    yield break;
                }
            }

            switch (ev.Stage)
            {
                case AbilityLifecycleStage.Start:
                    foreach (Event next in Start(ev))
                    {
                        yield return next;
                    }
                    break;
                case AbilityLifecycleStage.Tick:
                    if (!IsCurrentLease(ev))
                    {
                        yield break;
                    }
                    foreach (Event next in Tick(ev))
                    {
                        yield return next;
                    }
                    break;
                case AbilityLifecycleStage.Unapply:
                    if (!IsCurrentLease(ev))
                    {
                        yield break;
                    }
                    if (ev.LeaseAbilityIds is not { Count: > 0 })
                        throw new InvalidOperationException("An expiring ability lease must identify its abilities.");
                    yield return ev.Context.Factory.NewEvent<AbilityUninstallRequestEvent>() with
                    {
                        CommandEventId = ev.CommandEventId,
                        Source = ev.Source,
                        Target = ev.Target,
                        AbilityIds = ev.LeaseAbilityIds,
                        LeaseInstanceId = ev.LeaseInstanceId,
                        Receiver = ev.Target.EventReceiver,
                        TimeSpan = TimeSpan.Zero,
                        CancellationTokenSource = ev.CancellationTokenSource,
                        IsScopeDetached = true
                    };
                    RemoveLeaseRecord(ev);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ev.Stage), ev.Stage, "Unknown ability lifecycle stage.");
            }
        }

        [Miaosoft.Sim.Core.Handler]
        public IEnumerable<Event> OnLeaseInvalidated(AbilityLeaseInvalidatedEvent ev)
        {
            if (ev.Receiver != ev.Target.EventReceiver || ev.CompletedSimulationInstances.Count == 0)
                yield break;
            HashSet<Guid> completed = ev.CompletedSimulationInstances.ToHashSet();
            foreach (string id in leaseById.Where(pair => completed.Contains(pair.Value))
                .Select(pair => pair.Key).ToArray())
                leaseById.Remove(id);
        }

        private IEnumerable<Event> Start(AbilityLifecycleEvent ev)
        {
            CancellationTokenSource token = EnsureLeaseToken(ev);

            int remaining = ev.RemainingTicks;
            if (remaining > 0)
            {
                yield return CreateTick(ev);
                remaining--;
            }

            if (remaining > 0)
            {
                yield return NextTick(ev, remaining, token);
            }

            if (ev.LeaseExpireAt.HasValue && ev.LeaseAbilityIds is { Count: > 0 })
            {
                TimeSpan now = ev.Execute ?? ev.Create.Add(ev.TimeSpan);
                TimeSpan delay = ev.LeaseExpireAt.Value > now ? ev.LeaseExpireAt.Value - now : TimeSpan.Zero;
                yield return ev.Context.Factory.NewEvent<AbilityLifecycleEvent>() with
                {
                    CommandEventId = ev.CommandEventId,
                    Source = ev.Source,
                    Target = ev.Target,
                    Stage = AbilityLifecycleStage.Unapply,
                    LeaseId = ev.LeaseId,
                    LeaseInstanceId = ev.LeaseInstanceId,
                    LeaseAbilityIds = ev.LeaseAbilityIds,
                    LeaseExpireAt = ev.LeaseExpireAt,
                    TriggerChain = ev.TriggerChain,
                    Receiver = ev.Receiver,
                    TimeSpan = delay,
                    CancellationTokenSource = token,
                };
            }
        }

        private IEnumerable<Event> Tick(AbilityLifecycleEvent ev)
        {
            if (ev.RemainingTicks <= 0)
            {
                yield break;
            }

            yield return CreateTick(ev);

            int remaining = ev.RemainingTicks - 1;
            if (remaining > 0)
            {
                yield return NextTick(ev, remaining, ev.CancellationTokenSource);
            }
        }

        private static AbilityLifecycleEvent NextTick(
            AbilityLifecycleEvent ev, int remaining, CancellationTokenSource? token)
        {
            return ev.Context.Factory.NewEvent<AbilityLifecycleEvent>() with
            {
                CommandEventId = ev.CommandEventId,
                Source = ev.Source,
                Target = ev.Target,
                Stage = AbilityLifecycleStage.Tick,
                LeaseId = ev.LeaseId,
                LeaseInstanceId = ev.LeaseInstanceId,
                LeaseAbilityIds = ev.LeaseAbilityIds,
                LeaseExpireAt = ev.LeaseExpireAt,
                RemainingTicks = remaining,
                TickInterval = ev.TickInterval,
                TriggerChain = ev.TriggerChain,
                TickDamage = ev.TickDamage,
                TickHeal = ev.TickHeal,
                PeriodicEffect = ev.PeriodicEffect,
                Receiver = ev.Receiver,
                TimeSpan = ev.TickInterval,
                CancellationTokenSource = token,
            };
        }

        private static Event CreateTick(AbilityLifecycleEvent ev)
        {
            AbilityTriggerChain? chain = ev.TriggerChain;
            if (chain != null && ev.Context.ContextTimeSpan > ev.Create)
                chain = chain.RestartPeriodicTick();
            if ((ev.TickDamage == null) == (ev.TickHeal == null))
                throw new InvalidOperationException("A scheduled tick requires exactly one effect profile.");
            if (ev.TickDamage is { } damage)
                return ev.Context.Factory.NewEvent<ScheduledDamageTickEvent>() with
                {
                    CommandEventId = ev.CommandEventId, Source = ev.Source, Target = ev.Target, Damage = damage,
                    ApplicationOrigin = ev.PeriodicEffect?.ApplicationOrigin, TriggerChain = chain,
                    PeriodicEffect = ev.PeriodicEffect, IsFinalTick = ev.RemainingTicks == 1,
                    Receiver = ev.Target.EventReceiver, TimeSpan = TimeSpan.Zero,
                    CancellationTokenSource = ev.CancellationTokenSource
                };
            return ev.Context.Factory.NewEvent<ScheduledHealTickEvent>() with
            {
                CommandEventId = ev.CommandEventId, Source = ev.Source, Target = ev.Target, Heal = ev.TickHeal!,
                ApplicationOrigin = ev.PeriodicEffect?.ApplicationOrigin, TriggerChain = chain,
                PeriodicEffect = ev.PeriodicEffect, IsFinalTick = ev.RemainingTicks == 1,
                Receiver = ev.Target.EventReceiver, TimeSpan = TimeSpan.Zero,
                CancellationTokenSource = ev.CancellationTokenSource
            };
        }

        private CancellationTokenSource EnsureLeaseToken(AbilityLifecycleEvent ev)
        {
            CancellationTokenSource token = ev.CancellationTokenSource ??= new CancellationTokenSource();
            Guid instanceId = ev.LeaseInstanceId ??= Guid.NewGuid();
            if (!string.IsNullOrEmpty(ev.LeaseId))
            {
                leaseById[ev.LeaseId] = instanceId;
            }
            return token;
        }

        private bool IsCurrentLease(AbilityLifecycleEvent ev)
        {
            if (string.IsNullOrEmpty(ev.LeaseId))
            {
                return true;
            }

            Guid instanceId = ev.LeaseInstanceId
                ?? throw new InvalidOperationException("A scheduled ability lease must have an instance ID.");
            return leaseById.TryGetValue(ev.LeaseId, out Guid activeInstance) && activeInstance == instanceId;
        }

        private void RemoveLeaseRecord(AbilityLifecycleEvent ev)
        {
            if (!string.IsNullOrEmpty(ev.LeaseId)
                && leaseById.TryGetValue(ev.LeaseId, out Guid activeInstance)
                && activeInstance == ev.LeaseInstanceId)
            {
                leaseById.Remove(ev.LeaseId);
            }
        }
    }
}
