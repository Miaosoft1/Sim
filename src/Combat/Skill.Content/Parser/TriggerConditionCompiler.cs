using Miaosoft.Sim.Combat.Skill.Content.Dto;
using Miaosoft.Sim.Combat.Skill.Content.Registration;
using Miaosoft.Sim.Combat.Skill.Expression;

namespace Miaosoft.Sim.Combat.Skill.Content.Parser;

internal static class TriggerConditionCompiler
{
    public static IReadOnlyList<ITriggerCondition> Compile(
        IReadOnlyList<TriggerConditionDto>? conditions, TriggerType type, TriggerConditionSchemas schemas)
    {
        if (conditions == null) return Array.Empty<ITriggerCondition>();
        if (type == TriggerType.None)
            throw new InvalidDataException("A None trigger cannot declare conditions.");
        var compiled = new List<ITriggerCondition>(conditions.Count);
        foreach (TriggerConditionDto condition in conditions)
        {
            if (condition == null)
                throw new InvalidDataException("A trigger condition cannot be null.");
            compiled.Add(condition.Type switch
            {
                "Expression" when !string.IsNullOrWhiteSpace(condition.Expression) &&
                    condition.Relation == null && condition.Anchor == null && condition.Maximum == null =>
                    new ExpressionTriggerCondition(StrictExpressionCompiler.Compile(
                        condition.Expression, schemas.For(type), ExpressionValueType.Boolean)),
                "TargetRelation" when condition.Expression == null && condition.Anchor == null &&
                    condition.Maximum == null => new TargetRelationTriggerCondition(
                        ParseEnum<TriggerSourceScope>(condition.Relation, "relation")),
                "Distance" when condition.Expression == null && condition.Relation == null &&
                    condition.Maximum.HasValue => CompileDistance(condition, type),
                _ => throw new InvalidDataException("A trigger condition has an invalid type or incompatible fields.")
            });
        }
        return compiled.AsReadOnly();
    }

    private static DistanceTriggerCondition CompileDistance(TriggerConditionDto condition, TriggerType type)
    {
        TriggerDistanceAnchor anchor = ParseEnum<TriggerDistanceAnchor>(condition.Anchor, "anchor");
        if (anchor == TriggerDistanceAnchor.HitPoint &&
            type is not (TriggerType.ProjectileHit or TriggerType.ProjectileExplosion))
            throw new InvalidDataException("The HitPoint distance anchor requires a projectile hit or explosion.");
        return new DistanceTriggerCondition(anchor, condition.Maximum!.Value);
    }

    private static T ParseEnum<T>(string? value, string field) where T : struct, Enum =>
        value != null && Enum.TryParse(value, out T parsed) && Enum.IsDefined(parsed) &&
            string.Equals(parsed.ToString(), value, StringComparison.Ordinal)
            ? parsed : throw new InvalidDataException($"A trigger condition has an invalid {field}.");
}
