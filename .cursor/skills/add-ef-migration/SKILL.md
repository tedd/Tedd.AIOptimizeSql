---
name: add-ef-migration
description: Adds Entity Framework Core SQL Server migrations for Tedd.AIOptimizeSql by running dotnet ef against the Database project with WebUI as startup. Use when the user asks to add a migration, update the schema, or run dotnet ef migrations add/remove after model or DbContext changes. Never run database update automatically; see skill body.
---

# Add EF Core migration (Tedd.AIOptimizeSql)

## When to use

- `Database.Models` entities or `AIOptimizeDbContext` changed and the database schema must be versioned.
- User says "add migration", "new migration", "dotnet ef", or "update the database schema".

## Convention

| Piece | Value |
|-------|--------|
| Migrations assembly | `src/Tedd.AIOptimizeSql.Database` |
| DbContext | `AIOptimizeDbContext` |
| Startup project (for tools) | `src/Tedd.AIOptimizeSql.WebUI` (references `Microsoft.EntityFrameworkCore.Design`) |
| Design-time factory | `AIOptimizeDbContextFactory` — resolves `ConnectionStrings:AIOptimizeDb` from WebUI `appsettings*.json` or env |
| Working directory | `src` (so project names match the `.csproj` paths below) |

## Add a migration

From repository root:

```bash
cd src
dotnet ef migrations add <MigrationName> --project Tedd.AIOptimizeSql.Database --startup-project Tedd.AIOptimizeSql.WebUI --context AIOptimizeDbContext
```

Replace `<MigrationName>` with a descriptive PascalCase name (e.g. `AddBenchmarkColumns`).

## After adding a migration

- **Do not apply the database from this workflow**: Never run `dotnet ef database update` (or any other automatic apply) unless the user explicitly asks to apply migrations in the same turn. Adding a migration only versions the schema in source control; applying it is a separate, deliberate step for the user (see [update-ef-database](../update-ef-database/SKILL.md)).
- **Tell the user** they can apply via WebUI **Database setup** (`/database/migration`) → **Apply migrations**, or run `dotnet ef database update` themselves with the same `--project` / `--startup-project` / `--context` flags when they are ready.
- **Review** the generated `Migrations/*.cs` for unintended table/column renames; adjust the model or use explicit `MigrationBuilder` steps if needed.

## Troubleshooting

- **"Startup project doesn't reference Microsoft.EntityFrameworkCore.Design"**: Ensure `Tedd.AIOptimizeSql.WebUI` still references that package (PrivateAssets all is fine).
- **Wrong connection string at design time**: Set `ConnectionStrings__AIOptimizeDb` in the environment, or ensure `Tedd.AIOptimizeSql.WebUI/appsettings.json` (or Development) contains `AIOptimizeDb` — see `AIOptimizeDbContextFactory.GetAppsettingsSearchPaths()`.

## Do not

- Run **`dotnet ef database update`** (or otherwise apply migrations to a database) automatically after `migrations add` or as an implied follow-up—only when the user clearly requests applying migrations.
- Add migrations to the WebUI project; keep them under `Tedd.AIOptimizeSql.Database/Migrations/`.
- Rely on `EnsureCreated()` for this app; migrations are the source of truth.
