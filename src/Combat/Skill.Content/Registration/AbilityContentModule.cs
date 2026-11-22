using Miaosoft.Sim.Combat.Skill.Content.Dto;
using Miaosoft.Sim.Combat.Skill.Content.Model;
using Miaosoft.Sim.Combat.Skill.Content.Parser;
using Miaosoft.Sim.Combat.Skill.Expression;
using Miaosoft.Sim.Content.Core;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Miaosoft.Sim.Combat.Skill.Content.Registration;

internal sealed class AbilityContentModule(
    DamageAbilityParser damage, BeingDamageAbilityParser receiver, DeathAbilityParser death, ProjectileAbilityParser projectile,
    HealAbilityParser heal, StatusAbilityParser status, PropertyAbilityParser property) : IContentModule
{
    private sealed record Authored(
        AbilityContentDefinition Definition, string Type, string? Data, ContentReadContext Source);

    public string Id => SkillContentServiceCollectionExtensions.AbilityModuleId;

    public void Register(ContentSectionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        AbilityParserRegistration parsers = registry.AbilityParsers();
        parsers.Register(damage);
        parsers.Register(receiver);
        parsers.Register(death);
        parsers.Register(projectile);
        parsers.Register(heal);
        parsers.Register(status);
        parsers.Register(property);
        parsers.Register(new ExperienceRewardAbilityParser());
        ExpressionSchema<CombatFormulaContext> formulas = registry.CombatFormulas();
        ExpressionSchema<PropertyFormulaContext> propertyFormulas = registry.PropertyFormulas();
        propertyFormulas.RegisterNumber("ability.level", context => context.AbilityLevel);
        propertyFormulas.RegisterNumber("source.level", context => context.SourceProperty?.Level, nullable: true);
        propertyFormulas.RegisterNumber("target.level", context => context.TargetProperty.Level);
        propertyFormulas.RegisterNumber("target.life", context => context.TargetProperty.LifePoint, nullable: true);
        propertyFormulas.RegisterNumber("target.lifeMax", context => context.TargetProperty.LifePointMax);
        propertyFormulas.RegisterNumber("target.energy", context => (double)context.TargetProperty.Energy);
        propertyFormulas.RegisterNumber("target.energyMax", context => context.TargetProperty.EnergyMax);
        PropertyTargetRegistration propertyTargets = registry.PropertyTargets();
        propertyTargets.Register(PropertyModifierTargets.LifePointMax);
        propertyTargets.Register(PropertyModifierTargets.EnergyMax);
        propertyTargets.Register(PropertyModifierTargets.MoveSpeedBase);
        propertyTargets.Register(PropertyModifierTargets.MoveSpeed);
        propertyTargets.Register(PropertyModifierTargets.ExperienceMax);
        propertyTargets.Register(PropertyModifierTargets.VisionRange);
        propertyTargets.Register(PropertyModifierTargets.Stealth);
        TriggerConditionSchemas conditions = registry.TriggerConditions();
        var authored = new Dictionary<string, Authored>(StringComparer.Ordinal);

        registry.RegisterReferenceResolver("ability", id =>
            registry.Get<AbilityCatalog>("ability").Definitions.ContainsKey(id));
        registry.RegisterCollection("ability", (yaml, source) =>
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlSequenceNode entries)
                throw new InvalidDataException($"{source.SourceFile}: ability section must be a list.");
            foreach (YamlNode node in entries.Children)
            {
                if (node is not YamlMappingNode mapping)
                    throw new InvalidDataException($"{source.SourceFile}: every ability must be a mapping.");
                string? data = null;
                var dataKey = new YamlScalarNode("data");
                if (mapping.Children.TryGetValue(dataKey, out YamlNode? payload))
                {
                    data = Serialize(payload);
                    mapping.Children.Remove(dataKey);
                }
                AbilityDto dto = source.Deserialize<AbilityDto>(Serialize(mapping))
                    ?? throw new InvalidDataException($"{source.SourceFile}: an ability cannot be empty.");
                AbilityContentDefinition definition = CompileHeader(dto, source, conditions);
                string type = dto.Type?.Trim()
                    ?? throw new InvalidDataException($"{source.SourceFile}: ability '{definition.Id}' requires a type.");
                if (type.Length == 0)
                    throw new InvalidDataException($"{source.SourceFile}: ability '{definition.Id}' requires a type.");
                if (!authored.TryAdd(definition.Id, new(definition, type, data, source)))
                    throw new InvalidDataException($"{source.SourceFile}: duplicate ability '{definition.Id}'.");
            }
        }, () =>
        {
            var definitions = new List<AbilityHandlerDefinition>(authored.Count);
            foreach (Authored entry in authored.Values)
            {
                try
                {
                    var context = new AbilityParserContext(
                        registry, entry.Source, formulas, propertyFormulas, propertyTargets, conditions);
                    AbilityHandlerDefinition definition = parsers.Require(entry.Type).Compile(entry.Definition, entry.Data, context);
                    if (definition.Type != entry.Type)
                        throw new InvalidDataException(
                            $"Compiled type '{definition.Type}' does not match registered type '{entry.Type}'.");
                    definitions.Add(definition);
                }
                catch (Exception error) when (error is FormatException or ArgumentException or
                    InvalidDataException or YamlException)
                {
                    throw new InvalidDataException(
                        $"{entry.Source.SourceFile}: ability '{entry.Definition.Id}': {error.Message}", error);
                }
            }
            return new AbilityCatalog(definitions);
        }, "damageType");
    }

    private static AbilityContentDefinition CompileHeader(
        AbilityDto dto, ContentReadContext source, TriggerConditionSchemas schemas)
    {
        if (string.IsNullOrWhiteSpace(dto.Id))
            throw new InvalidDataException($"{source.SourceFile}: an ability requires an ID.");
        string id = dto.Id.Trim();
        if (!dto.AllowMultiple.HasValue)
            throw new InvalidDataException($"{source.SourceFile}: ability '{id}' requires allowMultiple.");
        AbilityTriggerDto trigger = dto.Trigger
            ?? throw new InvalidDataException($"{source.SourceFile}: ability '{id}' requires a trigger object.");
        return new AbilityContentDefinition(id, dto.Name, dto.Priority, dto.AllowMultiple.Value,
            CompileTrigger(trigger, id, source, schemas));
    }

    internal static AbilityTriggerDefinition CompileTrigger(
        AbilityTriggerDto trigger, string id, ContentReadContext source, TriggerConditionSchemas schemas)
    {
        if (trigger.Type == null || !Enum.TryParse(trigger.Type, out TriggerType type) ||
            !Enum.IsDefined(type) || !string.Equals(type.ToString(), trigger.Type, StringComparison.Ordinal))
            throw new InvalidDataException($"{source.SourceFile}: ability '{id}' has an invalid trigger type.");
        if (type is not (TriggerType.None or TriggerType.Command or TriggerType.ProjectileHit or
            TriggerType.ProjectileExplosion or TriggerType.TargetLifeReduce or TriggerType.Death or TriggerType.Heal or
            TriggerType.Signal or TriggerType.ScheduledDamageTick or TriggerType.ScheduledHealTick or
            TriggerType.DamageSkillHit or TriggerType.BeingDamaged or TriggerType.UnitLevelUp))
            throw new InvalidDataException($"{source.SourceFile}: ability '{id}' uses an unsupported trigger type '{type}'.");
        TriggerSourceScope scope = TriggerSourceScope.Self;
        if (trigger.EventSourceScope != null &&
            (!Enum.TryParse(trigger.EventSourceScope, out scope) || !Enum.IsDefined(scope) ||
             !string.Equals(scope.ToString(), trigger.EventSourceScope, StringComparison.Ordinal)))
            throw new InvalidDataException($"{source.SourceFile}: ability '{id}' has an invalid eventSourceScope.");
        if (scope != TriggerSourceScope.Self && type is not
            (TriggerType.Command or TriggerType.ProjectileHit or TriggerType.ProjectileExplosion or
                TriggerType.TargetLifeReduce or TriggerType.Death or TriggerType.Heal or TriggerType.Signal or
                TriggerType.ScheduledDamageTick or TriggerType.ScheduledHealTick or
                TriggerType.DamageSkillHit or TriggerType.BeingDamaged or TriggerType.UnitLevelUp))
            throw new InvalidDataException(
                $"{source.SourceFile}: ability '{id}' uses an unsupported eventSourceScope for trigger '{type}'.");
        DeathTriggerPhase deathPhase = DeathTriggerPhase.Confirmed;
        if (type == TriggerType.Signal ? string.IsNullOrWhiteSpace(trigger.SignalId) : trigger.SignalId != null)
            throw new InvalidDataException($"{source.SourceFile}: ability '{id}' requires signalId only for a Signal trigger.");
        if (trigger.DeathPhase != null &&
            (type != TriggerType.Death || !Enum.TryParse(trigger.DeathPhase, out deathPhase) ||
             !Enum.IsDefined(deathPhase) ||
             !string.Equals(deathPhase.ToString(), trigger.DeathPhase, StringComparison.Ordinal)))
            throw new InvalidDataException($"{source.SourceFile}: ability '{id}' has an invalid deathPhase.");
        if (trigger.Probability is double probability &&
            (!double.IsFinite(probability) || probability < 0 || probability > 1))
            throw new InvalidDataException($"{source.SourceFile}: ability '{id}' has an invalid trigger probability.");
        string[] sourceIds = (trigger.SourceAbilityIds ?? []).Select(sourceId =>
        {
            if (string.IsNullOrWhiteSpace(sourceId))
                throw new InvalidDataException($"{source.SourceFile}: ability '{id}' has an empty trigger source ID.");
            return sourceId.Trim();
        }).ToArray();
        if (sourceIds.Distinct(StringComparer.Ordinal).Count() != sourceIds.Length)
            throw new InvalidDataException($"{source.SourceFile}: ability '{id}' has duplicate trigger source IDs.");
        if (type == TriggerType.None && (sourceIds.Length != 0 || trigger.Probability.HasValue))
            throw new InvalidDataException($"{source.SourceFile}: ability '{id}' has options on a None trigger.");
        foreach (string sourceId in sourceIds)
            source.RequireReference("ability", sourceId);
        IReadOnlyList<ITriggerCondition> conditions;
        try
        {
            conditions = TriggerConditionCompiler.Compile(trigger.Conditions, type, schemas);
        }
        catch (Exception error) when (error is FormatException or ArgumentException or InvalidDataException)
        {
            throw new InvalidDataException(
                $"{source.SourceFile}: ability '{id}' trigger conditions: {error.Message}", error);
        }
        return new AbilityTriggerDefinition(type, scope, Array.AsReadOnly(sourceIds), trigger.Probability, deathPhase, conditions,
            trigger.SignalId);
    }

    private static string Serialize(YamlNode node)
    {
        using var writer = new StringWriter();
        new YamlStream(new YamlDocument(node)).Save(writer, assignAnchors: false);
        return writer.ToString();
    }
}
