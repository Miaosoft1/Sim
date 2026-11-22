using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.Vector;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Combat.Skill.Handler;
using Miaosoft.Sim.Combat.Skill.Targeting;

namespace Miaosoft.Sim.Combat.Skill;

public sealed class TriggerConditionContext
{
    private readonly TriggerHandler? trigger;
    private readonly MapObject? sourceObject;
    private readonly MapObject? targetObject;
    private AbilityRuntimeSnapshot? listenerAbility;

    public TriggerConditionContext(Event upstream, Unit holder, TriggerHandler trigger) : this(upstream, holder)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        this.trigger = trigger;
    }

    public TriggerConditionContext(Event upstream, Unit holder, AbilityRuntimeSnapshot listenerAbility) : this(upstream, holder)
    {
        ArgumentNullException.ThrowIfNull(listenerAbility);
        this.listenerAbility = listenerAbility;
    }

    private TriggerConditionContext(Event upstream, Unit holder)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        ArgumentNullException.ThrowIfNull(holder);
        Event = upstream;
        Holder = holder;
        (EventSource, EventTarget, sourceObject, targetObject, HitPoint, EventAbility) =
            TriggerEventView.From(upstream);
    }

    public Event Event { get; }
    public Unit Holder { get; }
    public Unit? EventSource { get; }
    public Unit? EventTarget { get; }
    public AbilityRuntimeSnapshot? EventAbility { get; }
    public AbilityRuntimeSnapshot ListenerAbility
    {
        get
        {
            if (listenerAbility != null) return listenerAbility;
            TriggerHandler registration = trigger ??
                throw new InvalidOperationException("The listener requires its trigger registration or captured ability.");
            Guid instanceId = registration.TargetInstanceId ??
                throw new InvalidOperationException("The listener requires a bound ability instance.");
            AbilityBase ability = Holder.EventReceiver.GetHandlers<AbilityBase>()
                .SingleOrDefault(candidate => candidate.RuntimeInstanceId == instanceId) ??
                throw new InvalidOperationException("The listener's bound ability is not installed on its holder.");
            if (!string.Equals(ability.Id, registration.TargetAbilityId, StringComparison.Ordinal) ||
                registration.Binding.Source is { } source && !ReferenceEquals(source, ability.Binding.Source))
                throw new InvalidOperationException("The listener and its bound ability have inconsistent identities or sources.");
            return listenerAbility = AbilityRuntimeSnapshot.Capture(ability);
        }
    }
    public Vector2D? HitPoint { get; }
    public MapRuntime? HitMap => (Event as ProjectileHitEvent)?.HitMap;
    public MapObject? SourceObject => sourceObject ?? EventSource?.MapObject;
    public MapObject? TargetObject => targetObject ?? (EventTarget == null ? null :
        SourceObject?.MapRuntime is { } map ? UnitSpatialRegistry.ObjectIn(EventTarget, map) : EventTarget.MapObject);
}
