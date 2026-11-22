using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using System.Diagnostics.CodeAnalysis;

namespace Miaosoft.Sim.Content.Core;

public sealed class ContentSectionRegistry
{
    private readonly HashSet<string> reservedNames;
    private readonly Dictionary<string, Action<string, ContentReadContext>> readers = new(StringComparer.Ordinal);
    private sealed class Compiler(string? section, IReadOnlyList<string> dependencies, Action compile)
    {
        public string? Section { get; } = section;
        public IReadOnlyList<string> Dependencies { get; } = dependencies;
        public Action Compile { get; } = compile;
    }

    private readonly List<Compiler> compilers = [];
    private readonly Dictionary<string, Compiler> sectionCompilers = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, object> catalogs = [];
    private readonly HashSet<Type> catalogTypes = [];
    private readonly Dictionary<string, object> sections = new(StringComparer.Ordinal);
    private readonly List<ContentReference> references = [];
    private readonly Dictionary<string, Func<string, bool>> referenceResolvers = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, (Type Schema, Action<DeserializerBuilder> Configure)> schemas = [];
    private readonly Dictionary<Type, object> registrations = [];
    private readonly List<Action> registrationSealers = [];
    private bool sealedRegistrations;
    private bool completed;

    public ContentSectionRegistry(IEnumerable<string>? reservedNames = null, bool strict = false)
    {
        this.reservedNames = new(reservedNames ?? [], StringComparer.Ordinal);
        IsStrict = strict;
    }

    public bool IsStrict { get; }

    public void RegisterCatalog<T>(Func<T> compile) where T : class
    {
        ArgumentNullException.ThrowIfNull(compile);
        if (sealedRegistrations) throw new InvalidOperationException("Content section registrations are sealed.");
        if (!catalogTypes.Add(typeof(T)))
            throw new InvalidOperationException($"Content catalog '{typeof(T).Name}' is already registered.");
        compilers.Add(new(null, [], () => catalogs.Add(typeof(T), compile() ??
            throw new InvalidDataException($"Content catalog '{typeof(T).Name}' compiled an empty result."))));
    }

    public T? GetCatalog<T>() where T : class =>
        catalogs.TryGetValue(typeof(T), out object? catalog) ? (T)catalog : null;

    public void Register<T>(string name, Func<string, ContentReadContext, T> read) where T : class
    {
        ArgumentNullException.ThrowIfNull(read);
        RegisterReader(name, (yaml, context) =>
        {
            if (sections.ContainsKey(name))
                throw new InvalidDataException($"{context.SourceFile}: content section '{name}' must have a single owner.");
            sections.Add(name, read(yaml, context) ??
                throw new InvalidDataException($"{context.SourceFile}: section '{name}' is empty."));
        });
    }

    public void RegisterCollection<T>(
        string name, Action<string, ContentReadContext> read, Func<T> compile, params string[] dependencies)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(compile);
        ArgumentNullException.ThrowIfNull(dependencies);
        foreach (string dependency in dependencies)
            ArgumentException.ThrowIfNullOrWhiteSpace(dependency);
        RegisterReader(name, read);
        var compiler = new Compiler(name, dependencies.ToArray(), () => sections.Add(name, compile() ??
            throw new InvalidDataException($"Content section '{name}' compiled an empty catalog.")));
        compilers.Add(compiler);
        sectionCompilers.Add(name, compiler);
    }

    private void RegisterReader(string name, Action<string, ContentReadContext> read)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (sealedRegistrations) throw new InvalidOperationException("Content section registrations are sealed.");
        if (reservedNames.Contains(name)) throw new InvalidOperationException($"Content section '{name}' is already owned by the root schema.");
        if (!readers.TryAdd(name, read))
            throw new InvalidOperationException($"Content section '{name}' is already registered.");
    }

    public T GetOrAddRegistration<T>(Func<T> create, Action<T> seal) where T : class
    {
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(seal);
        if (sealedRegistrations) throw new InvalidOperationException("Content section registrations are sealed.");
        if (registrations.TryGetValue(typeof(T), out object? existing)) return (T)existing;
        T registration = create() ?? throw new InvalidOperationException("A content registration cannot be null.");
        registrations.Add(typeof(T), registration);
        registrationSealers.Add(() => seal(registration));
        return registration;
    }

    public void Seal()
    {
        if (sealedRegistrations) throw new InvalidOperationException("Content section registrations are already sealed.");
        sealedRegistrations = true;
        foreach (Action seal in registrationSealers) seal();
        registrationSealers.Clear();
        registrations.Clear();
    }
    public void Complete()
    {
        if (!sealedRegistrations || completed)
            throw new InvalidOperationException("Content must be sealed and completed exactly once.");
        var visiting = new HashSet<Compiler>();
        var compiled = new HashSet<Compiler>();
        void Compile(Compiler compiler)
        {
            if (compiled.Contains(compiler)) return;
            if (!visiting.Add(compiler))
                throw new InvalidOperationException($"Content section '{compiler.Section}' has a cyclic compilation dependency.");
            foreach (string dependency in compiler.Dependencies)
            {
                if (sectionCompilers.TryGetValue(dependency, out Compiler? prerequisite))
                    Compile(prerequisite);
                else if (!sections.ContainsKey(dependency))
                    throw new InvalidOperationException(
                        $"Content section '{compiler.Section}' requires unloaded section '{dependency}'.");
            }
            compiler.Compile();
            visiting.Remove(compiler);
            compiled.Add(compiler);
        }
        foreach (Compiler compiler in compilers) Compile(compiler);
        compilers.Clear();
        sectionCompilers.Clear();
        readers.Clear();
        completed = true;
    }

    public void RegisterSchema<TBase, TSchema>() where TSchema : TBase
    {
        if (sealedRegistrations) throw new InvalidOperationException("Content section registrations are sealed.");
        if (!schemas.TryAdd(typeof(TBase), (typeof(TSchema), builder => builder.WithTypeMapping<TBase, TSchema>())))
            throw new InvalidOperationException($"Content schema '{typeof(TBase).Name}' is already registered.");
    }

    public Type GetSchemaType<TBase>() =>
        schemas.TryGetValue(typeof(TBase), out var schema) ? schema.Schema : typeof(TBase);

    public void RegisterReferenceResolver(string kind, Func<string, bool> contains)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(contains);
        if (sealedRegistrations) throw new InvalidOperationException("Content section registrations are sealed.");
        if (!referenceResolvers.TryAdd(kind, contains))
            throw new InvalidOperationException($"Content reference resolver '{kind}' is already registered.");
    }

    public IDeserializer CreateDeserializer(DeserializerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!sealedRegistrations) throw new InvalidOperationException("Seal content registrations before reading.");
        foreach (var schema in schemas.Values) schema.Configure(builder);
        return builder.Build();
    }

    public T Get<T>(string name) where T : class =>
        sections.TryGetValue(name, out object? section) && section is T typed
            ? typed : throw new InvalidOperationException($"Content section '{name}' of type '{typeof(T).Name}' was not loaded.");

    public bool TryGet<T>(string name, [NotNullWhen(true)] out T? section) where T : class
    {
        if (!sections.ContainsKey(name))
        {
            section = null;
            return false;
        }
        section = Get<T>(name);
        return true;
    }

    public T? ReadDocument<T>(
        string yaml, string sourceFile, IDeserializer deserializer, Action<T?>? validateRoot = null)
    {
        if (!sealedRegistrations) throw new InvalidOperationException("Seal content registrations before reading.");
        if (completed) throw new InvalidOperationException("The content catalog is complete.");
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidDataException($"{sourceFile}: content must contain exactly one mapping document.");
        var pending = new List<(Action<string, ContentReadContext> Read, YamlNode Node)>();
        foreach (var entry in root.Children.ToArray())
        {
            if (entry.Key is not YamlScalarNode { Value: { } key } || !readers.TryGetValue(key, out var read))
                continue;
            pending.Add((read, entry.Value));
            root.Children.Remove(entry.Key);
        }
        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        T? result = deserializer.Deserialize<T>(writer.ToString());
        validateRoot?.Invoke(result);
        foreach ((Action<string, ContentReadContext> read, YamlNode node) in pending)
        {
            var sectionStream = new YamlStream(new YamlDocument(node));
            using var sectionWriter = new StringWriter();
            sectionStream.Save(sectionWriter, assignAnchors: false);
            read(sectionWriter.ToString(), new ContentReadContext(sourceFile, references, deserializer));
        }
        return result;
    }

    public void ValidateReferences()
    {
        if (!completed) throw new InvalidOperationException("Content must be compiled before references are validated.");
        foreach (ContentReference reference in references)
        {
            if (!referenceResolvers.TryGetValue(reference.Kind, out Func<string, bool>? contains))
                throw new InvalidDataException(
                    $"{reference.SourceFile}: no content reference resolver is registered for '{reference.Kind}'.");
            if (!contains(reference.Id))
                throw new InvalidDataException(
                    $"{reference.SourceFile}: missing {reference.Kind} reference '{reference.Id}'.");
        }
    }
}
