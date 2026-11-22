namespace Miaosoft.Sim.Combat.Skill;

public sealed class UnitStateCollection
{
    private readonly Dictionary<Type, object> states = [];

    public T? Get<T>() where T : class => states.GetValueOrDefault(typeof(T)) as T;

    public void Add<T>(T state) where T : class
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state is IHandler) throw new ArgumentException("Unit state cannot contain handlers.", nameof(state));
        states.Add(typeof(T), state);
    }

    public bool Remove<T>(T state) where T : class =>
        ReferenceEquals(Get<T>(), state) && states.Remove(typeof(T));

    public void Clear() => states.Clear();
}
