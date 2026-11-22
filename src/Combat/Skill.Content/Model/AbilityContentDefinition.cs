namespace Miaosoft.Sim.Combat.Skill.Content.Model;

public sealed record AbilityTriggerDefinition(
    TriggerType Type, TriggerSourceScope EventSourceScope, IReadOnlyList<string> SourceAbilityIds,
    double? Probability, DeathTriggerPhase DeathPhase, IReadOnlyList<ITriggerCondition> Conditions,
    string? SignalId = null);

public sealed record AbilityContentDefinition(
    string Id, string? Name, int Priority, bool AllowMultiple, AbilityTriggerDefinition Trigger);
