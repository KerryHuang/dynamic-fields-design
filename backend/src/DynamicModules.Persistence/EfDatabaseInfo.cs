using DynamicModules.Application.Dynamic.OpsService;
using DynamicModules.Domain.Data;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence;

public sealed class EfDatabaseInfo(DynamicDbContext db) : IDatabaseInfo
{
    public string Provider => (db.Database.ProviderName ?? "unknown").Split('.').Last().ToLowerInvariant() switch
    {
        "sqlite" => "sqlite",
        "postgresql" => "postgres",   // 與 Database:Provider 的值一致
        var other => other,
    };

    public async Task<string?> LatestMigrationAsync(CancellationToken ct) =>
        (await db.Database.GetAppliedMigrationsAsync(ct)).LastOrDefault();
}
