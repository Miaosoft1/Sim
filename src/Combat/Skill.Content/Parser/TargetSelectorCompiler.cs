using Miaosoft.Sim.Combat.Skill.Content.Dto;
using Miaosoft.Sim.Combat.Skill.Content.Model;
using Miaosoft.Sim.Combat.Skill.Targeting;

namespace Miaosoft.Sim.Combat.Skill.Content.Parser;

internal static class TargetSelectorCompiler
{
    public static TargetSelectorDefinition? Compile(AbilitySelectorDto? data)
    {
        if (data == null) return null;
        string type = data.Type?.Trim().ToLowerInvariant() ?? "tracking";
        TargetingOrigin origin = Parse(data.Origin, TargetingOrigin.Target, "selector origin");
        TargetFilter filter = Parse(data.Filter, TargetFilter.All, "selector filter");
        foreach (double value in new[] { data.Range, data.Width, data.Radius, data.CastRange }.OfType<double>())
            if (!double.IsFinite(value) || value < 0)
                throw new InvalidDataException("Selector distances must be finite and non-negative.");
        if (data.MaxTargets is <= 0)
            throw new InvalidDataException("A selector's maximum target count must be positive.");
        if (data.Group != null && string.IsNullOrWhiteSpace(data.Group))
            throw new InvalidDataException("A selector group cannot be empty.");

        switch (type)
        {
            case "tracking":
                if (origin == TargetingOrigin.HitPoint || data.Width.HasValue || data.Radius.HasValue ||
                    data.CastRange.HasValue || data.StopAtFirstTarget.HasValue || data.MaxTargets.HasValue)
                    throw new InvalidDataException("Tracking selector contains options that do not apply to it.");
                break;
            case "circle":
                if (origin is TargetingOrigin.EventSource or TargetingOrigin.EventTarget ||
                    !data.Radius.HasValue || data.Group != null || data.Range.HasValue || data.Width.HasValue ||
                    data.StopAtFirstTarget.HasValue || data.MaxTargets.HasValue)
                    throw new InvalidDataException("Circle selector requires a radius and only circle options.");
                break;
            case "line":
                if (!data.Range.HasValue || !data.Width.HasValue ||
                    origin is TargetingOrigin.Source or TargetingOrigin.EventSource or TargetingOrigin.EventTarget ||
                    data.Group != null || data.Radius.HasValue || data.CastRange.HasValue)
                    throw new InvalidDataException("Line selector requires range and width and only line options.");
                break;
            default:
                throw new InvalidDataException($"Unsupported selector type '{data.Type}'.");
        }

        return new(type, origin, filter, data.Group?.Trim(), data.Range, data.Width, data.Radius,
            data.CastRange, data.StopAtFirstTarget ?? false, data.MaxTargets);
    }

    private static T Parse<T>(string? text, T defaultValue, string description) where T : struct, Enum
    {
        if (text == null) return defaultValue;
        if (!Enum.TryParse(text, true, out T value) || !Enum.IsDefined(value) ||
            !string.Equals(text, value.ToString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Invalid {description} '{text}'.");
        return value;
    }
}
