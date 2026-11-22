using Microsoft.Extensions.Hosting;
using Miaosoft.Sim.Runtime;

namespace Miaosoft.Sim.Server.Hosting;

internal sealed class SessionOwnerMonitor(SessionRuntime session) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await session.Completion.WaitAsync(stoppingToken).ConfigureAwait(false);
        if (!stoppingToken.IsCancellationRequested)
            throw new InvalidOperationException("The session execution owner stopped while its host was still running.");
    }
}
