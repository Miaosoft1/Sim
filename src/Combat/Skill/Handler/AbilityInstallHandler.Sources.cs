using Miaosoft.Sim.Combat.Skill.EventData;

namespace Miaosoft.Sim.Combat.Skill.Handler;

public partial class AbilityInstallHandler
{
    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<Event> OnSourcesChange(AbilitySourcesChangeRequestEvent ev)
    {
        if (ev.CancellationToken.IsCancellationRequested) yield break;
        if (ev.Receiver != ev.Coordinator.EventReceiver || !ev.Context.IsProcessingEvent ||
            ev.OperationId == Guid.Empty || !ev.Context.ContainsReceiver(ev.Coordinator.EventReceiver) ||
            !ReferenceEquals(ev.Coordinator.EventReceiver.Context, ev.Context) ||
            ev.Coordinator.EventReceiver.GetHandlers<AbilityInstallHandler>().Count != 1)
            throw new InvalidOperationException("A source change requires its coordinator and execution context.");
        bool prepared = ev.Preparation != null;
        if (ev.Preparation is { } preparation)
        {
            if (ev.Materialize.Count + ev.Assignments.Count + ev.LevelChanges.Count +
                ev.RegistrationChanges.Count + ev.Completions.Count != 0)
                throw new InvalidOperationException("A prepared source operation cannot also contain direct changes.");
            AbilitySourceChangePlan plan = preparation.Prepare(
                ev.Context, ev.Coordinator, ev.OperationId, ev.Transaction);
            ValidateSourceCompletions(ev.Context, plan.Completions);
            if (!plan.ShouldCommit)
            {
                if (plan.Materialize.Count + plan.Assignments.Count + plan.LevelChanges.Count +
                    plan.RegistrationChanges.Count != 0)
                    throw new InvalidOperationException("A rejected preparation cannot contain source changes.");
                foreach (Event completion in plan.Completions) yield return completion;
                yield break;
            }
            ev = ev with
            {
                Materialize = plan.Materialize, Assignments = plan.Assignments,
                LevelChanges = plan.LevelChanges, RegistrationChanges = plan.RegistrationChanges,
                Completions = plan.Completions, Preparation = null
            };
        }
        if (!prepared && ev.Materialize.Count + ev.Assignments.Count + ev.LevelChanges.Count +
            ev.RegistrationChanges.Count == 0)
            throw new InvalidOperationException("A source change requires at least one operation.");
        if (ev.RegistrationChanges.Any(change => change == null || change.Target == null ||
                change.Remove == null || change.Add == null ||
                change.Remove.Concat(change.Add).Any(part => part == null || part is IAbilityRuntimePart)) ||
            ev.RegistrationChanges.Select(change => change.Target).Distinct().Count() != ev.RegistrationChanges.Count)
            throw new InvalidOperationException("Domain registrations must identify distinct units and non-ability handlers.");
        if (ev.Assignments.Any(change => change == null || change.Source == null) ||
            ev.LevelChanges.Any(change => change == null || change.Source == null || change.Level < 1 ||
                change.AbilityInstanceId == Guid.Empty) ||
            ev.Materialize.Any(source => source == null) ||
            ev.Assignments.Select(change => change.Source).Distinct().Count() != ev.Assignments.Count ||
            ev.LevelChanges.Select(change => change.AbilityInstanceId).Distinct().Count() != ev.LevelChanges.Count)
            throw new InvalidOperationException("The source change contains an invalid or duplicate operation.");

        ValidateSourceCompletions(ev.Context, ev.Completions);
        foreach (AbilitySourceAssignment assignment in ev.Assignments)
        {
            if (assignment.Holder != null && assignment.Source.FixedOwner is { } fixedOwner &&
                !ReferenceEquals(assignment.Holder, fixedOwner))
                throw new InvalidOperationException("An owner-bound source cannot be granted to a different unit.");
            if (assignment.ExpectedRevision is { } revision && revision != assignment.Source.Revision)
                throw new InvalidOperationException("The ability source changed while its operation was prepared.");
        }
        AtomicTransaction transaction = ev.Transaction;
        AbilitySourceInstance[] sources = ev.Materialize.Concat(ev.Assignments.Select(change => change.Source))
            .Concat(ev.LevelChanges.Select(change => change.Source)).Distinct().ToArray();
        var states = new Dictionary<AbilitySourceInstance, AbilitySourceInstance.State>();
        var units = new List<Unit>();
        foreach (AbilitySourceInstance source in sources)
        {
            AbilitySourceInstance.State state = transaction.Read(source.Runtime);
            if (state.Context != null && !ReferenceEquals(state.Context, ev.Context))
                throw new InvalidOperationException("An ability source cannot move between contexts.");
            if (state.Holder != null)
            {
                if (!state.Holder.Abilities.Sources.Contains(source))
                    throw new InvalidOperationException("The source holder and registration disagree.");
                if (!units.Contains(state.Holder)) units.Add(state.Holder);
            }
            if (state.Groups == null)
                state = state with { Context = ev.Context, Groups = MaterializeSource(source, ev.Context) };
            states.Add(source, state);
        }
        foreach (AbilitySourceAssignment assignment in ev.Assignments)
        {
            if (assignment.Holder is { } holder && !units.Contains(holder)) units.Add(holder);
            states[assignment.Source] = states[assignment.Source] with { Holder = assignment.Holder };
        }
        foreach (AtomicUnitAbilityChange change in ev.RegistrationChanges)
            if (!units.Contains(change.Target)) units.Add(change.Target);
        foreach (Unit unit in units)
            if (!ev.Context.ContainsReceiver(unit.EventReceiver) || !ReferenceEquals(unit.EventReceiver.Context, ev.Context) ||
                unit.EventReceiver.GetHandlers<AbilityInstallHandler>().Count != 1)
                throw new InvalidOperationException("Each source participant requires one installer in the same context.");

        foreach (AbilitySourceLevelChange change in ev.LevelChanges)
        {
            AbilityBase ability = states[change.Source].Groups!
                .Select(group => group.Ability).SingleOrDefault(ability => ability.RuntimeInstanceId == change.AbilityInstanceId)
                ?? throw new InvalidOperationException("The level change does not identify this source's ability.");
            ability.StageLevel(transaction, change.Level);
        }
        foreach ((AbilitySourceInstance source, AbilitySourceInstance.State state) in states)
        {
            foreach (IAbilityRuntimePart part in state.Groups!.SelectMany(group => group.Parts).OfType<IAbilityRuntimePart>())
            {
                part.Binding.StageBind(transaction, source);
                part.Binding.StageAuthority(transaction, ev.Context, state.Holder?.Id);
            }
            transaction.Stage(source.Runtime, state);
        }

        var changes = new List<AtomicUnitAbilityChange>();
        var unregistered = new List<AbilityBase>();
        foreach (Unit unit in units)
        {
            AbilitySourceInstance[] before = transaction.Read(unit.Abilities.SourceRegistrations);
            var after = before.Where(source => !states.TryGetValue(source, out var state) ||
                ReferenceEquals(state.Holder, unit)).ToList();
            foreach (AbilitySourceInstance source in sources)
                if (ReferenceEquals(states[source].Holder, unit) && !after.Contains(source)) after.Add(source);
            transaction.Stage(unit.Abilities.SourceRegistrations, after.ToArray());
            AbilitySourceInstance.State Read(AbilitySourceInstance source) =>
                states.TryGetValue(source, out var state) ? state : transaction.Read(source.Runtime);
            AbilitySourceInstance.Group[] groups = after.SelectMany(source => Read(source).Groups!).ToArray();
            IHandler[] mounted = unit.EventReceiver.GetHandlers<IHandler>().ToArray();
            var previousParts = before.SelectMany(source => transaction.Read(source.Runtime).Groups!)
                .SelectMany(group => group.Parts).ToHashSet();
            if (mounted.OfType<AbilityBase>().Any(ability => !previousParts.Contains(ability) &&
                groups.Any(group => string.Equals(group.Ability.Id, ability.Id, StringComparison.Ordinal))))
                throw new InvalidOperationException("An unmanaged ability conflicts with a registered source.");
            var selected = new List<AbilitySourceInstance.Group>();
            foreach (IGrouping<string, AbilitySourceInstance.Group> candidates in groups.GroupBy(
                group => group.Ability.Id, StringComparer.Ordinal))
            {
                bool multiple = candidates.First().Ability.AllowMultiple;
                if (candidates.Any(group => group.Ability.AllowMultiple != multiple))
                    throw new InvalidOperationException("The same definition has inconsistent coexistence policies.");
                if (multiple) selected.AddRange(candidates);
                else
                {
                    int highest = candidates.Max(group => group.Ability.ReadLevel(transaction));
                    var strongest = candidates.Where(group => group.Ability.ReadLevel(transaction) == highest).ToArray();
                    selected.Add(strongest.FirstOrDefault(group => mounted.Contains(group.Ability)) ?? strongest[0]);
                }
            }
            var desired = selected.SelectMany(group => group.Parts).ToHashSet();
            AtomicUnitAbilityChange? registration = ev.RegistrationChanges.SingleOrDefault(change => change.Target == unit);
            changes.Add(new AtomicUnitAbilityChange(unit,
                mounted.Where(handler => previousParts.Contains(handler) && !desired.Contains(handler))
                    .Concat(registration?.Remove ?? []).ToArray(),
                selected.SelectMany(group => group.Parts).Where(handler => !mounted.Contains(handler))
                    .Concat(registration?.Add ?? []).ToArray()));
            unregistered.AddRange(groups.Where(group => !selected.Contains(group)).Select(group => group.Ability));
        }
        unregistered.AddRange(states.Values.Where(state => state.Holder == null)
            .SelectMany(state => state.Groups!).Select(group => group.Ability));
        if (ev.CancellationToken.IsCancellationRequested) yield break;
        var atomic = ev.Context.Factory.NewEvent<AtomicAbilityChangeEvent>() with
        {
            Target = ev.Coordinator, Receiver = ev.Receiver, OperationId = ev.OperationId,
            Transaction = transaction, Changes = changes, Causality = ev.Causality,
            UnregisteredAbilities = unregistered.Distinct().ToArray(),
            AdditionalCompletions = ev.Completions,
            Completion = ev.Context.Factory.NewEvent<AbilitySourcesChangedEvent>() with
            {
                OperationId = ev.OperationId, Sources = Array.AsReadOnly(sources),
                Receiver = ev.Receiver, IsScopeDetached = true
            }
        };
        foreach (Event notification in ApplyAtomicAbilityChange(atomic, allowNoUnitChanges: true)) yield return notification;
    }

    private IReadOnlyList<AbilitySourceInstance.Group> MaterializeSource(AbilitySourceInstance source, Context context)
    {
        var groups = new List<AbilitySourceInstance.Group>();
        var instances = new HashSet<IHandler>();
        foreach (AbilitySourceMember member in source.Members)
        {
            IReadOnlyList<IHandler> parts = AbilityFactory?.Invoke(member.AbilityId, member.Level)
                ?? throw new InvalidOperationException("Source creation requires the preset ability factory.");
            AbilityBase[] abilities = parts.OfType<AbilityBase>().ToArray();
            if (abilities.Length != 1 || parts.Any(part => part == null || !instances.Add(part)) ||
                parts.Any(part => part is not IAbilityRuntimePart) ||
                abilities[0].Id != member.AbilityId || abilities[0].Level != member.Level ||
                parts.OfType<TriggerHandler>().Any(trigger => trigger.TargetInstanceId != abilities[0].RuntimeInstanceId ||
                    !string.Equals(trigger.TargetAbilityId, member.AbilityId, StringComparison.Ordinal)))
                throw new InvalidOperationException("A source member must create one fresh ability and its explicitly bound runtime parts.");
            if (parts.Any(part => context.FindHandlerReceivers(part).Count != 0) ||
                parts.OfType<IAbilityRuntimePart>().Any(part => part.Binding.BoundContext != null || part.Binding.Source != null))
                throw new InvalidOperationException("The source factory reused an existing runtime part.");
            groups.Add(new(abilities[0], Array.AsReadOnly(parts.ToArray())));
        }
        return groups.AsReadOnly();
    }

    private static void RejectManagedDefinitionChanges(Unit unit, IEnumerable<string> requestedIds)
    {
        string[] ids = requestedIds.ToArray();
        if (unit.Abilities.Sources.SelectMany(source => source.Abilities)
            .Any(ability => ids.Any(id => AbilityIdMatcher.IsSameOrChild(id, ability.Id))))
            throw new InvalidOperationException("Managed source definitions must change through a source change event.");
    }

    private static void ValidateSourceCompletions(Context context, IReadOnlyList<Event> completions)
    {
        if (completions.Any(completion => completion == null ||
            !ReferenceEquals(completion.Context, context) || completion.Receiver == null ||
            !context.ContainsReceiver(completion.Receiver) || !completion.IsScopeDetached ||
            completion.CommittedOperationId.HasValue))
            throw new InvalidOperationException("Source completions require detached events in the same context.");
    }
}
