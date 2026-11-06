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
}
