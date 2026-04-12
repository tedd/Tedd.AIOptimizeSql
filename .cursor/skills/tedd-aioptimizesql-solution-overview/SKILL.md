---
name: tedd-aioptimizesql-solution-overview
description: Summarizes Tedd.AIOptimizeSql technology stack (.NET 10, server-side Blazor, Aspire) and solution layout. Use when orienting to the repository, explaining architecture, onboarding, or answering what this app is built with.
---

# Tedd.AIOptimizeSql — stack and solution overview

## Technology stack

- **Runtime / language**: C# on **.NET 10** (`net10.0`) across projects.
- **Web UI**: **ASP.NET Core Blazor** with **interactive server** rendering (`AddInteractiveServerComponents` / `AddInteractiveServerRenderMode` in `Tedd.AIOptimizeSql.WebUI`). UI uses **MudBlazor** and **BlazorMonaco** for SQL editing.
- **Data**: **Entity Framework Core 10** with **SQL Server** (`Microsoft.EntityFrameworkCore.SqlServer`, `Microsoft.Data.SqlClient`). EF migrations and design-time tooling live in the Database project; connection string key used in WebUI is `AIOptimizeDb`.
- **Orchestration**: **.NET Aspire** — `Tedd.AIOptimizeSql.AppHost` references WebUI and Worker; shared telemetry/defaults come from `Tedd.AIOptimizeSql.ServiceDefaults`.
- **Background work**: `Tedd.AIOptimizeSql.Worker` is a **.NET Worker** host that references OptimizeEngine and Database.
- **SQL optimization / AI**: `Tedd.AIOptimizeSql.OptimizeEngine` holds execution and AI integration (e.g. **Microsoft.Extensions.AI**, OpenAI/Anthropic agents, **OllamaSharp**, Azure OpenAI) against SQL Server.

## Solution layout (`src/Tedd.AIOptimizeSql.slnx`)

| Area (solution folder) | Project | Role |
|------------------------|---------|------|
| WebUI | `Tedd.AIOptimizeSql.WebUI` | Blazor Server app, main user interface |
| Database | `Tedd.AIOptimizeSql.Database.Models` | Entity models |
| Database | `Tedd.AIOptimizeSql.Database` | `DbContext`, EF Core, data access |
| Optimizer | `Tedd.AIOptimizeSql.OptimizeEngine` | SQL execution and AI-driven optimization logic |
| Aspire | `Tedd.AIOptimizeSql.AppHost` | Aspire host (orchestrates apps) |
| Aspire | `Tedd.AIOptimizeSql.ServiceDefaults` | Shared Aspire/service defaults |
| Aspire | `Tedd.AIOptimizeSql.Worker` | Background worker consuming OptimizeEngine |
| Tests | `Tedd.AIOptimizeSql.Tests` | Unit/integration tests |

Source root for the solution is **`src/`** (not the repo root).

## Maintaining this skill

**Update this `SKILL.md` when the solution meaningfully changes**, for example: target framework or major package upgrades, new/removed projects, switch away from Blazor Server or SQL Server, Aspire layout changes, or renamed connection/configuration keys. Stale overview text misleads future sessions—keep it aligned with `.slnx`, `.csproj`, and `Program.cs` patterns.
