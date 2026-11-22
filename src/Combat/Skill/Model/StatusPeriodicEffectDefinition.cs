using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.EventData;

namespace Miaosoft.Sim.Combat.Skill;

public sealed class StatusPeriodicEffectDefinition
{
    private readonly DamageDefinition[]? damage;
    private readonly int healAmount;
    private readonly bool restoreToMaximum;

    private StatusPeriodicEffectDefinition(
        string id, int tickCount, TimeSpan tickInterval, TimeSpan firstTickDelay,
        DamageDefinition[]? damage, int healAmount, bool restoreToMaximum)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (tickCount <= 0) throw new ArgumentOutOfRangeException(nameof(tickCount));
        if (tickInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(tickInterval));
        if (firstTickDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(firstTickDelay));
        Id = id;
        TickCount = tickCount;
        TickInterval = tickInterval;
        FirstTickDelay = firstTickDelay;
        this.damage = damage;
        this.healAmount = healAmount;
        this.restoreToMaximum = restoreToMaximum;
    }

    public string Id { get; }
    public int TickCount { get; }
    public TimeSpan TickInterval { get; }
    public TimeSpan FirstTickDelay { get; }

    public static StatusPeriodicEffectDefinition ForDamage(
        string id, int tickCount, TimeSpan tickInterval, TimeSpan firstTickDelay,
        IEnumerable<DamageDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        DamageDefinition[] compiled = definitions.ToArray();
        if (compiled.Length == 0 || compiled.Any(definition => definition == null))
            throw new ArgumentException("Periodic damage requires damage definitions.", nameof(definitions));
        if (compiled.Any(definition => !ReferenceEquals(definition.DamageTypes, compiled[0].DamageTypes)))
            throw new ArgumentException("Periodic damage cannot combine damage catalogs.", nameof(definitions));
        return new(id, tickCount, tickInterval, firstTickDelay, compiled, 0, false);
    }

    public static StatusPeriodicEffectDefinition ForHeal(
        string id, int tickCount, TimeSpan tickInterval, TimeSpan firstTickDelay,
        int amount, bool restoreToMaximum = false)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        return new(id, tickCount, tickInterval, firstTickDelay, null, amount, restoreToMaximum);
    }

    internal PeriodicEffectApplication Create(StatusAbility status, AbilityRuntimeSnapshot? ability) =>
        damage != null
            ? new PeriodicDamageApplication
            {
                Damage = DamageProfile.ForEffect(Id, status.Level, damage, status.FormulaPolicy, ability),
                TickCount = TickCount, TickInterval = TickInterval, FirstTickDelay = FirstTickDelay,
                StackOwner = status
            }
            : new PeriodicHealApplication
            {
                Heal = HealProfile.ForEffect(Id, healAmount, restoreToMaximum, ability),
                TickCount = TickCount, TickInterval = TickInterval, FirstTickDelay = FirstTickDelay,
                StackOwner = status
            };
}
