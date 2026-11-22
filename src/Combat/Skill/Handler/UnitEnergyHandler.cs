using Miaosoft.Sim.Combat.Skill.EventData;

namespace Miaosoft.Sim.Combat.Skill.Handler;

public sealed class UnitEnergyHandler : Handler
{
    private static readonly TimeSpan PublicationInterval = TimeSpan.FromMilliseconds(100);
    private bool tickScheduled;

    public UnitEnergyHandler() => Id = nameof(UnitEnergyHandler);

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<Event> OnEnergyChanged(UnitEnergyChangedEvent ev)
    {
        if (ev.Receiver != ev.Unit.EventReceiver || tickScheduled || !ev.Unit.IsAlive)
            yield break;

        UnitProperty property = ev.Unit.Property;
        if (property.Energy >= property.EnergyMax || property.EnergyRecoveryPerSecond <= 0)
            yield break;

        decimal ticksUntilFull = decimal.Ceiling((property.EnergyMax - property.Energy) *
            TimeSpan.TicksPerSecond / property.EnergyRecoveryPerSecond);
        long delayTicks = (long)Math.Min(PublicationInterval.Ticks, Math.Max(1, ticksUntilFull));
        tickScheduled = true;
        yield return ev.Context.Factory.NewEvent<UnitEnergyTickEvent>() with
        {
            Unit = ev.Unit, Receiver = ev.Unit.EventReceiver, TimeSpan = TimeSpan.FromTicks(delayTicks)
        };
    }

    [Miaosoft.Sim.Core.Handler]
    public IEnumerable<Event> OnEnergyTick(UnitEnergyTickEvent ev)
    {
        if (ev.Receiver != ev.Unit.EventReceiver || !tickScheduled)
            yield break;

        tickScheduled = false;
        if (!ev.Unit.IsAlive)
        {
            ev.Unit.SynchronizeEnergy(ev.Context.ContextTimeSpan);
            yield break;
        }
        yield return UnitEnergyChangedEvent.FromUnit(ev.Context, ev.Unit);
    }
}
