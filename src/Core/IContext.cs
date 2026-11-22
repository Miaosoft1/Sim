namespace Miaosoft.Sim.Core
{
    public interface IContext
    {
        bool Next(out IEvent? ev);
        ValueTask<ContextStepResult> NextAsync(CancellationToken cancellationToken = default);
        IEnumerable<IEvent> Dispatch(IEvent ev);
    }
}
