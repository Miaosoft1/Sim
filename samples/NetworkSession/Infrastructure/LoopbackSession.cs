using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Miaosoft.Sim.Runtime;
using Miaosoft.Sim.Server.Hosting;

namespace Miaosoft.Sim.Samples.NetworkSession.Infrastructure;

internal sealed class LoopbackSession(WebApplication app, SessionRuntime session, Uri address) : IAsyncDisposable
{
    public SessionRuntime Session { get; } = session;
    public Uri Address { get; } = address;

    public static async Task<LoopbackSession> StartAsync(
        CancellationToken cancellationToken, IEnumerable<ISessionObservationModule>? modules = null,
        bool enableDemo = true, int inputCapacity = 1024)
    {
        SessionRuntime session = await NeutralArena.CreateAsync(
            Path.Combine(AppContext.BaseDirectory, "combat.yaml"), enableDemo, modules, inputCapacity, cancellationToken);
        return await StartAsync(session, cancellationToken);
    }

    public static async Task<LoopbackSession> StartAsync(SessionRuntime session, CancellationToken cancellationToken)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddMiaosoftSession(session);
        WebApplication application = builder.Build();
        application.MapMiaosoftSession();
        bool transferred = false;
        try
        {
            await application.StartAsync(cancellationToken);
            string bound = application.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            transferred = true;
            return new(application, session, new Uri(bound + "/session"));
        }
        finally
        {
            if (!transferred)
            {
                await application.DisposeAsync();
                await session.DisposeAsync();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await app.StopAsync(timeout.Token); }
        finally { await app.DisposeAsync(); }
    }
}
