using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Combat.Map.EventData;

public sealed record MapVisibilityChangedEvent : Event, Visibility.IVisibilityInvalidatingEvent
{
    public MapObject? Body { get; init; }
    public bool RefreshVisibilityRules { get; init; } = true;
}
