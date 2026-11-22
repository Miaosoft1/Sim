using System.Globalization;

namespace Miaosoft.Sim.Combat.Skill
{
    public sealed class Damage
    {
        public const int ZOOM = 100;
        private readonly Dictionary<string, int> values = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> storedKeys = [];

        public Damage()
        {
            StoredKeys = storedKeys.AsReadOnly();
        }

        public required DamageProfile Handler { get; set; }
        public required Unit? Source { get; set; }
        public required Unit Target { get; set; }
        public required TimeSpan TimeSpan { get; set; }
        public bool UsedFormulaFallback { get; internal set; }

        public IReadOnlyList<string> StoredKeys { get; }

        public int this[string key]
        {
            get => TryGetValue(key, out int value) ? value : 0;
            set
            {
                if (!values.ContainsKey(key))
                    storedKeys.Add(key);
                values[key] = value;
            }
        }

        public void Add(string key, int value)
        {
            values.Add(key, value);
            storedKeys.Add(key);
        }

        public bool Remove(string key)
        {
            if (!values.Remove(key))
                return false;
            storedKeys.RemoveAll(stored => StringComparer.OrdinalIgnoreCase.Equals(stored, key));
            return true;
        }

        public void Clear()
        {
            values.Clear();
            storedKeys.Clear();
        }

        public bool TryGetValue(string key, out int value) => values.TryGetValue(key, out value);

        public int GetValueOrDefault(string key)
        {
            return TryGetValue(key, out int value) ? value : 0;
        }

        public int SumTrackedValues()
        {
            int total = 0;
            foreach (string key in storedKeys)
            {
                total = checked(total + values[key]);
            }

            return total;
        }

        internal static bool IsRepresentable(double value) => TryScale(value, out _);

        internal static int Scale(double value)
        {
            if (!TryScale(value, out int scaled))
                throw new ExpressionEvaluationException("Scaled damage is outside its representable range.");
            return scaled;
        }

        private static bool TryScale(double value, out int scaled)
        {
            scaled = 0;
            if (!double.IsFinite(value)) return false;
            Span<char> text = stackalloc char[64];
            // Quantize the round-trip decimal representation so decimal midpoints remain midpoints.
            if (!value.TryFormat(text, out int written, "R", CultureInfo.InvariantCulture) ||
                !decimal.TryParse(text[..written], NumberStyles.Float, CultureInfo.InvariantCulture, out decimal amount))
                return false;
            decimal result;
            try { result = decimal.Round(amount * ZOOM, 0, MidpointRounding.ToEven); }
            catch (OverflowException) { return false; }
            if (result < int.MinValue || result > int.MaxValue) return false;
            scaled = (int)result;
            return true;
        }
    }
}
