using DynamicModules.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>
/// 模型與 migration 一致：改了實體或設定卻忘了產 migration 就失敗（PostgreSQL provider 設計 §5.3）。
/// 用 design-time factory 建 context，與 dotnet-ef 產 migration 時看到的模型相同；不需要連資料庫。
/// pg0b 加上 PostgreSQL 的 design-time factory 後，這裡再加一個對應的案例。
/// </summary>
public class MigrationConsistencyTests
{
    [Fact]
    public void Sqlite_HasNoPendingModelChanges()
    {
        using var context = new DesignTimeDbContextFactory().CreateDbContext([]);

        context.Database.HasPendingModelChanges().Should().BeFalse(
            "SQLite 的 migration 要涵蓋目前的模型；改了 schema 請用 dotnet ef migrations add 產生");
    }
}
