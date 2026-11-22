# Trigger conditions

[简体中文](../zh-CN/trigger-conditions.md) · [Architecture](architecture.md)

[`combat.yaml`](../../samples/LocalCombat/combat.yaml) and
[`LocalCombatSample.cs`](../../samples/LocalCombat/LocalCombatSample.cs) are executable examples.

```yaml
trigger:
  type: ProjectileHit
  sourceAbilityIds: [bolt]
  eventSourceScope: Self
  conditions:
    - { type: Expression, expression: "event.source == holder && target.life > 0" }
    - { type: Distance, anchor: HitPoint, maximum: 10 }
```

`target.life` is registered by the sample module; it is **not** a built-in field.
Conditions filter events, while the ability's optional selector chooses effect
targets. Processing order is source scope -> conditions -> probability ->
ability activation gates. An accepted trigger is not a successful cast.

## Supported events and presets

Conditions currently support `Command`, `ProjectileHit`, `ProjectileExplosion`,
`DamageSkillHit`, `BeingDamaged`, `TargetLifeReduce`, `Heal`, `Death` (`Pending` or default `Confirmed`),
`Signal`, `ScheduledDamageTick`, `ScheduledHealTick`, and `UnitLevelUp`.
Other event routes are still being migrated. `None` cannot declare conditions.
`Heal` observes the heal request, not the actual life gain. A canceled request
does not produce a gain confirmation; a request of 10 may restore only 5.
`Signal` requires a nonblank `signalId`; other trigger types reject that option.
Signals keep their causality budget, including delayed signals. They may have no
unit source or producing ability; these fields remain null, not the recipient.
Periodic tick listeners observe each real tick once, separately from its
protected damage/heal settlement. A listener's independent response uses its
own holder and activation gates, not the original actor or settlement handler.
Tick conditions see the original effect's released ability snapshot.
Both damage trigger types can observe the original request at most once at actual
publication, not each receiver delivery. Their source and target are the
original actor and victim; a listener need not be either. Use explicit
conditions when a rule requires the holder to be the source or target.
Effect selection belongs in `data.selector`, not `trigger`. For a tracking
selector, `origin: EventSource` selects the original actor and
`origin: EventTarget` selects the original target; `origin: Source` selects
the responding holder. A missing actor/target or a target in another map fails
selection without consuming the ability's gate. Other selector shapes do not
currently support these event-role origins.

```yaml
data:
  selector: { type: tracking, origin: EventSource }
```

For example, these trigger objects can be used on a projectile response:

```yaml
trigger:
  type: Signal
  signalId: pulse
  eventSourceScope: Any
  conditions:
    - { type: Expression, expression: "event.source == null && event.target != holder" }
```

```yaml
trigger:
  type: ScheduledDamageTick
  sourceAbilityIds: [payload]
  eventSourceScope: Any
  conditions:
    - { type: Expression, expression: 'event.ability.definition == "payload" && event.source != holder' }
```

The full executable signal and periodic-response scenarios are in
[`DamageCatalogTest.cs`](../../tests/Content/DamageCatalogTest.cs).

- `eventSourceScope`: default `Self`; `Ally` includes self, `Enemy` requires a
  different team, `Any` also includes a missing actor. Conditions do not override it.
- `TargetRelation`: `relation: Self|Ally|Enemy|Any`; a missing target never matches.
- `Distance`: `anchor: Source|Target|HitPoint` and finite `maximum >= 0`.
  Distance uses the map's metric and the holder's representation in that same map
  instance. Missing spatial data does not match. `HitPoint` is valid only for
  projectile hits/explosions and uses the recorded impact map, not the actor's
  current map.
- `Expression`: a boolean `expression`. Presets and expressions share one
  declaration-ordered AND array. Incompatible fields or invalid enum values fail
  at load.

## Built-in fields

All listed fields are available to the supported events. Missing event data is
null, not fabricated. Unit, ability and grant-source identities are distinct,
opaque types, not strings or authorization credentials.

| Field | Type | Nullable |
|---|---|---|
| `holder` | Unit identity | No |
| `event.source` | Unit identity | Yes |
| `event.target` | Unit identity | Yes |
| `event.ability` | Ability identity | Yes |
| `event.ability.definition` | String | Yes |
| `event.ability.source` | Grant-source identity | Yes |
| `event.ability.source.category` | String | Yes |
| `listener.ability` | Ability identity | No |
| `listener.ability.definition` | String | No |
| `listener.ability.source` | Grant-source identity | Yes |
| `listener.ability.source.category` | String | Yes |

`event.ability` is the ability that directly produced the effect, not the first
ability in the chain. For `Command`, the requested ability is **not** the
event-producing ability: all `event.ability*` fields are null. Death maps its
source to nullable killer and target to victim. Explosions have no unit target.
Released effects retain their ability/source snapshot. Listener fields resolve
the actual bound instance; an absent or inconsistent binding is an error, not
a null grant source.

### Upgrade snapshots

`UnitLevelUp` observes the committed positive increase once, not its request
or a receiver-specific delivery. Both event source and target are the upgraded
unit; there is no invented producing ability. Self/ally/enemy source filters
still apply relative to the listener. It adds these non-null numeric fields
only to its own schema:

| Field | Recorded value |
|---|---|
| `event.level.old` | Previous level |
| `event.level.new` | Committed new level |
| `event.level.delta` | New minus old, strictly positive |

These are immutable event values, not live unit-property getters. Upgrade
observation and invocation retain the request priority and causal chain.
Re-publishing the same notification, including a record clone, is rejected.
The optional Loadout `TalentGrowth` ability can observe this trigger and
apply an explicitly selected tree rule; Combat itself awards no talent points.

## Syntax and types

Precedence, highest first: parentheses/functions; unary `! + -`; `* /`;
`+ -`; `< <= > >=`; `== !=`; `&&`; `||`.

Literals: finite numbers (including exponent notation), double-quoted strings,
and reserved lowercase `true`, `false`, `null`. String escapes are `\"`, `\\`,
`\n`, `\r`, `\t`. There is no assignment, reflection, arbitrary method invocation,
or script execution. Field names are case-sensitive.

| Type | Allowed comparisons |
|---|---|
| Finite double | `== != < <= > >=` |
| Boolean | `== !=` |
| String | `== !=`, ordinal and case-sensitive |
| Identity | `== !=` with the same identity type |

No implicit conversions. Explicit `== null` / `!= null` is supported.
`1 == "1"`, `true > false`, `holder == event.ability`, and identities compared
with GUID strings fail at load, including in otherwise short-circuited branches.

Functions are case-insensitive: `min`/`max` (at least two numeric arguments),
`clamp` (three), `floor`, `ceil`/`ceiling`, `round`, `abs` (one each).
`if(condition, whenTrue, whenFalse)` requires a boolean condition and same-typed,
non-null branches; only the selected branch is evaluated, and an unknown condition
produces an unknown result. `round` uses midpoint-to-even. Arguments evaluate left to right.

Examples executed by LocalCombat:

```text
event.source == holder && event.target != holder
event.ability != listener.ability && event.ability.definition == "bolt"
event.ability.source == null && listener.ability.source == null
event.ability.source.category == null || listener.ability.source.category != "Equipment"
(2 + 3) * 4 / 2 - 1 == 9 && -2 < +1 && 2 >= 2 && 2 <= 3 && 3 > 2
min(3, 2, 1) == 1 && MAX(1, 3) == 3 && clamp(5, 0, 3) == 3
if(event.source == null, true, event.source == holder)
floor(1.9) == 1 && ceil(1.1) == 2 && ceiling(1.1) == 2 && round(2.5) == 2 && abs(-2) == 2
"Bolt" != "bolt" && (true == !false)
abs((0.1 + 0.2) - 0.3) < 0.000001
```

Finite doubles still have floating-point rounding; use an explicit tolerance
when approximate numeric equality is intended.

## Nulls, ordering and failures

Ordinary comparisons with missing values yield unknown; even two missing
sources are not equal. `!unknown` remains unknown. Only final true matches.
`false && ...` and `true || ...` short-circuit. Unknown does not short-circuit:
`unknown && false` is false and `unknown || true` is true. In condition arrays,
only false stops evaluation; a later executed error after unknown still fails.
`unknown` is explanatory terminology, not a writable literal.

Thus `event.ability.source.category != "Equipment"` excludes missing sources.
Include them explicitly with `== null || ...` when intended. Neither expression
widens the default actor scope.

Unknown fields, type mismatches and invalid function signatures fail during
content loading. Evaluated division by zero, non-finite numbers, invalid clamp
bounds and field errors propagate. Condition expressions have **no fallback**,
including in production. Numeric formulas use the same type engine but have a
separate, explicit production-only diagnosed fallback policy. Committed effects
are not rolled back by a later error.

## Module fields

Live unit life/level/team and general event amounts are not built-ins. The
recorded upgrade fields above are event-specific exceptions. Trusted modules explicitly
register fields for each applicable event before the catalog schema is sealed:

```csharp
registry.TriggerConditions().For(TriggerType.ProjectileHit)
    .RegisterNumber("target.life",
        value => value.EventTarget?.Property.LifePoint, nullable: true);
```

The example's complete extension set is `target.life` (nullable number,
ProjectileHit conditions) and `source.level` (number, combat formulas).
Extensions may register number, boolean, string or typed identity fields.
Duplicate and late registrations fail. Catalogs bind independently; registering
a hit field does not make it available in command conditions. Getters read state
at evaluation time and their errors propagate. Trusted extension code is not
sandboxed by the expression language.

Numeric damage/mitigation formulas use a separate schema: the damage module
registers `ability.level`, nullable `source.level`, nullable `target.level`, and
`raw` (nullable number, populated for mitigation). A mode may register its own
fields; for example, Adventure registers `target.armor.<type>`. These fields are
not automatically visible to conditions. A numeric formula requires a number,
while a condition requires a boolean.

Passive `BaseProperty` and status property formulas use another schema: `ability.level`, nullable
`source.level`, `target.level`, nullable `target.life`, `target.lifeMax`,
`target.energy` and `target.energyMax` are numbers. Built-in additive targets are
`lifePointMax`, `energyMax`, `moveSpeedBase`, `moveSpeed`, `experienceMax`,
`visionRange` and `stealth`; modules may register more. Formulas use the applied
status's captured level and the source and projected target values at
recalculation. Passive formulas use their owner's current ability level;
for a self-sourced formula, both source and target use the projected owner
properties.

Both `BaseProperty` and `Status` data accept a projection recalculation policy:

```yaml
recalculateOn: [AbilityLevelChanged, TargetLevelChanged]
modifiers:
  - { target: lifePointMax, formula: "ability.level * 10 + target.level" }
```

Omission means `[ProjectionChanged]`, preserving evaluation whenever the existing
projection lifecycle runs, including installation, removal, stacking and unit
level changes. `AbilityLevelChanged` and `TargetLevelChanged` retain the last
evaluated per-instance values until the corresponding level differs; they may
be combined. `[]` retains each modifier's first value for that registration.
Initial registration, re-registration and a changed actor/recipient always
evaluate afresh, without recreating the ability. These are projection policies,
not new activation trigger types or arbitrary event subscriptions. They do not
install a watcher on another unit's changing fields.

Stacks multiply the retained per-stack values; they do not resample formulas
unless the policy requests evaluation. A retained value that becomes
unrepresentable at the new stack count rejects the operation rather than
silently resampling or truncating. Cached values, fallback markers, levels and
the final projection are staged in the same transaction. Compiled definitions
never contain another runtime instance's cached values.

Unknown, numeric, duplicate or null entries are invalid. Use `[]`, not `[None]`;
`ProjectionChanged` cannot be combined with the narrower level policies.

