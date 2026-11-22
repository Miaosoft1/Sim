using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Combat.Skill.Targeting;

namespace Miaosoft.Sim.Combat.Skill;

internal static class DamageFlow
{
    public static IEnumerable<Event> Create(
        Event cause, DamageProfile profile, Unit? source, Unit target, TimeSpan delay,
        CancellationTokenSource? cancellation, double? configuredProjectileSpeed)
    {
        DamageEvent damage = CreateEvent(cause, profile, source, target, delay,
            source?.EventReceiver ?? target.EventReceiver, cancellation, configuredProjectileSpeed);
        yield return DamageRequestEvent.FromDamage(damage);
    }

    private static DamageEvent CreateEvent(
        Event cause, DamageProfile profile, Unit? source, Unit target, TimeSpan delay, EventReceiver receiver,
        CancellationTokenSource? cancellation, double? configuredProjectileSpeed)
    {
        Guid commandId = cause switch
        {
            CommandEvent command => command.CommandInstanceId,
            ProjectileHitEvent hit => hit.CommandEventId,
            AbilityLifecycleEvent lifecycle => lifecycle.CommandEventId,
            DamageEvent previous => previous.CommandEventId,
            AbilityTriggerInvokeEvent trigger => trigger.CommandEventId,
            _ => throw new InvalidOperationException($"Cannot create damage from {cause.GetType().Name}.")
        };
        (double? distance, double? projectileSpeed, double travelDistance) = cause switch
        {
            CommandEvent command =>
                (command.Distance ?? (source?.MapObject != null && target.MapObject != null
                    ? source.MapObject.Position.DistanceTo(target.MapObject.Position) : null),
                    configuredProjectileSpeed, 0d),
            DamageEvent previous => (previous.Distance, previous.ProjectileSpeed, previous.ProjectileTravelDistance),
            _ => (null, null, 0d)
        };
        AbilityTriggerChain? chain = cause switch
        {
            ProjectileHitEvent hit => hit.TriggerChain,
            DamageEvent previous => previous.TriggerChain,
            AbilityLifecycleEvent lifecycle => lifecycle.TriggerChain,
            AbilityTriggerInvokeEvent trigger => trigger.TriggerChain,
            _ => null
        };
        DamageEvent result = cause.Context.Factory.NewEvent<DamageEvent>() with
        {
            CommandEventId = commandId,
            Source = source,
            Target = target,
            ApplicationOrigin = cause switch
            {
                AbilityTriggerInvokeEvent { UpstreamEvent: ScheduledDamageTickEvent tick } => tick.ApplicationOrigin,
                DamageEvent previous => previous.ApplicationOrigin,
                _ => null
            },
            Distance = distance,
            SourceObject = cause is AbilityTriggerInvokeEvent spatial ? spatial.SourceObject : source?.MapObject,
            TargetObject = cause switch
            {
                AbilityLifecycleEvent => target.MapObject,
                AbilityTriggerInvokeEvent trigger when trigger.Target == target => trigger.TargetObject,
                _ when source == null => target.MapObject,
                _ => UnitSpatialRegistry.ObjectIn(target,
                    (cause is AbilityTriggerInvokeEvent origin ? origin.SourceObject : source.MapObject)?.MapRuntime)
            },
            ProjectileSpeed = projectileSpeed,
            ProjectileTravelDistance = travelDistance,
            TriggerChain = chain,
            Receiver = receiver,
            TimeSpan = delay,
            CancellationTokenSource = cancellation
        };
        result.SetPendingDamage(profile, source, target, delay);
        return result;
    }
}
