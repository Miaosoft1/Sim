namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record UnitEnergyChangedEvent : Event
{
    public required Unit Unit { get; init; }
    public required decimal Energy { get; init; }
    public required int EnergyMax { get; init; }

    public static UnitEnergyChangedEvent FromUnit(Context context, Unit unit)
    {
        UnitProperty property = unit.Property;
        return context.Factory.NewEvent<UnitEnergyChangedEvent>() with
        {
            Unit = unit, Energy = property.Energy, EnergyMax = property.EnergyMax,
            Receiver = unit.EventReceiver, TimeSpan = TimeSpan.Zero
        };
    }
}

public sealed record UnitEnergyTickEvent : Event
{
    public required Unit Unit { get; init; }
}
