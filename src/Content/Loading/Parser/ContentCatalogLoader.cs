using Miaosoft.Sim.Content.Core;
using Miaosoft.Sim.Content.Loading.Dto;
using Miaosoft.Sim.Serialization.Yaml;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Miaosoft.Sim.Content.Loading.Parser;

internal sealed class ContentCatalogLoader : IContentCatalogLoader
{
    private const int SupportedSchemaVersion = 1;
    private readonly Dictionary<string, IContentModule> modules;

    public ContentCatalogLoader(IEnumerable<IContentModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        this.modules = new(StringComparer.Ordinal);
        foreach (IContentModule module in modules)
        {
            ArgumentNullException.ThrowIfNull(module);
            ArgumentException.ThrowIfNullOrWhiteSpace(module.Id);
            if (!this.modules.TryAdd(module.Id, module))
                throw new InvalidOperationException($"Content module '{module.Id}' is already registered.");
        }
    }

    public ContentCatalog Load(IEnumerable<ContentDocument> documents, IEnumerable<string> moduleIds)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(moduleIds);
        var selected = new List<IContentModule>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in moduleIds)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            if (!names.Add(id))
                throw new InvalidOperationException($"Content module '{id}' was selected more than once.");
            if (!modules.TryGetValue(id, out IContentModule? module))
                throw new InvalidOperationException($"Content module '{id}' is not registered.");
            selected.Add(module);
        }
        var registry = new ContentSectionRegistry(["schemaVersion"], strict: true);
        foreach (IContentModule module in selected)
            module.Register(registry);
        registry.Seal();
        var builder = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithTypeConverter(new ReadOnlyListYamlTypeConverter())
            .WithDuplicateKeyChecking();
        IDeserializer deserializer = registry.CreateDeserializer(builder);
        foreach (ContentDocument document in documents)
        {
            ArgumentNullException.ThrowIfNull(document);
            ArgumentException.ThrowIfNullOrWhiteSpace(document.Source);
            ArgumentNullException.ThrowIfNull(document.Yaml);
            try
            {
                _ = registry.ReadDocument<ContentRootDto>(document.Yaml, document.Source, deserializer, root =>
                {
                    if (root?.SchemaVersion != SupportedSchemaVersion)
                        throw new InvalidDataException(
                            $"{document.Source}: schemaVersion must be {SupportedSchemaVersion}; " +
                            $"received {root?.SchemaVersion?.ToString() ?? "none"}.");
                });
            }
            catch (YamlException exception)
            {
                throw new InvalidDataException($"{document.Source}: {exception.Message}", exception);
            }
        }
        registry.Complete();
        registry.ValidateReferences();
        return new ContentCatalog(registry);
    }
}
