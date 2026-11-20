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

    public async Task<LocaleVersionStamp> StampAsync(CancellationToken ct)
    {
        var count = await Rows.CountAsync(ct);
        var max = count == 0 ? (DateTime?)null : await Rows.MaxAsync(l => l.ModifiedUtc, ct);
        return new LocaleVersionStamp(count, max is { } m ? DateTime.SpecifyKind(m, DateTimeKind.Utc) : null);
    }
}
