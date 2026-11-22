using Miaosoft.Sim.Combat.Skill.Expression;
using Miaosoft.Sim.Combat.Skill.EventData;

namespace Miaosoft.Sim.Combat.Skill.Content.Registration;

public sealed class TriggerConditionSchemas
{
    private readonly Dictionary<TriggerType, ExpressionSchema<TriggerConditionContext>> schemas = new();

    internal TriggerConditionSchemas()
    {
        foreach (TriggerType type in new[] { TriggerType.Command, TriggerType.ProjectileHit,
            TriggerType.ProjectileExplosion, TriggerType.TargetLifeReduce, TriggerType.Death, TriggerType.Heal,
            TriggerType.Signal, TriggerType.ScheduledDamageTick, TriggerType.ScheduledHealTick,
            TriggerType.DamageSkillHit, TriggerType.BeingDamaged, TriggerType.UnitLevelUp })
        {
            var schema = new ExpressionSchema<TriggerConditionContext>();
            schema.RegisterIdentity("holder", ExpressionValueType.UnitIdentity, c => c.Holder.Id);
            schema.RegisterIdentity("event.source", ExpressionValueType.UnitIdentity, c => c.EventSource?.Id, nullable: true);
            schema.RegisterIdentity("event.target", ExpressionValueType.UnitIdentity, c => c.EventTarget?.Id, nullable: true);
            schema.RegisterIdentity("event.ability", ExpressionValueType.AbilityIdentity,
                c => c.EventAbility?.RuntimeInstanceId, nullable: true);
            schema.RegisterString("event.ability.definition", c => c.EventAbility?.DefinitionId, nullable: true);
            schema.RegisterIdentity("event.ability.source", ExpressionValueType.SourceIdentity,
                c => c.EventAbility?.GrantSourceInstanceId, nullable: true);
            schema.RegisterString("event.ability.source.category", c => c.EventAbility?.GrantSourceCategory, nullable: true);
            schema.RegisterIdentity("listener.ability", ExpressionValueType.AbilityIdentity,
                c => c.ListenerAbility.RuntimeInstanceId);
            schema.RegisterString("listener.ability.definition", c => c.ListenerAbility.DefinitionId);
            schema.RegisterIdentity("listener.ability.source", ExpressionValueType.SourceIdentity,
                c => c.ListenerAbility.GrantSourceInstanceId, nullable: true);
            schema.RegisterString("listener.ability.source.category", c => c.ListenerAbility.GrantSourceCategory, nullable: true);
            schemas.Add(type, schema);
        }
        ExpressionSchema<TriggerConditionContext> upgrades = schemas[TriggerType.UnitLevelUp];
        upgrades.RegisterNumber("event.level.old", c => ((UnitLevelUpEvent)c.Event).OldLevel);
        upgrades.RegisterNumber("event.level.new", c => ((UnitLevelUpEvent)c.Event).NewLevel);
        upgrades.RegisterNumber("event.level.delta", c =>
            ((UnitLevelUpEvent)c.Event).NewLevel - ((UnitLevelUpEvent)c.Event).OldLevel);
    }

    public ExpressionSchema<TriggerConditionContext> For(TriggerType type) =>
        schemas.TryGetValue(type, out var schema) ? schema :
            throw new ArgumentOutOfRangeException(nameof(type), "This trigger does not support conditions.");

    internal void Seal()
    {
        foreach (var schema in schemas.Values) schema.Seal();
    }
}
