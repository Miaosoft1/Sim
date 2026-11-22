namespace Miaosoft.Sim.Core;

public interface IEventMessage { }
public interface IDiscreteEvent : IEventMessage { }
public interface ICoalescibleEvent : IEventMessage
{
    string CoalescingKey { get; }
}
