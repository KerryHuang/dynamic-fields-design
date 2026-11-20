using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

public class LocaleRepository(DynamicDbContext db) : ILocaleRepository
{
    private DbSet<InstalledLocale> Rows => db.Set<InstalledLocale>();

    public Task<List<InstalledLocale>> ListAsync(CancellationToken ct) =>
        Rows.AsNoTracking()
            .OrderBy(l => l.Origin == LocaleOrigin.Builtin ? 0 : 1).ThenBy(l => l.CreatedUtc).ThenBy(l => l.Code)
            .ToListAsync(ct);

    /// <summary>語系表的筆數與最大修改時間，加上覆寫層版本表的筆數與最大 Id（預覽表不算，i4d1 計畫判斷 9）。</summary>
    public async Task<LocaleVersionStamp> StampAsync(CancellationToken ct)
    {
        var count = await Rows.CountAsync(ct);
        var max = count == 0 ? (DateTime?)null : await Rows.MaxAsync(l => l.ModifiedUtc, ct);
        var packs = db.Set<LocalePack>();
        var packCount = await packs.CountAsync(ct);
        var packMaxId = packCount == 0 ? null : await packs.MaxAsync(p => (long?)p.Id, ct);
        return new LocaleVersionStamp(count, max is { } m ? DateTime.SpecifyKind(m, DateTimeKind.Utc) : null, packCount, packMaxId);
    }

    public Task<InstalledLocale?> FindAsync(string code, CancellationToken ct) => Rows.FirstOrDefaultAsync(l => l.Code == code, ct);

    public void Add(InstalledLocale row) => Rows.Add(row);

    public void Remove(InstalledLocale row) => Rows.Remove(row);
}
