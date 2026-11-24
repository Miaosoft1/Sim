# Network prototype

[Overview](../../README.md) · [中文](../zh-CN/networking.md)

## Composition

`Protocol` defines versioned contracts and registered, directional YAML event
codecs. SignalR supplies connection/RPC framing; YAML remains the event payload
format (SignalR's own framing is not a custom YAML transport).
`Runtime.Abstractions` defines simulation and observation seams. `Runtime`
serializes ingress alongside `Context.NextAsync`; `Server` adapts actual Combat
events and observations. `Server.Hosting` binds SignalR without account,
matchmaking or scoring implementations. Client/Console reference none of these
server implementations. The neutral arena and its balance belong to the server
application; its canonical YAML is shared with LocalCombat.

## Admission and observations

Anonymous demo admission must be explicitly enabled and the sample server binds
one loopback endpoint. The host chooses one of two preconfigured participants and
issues a random, in-memory, session-bound credential. A name cannot claim another
participant. Rejoin invalidates the old connection generation and prepares a full
snapshot; clients apply it before acknowledgement and subsequent buffered frames.
Credentials are not persisted and are redacted from their record printing.

Frames atomically publish a world and module updates at one state version.
Capabilities identify each module and version. Full snapshots include full state
or explicit removal for every negotiated module; omitted modules in subsequent
frames mean unchanged, not removal. Delta codecs require existing state.
The common host removes controlled-unit-only state for unitless recipients before
calling custom projectors. Unknown versions and dropping an established module on
rejoin are rejected. Client codecs must return detached immutable state.
Their full/delta methods receive the current `SessionBinding`; private codecs
must validate owner identity before the entire frame commits.
The prepared-frame buffer is bounded; overflow invalidates the old snapshot and
requires a fresh preparation rather than silently dropping updates. Fresh full
preparation retains undelivered business outcomes from the prior prepared
snapshot and buffered frames. This does not make already-sent outcomes durable.

World observations include map instance identity and authored room/corridor
geometry, owner-only experience, visible active projectiles and applied abilities
carrying the Status label. Status snapshots preserve runtime/application identity
and source; simulation expiry comes from the current committed install lease,
not the configured duration or an assumed equality of application and lease IDs.
Real-time leases report remaining wall time separately. Rejoin restores an
existing flight; command activation can complete before its later hit.

`ProjectileFact` carries confirmed emission, semantic hit and explosion facts
with actual projectile/ability/map identities, execution time and impact position.
Emission is published only after ballistic setup succeeds; receiver-specific hit
deliveries are not additional hits. These are transient server-only events,
captured with the current observer's actual position visibility. They are sent
only on live acknowledged connections, not retained through preparation,
overflow, disconnect or rejoin. A full snapshot restores existing flights, never
replays their cast sound or an earlier impact. Business results retain their
separate recovery behavior. Renderers choose explosion rather than hit visuals
when the same projectile explodes at that hit; these facts do not duplicate
combat settlement or prescribe an asset system.

Hex maps additionally publish their actual playable axial cells, authoritative
world centers, cell size, origin and orientation. Grid occupancy and reservations
are not public map metadata. Entrances are projected from live map connections
only when visible to the controlled unit; unitless recipients receive none.
A destination instance ID identifies a connection, not permission to enter or
authorization to observe the destination world. Per-role return paths stay
private. The SDK validates and freezes nested geometry before committing the
entire frame; malformed geometry cannot partially replace the previous world.
Both hex orientations, actual grid movement, entrance visibility and full
recovery are exercised in [the existing network sample](../../samples/NetworkSession/NetworkSessionSample.Maps.cs).

## Queue receipts are not business results

Each participant starts at operation one. Admission checks current connection and
input authority before consulting its last **1024** accepted receipts. An exact
retry returns the original receipt without enqueueing again; different content
at the same sequence conflicts. Older evicted sequences expire, gaps report the
expected number, and rejected input or queue pressure does not advance it.
`long.MaxValue` is reserved for exhausted numbering and cannot be accepted.
Unauthorized receipts reveal no next-sequence hint.

Operation sequence, correlation ID and publication sequence are independent.
Several operations may share a correlation ID. Rejoin reports the next accepted
operation, not which work has executed in its snapshot. Business outcomes are
separate typed server events: cast success concerns command execution, not a
promise of a later hit; movement success updates a destination, not arrival.
The SDK retains the original unresolved operation and never silently replaces
its sequence or payload after transport failure. An expired/uncertain result
must not be blindly submitted again under a fresh number.

`OperationResult.Status` is explicitly `Succeeded`, `Rejected`, `Aborted` or
`Unknown`; `Unspecified` is invalid. `Reason` describes the disposition.
`Aborted` means accepted work was prevented from executing. `Unknown` does not
promise absence of side effects and is never permission to replay under a new ID.

## Completion

Only a trusted host requests completion; this is not a client Hub method.
Completion waits for the current timestamp's event/handler chain, records the
terminal event, discards pending combat and freezes simulation time. Accepted
executions without a published terminal outcome report `Unknown`; queued inputs
that did not execute report `Aborted`. Snapshots remain completed across rejoin,
and clients reject a later frame that tries to restart them.
Mode result events implement `ISessionResultRecordEvent.CreateResultRecord()`;
the owner calls it after `Execute` is assigned, not through a computed record
property that queue logging could evaluate early.

The same serialized owner continues processing only explicitly allowed completed
inputs and real-time lease cleanup. It does not resume the old combat queue.

## Optional Loadout adapter

`Miaosoft.Sim.Loadout.Protocol` and `.Server` are publicly neutral optional modules.
The private `Miaosoft.Sim.Loadout.Client` observation adapter is separately selected.
The base Combat Server, Client and Console do not reference private Loadout content. Register
`EventCodec.CreateBuilder().AddLoadout()` at both endpoints. Wrap the simulation
in `LoadoutSessionSimulation`, supplying the actual unit features and
trusted exchange/death directories; register its `Observation` with the owner.
The adapter depends on `Runtime.Abstractions`, not the execution-loop assembly
or a concrete battle host. Select `LoadoutClientModule` in the SDK to negotiate
the version-one `loadout` observation. The private adapter depends only on the public
client and protocol projects.

For changing membership, supply `Func<LoadoutSessionFeatures>` instead of fixed
collections. The mode publishes a new, Context-bound membership snapshot during
setup or on the existing serialized event chain; the provider is not a
cross-thread mutation API. Membership collections are copied and frozen, but
their unit/inventory/session handles still refer to the original live objects.
Assessments, execution and projection read current membership. A removed feature
rejects queued input explicitly; an already executing operation retains its
original submitting receiver for its result. Close exchanges and clean up death
capabilities through domain events before retiring their directory entries.

Feature owners need not be login participants: NPC inventory and death directories
can be included without adding admission credentials or control bindings.
Only `ISessionSimulation.ParticipantUnitIds` can submit participant commands.
The snapshot constructor validates original inventory ownership and shared Context;
a provider returning a snapshot from another Context fails explicitly. Losing a
death handler suppresses its private observations as well as future commands.
Mode state and bootstrap publication remain the mode host's responsibility.

For dynamic world units, `BattleSessionSimulation` separately accepts a
`worldUnits` provider. Its constructor's unit list remains the fixed admission
roster; the provider supplies the original, initialized units used for visibility,
target lookup and projectile capture. A later NPC can be observed and targeted
without becoming a login actor. Mutate the directory only during setup or on the
same execution chain. Foreign-Context units and replacement objects carrying an
actor's ID fail explicitly; queued commands are reassessed before execution.
Removing a world entry does not retire its domain state or revoke its capabilities.

Wire requests carry IDs and expected revisions, never runtime inventory objects:

- Equip/unequip/discard require the current owner's exact item and inventory revision.
  Acquisition and item creation are trusted gameplay operations, not client commands.
- Talent allocation binds the role's own tree, revision and authority generation;
  clients cannot award points or grant trees.
- Exchanges bind current registered inventories, exact offered instances, explicit
  destination slots and versioned bilateral consent. Only offered counterpart
  items are previewed; unrelated counterpart inventory stays private.
- Death inspection/claim binds an existing capability to its beneficiary.
  Only these Loadout commands remain allowed after completion. Inspection details
  disappear when range, corpse, inventory revisions or authorization become invalid;
  claim rechecks these rules through the existing domain transaction.

Observation version one sends full immutable replacements when its state changes,
or explicit removal on loss of control; it does not invent a delta protocol.
Inventory, role-tree points/ranks/progress, exchange terms/consent and inspected
death items commit with the world frame. Transfers preserve the original item and
ability identities; role-owned trees and points never travel with the item.
See the optional scenarios in
[NetworkSession](../../samples/NetworkSession/NetworkSessionSample.Loadout.cs).
The interactive base Console intentionally remains a Combat-only client.

## Current limits

This is not production identity, durable exactly-once execution, cross-process
recovery or distributed coordination. The publication queue is bounded and fails
explicitly under overload. Basic Combat commands and the world observations above
are wired; private compatibility remains migration work. NetworkSession tests cover actual loopback
combat/status/flight rejoin, accepted-but-not-executed snapshots, buffer overflow,
exact receipt boundaries, mixed queue pressure, authorization,
module full/delta/removal, completion, result records, optional Loadout and explicit demo opt-in; they are not final migration
or production-readiness evidence.
