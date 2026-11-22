using Miaosoft.Sim.Combat.Skill.EventData;

namespace Miaosoft.Sim.Combat.Skill.Handler;

public sealed class ScheduledHealHandler : IHandler, IRuntimeInstanceHandler
{
    public string Id => nameof(ScheduledHealHandler);
    public Guid RuntimeInstanceId { get; } = Guid.NewGuid();

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<IEvent> OnTriggerInvoke(AbilityTriggerInvokeEvent ev)
    {
        if (ev.UpstreamEvent is not ScheduledHealTickEvent tick ||
            ev.TargetRuntimeInstanceId != RuntimeInstanceId ||
            ev.Receiver != tick.Target.EventReceiver || ev.Source != tick.Source || ev.Target != tick.Target ||
            !string.Equals(ev.AbilityId, tick.Heal.Id, StringComparison.Ordinal) ||
            ev.CancellationToken.IsCancellationRequested || !tick.Target.IsAlive)
            yield break;

        foreach (Event next in HealFlow.Create(ev, tick.Heal, tick.Source, tick.Target))
            yield return next;
    }
}
