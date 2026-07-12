# Tedd.AIOptimizeSql

AI powered auto optimization for SQL Server.

## Quick start (standalone)

Grab the latest binary for your OS from the
[Releases page](https://github.com/tedd/Tedd.AIOptimizeSql/releases) (built
automatically from the `deploy` branch), or build it yourself — a single
self-contained executable, no .NET runtime, no database server needed:

```powershell
./publish-standalone.ps1        # or -Runtime linux-x64 / osx-arm64
```

Run `publish/standalone-win-x64/Tedd.AIOptimizeSql.WebUI.exe`. It stores its data in
a local SQLite database, starts on `http://localhost:5000` and opens your browser
automatically. Configuration is optional — drop an `appsettings.json` next to the
executable to change the port, database, or engine settings.

## Deployment

Two supported modes:

- **Standalone** — the single executable above (SQLite, web UI + optimize engine in one process).
- **Azure App Service** — combined app or split web/worker, with Azure SQL.

See [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) for the full guide, including Azure CLI
steps, configuration reference, and how migrations are handled per database provider.

## Development

Open `src/Tedd.AIOptimizeSql.slnx` and run the `Tedd.AIOptimizeSql.AppHost` (Aspire)
project, or run `Tedd.AIOptimizeSql.WebUI` directly. Development settings use SQL Server
(see `appsettings.Development.json`).
