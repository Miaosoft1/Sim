using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Core
{
    public interface IContextAttachObserver
    {
        IEnumerable<IEvent> OnAttached(Context context);
    }
}
