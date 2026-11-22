using System.Diagnostics.CodeAnalysis;
using Miaosoft.Sim.Content.Core;

namespace Miaosoft.Sim.Content.Loading;

public sealed class ContentCatalog
{
    private readonly ContentSectionRegistry registry;

    internal ContentCatalog(ContentSectionRegistry registry) => this.registry = registry;

    public T GetSection<T>(string name) where T : class => registry.Get<T>(name);

    public bool TryGetSection<T>(string name, [NotNullWhen(true)] out T? section) where T : class =>
        registry.TryGet(name, out section);

    public T? GetCatalog<T>() where T : class => registry.GetCatalog<T>();
}
