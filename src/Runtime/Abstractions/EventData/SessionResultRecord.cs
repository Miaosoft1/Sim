using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Runtime.EventData;

public abstract record SessionResultRecord(TimeSpan Time);

public interface ISessionResultRecordEvent : IEvent
{
    SessionResultRecord CreateResultRecord();
}
