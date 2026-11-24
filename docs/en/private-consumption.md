# Private applications and source references

[Overview](../../README.md) · [中文](../zh-CN/private-consumption.md)

## Two independent source trees

Keep the public framework next to the private application repository:

```text
repos/
  Miaosoft.Sim/              public framework and neutral examples
  Private.Game/          private modes, applications, content and assets
```

The public tree never references the private tree. Private gameplay, Godot,
generated media, credentials and AI tooling do not belong in the public directory.
They retain their existing private-repository/ignore rules. Do not copy private
history into the public tree or use Git `assume-unchanged` to hide private files
inside public source.

The public directory is independently validated, and the existing private
application now uses relative references to it. Historical private shared-source
projects remain in the private checkout but are excluded from the active solution;
new implementation work must target the adjacent public assemblies. No public Git
repository, commit, remote or package is created by these instructions.

## Relative ProjectReference

For a private project located at `apps/Console/Console.csproj`, the core SDK
reference is:

```xml
<ProjectReference Include="..\..\..\Miaosoft.Sim\clients\csharp\Miaosoft.Sim.Client.csproj" />
```

If that application selects Loadout, also reference the optional client:

```xml
<ProjectReference Include="..\Miaocrosoft.Script\src\BattleGame\Client\Loadout\Miaosoft.Sim.Loadout.Client.csproj" />
```

A private gameplay project at `src/Game/Modes/Example/Example.csproj`
can reference Combat through:

```xml
<ProjectReference Include="..\..\..\..\..\Miaosoft.Sim\src\Combat\Skill\Miaosoft.Sim.Combat.Skill.csproj" />
```

Compute each path from its project file, not from the solution or current shell.
Do not reference session folders, copied DLLs or another checkout's `bin` output.
The IDE may include required public projects in the private solution for debugging;
this does not merge the repositories or change dependency direction.

## Ownership and extension points

- Private simulation adapters implement `ISessionSimulation` from
  `Runtime.Abstractions`. The application alone creates the `SessionRuntime` owner.
- Production private admission stays private: it validates allocation owner,
  generation and tickets, commits participation, then binds each admitted seat to
  the authoritative public session credential. Demo admission must stay disabled.
  Legacy private clients consume a protocol bridge; that bridge may not infer
  execution success from queue acceptance.
- Private wire commands register through the directional event codec; their
  server adapters must bind current participants and validate authority/revisions.
- Select observation modules explicitly on both ends. Their detached immutable
  states commit with the world; private data is removed when control is lost.
- Register selected content modules and typed formula fields through DI.
  Keep gameplay catalogs and assets private; do not extend global YAML state.
- Loadout is optional at the assembly level. Tree grants, inventory and trading
  remain independently composed capabilities, not dependencies of base Combat.

Migration retains old shared source as an inactive historical copy, rather than
deleting it. Do not compile old and new Context/Combat implementations together
or patch an incompatibility by recreating transferred abilities. Private custom
handlers/content and replay orchestration need actual adaptation and regression
tests; a namespace substitution alone is not migration.

## Versioning and verification

Project references follow the source currently checked out; they do **not** pin
a Git commit. Once the public tree has an authorized, verified commit, record its
real SHA in the private repository and have private CI explicitly check it out
beside the private tree. Until then the public dependency is **unlocked**; do not
invent a hash or claim a reproducible release.

Validate public changes using the root commands first, then build/test affected
private applications and modes. Preserve original item identity, cooldowns,
charges, role-owned talent progress, admission/participation authority and
recovery semantics across the boundary. Publish neither private content nor
unreviewed generated artifacts as part of the public deliverable.
