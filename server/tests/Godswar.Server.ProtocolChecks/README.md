# Protocol checks

Build the solution before invoking the check assembly. Positional arguments select
check labels by case-insensitive substring. Every supplied filter must match at
least one label; an unmatched filter exits with code 2 before any checks run.

```powershell
dotnet tests/Godswar.Server.ProtocolChecks/bin/Release/net10.0/Godswar.Server.ProtocolChecks.dll "Data-boundary architecture ratchet"
```

Checks report `PASS`, `FAIL`, or `SKIP`. Missing PostgreSQL fixtures are explicit
skips and never count as passes. A normal local run permits skips; required gates
should opt into `--require-no-skips`.

```powershell
dotnet tests/Godswar.Server.ProtocolChecks/bin/Release/net10.0/Godswar.Server.ProtocolChecks.dll --require-no-skips --results-json artifacts/check-results.json "Protocol-check outcomes and required selection"
```

Exit codes are 0 for a successful selection, 1 for failures or disallowed skips,
and 2 for invalid arguments or unmatched filters. JSON reports use schema version
1 and contain `exitCode`, `requireNoSkips`, `unmatchedFilters`, and one result per
selected check. Each result includes its name, outcome (`passed`, `failed`, or
`skipped`), duration, and optional reason. A selection rejected for an unmatched
filter writes an empty result array.

The assembly can expose its actual compiled migration catalog without opening a
database or running checks:

```powershell
dotnet tests/Godswar.Server.ProtocolChecks/bin/Release/net10.0/Godswar.Server.ProtocolChecks.dll --schema-metadata
```

This emits JSON containing `schemaVersion`, `migrationCount`, and `migrationHead`.
The B03 PostgreSQL gate uses this metadata for current-release expectations;
historical migration fixtures keep their explicit prefixes.

<!-- ROUND3: keep the reviewed history gate independently runnable while retaining the broader foundation. -->
The `PostgreSQL reviewed migration catalog` selection verifies the explicit,
ordered list of reviewed migration IDs and its existing SQL contract/checksum
guards. New migrations require an intentional update to that list. The broader
`PostgreSQL migration safety foundation` also runs this guard and historical
content fixtures; a failure in those fixtures remains a failure. Feature checks
such as Harbor Attack verify their migration dependencies in order without
requiring those dependencies to be the latest global migration.

New checks should throw `CheckSkippedException` only for an unavailable or
inapplicable prerequisite. Assertion failures and unexpected exceptions remain
failures. The runner regression check verifies these distinctions and selection
handling with synthetic checks.
