using Miaosoft.Sim.Combat.Skill.EventData;

namespace Miaosoft.Sim.Combat.Skill.Handler;

public sealed class ScheduledDamageHandler : IHandler, IRuntimeInstanceHandler
{
    public string Id => nameof(ScheduledDamageHandler);
    public Guid RuntimeInstanceId { get; } = Guid.NewGuid();

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<IEvent> OnTriggerInvoke(AbilityTriggerInvokeEvent ev)
    {
        if (ev.UpstreamEvent is not ScheduledDamageTickEvent tick ||
            ev.TargetRuntimeInstanceId != RuntimeInstanceId ||
            ev.Receiver != tick.Target.EventReceiver || ev.Source != tick.Source || ev.Target != tick.Target ||
            !string.Equals(ev.AbilityId, tick.Damage.Id, StringComparison.Ordinal) ||
            ev.CancellationToken.IsCancellationRequested || !tick.Target.IsAlive)
            yield break;

        DamageProfile damage = tick.Damage.WithStack(tick.PeriodicEffect?.ReadStatusStack() ?? 1);
        foreach (Event next in DamageFlow.Create(ev, damage, tick.Source, tick.Target,
            TimeSpan.Zero, ev.CancellationTokenSource, null))
            yield return next;
    }
}
