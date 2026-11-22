using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Talents.EventData;
using Miaosoft.Sim.Loadout.Talents.Model;
using Miaosoft.Sim.Loadout.Talents.Policy;

namespace Miaosoft.Sim.Loadout.Talents.Handler;

public sealed class TalentDirectoryHandler(UnitTalents talents) : Miaosoft.Sim.Combat.Skill.Handler.Handler
{
    public UnitTalents Talents { get; } = talents ?? throw new ArgumentNullException(nameof(talents));
    public override string Id { get; set; } = nameof(TalentDirectoryHandler);

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<Event> OnAuthorization(TalentAuthorizationRequestEvent ev)
    {
        if (ev.CancellationToken.IsCancellationRequested) yield break;
        TalentAccess.Validate(ev.Context, Talents, ev.Owner, ev.Receiver);
        yield return ev.Context.Factory.NewEvent<AbilitySourcesChangeRequestEvent>() with
        {
            Coordinator = ev.Owner, OperationId = ev.OperationId,
            Preparation = new TalentAuthorizationPreparation(ev, Talents),
            Receiver = ev.Receiver, CancellationTokenSource = ev.CancellationTokenSource, Priority = ev.Priority
        };
    }

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<Event> OnAllocate(TalentNodeAllocateRequestEvent ev) => Change(ev);

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<Event> OnAward(TalentPointsAwardRequestEvent ev) => Change(ev);

    private IEnumerable<Event> Change(TalentTreeRequestEvent ev)
    {
        if (ev.CancellationToken.IsCancellationRequested) yield break;
        TalentAccess.Validate(ev.Context, Talents, ev.Tree.Owner, ev.Receiver);
        yield return ev.Context.Factory.NewEvent<AbilitySourcesChangeRequestEvent>() with
        {
            Coordinator = ev.Tree.Owner, OperationId = ev.OperationId,
            Preparation = new TalentProgressPreparation(ev, Talents),
            Receiver = ev.Receiver, CancellationTokenSource = ev.CancellationTokenSource, Priority = ev.Priority
        };
    }
}
