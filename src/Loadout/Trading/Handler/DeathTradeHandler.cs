using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Trading.EventData;
using Miaosoft.Sim.Loadout.Trading.Model;
using Miaosoft.Sim.Loadout.Trading.Policy;

namespace Miaosoft.Sim.Loadout.Trading.Handler;

public sealed class DeathTradeHandler(DeathTradeDirectory directory)
    : Miaosoft.Sim.Combat.Skill.Handler.Handler, IRequiresCurrentRegistration
{
    public DeathTradeDirectory Directory { get; } = directory ?? throw new ArgumentNullException(nameof(directory));
    public override string Id { get; set; } = nameof(DeathTradeHandler);
    public bool RequiresCurrentRegistration(IEvent ev) => true;

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<Event> OnConfirmedDeath(DeathResolvedEvent ev)
    {
        if (ev.CancellationToken.IsCancellationRequested) yield break;
        yield return ev.Context.Factory.NewEvent<AbilitySourcesChangeRequestEvent>() with
        {
            Coordinator = Directory.Inventory.Owner, OperationId = ev.DeathId,
            Preparation = new DeathTradeOpeningPreparation(ev, Directory),
            Receiver = ev.Receiver, Priority = ev.Priority, CancellationTokenSource = ev.CancellationTokenSource
        };
    }

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<Event> OnCleanup(DeathTradeCleanupRequestEvent ev)
    {
        if (ev.CancellationToken.IsCancellationRequested) yield break;
        if (!ReferenceEquals(ev.Directory, Directory))
            throw new InvalidOperationException("Death-trade cleanup must address its own directory.");
        yield return ev.Context.Factory.NewEvent<AbilitySourcesChangeRequestEvent>() with
        {
            Coordinator = Directory.Inventory.Owner, OperationId = ev.OperationId,
            Preparation = new DeathTradeCleanupPreparation(ev),
            Receiver = ev.Receiver, Priority = ev.Priority, CancellationTokenSource = ev.CancellationTokenSource
        };
    }
}
