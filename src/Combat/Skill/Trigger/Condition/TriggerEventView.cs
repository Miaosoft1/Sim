using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.Vector;
using Miaosoft.Sim.Combat.Skill.EventData;

namespace Miaosoft.Sim.Combat.Skill;

internal static class TriggerEventView
{
    internal static (Unit? Source, Unit? Target, MapObject? SourceObject, MapObject? TargetObject,
        Vector2D? HitPoint, AbilityRuntimeSnapshot? Ability) From(Event upstream) => upstream switch
    {
        CommandEvent command => (command.Source, command.Target, command.SourceObject, command.TargetObject,
            null, null),
        ProjectileHitEvent hit => (hit.Source, hit.Target, hit.SourceObject, hit.TargetObject,
            hit.HitPoint, hit.AbilitySnapshot),
        DamageLifeReduceEvent damage => (damage.Source, damage.Target, damage.SourceObject, damage.TargetObject,
            null, damage.DamageAbility.Ability),
        DamageEvent damage => (damage.Source, damage.Target, damage.SourceObject, damage.TargetObject,
            null, damage.AbilitySnapshot),
        HealEvent heal => (heal.Source, heal.Target, heal.SourceObject, heal.TargetObject,
            null, heal.HealAbility.Ability),
        AbilitySignalEvent signal => (signal.Source, signal.Target, null, null, null, signal.AbilitySnapshot),
        UnitLevelUpEvent upgrade => (upgrade.Unit, upgrade.Unit, null, null, null, null),
        ScheduledDamageTickEvent tick => (tick.Source, tick.Target, null, null, null, tick.Damage.Ability),
        ScheduledHealTickEvent tick => (tick.Source, tick.Target, null, null, null, tick.Heal.Ability),
        DeathEvent death => (death.Killer, death.Victim, death.SourceObject, death.TargetObject,
            null, death.DamageAbility.Ability),
        DeathResolvedEvent death => (death.Killer, death.Victim, death.SourceObject, death.TargetObject,
            null, death.DamageAbility?.Ability),
        _ => throw new InvalidOperationException(
            $"Trigger events do not support event '{upstream.GetType().Name}'.")
    };
}
