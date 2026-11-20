using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

/// <summary>
/// 覆寫層版本與預覽暫存（i4d1 計畫判斷 9）。<see cref="DeleteForLocaleAsync"/>、<see cref="PrunePreviewsAsync"/> 用 <c>ExecuteDelete</c>，
/// 都在交易內呼叫（移除語系、預覽的 handler 是 <c>ITransactionalRequest</c>）。
/// </summary>
public class LocalePackRepository(DynamicDbContext db) : ILocalePackRepository
{
    private DbSet<LocalePack> Packs => db.Set<LocalePack>();
    private DbSet<LocalePackPreview> Previews => db.Set<LocalePackPreview>();

    public async Task<List<LocalePack>> ListCurrentAsync(CancellationToken ct)
    {
        var list = await Packs.AsNoTracking().Where(p => p.IsCurrent).ToListAsync(ct);
        // 在記憶體排序：資料庫的字串定序因 provider 而異
        return list.OrderBy(p => p.Code, StringComparer.Ordinal).ToList();
    }

    public Task<LocalePack?> CurrentAsync(string code, CancellationToken ct)
        => Packs.FirstOrDefaultAsync(p => p.Code == code && p.IsCurrent, ct);

    public Task<LocalePack?> PreviousAsync(string code, CancellationToken ct)
        => Packs.FirstOrDefaultAsync(p => p.Code == code && !p.IsCurrent, ct);

    public void AddVersion(LocalePack next, LocalePack? current, LocalePack? previous, DateTime now)
    {
        if (previous is not null) Packs.Remove(previous);
        if (current is not null)
        {
            current.IsCurrent = false;
            current.ModifiedUtc = now;
        }
        next.IsCurrent = true;
        Packs.Add(next);
    }

    public async Task DeleteForLocaleAsync(string code, CancellationToken ct)
    {
        await Packs.Where(p => p.Code == code).ExecuteDeleteAsync(ct);
        await Previews.Where(p => p.Code == code).ExecuteDeleteAsync(ct);
    }

    public void AddPreview(LocalePackPreview p) => Previews.Add(p);

    public Task<LocalePackPreview?> FindPreviewAsync(string id, CancellationToken ct)
        => Previews.FirstOrDefaultAsync(p => p.Id == id, ct);

    public void RemovePreview(LocalePackPreview p) => Previews.Remove(p);

    public async Task PrunePreviewsAsync(DateTime now, int keep, CancellationToken ct)
    {
        await Previews.Where(p => p.ExpiresUtc <= now).ExecuteDeleteAsync(ct);
        var overflow = await Previews.OrderByDescending(p => p.CreatedUtc).ThenByDescending(p => p.Id)
            .Skip(Math.Max(keep, 0)).Select(p => p.Id).ToListAsync(ct);
        if (overflow.Count > 0) await Previews.Where(p => overflow.Contains(p.Id)).ExecuteDeleteAsync(ct);
    }
}
