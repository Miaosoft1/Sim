# @miaosoft/sim-client

A JavaScript SDK for the Miaosoft.Sim session protocol. The core is an ES module
and has no Node-only API; it can be used from a browser or Node.js application.
Node.js and a test runner are needed only for installing dependencies and running
automated tests.

This package is a protocol prototype client. It does not include private gameplay,
authentication, matchmaking, rendering or private Loadout integration.

## Install

```powershell
cd clients/javascript
npm install
npm test
```

## Browser or Node usage

```javascript
import { GameClient, createSignalRTransport } from "@miaosoft/sim-client";

const client = new GameClient({
  transport: createSignalRTransport("http://127.0.0.1:5050/session"),
});

const welcome = await client.enterDemo();
const self = client.world.state.world.units.find(
  unit => unit.id === client.world.state.binding.unitId,
);

const target = client.world.state.world.units.find(unit => unit.id !== self.id);
const bolt = self.abilities.find(ability => ability.definitionId === "bolt");

const receipt = await client.send({
  unitId: self.id,
  mapInstanceId: self.mapInstanceId,
  controlVersion: self.controlVersion,
  abilityInstanceId: bolt.runtimeInstanceId,
  targetUnitId: target.id,
});

if (receipt.status === "Enqueued") {
  const result = await client.waitForOutcome(receipt.sequence);
  console.log(result.status, result.reason);
}
```

Pass plain command objects or use the exported factory functions:

```javascript
import { moveCommand, stopMoveCommand, castAbilityCommand } from "@miaosoft/sim-client";

await client.send(moveCommand({
  unitId: self.id,
  mapInstanceId: self.mapInstanceId,
  controlVersion: self.controlVersion,
  destination: { x: 14, y: 10 },
}));
```

## Protocol rules

- The SDK validates protocol version 5.
- Queue receipt status is not execution success.
- A pending operation retains its exact sequence and event envelope for retries.
- `Unknown` never authorizes replay under a fresh sequence.
- Full snapshots restore state; transient `projectile.fact` events are live-only.
- Module update payloads and private codecs must be supplied explicitly; base
  combat state is supported without private project integration.
