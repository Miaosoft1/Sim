using System.Globalization;
using Miaosoft.Sim.Client.Observation;
using Miaosoft.Sim.Runtime;
using Miaosoft.Sim.Protocol.Model;

namespace Miaosoft.Sim.Samples.NetworkSession.Infrastructure;

internal sealed class CounterModule(int version = 1) : ISessionObservationModule, IClientObservationModule
{
    private sealed record State(long Value);
    public string Id => "sample.counter";
    public int Version => version;
    public bool RequiresControlledUnit => true;
    public int UnitlessCaptures { get; private set; }

    public ModuleProjection Capture(Guid? unitId, long stateVersion, bool full, string? previousPublishedState)
    {
        if (unitId == null) UnitlessCaptures++;
        string current = stateVersion.ToString(CultureInfo.InvariantCulture);
        if (full || previousPublishedState == null) return new(ModuleProjectionKind.Full, current, current);
        long previous = Read(previousPublishedState);
        return stateVersion == previous ? new(ModuleProjectionKind.Unchanged) :
            new(ModuleProjectionKind.Delta, checked(stateVersion - previous).ToString(CultureInfo.InvariantCulture), current);
    }

    public object ReadFull(string payload, SessionBinding binding) => new State(Read(payload));
    public object ApplyDelta(object previous, string payload, SessionBinding binding) => previous is State state
        ? new State(checked(state.Value + Read(payload))) : throw new InvalidDataException("A counter delta needs counter state.");
    public static long Value(object state) => state is State counter
        ? counter.Value : throw new InvalidDataException("The counter state is invalid.");
    private static long Read(string payload) => long.TryParse(payload, CultureInfo.InvariantCulture, out long value) && value >= 0
        ? value : throw new InvalidDataException("The counter payload is not a nonnegative YAML integer.");
}
