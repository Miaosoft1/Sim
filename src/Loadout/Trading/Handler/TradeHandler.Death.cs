using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Trading.EventData;
using Miaosoft.Sim.Loadout.Trading.Policy;

namespace Miaosoft.Sim.Loadout.Trading.Handler;

public sealed partial class TradeHandler
{
    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<Event> OnDeathInspect(DeathTradeInspectRequestEvent ev) => ChangeDeathTrade(ev);

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<Event> OnDeathClaim(DeathTradeClaimRequestEvent ev) => ChangeDeathTrade(ev);

    private IEnumerable<Event> ChangeDeathTrade(DeathTradeRequestEvent ev)
    {
        if (ev.CancellationToken.IsCancellationRequested) yield break;
        if (!ReferenceEquals(ev.Actor, Inventory.Owner) || ev.Receiver != Inventory.Owner.EventReceiver)
            throw new InvalidOperationException("A death trade must address its beneficiary's inventory.");
        yield return ev.Context.Factory.NewEvent<AbilitySourcesChangeRequestEvent>() with
        {
            Coordinator = ev.Actor, OperationId = ev.OperationId,
            Preparation = new DeathTradeClaimPreparation(ev, Inventory),
            Receiver = ev.Receiver, Priority = ev.Priority, CancellationTokenSource = ev.CancellationTokenSource
        };
    }
}
