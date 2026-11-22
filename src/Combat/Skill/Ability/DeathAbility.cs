using Miaosoft.Sim.Core;
using Miaosoft.Sim.Combat.Skill.EventData;

namespace Miaosoft.Sim.Combat.Skill.Ability
{
    public class DeathAbility : AbilityBase
    {
        public TimeSpan TimeSpan { get; } = TimeSpan.Zero;

        [Handler]
        public IEnumerable<Event> OnDeath(DeathEvent ev)
        {
            if (ev.CancellationToken.IsCancellationRequested || ev.Receiver != ev.Victim.EventReceiver ||
                !ev.Victim.IsAlive)
                yield break;

            yield return ev.Context.Factory.NewEvent<DeathCommitEvent>() with
            {
                Attempt = ev,
                Receiver = ev.Victim.EventReceiver,
                TimeSpan = TimeSpan.Zero,
                CancellationTokenSource = ev.CancellationTokenSource
            };
        }

        [Handler]
        public IEnumerable<Event> OnCommit(DeathCommitEvent ev)
        {
            DeathEvent attempt = ev.Attempt;
            if (ev.CancellationToken.IsCancellationRequested || ev.Receiver != attempt.Victim.EventReceiver ||
                !attempt.Victim.IsAlive)
                yield break;

            if (attempt.Victim.DeferredImpacts?.TryDeferDeath(attempt) == true)
                yield break;

            if (ev.ReapplyDeferredDamage && attempt.LifePointReduce > 0)
            {
                int previous = attempt.Victim.Property.LifePoint ?? 0;
                attempt.Victim.Property.LifePoint = Math.Max(0, previous - attempt.LifePointReduce);
                yield return UnitLifeChangedEvent.FromUnit(ev.Context, attempt.Victim, previous);
                if (attempt.Victim.Property.LifePoint > 0)
                    yield break;
            }
            else if (ev.ReapplyDeferredDamage)
            {
                int previous = attempt.Victim.Property.LifePoint ?? 0;
                attempt.Victim.Property.LifePoint = 0;
                yield return UnitLifeChangedEvent.FromUnit(ev.Context, attempt.Victim, previous);
            }
            attempt.Victim.Killed(attempt.AttemptId);
            yield return ev.Context.Factory.NewEvent<DeathResolvedEvent>() with
            {
                DeathId = attempt.AttemptId,
                Victim = attempt.Victim,
                Killer = attempt.Killer,
                DamageAbility = attempt.DamageAbility,
                ApplicationOrigin = attempt.ApplicationOrigin,
                SourceObject = attempt.SourceObject ?? attempt.Killer?.MapObject,
                TargetObject = attempt.TargetObject ?? attempt.Victim.MapObject,
                Receiver = ev.Receiver,
                TimeSpan = TimeSpan.Zero,
                Priority = CombatEventPriority.DeathResolved
            };
        }
    }
}
