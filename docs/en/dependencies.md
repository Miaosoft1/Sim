# Dependency review (in progress)

The framework's intended [MIT license](../../LICENSE) does not relicense its
dependencies. After independently restoring the adjacent public source tree, the local NuGet
metadata for all 27 resolved direct and transitive packages declares MIT.
This includes the added SignalR client packages; shared .NET/ASP.NET runtimes
are not NuGet entries in that count. Runtime content uses YamlDotNet and dependency injection; test and sample
projects explicitly reference the MSTest adapter, framework and TRX reporter.

The former MSTest meta-package pulled in a CodeCoverage extension with a
separate Microsoft license. It is no longer in the current resolved dependency
graph; old build directories may still contain stale files and must not be
included in a release. A fresh LocalCombat publish contained no CodeCoverage
files and passed its 25 scenarios; this is not an audit of future artifacts.

The test adapter still resolves a telemetry extension and Application Insights.
The test and sample projects exclude that extension's build assets to avoid
automatically registering its telemetry hook. All seven independently generated
Debug runners were checked for telemetry/coverage registration; none was found.
The framework-dependent Server and Console publish outputs contain no testing,
Application Insights, telemetry or coverage extension assemblies.
The earlier published LocalCombat runner still contains the telemetry extension
assembly even though its hook was excluded. This does not establish
the privacy behavior of all tooling or third-party components. Do not assume
the .NET CLI telemetry setting disables their telemetry.

This is a local metadata inventory, not legal or privacy clearance. Recheck the
final dependency graph, third-party notices, data practices and actual release
artifacts before any external publication. Product-name availability is also
unverified; local implementation and the MIT source license are not trademark clearance.
