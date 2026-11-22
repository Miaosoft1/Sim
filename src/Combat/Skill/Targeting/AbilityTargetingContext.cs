using Miaosoft.Sim.Combat.Map.Vector;
using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Skill.EventData;

namespace Miaosoft.Sim.Combat.Skill.Targeting
{
    public sealed record AbilityTargetingContext
    {
        public required Unit Source { get; init; }
        public Unit? Target { get; init; }
        public Vector2D? TargetPoint { get; init; }
        private readonly MapObject? sourceObject;
        private readonly MapObject? targetObject;
        public MapObject? SourceObject { get => sourceObject ?? Source.MapObject; init => sourceObject = value; }
        public MapObject? TargetObject
        {
            get => targetObject ?? (Target == null ? null : UnitSpatialRegistry.ObjectIn(Target, SourceObject?.MapRuntime));
            init => targetObject = value;
        }
        public bool RequireSelectable { get; init; }
        public DeathResolvedEvent? Death { get; init; }
        public DeathEvent? PendingDeath { get; init; }
        public Event? UpstreamEvent { get; init; }

        public MapObject? ObjectFor(Unit unit) => unit == Source ? SourceObject :
            unit == Target ? TargetObject : UnitSpatialRegistry.ObjectIn(unit, SourceObject?.MapRuntime);

        public AbilityTargetingContext FromOrigin(TargetingOrigin origin)
        {
            return origin switch
            {
                TargetingOrigin.Source => this with
                {
                    Target = Source,
                    TargetPoint = SourceObject?.Position,
                    TargetObject = SourceObject,
                },
                TargetingOrigin.HitPoint => this with { Target = null },
                TargetingOrigin.EventSource => WithEventTarget(source: true),
                TargetingOrigin.EventTarget => WithEventTarget(source: false),
                _ => this,
            };
        }

        private AbilityTargetingContext WithEventTarget(bool source)
        {
            Event upstream = UpstreamEvent ??
                throw new InvalidOperationException("An event-role selector requires an upstream event.");
            var (eventSource, eventTarget, eventSourceObject, eventTargetObject, _, _) =
                TriggerEventView.From(upstream);
            Unit? target = source ? eventSource : eventTarget;
            MapObject? targetObject = source ? eventSourceObject : eventTargetObject;
            targetObject ??= target == null ? null : UnitSpatialRegistry.ObjectIn(target, SourceObject?.MapRuntime);
            return this with { Target = target, TargetObject = targetObject, TargetPoint = targetObject?.Position };
        }

        public static AbilityTargetingContext FromCommand(CommandEvent command)
        {
            return new AbilityTargetingContext
            {
                Source = command.Source,
                Target = command.Target,
                SourceObject = command.SourceObject ?? command.Source.MapObject,
                TargetObject = command.TargetObject ?? (command.Target == null ? null :
                    UnitSpatialRegistry.ObjectIn(command.Target, (command.SourceObject ?? command.Source.MapObject)?.MapRuntime)),
                TargetPoint = command.ResolvedTargetPoint,
                RequireSelectable = true,
                UpstreamEvent = command,
            };
        }

        public static AbilityTargetingContext FromTrigger(AbilityTriggerInvokeEvent trigger)
        {
            if (trigger.DirectCommand is { } command)
            {
                return FromCommand(command);
            }

            return new AbilityTargetingContext
            {
                Source = trigger.Source ?? throw new InvalidOperationException("Triggered abilities require a source unit."),
                Death = trigger.UpstreamEvent as DeathResolvedEvent,
                PendingDeath = trigger.UpstreamEvent as DeathEvent,
                Target = trigger.Target,
                SourceObject = trigger.SourceObject ?? trigger.Source?.MapObject,
                TargetObject = trigger.TargetObject,
                TargetPoint = trigger.HitPoint ?? trigger.TargetObject?.Position,
                UpstreamEvent = trigger.UpstreamEvent,
            };
        }
    }
}
