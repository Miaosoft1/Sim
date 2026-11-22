using Miaosoft.Sim.Combat.Skill.EventData;

namespace Miaosoft.Sim.Combat.Skill.Handler;

public sealed class UnitRetirementHandler : Handler
{
    public UnitRetirementHandler() => Id = nameof(UnitRetirementHandler);

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<Event> OnRetirementRequest(UnitRetirementRequestEvent ev)
    {
        if (ev.CancellationToken.IsCancellationRequested || ev.Receiver != ev.Unit.EventReceiver ||
            ev.Unit.IsRetired)
            yield break;
        ev.Unit.Retire();
        yield return ev.Context.Factory.NewEvent<UnitRetiredEvent>() with
        {
            Unit = ev.Unit, Receiver = ev.Unit.EventReceiver, TimeSpan = TimeSpan.Zero
        };
    }
}
