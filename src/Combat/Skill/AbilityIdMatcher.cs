namespace Miaosoft.Sim.Combat.Skill;

internal static class AbilityIdMatcher
{
    public static bool IsSameOrChild(string abilityId, string handlerId)
        => string.Equals(abilityId, handlerId, StringComparison.Ordinal)
            || handlerId.StartsWith(abilityId + ".", StringComparison.Ordinal);
}
