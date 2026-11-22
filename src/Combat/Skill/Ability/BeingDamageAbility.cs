using Miaosoft.Sim.Core;
using Miaosoft.Sim.Combat.Skill.EventData;
using Miaosoft.Sim.Combat.Skill.Expression;

namespace Miaosoft.Sim.Combat.Skill.Ability
{
    public class BeingDamageAbility : AbilityBase
    {
        public TimeSpan Delay { get; } = TimeSpan.Zero;
        public required DamageTypeRegistry DamageTypes { get; init; }

        /// <summary>
        /// Compiled mitigation formulas per configured damage type.
        /// </summary>
        private readonly Dictionary<string, NumericFormula<CombatFormulaContext>> mitigationFormulas =
            new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyDictionary<string, NumericFormula<CombatFormulaContext>> MitigationFormulas => mitigationFormulas;

        public void SetMitigationFormula(string damageType, NumericFormula<CombatFormulaContext> formula)
        {
            ArgumentNullException.ThrowIfNull(formula);
            mitigationFormulas[DamageTypes.Require(damageType).Id] = formula;
        }

        [Handler]
        public IEnumerable<Event> OnBeingDamage(DamageEvent ev)
        {
            if (ev.Receiver == null)
            {
                throw new ArgumentException();
            }

            if (!ev.HasDamage)
            {
                throw new ArgumentException("Damage can be null.");
            }

            if (ev.Target?.Property?.LifePoint == null)
            {
                ev.CancellationTokenSource?.Cancel();
                yield break;
            }

            if (ev.Receiver == ev.Target.EventReceiver)
            {
                foreach (Event result in ApplyDamage(ev, ev.SettlementReplayId))
                    yield return result;
            }
        }

        private IEnumerable<Event> ApplyDamage(DamageEvent ev, Guid? settlementReplayId)
        {
                if (ev.Target == null)
                    throw new InvalidOperationException("Damage requires a target.");
                ev.CalculatePendingDamage();
                DamageProfile incomingAbility = ev.TotalDamage.Handler;
                if (!ReferenceEquals(incomingAbility.DamageTypes, DamageTypes))
                    throw new InvalidOperationException("Damage and its receiver must use the same damage type catalog.");
                Damage mergedDamage = new Damage
                {
                    Handler = incomingAbility,
                    Source = ev.Source,
                    Target = ev.Target,
                    TimeSpan = ev.TimeSpan,
                    UsedFormulaFallback = ev.TotalDamage.UsedFormulaFallback
                };

                int rawTotalScaled = 0;

                foreach (string damageType in ev.TotalDamage.StoredKeys)
                {
                    DamageTypes.Require(damageType);
                    int rawTypeScaled = ev.TotalDamage.GetValueOrDefault(damageType);
                    if (rawTypeScaled <= 0)
                    {
                        continue;
                    }

                    rawTotalScaled = checked(rawTotalScaled + rawTypeScaled);
                    int currentScaled = rawTypeScaled;
                    if (mitigationFormulas.TryGetValue(damageType, out NumericFormula<CombatFormulaContext>? formula))
                    {
                        double rawHuman = rawTypeScaled / (double)Damage.ZOOM;
                        var ctx = new CombatFormulaContext(ev.Source, ev.Target, rawHuman, incomingAbility.Level);
                        FormulaEvaluationResult mitigated = FormulaPolicy.Evaluate(formula, ctx, Damage.IsRepresentable);
                        currentScaled = Math.Max(0, Damage.Scale(mitigated.Value));
                        mergedDamage.UsedFormulaFallback |= mitigated.UsedFallback;
                        ev.TotalDamage.UsedFormulaFallback |= mitigated.UsedFallback;
                    }

                    mergedDamage[damageType] = currentScaled;
                }

                int totalScaledDamage = mergedDamage.SumTrackedValues();

                int totalLifeReduce = totalScaledDamage / Damage.ZOOM;
                int totalUnmitigatedReduce = rawTotalScaled / Damage.ZOOM;
                int mitigatedDamage = Math.Max(0, totalUnmitigatedReduce - totalLifeReduce);

                ev.Target.Property.LifePoint = Math.Max(0, (ev.Target.Property.LifePoint ?? 0) - totalLifeReduce);

                CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
                if (totalLifeReduce > 0 || mitigatedDamage > 0)
                {
                    var newEvent = ev.Context.Factory.NewEvent<DamageLifeReduceEvent>() with
                    {
                        CommandEventId = ev.CommandEventId,
                        DamageAbility = incomingAbility,
                        Damage = mergedDamage,
                        ApplicationOrigin = ev.ApplicationOrigin,
                        SourceObject = ev.SourceObject,
                        TargetObject = ev.TargetObject,
                        TimeSpan = TimeSpan.Zero,
                        Source = ev.Source,
                        Target = ev.Target,
                        TriggerChain = ev.TriggerChain,
                        CancellationTokenSource = cancellationTokenSource,
                        LifePointReduce = totalLifeReduce,
                        MitigatedDamage = mitigatedDamage,
                        TargetLifePoint = ev.Target.Property.LifePoint,
                        Receiver = ev.Receiver,
                        Priority = (ev.Target.Property.LifePoint ?? 0) <= 0
                            ? CombatEventPriority.Death - 2 : 0
                    };

                    yield return DamageLifeReducePublicationEvent.FromReduction(newEvent);
                }

                if ((ev.Target.Property.LifePoint ?? 0) <= 0)
                {
                    DamageProfile killerAbility = ev.TotalDamage.Handler;

                    DeathEvent attempt = ev.Context.Factory.NewEvent<DeathEvent>() with
                    {
                        CommandEventId = ev.CommandEventId,
                        DamageAbility = killerAbility,
                        Receiver = ev.Receiver,
                        Killer = ev.Source,
                        Victim = ev.Target,
                        ApplicationOrigin = ev.ApplicationOrigin,
                        SourceObject = ev.SourceObject,
                        TargetObject = ev.TargetObject,
                        SettlementReplayId = settlementReplayId,
                        Causality = ev.Causality,
                        LifePointReduce = totalLifeReduce,
                        TimeSpan = TimeSpan.Zero,
                        Priority = CombatEventPriority.Death
                    };
                    yield return PendingDeathPublicationEvent.FromAttempt(attempt);
            }
        }

    }
}
