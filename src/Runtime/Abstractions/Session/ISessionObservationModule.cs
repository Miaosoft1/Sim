namespace Miaosoft.Sim.Runtime;

public enum ModuleProjectionKind { Unchanged, Full, Delta, Removed }

public sealed record ModuleProjection(
    ModuleProjectionKind Kind, string Payload = "", string? PublishedState = null);

public interface ISessionObservationModule
{
    string Id { get; }
    int Version { get; }
    bool RequiresControlledUnit { get; }
    ModuleProjection Capture(Guid? unitId, long stateVersion, bool full, string? previousPublishedState);
}
