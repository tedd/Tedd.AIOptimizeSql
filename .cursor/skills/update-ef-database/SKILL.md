---
name: update-ef-database
description: Applies pending Entity Framework Core migrations to SQL Server for Tedd.AIOptimizeSql using dotnet ef database update or the WebUI database setup page. Use when the user asks to update the database, apply migrations, sync the schema, or run dotnet ef database update.
---

# Update EF Core database (Tedd.AIOptimizeSql)

## When to use

- Migrations exist under `Tedd.AIOptimizeSql.Database/Migrations/` and the target SQL Server database must be brought up to date.
- User says "update database", "apply migrations", "run migrations", `database update`, or "sync database".

## Convention

| Piece | Value |
|-------|--------|
| Migrations assembly | `src/Tedd.AIOptimizeSql.Database` |
| DbContext | `AIOptimizeDbContext` |
| Startup project (for CLI) | `src/Tedd.AIOptimizeSql.WebUI` |
| Connection string | `ConnectionStrings:AIOptimizeDb` (WebUI `appsettings*.json` or `ConnectionStrings__AIOptimizeDb` env) |
| Working directory | `src` |

For **creating** new migrations, see [add-ef-migration](../add-ef-migration/SKILL.md).

## Option A — WebUI (typical for local/dev)

1. Run the WebUI and open `/database/migration`.
2. Confirm **Server**, **Database**, and **User** match the intended target.
3. Click **Apply migrations**, then **Continue to app** when the schema is current.

Uses the same connection string as the running app (`DatabaseReadinessService` / `MigrateAsync`).

## Option B — dotnet ef (CI, scripts, or when the app is not running)

Apply all pending migrations:

```bash
cd src
dotnet ef database update --project Tedd.AIOptimizeSql.Database --startup-project Tedd.AIOptimizeSql.WebUI --context AIOptimizeDbContext
```

Apply up to a specific migration (inclusive):

```bash
cd src
dotnet ef database update <MigrationName> --project Tedd.AIOptimizeSql.Database --startup-project Tedd.AIOptimizeSql.WebUI --context AIOptimizeDbContext
```

`<MigrationName>` is the migration class name (e.g. `InitialCreate`), not the timestamp prefix.

## Troubleshooting

- **Wrong database**: Fix `AIOptimizeDb` in WebUI config or environment before running CLI or the setup page.
- **"Startup project doesn't reference Microsoft.EntityFrameworkCore.Design"**: Same as migration tooling — WebUI must reference that package (see [add-ef-migration](../add-ef-migration/SKILL.md)).
- **Production / shared servers**: Confirm the connection string and backup strategy with the user before applying migrations.

## Do not

- Point `database update` at production without explicit user confirmation and the correct connection string.
- Use `EnsureCreated()` instead of migrations for this app.
