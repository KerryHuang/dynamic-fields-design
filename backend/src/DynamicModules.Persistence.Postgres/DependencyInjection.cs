using DynamicModules.Application.Common;
using DynamicModules.Domain.Data;
using DynamicModules.Persistence.Dynamic.Dialects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.Persistence.Postgres;

public static class DependencyInjection
{
    /// <summary>
    /// PostgreSQL 專屬的註冊（DbContext 選項、方言、交易管理、啟動鎖）加上 provider 無關的 repository；
    /// <c>Program.cs</c> 在 <c>Database:Provider=Postgres</c> 時選這個（PostgreSQL provider 設計 §5.2）。
    /// </summary>
    public static IServiceCollection AddPostgresDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Dynamic")
            ?? throw new InvalidOperationException("Database:Provider=Postgres 需要設定 ConnectionStrings:Dynamic");
        var settings = PostgresWriteSettings.From(configuration);
        services.AddSingleton(settings);
        services.AddDbContext<DynamicDbContext>(o => PostgresDatabase.Configure(o, connectionString, settings));
        services.AddSingleton<ISqlDialect, PostgresDialect>();
        services.AddScoped<ITransactionManager, PostgresTransactionManager>();
        services.AddScoped<IWorkSavepoints, EfWorkSavepoints>();
        services.AddScoped<Application.Dynamic.OpsService.IDatabaseInfo, EfDatabaseInfo>();
        services.AddSingleton<IDatabaseStartupLock>(new PostgresStartupLock(connectionString));
        return services.AddPersistenceRepositories();
    }
}
