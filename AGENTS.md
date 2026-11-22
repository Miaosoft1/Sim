# AI contributor guide

This directory is an in-progress framework extraction. The human entry points
are [README.md](README.md) and [README.zh-CN.md](README.zh-CN.md). Do not describe
planned capabilities or private-consumer compatibility as delivered. The current
network path is an isolated, tested prototype, not completed private integration.

## Boundaries

- `src/Core` owns event scheduling and atomic changes; it must not depend on
  combat, content, transport or applications.
- `src/Combat/Map` owns spatial runtime behavior.
- `src/Combat/Skill` owns abilities, triggers, activation and combat effects.
- Content projects depend on their runtime domain, never the reverse.
  `src/Content/Loading` composes explicitly selected DI modules.
- Catalog definitions and sealed field schemas may be shared; runtime handlers,
  queues, ownership and gates may not be shared between contexts.
- Keep neutral public content and framework code separate from private gameplay
  and assets. Do not copy a private tree or its history into the public tree.
- Protocol and Control must not reference server implementations or private
  account/matchmaking services. Client and Console must not reference Server.
  Runtime owns the serialized execution loop; gameplay adapters depend on
  Runtime.Abstractions, not Runtime. Server.Hosting is transport composition.
- Preserve operation sequence, correlation and publication sequence as distinct
  identities. Authorize the current participant/connection before receipt lookup;
  retries preserve the original envelope and cached acknowledgement. Never
  interpret the next sequence as proof of execution or replay an expired
  operation under a fresh number.
- Project world and negotiated module state at one version. A unitless recipient
  receives removal for controlled-unit-only data even with a custom projector.
  Do not drop established module contracts on rejoin. Client module codecs
  return detached immutable state and cannot mutate previous publications.
  Full/delta codecs receive SessionBinding and validate private owner identity
  before publication. Queue acceptance is distinct from Succeeded, Rejected,
  Aborted or Unknown execution outcomes; Unknown never authorizes fresh-ID replay.
- Capture transient projectile facts only from confirmed ballistic emission,
  semantic hit publication or explosion events, under actual observer visibility.
  Never treat receiver-specific deliveries as new hits. Transients go only to
  acknowledged live connections, never full recovery or preparation buffers;
  retain business outcomes separately. Simulation decorators forward transient
  projection. Map grids expose geometry, not occupancy/reservations; live
  entrances require explicit recipient visibility, not destination-world access.
- Completion settles the current timestamp, clears future combat and freezes
  simulation while the same owner handles explicitly allowed inputs/real-time
  cleanup. Rejoin cannot restart completion. Build mode result records through
  CreateResultRecord() after Execute is assigned, not computed record properties.
- Optional Loadout.Protocol has no domain/server references; Loadout.Server
  decorates Runtime.Abstractions, and Loadout.Client depends only on the client
  SDK and optional contracts. Base Server/Client/Console remain independent.
  Bind requests to current participant features, exact instance IDs and expected
  revisions/authority. Clients cannot create/acquire items, grant trees or award
  points. Expose counterpart items only through its actual offer and death items
  only after a currently valid beneficiary inspection. Complete-session Loadout
  input is restricted to existing authorized death inspection/claim, not new
  exchanges, talent allocation or general inventory mutation.
  Dynamic membership comes from Context-bound LoadoutSessionFeatures, published
  by the mode on the same execution chain, never mutated by network threads.
  Its frozen membership retains live original objects; it is not a world-state
  clone. NPC feature ownership never grants admission/control. Preserve the
  submitting receiver for in-flight results across membership removal, and close
  exchanges/clean up death capabilities through domain events before retiring
  directory entries.
- BattleSessionSimulation keeps its admission actors fixed while an optional
  same-owner world provider can expose later NPCs. Validate original identities
  and Context registration, and reassess queued commands at execution. A world
  entry is not a participant, credential or control grant.

## Runtime invariants

- Context state has one logical execution owner. Event order is observable;
  do not add batch snapshots or execute handlers directly to bypass the queue.
- Events holding pending resources can implement `IEventProcessingFailure` to
  release them if processing or a handler invocation throws. Cleanup errors
  must remain visible together with the original error and committed identity.
  Cleanup must be idempotent and must never undo a committed operation.
- Ability installation/removal goes through context events and the preset
  installer. Use the existing atomic transaction for coordinated mutations.
  Commit notifications cannot claim that already-observed changes were undone.
- Transaction updates compose staged values without publishing intermediate
  states. Periodic removals must be staged before additions when computing
  net capacity across targets, and all prepared handles must be closed on abort.
  Triggered periodic damage and gated status releases stage gate, energy and
  effects together; status batches also stage all targets' installations.
- Triggers evaluate conditions and emit invocation events. They do not create,
  retain or directly call another ability handler.
- Conditional charge recovery uses its own bound trigger and a specialized
  invocation, then an instance-targeted restore request. A queued recovery
  must not survive registration or authorization changes; it must not activate
  the ability or reset earlier retained charge timers.
- Managed sources own the original ability objects and bound runtime parts;
  units index assigned sources and selected EventReceiver registrations only.
  Source changes commit ownership, levels, selection, routing and projections
  together through the preset installer, never definition-ID or bare atomic
  handler operations. Materialized levels change only through source events;
  their coexistence policy cannot change.
- Non-coexisting definitions select the highest actual ability level, retain
  the current tied instance, then use stable source/member order. Gate failure
  never falls back to a reserve. Only selected registrations observe new
  activations/recovery, but reserve and unheld instances retain charge timers.
- Transfers keep the original objects, gates and runtime IDs. Accepted casts
  survive selection changes, not revoked release authority; new-owner requests
  must not overwrite old reservations. Source Holder is activation authority,
  not physical inventory ownership or transport authorization.
- Partial Loadout inventory/equipment operations stage physical ownership,
  placements and source assignments in one transaction. Owning-domain
  item creation goes through ItemCreateRequestEvent; ItemCreatedEvent exposes
  the instance only after its original abilities and Context binding commit.
  Acquisition never lazily creates abilities. Materializing an unheld source
  does not reproject an unrelated coordinator. Owning-domain
  preparations run inside source execution, not earlier in queued request
  handlers; otherwise ordinary same-timestamp requests can prepare stale
  transactions. Preparations stage domain cells, never publish, commit or
  create/mount handlers. Validate net backpack/carry capacity, including
  equipped items. Discard destroys an instance and advances revisions.
  Bilateral exchange binds exact item IDs and explicit destination slots to
  versioned terms; any changed offer resets both consents. Final preparation
  rechecks the shared session policy and both inventory revisions and stages
  both final layouts, item/source ownership and closed consent together.
  One-way gifts create no counterpart. Loadout.Content's AddInventoryContent
  only registers the explicitly selected loadout-inventory module: item roots
  compile once across documents and validate ability references afterward.
  It does not enable runtime inventory, talents or trading. Neutral items need
  no ability-content module. Talent content and the equipment bridge are
  separate selections: loadout-talents and loadout-equipment-talents.
  Death pickup is separately composed; private integration remains incomplete.
- Compose UnitTalents with the owner's selected immutable tree definitions.
  It prepares retained per-tree domain observers, not ability instances; only
  the preset installer mounts them through non-ability RegistrationChanges.
  Preparations must not construct handlers. Trees have independent point
  pools, node sources and growth counters. Distinct real source identities
  count separately; losing the last grant removes observation and source
  authorization but retains original tree/observer/abilities and progress.
  Tree/node/growth sources are fixed to their role owner, never to an item.
  Node abilities grow in place only when growAbilitiesWithRank is explicit;
  their level is the member's initial level plus purchased rank minus one.
  No respec operation is implemented.
- TalentGrowth is a real observing ability compiled through IHandlerParser
  and the shared AbilityHandlerFactory.WithTrigger, not a synthetic tree
  ability or a second trigger engine. Rules reference exactly one matching
  tree/rule-bound ability, count valid observations, retain remainders and
  support maximumRewards (1 means once). Counters live on the role's tree.
  The ability's typed progress expression defaults to 1 and must produce a
  positive representable integer without rounding or fallback. Configure
  event.level.delta for rewards per actual level, not per notification.
  Accumulate weighted progress in wide arithmetic, award complete thresholds
  up to the configured reward-count cap and retain the remainder; reject
  point overflow without consuming progress. Never backfill from live level.
  Ungranted events are neither observed nor replayed. Retain the source
  authority epoch on invocations and the tree epoch on queued observations.
  The opt-in IEquipmentChangePreparation joins inventory and bilateral trade
  in the same source transaction. Batch net grants by tree: replacing one
  grant with another must not spuriously revoke/reinstall its observer.
- DeathTradeDirectory uses an explicit IDeathTradeDistributionPolicy and
  live interaction policy; do not hardcode killer rewards, factions or range.
  DeathTradeHandler opens capabilities only for the unit's actual
  CurrentDeathId. The ID is null while alive and changes at the next death
  commit, before confirmed-death observers run. A revival/redeath must not
  make old capabilities valid again.
  A beneficiary needs current inventory/trading features, in-range inspection
  and matching inspection/both-inventory revisions. Validate the exact captured
  corpse still exists in its map even when an injected policy returns true.
  Pickup is one-way authorized transfer, not fabricated consent by a dead
  participant. Reuse InventoryTransfer and both equipment preparations in the
  same source commit; preserve original item/ability state and role-owned trees.
  Leave unclaimed items and grants until explicit cleanup; cleanup destroys
  only remaining items and closes authorization. Runtime capabilities are not
  wire credentials or detached snapshots.
- Passive property abilities and applied statuses share one ordered projection.
  Combat owns UnitLevelUpRequestEvent and the committed UnitLevelUpEvent,
  independent of Loadout. Use the request for runtime growth; validate its
  expected level and configured Unit.MaximumLevel, commit projection/cache
  first, then publish the actual positive delta and result with propagated
  priority. No-change/stale/range rejection emits no growth event.
  CompletionReceiver optionally redirects only results to a registered
  same-context coordinator; the actual unit publishes its upgrade once.
  Reject foreign/unregistered completion destinations before mutation.
  Sequence relative increases from results and explicitly reconsider stale
  versions; never treat a stale request as a successful reward.
  Existing SetLevel/LevelUp helpers are low-level setup operations, not growth flows.
  Unit level changes do not implicitly change ability levels.
  UnitLevelUp is an indexed trigger with recorded event.level.old/new/delta
  fields, not live properties or a synthetic producing ability. Its source
  and target are the upgraded unit; retain event priority and reject
  notification republication including clones. Loadout may observe these
  records through its explicitly selected TalentGrowth rules.
  When source and target are the same unit, formulas read the staged projection;
  extension getters should use `PropertyFormulaContext.SourceProperty`, not
  `Source.Property`. `recalculateOn` defaults to ProjectionChanged; narrower
  AbilityLevelChanged/TargetLevelChanged policies retain per-instance values,
  and [] retains initial registration values. New registrations/actor identity
  changes evaluate afresh. These are projection policies, not activation
  triggers or implicit cross-unit subscriptions. Stage retained values and
  fallback markers with the projection; apply current stacks to retained
  per-stack values and reject unrepresentable totals without resampling.
- Status listeners may use the same runtime gate as other effects without
  becoming command-addressable. Validate targets before reservation, spend
  charges/resources at release, and start the applied lease at release.
  Applied status instances do not retain the listener's gate. Status YAML
  supports catalog trigger types or `None` for explicit application, and additive
  expression modifiers and compiled periodic damage/healing. Periodic templates
  create profiles with the release level and original source snapshot, never
  another ability handler. Rule applications keep a null actor when appropriate.
  Same-level reapplication keeps the existing schedule, including completed
  ticks; compiled periodic effects use the retained state's current stack count.
  Higher-level replacement and Independent applications get new schedules.
  Private integration remains open.
  Coordinate all target installations, net
  periodic capacity and gate commitment in one transaction; failed preparation
  must not publish success or consume resources. Complete committed lease and
  periodic scheduling even if a publication observer fails.
- Accepted status casts retain their captured handler after unregistration, but
  release still checks ownership. Released origins authorize only their original
  request/install pair and cannot be replayed.
- Capture routing candidates when a semantic event is actually dispatched;
  execute them using current registration validity and live state. Do not
  replay an old event to newly installed subscriptions.
- Published command, hit, explosion, damage, heal, life-reduction, death, signal and periodic tick routes
  distinguish one semantic event from receiver deliveries. A command ID is
  not a semantic-event deduplication key.
- Periodic tick observation is separate from its protected settlement trigger.
  Responses use the listener's holder and cannot impersonate the settlement
  handler. Signals inherit their causal budget; only actual later periodic
  ticks reset the ordinary budget, retaining periodic nesting.
- Preserve cancellation and pending/confirmed death priority. A canceled
  request must not manufacture a confirmation. Heal request amount is not
  actual life gain.
- A deferred damage delivery can be replayed only after it is taken from its
  active settlement, using the original event and receiver, and at most once.
  Completion must not discard an uncanceled taken delivery or an in-flight
  replay. An independently canceled replay reports failure rather than
  silently completing. An optional same-context completion receiver can
  sequence further replay work via `DeferredDamageReplayCompletedEvent`.
  Completion does not imply all derived events or death chains have settled.
  Damage admission is distinct from publication; deferred damage publishes
  only on replay. A settlement created after admission does not retroactively
  defer an already admitted request. Private replay orchestration still needs
  migration and integration tests.
- Actor, effect-producing ability, grant source and responding holder are
  different roles. Missing actors/sources remain null. Death roles are
  nullable killer and victim. Released effects retain source snapshots.
- Trigger conditions filter events; effect selectors belong to abilities.
  Tracking selectors can explicitly target the original event source or target;
  null and different-map roles do not become fabricated targets. Other selector
  shapes do not currently accept event-role origins. Private death and signal
  content using legacy trigger selectors still requires migration.
- Real grant-source identity is checked across bindings and contexts. Do not
  recreate source objects just to reuse their IDs or copy mutable ability state.
- Commands may address `AbilityInstanceId`. Multiple same-definition command
  abilities require explicit selection. Reject invalid ownership or bindings;
  never fall back from an unavailable selected instance to another copy.

## Conditions and content

- Source scope precedes the declaration-ordered condition array, which precedes
  probability and activation gates. Only false short-circuits an AND array;
  unknown still evaluates later entries. Evaluated errors propagate.
- Conditions share the strict typed expression engine with numeric formulas,
  but never use numeric-formula fallbacks.
- Built-ins expose only identities, definition strings and source categories.
  Gameplay fields require explicit event-specific module registration before
  schema sealing. Do not add global field caches keyed only by expression text.
- Identity values have distinct semantic types; do not expose GUIDs to YAML
  expressions or treat identity as authorization.
- Distance conditions use the correct map instance and its distance rules.
  Impact points retain their impact map independently of current actor control.
- Loading compiles immutable definitions, not runtime handlers. Validate through
  the unified DI catalog loader, not isolated domain parser calls.
  AbilityHandlerDefinition retains its normalized registered Type; compilers
  must not change it. Use this metadata for content contracts rather than
  constructing handlers or reflecting implementation classes during loading.
  Compile additional triggers through AbilityParserContext.CompileTrigger so
  their own event schema and references are validated. Install factories use
  AbilityHandlerFactory.CreateTrigger for named, runtime-bound triggers;
  never capture authoring DTOs/loading contexts or reuse installed handlers.

## Validation and documentation

- Follow the verified commands in the root READMEs. Run projects with shared
  outputs sequentially; simultaneous builds can lock shared binaries.
- Use focused tests for the changed behavior, then affected suites. Inspect
  detailed TestContext/stdout and stderr, not only exit status.
- LocalCombat is the executable expression reference. Keep the bilingual
  trigger-condition references aligned with its tested fields, syntax, null and
  error behavior. The content tests also execute signal and periodic-response
  configurations.
- Do not create a fourth sample just to demonstrate another expression.
- Do not commit, push, publish or claim release readiness without explicit
  authorization and completion of the public-source and third-party audit.
