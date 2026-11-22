using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Combat.Skill.Execution;

namespace Miaosoft.Sim.Combat.Skill.Handler;

public sealed class UnitLevelUpHandler : Handler
{
    public override string Id { get; set; } = nameof(UnitLevelUpHandler);

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<Event> OnRequest(UnitLevelUpRequestEvent ev)
    {
        if (ev.CancellationToken.IsCancellationRequested) yield break;
        if (!ev.Context.IsProcessingEvent || ev.Receiver != ev.Unit.EventReceiver ||
            !ReferenceEquals(ev.Unit.EventReceiver.Context, ev.Context) ||
            !ev.Context.ContainsReceiver(ev.Receiver) || ev.OperationId == Guid.Empty || ev.ExpectedLevel < 1 ||
            ev.Unit.EventReceiver.GetHandlers<UnitLevelUpHandler>() is not [var handler] ||
            !ReferenceEquals(handler, this))
            throw new InvalidOperationException("A level increase requires its unit and unique preset handler.");
        if (ev.CompletionReceiver is { } completion &&
            (!ReferenceEquals(completion.Context, ev.Context) || !ev.Context.ContainsReceiver(completion)))
            throw new InvalidOperationException("A level increase completion receiver must be registered in the same context.");

        UnitProperty before = ev.Unit.Property.Copy();
        UnitLevelUpFailure failure = before.Level != ev.ExpectedLevel ? UnitLevelUpFailure.StaleLevel :
            ev.NewLevel < 1 || ev.NewLevel > ev.Unit.MaximumLevel ? UnitLevelUpFailure.OutsideLevelRange :
            ev.NewLevel <= before.Level ? UnitLevelUpFailure.NotAnIncrease : UnitLevelUpFailure.None;
        var result = ev.Context.Factory.NewEvent<UnitLevelUpResultEvent>() with
        {
            Unit = ev.Unit, OperationId = ev.OperationId, OldLevel = before.Level,
            NewLevel = failure == UnitLevelUpFailure.None ? ev.NewLevel : before.Level,
            Failure = failure, Receiver = ev.CompletionReceiver ?? ev.Receiver,
            Priority = ev.Priority, IsScopeDetached = true
        };
        if (failure != UnitLevelUpFailure.None)
        {
            yield return result;
            yield break;
        }

        var transaction = new AtomicTransaction();
        ev.Unit.Abilities.StageReApply(transaction, ev.Receiver.GetHandlers<IHandler>(), ev.NewLevel);
        if (ev.CancellationToken.IsCancellationRequested) yield break;
        transaction.Commit();
        ev.Context.MarkCurrentOperationCommitted(ev.OperationId);
        result.CommittedOperationId = ev.OperationId;
        var failures = new List<Exception>();
        try { ev.Unit.Abilities.PublishProjection(); }
        catch (Exception error) { failures.Add(error); }
        var notifications = new List<Event>
        {
            ev.Context.Factory.NewEvent<UnitLevelUpEvent>() with
            {
                Unit = ev.Unit, OperationId = ev.OperationId, OldLevel = before.Level, NewLevel = ev.NewLevel,
                Receiver = ev.Receiver, Priority = ev.Priority, IsScopeDetached = true,
                CommittedOperationId = ev.OperationId
            }
        };
        UnitProperty current = ev.Unit.Property;
        if (before.LifePoint != current.LifePoint || before.LifePointMax != current.LifePointMax)
            notifications.Add(UnitLifeChangedEvent.FromUnit(ev.Context, ev.Unit, before.LifePoint ?? before.LifePointMax)
                with { Priority = ev.Priority, IsScopeDetached = true, CommittedOperationId = ev.OperationId });
        if (before.Energy != current.Energy || before.EnergyMax != current.EnergyMax)
            notifications.Add(UnitEnergyChangedEvent.FromUnit(ev.Context, ev.Unit)
                with { Priority = ev.Priority, IsScopeDetached = true, CommittedOperationId = ev.OperationId });
        notifications.Add(result);
        CommittedEventPublisher.Dispatch(ev.Context, notifications, failures);
    }
}
