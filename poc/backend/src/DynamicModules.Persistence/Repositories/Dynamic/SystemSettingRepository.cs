using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

public class SystemSettingRepository(DynamicDbContext db) : ISystemSettingRepository
{
    public async Task<string?> GetValueAsync(string key, CancellationToken ct) =>
        (await db.Set<SystemSetting>().AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct))?.Value;

    public async Task SetAsync(string key, string value, CancellationToken ct)
    {
        var setting = await db.Set<SystemSetting>().FirstOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null) db.Set<SystemSetting>().Add(new SystemSetting { Key = key, Value = value });
        else setting.Value = value;
    }

    public async Task<IReadOnlyDictionary<string, string>> ListByPrefixAsync(string prefix, CancellationToken ct)
    {
        // SQLite 的 LIKE 不分大小寫：資料庫先粗篩，再以 Ordinal 精篩
        var rows = await db.Set<SystemSetting>().AsNoTracking().Where(s => s.Key.StartsWith(prefix)).ToListAsync(ct);
        return rows.Where(s => s.Key.StartsWith(prefix, StringComparison.Ordinal)).ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal);
    }

    public async Task RemoveAsync(string key, CancellationToken ct)
    {
        if (await db.Set<SystemSetting>().FirstOrDefaultAsync(s => s.Key == key, ct) is { } setting)
            db.Set<SystemSetting>().Remove(setting);
    }
}
