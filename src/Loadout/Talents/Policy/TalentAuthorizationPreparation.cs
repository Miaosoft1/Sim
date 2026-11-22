using System.Collections.ObjectModel;
using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Talents.EventData;
using Miaosoft.Sim.Loadout.Talents.Model;

namespace Miaosoft.Sim.Loadout.Talents.Policy;

internal sealed class TalentAuthorizationPreparation(TalentAuthorizationRequestEvent request, UnitTalents talents)
    : IAbilitySourceChangePreparation
{
    public AbilitySourceChangePlan Prepare(Context context, Unit coordinator, Guid operationId, AtomicTransaction transaction)
    {
        TalentAuthorizationRequestEvent ev = request;
        if (!ReferenceEquals(ev.Context, context) || operationId != ev.OperationId || operationId == Guid.Empty)
            throw new InvalidOperationException("A talent authorization requires its original request.");
        TalentAccess.Validate(context, talents, coordinator, ev.Receiver);
        if (!ReferenceEquals(ev.Owner, coordinator))
            throw new InvalidOperationException("The authorization does not address the talent owner.");
        return Stage(ev, operationId, talents, ev.Definition, [(ev.Grant, ev.Authorize)], transaction, ev.ExpectedRevision);
    }

    internal static AbilitySourceChangePlan Stage(
        Event ev, Guid operationId, UnitTalents talents, TalentTreeDefinition definition,
        IReadOnlyList<(IAbilitySource Source, bool Authorize)> changes, AtomicTransaction transaction,
        long? expectedRevision = null)
    {
        Context context = ev.Context;
        Unit coordinator = talents.Owner;
        TalentAccess.Validate(context, talents, coordinator, coordinator.EventReceiver);
        ArgumentNullException.ThrowIfNull(definition);
        if (changes.Count == 0) throw new InvalidOperationException("An authorization batch must change a real grant.");
        foreach (var change in changes)
        {
            ArgumentNullException.ThrowIfNull(change.Source);
            if (change.Source.InstanceId == Guid.Empty || string.IsNullOrWhiteSpace(change.Source.Category) ||
                change.Source.Category != change.Source.Category.Trim())
                throw new InvalidOperationException("A talent authorization needs a real stable source identity.");
        }
        IReadOnlyDictionary<string, TalentTreeInstance> trees = transaction.Read(talents.Contents);
        bool existing = trees.TryGetValue(definition.Id, out TalentTreeInstance? tree);
        if (existing)
        {
            TalentAccess.ValidateTree(context, talents, tree!);
            if (!ReferenceEquals(tree!.Definition, definition))
                throw new InvalidOperationException("One owner cannot mix different catalogs for the same talent tree.");
        }
        AbilitySourceChangePlan Reject(TalentChangeFailure failure) => new()
        {
            ShouldCommit = false,
            Completions = changes.Select(change => TalentAccess.Result(ev, operationId, coordinator, definition.Id,
                change.Authorize ? TalentChangeOperation.Grant : TalentChangeOperation.Revoke, failure)).ToArray()
        };
        if (expectedRevision is { } expected && expected != (tree?.Revision ?? 0))
            return Reject(TalentChangeFailure.StaleRevision);
        if (!existing && changes.All(change => !change.Authorize)) return Reject(TalentChangeFailure.GrantNotFound);
        if (!talents.AvailableTrees.TryGetValue(definition.Id, out TalentTreeInstance? retained) ||
            !ReferenceEquals(retained.Definition, definition))
            throw new InvalidOperationException("Select the immutable talent definition when composing this owner's feature.");
        tree ??= retained;
        TalentTreeInstance.State state = transaction.Read(tree.Runtime);
        var afterGrants = state.Grants.ToList();
        foreach (var change in changes)
        {
            TalentTreeInstance.Grant? prior = afterGrants.SingleOrDefault(grant => grant.Id == change.Source.InstanceId);
            if (prior != null && !ReferenceEquals(prior.Source, change.Source))
                throw new InvalidOperationException("Distinct talent sources cannot share the same identity.");
            if (change.Authorize && prior != null) return Reject(TalentChangeFailure.AlreadyGranted);
            if (!change.Authorize && prior == null) return Reject(TalentChangeFailure.GrantNotFound);
            if (change.Authorize) afterGrants.Add(new(change.Source, change.Source.InstanceId, change.Source.Category));
            else afterGrants.Remove(prior!);
        }
        TalentTreeInstance.Grant[] grants = afterGrants.ToArray();
        bool wasActive = state.Grants.Length != 0;
        bool active = grants.Length != 0;
        transaction.Stage(tree.Runtime, state with
        {
            Grants = grants,
            AuthorityVersion = wasActive == active ? state.AuthorityVersion : checked(state.AuthorityVersion + 1)
        });
        if (!existing)
        {
            var after = trees.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            after.Add(tree.Definition.Id, tree);
            transaction.Update(talents.Contents, _ => new ReadOnlyDictionary<string, TalentTreeInstance>(after));
        }
        AbilitySourceInstance[] sources = tree.NodeSources.Values.Where(source => source.IsMaterialized)
            .Concat(tree.Definition.GrowthRules.Count == 0 ? [] : new[] { tree.GrowthSource }).ToArray();
        return new()
        {
            Assignments = wasActive == active ? [] :
                sources.Select(source => new AbilitySourceAssignment(source, active ? coordinator : null, source.Revision)).ToArray(),
            RegistrationChanges = wasActive == active ? [] :
                [new(coordinator, active ? [] : [tree.Observer], active ? [tree.Observer] : [])],
            Completions = changes.Select(change => TalentAccess.Result(ev, operationId, coordinator, definition.Id,
                change.Authorize ? TalentChangeOperation.Grant : TalentChangeOperation.Revoke, TalentChangeFailure.None)).ToArray()
        };
    }
}
