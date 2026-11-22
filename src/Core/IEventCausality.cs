namespace Miaosoft.Sim.Core;

public interface IEventCausality
{
    Context Context { get; }
}

public interface IEventCausalityFactory
{
    IEventCausality Create(Context context);
}
