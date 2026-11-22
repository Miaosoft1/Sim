using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Combat.Skill.Ability;

internal static class StatusApplicationRequests
{
    public static IEnumerable<AbilityInstallRequestEvent> Create(
        StatusAbility ability, AbilityTriggerInvokeEvent ev, IReadOnlyList<Unit> targets,
        CapturedHandlerTarget abilityTarget, StatusReleaseBatch? batch = null)
    {
        Unit source = ev.Source ?? throw new InvalidOperationException("A triggered status requires a source.");
        foreach (Unit target in targets)
        {
            TimeSpan? expireAt = ability.TimeBasis == AbilityTimeBasis.Simulation && ability.Duration > TimeSpan.Zero
                ? ev.Context.ContextTimeSpan.Add(ability.Duration).Subtract(TimeSpan.FromTicks(1))
                : null;

            StatusApplicationOrigin origin = StatusApplicationOrigin.FromAcceptedAbility(
                ability, source, ev.Context, abilityTarget);
            var request = ev.Context.Factory.NewEvent<AbilityInstallRequestEvent>() with
            {
                CommandEventId = ev.CommandEventId,
                Source = ev.Source,
                Target = target,
                AbilityIds = new[] { ability.PayloadId ?? ability.Id },
                Level = ability.Level,
                Purpose = AbilityInstallPurpose.StatusApplication,
                ApplicationOrigin = origin,
                PeriodicEffects = ability.PeriodicEffects.ToArray(),
                LeaseCancellationPolicy = AbilityLeaseCancellationPolicy.ContinueAfterCommit,
                AdditionalLabels = ability.Labels,
                ExpireAt = expireAt,
                Receiver = target.EventReceiver,
                TimeSpan = TimeSpan.Zero,
                CancellationTokenSource = batch == null ? new CancellationTokenSource() : null,
                IsScopeDetached = true,
                StatusBatch = batch
            };
            origin.BindReleasedRequest(request);
            batch?.AddRequest(request);
            yield return request;
        }
    }
}
