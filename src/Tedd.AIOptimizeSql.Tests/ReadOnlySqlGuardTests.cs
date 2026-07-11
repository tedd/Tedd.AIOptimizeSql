using Tedd.AIOptimizeSql.OptimizeEngine.Utils;

namespace Tedd.AIOptimizeSql.Tests;

public class ReadOnlySqlGuardTests
{
    [Theory]
    [InlineData("SELECT * FROM sys.tables")]
    [InlineData("select name from sys.indexes where object_id = 5")]
    [InlineData("WITH cte AS (SELECT 1 AS x) SELECT * FROM cte")]
    [InlineData(";WITH cte AS (SELECT 1 AS x) SELECT x FROM cte")]
    [InlineData("DECLARE @x int; SELECT @x = 1; SELECT @x")]
    [InlineData("SELECT * FROM sys.dm_db_missing_index_details")]
    [InlineData("SELECT * FROM sys.dm_db_index_physical_stats(DB_ID(), NULL, NULL, NULL, 'LIMITED')")]
    [InlineData("PRINT 'hello'")]
    [InlineData("IF 1 = 1 SELECT 1 ELSE SELECT 2")]
    [InlineData("EXEC sp_helpindex 'dbo.MyTable'")]
    [InlineData("EXEC sp_spaceused")]
    [InlineData("EXECUTE sys.sp_help 'dbo.MyTable'")]
    [InlineData("DBCC SHOW_STATISTICS ('dbo.T', 'IX_T')")]
    [InlineData("SET NOCOUNT ON; SELECT 1")]
    [InlineData("USE master; SELECT DB_NAME()")]
    [InlineData("SELECT col1 INTO #temp FROM sys.objects")]
    public void Check_AllowsReadOnlyStatements(string sql)
    {
        var result = ReadOnlySqlGuard.Check(sql);
        Assert.True(result.IsAllowed, $"Expected allowed but was blocked: {result.Reason}");
    }

    [Theory]
    [InlineData("INSERT INTO T VALUES (1)")]
    [InlineData("UPDATE T SET x = 1")]
    [InlineData("DELETE FROM T WHERE id = 1")]
    [InlineData("MERGE T USING S ON T.id = S.id WHEN MATCHED THEN UPDATE SET x = 1;")]
    [InlineData("TRUNCATE TABLE T")]
    [InlineData("CREATE INDEX IX ON T (col)")]
    [InlineData("CREATE NONCLUSTERED INDEX IX_X ON dbo.T (a) INCLUDE (b)")]
    [InlineData("ALTER TABLE T ADD col int")]
    [InlineData("ALTER INDEX ALL ON T REBUILD")]
    [InlineData("DROP TABLE T")]
    [InlineData("DROP INDEX IX ON T")]
    [InlineData("CREATE OR ALTER VIEW V AS SELECT 1 AS x")]
    [InlineData("CREATE OR ALTER PROCEDURE P AS SELECT 1")]
    [InlineData("UPDATE STATISTICS dbo.T")]
    [InlineData("GRANT SELECT ON T TO public")]
    [InlineData("BACKUP DATABASE x TO DISK = 'nul'")]
    [InlineData("RESTORE DATABASE x FROM DISK = 'nul'")]
    [InlineData("SELECT * INTO NewTable FROM T")]
    [InlineData("EXEC sp_executesql N'DELETE FROM T'")]
    [InlineData("EXEC xp_cmdshell 'dir'")]
    [InlineData("EXEC dbo.MyCustomProc")]
    [InlineData("DBCC DROPCLEANBUFFERS")]
    [InlineData("DBCC FREEPROCCACHE")]
    [InlineData("SET SHOWPLAN_XML ON")]
    [InlineData("SET IDENTITY_INSERT T ON")]
    [InlineData("CHECKPOINT")]
    [InlineData("SELECT 1; DROP TABLE T")]
    [InlineData("RECONFIGURE")]
    public void Check_BlocksModifyingStatements(string sql)
    {
        var result = ReadOnlySqlGuard.Check(sql);
        Assert.False(result.IsAllowed, $"Expected blocked but was allowed: {sql}");
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    [Fact]
    public void Check_BlocksEmptySql()
    {
        Assert.False(ReadOnlySqlGuard.Check("").IsAllowed);
        Assert.False(ReadOnlySqlGuard.Check("   ").IsAllowed);
        Assert.False(ReadOnlySqlGuard.Check(null).IsAllowed);
    }

    [Fact]
    public void Check_IgnoresKeywordsInStringLiterals()
    {
        var result = ReadOnlySqlGuard.Check("SELECT 'DROP TABLE T' AS threat, 'DELETE everything' AS msg");
        Assert.True(result.IsAllowed, result.Reason);
    }

    [Fact]
    public void Check_IgnoresKeywordsInComments()
    {
        var result = ReadOnlySqlGuard.Check("""
            -- This would DELETE things if it were real
            /* UPDATE T SET x = 1 */
            SELECT 1
            """);
        Assert.True(result.IsAllowed, result.Reason);
    }

    [Fact]
    public void Check_BlocksDmlHiddenAfterGoBatch()
    {
        var result = ReadOnlySqlGuard.Check("SELECT 1\nGO\nDROP TABLE T");
        Assert.False(result.IsAllowed);
    }

    [Fact]
    public void Check_DoesNotBlockColumnNamesContainingKeywords()
    {
        // 'Updates', 'created_at', 'user_updates' must not trigger UPDATE/CREATE detection.
        var result = ReadOnlySqlGuard.Check(
            "SELECT user_updates, created_at FROM sys.dm_db_index_usage_stats ORDER BY user_updates");
        Assert.True(result.IsAllowed, result.Reason);
    }

    [Fact]
    public void StripCommentsAndStrings_HandlesNestedBlockComments()
    {
        var stripped = ReadOnlySqlGuard.StripCommentsAndStrings("SELECT 1 /* outer /* inner */ still comment */ FROM T");
        Assert.DoesNotContain("inner", stripped);
        Assert.DoesNotContain("still comment", stripped);
        Assert.Contains("SELECT 1", stripped);
    }

    [Fact]
    public void StripCommentsAndStrings_HandlesEscapedQuotes()
    {
        var stripped = ReadOnlySqlGuard.StripCommentsAndStrings("SELECT 'it''s a DROP test' FROM T");
        Assert.DoesNotContain("DROP", stripped);
        Assert.Contains("FROM T", stripped);
    }
}
