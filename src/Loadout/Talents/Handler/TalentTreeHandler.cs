using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Talents.EventData;
using Miaosoft.Sim.Loadout.Talents.Model;
using Miaosoft.Sim.Loadout.Talents.Policy;

namespace Miaosoft.Sim.Loadout.Talents.Handler;

public sealed class TalentTreeHandler : Miaosoft.Sim.Combat.Skill.Handler.Handler,
    IRequiresCurrentRegistration, IRuntimeInstanceHandler
{
    internal TalentTreeHandler(TalentTreeInstance tree)
    {
        Tree = tree;
        Id = $"talent-tree:{tree.Definition.Id}";
    }

    public TalentTreeInstance Tree { get; }
    public Guid RuntimeInstanceId => Tree.InstanceId;
    public bool RequiresCurrentRegistration(IEvent ev) => true;

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<Event> OnGrowth(TalentGrowthObservationEvent ev)
    {
        if (ev.CancellationToken.IsCancellationRequested) yield break;
        if (!ReferenceEquals(ev.Tree, Tree))
            throw new InvalidOperationException("A growth observation must address its own tree.");
        yield return ev.Context.Factory.NewEvent<AbilitySourcesChangeRequestEvent>() with
        {
            Coordinator = Tree.Owner, OperationId = ev.OperationId,
            Preparation = new TalentProgressPreparation(ev, Tree.Directory),
            Receiver = ev.Receiver, CancellationTokenSource = ev.CancellationTokenSource, Priority = ev.Priority
        };
    }
}
