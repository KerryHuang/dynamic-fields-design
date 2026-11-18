using DynamicModules.Domain.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DynamicModules.Persistence.Postgres;

/// <summary>
/// 只給 dotnet-ef 與 <c>MigrationConsistencyTests</c> 用：產 PostgreSQL migration 不需要連資料庫，連線字串只是佔位。
/// 與執行期共用 <see cref="PostgresDatabase.Configure"/>（PostgreSQL provider 設計 §5.3）。
/// </summary>
public class PostgresDesignTimeDbContextFactory : IDesignTimeDbContextFactory<DynamicDbContext>
{
    public DynamicDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<DynamicDbContext>();
        PostgresDatabase.Configure(builder, "Host=localhost;Database=formwright_design", new PostgresWriteSettings(PostgresWriteSettings.DefaultLockTimeoutSeconds));
        return new DynamicDbContext(builder.Options);
    }
}
