using DynamicModules.Domain.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace DynamicModules.Persistence.Postgres;

/// <summary>
/// PostgreSQL 的 DbContext 選項設定，唯一一份（PostgreSQL provider 設計 §5.2、§5.3）：執行期、design-time factory、測試基礎都呼叫它，
/// migration 看到的模型（模型調整器）與執行時的交易行為（閘門與全庫鎖攔截器）才會一致。
/// </summary>
public static class PostgresDatabase
{
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder builder, string connectionString, PostgresWriteSettings settings)
    {
        builder.UseNpgsql(connectionString, b => b.MigrationsAssembly(typeof(PostgresDatabase).Assembly.FullName));
        builder.ReplaceService<IModelCustomizer, PostgresModelCustomizer>();
        builder.ReplaceService<Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure.IProviderConventionSetBuilder, PostgresConventionSetBuilder>();
        builder.AddInterceptors(settings.SaveChangesGate, settings.WriteLock);
        return builder;
    }
}
