# Miaosoft.Sim

[简体中文](README.zh-CN.md)

A modular, event-driven simulation framework for composing combat rules from
typed content. Its focus is explicit event ordering, isolated runtime state,
transactional ability changes, and testable behavior.

**Work in progress:** this extraction is not yet a release. Loadout primitives,
the real loopback network/Console prototype and private source references are
tested. Final private-mode admission coverage and the public source/license
audit are still in progress. No graphical client is included.
The intended license for the public framework is [MIT](LICENSE); dependency and
content redistribution rights still require [review](docs/en/dependencies.md)
before release.

## Architecture

```text
Samples / application composition
    |-- Content.Loading -> Content.Core
    |-- Combat.Skill.Content -> Combat.Skill -> Combat.Map -> Core
    |-- Combat.Map.Content   -> Combat.Map
    |-- optional Loadout.Content -> Loadout -> Combat.Skill
    |-- Server.Hosting -> Runtime -> Protocol -> Core / Serialization.Yaml
    |-- Server -> Runtime.Abstractions / Combat.Skill
    `-- Console -> clients/csharp -> Control -> Protocol
```

- **Core:** simulation-time queue, event receivers, cancellation and atomic changes.
- **Combat.Map:** map instances, geometry, movement and spatial targeting.
- **Combat.Skill:** abilities, delivery/effects, activation gates, charges and triggers.
- **Content:** DI-selected modules compile catalog-local immutable definitions;
  installation creates fresh runtime instances. Combat does not depend on YAML loading.
- **Loadout:** optional inventory, retained role talent trees and atomic real-instance exchange.
- **Sessions:** single-owner execution, credential-bound connections, prepared
  snapshots and retry receipts; the Client/Console closure contains no server implementation.

See [architecture and execution model](docs/en/architecture.md) for the current
event flow, content boundaries and known migration gaps.

## Run and verify

Use the .NET SDK selected by [global.json](global.json). From this directory,
run these commands **sequentially**:

```powershell
dotnet run --project .\samples\EventScheduling\EventScheduling.csproj -- --output Detailed
dotnet run --project .\samples\LocalCombat\LocalCombat.csproj -- --output Detailed
dotnet run --project .\samples\NetworkSession\NetworkSession.csproj -- --output Detailed
dotnet run --project .\tests\Skill\Miaosoft.Sim.Combat.Skill.Tests.csproj -- --output Detailed
dotnet run --project .\tests\Content\Miaosoft.Sim.Content.Tests.csproj -- --output Detailed
```

These are executable MSTest samples, not additional client applications. EventScheduling
demonstrates ordering, delay and cancellation. LocalCombat demonstrates YAML
loading, projectile -> damage -> healing, finite charges, status stacking and
expiry, independent scenes, and executable expression examples (including rejected content). Read the
per-test output as well as the summary.

NetworkSession starts and stops its own real loopback listener. It verifies
combat, movement, status expiry, in-flight rejoin, the exact 1024-receipt boundary
and module privacy, hex/entrance geometry and live-only projectile facts,
plus optional Loadout exchange and authorized death pickup
after completion. The base applications remain independent of Loadout.
For interactive use, run the server and **two** consoles in separate terminals:

```powershell
dotnet run --project .\apps\Server\Miaosoft.Sim.Server.App.csproj -- --Demo:Enabled=true
dotnet run --project .\apps\Console\Miaosoft.Sim.Console.App.csproj
```

In the Alpha console try `cast bolt Beta`, then `status`, `disconnect` and
`rejoin`. `bolt` has two charges; `moving-bolt` adds windup and a visible trajectory.
See [network contracts and current
limits](docs/en/networking.md); queue acceptance is not execution success.

## JavaScript client

A shared browser-and-Node ES-module SDK is available at
[clients/javascript](clients/javascript). It implements the isolated session
protocol prototype, including demo admission/rejoin, YAML event envelopes,
snapshot updates, queue receipts and retry preservation. The SDK has no server
implementation dependency and no Node-only runtime API. Node.js is used only for
installing packages and running its optional Vitest suite:

```powershell
cd .\clients\javascript
npm install
npm test
```

This is not a graphical client and does not enable private Loadout or private
authentication behavior.

LocalCombat commands set `AbilityInstanceId` to the selected runtime instance.
With multiple installed command abilities of the same definition, omitting it
fails with `AmbiguousAbility`; an unknown, foreign, wrong-definition or
unauthorized instance fails with `AbilityNotAvailable`. An exhausted instance
never falls back to a different copy. The network prototype also addresses commands by actual runtime instance.

## Reference

See the [trigger conditions and expression reference](docs/en/trigger-conditions.md).
For a separate private application, see [source references and extension
boundaries](docs/en/private-consumption.md).
