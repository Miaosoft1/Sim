using System.Runtime.CompilerServices;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Core;

namespace Miaosoft.Sim.Combat.Skill;

internal sealed class AbilityChargeScheduler : IContextObserver
{
    private sealed class Entry(string abilityId, AbilitySourceBinding binding, CommandGateManager gate, Unit? holder)
    {
        public string AbilityId { get; } = abilityId;
        public AbilitySourceBinding Binding { get; } = binding;
        public CommandGateManager Gate { get; } = gate;
        public Guid? HolderId { get; set; } = holder?.Id;
        public Guid? PublishedOwnerId { get; set; }
        public long Generation { get; set; }
        public TimeSpan? ScheduledAt { get; set; }
        public bool TimerSuspended { get; set; }
    }

    private static readonly ConditionalWeakTable<Context, AbilityChargeScheduler> schedulers = new();
    private readonly Context context;
    private readonly EventReceiver receiver;
    private readonly Dictionary<Guid, Entry> entries = [];

    private AbilityChargeScheduler(Context context)
    {
        this.context = context;
        receiver = context.Factory.NewEventReceiver();
        context.AddEventReceiver(receiver);
        context.AddObserver(this);
    }

    public static AbilityChargeScheduler For(Context context)
    {
        ObjectDisposedException.ThrowIf(context.IsDisposed, context);
        return schedulers.GetValue(context, value => new AbilityChargeScheduler(value));
    }

    public void Register(AbilityBase ability, CommandGateManager gate, Unit? holder)
    {
        if (entries.TryGetValue(ability.RuntimeInstanceId, out Entry? existing))
        {
            if (!ReferenceEquals(existing.Gate, gate))
                throw new InvalidOperationException("The ability already has another charge gate.");
            SetHolder(ability.RuntimeInstanceId, holder);
            return;
        }
        entries.Add(ability.RuntimeInstanceId, new Entry(ability.Id, ability.Binding, gate, holder));
        Changed(ability.RuntimeInstanceId);
    }

    public void SetHolder(Guid abilityInstanceId, Unit? holder)
    {
        Entry entry = entries[abilityInstanceId];
        entry.HolderId = holder?.Id;
        if (entry.PublishedOwnerId != OwnerId(entry))
            Changed(abilityInstanceId);
    }

    private static Guid? OwnerId(Entry entry) => entry.Binding.SourceInstanceId.HasValue
        ? entry.Binding.OwnerId : entry.HolderId;

    public void Changed(Guid abilityInstanceId)
    {
        Entry entry = entries[abilityInstanceId];
        Schedule(abilityInstanceId, entry);
        AbilityChargeSnapshot snapshot = entry.Gate.GetChargeSnapshot()
            ?? throw new InvalidOperationException("A scheduled ability must have charges.");
        entry.PublishedOwnerId = OwnerId(entry);
        context.Dispatch(context.Factory.NewEvent<AbilityChargesChangedEvent>() with
        {
            AbilityId = entry.AbilityId,
            AbilityInstanceId = abilityInstanceId,
            OwnerId = entry.PublishedOwnerId,
            Charges = snapshot,
            Receiver = receiver,
            IsScopeDetached = true
        }).ToList();
    }

    private void Schedule(Guid abilityInstanceId, Entry entry)
    {
        if (entry.TimerSuspended) return;
        TimeSpan? next = entry.Gate.NextChargeRecovery;
        if (entry.ScheduledAt == next) return;
        entry.Generation = checked(entry.Generation + 1);
        entry.ScheduledAt = next;
        if (next is not { } deadline) return;
        if (deadline <= context.ContextTimeSpan)
            throw new InvalidOperationException("A charge timer must be scheduled in the future.");
        context.Dispatch(context.Factory.NewEvent<AbilityChargeTimerEvent>() with
        {
            AbilityInstanceId = abilityInstanceId,
            Generation = entry.Generation,
            Receiver = receiver,
            TimeSpan = deadline - context.ContextTimeSpan,
            IsScopeDetached = true,
            IsCausalityDetached = true
        }).ToList();
    }

    public void ResumeTimer(Guid abilityInstanceId)
    {
        Entry entry = entries[abilityInstanceId];
        entry.TimerSuspended = false;
        Schedule(abilityInstanceId, entry);
    }

    public IEnumerable<IEvent> OnQueued(Context context, IEvent ev) => [];

    public IEnumerable<IEvent> OnProcessed(Context context, IEvent ev)
    {
        if (ev is AbilityChargeTimerEvent timer && ReferenceEquals(timer.Receiver, receiver) &&
            entries.TryGetValue(timer.AbilityInstanceId, out Entry? entry) &&
            entry.Generation == timer.Generation && entry.ScheduledAt == context.ContextTimeSpan)
        {
            entry.ScheduledAt = null;
            entry.Gate.AdvanceCharges(context.ContextTimeSpan);
        }
        return [];
    }

    public void OnPendingEventsCleared(Context context)
    {
        if (context.IsDisposed)
        {
            entries.Clear();
            return;
        }
        foreach (Entry entry in entries.Values)
        {
            entry.ScheduledAt = null;
            entry.Generation = checked(entry.Generation + 1);
            entry.TimerSuspended = true;
        }
    }
}
