using Miaosoft.Sim.Protocol.Model;

namespace Miaosoft.Sim.Client.Observation;

public interface IClientObservationModule
{
    string Id { get; }
    int Version { get; }
    object ReadFull(string payload, SessionBinding binding);
    /// <summary>Returns detached immutable state without mutating the previous publication.</summary>
    object ApplyDelta(object previous, string payload, SessionBinding binding);
}
