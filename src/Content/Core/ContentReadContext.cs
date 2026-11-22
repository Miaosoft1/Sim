using YamlDotNet.Serialization;

namespace Miaosoft.Sim.Content.Core;

public sealed class ContentReadContext
{
    private readonly List<ContentReference> references;
    private readonly IDeserializer deserializer;

    internal ContentReadContext(string sourceFile, List<ContentReference> references, IDeserializer deserializer)
    {
        SourceFile = sourceFile;
        this.references = references;
        this.deserializer = deserializer;
    }

    public string SourceFile { get; }
    public T Deserialize<T>(string yaml) => deserializer.Deserialize<T>(yaml);

    public void RequireReference(string kind, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        references.Add(new(kind, id, SourceFile));
    }
}

internal sealed record ContentReference(string Kind, string Id, string SourceFile);
