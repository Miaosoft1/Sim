namespace Miaosoft.Sim.Core;

/// <summary>
/// Groups derived event work under the context's execution owner. Closing a scope
/// cancels that work without cancelling independently owned event tokens.
/// </summary>
public sealed class EventScope : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly CancellationToken scopeToken;
    private readonly AtomicCell<bool> logicalCancellation = new(false);
    private readonly Dictionary<CancellationToken, CancellationTokenSource> linkedTokens = [];

    internal EventScope(Context context)
    {
        Context = context;
        scopeToken = cancellation.Token;
    }

    internal Context Context { get; }
    public Guid Id { get; } = Guid.NewGuid();
    public bool IsCancellationRequested => logicalCancellation.Value;
    public EventCancellation Token => new(this, default);
    public EventCancellation With(CancellationToken cancellationToken) => new(this, cancellationToken);

    public void StageCancellation(AtomicTransaction transaction)
    {
        if (!transaction.Read(logicalCancellation))
            transaction.Stage(logicalCancellation, true);
    }

    internal CancellationToken Combine(CancellationToken token)
    {
        if (scopeToken.IsCancellationRequested) return scopeToken;
        if (!token.CanBeCanceled || token == scopeToken) return scopeToken;
        if (token.IsCancellationRequested) return token;
        if (!linkedTokens.TryGetValue(token, out CancellationTokenSource? linked))
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(scopeToken, token);
            linkedTokens.Add(token, linked);
        }
        return linked.Token;
    }

    public void Cancel()
    {
        if (scopeToken.IsCancellationRequested) return;
        if (!logicalCancellation.Value)
            logicalCancellation.Set(true);
        try
        {
            cancellation.Cancel();
        }
        finally
        {
            try
            {
                Context.DiscardScopeEvents(this);
            }
            finally
            {
                foreach (CancellationTokenSource linked in linkedTokens.Values) linked.Dispose();
                linkedTokens.Clear();
                cancellation.Dispose();
                Context.ScopeClosed(this);
            }
        }
    }

    public void Dispose() => Cancel();
}
