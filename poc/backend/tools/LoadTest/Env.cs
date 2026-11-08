using DynamicModules.Application;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Domain.Data;
using DynamicModules.Persistence;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace LoadTest;

/// <summary>與 WebAPI 相同的服務組態（每個 scope 一個 DbContext 與一條連線），差別只在固定的使用者與時間。</summary>
public static class Env
{
    /// <summary>固定「今天」，讓資料的日期範圍與查詢可重現。</summary>
    public static readonly DateTime FixedNow = new(2026, 11, 6, 9, 0, 0);

    private sealed class AdminUser : ICurrentUser { public string UserId => "admin"; }

    private sealed class FixedTime : IBusinessTime
    {
        public DateTime Now => FixedNow;
        public DateOnly Today => DateOnly.FromDateTime(FixedNow);
    }

    /// <summary>實驗用：環境變數 LT_PRAGMAS（以分號分隔，如 "synchronous=NORMAL;wal_autocheckpoint=10000"）在每條連線開啟時套用。</summary>
    private sealed class PragmaInterceptor(string[] pragmas) : DbConnectionInterceptor
    {
        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) => Apply(connection);

        public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken ct = default)
        {
            Apply(connection);
            return Task.CompletedTask;
        }

        private void Apply(DbConnection c)
        {
            foreach (var p in pragmas)
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "PRAGMA " + p;
                cmd.ExecuteNonQuery();
            }
        }
    }

    public static string ConnectionString(string dbPath) => $"Data Source={Path.GetFullPath(dbPath)}";

    public static ServiceProvider Build(string dbPath)
    {
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddPersistenceRepositories();
        var pragmas = (Environment.GetEnvironmentVariable("LT_PRAGMAS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
        services.AddDbContext<DynamicDbContext>(o =>
        {
            o.UseSqlite(ConnectionString(dbPath), b => b.MigrationsAssembly(typeof(DynamicModules.Persistence.DependencyInjection).Assembly.FullName));
            if (pragmas.Length > 0) o.AddInterceptors(new PragmaInterceptor(pragmas));
        });
        services.AddScoped<ITransactionManager, EfTransactionManager>();
        services.AddScoped<IWorkSavepoints, EfWorkSavepoints>();
        services.AddSingleton<ICurrentUser, AdminUser>();
        services.AddSingleton(new AuthSettings(false));
        services.AddSingleton<IBusinessTime, FixedTime>();
        var sp = services.BuildServiceProvider();
        // 環境變數 LT_ENGINE（Auto|MemoryOnly|SqlOnly）切換報表引擎，預設 Auto；LT_MAX_LOAD_ROWS 調整載入上限
        if (Enum.TryParse<DynamicModules.Application.Dynamic.ReportService.ReportEngineMode>(Environment.GetEnvironmentVariable("LT_ENGINE"), out var engine))
            sp.GetRequiredService<DynamicModules.Application.Dynamic.ReportService.ReportOptions>().Engine = engine;
        if (int.TryParse(Environment.GetEnvironmentVariable("LT_MAX_LOAD_ROWS"), out var maxLoad))
            sp.GetRequiredService<DynamicModules.Application.Dynamic.ReportService.ReportOptions>().MaxLoadRows = maxLoad;
        return sp;
    }
}
