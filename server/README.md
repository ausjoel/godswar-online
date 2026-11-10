# GodsWar server

C# / .NET 10 game and login server backed by PostgreSQL. Normally you run it through the
top-level `setup.sh` (native systemd install, or Docker). To build it directly:

```bash
dotnet build GodswarServer.sln -c Release
dotnet run --project src/Godswar.Server -c Release     # reads appsettings.json and GODSWAR_* environment variables
dotnet run --project tests/Godswar.Server.ProtocolChecks -c Release   # protocol checks (several minutes)
```
Protocol checks: about 36 checks fail on the development tree too (known, some timing dependent; the
total exit code is therefore 1). 14 checks that read development-repository files not shipped in this
package are reported as SKIP (list in `tests/Godswar.Server.ProtocolChecks/PublicPackageSkips.cs`).

Settings: `appsettings.json` and `GODSWAR_*` environment variables (see `.env.example`).
Database migrations in `database/postgres` and the server's embedded migrations apply on start.

Credits and licence: `LICENSE` (GPL-3.0) and `NOTICE.md`.
