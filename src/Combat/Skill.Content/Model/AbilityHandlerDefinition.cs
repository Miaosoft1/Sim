using Miaosoft.Sim.Core;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.Expression;

namespace Miaosoft.Sim.Combat.Skill.Content.Model;

public sealed class AbilityHandlerDefinition
{
    private readonly Func<int, FormulaEvaluationPolicy, IReadOnlyList<IHandler>> create;

    public AbilityHandlerDefinition(
        AbilityContentDefinition content, string type, Func<int, FormulaEvaluationPolicy, IReadOnlyList<IHandler>> create)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        if (type != type.Trim())
            throw new ArgumentException("Ability types must be normalized.", nameof(type));
        ArgumentNullException.ThrowIfNull(create);
        Content = content;
        Type = type;
        this.create = create;
    }

    public AbilityContentDefinition Content { get; }
    public string Type { get; }

    public IReadOnlyList<IHandler> CreateHandlers(int? level = null, FormulaEvaluationPolicy? policy = null)
    {
        int actualLevel = level ?? 1;
        if (actualLevel < 1)
            throw new ArgumentOutOfRangeException(nameof(level), "Ability level must be positive.");
        IReadOnlyList<IHandler> handlers = create(actualLevel, policy ?? FormulaEvaluationPolicy.Strict)
            ?? throw new InvalidOperationException($"Ability '{Content.Id}' created no handler list.");
        if (handlers.Count == 0 || handlers.Any(handler => handler == null))
            throw new InvalidOperationException($"Ability '{Content.Id}' must create non-null handlers.");
        foreach (AbilityBase ability in handlers.OfType<AbilityBase>())
        {
            ability.Level = actualLevel;
            ability.AllowMultiple = Content.AllowMultiple;
        }
        return handlers;
    }
}
