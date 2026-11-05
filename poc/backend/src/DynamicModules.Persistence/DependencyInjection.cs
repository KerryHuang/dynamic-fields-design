using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.References;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Persistence.Dynamic.Dialects;
using DynamicModules.Persistence.References;
using DynamicModules.Persistence.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.Persistence;

public static class DependencyInjection
{
    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<DynamicDbContext>(o => o.UseSqlite(
            configuration.GetConnectionString("Dynamic"),
            b => b.MigrationsAssembly(typeof(DependencyInjection).Assembly.FullName)));
        services.AddScoped<ITransactionManager, EfTransactionManager>();
        services.AddSingleton<IKnownUsers, DbKnownUsers>();
        return services.AddPersistenceRepositories();
    }

    public static IServiceCollection AddPersistenceRepositories(this IServiceCollection services)
    {
        services.AddSingleton<ISqlDialect, SqliteDialect>();
        services.AddScoped<IModuleRepository, ModuleRepository>();
        services.AddScoped<IProgramRepository, ProgramRepository>();
        services.AddScoped<IListViewRepository, ListViewRepository>();
        services.AddScoped<IPermissionRepository, PermissionRepository>();
        services.AddScoped<IRecordRepository, RecordRepository>();
        services.AddScoped<INumberSequenceRepository, NumberSequenceRepository>();
        services.AddScoped<IRecordHistoryRepository, RecordHistoryRepository>();
        services.AddScoped<IModuleTemplateRepository, ModuleTemplateRepository>();
        services.AddScoped<IReferenceProvider, MockMoldProvider>();
        services.AddScoped<IReferenceProvider, MockCustomerProvider>();
        services.AddScoped<ReferenceProviderRegistry>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        return services;
    }
}
