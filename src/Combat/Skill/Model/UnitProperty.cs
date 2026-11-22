namespace Miaosoft.Sim.Combat.Skill
{
    /// <summary>
    /// Runtime combat properties.
    /// </summary>
    public partial class UnitProperty
    {
        private readonly Dictionary<string, double> numericExtensions = new(StringComparer.Ordinal);

        public int? LifePoint { get; set; }
        public int LifePointMax { get; set; }
        public decimal Energy { get; set; } = 100;
        public int EnergyMax { get; set; } = 100;
        public decimal EnergyRecoveryPerSecond { get; set; } = 10;
        public int? MoveSpeedBase { get; set; }
        public int? BaseMoveSpeed { get => MoveSpeedBase; set => MoveSpeedBase = value; }
        public int Experience { get; set; }
        public int ExperienceMax { get; set; } = 100;
        private int? moveSpeedOffset;
        public int? MoveSpeed
        {
            get => moveSpeedOffset.HasValue ? (MoveSpeedBase ?? 0) + moveSpeedOffset.Value : MoveSpeedBase;
            set => moveSpeedOffset = value.HasValue ? value.Value - (MoveSpeedBase ?? 0) : null;
        }
        public int Level { get; set; } = 1;

        public double GetNumericExtension(string id)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            return numericExtensions.GetValueOrDefault(id);
        }

        public void AddNumericExtension(string id, double delta)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            if (!double.IsFinite(delta)) throw new ArgumentOutOfRangeException(nameof(delta));
            double value = GetNumericExtension(id) + delta;
            if (!double.IsFinite(value)) throw new InvalidOperationException($"Numeric property '{id}' overflowed.");
            numericExtensions[id] = value;
        }

        public UnitProperty Copy()
        {
            var copy = new UnitProperty
            {
                LifePoint = LifePoint,
                LifePointMax = LifePointMax,
                Energy = Energy,
                EnergyMax = EnergyMax,
                EnergyRecoveryPerSecond = EnergyRecoveryPerSecond,
                MoveSpeedBase = MoveSpeedBase,
                MoveSpeed = MoveSpeed,
                Level = Level,
                Experience = Experience,
                ExperienceMax = ExperienceMax,
            };
            foreach ((string id, double value) in numericExtensions) copy.numericExtensions.Add(id, value);
            return copy;
        }
    }
}
