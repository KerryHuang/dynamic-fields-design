using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

public class McpConfirmationRepository(DynamicDbContext db) : IMcpConfirmationRepository
{
    private DbSet<McpConfirmation> Set => db.Set<McpConfirmation>();

    public void Add(McpConfirmation row) => Set.Add(row);

    public Task<McpConfirmation?> FindByHashTrackedAsync(string codeHash, CancellationToken ct) =>
        Set.FirstOrDefaultAsync(r => r.CodeHash == codeHash, ct);

    public Task<List<McpConfirmation>> ListOpenTrackedAsync(long tokenId, DateTime nowUtc, CancellationToken ct) =>
        Set.Where(r => r.TokenId == tokenId && r.UsedUtc == null && r.VoidedUtc == null && r.ExpiresUtc > nowUtc)
            .OrderBy(r => r.CreatedUtc).ThenBy(r => r.Id).ToListAsync(ct);

    // 條件式更新：兩個並行確認只有一個影響 1 列（PostgreSQL 上在請求的全庫寫入鎖內；計畫判斷 18）
    public Task<int> TryConsumeAsync(long id, DateTime nowUtc, CancellationToken ct) =>
        Set.Where(r => r.Id == id && r.UsedUtc == null && r.VoidedUtc == null && r.ExpiresUtc > nowUtc)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.UsedUtc, nowUtc), ct);

    // 保留期從結束的時間算（計畫判斷 22）：已用看 UsedUtc、作廢看 VoidedUtc、兩者皆無看 ExpiresUtc
    public Task<List<McpConfirmation>> ListPurgeBatchTrackedAsync(DateTime cutoffUtc, int take, CancellationToken ct) =>
        Set.Where(r => (r.UsedUtc != null && r.UsedUtc < cutoffUtc)
                || (r.UsedUtc == null && r.VoidedUtc != null && r.VoidedUtc < cutoffUtc)
                || (r.UsedUtc == null && r.VoidedUtc == null && r.ExpiresUtc < cutoffUtc))
            .OrderBy(r => r.Id).Take(take).ToListAsync(ct);

    public void RemoveRange(IEnumerable<McpConfirmation> rows) => Set.RemoveRange(rows);
}
