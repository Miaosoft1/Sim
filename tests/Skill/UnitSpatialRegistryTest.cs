using Miaosoft.Sim.Core;
using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.Hex;
using Miaosoft.Sim.Combat.Map.Vector;
using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.Handler;
using Miaosoft.Sim.Combat.Skill.Targeting;

namespace Miaosoft.Sim.Combat.Skill.Tests;

[TestClass]
public sealed class UnitSpatialRegistryTest
{
    public required TestContext TestContext { get; set; }

    [TestMethod]
    public void MaplessMovementAndMapRegistrationPreserveOrder()
    {
        var context = new Context();
        var registry = new UnitSpatialRegistry();
        Unit first = CreateUnit(context, new(19, 19));
        Unit second = CreateUnit(context, new(22, 19));
        Unit corner = CreateUnit(context, new(24, 24));
        registry.Register(second);
        registry.Register(first);
        registry.Register(corner);
        AddDistantUnits(context, registry);

        CollectionAssert.AreEqual(new[] { second, first }, registry.QueryInRadius(new(19, 19), 5).ToArray());
        second.MapObject!.Position = new(250, 250);
        CollectionAssert.AreEqual(new[] { first }, registry.QueryInRadius(new(19, 19), 5).ToArray());
        var map = new MapRuntime(500, 500);
        map.AddObject(first.MapObject!);
        Assert.IsEmpty(registry.QueryInRadius(new(19, 19), 5));
        CollectionAssert.AreEqual(new[] { first }, registry.QueryInRadius(new(19, 19), 5, map: map).ToArray());
        Assert.IsTrue(registry.Unregister(second));
        second.MapObject!.Position = new(20, 19);
        Assert.IsEmpty(registry.QueryInRadius(new(19, 19), 5));
        registry.Register(second);
        CollectionAssert.AreEqual(new[] { second }, registry.QueryInRadius(new(19, 19), 5).ToArray());
        TestContext.WriteLine("Mapless movement, map entry and unregister/re-register maintained the correct candidates.");
    }

    [TestMethod]
    public void MapInstancesAndDirectSkillGeometryRemainDistinct()
    {
        var context = new Context();
        var registry = new UnitSpatialRegistry();
        var firstMap = new MapRuntime(new MapDefinition("world", "world", 103, 97, "Overworld", "Torus"));
        var secondMap = new MapRuntime(new MapDefinition("world", "world", 103, 97, "Overworld", "Torus"));
        Unit nearRight = CreateUnit(context, new(98, 50));
        Unit nearLeft = CreateUnit(context, new(1, 50));
        Unit otherInstance = CreateUnit(context, new(98, 50));
        foreach (Unit unit in new[] { nearRight, nearLeft, otherInstance }) registry.Register(unit);
        AddDistantUnits(context, registry);
        firstMap.AddObject(nearRight.MapObject!);
        firstMap.AddObject(nearLeft.MapObject!);
        secondMap.AddObject(otherInstance.MapObject!);

        CollectionAssert.AreEqual(new[] { nearRight },
            registry.QueryInRadius(new(98, 50), 5, map: firstMap).ToArray());
        CollectionAssert.AreEqual(new[] { otherInstance },
            registry.QueryInRadius(new(98, 50), 5, map: secondMap).ToArray());
        Assert.AreEqual(6, firstMap.Space.Distance(nearRight.MapObject!.Position, nearLeft.MapObject!.Position));
        CollectionAssert.AreEqual(new[] { nearRight },
            registry.QueryInLine(new(95, 50), new(1, 0), 8, 1, map: firstMap).ToArray());
        TestContext.WriteLine("Map-local targeting retained direct geometry even when visibility uses a wrapped map.");
    }

    [TestMethod]
    public void HexTargetingUsesCellDistanceWithoutChangingContinuousProjectileMovement()
    {
        var context = new Context();
        var registry = new UnitSpatialRegistry();
        var definition = new HexBoardDefinition([new(0, 0), new(1, 0), new(1, -1), new(1, 1)]);
        var layout = new HexBoardLayout(definition, 10, HexOrientation.PointyTop);
        var firstMap = new MapRuntime(layout.Width, layout.Height, targetingMetric: layout);
        var secondMap = new MapRuntime(layout.Width, layout.Height, targetingMetric: layout);
        Unit source = CreateUnit(context, layout.ToWorld(new(0, 0)));
        Unit adjacent = CreateUnit(context, layout.ToWorld(new(1, 0)));
        Unit distant = CreateUnit(context, layout.ToWorld(new(1, 1)));
        Unit otherPlane = CreateUnit(context, layout.ToWorld(new(1, 0)));
        foreach (Unit unit in new[] { source, adjacent, distant, otherPlane }) registry.Register(unit);
        AddDistantUnits(context, registry);
        firstMap.AddObject(source.MapObject!);
        firstMap.AddObject(adjacent.MapObject!);
        firstMap.AddObject(distant.MapObject!);
        secondMap.AddObject(otherPlane.MapObject!);

        var tracking = new TrackingSelector { Range = 1 };
        Assert.IsTrue(tracking.Validate(new AbilityTargetingContext { Source = source, Target = adjacent }));
        Assert.IsFalse(tracking.Validate(new AbilityTargetingContext { Source = source, Target = distant }));
        Assert.IsFalse(tracking.Validate(new AbilityTargetingContext { Source = source, Target = otherPlane }));
        var circle = new CircleSelector { Radius = 1, CastRange = 1 };
        var atSource = new AbilityTargetingContext { Source = source, TargetPoint = source.MapObject!.Position };
        CollectionAssert.AreEqual(new[] { adjacent }, circle.ResolveTargets(atSource, registry).ToArray());
        Assert.IsFalse(circle.Validate(atSource with { TargetPoint = distant.MapObject!.Position }));
        Assert.IsFalse(circle.Validate(atSource with { TargetPoint = new Vector2D(double.MaxValue, double.MaxValue) }));
        var line = new LineSelector { Range = 1, Width = 1 };
        var alongAdjacent = atSource with { TargetPoint = adjacent.MapObject!.Position };
        Assert.IsTrue(line.Validate(alongAdjacent));
        CollectionAssert.AreEqual(new[] { adjacent }, line.ResolveTargets(alongAdjacent, registry).ToArray());
        Assert.AreEqual(1, firstMap.TargetingDistance(source.MapObject.Position, adjacent.MapObject!.Position));
        Assert.IsGreaterThan(1, firstMap.Movement.Distance(source.MapObject.Position, adjacent.MapObject.Position));
        TestContext.WriteLine("Hex tracking and area range used axial steps; parallel maps stayed isolated and movement geometry remained continuous.");
    }

    [TestMethod]
    public void IndexedCircleAndLineMatchRegistrationOrderedBruteForce()
    {
        var context = new Context();
        var registry = new UnitSpatialRegistry();
        var map = new MapRuntime(1000, 1000);
        var random = new Random(82431);
        var units = new List<Unit>();
        for (int index = 0; index < 100; index++)
        {
            var position = new Vector2D(random.NextDouble() * 1000, random.NextDouble() * 1000);
            Unit unit = CreateUnit(context, position);
            units.Add(unit);
            registry.Register(unit);
            if (index % 2 == 0) map.AddObject(unit.MapObject!);
        }

        for (int query = 0; query < 200; query++)
        {
            Unit moved = units[random.Next(units.Count)];
            moved.MapObject!.Position = new(random.NextDouble() * 1000, random.NextDouble() * 1000);
            MapRuntime? scope = query % 2 == 0 ? map : null;
            Vector2D center = new(random.NextDouble() * 1000, random.NextDouble() * 1000);
            double radius = random.NextDouble() * 150;
            Unit[] expectedCircle = units.Where(unit => UnitSpatialRegistry.ObjectIn(unit, scope) is { } body &&
                unit.IsAlive && body.IsActive && body.IsInteractable && body.IsSelectable &&
                body.Position.DistanceTo(center) <= radius).ToArray();
            CollectionAssert.AreEqual(expectedCircle, registry.QueryInRadius(center, radius, map: scope).ToArray(),
                $"circle query {query}");

            var direction = new Vector2D(random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1);
            double length = random.NextDouble() * 300, width = random.NextDouble() * 30;
            double norm = Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
            double nx = direction.X / norm, ny = direction.Y / norm;
            Unit[] expectedLine = units.Where(unit =>
            {
                if (UnitSpatialRegistry.ObjectIn(unit, scope) is not { IsActive: true, IsInteractable: true, IsSelectable: true } body ||
                    !unit.IsAlive) return false;
                double dx = body.Position.X - center.X, dy = body.Position.Y - center.Y;
                double along = dx * nx + dy * ny;
                return along >= 0 && along <= length && Math.Abs(dx * ny - dy * nx) <= width;
            }).ToArray();
            CollectionAssert.AreEqual(expectedLine,
                registry.QueryInLine(center, direction, length, width, map: scope).ToArray(), $"line query {query}");
        }
        TestContext.WriteLine("400 fixed-seed circle and line queries matched direct scans in map and mapless scopes.");
    }

    [TestMethod]
    public void CircleCandidatesPreserveUnderflowingDirectDistance()
    {
        var context = new Context();
        var registry = new UnitSpatialRegistry();
        AddDistantUnits(context, registry);
        Unit first = CreateUnit(context, new(-1e-200, 0));
        Unit second = CreateUnit(context, new(-5e-163, 0));
        Unit third = CreateUnit(context, new(-5e-163, -5e-163));
        registry.Register(first);
        registry.Register(second);
        registry.Register(third);
        var center = new Vector2D(1e-162, 1e-162);
        Unit[] units = [first, second, third];
        foreach (double radius in new[] { 0d, 5e-163, 1.1e-162, 2.3e-162 })
        {
            Unit[] expected = units.Where(unit => unit.MapObject!.Position.DistanceTo(center) <= radius).ToArray();
            CollectionAssert.AreEqual(expected, registry.QueryInRadius(center, radius).ToArray(), $"radius={radius}");
        }
        Assert.AreEqual(0, first.MapObject!.Position.DistanceTo(center));
        TestContext.WriteLine("Circle candidates include values whose existing direct distance rounds to zero.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CircleAndLinePreserveInclusiveFloatingPointBoundaries(bool mapped)
    {
        var context = new Context();
        var registry = new UnitSpatialRegistry();
        MapRuntime? map = mapped ? new MapRuntime(20000, 20000) : null;
        Vector2D center = new(63, 63);
        Vector2D[] positions =
        [
            new(Math.BitDecrement(68d), 63), new(68, 63), new(Math.BitIncrement(68d), 63),
            new(58, 63), new(Math.BitDecrement(58d), 63), new(63, 68), new(68, 68),
            new(63, 65), new(63, Math.BitIncrement(65d)), new(73, 63),
            new(Math.BitIncrement(73d), 63), new(Math.BitDecrement(63d), 63),
        ];
        Unit[] units = positions.Select(position => CreateUnit(context, position)).ToArray();
        foreach (Unit unit in units)
        {
            registry.Register(unit);
            map?.AddObject(unit.MapObject!);
        }
        AddDistantUnits(context, registry, map);
        if (map != null)
            Assert.IsFalse(map.PrefersSpatialScan(new(58, 58), new(68, 68)));

        Unit[] circleExpected = units.Where(unit => unit.MapObject!.Position.DistanceTo(center) <= 5).ToArray();
        CollectionAssert.AreEqual(circleExpected, registry.QueryInRadius(center, 5, map: map).ToArray());
        Assert.Contains(units[1], circleExpected);
        Assert.DoesNotContain(units[2], circleExpected);
        Assert.Contains(units[3], circleExpected);
        Assert.DoesNotContain(units[4], circleExpected);
        Assert.DoesNotContain(units[6], circleExpected);

        Unit[] lineExpected = units.Where(unit =>
        {
            Vector2D point = unit.MapObject!.Position;
            double along = point.X - center.X;
            return along >= 0 && along <= 10 && Math.Abs(point.Y - center.Y) <= 2;
        }).ToArray();
        CollectionAssert.AreEqual(lineExpected,
            registry.QueryInLine(center, new(1, 0), 10, 2, map: map).ToArray());
        Assert.Contains(units[7], lineExpected);
        Assert.DoesNotContain(units[8], lineExpected);
        Assert.Contains(units[9], lineExpected);
        Assert.DoesNotContain(units[10], lineExpected);
        Assert.DoesNotContain(units[11], lineExpected);
        TestContext.WriteLine($"Mapped={mapped}: circle and line agree at exact boundaries and adjacent doubles.");
    }

    [TestMethod]
    [DataRow(100d)]
    [DataRow(5000d)]
    public void WrappedMapRetainsRawCoordinatesAtBothSidesOfSeam(double size)
    {
        var context = new Context();
        var registry = new UnitSpatialRegistry();
        var map = new MapRuntime(new MapDefinition("world", "world", size, size, "Overworld", "Torus"));
        Unit right = CreateUnit(context, new(size - 2, 50));
        Unit zero = CreateUnit(context, new(0, 50));
        Unit width = CreateUnit(context, new(size, 50));
        Unit one = CreateUnit(context, new(1, 50));
        foreach (Unit unit in new[] { right, zero, width, one })
        {
            registry.Register(unit);
            map.AddObject(unit.MapObject!);
        }
        if (size > 100)
        {
            AddDistantUnits(context, registry, map, 500, 50);
            Assert.IsFalse(map.PrefersSpatialScan(new(size - 7, 45), new(size + 3, 55)));
        }
        else AddDistantUnits(context, registry);

        Assert.AreEqual(3, map.Space.Distance(right.MapObject!.Position, one.MapObject!.Position));
        CollectionAssert.AreEqual(new[] { right, width },
            registry.QueryInRadius(new(size - 2, 50), 5, map: map).ToArray());
        CollectionAssert.AreEqual(new[] { right, width },
            registry.QueryInLine(new(size - 2, 50), new(1, 0), 2, 0, map: map).ToArray());
        TestContext.WriteLine($"Visibility wraps across x=0/{size}; circle and line targeting use raw coordinates.");
    }

    [TestMethod]
    public void ControlTransitionsKeepMaplessAndMultipleRepresentationsInSync()
    {
        var context = new Context();
        var registry = new UnitSpatialRegistry();
        Unit unit = CreateUnit(context, new(19, 19));
        registry.Register(unit);
        AddDistantUnits(context, registry);
        var firstMap = new MapRuntime(500, 500);
        var secondMap = new MapRuntime(500, 500);
        Assert.Contains(unit, registry.QueryInRadius(new(19, 19), 1));

        MapObject first = unit.MapObjects.Enter(firstMap, new(70, 70), TimeSpan.Zero, keepSourceVisible: true);
        Assert.IsEmpty(registry.QueryInRadius(new(19, 19), 1));
        Assert.Contains(unit, registry.QueryInRadius(new(70, 70), 1, map: firstMap));
        unit.MapObjects.Enter(secondMap, new(130, 130), TimeSpan.Zero, keepSourceVisible: true);
        Assert.Contains(unit, registry.QueryInRadius(new(70, 70), 1, map: firstMap));
        Assert.Contains(unit, registry.QueryInRadius(new(130, 130), 1, map: secondMap));

        unit.MapObjects.Return(TimeSpan.Zero);
        Assert.IsEmpty(registry.QueryInRadius(new(130, 130), 1, map: secondMap));
        unit.MapObjects.Return(TimeSpan.Zero);
        Assert.IsEmpty(registry.QueryInRadius(new(70, 70), 1, map: firstMap));
        Assert.Contains(unit, registry.QueryInRadius(new(19, 19), 1));

        unit.MapObjects.Enter(firstMap, new(70, 70), TimeSpan.Zero, keepSourceVisible: true);
        Assert.IsTrue(firstMap.RemoveObject(first));
        Assert.Contains(unit, registry.QueryInRadius(new(19, 19), 1));
        Assert.IsEmpty(registry.QueryInRadius(new(70, 70), 1, map: firstMap));
        TestContext.WriteLine("Control entry, return and removal preserved mapless and map-local candidate state.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void LineSelectorKeepsRegistrationOrderForEqualDistanceCollisions(bool mapped)
    {
        var context = new Context();
        var registry = new UnitSpatialRegistry();
        MapRuntime? map = mapped ? new MapRuntime(20000, 20000) : null;
        Unit source = CreateUnit(context, new(60, 63));
        Unit first = CreateUnit(context, new(65, 64));
        Unit second = CreateUnit(context, new(65, 62));
        Unit far = CreateUnit(context, new(70, 63));
        Unit ally = CreateUnit(context, new(64, 63));
        source.TeamId = ally.TeamId = 1;
        first.TeamId = second.TeamId = far.TeamId = 2;
        foreach (Unit unit in new[] { far, first, source, ally, second })
        {
            registry.Register(unit);
            map?.AddObject(unit.MapObject!);
        }
        AddDistantUnits(context, registry, map);
        if (map != null)
            Assert.IsFalse(map.PrefersSpatialScan(new(58, 61), new(82, 65)));
        var targeting = new AbilityTargetingContext { Source = source, TargetPoint = new(80, 63) };
        var selector = new LineSelector { Range = 20, Width = 4, Filter = TargetFilter.Enemy, MaxTargets = 2 };

        CollectionAssert.AreEqual(new[] { first, second }, selector.ResolveTargets(targeting, registry).ToArray());
        selector.StopAtFirstTarget = true;
        CollectionAssert.AreEqual(new[] { first }, selector.ResolveTargets(targeting, registry).ToArray());
        TestContext.WriteLine($"Mapped={mapped}: equal-distance collision ties and target limits preserved registration order.");
    }

    private static void AddDistantUnits(Context context, UnitSpatialRegistry registry, MapRuntime? map = null,
        double firstX = 1000, double spacing = 100)
    {
        for (int index = 0; index < 80; index++)
        {
            Unit unit = CreateUnit(context, new(firstX + index * spacing, 1000));
            registry.Register(unit);
            map?.AddObject(unit.MapObject!);
        }
    }

    private static Unit CreateUnit(Context context, Vector2D position)
    {
        var unit = new Unit
        {
            Name = "unit",
            ArchetypeId = "unit",
            Property = new UnitProperty { LifePoint = 100, LifePointMax = 100 },
            Abilities = new UnitAbilityManager(),
            EventReceiver = context.Factory.NewEventReceiver(),
        };
        unit.EventReceiver.AddHandler(new AbilityInstallHandler(), new UnitActionGateHandler(), new AbilityLifecycleHandler());
        context.AddEventReceiver(unit.EventReceiver);
        unit.Initialize();
        unit.MapObjects.Initialize(new MapObject(unit.MapObjects.UnitId, position, unit.EventReceiver));
        return unit;
    }
}
