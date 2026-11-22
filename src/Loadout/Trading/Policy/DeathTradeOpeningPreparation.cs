using System.Collections.ObjectModel;
using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Trading.EventData;
using Miaosoft.Sim.Loadout.Trading.Model;

namespace Miaosoft.Sim.Loadout.Trading.Policy;

internal sealed class DeathTradeOpeningPreparation(DeathResolvedEvent death, DeathTradeDirectory directory)
    : IAbilitySourceChangePreparation
{
    public AbilitySourceChangePlan Prepare(Context context, Unit coordinator, Guid operationId, AtomicTransaction transaction)
    {
        DeathTradeAccess.ValidateDirectory(context, directory);
        if (!ReferenceEquals(context, death.Context) || !ReferenceEquals(coordinator, death.Victim) ||
            !ReferenceEquals(coordinator, directory.Inventory.Owner) || death.Receiver != coordinator.EventReceiver ||
            operationId != death.DeathId || operationId == Guid.Empty || coordinator.IsAlive ||
            coordinator.CurrentDeathId != death.DeathId)
            throw new InvalidOperationException("Death grants require the actual committed victim and death episode.");
        if (transaction.Read(directory.Runtime).DeathId == death.DeathId)
            return new()
            {
                ShouldCommit = false,
                Completions = [DeathTradeAccess.Result(death, operationId, DeathTradeOperation.Authorize, death.DeathId,
                    DeathTradeFailure.AlreadyAuthorized, coordinator)]
            };
        var items = transaction.Read(directory.Inventory.Contents).Select(placement => placement.Item).ToArray();
        HashSet<Guid> ids = items.Select(item => item.InstanceId).ToHashSet();
        var authorizations = new Dictionary<Guid, DeathTradeAuthorization>();
        var completions = new List<Event>();
        foreach (DeathTradeAllocation allocation in directory.Distribution.Allocate(death, Array.AsReadOnly(items)))
        {
            if (allocation == null || allocation.Beneficiary == null || allocation.ItemIds == null ||
                ReferenceEquals(allocation.Beneficiary, coordinator) ||
                !allocation.Beneficiary.CanParticipate || !context.ContainsReceiver(allocation.Beneficiary.EventReceiver) ||
                allocation.ItemIds.Distinct().Count() != allocation.ItemIds.Count || allocation.ItemIds.Any(id => !ids.Contains(id)) ||
                authorizations.ContainsKey(allocation.Beneficiary.Id))
                throw new InvalidOperationException("Death distribution returned an invalid beneficiary, duplicate allocation or unowned item.");
            var authorization = new DeathTradeAuthorization(directory, death, allocation.Beneficiary, allocation.ItemIds);
            authorizations.Add(allocation.Beneficiary.Id, authorization);
            completions.Add(context.Factory.NewEvent<DeathTradeAuthorizedEvent>() with
            {
                Authorization = authorization, Receiver = allocation.Beneficiary.EventReceiver,
                IsScopeDetached = true, Priority = death.Priority
            });
        }
        transaction.Stage(directory.Runtime, new(death.DeathId,
            new ReadOnlyDictionary<Guid, DeathTradeAuthorization>(authorizations), false));
        return new() { Completions = completions };
    }
}
