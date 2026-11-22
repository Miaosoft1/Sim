using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Combat.Skill.Targeting;

namespace Miaosoft.Sim.Combat.Skill;

internal static class HealFlow
{
    public static IEnumerable<Event> Create(
        AbilityTriggerInvokeEvent cause, HealProfile profile, Unit? source, Unit target)
    {
        bool periodic = cause.UpstreamEvent is ScheduledHealTickEvent;
        CancellationTokenSource? cancellation = periodic ? cause.CancellationTokenSource : new CancellationTokenSource();
        var heal = cause.Context.Factory.NewEvent<HealEvent>() with
        {
            CommandEventId = cause.CommandEventId, HealAbility = profile, Source = source, Target = target,
            ApplicationOrigin = (cause.UpstreamEvent as ScheduledHealTickEvent)?.ApplicationOrigin,
            CompletionReceiver = cause.CompletionReceiver,
            SourceObject = cause.SourceObject,
            TargetObject = source == null ? target.MapObject :
                UnitSpatialRegistry.ObjectIn(target, cause.SourceObject?.MapRuntime),
            HealAmount = profile.RestoreToMaximum
                ? Math.Max(0, target.Property.LifePointMax - (target.Property.LifePoint ?? target.Property.LifePointMax))
                : checked(profile.Amount * ((cause.UpstreamEvent as ScheduledHealTickEvent)?.PeriodicEffect?.ReadStatusStack() ?? 1)),
            RestoreToMaximum = profile.RestoreToMaximum,
            TriggerChain = cause.TriggerChain, Receiver = target.EventReceiver,
            TimeSpan = TimeSpan.Zero, CancellationTokenSource = cancellation
        };
        yield return HealPublicationEvent.FromHeal(heal);
    }
}
