namespace Miaosoft.Sim.Combat.Skill.Content.Parser;

internal static class PropertyRecalculationCompiler
{
    internal static PropertyRecalculationPolicy Compile(IReadOnlyList<string>? names)
    {
        if (names == null) return PropertyRecalculationPolicy.ProjectionChanged;
        PropertyRecalculationPolicy result = PropertyRecalculationPolicy.None;
        foreach (string name in names)
        {
            if (!Enum.TryParse(name, true, out PropertyRecalculationPolicy value) ||
                !Enum.IsDefined(value) || value == PropertyRecalculationPolicy.None ||
                !string.Equals(name, value.ToString(), StringComparison.OrdinalIgnoreCase) ||
                (result & value) != 0)
                throw new InvalidDataException($"Invalid or duplicate recalculateOn value '{name}'.");
            result |= value;
        }
        if (result.HasFlag(PropertyRecalculationPolicy.ProjectionChanged) &&
            result != PropertyRecalculationPolicy.ProjectionChanged)
            throw new InvalidDataException("ProjectionChanged cannot be combined with narrower recalculateOn values.");
        return result;
    }
}
