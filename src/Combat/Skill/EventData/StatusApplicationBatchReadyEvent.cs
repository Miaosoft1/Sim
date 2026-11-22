namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record StatusApplicationBatchReadyEvent : Event, IEventProcessingFailure
{
    internal StatusReleaseBatch Batch { get; init; } = null!;
    void IEventProcessingFailure.OnProcessingFailure(Context context) => Batch?.Abort(context);
}

internal sealed class StatusReleaseBatch(
    StatusAbility ability, TriggeredAbilityReleaseEvent release, AbilityTriggerInvokeEvent trigger)
{
    private readonly Dictionary<Unit, AbilityInstallEvent> installations = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Unit, AbilityInstallRequestEvent> requests = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Unit, AbilityInstallEvent> expectedInstallations = new(ReferenceEqualityComparer.Instance);

    public StatusAbility Ability { get; } = ability;
    public TriggeredAbilityReleaseEvent Release { get; } = release;
    public AbilityTriggerInvokeEvent Trigger { get; } = trigger;
    public IReadOnlyList<Unit> Targets { get; private set; } = [];
    public int Level { get; private set; }
    public string? PayloadId { get; private set; }
    public TimeSpan Duration { get; private set; }
    public AbilityTimeBasis TimeBasis { get; private set; }
    public AbilityLabel Labels { get; private set; }
    public IReadOnlyList<PeriodicEffectApplication> PeriodicEffects { get; private set; } = [];
    public bool IsAborted { get; private set; }
    public bool IsCommitted { get; private set; }
    public IReadOnlyList<AbilityInstallEvent> Installations =>
        Targets.Select(target => installations[target]).ToArray();

    public void BeginRound(IReadOnlyList<Unit> targets)
    {
        if (IsAborted || IsCommitted)
            throw new InvalidOperationException("A finished status release cannot begin another round.");
        if (targets.Count == 0 || targets.Distinct(ReferenceEqualityComparer.Instance).Count() != targets.Count)
            throw new InvalidOperationException("A coordinated status release requires distinct targets.");
        Targets = targets.ToArray();
        Level = Ability.Level;
        PayloadId = Ability.PayloadId ?? Ability.Id;
        Duration = Ability.Duration;
        TimeBasis = Ability.TimeBasis;
        Labels = Ability.Labels;
        PeriodicEffects = Ability.PeriodicEffects.ToArray();
        installations.Clear();
        requests.Clear();
        expectedInstallations.Clear();
    }

    public bool Matches(IReadOnlyList<Unit> targets) =>
        targets.Count == Targets.Count && targets.SequenceEqual(Targets, ReferenceEqualityComparer.Instance) &&
        Level == Ability.Level && PayloadId == (Ability.PayloadId ?? Ability.Id) &&
        Duration == Ability.Duration && TimeBasis == Ability.TimeBasis && Labels == Ability.Labels &&
        PeriodicEffects.SequenceEqual(Ability.PeriodicEffects);

    public void AddRequest(AbilityInstallRequestEvent request)
    {
        if (!Targets.Contains(request.Target, ReferenceEqualityComparer.Instance) ||
            !requests.TryAdd(request.Target, request))
            throw new InvalidOperationException("A status release received an unexpected or duplicate request.");
    }

    public void ValidateRequest(AbilityInstallRequestEvent request)
    {
        if (IsCommitted || !requests.TryGetValue(request.Target, out AbilityInstallRequestEvent? expected) ||
            !ReferenceEquals(expected, request))
            throw new InvalidOperationException("The status request does not belong to the current release round.");
    }

    public void AddExpectedInstallation(AbilityInstallRequestEvent request, AbilityInstallEvent installation)
    {
        ValidateRequest(request);
        if (!expectedInstallations.TryAdd(request.Target, installation))
            throw new InvalidOperationException("A status request produced more than one installation.");
    }

    public void ValidateInstallation(AbilityInstallEvent installation)
    {
        if (IsCommitted || !expectedInstallations.TryGetValue(installation.Target, out AbilityInstallEvent? expected) ||
            !ReferenceEquals(expected, installation) || installations.ContainsKey(installation.Target))
            throw new InvalidOperationException("The status installation does not belong to the current release round.");
    }

    public bool Add(AbilityInstallEvent installation)
    {
        ValidateInstallation(installation);
        installations.Add(installation.Target, installation);
        return installations.Count == Targets.Count;
    }

    public void Abort(Context context)
    {
        if (IsAborted || IsCommitted) return;
        Reject();
        Event finished = TriggeredAbilityGate.Finish(Release, Release.Request) with { IsScopeDetached = true };
        foreach (IEvent _ in context.Dispatch(finished)) { }
    }

    public void Reject()
    {
        if (IsAborted || IsCommitted)
            throw new InvalidOperationException("The status release has already finished.");
        IsAborted = true;
        UnitActionGateRequestEvent request = Release.Request;
        (request.AbilityGate ??
            throw new InvalidOperationException("A coordinated status release requires its gate."))
            .CancelReservation(request.ActivationId);
    }

    public void MarkCommitted()
    {
        if (IsAborted || IsCommitted)
            throw new InvalidOperationException("The status release has already finished.");
        IsCommitted = true;
    }
}
