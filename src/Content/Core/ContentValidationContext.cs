namespace Miaosoft.Sim.Content.Core;

public sealed class ContentValidationContext(IReadOnlyDictionary<object, string> sources)
{
    public string Source(object owner) => sources.TryGetValue(owner, out string? file) ? file : "<content>";

    public void RequireReference(object owner, string field, string? id, IReadOnlySet<string> ids)
    {
        if (string.IsNullOrWhiteSpace(id) || !ids.Contains(id))
            throw new InvalidDataException($"{Source(owner)}: {field} references missing ID '{id}'.");
    }
}
