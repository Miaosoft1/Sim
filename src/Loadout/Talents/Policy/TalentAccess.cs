using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Talents.EventData;
using Miaosoft.Sim.Loadout.Talents.Handler;
using Miaosoft.Sim.Loadout.Talents.Model;

namespace Miaosoft.Sim.Loadout.Talents.Policy;

internal static class TalentAccess
{
    public static void Validate(Context context, UnitTalents talents, Unit owner, EventReceiver? receiver)
    {
        if (!context.IsProcessingEvent || !ReferenceEquals(talents.Owner, owner) ||
            !ReferenceEquals(owner.EventReceiver.Context, context) ||
            !ReferenceEquals(receiver, owner.EventReceiver) || !context.ContainsReceiver(receiver))
            throw new InvalidOperationException("Talent operations require their owner in the executing context.");
        TalentDirectoryHandler[] handlers = owner.EventReceiver.GetHandlers<TalentDirectoryHandler>().ToArray();
        if (handlers.Length != 1 || !ReferenceEquals(handlers[0].Talents, talents))
            throw new InvalidOperationException("Talent operations require the unique current talent directory.");
    }

    public static void ValidateTree(Context context, UnitTalents talents, TalentTreeInstance tree)
    {
        if (!ReferenceEquals(tree.Context, context) || !ReferenceEquals(tree.Directory, talents) ||
            !talents.Trees.TryGetValue(tree.Definition.Id, out TalentTreeInstance? current) ||
            !ReferenceEquals(current, tree))
            throw new InvalidOperationException("A talent tree belongs to its original owner, directory and context.");
        TalentTreeHandler[] handlers = tree.Owner.EventReceiver.GetHandlers<TalentTreeHandler>()
            .Where(handler => ReferenceEquals(handler.Tree, tree)).ToArray();
        if (tree.IsAuthorized ? handlers.Length != 1 || !ReferenceEquals(handlers[0], tree.Observer) : handlers.Length != 0)
            throw new InvalidOperationException("The talent tree's authorization and current handler disagree.");
        foreach (TalentTreeInstance.Grant grant in tree.Runtime.Value.Grants)
            if (grant.Source.InstanceId != grant.Id || grant.Source.Category != grant.Category)
                throw new InvalidOperationException("A talent authorization source changed its identity.");
    }

    public static TalentChangeResultEvent Result(
        Event ev, Guid operationId, Unit owner, string treeId,
        TalentChangeOperation operation, TalentChangeFailure failure) =>
        ev.Context.Factory.NewEvent<TalentChangeResultEvent>() with
        {
            OperationId = operationId, Owner = owner, TreeId = treeId, Operation = operation, Failure = failure,
            Receiver = owner.EventReceiver, IsScopeDetached = true, Priority = ev.Priority
        };
}
