using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

using Tedd.AIOptimizeSql.Database;

namespace Tedd.AIOptimizeSql.Database.Sqlite;

/// <summary>
/// Design-time factory for the SQLite migrations. Example:
/// <c>dotnet ef migrations add SomeChange --project Tedd.AIOptimizeSql.Database.Sqlite --startup-project Tedd.AIOptimizeSql.Database.Sqlite</c>
/// (run from the <c>src</c> directory).
/// <para>
/// Note: newly generated migrations may contain phantom <c>AlterColumn</c> operations that
/// only remove a <c>Sqlite:Autoincrement</c> annotation. They are an artifact of the
/// strongly-typed ID converters (the snapshot stores plain <c>int</c>, the live model the
/// converted type) and can safely be deleted from the generated migration.
/// </para>
/// </summary>
public sealed class SqliteDesignTimeDbContextFactory : IDesignTimeDbContextFactory<AIOptimizeDbContext>
{
    public AIOptimizeDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AIOptimizeDbContext>();
        optionsBuilder.UseSqlite(
            "Data Source=design-time.db",
            sqlite => sqlite.MigrationsAssembly(AIOptimizeDatabaseServiceExtensions.SqliteMigrationsAssembly));
        return new AIOptimizeDbContext(optionsBuilder.Options);
    }
}
