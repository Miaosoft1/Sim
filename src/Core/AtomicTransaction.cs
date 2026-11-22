namespace Miaosoft.Sim.Core;

public sealed class AtomicCell<T>(T initialValue)
{
    internal sealed record Snapshot(T Value, long Version);
    private Snapshot state = new(initialValue, 0);
    private object? reservation;

    public T Value => Capture().Value;
    public long Version => Capture().Version;
    internal Snapshot Capture() => Volatile.Read(ref state);

    public void Set(T next)
    {
        var transaction = new AtomicTransaction();
        transaction.Stage(this, next);
        transaction.Commit();
    }

    public void Update(Func<T, T> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var transaction = new AtomicTransaction();
        T current = transaction.Read(this);
        transaction.Stage(this, update(current));
        transaction.Commit();
    }

    internal void Validate(Snapshot expected)
    {
        if (!ReferenceEquals(Capture(), expected))
            throw new InvalidOperationException("The state changed while the transaction was prepared.");
    }

    internal bool TryReserve(object owner) =>
        Interlocked.CompareExchange(ref reservation, owner, null) == null;

    internal void Release(object owner)
    {
        if (!ReferenceEquals(Interlocked.CompareExchange(ref reservation, null, owner), owner))
            throw new InvalidOperationException("The state reservation is inconsistent.");
    }

    internal void Apply(T next, Snapshot expected)
    {
        Volatile.Write(ref state, new Snapshot(next, expected.Version + 1));
    }
}

public sealed class AtomicTransaction
{
    private interface IRead
    {
        bool TryReserve(object owner);
        void Release(object owner);
        void Validate();
    }

    private sealed class ReadReservation<T>(AtomicCell<T> cell, AtomicCell<T>.Snapshot snapshot) : IRead
    {
        public AtomicCell<T>.Snapshot Snapshot { get; } = snapshot;
        public bool TryReserve(object owner) => cell.TryReserve(owner);
        public void Release(object owner) => cell.Release(owner);
        public void Validate() => cell.Validate(Snapshot);
    }

    private interface IWrite
    {
        void Validate();
        void Apply();
    }

    private sealed class Write<T>(AtomicCell<T> cell, T next, ReadReservation<T> read) : IWrite
    {
        public T Next { get; set; } = next;
        public void Validate()
        {
            if (read.Snapshot.Version == long.MaxValue)
                throw new InvalidOperationException("The state version cannot be incremented.");
        }
        public void Apply() => cell.Apply(Next, read.Snapshot);
    }

    private readonly List<IWrite> writes = [];
    private readonly Dictionary<object, IWrite> cells = new(ReferenceEqualityComparer.Instance);
    private readonly List<IRead> reservations = [];
    private readonly Dictionary<object, IRead> reads = new(ReferenceEqualityComparer.Instance);
    private bool finished;
    private bool updating;

    public void Stage<T>(AtomicCell<T> cell, T next)
    {
        ArgumentNullException.ThrowIfNull(cell);
        if (finished)
            throw new InvalidOperationException("The transaction has already completed.");
        if (updating)
            throw new InvalidOperationException("A transaction cannot be changed by its own updater.");
        var write = new Write<T>(cell, next, GetRead(cell));
        if (!cells.TryAdd(cell, write))
            throw new InvalidOperationException("A state cell cannot be written twice in one transaction.");
        writes.Add(write);
    }

    public T Read<T>(AtomicCell<T> cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        if (finished)
            throw new InvalidOperationException("The transaction has already completed.");
        if (updating)
            throw new InvalidOperationException("A transaction cannot be accessed by its own updater.");
        if (cells.TryGetValue(cell, out IWrite? write))
            return ((Write<T>)write).Next;
        return GetRead(cell).Snapshot.Value;
    }

    public void Update<T>(AtomicCell<T> cell, Func<T, T> update)
    {
        ArgumentNullException.ThrowIfNull(cell);
        ArgumentNullException.ThrowIfNull(update);
        if (updating)
            throw new InvalidOperationException("A transaction cannot be changed by its own updater.");
        T current = Read(cell);
        T next;
        updating = true;
        try { next = update(current); }
        finally { updating = false; }
        if (cells.TryGetValue(cell, out IWrite? write))
            ((Write<T>)write).Next = next;
        else
            Stage(cell, next);
    }

    private ReadReservation<T> GetRead<T>(AtomicCell<T> cell)
    {
        if (!reads.TryGetValue(cell, out IRead? read))
        {
            read = new ReadReservation<T>(cell, cell.Capture());
            reads.Add(cell, read);
            reservations.Add(read);
        }
        return (ReadReservation<T>)read;
    }

    public void Commit()
    {
        if (finished)
            throw new InvalidOperationException("The transaction has already completed.");
        if (updating)
            throw new InvalidOperationException("A transaction cannot be committed by its own updater.");

        int reserved = 0;
        try
        {
            foreach (IRead read in reservations)
            {
                if (!read.TryReserve(this))
                    throw new InvalidOperationException("The state is being committed by another transaction.");
                reserved++;
            }
            foreach (IRead read in reservations)
                read.Validate();
            foreach (IWrite write in writes)
                write.Validate();
            foreach (IWrite write in writes)
                write.Apply();
            finished = true;
        }
        finally
        {
            for (int index = reserved - 1; index >= 0; index--)
                reservations[index].Release(this);
        }
    }
}

public interface IRequiresCurrentRegistration
{
    bool RequiresCurrentRegistration(IEvent ev);
}

public interface ITargetableHandler : IHandler;

public interface ITargetedHandlerEvent
{
    CapturedHandlerTarget? TargetedHandler { get; }
}

public interface IRuntimeInstanceHandler
{
    Guid RuntimeInstanceId { get; }
}

public interface IRuntimeTargetEvent
{
    Guid? TargetRuntimeInstanceId { get; }
}
