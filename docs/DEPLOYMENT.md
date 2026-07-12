# Deployment Guide

AIOptimizeSql runs in two modes:

| Mode | What runs | Storage | Best for |
|------|-----------|---------|----------|
| **Standalone** | One self-contained executable (WebUI + optimize engine in one process) | SQLite (zero configuration) | Trying it out, single-user desktop/server use |
| **Azure App Service** | WebUI (engine in-process by default), optionally a separate Worker | Azure SQL / SQL Server (recommended) or SQLite | Team use, always-on deployments |

The metadata database only stores the app's own state (connections, experiments, results).
The SQL Server databases you *optimize* are always separate and configured inside the app.

---

## 1. Standalone single executable

### Download

Every push to the `deploy` branch builds versioned binaries for all three platforms
via GitHub Actions (`.github/workflows/release.yml`) and publishes them on the
repository's **Releases** page:

- `AIOptimizeSql-<version>-win-x64.exe` — download and run.
- `AIOptimizeSql-<version>-linux-x64.tar.gz` / `-osx-arm64.tar.gz` — extract and run
  `./Tedd.AIOptimizeSql.WebUI` (the tarball preserves the executable bit).

The version is `<VERSION file>.<build number>` (e.g. `1.0.42`); bump the major/minor
by editing the `VERSION` file at the repo root. Each OS is built on its native
runner, tests run first, and the release is tagged `v<version>`.

### Build

```powershell
./publish-standalone.ps1                      # win-x64 (default)
./publish-standalone.ps1 -Runtime linux-x64
./publish-standalone.ps1 -Runtime osx-arm64
```

or directly:

```powershell
dotnet publish src/Tedd.AIOptimizeSql.WebUI/Tedd.AIOptimizeSql.WebUI.csproj -p:PublishProfile=standalone-win-x64
```

The output lands in `publish/standalone-<runtime>/`:

- `Tedd.AIOptimizeSql.WebUI.exe` — the whole application in one file (~90 MB, no .NET runtime needed; web UI, optimize engine, and all static assets embedded).
- `appsettings.json` — optional; the exe runs without it.

### Run

Double-click the executable or start it from a terminal. On first start it:

1. Creates a SQLite database at `%LocalAppData%\Tedd.AIOptimizeSql\aioptimize.db`
   (Linux/macOS: `~/.local/share/Tedd.AIOptimizeSql/aioptimize.db`) and applies migrations automatically.
2. Listens on `http://localhost:5000` (override with `Urls`, see below).
3. Opens your default browser at that address.

### Configure

Drop an `appsettings.json` next to the executable (all keys optional):

```json
{
  "Urls": "http://localhost:8080",
  "LaunchBrowser": true,
  "ConnectionStrings": {
    "AIOptimizeDb": "Data Source=my-data.db"
  }
}
```

Environment variables and command-line arguments override the JSON file
(`--Urls http://localhost:8080`, `LaunchBrowser=false`, …).

| Setting | Default | Purpose |
|---------|---------|---------|
| `Urls` | `http://localhost:5000` | Listen address(es) |
| `LaunchBrowser` | `true` | Open the browser on startup (auto-disabled in containers and on Azure) |
| `Database:Provider` | inferred | `Sqlite` or `SqlServer`; inferred from the connection string when omitted |
| `ConnectionStrings:AIOptimizeDb` | local SQLite file | Metadata database. Relative SQLite paths resolve next to the exe |
| `OptimizeEngine:RunInProcess` | `true` | Host the optimize engine inside the web process |
| `OptimizeEngine:WebSearch:ApiKey` | empty | Brave search API key (optional, enables the AI's web-search tool) |

Pointing the standalone exe at SQL Server instead of SQLite works too:

```json
{
  "ConnectionStrings": {
    "AIOptimizeDb": "Server=myserver;Database=AIOptimizeSql;Trusted_Connection=True;TrustServerCertificate=True"
  }
}
```

With SQL Server the app does **not** migrate automatically; the built-in database
status page offers to create the database and apply migrations.

---

## 2. Azure App Service

### Recommended: single App Service (combined mode)

The WebUI hosts the optimize engine in-process, so one App Service runs everything.
Because the engine polls for queued work continuously, use a **Basic tier or higher**
and enable **Always On**.

```bash
RG=aioptimize-rg
PLAN=aioptimize-plan
APP=aioptimize-web          # must be globally unique
SQLSRV=aioptimize-sql       # must be globally unique

az group create -n $RG -l westeurope
az appservice plan create -g $RG -n $PLAN --sku B1 --is-linux

# Azure SQL for the metadata database
az sql server create -g $RG -n $SQLSRV -u aioptadmin -p '<strong-password>'
az sql db create -g $RG -s $SQLSRV -n AIOptimizeSql --service-objective S0
az sql server firewall-rule create -g $RG -s $SQLSRV -n AllowAzureServices \
  --start-ip-address 0.0.0.0 --end-ip-address 0.0.0.0

# Web app (.NET 10)
az webapp create -g $RG -p $PLAN -n $APP --runtime "DOTNETCORE:10.0"
az webapp config set -g $RG -n $APP --always-on true

az webapp config appsettings set -g $RG -n $APP --settings \
  Database__Provider=SqlServer \
  "ConnectionStrings__AIOptimizeDb=Server=tcp:$SQLSRV.database.windows.net,1433;Database=AIOptimizeSql;User ID=aioptadmin;Password=<strong-password>;Encrypt=True;" \
  LaunchBrowser=false
```

Deploy:

```bash
cd src
dotnet publish Tedd.AIOptimizeSql.WebUI/Tedd.AIOptimizeSql.WebUI.csproj -c Release -o ./deploy
cd deploy && zip -r ../deploy.zip . && cd ..
az webapp deploy -g $RG -n $APP --src-path deploy.zip --type zip
```

First visit: the database status page detects the empty database and applies the
migrations with one click (the configured login needs `CREATE DATABASE`/`db_owner`,
or pre-create the DB and grant `db_owner`).

Notes:

- App Service settings use `__` (double underscore) as the section separator.
- `LaunchBrowser` is ignored on App Service anyway (detected via `WEBSITE_SITE_NAME`), setting it is just belt-and-braces.
- SQLite *can* be used on App Service by setting
  `ConnectionStrings__AIOptimizeDb=Data Source=D:\home\data\aioptimize.db` (Windows)
  or `/home/data/aioptimize.db` (Linux) — the `%HOME%` share is persistent — but it
  is limited to a single instance; prefer Azure SQL for anything shared.
- The App Service must be able to reach the SQL Server databases you want to
  optimize (VNet integration or hybrid connections for on-prem servers).

### Optional: split WebUI and Worker

For heavier workloads you can run the optimize engine as its own App Service
(or container) so the web UI stays responsive:

1. Deploy the WebUI as above, and add the app setting `OptimizeEngine__RunInProcess=false`.
2. Publish `Tedd.AIOptimizeSql.Worker` to a second (Linux) App Service, container, or
   VM with the **same** `ConnectionStrings__AIOptimizeDb` (and `Database__Provider`).
   The worker is a headless host — for App Service run it as a WebJob or a custom
   container with Always On; there is no HTTP endpoint to serve.

Both processes coordinate purely through the shared metadata database, so no other
wiring is needed. This split requires SQL Server — two processes cannot share a
local SQLite file across machines.

---

## 3. Local development

- `Tedd.AIOptimizeSql.AppHost` (Aspire) starts WebUI and Worker as separate
  processes; `appsettings.Development.json` disables the in-process engine so work
  is not processed twice.
- The development database is SQL Server (see `appsettings.Development.json`).
- EF Core migrations exist per provider:

```bash
# SQL Server (lives in Tedd.AIOptimizeSql.Database)
dotnet ef migrations add SomeChange --project Tedd.AIOptimizeSql.Database --startup-project Tedd.AIOptimizeSql.WebUI

# SQLite (lives in Tedd.AIOptimizeSql.Database.Sqlite)
dotnet ef migrations add SomeChange --project Tedd.AIOptimizeSql.Database.Sqlite --startup-project Tedd.AIOptimizeSql.Database.Sqlite
```

Add every model change to **both** migration sets. SQLite migrations may generate
phantom `AlterColumn` operations that only touch a `Sqlite:Autoincrement`
annotation — these are an artifact of the strongly-typed ID converters and can be
deleted from the generated migration.
