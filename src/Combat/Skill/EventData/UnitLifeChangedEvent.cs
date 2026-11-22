namespace Miaosoft.Sim.Combat.Skill.EventData;

public sealed record UnitLifeChangedEvent : Event
{
    public required Unit Unit { get; init; }
    public required int LifePoint { get; init; }
    public required int LifePointMax { get; init; }
    public required int PreviousLifePoint { get; init; }

    public static UnitLifeChangedEvent FromUnit(Context context, Unit unit, int previousLifePoint)
    {
        UnitProperty property = unit.Property;
        return context.Factory.NewEvent<UnitLifeChangedEvent>() with
        {
            Unit = unit,
            LifePoint = property.LifePoint ?? throw new InvalidOperationException("Life has not been initialized."),
            LifePointMax = property.LifePointMax,
            PreviousLifePoint = previousLifePoint,
            Receiver = unit.EventReceiver,
            TimeSpan = TimeSpan.Zero
        };
    }
}
