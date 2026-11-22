using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Miaosoft.Sim.Content.Loading.Parser;

namespace Miaosoft.Sim.Content.Loading.Registration;

public static class ContentLoadingServiceCollectionExtensions
{
    public static IServiceCollection AddContentLoading(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddTransient<IContentCatalogLoader, ContentCatalogLoader>();
        return services;
    }
}
