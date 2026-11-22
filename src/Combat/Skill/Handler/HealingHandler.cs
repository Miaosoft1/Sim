using Miaosoft.Sim.Combat.Skill.EventData;

namespace Miaosoft.Sim.Combat.Skill.Handler;

public sealed class HealingHandler : IHandler
{
    public string Id => nameof(HealingHandler);

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<Event> OnHeal(HealEvent ev)
    {
        if (ev.Receiver != ev.Target.EventReceiver || ev.CancellationToken.IsCancellationRequested)
            yield break;

        int maximum = ev.Target.Property.LifePointMax;
        int current = ev.Target.Property.LifePoint ?? maximum;
        int missing = Math.Max(0, maximum - current);
        int gain = ev.RestoreToMaximum ? missing : Math.Clamp(ev.HealAmount, 0, missing);
        ev.Target.Property.LifePoint = current + gain;

        var gained = ev.Context.Factory.NewEvent<HealLifeGainEvent>() with
        {
            CommandEventId = ev.CommandEventId, HealAbility = ev.HealAbility,
            Source = ev.Source, Target = ev.Target, ApplicationOrigin = ev.ApplicationOrigin,
            SourceObject = ev.SourceObject, TargetObject = ev.TargetObject,
            LifeGain = gain, TargetLifePoint = ev.Target.Property.LifePoint.Value,
            Receiver = ev.Target.EventReceiver, TimeSpan = TimeSpan.Zero,
            CancellationTokenSource = new CancellationTokenSource()
        };
        yield return gained;
        if (ev.Source != null && ev.Source.EventReceiver != ev.Target.EventReceiver)
            yield return gained with { Receiver = ev.Source.EventReceiver };
        if (ev.CompletionReceiver != null && ev.CompletionReceiver != ev.Target.EventReceiver &&
            ev.CompletionReceiver != ev.Source?.EventReceiver)
            yield return gained with { Receiver = ev.CompletionReceiver };
    }
}
