namespace Miaosoft.Sim.Combat.Map.Visibility;

public readonly record struct MapVisibilityTarget(MapObject Body, double Stealth);

public sealed record MapVisibilityScene(
    IReadOnlyList<string> Teams,
    IReadOnlyList<MapVisibilityObserver> Observers,
    IReadOnlyList<MapVisibilityTarget> Targets);
