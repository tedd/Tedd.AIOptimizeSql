---
name: ef-migrations-user-initiated
description: Requires explicit user instruction before adding Entity Framework Core migrations (dotnet ef migrations add). Use when editing entities, DbContext, database models, or schema-related code in Tedd.AIOptimizeSql, or whenever the agent might otherwise add or remove EF migrations without being asked.
---

# EF migrations: user-initiated only

The same policy is enforced as a **scoped project rule** when files under `src/Tedd.AIOptimizeSql.Database/` or `src/Tedd.AIOptimizeSql.Database.Models/` are in play: `.cursor/rules/ef-migrations-user-initiated.mdc`.

## Rule

**Do not** run `dotnet ef migrations add`, `migrations remove`, or scaffold new migration files **unless the user clearly asks in the same conversation** (e.g. "add a migration", "create migration for X", "run dotnet ef migrations add").

- Finishing a model or `DbContext` change **does not** imply adding a migration.
- Do **not** suggest running `migrations add` as an automatic "next step" unless the user wants that workflow; a short note that they can ask when ready is enough.

## When migrations are allowed

Follow [add-ef-migration](../add-ef-migration/SKILL.md) **only after** the user explicitly requests a new or updated migration.

## Related

- Applying migrations to a database remains user-initiated per [update-ef-database](../update-ef-database/SKILL.md).
