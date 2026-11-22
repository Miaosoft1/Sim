namespace Miaosoft.Sim.Combat.Map;

public sealed record MovementTiming
{
    public double FrameRate { get; init; } = 60;
    public int FramesPerStep { get; init; } = 2;

    public TimeSpan Interval
    {
        get
        {
            if (!double.IsFinite(FrameRate) || FrameRate <= 0 || FramesPerStep <= 0)
                throw new ArgumentOutOfRangeException(nameof(FrameRate), "Frame rate and frames per step must be positive.");
            double ticks = TimeSpan.TicksPerSecond * FramesPerStep / FrameRate;
            if (!double.IsFinite(ticks) || ticks > TimeSpan.MaxValue.Ticks || ticks < 1)
                throw new ArgumentOutOfRangeException(nameof(FrameRate), "Movement interval is outside the supported clock precision.");
            return TimeSpan.FromTicks((long)Math.Round(ticks));
        }
    }

    public static TimeSpan DefaultInterval => new MovementTiming().Interval;
}
