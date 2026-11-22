using Miaosoft.Sim.Combat.Skill.Ability;

namespace Miaosoft.Sim.Combat.Skill;

public sealed class HealProfile
{
    private HealProfile(string id, AbilityRuntimeSnapshot? ability, int amount, bool restoreToMaximum)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (amount < 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "Healing cannot be negative.");
        Id = id;
        Ability = ability;
        Amount = amount;
        RestoreToMaximum = restoreToMaximum;
    }

    public AbilityRuntimeSnapshot? Ability { get; }
    public string Id { get; }
    public int Amount { get; }
    public bool RestoreToMaximum { get; }

    public static HealProfile From(HealAbility ability)
    {
        ArgumentNullException.ThrowIfNull(ability);
        return new HealProfile(ability.Id, AbilityRuntimeSnapshot.Capture(ability),
            ability.BaseHeal, ability.RestoreToMaximum);
    }

    public static HealProfile ForEffect(
        string id, int amount, bool restoreToMaximum = false, AbilityRuntimeSnapshot? ability = null) =>
        new(id, ability, amount, restoreToMaximum);
}
