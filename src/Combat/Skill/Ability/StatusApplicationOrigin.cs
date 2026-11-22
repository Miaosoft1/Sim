using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Combat.Skill.Ability;

public sealed class StatusApplicationOrigin
{
    private readonly object instance;
    private readonly EventReceiver? ruleReceiver;
    // Released effects outlive registration, but their delivery authorization is single-use.
    private readonly Context? releasedContext;
    private AbilityInstallRequestEvent? releasedRequest;
    private AbilityInstallEvent? releasedInstall;
    private bool requestConsumed;
    private bool installConsumed;

    private StatusApplicationOrigin(
        object instance, EventReceiver? ruleReceiver = null, Context? releasedContext = null)
    {
        this.instance = instance;
        this.ruleReceiver = ruleReceiver;
        this.releasedContext = releasedContext;
        if (instance is AbilityBase ability)
            AbilitySnapshot = AbilityRuntimeSnapshot.Capture(ability);
    }

    public AbilityBase? Ability => instance as AbilityBase;
    public MapObject? MapObject => instance as MapObject;
    public IHandler? Rule => ruleReceiver == null ? null : (IHandler)instance;
    public AbilityRuntimeSnapshot? AbilitySnapshot { get; }
    public Guid? AbilityRuntimeInstanceId => AbilitySnapshot?.RuntimeInstanceId;
    public Guid? AbilityGrantSourceInstanceId => AbilitySnapshot?.GrantSourceInstanceId;

    public static StatusApplicationOrigin FromAbility(AbilityBase ability)
    {
        ArgumentNullException.ThrowIfNull(ability);
        return new StatusApplicationOrigin(ability);
    }

    internal static StatusApplicationOrigin FromAcceptedAbility(
        AbilityBase ability, Unit owner, Context context, CapturedHandlerTarget target)
    {
        if (!context.IsProcessingEvent || !ReferenceEquals(ability.Binding.BoundContext, context) ||
            !context.ContainsReceiver(owner.EventReceiver) ||
            !target.Targets(owner.EventReceiver, ability) ||
            ability.Binding.SourceInstanceId != null && !ability.Binding.IsAuthorized(owner.Id))
            throw new InvalidOperationException("The released status origin is not authorized by its accepted ability.");
        return new StatusApplicationOrigin(ability, releasedContext: context);
    }

    internal void BindReleasedRequest(AbilityInstallRequestEvent request)
    {
        if (releasedContext == null || requestConsumed || releasedRequest != null ||
            !ReferenceEquals(request.ApplicationOrigin, this))
            throw new InvalidOperationException("The released status request cannot be bound.");
        releasedRequest = request;
    }

    internal void BindReleasedInstall(AbilityInstallRequestEvent request, AbilityInstallEvent install)
    {
        if (releasedContext == null) return;
        if (!requestConsumed || !ReferenceEquals(releasedRequest, request) || installConsumed || releasedInstall != null ||
            !ReferenceEquals(install.ApplicationOrigin, this))
            throw new InvalidOperationException("The released status installation cannot be bound.");
        releasedRequest = null;
        releasedInstall = install;
    }

    public static StatusApplicationOrigin FromMapObject(MapObject mapObject)
    {
        ArgumentNullException.ThrowIfNull(mapObject);
        return new StatusApplicationOrigin(mapObject);
    }

    public static StatusApplicationOrigin FromRule(IHandler rule, EventReceiver receiver)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(receiver);
        if (rule is AbilityBase)
            throw new ArgumentException("An ability must be identified as an ability origin.", nameof(rule));
        return new StatusApplicationOrigin(rule, receiver);
    }

    internal bool IsSameInstance(StatusApplicationOrigin other) => ReferenceEquals(instance, other.instance);

    internal void Validate(Context context, Event currentEvent)
    {
        if (!context.IsProcessingEvent)
            throw new InvalidOperationException("Status origins must be checked by the context's execution owner.");
        if (instance is AbilityBase ability)
        {
            if (!ReferenceEquals(ability.Binding.BoundContext, context) ||
                ability.Binding.SourceInstanceId != AbilityGrantSourceInstanceId)
                throw new InvalidOperationException("The status ability origin is not registered in this context.");
            if (releasedContext != null)
            {
                if (!ReferenceEquals(releasedContext, context))
                    throw new InvalidOperationException("The released status belongs to a different context.");
                if (ReferenceEquals(currentEvent, releasedRequest) && !requestConsumed)
                    requestConsumed = true;
                else if (ReferenceEquals(currentEvent, releasedInstall) && !installConsumed)
                {
                    installConsumed = true;
                    if (currentEvent is not AbilityInstallEvent { StatusBatch: not null })
                        releasedInstall = null;
                }
                else
                    throw new InvalidOperationException("The released status authorization is invalid or already used.");
            }
            else if (context.FindHandlerReceivers(ability).Count != 1)
                throw new InvalidOperationException("The status ability origin is not registered in this context.");
            return;
        }
        if (instance is MapObject mapObject)
        {
            if (!context.ContainsReceiver(mapObject.EventReceiver) ||
                !ReferenceEquals(mapObject.EventReceiver.Context, context) ||
                mapObject.MapRuntime == null ||
                !mapObject.MapRuntime.Objects.Any(current => ReferenceEquals(current, mapObject)))
                throw new InvalidOperationException("The status map origin is not registered in this context.");
            return;
        }
        if (ruleReceiver == null || !context.ContainsReceiver(ruleReceiver) ||
            !ReferenceEquals(ruleReceiver.Context, context) ||
            !ruleReceiver.GetHandlers<IHandler>().Any(handler => ReferenceEquals(handler, instance)))
            throw new InvalidOperationException("The status rule origin is not registered in this context.");
    }

    internal void ValidatePreparedInstall(Context context, AbilityInstallEvent installation)
    {
        if (!context.IsProcessingEvent || !ReferenceEquals(releasedContext, context) ||
            instance is not AbilityBase ability || !ReferenceEquals(ability.Binding.BoundContext, context) ||
            ability.Binding.SourceInstanceId != AbilityGrantSourceInstanceId ||
            !installConsumed || !ReferenceEquals(releasedInstall, installation))
            throw new InvalidOperationException("The prepared status installation is not authorized.");
        releasedInstall = null;
    }
}
