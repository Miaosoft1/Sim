namespace Miaosoft.Sim.Combat.Map;

public readonly record struct MapCloseRequest(string Reason, TimeSpan Now, Guid? Authorization = null);

public interface IMapClosingPolicy
{
    void Validate(MapRuntime map, MapCloseRequest request);
    void Execute(MapRuntime map, MapCloseRequest request);
}
