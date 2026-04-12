using System.Data;
using System.Data.Common;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using Tedd.AIOptimizeSql.OptimizeEngine.Services;

namespace Tedd.AIOptimizeSql.Tests;

public class SqlToolWrapperTests
{
#nullable disable
    private sealed class FakeDbConnection : DbConnection
    {
        public bool WasDisposed { get; private set; }

        public override string ConnectionString { get; set; } = "";

        public override string Database => "";

        public override string DataSource => "";

        public override string ServerVersion => "";

        public override ConnectionState State => ConnectionState.Closed;

        public override void ChangeDatabase(string databaseName)
        {
        }

        public override void Close()
        {
        }

        public override void Open()
        {
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) WasDisposed = true;
            base.Dispose(disposing);
        }
    }

#nullable restore

    private static SqlToolWrapper CreateWrapper(
        Mock<IDatabaseExecutor> executor,
        int maxBytes = 64_000,
        ILogger<SqlToolWrapper>? logger = null)
    {
        var connection = new Mock<DbConnection>();
        return new SqlToolWrapper(
            executor.Object,
            connection.Object,
            maxBytes,
            logger ?? NullLogger<SqlToolWrapper>.Instance);
    }

    [Fact]
    public void ExecuteSqlQuery_returns_message_when_no_rows()
    {
        var executor = new Mock<IDatabaseExecutor>(MockBehavior.Strict);
        executor
            .Setup(e => e.ExecuteQuery(It.IsAny<DbConnection>(), It.IsAny<string>()))
            .Returns([]);

        using var wrapper = CreateWrapper(executor);

        var result = wrapper.ExecuteSqlQuery("SELECT 1 WHERE 1=0");

        Assert.Equal("(no rows returned)", result);
    }

    [Fact]
    public void ExecuteSqlQuery_formats_rows_with_headers()
    {
        var executor = new Mock<IDatabaseExecutor>(MockBehavior.Strict);
        executor
            .Setup(e => e.ExecuteQuery(It.IsAny<DbConnection>(), It.IsAny<string>()))
            .Returns(
            [
                new Dictionary<string, string> { ["Id"] = "1", ["Name"] = "a" },
                new Dictionary<string, string> { ["Id"] = "2", ["Name"] = "b" },
            ]);

        using var wrapper = CreateWrapper(executor);

        var result = wrapper.ExecuteSqlQuery("SELECT Id, Name FROM T");

        Assert.Contains("Id\tName", result, StringComparison.Ordinal);
        Assert.Contains("1\ta", result, StringComparison.Ordinal);
        Assert.Contains("2\tb", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecuteSqlQuery_returns_error_message_on_failure()
    {
        var executor = new Mock<IDatabaseExecutor>(MockBehavior.Strict);
        executor
            .Setup(e => e.ExecuteQuery(It.IsAny<DbConnection>(), It.IsAny<string>()))
            .Throws(new InvalidOperationException("boom"));

        using var wrapper = CreateWrapper(executor);

        var result = wrapper.ExecuteSqlQuery("SELECT 1");

        Assert.StartsWith("ERROR: boom", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecuteSqlNonQuery_returns_success_message()
    {
        var executor = new Mock<IDatabaseExecutor>(MockBehavior.Strict);
        executor
            .Setup(e => e.ExecuteNonQuery(It.IsAny<DbConnection>(), It.IsAny<string>()))
            .Verifiable();

        using var wrapper = CreateWrapper(executor);

        var result = wrapper.ExecuteSqlNonQuery("UPDATE T SET X=1");

        Assert.Equal("Statement executed successfully.", result);
        executor.Verify(
            e => e.ExecuteNonQuery(It.IsAny<DbConnection>(), "UPDATE T SET X=1"),
            Times.Once);
    }

    [Fact]
    public void GetExecutionPlan_truncates_by_utf8_byte_length_not_char_length()
    {
        var executor = new Mock<IDatabaseExecutor>(MockBehavior.Strict);
        var seq = new MockSequence();
        executor.InSequence(seq)
            .Setup(e => e.ExecuteNonQuery(It.IsAny<DbConnection>(), "SET SHOWPLAN_XML ON"))
            .Verifiable();
        // Each "😀" is 4 UTF-8 bytes; 8 chars => 32 bytes > maxResponseBytes (12).
        var wide = string.Concat(Enumerable.Repeat("😀", 8));
        executor.InSequence(seq)
            .Setup(e => e.ExecuteScalar(It.IsAny<DbConnection>(), It.IsAny<string>()))
            .Returns(wide);
        executor.InSequence(seq)
            .Setup(e => e.ExecuteNonQuery(It.IsAny<DbConnection>(), "SET SHOWPLAN_XML OFF"))
            .Verifiable();

        using var wrapper = CreateWrapper(executor, maxBytes: 12);

        var result = wrapper.GetExecutionPlan("SELECT 1");

        Assert.Contains("truncated at 12 bytes", result, StringComparison.Ordinal);
        executor.Verify();
    }

    [Fact]
    public void GetExecutionPlan_toggles_showplan_and_returns_scalar_truncated()
    {
        var executor = new Mock<IDatabaseExecutor>(MockBehavior.Strict);
        var seq = new MockSequence();
        executor.InSequence(seq)
            .Setup(e => e.ExecuteNonQuery(It.IsAny<DbConnection>(), "SET SHOWPLAN_XML ON"))
            .Verifiable();
        executor.InSequence(seq)
            .Setup(e => e.ExecuteScalar(It.IsAny<DbConnection>(), "SELECT 1"))
            .Returns(new string('x', 20));
        executor.InSequence(seq)
            .Setup(e => e.ExecuteNonQuery(It.IsAny<DbConnection>(), "SET SHOWPLAN_XML OFF"))
            .Verifiable();

        using var wrapper = CreateWrapper(executor, maxBytes: 10);

        var result = wrapper.GetExecutionPlan("SELECT 1");

        Assert.Contains("truncated at 10 bytes", result, StringComparison.Ordinal);
        executor.Verify();
    }

    [Fact]
    public void Dispose_disposes_underlying_connection()
    {
        var executor = new Mock<IDatabaseExecutor>(MockBehavior.Strict);
        var connection = new FakeDbConnection();

        using (var wrapper = new SqlToolWrapper(
                   executor.Object,
                   connection,
                   1024,
                   NullLogger<SqlToolWrapper>.Instance))
        {
        }

        Assert.True(connection.WasDisposed);
    }
}
