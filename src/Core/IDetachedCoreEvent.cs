namespace Miaosoft.Sim.Core;

/// <summary>
/// A core event independent of the dispatching scope and its correlation.
/// Handler-dispatched Event subclasses use IsScopeDetached instead.
/// </summary>
public interface IDetachedCoreEvent : IEvent;
