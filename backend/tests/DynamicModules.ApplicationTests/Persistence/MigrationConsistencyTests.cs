using DynamicModules.Persistence;
using DynamicModules.Persistence.Postgres;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>
/// 模型與 migration 一致：改了實體或設定卻忘了產 migration 就失敗（PostgreSQL provider 設計 §5.3）。
/// 用 design-time factory 建 context，與 dotnet-ef 產 migration 時看到的模型相同；不需要連資料庫。
/// 兩個 provider 各一條 migration 鏈，改 schema 要用 <c>scripts/db/add-migration.sh</c> 一次產兩份。
/// </summary>
public class MigrationConsistencyTests
{
    [Fact]
    public void Sqlite_HasNoPendingModelChanges()
    {
        using var context = new DesignTimeDbContextFactory().CreateDbContext([]);

        context.Database.HasPendingModelChanges().Should().BeFalse(
            "SQLite 的 migration 要涵蓋目前的模型；改了 schema 請用 scripts/db/add-migration.sh 產生");
    }

    [Fact]
    public void Postgres_HasNoPendingModelChanges()
    {
        using var context = new PostgresDesignTimeDbContextFactory().CreateDbContext([]);

        context.Database.HasPendingModelChanges().Should().BeFalse(
            "PostgreSQL 的 migration 要涵蓋目前的模型；改了 schema 請用 scripts/db/add-migration.sh 產生");
    }
}
