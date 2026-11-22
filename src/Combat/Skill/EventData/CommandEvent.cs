using Miaosoft.Sim.Combat.Map.Vector;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Core;
using System.Text;

namespace Miaosoft.Sim.Combat.Skill.EventData
{
    public record class CommandEvent : Event, ICommandAbilityEvent
    {
        private sealed class PublicationState
        {
            public bool Dispatched { get; set; }
        }

        private readonly PublicationState publication = new();
        public Guid CommandInstanceId { get; init; } = Guid.NewGuid();
        public required string AbilityId { get; init; }
        public Guid? AbilityInstanceId { get; init; }
        public required Unit Source { get; init; }
        /// <summary>
        /// Target unit. Optional: direction/point-targeted abilities carry no unit target.
        /// </summary>
        public Unit? Target { get; init; }
        public Map.MapObject? SourceObject { get; init; }
        public Map.MapObject? TargetObject { get; init; }
        public long? ControlVersion { get; init; }
        /// <summary>
        /// Explicit target point. Used when there is no target unit; when a target unit
        /// exists, its position takes precedence.
        /// </summary>
        public Vector2D? TargetPoint { get; init; }
        public double? Distance { get; init; }
        public AbilityTriggerChain? TriggerChain
        {
            get => Causality as AbilityTriggerChain;
            set => Causality = value;
        }

        /// <summary>
        /// Resolved target point: the target unit's position when present, otherwise the explicit point.
        /// </summary>
        public Vector2D? ResolvedTargetPoint => TargetObject?.Position ??
            (Target == null ? TargetPoint : Targeting.UnitSpatialRegistry.ObjectIn(Target,
                (SourceObject ?? Source.MapObject)?.MapRuntime)?.Position) ?? TargetPoint;

        public override IEnumerable<IEvent> Dispatch()
        {
            if (!Context.HasIndexedEvent<CommandEvent>())
                return base.Dispatch();
            if (publication.Dispatched)
                throw new InvalidOperationException("The command has already been published.");
            ArgumentNullException.ThrowIfNull(Source);
            ArgumentException.ThrowIfNullOrWhiteSpace(AbilityId);
            if (!ReferenceEquals(Receiver, Source.EventReceiver) ||
                !ReferenceEquals(Source.EventReceiver.Context, Context) ||
                Target != null && !ReferenceEquals(Target.EventReceiver.Context, Context))
                throw new InvalidOperationException("The command source or target does not belong to this context.");
            AbilityTriggerChain chain = TriggerChain ??
                throw new InvalidOperationException("The command requires the context's trigger chain.");
            publication.Dispatched = true;
            AbilityBase[] candidates = Source.EventReceiver.GetHandlers<AbilityBase>()
                .Where(ability => ability is ICommandAbility &&
                    string.Equals(ability.Id, AbilityId, StringComparison.Ordinal)).ToArray();
            CommandExecutionFailureReason failure = CommandExecutionFailureReason.None;
            if (AbilityInstanceId is { } instanceId)
            {
                AbilityBase? selected = candidates.SingleOrDefault(ability => ability.RuntimeInstanceId == instanceId);
                if (selected == null || selected.Binding.SourceInstanceId != null && !selected.Binding.IsAuthorized(Source.Id))
                    failure = CommandExecutionFailureReason.AbilityNotAvailable;
            }
            else if (candidates.Length > 1)
                failure = CommandExecutionFailureReason.AmbiguousAbility;
            else if (candidates.Length == 0 ||
                candidates[0].Binding.SourceInstanceId != null && !candidates[0].Binding.IsAuthorized(Source.Id))
                failure = CommandExecutionFailureReason.AbilityNotAvailable;
            if (failure != CommandExecutionFailureReason.None)
                return [Context.Factory.NewEvent<CommandExecutionEvent>() with
                {
                    Command = this, HandlerId = AbilityId, Status = CommandExecutionStatus.Failure,
                    FailureReason = failure, Receiver = Receiver, Priority = Priority, Causality = Causality,
                    Scope = Scope, CancellationTokenSource = CancellationTokenSource
                }];
            var (local, other) = chain.Router.PlanCommand(this);
            return Receiver.Dispatch(this, local).Concat(other.Select(invocation => invocation.Invocation));
        }

        protected override bool PrintMembers(StringBuilder stringBuilder)
        {
            stringBuilder.Append($"{nameof(CommandInstanceId)} = {CommandInstanceId}, ");
            stringBuilder.Append($"{nameof(AbilityId)} = {AbilityId}, ");
            stringBuilder.Append($"{nameof(AbilityInstanceId)} = {AbilityInstanceId}, ");
            stringBuilder.Append($"{nameof(Source)} = {Source.Name}, ");
            stringBuilder.Append($"{nameof(Target)} = {Target?.Name}, ");
            stringBuilder.Append($"{nameof(TargetPoint)} = {TargetPoint}, ");
            stringBuilder.Append($"{nameof(Distance)} = {Distance}, ");
            stringBuilder.Append($"{nameof(Delay)} = {Delay}, ");
            stringBuilder.Append($"{nameof(TimeSpan)} = {TimeSpan}, ");
            base.PrintMembers(stringBuilder);
            return true;
        }
    }
}
