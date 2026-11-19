using System.Data;
using Dapper;
using DynamicModules.Domain.Data;
using DynamicModules.Persistence.Dynamic.Query;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>SQLite 報表快照結束後，連線開啟計數要平衡（釋放 UseTransactionAsync 回傳的交易包裝）。</summary>
public class SqliteReportSqlSnapshotTests
{
    [Fact]
    public async Task Snapshot_ReleasesTheConnection_AndTransactionHandle()
    {
        var path = Path.Combine(Path.GetTempPath(), $"snapshot-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = new DynamicDbContext(new DbContextOptionsBuilder<DynamicDbContext>()
                .UseSqlite($"DataSource={path}").Options);
            var snapshot = new SqliteReportSqlSnapshot(NullLogger<SqliteReportSqlSnapshot>.Instance);

            var result = await snapshot.RunAsync(db, TimeSpan.FromSeconds(5),
                async (connection, tx) => (await connection.ExecuteScalarAsync("SELECT 1", transaction: tx))?.ToString(),
                CancellationToken.None);

            result.Should().Be("1");
            db.Database.CurrentTransaction.Should().BeNull();
            // 計數不平衡時 CloseConnectionAsync 不會真的關閉連線
            db.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }
}
