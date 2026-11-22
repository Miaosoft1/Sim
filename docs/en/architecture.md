# Architecture and execution model

[Overview and commands](../../README.md) · [中文](../zh-CN/architecture.md)

This describes the current isolated extraction, not a released API. The
loopback/Console prototype is tested; remaining network integration, private
integration and the final source and license review are still outstanding.

## Layers

| Layer | Responsibility |
|---|---|
| [Core](../../src/Core) | Context, event scheduling, receivers, cancellation and atomic transactions. |
| [Combat.Map](../../src/Combat/Map) | Map definitions and instances, movement, spatial objects and visibility. |
| [Combat.Skill](../../src/Combat/Skill) | Units, abilities, targeting, activation gates, effects and trigger routing. |
| [Content.Core](../../src/Content/Core) and [Content.Loading](../../src/Content/Loading) | Content contracts, module registration and catalog composition. |
| [Skill.Content](../../src/Combat/Skill.Content) and [Map.Content](../../src/Combat/Map.Content) | Compile the selected YAML formats into runtime-domain definitions. |

Content adapters depend on their runtime domains, not the other way around.
The host chooses its modules explicitly; catalogs are separate and do not share
mutable authoring state. A compiled ability definition is reusable, whereas
each installation creates runtime handlers and state. A Context is an isolated
simulation with one logical execution owner.

`AbilityHandlerDefinition.Type` retains the normalized registered content type.
Hosts can validate build/control contracts without constructing handlers or
inspecting their implementation types. A compiler that changes that type is
rejected at load time with the source file and ability ID.

Extension compilers can use `AbilityParserContext.CompileTrigger` during loading
to compile an additional trigger against its event's condition schema and source
references. At installation, `AbilityHandlerFactory.CreateTrigger` creates a named
trigger bound to that actual ability runtime instance. Several triggers may invoke
one ability; never reuse an installed handler or capture authoring DTOs/loading
contexts in the runtime factory.

## Events and handlers

A Context owns a simulation-time queue. `Dispatch` enqueues work; a runner
such as `RunUntil` advances the queue and invokes registered handlers. The
[scheduling sample](../../samples/EventScheduling/EventSchedulingSample.cs)
shows priority, insertion order, delay, scope cancellation and an unrelated
follow-up. Do not equate enqueuing with execution.

Ability installation and removal are requested through events. The unit's
installer coordinates changes; atomic transactions prevent partially applied
handler and state changes. Triggers evaluate the configured event and emit an
invocation event. They neither directly call another handler nor own the
responding ability's mutable state.

Current single-publication routing covers commands, projectile hits and
explosions, damage requests, life reductions, pending and confirmed deaths, heal requests,
signals and periodic damage/heal ticks. Receiver deliveries are not new
semantic events. Candidate listeners are captured at actual dispatch and
their registration is rechecked when invoked. A separate real event remains
separate even if it shares a command ID and timestamp.

Damage admission is a separate queued request, not a semantic publication.
Admission determines whether an active settlement defers the damage. Otherwise
it enqueues its publication; a later settlement does not retroactively defer
an already admitted request. A deferred request publishes only upon replay.
The original receiver and event are required, and replay is at most once.
Completion waits for the replay's handler invocations, not all derived events
or death chains; an independently canceled in-flight replay reports failure. An optional
same-context completion receiver receives `DeferredDamageReplayCompletedEvent`
after those invocations, so an event-driven host can sequence its next replay.
Hosts must separately validate derived-chain finalization; one replay completion
does not prove that all derived events or death chains have finished.

## Combat chain

[LocalCombat's content](../../samples/LocalCombat/combat.yaml) and
[executable scenario](../../samples/LocalCombat/LocalCombatSample.cs) load
explicitly selected modules and run two independent contexts against one
compiled catalog:

1. A command addresses `bolt` and its runtime instance.
2. Its projectile hits; `bolt-damage` evaluates its conditions and applies
   configured damage through the target's receiver.
3. The resulting life-reduction event triggers `heal-delivery`; its hit
   activates `heal-payload`.
4. The test verifies damage, healing, charge exhaustion and distinct runtime
   ability identities. Read its detailed output as well as the test result.

A second scenario installs `endurance` and applies `fortify` twice,
demonstrating passive and status modifiers, stacking, expiry and uninstall.

Projectile delivery data can declare deterministic impact behavior and cast
movement policy:

```yaml
data:
  speed: 100
  explosionTrigger: OnHit
  gate:
    windupSeconds: 0.2
    casting:
      canMove: true
      canInterrupt: true
      interruptOnDamage: true
```

`explosionTrigger` accepts `None`, `OnHit` and `OnArrival`; it controls the
runtime projectile's explosion fact and does not replace downstream
`ProjectileHit` or `ProjectileExplosion` effect triggers. `gate.casting` is
optional. When present, both movement/interruption flags are required; its
duration is `windupSeconds`, and an explicit `durationSeconds` must match it.
Channel duration and interrupt refunds remain separate, explicit fields.

Finite charges may also restore in response to configured events:

```yaml
gate:
  charges:
    maximum: 3
    initial: 0
    recovery: { intervalSeconds: 10, mode: Parallel }
    conditionalRecovery:
      - amount: 1
        trigger: { type: Signal, signalId: recharge }
```

Each recovery trigger uses the normal source scope, conditions and probability
and targets its bound ability instance; it does not activate that ability.
It only observes while that instance and trigger are registered and authorized.
Queued recovery is rejected if either registration or authorization changes.
Restoration caps at the maximum; it retains the earliest pending timer for serial
recovery and cancels the latest timers for parallel recovery.

### Real ability sources

`AbilitySourceInstance` owns the original ability objects and their bound runtime
parts; `Unit.Abilities.Sources` indexes the sources currently assigned to a unit.
The preset installer's catalog factory materializes them, including unheld
sources, only inside an `AbilitySourcesChangeRequestEvent`. A source enters one
Context and cannot later move into another one. Empty sources are valid.

One request can assign, detach or swap several sources and change an existing
ability's level by runtime ID. Source ownership, selected registrations, trigger
routes, levels and property projections commit together. Factory or projection
failure publishes no success and leaves the prior state unchanged. Post-commit
publication errors report a `CommittedOperationException` with the operation ID;
other committed timers and notifications are still scheduled.

With `allowMultiple: true`, each member retains its own runtime object and
contribution, even when a source contains the same definition twice. Otherwise,
the highest actual ability level wins on each unit. A tie retains the current
instance, then uses stable source/member registration order. A gate failure
does not select a reserve. In-place upgrades preserve runtime identity, charges
and cooldowns; materialized levels change only through source events and their
coexistence policy cannot change.

Only selected registrations observe new activations and conditional recovery.
Reserve and unheld instances retain their simulation-time charge recovery.
An accepted cast can finish after a selection change, but release rechecks the
original holder's authority after actual detachment or transfer. New-owner
activations keep separate reservation identities and cannot overwrite old casts.
Source transfers move original objects and gates, never copied state.

These are trusted simulation operations, not network authorization. `Holder`
means activation authority, not physical inventory ownership. Inventory,
equipment, consent and talent-tree authorization are separate Loadout concerns. Managed
source parts cannot be changed through definition-ID install/uninstall or bare
atomic handler changes; use source events instead.

### Unit upgrades

Combat, not Loadout, owns `UnitLevelUpRequestEvent`, the committed
`UnitLevelUpEvent` and `UnitLevelUpResultEvent`. Runtime requests specify an
expected level and an explicit higher level. Stale, non-increasing and
out-of-range requests return typed rejection without an upgrade publication.
The protected preset handler commits level, formula projection and retained
values together. Formula failure publishes no success; post-commit publisher
failure preserves the new level and still schedules the result. Events retain
the request priority so an eventual reward chain does not lose its ordering.

An optional `CompletionReceiver` returns success/rejection results to a registered
coordinator in the same Context; the actual upgrade still publishes once on the
target unit. Without it, results still return to the unit. Foreign or unregistered
destinations fail before mutation. For successive relative increases, wait for
the result before reading the next expected level. An independent commit can
produce `StaleLevel`; the caller must explicitly reconsider it, not report success.

`Unit.MaximumLevel` is constructor-time configuration (default 10), not a
universal static limit. Unit and ability levels remain independent. Existing
`SetLevel`/`LevelUp` helpers directly establish low-level state; they are not the
runtime growth event flow. `UnitLevelUp` triggers now observe the committed
increase through the common router, with typed old/new/delta fields, source
filters and original priority. A record clone cannot republish it. The optional
Loadout talent rules below can observe these records without a reverse dependency.

### Loadout inventory

The optional `Miaosoft.Sim.Loadout` runtime now has item definitions/instances,
owner-bound inventories and acquire/equip/unequip/discard requests. Each item
has a real source, including empty sources for neutral items. Backpack capacity
counts unequipped items; carry capacity includes equipped items. Replacement
validates the final layout, not a temporary remove/add order. Discard destroys
the instance and prevents reacquisition.

Runtime creation is an event, not an unmaterialized public constructor:
`ItemCreateRequestEvent` produces `ItemCreatedEvent` only after the preset
installer has created the original abilities and bound the item to its Context.
Creation grants neither inventory ownership nor activation authority and does
not resample an unrelated coordinator's property projection. Acquisition and
equip reuse those original objects. A failed factory exposes no half-created
item; even a neutral, never-acquired item cannot cross Contexts afterward.

Inventory policy prepares physical ownership, item revisions and placements
inside source execution through `IAbilitySourceChangePreparation`. The preset
installer alone materializes and mounts abilities and commits all cells together.
Preparing the inventory transaction in an earlier queued handler would permit
stale intermediate plans even with a single Context execution owner. Preparations
must only stage domain state, not commit, publish or create handlers. Normal
capacity and explicit version mismatches produce typed rejection results;
invalid factories/projections fail without changing inventory or capability.
Inventory completion is scheduled even if another post-commit publisher fails.

`TradeSession` adds bilateral, versioned consent for exact item IDs and explicit
destination slots. A new offer resets both consents; inventory changes invalidate
them without locking inventory. The final source-execution preparation rechecks
the session's shared policy and inventory revisions, validates both net layouts,
and commits item ownership, original ability sources and closed consent together.
Full equipped inventories can swap their original items and gates; a one-way
gift creates no artificial counterpart. Projection failure moves nothing and
leaves the session open. A completed session cannot execute again. Applications
must supply the shared eligibility policy; it is rechecked at actual commitment,
not only at confirmation.
Both participants must retain their unique current inventory and matching
trade feature through final preparation; stale detached features fail before
ownership changes, rather than mutating an obsolete inventory object.

`Miaosoft.Sim.Loadout.Content` registers inventory authoring through
`AddInventoryContent`; the loader must also explicitly select
`LoadoutContentServiceCollectionExtensions.InventoryModuleId`. This does not
enable runtime features. The `item` collection compiles once across documents;
referenced abilities can appear in later files and are validated after compilation.
It retains immutable definitions, never runtime abilities. Unknown fields,
duplicate IDs, missing references and invalid ability levels fail with their
source file. Neutral items need no ability-content module. An item document
loaded alongside the referenced ability definitions can contain:

```yaml
schemaVersion: 1
item:
  - id: tool
    name: Tool
    slotType: weapon
    abilities: [{ id: endurance, level: 3 }]
```

Obtain its definition with `catalog.GetSection<ItemCatalog>("item").Require("tool")`
and create a fresh runtime item through the creation event above.

### Role-owned talents and equipment grants

Select `loadout-talents` separately from inventory, and register it with
`AddTalentContent`. Its immutable `TalentTreeCatalog` compiles the `talentTree`
collection, validates node dependencies, ranks, exclusions and ability references,
and does not construct runtime handlers. A point-only tree needs no ability module.
For runtime composition, bind the unit to its context, construct
`UnitTalents(unit, definitions)` with the selected catalog definitions, and add its
`TalentDirectoryHandler`. This prepares each role-owned tree and its retained
domain observer, not its ability instances. A tree is published on first committed
grant. Only the preset installer mounts observers and creates ability handlers.

Each tree has an independent point pool, ranks, original node sources and growth
counters. Authorize/revoke with `TalentAuthorizationRequestEvent` and a real
`IAbilitySource`, not an invented count or an item definition ID. Duplicate grants
do not increase the count. Losing the last grant removes that tree's observer,
growth subscriptions and new ability authority, not its original objects or
progress. Regrant restores the same tree/observer/abilities; fixed-owner sources
cannot transfer to another unit. Queued operations carry authority epochs and
recheck them at actual preparation. No respec operation is implemented.

`TalentNodeAllocateRequestEvent` consumes one point after checking maximum rank,
explicit node/branch/tree prerequisites and exclusions in both directions. `tier`
is presentation, not an implicit five-points-per-tier rule. Abilities keep their
configured levels unless `growAbilitiesWithRank` is true; then each original
ability's level is its initial level plus purchased rank minus one. Rank, points,
source selection, ability levels and property projection commit together.

`TalentGrowth` is an ordinary observing ability using the shared trigger router.
Its `data` identifies a tree and rule; the tree's matching rule identifies that
ability. Loading checks both directions without instantiating handlers. Rules
accumulate valid progress, award `pointsPerReward` at `progressPerReward`, and retain the
remainder. `maximumRewards: 1` is one-shot; an omitted maximum repeats without a
reward-count cap. Unauthorized events are not accumulated or caught up later.
For example, load the ability/damage-type modules plus the selected talent module:

```yaml
talentTree:
  - id: training
    name: Training
    growthRules:
      - { id: practice, abilityId: training.progress, progressPerReward: 5 }
ability:
  - id: training.progress
    type: TalentGrowth
    allowMultiple: false
    trigger: { type: Signal, signalId: practice }
    data: { treeId: training, ruleId: practice }
```

Eleven matching signals award two points and retain one event. The counter belongs
to the role's tree, not the trigger. Conditions may instead select committed
`UnitLevelUp` records and inspect their typed delta.

`TalentGrowth.data.progress` is an optional numeric expression using that trigger's
typed schema; its default is `"1"`. It must evaluate to a positive integer no
larger than `int.MaxValue`. Null, fractional, nonpositive, non-finite and out-of-range
quantities fail explicitly, without rounding or a silent fallback. One observation
can contribute several progress units, complete several thresholds and retain the
remainder; `maximumRewards` still caps reward occurrences. Point overflow rejects
the whole progress update. For a rule awarding one point per **actual level**:

```yaml
trigger: { type: UnitLevelUp }
data: { treeId: training, ruleId: practice, progress: "event.level.delta" }
```

With threshold one and three initial points, upgrades 1->4->10 award 3+6 points,
ending at twelve. Creating a unit directly at level four does not reconstruct
three historical rewards; upgrades while unauthorized are not caught up later.
An invalid progress formula does not roll back its already committed upstream event.

Equipment grants are a third explicit selection: register `AddEquipmentTalentContent`
and load `loadout-equipment-talents` alongside both inventory and talents.
`equipmentTalent: [{ itemId: tool, treeIds: [training] }]` links existing definitions;
backpack ownership grants nothing. Pass
`EquipmentTalentCatalog.CreatePreparation(talents)` into the `UnitInventory`
constructor to enable the runtime bridge. Merely loading content enables no runtime feature.

The bridge joins inventory operations and bilateral exchange at the same commit
point. It computes the **net** grants per tree, so replacing two items granting the
same tree keeps its observer and authority epoch. Two equipped originals count
twice; removing one cannot revoke the other's grant. Trading moves the original
item and its grant, never the donor's role tree, learned ability objects, spent
points or event remainder. The recipient uses its own tree. Projection failure
rolls back inventory, grants, observers and effects together.

These are trusted in-process capabilities, not detached network snapshots.
Private integration remains pending. Core, Combat and the basic samples do not
reference Loadout.

### Death-authorized pickup

Compose a `DeathTradeDirectory` over the corpse owner's inventory with an explicit
`IDeathTradeDistributionPolicy` and live interaction predicate, then register its
`DeathTradeHandler`. Distribution selects beneficiaries and exact item instance
IDs from a confirmed death; the framework does not assume the killer, a faction,
or a range formula. The interaction predicate supplies the mode's geometry,
authorization and **both participants'** applicable ranges, including any corpse
base-range rule.

The unit's `CurrentDeathId` is null while alive and is bound to the committed
death attempt before publishing `DeathResolvedEvent`. Capabilities retain this
identity, the beneficiary and the exact death representation. Revival and a
subsequent death cannot reactivate an old capability, including the interval
before new confirmed-death observers run.

The beneficiary's current inventory/trading feature accepts
`DeathTradeInspectRequestEvent`; only a successful live in-range inspection
returns authorized item views. `DeathTradeClaimRequestEvent` binds that inspection
revision and checks both inventory revisions again at actual preparation.
Selection is an explicit one-way pickup with destination slots, not a dead
participant's fabricated trade confirmation. Foreign IDs, stale inspections,
changed inventories, capacity failures and policy rejections report typed results.
The framework independently rejects an exact corpse removed from its map even
when the supplied policy returns true.

Original items, abilities and equipment-tree grants use the same atomic transfer
path as normal exchange. A projection failure preserves both inventories, source
authority and inspection for a valid retry. Success invalidates consumed inspection,
but leaves the remaining inventory and authorization available for another
inspection. Explicit `DeathTradeCleanupRequestEvent` destroys only remaining items
and closes access; removing a corpse does not silently destroy its retained items.
The mode owns corpse placement/removal. These are trusted server-side runtime
capabilities: network admission must bind the actual caller and project detached
views, never accept a capability object from the wire.

### Status application and periodic payloads

```yaml
- id: fortify
  type: Status
  allowMultiple: false
  trigger: { type: ProjectileHit, sourceAbilityIds: [bolt] }
  data:
    durationSeconds: 5
    stackPolicy: StackOnly
    stackLimit: 2
    modifiers:
      - { target: lifePointMax, formula: "ability.level * 10 + target.level" }
```

The applied instance keeps its release-time ability level. A real-time lease
must use `Independent` and a positive duration; `protectedUntilExpiry` is only
valid for such leases. Numeric fallbacks must be explicit and are used only by
the production evaluation policy with diagnostics.

Status `data.periodicEffects` compiles standard damage/heal templates, not
additional abilities or triggers:

```yaml
periodicEffects:
  - type: Damage
    tick: { tickCount: 3, tickIntervalSeconds: 1, firstTickDelaySeconds: 1 }
    damage: [{ type: impact, formula: "ability.level * 2" }]
  - type: Heal
    tick: { tickCount: 3, tickIntervalSeconds: 1, firstTickDelaySeconds: 1 }
    amount: 2
```

Declare the damage type in the selected catalog. Damage components use the same
`type`, `formula`, optional `fallback` and `priority` as ordinary damage.
Healing accepts either a non-negative integer `amount` or
`restoreToMaximum: true`, never both. Each entry requires a positive `tickCount`
and `tickIntervalSeconds`; the optional non-negative first delay defaults to zero.
An immediate first tick keeps its creating causal budget. Later ticks use
simulation time, including on a status with an independent real-time lease.
Damage and healing are independent queued effects, not a promised
damage-then-heal sequence.

The applied effect fixes its ability level and source snapshot at release;
each damage tick evaluates the formula against then-current unit properties.
Explicit rule applications use their requested level (default 1), keep their
real rule origin and may have no actor or producing ability. The recipient
never substitutes for a missing actor. A finite status duration also bounds
explicit applications unless the request provides its own simulation expiry.
Uninstall or expiry cancels remaining owned ticks. The content cannot override
its compiled effects with another periodic list in an installation request.
Within one source/actor group, same-level reapplication keeps the original
periodic executors and tick times. `StackOnly` and `ExtendAndStack` update the
stack count up to the configured limit; `ExtendOnly` does not. Each compiled
periodic effect uses the current stack count when it creates a settlement.
Damage is quantized per stack at the existing component precision, then
accumulated across stacks before mitigation; fixed healing amounts multiply
by the stack count, while restore-to-maximum remains capped at the maximum.
Refreshing a lease does not restart completed ticks. Higher-level replacement
removes the old executors and starts the new state's effects with one stack;
`Independent` still creates a fresh state and schedule for every application.
The [content tests](../../tests/Content/DamageCatalogTest.cs) exercise both
effect types, release-time upgrades, live target properties, actorless rules,
expiry, atomic quota rejection and same-level stacking/extension without
duplicate executors.

`Death` is an explicitly installed passive ability (`trigger: { type: None }`,
omitted data or `data: {}`). It handles the existing pending-death/commit chain
and publishes the confirmed death once; it is not a command or a synthetic death
notification. Unknown data fields and active triggers fail during content loading.
The framework does not silently install mortality on every unit. The network
Loadout scenario installs it on its NPC and drives death through real damage.

`BaseProperty` is a separate passive ability with a `None` trigger; it shares
the additive modifier schema and priority order with applied statuses, but is
not itself a status. Its level can change without replacing its runtime
instance. Both kinds currently recompute together when the unit projection
changes. The following recalculation policy applies to all of an ability's
modifiers; it does not provide per-formula arbitrary event subscriptions.

```yaml
- id: endurance
  type: BaseProperty
  allowMultiple: false
  trigger: { type: None }
  data:
    modifiers:
      - { target: lifePointMax, formula: "ability.level * 5" }
```

The trigger's source scope and conditions filter the *original event*;
the response executes as its own holder and uses its own activation gates.
An ability's optional selector chooses effect targets. A heal request and
confirmed life gain are different observations. Released effects retain their
ability snapshot, including the ability level at release; runtime unit
properties can be read later at settlement. An upgrade during windup uses the
level at release, without rewriting earlier effects.
Status listeners can use runtime gates without becoming command-addressable;
their applied leases begin at release. They filter out dead or retired targets
at release and fail without consuming their gate if none remain. An accepted cast can finish after its
listener is unregistered, provided the original owner remains authorized;
released status applications bind their origin to the original request and
installation, rejecting replay. Status YAML accepts the catalog's supported
trigger types; `None` declares an effect for explicit application with a real
origin rather than registering a listener. Commands with no matching command
ability or an unauthorized selected ability fail before publication to observers.
Status modifiers, stacking, leases, gates and periodic content are supported.
The [property formula reference](trigger-conditions.md) documents the optional
projection recalculation policy; arbitrary event subscriptions are not implied.
Private integration is still pending. A gated status
prepares all selected installations and their periodic effects with the gate
and energy cost in one transaction. Capacity rejection leaves the gate and all
targets unchanged; post-commit publication failures are reported as committed
and the remaining committed events are still scheduled.

Definition ID identifies reusable content, runtime instance ID selects an
installed ability, and an activation/command ID identifies an operation. None
is a substitute for validating ownership and authority. Multiple installed
command instances with the same definition require explicit selection.

## Content extensions and expressions

The sample registers content modules using dependency injection, selects them
for one catalog, and adds a `ProjectileHit` condition field `target.life`.
That field is sample-specific. Registration is event-specific and sealed
before definitions are used. See the [complete current expression reference](trigger-conditions.md)
for supported events, built-in fields, null behavior, operators and executable
examples. Invalid content is rejected at load, rather than silently ignored.

## Verification and next boundaries

Run the commands in the [root overview](../../README.md#run-and-verify)
sequentially and inspect per-test output. The first two samples currently use
the test runner, not interactive clients. The third sample exercises real
loopback networking with the shared Client SDK; the sole interactive client is
Console. See [network contracts and limits](networking.md). The project is not ready
for publication until the remaining implementation, private migration and
rights checks are complete.
