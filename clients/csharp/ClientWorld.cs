using System.Collections.ObjectModel;
using Miaosoft.Sim.Client.Observation;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Protocol.Event;
using Miaosoft.Sim.Protocol.Model;

namespace Miaosoft.Sim.Client;

public sealed record ClientObservationState(
    SessionBinding Binding, long StateVersion, long PublicationSequence, WorldSnapshot World,
    IReadOnlyDictionary<string, object> Modules, IReadOnlyList<IEventMessage> Events);

public enum FrameApplication { Applied, StaleConnection, SupersededPublication }

public sealed class ClientWorld : IWorld
{
    private readonly Dictionary<string, IClientObservationModule> modules;
    private readonly EventCodecRegistry codec;
    private readonly object updates = new();
    private ClientObservationState? published;

    public ClientWorld(EventCodecRegistry codec, IEnumerable<IClientObservationModule>? observationModules = null)
    {
        ArgumentNullException.ThrowIfNull(codec);
        this.codec = codec;
        modules = (observationModules ?? []).ToDictionary(module => module.Id, StringComparer.Ordinal);
        if (modules.Values.Any(module => string.IsNullOrWhiteSpace(module.Id) || module.Version < 1))
            throw new ArgumentException("Client observation modules require distinct IDs and positive versions.", nameof(observationModules));
    }

    public ClientObservationState State => Volatile.Read(ref published) ??
        throw new InvalidOperationException("No session world has been prepared.");
    public IReadOnlyList<ModuleVersion> Capabilities =>
        Array.AsReadOnly(modules.Values.Select(module => new ModuleVersion(module.Id, module.Version)).ToArray());
    public WorldSnapshot GetWorldSnapshot() => State.World;

    public void Prepare(PreparedSessionSnapshot snapshot)
    {
        lock (updates) PrepareCore(snapshot);
    }

    private void PrepareCore(PreparedSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        SessionBinding binding = snapshot.Binding;
        ClientObservationState? previous = Volatile.Read(ref published);
        if (snapshot.SnapshotId == Guid.Empty || binding == null || binding.SessionId == Guid.Empty ||
            binding.ParticipantId == Guid.Empty || binding.ConnectionVersion < 1 || snapshot.NextOperationSequence < 1 ||
            !snapshot.Frame.IsFull || snapshot.Frame.ConnectionVersion != binding.ConnectionVersion ||
            snapshot.Frame.ControlledUnitId != binding.UnitId)
            throw new InvalidDataException("The prepared session snapshot is invalid.");
        if (previous != null && (previous.Binding.SessionId != binding.SessionId ||
            previous.Binding.ParticipantId != binding.ParticipantId || binding.ConnectionVersion < previous.Binding.ConnectionVersion ||
            binding.ConnectionVersion == previous.Binding.ConnectionVersion &&
                (snapshot.Frame.PublicationSequence < previous.PublicationSequence || snapshot.Frame.StateVersion < previous.StateVersion)))
            throw new InvalidDataException("A prepared snapshot cannot change participant identity or restore an older connection.");
        if (previous?.World.IsCompleted == true && snapshot.Frame.World is { IsCompleted: false })
            throw new InvalidDataException("Recovery cannot restart a completed session.");
        ClientObservationState next = Build(snapshot.Frame, binding, null);
        Volatile.Write(ref published, next);
    }

    public FrameApplication Apply(ObservationFrame frame)
    {
        lock (updates) return ApplyCore(frame);
    }

    private FrameApplication ApplyCore(ObservationFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ClientObservationState previous = State;
        if (frame.ConnectionVersion < previous.Binding.ConnectionVersion) return FrameApplication.StaleConnection;
        if (frame.ConnectionVersion == previous.Binding.ConnectionVersion &&
            frame.PublicationSequence <= previous.PublicationSequence && frame.StateVersion <= previous.StateVersion)
            return FrameApplication.SupersededPublication;
        if (frame.IsFull || frame.ConnectionVersion != previous.Binding.ConnectionVersion ||
            frame.PublicationSequence != checked(previous.PublicationSequence + 1) ||
            frame.StateVersion <= previous.StateVersion ||
            previous.World.IsCompleted && frame.World is { IsCompleted: false })
            throw new InvalidDataException("The observation stream is discontinuous; a fresh snapshot is required.");
        SessionBinding binding = previous.Binding with { UnitId = frame.ControlledUnitId };
        ClientObservationState next = Build(frame, binding, previous);
        Volatile.Write(ref published, next);
        return FrameApplication.Applied;
    }

    private ClientObservationState Build(ObservationFrame frame, SessionBinding binding, ClientObservationState? previous)
    {
        if (frame.StateVersion < 0 || frame.PublicationSequence < 1 || frame.World == null ||
            frame.World.Units == null || frame.World.Maps == null || frame.World.Projectiles == null ||
            frame.Modules == null || frame.Events == null ||
            frame.World.Time < TimeSpan.Zero || !double.IsFinite(frame.World.TimeScale) || frame.World.TimeScale < 0)
            throw new InvalidDataException("The world frame header is invalid.");
        if (frame.World.Units.Any(unit => unit == null || unit.Id == Guid.Empty ||
                !double.IsFinite(unit.Position.X) || !double.IsFinite(unit.Position.Y) || unit.Level < 1 ||
                unit.MaximumLife < 1 || unit.Life < 0 || unit.Life > unit.MaximumLife ||
                unit.MaximumEnergy < 0 || unit.Energy < 0 || unit.Energy > unit.MaximumEnergy ||
                unit.Experience.HasValue != unit.MaximumExperience.HasValue ||
                unit.Experience < 0 || unit.MaximumExperience < 0 || unit.Experience > unit.MaximumExperience ||
                unit.Statuses == null || unit.Statuses.Any(status => status == null || status.RuntimeInstanceId == Guid.Empty ||
                    status.ApplicationId == Guid.Empty || string.IsNullOrWhiteSpace(status.DefinitionId) || status.Stacks < 1 ||
                    status.ExpiresAt < TimeSpan.Zero ||
                    status.UsesRealTime && (status.ExpiresAt != null || status.RemainingRealTimeSeconds == null) ||
                    !status.UsesRealTime && status.RemainingRealTimeSeconds != null ||
                    status.RemainingRealTimeSeconds is { } seconds && (!double.IsFinite(seconds) || seconds < 0)) ||
                unit.Statuses.Select(status => status.RuntimeInstanceId).Distinct().Count() != unit.Statuses.Count ||
                unit.Abilities == null || unit.Abilities.Any(ability => ability == null ||
                    ability.RuntimeInstanceId == Guid.Empty || string.IsNullOrWhiteSpace(ability.DefinitionId) || ability.Level < 1 ||
                    !double.IsFinite(ability.RemainingCooldownSeconds) || ability.RemainingCooldownSeconds < 0 ||
                    ability.AvailableCharges < 0) ||
                unit.Abilities.Select(ability => ability.RuntimeInstanceId).Distinct().Count() != unit.Abilities.Count) ||
            frame.World.Units.Select(unit => unit.Id).Distinct().Count() != frame.World.Units.Count ||
            binding.UnitId.HasValue && frame.World.Units.All(unit => unit.Id != binding.UnitId))
            throw new InvalidDataException("The world contains an invalid or missing controlled unit.");
        if (frame.World.Maps.Any(map => map == null || map.InstanceId == Guid.Empty ||
                string.IsNullOrWhiteSpace(map.DefinitionId) || !double.IsFinite(map.Width) || !double.IsFinite(map.Height) ||
                map.Width <= 0 || map.Height <= 0 || map.Rooms == null || map.Corridors == null || map.Portals == null ||
                map.HexGrid is { } grid && (!double.IsFinite(grid.CellSize) || grid.CellSize <= 0 ||
                    !Enum.IsDefined(grid.Orientation) || !Finite(grid.Origin) || grid.Cells == null ||
                    grid.Cells.Count == 0 || grid.Cells.Any(cell => cell == null || !Finite(cell.Center)) ||
                    grid.Cells.Select(cell => (cell.Q, cell.R)).Distinct().Count() != grid.Cells.Count) ||
                map.Rooms.Any(room => room == null || string.IsNullOrWhiteSpace(room.Id) ||
                    !Finite(room.Position) || !Finite(room.Size) || room.Size.X <= 0 || room.Size.Y <= 0) ||
                map.Corridors.Any(corridor => corridor == null || !Finite(corridor.Start) || !Finite(corridor.End) ||
                    !double.IsFinite(corridor.Width) || corridor.Width <= 0) ||
                map.Portals.Any(portal => portal == null || string.IsNullOrWhiteSpace(portal.Id) ||
                    portal.DestinationInstanceId == Guid.Empty || !Finite(portal.Position) ||
                    !Finite(portal.Size) || portal.Size.X <= 0 || portal.Size.Y <= 0) ||
                map.Portals.Select(portal => portal.Id).Distinct(StringComparer.Ordinal).Count() != map.Portals.Count) ||
            frame.World.Maps.Select(map => map.InstanceId).Distinct().Count() != frame.World.Maps.Count ||
            frame.World.Units.Any(unit => unit.MapInstanceId is { } id && frame.World.Maps.All(map => map.InstanceId != id)))
            throw new InvalidDataException("A world map or unit map reference is invalid.");
        if (frame.World.Projectiles.Any(projectile => projectile == null || projectile.Id == Guid.Empty ||
                projectile.SourceUnitId == Guid.Empty || projectile.AbilityInstanceId == Guid.Empty ||
                string.IsNullOrWhiteSpace(projectile.AbilityId) || !Finite(projectile.Position) || !Finite(projectile.Destination) ||
                !double.IsFinite(projectile.Speed) || projectile.Speed < 0 ||
                frame.World.Maps.All(map => map.InstanceId != projectile.MapInstanceId)) ||
            frame.World.Projectiles.Select(projectile => projectile.Id).Distinct().Count() != frame.World.Projectiles.Count)
            throw new InvalidDataException("An observed projectile or map reference is invalid.");
        if (frame.Modules.Any(module => module == null) ||
            frame.Modules.Select(module => module.Id).Distinct(StringComparer.Ordinal).Count() != frame.Modules.Count ||
            frame.IsFull && !frame.Modules.Select(module => module.Id).ToHashSet(StringComparer.Ordinal).SetEquals(modules.Keys))
            throw new InvalidDataException("The observation frame omitted or duplicated a negotiated module.");
        var states = previous == null ? new Dictionary<string, object>(StringComparer.Ordinal) :
            new Dictionary<string, object>(previous.Modules, StringComparer.Ordinal);
        foreach (ModuleSnapshot update in frame.Modules)
        {
            if (string.IsNullOrWhiteSpace(update.Id) || !modules.TryGetValue(update.Id, out IClientObservationModule? module) ||
                module.Version != update.Version || update.StateVersion != frame.StateVersion)
                throw new InvalidDataException("The observation module identity, version or publication boundary is invalid.");
            switch (update.Kind)
            {
                case ModuleUpdateKind.Full:
                    states[update.Id] = module.ReadFull(update.Payload, binding) ??
                        throw new InvalidDataException("A full module state cannot be null.");
                    break;
                case ModuleUpdateKind.Delta:
                    if (frame.IsFull || !states.TryGetValue(update.Id, out object? state))
                        throw new InvalidDataException("A module delta requires its established full state.");
                    states[update.Id] = module.ApplyDelta(state, update.Payload, binding) ??
                        throw new InvalidDataException("A module delta cannot erase state implicitly.");
                    break;
                case ModuleUpdateKind.Removed:
                    if (!string.IsNullOrEmpty(update.Payload))
                        throw new InvalidDataException("A module removal cannot carry private payload.");
                    states.Remove(update.Id);
                    break;
                default:
                    throw new InvalidDataException("The observation module update kind is unknown.");
            }
        }
        IEventMessage[] events = frame.Events.Select(envelope => codec.Decode(envelope, EventDirection.ServerToClient)).ToArray();
        if (events.OfType<OperationResult>().Any(result => result.OperationSequence < 1 ||
                result.CorrelationId == Guid.Empty || !Enum.IsDefined(result.Status) ||
                result.Status == OperationExecutionStatus.Unspecified))
            throw new InvalidDataException("A business outcome omitted its operation identity or explicit execution status.");
        ProjectileFact[] facts = events.OfType<ProjectileFact>().ToArray();
        if (frame.IsFull && events.OfType<ITransientEventMessage>().Any() ||
            facts.Any(fact => fact.EventSequence < 0 || fact.Time < TimeSpan.Zero || fact.Time > frame.World.Time ||
                !Enum.IsDefined(fact.Kind) || fact.ProjectileId == Guid.Empty || fact.SourceUnitId == Guid.Empty ||
                fact.TargetUnitId == Guid.Empty || fact.AbilityInstanceId == Guid.Empty ||
                string.IsNullOrWhiteSpace(fact.AbilityId) || !Finite(fact.Position) ||
                (fact.Kind == ProjectileFactKind.Emitted) != fact.Destination.HasValue ||
                fact.Destination is { } destination && !Finite(destination) ||
                frame.World.Maps.All(map => map.InstanceId != fact.MapInstanceId)) ||
            facts.Select(fact => fact.EventSequence).Distinct().Count() != facts.Length)
            throw new InvalidDataException("A transient projectile fact is malformed or was replayed in a full snapshot.");
        WorldSnapshot world = frame.World with
        {
            Units = Array.AsReadOnly(frame.World.Units.Select(unit => unit with
            {
                Abilities = Array.AsReadOnly(unit.Abilities.ToArray()),
                Statuses = Array.AsReadOnly(unit.Statuses.ToArray())
            }).ToArray()),
            Maps = Array.AsReadOnly(frame.World.Maps.Select(map => map with
            {
                Rooms = Array.AsReadOnly(map.Rooms.ToArray()), Corridors = Array.AsReadOnly(map.Corridors.ToArray()),
                Portals = Array.AsReadOnly(map.Portals.ToArray()),
                HexGrid = map.HexGrid is { } grid ? grid with { Cells = Array.AsReadOnly(grid.Cells.ToArray()) } : null
            }).ToArray()),
            Projectiles = Array.AsReadOnly(frame.World.Projectiles.ToArray())
        };
        return new(binding, frame.StateVersion, frame.PublicationSequence, world,
            new ReadOnlyDictionary<string, object>(states), Array.AsReadOnly(events));
    }

    private static bool Finite(WorldPosition point) => double.IsFinite(point.X) && double.IsFinite(point.Y);
}
