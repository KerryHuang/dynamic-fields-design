using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.DemoService;
using DynamicModules.Application.Dynamic.References;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Persistence.Dynamic.Dialects;
using DynamicModules.Persistence.References;
using DynamicModules.Persistence.Seed;
using DynamicModules.Persistence.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.Persistence;

public static class DependencyInjection
{
    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        // Sqlite:MmapSizeMb（MB，預設 0＝關閉）：每條連線的記憶體對映大小，只在 SQLite 時套用，見 README 的設定說明
        var mmapBytes = long.TryParse(configuration["Sqlite:MmapSizeMb"], out var mmapMb) ? mmapMb * 1024 * 1024 : 0;
        services.AddDbContext<DynamicDbContext>(o =>
        {
            o.UseSqlite(
                configuration.GetConnectionString("Dynamic"),
                b => b.MigrationsAssembly(typeof(DependencyInjection).Assembly.FullName));
            if (mmapBytes > 0) o.AddInterceptors(new SqliteMmapInterceptor(mmapBytes));
        });
        services.AddScoped<ITransactionManager, EfTransactionManager>();
        services.AddScoped<IWorkSavepoints, EfWorkSavepoints>();
        return services.AddPersistenceRepositories();
    }

    public static IServiceCollection AddPersistenceRepositories(this IServiceCollection services)
    {
        services.AddSingleton<ISqlDialect, SqliteDialect>();
        services.AddScoped<IModuleRepository, ModuleRepository>();
        services.AddScoped<IProgramRepository, ProgramRepository>();
        services.AddScoped<IListViewRepository, ListViewRepository>();
        services.AddScoped<IConversionRepository, ConversionRepository>();
        services.AddScoped<IEventRepository, EventRepository>();
        services.AddScoped<IEventFactRepository, EventFactRepository>();
        services.AddScoped<ICheckRepository, CheckRepository>();
        services.AddScoped<ILedgerRepository, LedgerRepository>();
        services.AddScoped<IOperationRepository, OperationRepository>();
        services.AddScoped<IPrintTemplateRepository, PrintTemplateRepository>();
        services.AddScoped<IReportRepository, ReportRepository>();
        services.AddScoped<IReportQueryRepository, ReportQueryRepository>();
        services.AddScoped<IDashboardRepository, DashboardRepository>();
        services.AddScoped<ISystemSettingRepository, SystemSettingRepository>();
        services.AddScoped<IPermissionRepository, PermissionRepository>();
        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddScoped<IRecordRepository, RecordRepository>();
        services.AddScoped<IDeletionRepository, DeletionRepository>();
        services.AddScoped<INumberSequenceRepository, NumberSequenceRepository>();
        services.AddScoped<IRecordHistoryRepository, RecordHistoryRepository>();
        services.AddScoped<IModuleTemplateRepository, ModuleTemplateRepository>();
        services.AddScoped<IAppRepository, AppRepository>();
        services.AddScoped<IAppPackageRepository, AppPackageRepository>();
        services.AddScoped<IReferenceProvider, MockMoldProvider>();
        services.AddScoped<IReferenceProvider, MockCustomerProvider>();
        services.AddScoped<ReferenceProviderRegistry>();
        services.AddScoped<IDemoPeople, DemoPeopleSeeder>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        return services;
    }
}
