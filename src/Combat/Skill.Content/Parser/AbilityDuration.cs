namespace Miaosoft.Sim.Combat.Skill.Content.Parser;

internal static class AbilityDuration
{
    public static TimeSpan? ParseSeconds(double? seconds, string field, bool allowZero)
    {
        if (!seconds.HasValue) return null;
        if (!double.IsFinite(seconds.Value) || seconds.Value < 0 ||
            seconds.Value >= TimeSpan.MaxValue.TotalSeconds)
            throw new InvalidDataException($"{field} must be a finite non-negative duration.");
        TimeSpan value = TimeSpan.FromSeconds(seconds.Value);
        if (!allowZero && value <= TimeSpan.Zero)
            throw new InvalidDataException($"{field} must be a positive duration.");
        return value;
    }
}
