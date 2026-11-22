using Miaosoft.Sim.Combat.Skill.Ability;

namespace Miaosoft.Sim.Combat.Skill;

public sealed class DamageProfile
{
    private readonly DamageDefinition[] definitions;
    private readonly FormulaEvaluationPolicy policy;

    private DamageProfile(
        string id, AbilityRuntimeSnapshot? ability, string? name, int level,
        IEnumerable<DamageDefinition> definitions, FormulaEvaluationPolicy policy, int stack = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(policy);
        Id = id;
        Ability = ability;
        Name = name;
        Level = level;
        Stack = stack > 0 ? stack : throw new ArgumentOutOfRangeException(nameof(stack));
        this.definitions = definitions.OrderBy(definition => definition.Priority).ToArray();
        if (this.definitions.Length == 0)
            throw new InvalidOperationException("A damage profile requires at least one damage definition.");
        DamageTypes = this.definitions[0].DamageTypes;
        if (this.definitions.Any(definition => !ReferenceEquals(definition.DamageTypes, DamageTypes)))
            throw new InvalidOperationException("A damage profile cannot combine different damage type catalogs.");
        this.policy = policy;
    }

    public AbilityRuntimeSnapshot? Ability { get; }
    public string Id { get; }
    public string? Name { get; }
    public int Level { get; }
    public int Stack { get; }
    public DamageTypeRegistry DamageTypes { get; }

    public static DamageProfile From(DamageAbility ability)
    {
        ArgumentNullException.ThrowIfNull(ability);
        return new DamageProfile(ability.Id, AbilityRuntimeSnapshot.Capture(ability), ability.Name, ability.Level,
            ability.Damages ?? [], ability.FormulaPolicy);
    }

    public static DamageProfile ForEffect(
        string id, int level, IEnumerable<DamageDefinition> definitions, FormulaEvaluationPolicy policy,
        AbilityRuntimeSnapshot? ability = null)
    {
        if (level < 1)
            throw new ArgumentOutOfRangeException(nameof(level), "Effect level must be positive.");
        return new DamageProfile(id, ability, null, level, definitions, policy);
    }

    public Damage Calculate(Unit? source, Unit target, TimeSpan timeSpan)
    {
        ArgumentNullException.ThrowIfNull(target);
        var damage = new Damage { Handler = this, Source = source, Target = target, TimeSpan = timeSpan };
        foreach (DamageDefinition definition in definitions)
        {
            foreach (DamageResult result in definition.Calculate(damage, policy))
            {
                int previous = damage.TryGetValue(result.DamageType, out int existed) ? existed : 0;
                damage[result.DamageType] = checked(previous + result.ScaledValue);
                damage.UsedFormulaFallback |= result.UsedFallback;
            }
        }
        if (Stack != 1)
            foreach (string type in damage.StoredKeys.ToArray())
                damage[type] = checked(damage.GetValueOrDefault(type) * Stack);
        return damage;
    }

    internal DamageProfile WithStack(int stack) =>
        stack == Stack ? this : new DamageProfile(Id, Ability, Name, Level, definitions, policy, stack);
}
