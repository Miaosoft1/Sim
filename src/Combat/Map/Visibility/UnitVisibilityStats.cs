using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Combat.Map.Visibility;

public sealed class UnitVisibilityStats
{
    private readonly AtomicCell<(double Vision, double Stealth)> modifiers = new((0, 0));
    private double visionModifier => modifiers.Value.Vision;
    private double stealthModifier => modifiers.Value.Stealth;
    internal event Action? Changed;

    public UnitVisibilityStats()
    {
        SetBase(MapVisibilityArea.Default.DefaultVisionRange, 0);
    }

    public double BaseVisionRange { get; private set; }
    public double BaseStealth { get; private set; }
    public double VisionRange => Math.Max(0, BaseVisionRange + visionModifier);
    public double Stealth => Math.Max(0, BaseStealth + stealthModifier);

    public void SetBase(double visionRange, double stealth)
    {
        if (!double.IsFinite(visionRange) || visionRange < 0)
            throw new ArgumentOutOfRangeException(nameof(visionRange));
        if (!double.IsFinite(stealth) || stealth < 0)
            throw new ArgumentOutOfRangeException(nameof(stealth));
        if (!double.IsFinite(visionRange + visionModifier) || !double.IsFinite(stealth + stealthModifier))
            throw new ArgumentOutOfRangeException(nameof(visionRange), "Visibility attributes must remain finite.");
        BaseVisionRange = visionRange;
        BaseStealth = stealth;
        Changed?.Invoke();
    }

    public void ResetModifiers()
    {
        modifiers.Set((0, 0));
        Changed?.Invoke();
    }

    public void AddVisionRange(double amount)
    {
        if (!double.IsFinite(amount) || !double.IsFinite(visionModifier + amount) ||
            !double.IsFinite(BaseVisionRange + visionModifier + amount))
            throw new ArgumentOutOfRangeException(nameof(amount));
        modifiers.Set((visionModifier + amount, stealthModifier));
        Changed?.Invoke();
    }

    public void AddStealth(double amount)
    {
        if (!double.IsFinite(amount) || !double.IsFinite(stealthModifier + amount) ||
            !double.IsFinite(BaseStealth + stealthModifier + amount))
            throw new ArgumentOutOfRangeException(nameof(amount));
        modifiers.Set((visionModifier, stealthModifier + amount));
        Changed?.Invoke();
    }

    public UnitVisibilityStats CreateDetached()
    {
        var detached = new UnitVisibilityStats();
        detached.SetBase(BaseVisionRange, BaseStealth);
        return detached;
    }

    public void StageModifiers(AtomicTransaction transaction, UnitVisibilityStats staged)
    {
        if (BaseVisionRange != staged.BaseVisionRange || BaseStealth != staged.BaseStealth)
            throw new InvalidOperationException("The visibility base changed during preparation.");
        transaction.Stage(modifiers, staged.modifiers.Value);
    }

    public void PublishModifiersChanged() => Changed?.Invoke();
}
