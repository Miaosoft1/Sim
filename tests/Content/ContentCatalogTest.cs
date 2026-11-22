using Microsoft.Extensions.DependencyInjection;
using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.Content.Registration;
using Miaosoft.Sim.Content.Core;
using Miaosoft.Sim.Content.Loading;
using Miaosoft.Sim.Content.Loading.Registration;

namespace Miaosoft.Sim.Content.Tests;

[TestClass]
public sealed class ContentCatalogTest
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void DiLoaderCompilesAllDocumentsBeforeResolvingMapReferences()
    {
        using ServiceProvider provider = CreateProvider(withMaps: true);
        IContentCatalogLoader loader = provider.GetRequiredService<IContentCatalogLoader>();
        ContentCatalog catalog = loader.Load([
            Doc("outside.yaml", """
                map:
                  - id: outside
                    name: Outside
                    width: 100
                    height: 100
                    isMainPlane: true
                    dungeons:
                      - id: entrance
                        name: Entrance
                        x: 10
                        y: 10
                        targetMapId: inside
                """),
            Doc("inside.yaml", """
                map:
                  - id: inside
                    name: Inside
                    width: 40
                    height: 30
                    isMainPlane: false
                """)
        ], ["map"]);
        IReadOnlyDictionary<string, MapDefinition> maps =
            catalog.GetSection<IReadOnlyDictionary<string, MapDefinition>>("map");
        Assert.HasCount(2, maps);
        Assert.AreEqual("inside", maps["outside"].Dungeons.Single().TargetMapId);
        Assert.AreEqual(40d, maps["inside"].Width);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IDictionary<string, MapDefinition>)maps).Remove("inside"));
        Assert.IsTrue(maps["outside"].IsMainPlane);
        Assert.IsFalse(maps["inside"].IsMainPlane);
        Assert.HasCount(1, provider.GetServices<IContentModule>());
        TestContext.WriteLine("DI selected one map module; a forward reference resolved after both documents were read.");
    }

    [TestMethod]
    public void CatalogsAreIsolatedAndAnUnregisteredSectionIsRejected()
    {
        using ServiceProvider provider = CreateProvider(withMaps: true);
        IContentCatalogLoader loader = provider.GetRequiredService<IContentCatalogLoader>();
        ContentCatalog first = loader.Load([Doc("first.yaml", Map("arena", 10))], ["map"]);
        ContentCatalog second = loader.Load([Doc("second.yaml", Map("arena", 20))], ["map"]);
        MapDefinition firstMap = first.GetSection<IReadOnlyDictionary<string, MapDefinition>>("map")["arena"];
        MapDefinition secondMap = second.GetSection<IReadOnlyDictionary<string, MapDefinition>>("map")["arena"];
        Assert.AreEqual(10d, firstMap.Width);
        Assert.AreEqual(20d, secondMap.Width);
        Assert.AreNotSame(firstMap, secondMap);

        var error = Assert.ThrowsExactly<InvalidDataException>(() =>
            loader.Load([Doc("unregistered.yaml", Map("arena", 10))], []));
        StringAssert.Contains(error.Message, "unregistered.yaml");
        using ServiceProvider noMaps = CreateProvider(withMaps: false);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            noMaps.GetRequiredService<IContentCatalogLoader>().Load([], ["map"]));
        Assert.ThrowsExactly<InvalidOperationException>(() => loader.Load([], ["map", "map"]));
        TestContext.WriteLine("Catalogs kept separate definitions; an unloaded module's section was rejected.");
    }

    [TestMethod]
    public void InvalidDataHasSourceDiagnosticsAndDoesNotContaminateTheNextLoad()
    {
        using ServiceProvider provider = CreateProvider(withMaps: true);
        IContentCatalogLoader loader = provider.GetRequiredService<IContentCatalogLoader>();

        var duplicate = Assert.ThrowsExactly<InvalidDataException>(() => loader.Load([
            Doc("first.yaml", Map("arena", 10)),
            Doc("duplicate.yaml", Map("arena", 20))
        ], ["map"]));
        StringAssert.Contains(duplicate.Message, "duplicate.yaml");
        StringAssert.Contains(duplicate.Message, "duplicate map");

        var missing = Assert.ThrowsExactly<InvalidDataException>(() => loader.Load([
            Doc("missing.yaml", """
                map:
                  - id: outside
                    name: Outside
                    width: 100
                    height: 100
                    isMainPlane: true
                    dungeons:
                      - id: entrance
                        name: Entrance
                        x: 10
                        y: 10
                        targetMapId: absent
                """)
        ], ["map"]));
        StringAssert.Contains(missing.Message, "missing.yaml");
        StringAssert.Contains(missing.Message, "absent");

        var invalid = Assert.ThrowsExactly<InvalidDataException>(() =>
            loader.Load([Doc("invalid.yaml", Map("arena", -1))], ["map"]));
        StringAssert.Contains(invalid.Message, "invalid.yaml");
        StringAssert.Contains(invalid.Message, "arena");
        Assert.ThrowsExactly<InvalidDataException>(() =>
            loader.Load([Doc("null.yaml", "map: null")], ["map"]));

        var duplicateKey = Assert.ThrowsExactly<InvalidDataException>(() =>
            loader.Load([Doc("duplicate-key.yaml", "map: []\nmap: []")], ["map"]));
        StringAssert.Contains(duplicateKey.Message, "duplicate-key.yaml");
        var unknownField = Assert.ThrowsExactly<InvalidDataException>(() =>
            loader.Load([Doc("unknown-field.yaml", Map("arena", 10) + "\n    unknownField: 1")], ["map"]));
        StringAssert.Contains(unknownField.Message, "unknown-field.yaml");
        Assert.ThrowsExactly<InvalidDataException>(() =>
            loader.Load([Doc("multi-document.yaml", "map: []\n---\nmap: []")], ["map"]));

        ContentCatalog recovered = loader.Load([Doc("valid.yaml", Map("arena", 30))], ["map"]);
        Assert.AreEqual(30d, recovered.GetSection<IReadOnlyDictionary<string, MapDefinition>>("map")["arena"].Width);
        TestContext.WriteLine("Invalid IDs, references, geometry and YAML failed with no cross-load state.");
    }

    [TestMethod]
    public void CrossModuleReferencesAreValidatedAfterAllCatalogsCompile()
    {
        var services = new ServiceCollection();
        services.AddContentLoading();
        services.AddSingleton<IContentModule, ReferenceModule>();
        services.AddMapContent();
        using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);
        IContentCatalogLoader loader = provider.GetRequiredService<IContentCatalogLoader>();

        ContentCatalog catalog = loader.Load([
            Doc("link.yaml", "link: { mapId: arena }"),
            Doc("map.yaml", Map("arena", 10))
        ], ["reference-test", "map"]);
        Assert.AreEqual("arena", catalog.GetSection<ReferenceContent>("link").MapId);
        var missing = Assert.ThrowsExactly<InvalidDataException>(() =>
            loader.Load([Doc("broken-link.yaml", "link: { mapId: absent }")], ["reference-test", "map"]));
        StringAssert.Contains(missing.Message, "broken-link.yaml");
        StringAssert.Contains(missing.Message, "absent");

        var incomplete = new ServiceCollection();
        incomplete.AddContentLoading();
        incomplete.AddSingleton<IContentModule, ReferenceModule>();
        using ServiceProvider noMaps = incomplete.BuildServiceProvider(validateScopes: true);
        var unresolved = Assert.ThrowsExactly<InvalidDataException>(() =>
            noMaps.GetRequiredService<IContentCatalogLoader>().Load([
                Doc("unknown-kind.yaml", "link: { mapId: arena }")
            ], ["reference-test"]));
        StringAssert.Contains(unresolved.Message, "unknown-kind.yaml");
        StringAssert.Contains(unresolved.Message, "resolver");
        TestContext.WriteLine("Cross-module references resolved after compilation; missing IDs and unloaded resolvers failed.");
    }

    [TestMethod]
    public void CatalogCompilationHonorsDependenciesRegardlessOfModuleSelectionOrder()
    {
        var services = new ServiceCollection();
        services.AddContentLoading();
        services.AddSingleton<IContentModule, DependentModule>();
        services.AddMapContent();
        using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);
        IContentCatalogLoader loader = provider.GetRequiredService<IContentCatalogLoader>();

        ContentCatalog catalog = loader.Load([Doc("map.yaml", Map("arena", 10))], ["dependent-test", "map"]);
        Assert.AreEqual(10d, catalog.GetSection<DependentContent>("dependent").Width);
        var missing = Assert.ThrowsExactly<InvalidOperationException>(() =>
            loader.Load([], ["dependent-test"]));
        StringAssert.Contains(missing.Message, "map");

        var cyclic = new ServiceCollection();
        cyclic.AddContentLoading();
        cyclic.AddSingleton<IContentModule, CyclicModule>();
        using ServiceProvider cycles = cyclic.BuildServiceProvider(validateScopes: true);
        var cycle = Assert.ThrowsExactly<InvalidOperationException>(() =>
            cycles.GetRequiredService<IContentCatalogLoader>().Load([], ["cyclic-test"]));
        StringAssert.Contains(cycle.Message, "cyclic");
        TestContext.WriteLine("Catalog dependencies compiled first regardless of selection order; unloaded and cyclic dependencies failed.");
    }

    [TestMethod]
    public void SchemaVersionIsValidatedBeforeContentModuleReadersRun()
    {
        var probe = new ReaderProbeModule();
        var services = new ServiceCollection();
        services.AddContentLoading();
        services.AddSingleton<IContentModule>(probe);
        using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);
        IContentCatalogLoader loader = provider.GetRequiredService<IContentCatalogLoader>();

        var missing = Assert.ThrowsExactly<InvalidDataException>(() =>
            loader.Load([new("legacy.yaml", "probe: {}")], ["reader-probe"]));
        StringAssert.Contains(missing.Message, "legacy.yaml");
        StringAssert.Contains(missing.Message, "schemaVersion");
        var unsupported = Assert.ThrowsExactly<InvalidDataException>(() =>
            loader.Load([new("future.yaml", "schemaVersion: 2\nprobe: {}")], ["reader-probe"]));
        StringAssert.Contains(unsupported.Message, "future.yaml");
        StringAssert.Contains(unsupported.Message, "2");
        Assert.IsFalse(probe.WasRead);

        ContentCatalog catalog = loader.Load([Doc("current.yaml", "probe: {}")], ["reader-probe"]);
        Assert.IsNotNull(catalog.GetSection<ReaderProbeContent>("probe"));
        Assert.IsTrue(probe.WasRead);
        TestContext.WriteLine("Missing and unsupported schema versions failed before the selected module read any data.");
    }

    private static ServiceProvider CreateProvider(bool withMaps)
    {
        var services = new ServiceCollection();
        services.AddContentLoading();
        if (withMaps) services.AddMapContent().AddMapContent();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static string Map(string id, int width) => $$"""
        map:
          - id: {{id}}
            name: Arena
            width: {{width}}
            height: 20
            isMainPlane: true
        """;

    private static ContentDocument Doc(string source, string yaml) =>
        new(source, "schemaVersion: 1\n" + yaml);

    public sealed class ReferenceModule : IContentModule
    {
        public string Id => "reference-test";

        public void Register(ContentSectionRegistry registry) =>
            registry.Register("link", (yaml, context) =>
            {
                ReferenceDto reference = context.Deserialize<ReferenceDto>(yaml);
                context.RequireReference("map", reference.MapId);
                return new ReferenceContent(reference.MapId);
            });
    }

    private sealed record ReferenceContent(string MapId);

    public sealed class DependentModule : IContentModule
    {
        public string Id => "dependent-test";

        public void Register(ContentSectionRegistry registry) =>
            registry.RegisterCollection("dependent", (_, _) => { },
                () => new DependentContent(
                    registry.Get<IReadOnlyDictionary<string, MapDefinition>>("map")["arena"].Width), "map");
    }

    private sealed record DependentContent(double Width);

    public sealed class CyclicModule : IContentModule
    {
        public string Id => "cyclic-test";

        public void Register(ContentSectionRegistry registry)
        {
            registry.RegisterCollection("first", (_, _) => { }, () => new object(), "second");
            registry.RegisterCollection("second", (_, _) => { }, () => new object(), "first");
        }
    }

    public sealed class ReferenceDto
    {
        public string MapId { get; set; } = "";
    }

    private sealed class ReaderProbeModule : IContentModule
    {
        public string Id => "reader-probe";
        public bool WasRead { get; private set; }

        public void Register(ContentSectionRegistry registry) =>
            registry.Register("probe", (_, _) =>
            {
                WasRead = true;
                return new ReaderProbeContent();
            });
    }

    private sealed class ReaderProbeContent;
}
