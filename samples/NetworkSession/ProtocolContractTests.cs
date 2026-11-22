using Miaosoft.Sim.Control;
using Miaosoft.Sim.Client;
using Miaosoft.Sim.Protocol.Command;
using Miaosoft.Sim.Protocol.Event;
using Miaosoft.Sim.Protocol.Model;
using YamlDotNet.Core;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace Miaosoft.Sim.Samples.NetworkSession;

[TestClass]
public sealed class ProtocolContractTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void TransientFactsCannotBeInjectedOrRestoredAsFullSnapshotState()
    {
        EventCodecRegistry codec = EventCodec.CreateBuilder().Build();
        var fact = new ProjectileFact
        {
            EventSequence = 1, Time = TimeSpan.Zero, Kind = ProjectileFactKind.Hit,
            ProjectileId = Guid.NewGuid(), MapInstanceId = Guid.NewGuid(), SourceUnitId = Guid.NewGuid(),
            AbilityId = "bolt", AbilityInstanceId = Guid.NewGuid(), Position = new(3, 5)
        };
        EventEnvelope envelope = codec.Encode(fact, EventDirection.ServerToClient);
        Assert.AreEqual(fact, codec.Decode(envelope, EventDirection.ServerToClient));
        Assert.ThrowsExactly<ArgumentException>(() => codec.Decode(envelope, EventDirection.ClientToServer));
        var frame = new ObservationFrame
        {
            StateVersion = 0, PublicationSequence = 1, ConnectionVersion = 1, IsFull = true,
            World = new()
            {
                Time = TimeSpan.Zero, Maps = [new()
                {
                    InstanceId = fact.MapInstanceId, DefinitionId = "arena", Name = "Arena", Width = 10, Height = 10
                }]
            }
        };
        var world = new ClientWorld(codec);
        var snapshot = new PreparedSessionSnapshot(Guid.NewGuid(), new(Guid.NewGuid(), Guid.NewGuid(), null, 1), 1, frame);
        world.Prepare(snapshot);
        ClientObservationState original = world.State;
        Assert.ThrowsExactly<InvalidDataException>(() => world.Prepare(snapshot with { Frame = frame with { Events = [envelope] } }));
        Assert.AreSame(original, world.State);
        Assert.AreEqual(FrameApplication.Applied, world.Apply(frame with
        {
            IsFull = false, StateVersion = 1, PublicationSequence = 2, Events = [envelope]
        }));
        Assert.AreEqual(fact, world.State.Events.Single());
        TestContext.WriteLine("A typed confirmed projectile fact roundtripped only in the server direction, applied once in a live frame, and could not be injected or restored as full snapshot state.");
    }

    [TestMethod]
    public void HexFrameValidationAndFreezingCommitAtomically()
    {
        var cells = new List<HexCellSnapshot> { new() { Q = 0, R = 0, Center = new(2, 2) } };
        var grid = new HexGridSnapshot
        {
            CellSize = 2, Orientation = HexGridOrientation.PointyTop, Origin = new(2, 2), Cells = cells
        };
        var map = new MapSnapshot
        {
            InstanceId = Guid.NewGuid(), DefinitionId = "hex", Name = "Hex", Width = 4, Height = 4, HexGrid = grid
        };
        var frame = new ObservationFrame
        {
            StateVersion = 0, PublicationSequence = 1, ConnectionVersion = 1, IsFull = true,
            World = new() { Time = TimeSpan.Zero, Maps = [map] }
        };
        var world = new ClientWorld(EventCodec.CreateBuilder().Build());
        world.Prepare(new(Guid.NewGuid(), new(Guid.NewGuid(), Guid.NewGuid(), null, 1), 1, frame));
        ClientObservationState original = world.State;
        cells.Clear();
        Assert.HasCount(1, original.World.Maps.Single().HexGrid!.Cells);
        HexCellSnapshot cell = original.World.Maps.Single().HexGrid!.Cells.Single();
        HexGridSnapshot[] invalid =
        [
            grid with { Cells = [cell, cell] },
            grid with { Cells = [cell with { Center = new(double.NaN, 2) }] },
            grid with { Cells = [cell], Orientation = (HexGridOrientation)99 }
        ];
        foreach (HexGridSnapshot candidate in invalid)
        {
            Assert.ThrowsExactly<InvalidDataException>(() => world.Apply(frame with
            {
                StateVersion = 1, PublicationSequence = 2, IsFull = false,
                World = frame.World with { Maps = [map with { HexGrid = candidate }] }
            }));
            Assert.AreSame(original, world.State);
        }
        TestContext.WriteLine("Detached hex geometry survived author-list mutation. Duplicate cells, nonfinite centers and unsupported orientation each rejected the entire frame without changing the prior published world.");
    }

    [TestMethod]
    public void NativeYamlRoundtripRetainsActualAbilityIdentityAndRejectsUnknownFieldsAndVersions()
    {
        EventCodecRegistry codec = EventCodec.CreateBuilder().Build();
        var command = new CastAbilityCommand
        {
            UnitId = Guid.NewGuid(), MapInstanceId = Guid.NewGuid(), ControlVersion = 4,
            AbilityInstanceId = Guid.NewGuid(), TargetUnitId = Guid.NewGuid(), TargetPoint = new(3, 5)
        };
        EventEnvelope envelope = codec.Encode(command, EventDirection.ClientToServer);
        Assert.AreEqual(command, codec.Decode(envelope, EventDirection.ClientToServer));
        Assert.ThrowsExactly<ArgumentException>(() => codec.Decode(envelope with { Version = 1 }, EventDirection.ClientToServer));
        Assert.Throws<YamlException>(() =>
            codec.Decode(envelope with { Payload = envelope.Payload + "unknownField: true\n" }, EventDirection.ClientToServer));
        TestContext.WriteLine("Native YAML preserved unit/map/control and actual runtime ability identities; old wire versions and unknown authored fields were rejected.");
    }

    [TestMethod]
    [DataRow(OperationExecutionStatus.Rejected)]
    [DataRow(OperationExecutionStatus.Aborted)]
    [DataRow(OperationExecutionStatus.Unknown)]
    public void BusinessOutcomesAreTypedServerEventsAndCannotBeInjectedAsClientCommands(OperationExecutionStatus status)
    {
        EventCodecRegistry codec = EventCodec.CreateBuilder().Build();
        var result = new OperationResult(17, Guid.NewGuid(), status, "explicit execution disposition");
        EventEnvelope envelope = codec.Encode(result, EventDirection.ServerToClient);
        Assert.AreEqual(result, codec.Decode(envelope, EventDirection.ServerToClient));
        Assert.ThrowsExactly<ArgumentException>(() => codec.Decode(envelope, EventDirection.ClientToServer));
        TestContext.WriteLine($"An explicit {status} execution outcome retained its operation sequence/correlation independently of queue acceptance, and the same result contract was forbidden on client ingress.");
    }

    [TestMethod]
    public void SharedControlChecksTheActualInstanceWithoutFallingBackToAnotherSameDefinitionAbility()
    {
        Guid unit = Guid.NewGuid(), map = Guid.NewGuid(), first = Guid.NewGuid(), second = Guid.NewGuid();
        var self = new UnitSnapshot
        {
            Id = unit, Name = "Actor", IsAlive = true, Position = new(0, 0), MapInstanceId = map,
            ControlVersion = 3, MoveSpeed = 5,
            Abilities =
            [
                new() { RuntimeInstanceId = first, DefinitionId = "bolt", Name = "Bolt", Level = 3,
                    CanActivate = false, AvailableCharges = 0 },
                new() { RuntimeInstanceId = second, DefinitionId = "bolt", Name = "Bolt", Level = 1,
                    CanActivate = true, AvailableCharges = 1 }
            ]
        };
        var world = new WorldSnapshot { Time = TimeSpan.Zero, Units = [self] };
        var cast = new CastAbilityCommand { UnitId = unit, MapInstanceId = map, ControlVersion = 3, AbilityInstanceId = first };
        Assert.IsFalse(UnitCommandPreconditions.Evaluate(cast, world, unit).Ready);
        Assert.IsTrue(UnitCommandPreconditions.Evaluate(cast with { AbilityInstanceId = second }, world, unit).Ready);
        Assert.IsFalse(UnitCommandPreconditions.Evaluate(cast with { AbilityInstanceId = second, ControlVersion = 2 }, world, unit).Ready);
        Assert.IsFalse(UnitCommandPreconditions.Evaluate(cast with { UnitId = Guid.NewGuid() }, world, unit).Ready);
        TestContext.WriteLine("The exhausted level-3 instance did not fall back to another copy. Explicitly selecting the other instance worked; stale control and foreign ownership failed shared checks.");
    }
}
