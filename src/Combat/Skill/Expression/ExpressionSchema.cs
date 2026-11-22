namespace Miaosoft.Sim.Combat.Skill.Expression;

public enum ExpressionValueType
{
    Number,
    Boolean,
    String,
    UnitIdentity,
    AbilityIdentity,
    SourceIdentity
}

public sealed record ExpressionFieldDescriptor(string Name, ExpressionValueType Type, bool IsNullable);

public sealed class ExpressionSchema<TContext>
{
    private readonly Dictionary<string, BoundField<TContext>> fields = new(StringComparer.Ordinal);
    private IReadOnlyList<ExpressionFieldDescriptor>? descriptors;

    public bool IsSealed => descriptors != null;

    public IReadOnlyList<ExpressionFieldDescriptor> Fields =>
        descriptors ?? throw new InvalidOperationException("The expression schema has not been sealed.");

    public void RegisterNumber(string name, Func<TContext, double?> read, bool nullable = false)
    {
        ArgumentNullException.ThrowIfNull(read);
        Add(name, ExpressionValueType.Number, nullable, context =>
        {
            double? value = read(context);
            if (!value.HasValue) return Missing(name, ExpressionValueType.Number, nullable);
            if (!double.IsFinite(value.Value))
                throw new ExpressionEvaluationException($"Field '{name}' produced a non-finite number.");
            return ExpressionValue.Number(value.Value);
        });
    }

    public void RegisterBoolean(string name, Func<TContext, bool?> read, bool nullable = false)
    {
        ArgumentNullException.ThrowIfNull(read);
        Add(name, ExpressionValueType.Boolean, nullable, context =>
        {
            bool? value = read(context);
            return value.HasValue
                ? ExpressionValue.Boolean(value.Value)
                : Missing(name, ExpressionValueType.Boolean, nullable);
        });
    }

    public void RegisterString(string name, Func<TContext, string?> read, bool nullable = false)
    {
        ArgumentNullException.ThrowIfNull(read);
        Add(name, ExpressionValueType.String, nullable, context =>
        {
            string? value = read(context);
            return value != null
                ? ExpressionValue.String(value)
                : Missing(name, ExpressionValueType.String, nullable);
        });
    }

    public void RegisterIdentity(
        string name, ExpressionValueType type, Func<TContext, Guid?> read, bool nullable = false)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (type is not (ExpressionValueType.UnitIdentity or ExpressionValueType.AbilityIdentity
            or ExpressionValueType.SourceIdentity))
            throw new ArgumentOutOfRangeException(nameof(type), "An identity field needs an identity type.");
        Add(name, type, nullable, context =>
        {
            Guid? value = read(context);
            return value.HasValue ? ExpressionValue.Identity(type, value.Value) : Missing(name, type, nullable);
        });
    }

    public void Seal()
    {
        if (IsSealed) throw new InvalidOperationException("The expression schema is already sealed.");
        descriptors = Array.AsReadOnly(fields.Values.Select(field => field.Descriptor).ToArray());
    }

    internal BoundField<TContext> GetField(string name)
    {
        if (!IsSealed) throw new InvalidOperationException("The expression schema must be sealed before compilation.");
        return fields.TryGetValue(name, out BoundField<TContext>? field)
            ? field
            : throw new FormatException($"Unknown expression field '{name}'.");
    }

    private void Add(
        string name, ExpressionValueType type, bool nullable, Func<TContext, ExpressionValue> read)
    {
        if (IsSealed) throw new InvalidOperationException("The expression schema is sealed.");
        if (!IsValidName(name))
            throw new ArgumentException("A field name must contain valid dot-separated identifiers.", nameof(name));
        if (name is "true" or "false" or "null")
            throw new ArgumentException("A field name cannot be a reserved literal.", nameof(name));
        var field = new BoundField<TContext>(new ExpressionFieldDescriptor(name, type, nullable), read);
        if (!fields.TryAdd(name, field))
            throw new InvalidOperationException($"Expression field '{name}' is already registered.");
    }

    private static bool IsValidName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        bool start = true;
        foreach (char character in name)
        {
            if (character == '.')
            {
                if (start) return false;
                start = true;
            }
            else if (start)
            {
                if (!(char.IsLetter(character) || character == '_')) return false;
                start = false;
            }
            else if (!(char.IsLetterOrDigit(character) || character == '_'))
            {
                return false;
            }
        }
        return !start;
    }

    private static ExpressionValue Missing(string name, ExpressionValueType type, bool nullable) =>
        nullable
            ? ExpressionValue.Missing(type)
            : throw new ExpressionEvaluationException($"Required field '{name}' is missing.");
}

internal sealed record BoundField<TContext>(
    ExpressionFieldDescriptor Descriptor, Func<TContext, ExpressionValue> Read);

internal readonly struct ExpressionValue
{
    private readonly double number;
    private readonly bool boolean;
    private readonly string? text;
    private readonly Guid identity;

    private ExpressionValue(
        ExpressionValueType type, bool hasValue, double number = 0, bool boolean = false,
        string? text = null, Guid identity = default)
    {
        Type = type;
        HasValue = hasValue;
        this.number = number;
        this.boolean = boolean;
        this.text = text;
        this.identity = identity;
    }

    public ExpressionValueType Type { get; }
    public bool HasValue { get; }
    public double AsNumber => Type == ExpressionValueType.Number && HasValue
        ? number : throw new InvalidOperationException("The value is not a number.");
    public bool AsBoolean => Type == ExpressionValueType.Boolean && HasValue
        ? boolean : throw new InvalidOperationException("The value is not a boolean.");
    public string AsString => Type == ExpressionValueType.String && HasValue
        ? text! : throw new InvalidOperationException("The value is not a string.");
    public Guid AsIdentity => HasValue && Type is (ExpressionValueType.UnitIdentity
        or ExpressionValueType.AbilityIdentity or ExpressionValueType.SourceIdentity)
        ? identity : throw new InvalidOperationException("The value is not an identity.");

    public static ExpressionValue Missing(ExpressionValueType type) => new(type, false);
    public static ExpressionValue Number(double value) => new(ExpressionValueType.Number, true, number: value);
    public static ExpressionValue Boolean(bool value) => new(ExpressionValueType.Boolean, true, boolean: value);
    public static ExpressionValue String(string value) => new(ExpressionValueType.String, true, text: value);
    public static ExpressionValue Identity(ExpressionValueType type, Guid value) => new(type, true, identity: value);
}
