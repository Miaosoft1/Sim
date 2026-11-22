namespace Miaosoft.Sim.Core;

public readonly struct EventCancellation
{
    private readonly EventScope? scope;
    private readonly CancellationToken local;

    internal EventCancellation(EventScope? scope, CancellationToken local)
    {
        this.scope = scope;
        this.local = local;
    }

    public bool CanBeCanceled => scope != null || local.CanBeCanceled;
    public bool IsCancellationRequested => scope?.IsCancellationRequested == true || local.IsCancellationRequested;

    public CancellationTokenRegistration Register(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (IsCancellationRequested)
        {
            callback();
            return default;
        }
        CancellationToken notification = scope?.Combine(local) ?? local;
        return notification.Register(callback);
    }

    public static implicit operator EventCancellation(CancellationToken token) => new(null, token);
}
