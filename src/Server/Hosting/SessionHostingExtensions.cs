using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Miaosoft.Sim.Runtime;

namespace Miaosoft.Sim.Server.Hosting;

public static class SessionHostingExtensions
{
    public static IServiceCollection AddMiaosoftSession(this IServiceCollection services, SessionRuntime session)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(session);
        services.AddSingleton(_ => session);
        services.AddSignalR(options => options.MaximumReceiveMessageSize = 64 * 1024);
        services.AddHostedService<SessionFramePublisher>();
        services.AddHostedService<SessionOwnerMonitor>();
        return services;
    }

    public static HubEndpointConventionBuilder MapMiaosoftSession(
        this IEndpointRouteBuilder endpoints, string pattern = "/session") => endpoints.MapHub<SessionHub>(pattern);
}
