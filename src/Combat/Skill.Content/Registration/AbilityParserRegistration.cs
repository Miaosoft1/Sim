using Miaosoft.Sim.Combat.Skill.Content.Parser;
using Miaosoft.Sim.Content.Core;

namespace Miaosoft.Sim.Combat.Skill.Content.Registration;

public sealed class AbilityParserRegistration
{
    private readonly Dictionary<string, IHandlerParser> parsers = new(StringComparer.Ordinal);
    private bool sealedRegistration;

    public void Register(IHandlerParser parser)
    {
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentException.ThrowIfNullOrWhiteSpace(parser.Type);
        if (sealedRegistration) throw new InvalidOperationException("Ability parser registration is sealed.");
        if (!parsers.TryAdd(parser.Type, parser))
            throw new InvalidOperationException($"Ability parser '{parser.Type}' is already registered.");
    }

    internal void Seal() => sealedRegistration = true;

    internal IHandlerParser Require(string type) =>
        parsers.TryGetValue(type, out IHandlerParser? parser) ? parser :
        throw new InvalidDataException($"No selected content module provides ability type '{type}'.");
}

public static class AbilityParserRegistrationExtensions
{
    public static AbilityParserRegistration AbilityParsers(this ContentSectionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.GetOrAddRegistration(
            static () => new AbilityParserRegistration(), static registration => registration.Seal());
    }
}
