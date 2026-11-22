using Miaosoft.Sim.Client;
using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.Hex;
using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Protocol.Command;
using Miaosoft.Sim.Protocol.Event;
using Miaosoft.Sim.Protocol.Model;
using Miaosoft.Sim.Runtime;
using Miaosoft.Sim.Runtime.EventData;
using Miaosoft.Sim.Samples.NetworkSession.Infrastructure;

namespace Miaosoft.Sim.Samples.NetworkSession;

public sealed partial class NetworkSessionSample
{
    [TestMethod]
    public async Task DynamicWorldRechecksQueuedControlAndRejectsReplacementParticipantObjects()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CancellationToken token = timeout.Token;
        var (simulation, units, world) = await NeutralArena.PrepareAsync(
            Path.Combine(AppContext.BaseDirectory, "combat.yaml"), token);
        using Context context = simulation.Context;
        Unit unit = units[0];
        var position = unit.MapObject!.Position;
        var command = new MoveCommand
        {
            UnitId = unit.Id, MapInstanceId = unit.MapObjects.ControlledMapInstanceId!.Value,
            ControlVersion = unit.MapObjects.ControlVersion, Destination = new(20, 10)
        };
        Assert.IsTrue(simulation.AssessInput(unit.Id, command).Allowed);
        var input = new SessionInput(Guid.NewGuid(), Guid.NewGuid(), unit.Id, 1, 1, Guid.NewGuid(), command);
        SessionOperationCompletedEvent? completed = null;
        context.EventProcessed += ev =>
        {
            if (ev is SessionOperationCompletedEvent result && result.ExecutionId == input.ExecutionId)
                completed = result;
        };
        foreach (IEvent ev in simulation.CreateInputEvents(input))
        {
            context.Dispatch(ev).ToList();
            while (context.HasEventsDueNow)
                Assert.IsTrue((await context.NextAsync(token)).KeepRunning);
            if (ev is MessageAdmissionEvent) world.Remove(unit.Id);
        }
        Assert.IsNotNull(completed);
        Assert.AreEqual(OperationExecutionStatus.Rejected, completed.Status);
        Assert.AreEqual(position, unit.MapObject.Position);
        Assert.IsFalse(simulation.AssessInput(unit.Id, command).Allowed);
        world[unit.Id] = new Unit
        {
            Id = unit.Id, Name = unit.Name, ArchetypeId = unit.ArchetypeId,
            Abilities = unit.Abilities, EventReceiver = unit.EventReceiver
        };
        Assert.ThrowsExactly<InvalidOperationException>(() => simulation.CaptureWorld(unit.Id));
        world[unit.Id] = unit;
        using var foreign = new Context();
        var receiver = foreign.Factory.NewEventReceiver();
        foreign.AddEventReceiver(receiver);
        var foreignUnit = new Unit
        {
            Name = "Foreign", ArchetypeId = unit.ArchetypeId, Abilities = new(), EventReceiver = receiver
        };
        world.Add(foreignUnit.Id, foreignUnit);
        Assert.ThrowsExactly<InvalidOperationException>(() => simulation.CaptureWorld(unit.Id));
        world.Remove(foreignUnit.Id);
        Assert.HasCount(units.Count, simulation.CaptureWorld(unit.Id).Units);
        TestContext.WriteLine("Removing an original actor after queue acceptance rejected execution without moving its body. The dynamic directory rejected a different object carrying a participant's ID and an initialized foreign-Context unit, then accepted the restored originals. World membership never rewrote the fixed admission roster.");
    }

    [TestMethod]
    public async Task PortalsRespectActualVisibilityAndControlAcrossFullRecovery()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        CancellationToken token = timeout.Token;
        var (simulation, units, _) = await NeutralArena.PrepareAsync(
            Path.Combine(AppContext.BaseDirectory, "combat.yaml"), token);
        MapRuntime map = units[0].MapObject!.MapRuntime!;
        var visibleDestination = new MapRuntime(id: "interior.visible", name: "Visible interior");
        var hiddenDestination = new MapRuntime(id: "interior.hidden", name: "Hidden interior");
        map.RegisterDungeonMap("near", visibleDestination);
        map.RegisterDungeonMap("far", hiddenDestination);
        var nearEvents = simulation.Context.Factory.NewEventReceiver();
        var farEvents = simulation.Context.Factory.NewEventReceiver();
        simulation.Context.AddEventReceiver(nearEvents);
        simulation.Context.AddEventReceiver(farEvents);
        var near = new MapObject("near", new(12, 10), nearEvents, kind: MapObjectKind.Dungeon, size: new(2, 3));
        var far = new MapObject("far", new(99, 99), farEvents, kind: MapObjectKind.Dungeon);
        map.AddRoom(new(near));
        map.AddRoom(new(far));
        var session = new SessionRuntime(simulation, EventCodec.CreateBuilder().Build(), enableDemoAdmission: true);
        await using LoopbackSession host = await LoopbackSession.StartAsync(session, token);
        await using var first = new GameClient();
        await using var second = new GameClient();
        SessionWelcome welcome = await first.EnterDemoAsync(host.Address, token);
        await second.EnterDemoAsync(host.Address, token);
        await WaitUntil(() => first.World.State.World.TimeScale == 1, token);
        MapPortalSnapshot portal = first.World.State.World.Maps.Single().Portals.Single();
        Assert.AreEqual("near", portal.Id);
        Assert.AreEqual(visibleDestination.InstanceId, portal.DestinationInstanceId);
        Assert.AreEqual(new WorldPosition(12, 10), portal.Position);
        Assert.AreEqual(new WorldPosition(2, 3), portal.Size);
        Assert.IsFalse(first.World.State.World.Maps.Any(entry => entry.InstanceId == visibleDestination.InstanceId ||
            entry.InstanceId == hiddenDestination.InstanceId));
        await session.SetControlledUnitAsync(welcome.Credentials.ParticipantId, null, token);
        await WaitUntil(() => first.World.State.Binding.UnitId == null, token);
        Assert.HasCount(0, first.World.State.World.Maps.Single().Portals);
        await session.SetControlledUnitAsync(welcome.Credentials.ParticipantId, welcome.Snapshot.Binding.UnitId, token);
        await WaitUntil(() => first.World.State.World.Maps.Single().Portals.Count == 1, token);
        await first.DisconnectAsync(token);
        PreparedSessionSnapshot rejoined = await first.RejoinAsync(cancellationToken: token);
        Assert.AreEqual(portal, rejoined.Frame.World.Maps.Single().Portals.Single());
        Assert.IsNull(first.ObservationFailure);
        TestContext.WriteLine("Only the genuinely visible entrance was published; neither unseen entrance nor destination world was disclosed. Losing control removed entrances, and same-participant recovery restored only the authorized original destination identity.");
    }

    [TestMethod]
    [DataRow(HexOrientation.PointyTop, HexGridOrientation.PointyTop)]
    [DataRow(HexOrientation.FlatTop, HexGridOrientation.FlatTop)]
    public async Task HexGeometrySurvivesActualMovementAndSameParticipantRejoin(
        HexOrientation orientation, HexGridOrientation expectedOrientation)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        CancellationToken token = timeout.Token;
        var layout = new HexBoardLayout(new HexBoardDefinition([new(0, 0), new(0, 1), new(1, 0)]), 2, orientation);
        var (simulation, _, _) = await NeutralArena.PrepareAsync(
            Path.Combine(AppContext.BaseDirectory, "combat.yaml"), token, layout);
        var session = new SessionRuntime(simulation, EventCodec.CreateBuilder().Build(), enableDemoAdmission: true);
        await using LoopbackSession host = await LoopbackSession.StartAsync(session, token);
        await using var first = new GameClient();
        await using var second = new GameClient();
        SessionWelcome welcome = await first.EnterDemoAsync(host.Address, token);
        await second.EnterDemoAsync(host.Address, token);
        await WaitUntil(() => first.World.State.World.TimeScale == 1, token);
        MapSnapshot map = first.World.State.World.Maps.Single();
        HexGridSnapshot grid = map.HexGrid!;
        Assert.IsNotNull(grid);
        Assert.AreEqual(expectedOrientation, grid.Orientation);
        Assert.AreEqual(layout.CellSize, grid.CellSize);
        Assert.AreEqual(new WorldPosition(layout.Origin.X, layout.Origin.Y), grid.Origin);
        Assert.HasCount(3, grid.Cells);
        foreach (HexCellSnapshot cell in grid.Cells)
        {
            var actual = layout.ToWorld(new(cell.Q, cell.R));
            Assert.AreEqual(new WorldPosition(actual.X, actual.Y), cell.Center);
        }
        UnitSnapshot source = Self(first);
        var destination = layout.ToWorld(new(1, 0));
        OperationReceipt movement = await first.SendAsync(new MoveCommand
        {
            UnitId = source.Id, MapInstanceId = map.InstanceId, ControlVersion = source.ControlVersion,
            Destination = new(destination.X, destination.Y)
        }, token);
        Assert.AreEqual(OperationExecutionStatus.Succeeded, (await first.WaitForOutcomeAsync(movement.Sequence, token)).Status);
        await WaitUntil(() => Self(first).Position == new WorldPosition(destination.X, destination.Y), token);
        await first.DisconnectAsync(token);
        PreparedSessionSnapshot rejoined = await first.RejoinAsync(cancellationToken: token);
        MapSnapshot restored = rejoined.Frame.World.Maps.Single();
        Assert.AreEqual(welcome.Credentials.ParticipantId, rejoined.Binding.ParticipantId);
        Assert.AreEqual(map.InstanceId, restored.InstanceId);
        CollectionAssert.AreEqual(grid.Cells.ToArray(), restored.HexGrid!.Cells.ToArray());
        Assert.AreEqual(new WorldPosition(destination.X, destination.Y), Self(first).Position);
        Assert.IsNull(first.ObservationFailure);
        TestContext.WriteLine($"{orientation}: three actual playable cells retained authoritative centers and map identity across a real grid move and same-participant rejoin. Occupancy/reservation dictionaries were not published.");
    }
}
