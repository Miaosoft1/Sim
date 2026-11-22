using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Miaosoft.Sim.Combat.Map.Content.Parser;
using Miaosoft.Sim.Content.Core;

namespace Miaosoft.Sim.Combat.Map.Content.Registration;

public static class MapContentServiceCollectionExtensions
{
    public static IServiceCollection AddMapContent(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<MapParser>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IContentModule, MapContentModule>());
        return services;
    }
}
