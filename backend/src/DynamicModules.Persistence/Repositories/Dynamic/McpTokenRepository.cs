using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

public class McpTokenRepository(DynamicDbContext db) : IMcpTokenRepository
{
    private DbSet<McpToken> Set => db.Set<McpToken>();

    public void Add(McpToken token) => Set.Add(token);

    public Task<McpToken?> FindByHashAsync(string tokenHash, CancellationToken ct) =>
        Set.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);

    public Task<McpToken?> GetTrackedAsync(long id, CancellationToken ct) => Set.FirstOrDefaultAsync(t => t.Id == id, ct);

    public Task<List<McpToken>> ListByUserAsync(string userId, CancellationToken ct) =>
        Set.AsNoTracking().Where(t => t.UserId == userId).OrderByDescending(t => t.Id).ToListAsync(ct);

    public Task<List<McpToken>> ListAsync(string? userId, CancellationToken ct)
    {
        var q = Set.AsNoTracking();
        if (userId is not null) q = q.Where(t => t.UserId == userId);
        return q.OrderByDescending(t => t.Id).ToListAsync(ct);
    }

    public Task<int> CountActiveAsync(string userId, DateTime nowUtc, CancellationToken ct) =>
        Set.CountAsync(t => t.UserId == userId && t.RevokedUtc == null && t.ExpiresUtc > nowUtc, ct);

    public Task<List<McpToken>> ListActiveTrackedAsync(string userId, DateTime nowUtc, CancellationToken ct) =>
        Set.Where(t => t.UserId == userId && t.RevokedUtc == null && t.ExpiresUtc > nowUtc).OrderBy(t => t.Id).ToListAsync(ct);

    public Task<List<McpToken>> ListPurgeBatchTrackedAsync(DateTime cutoffUtc, int take, CancellationToken ct) =>
        Set.Where(t => t.ExpiresUtc < cutoffUtc || (t.RevokedUtc != null && t.RevokedUtc < cutoffUtc))
            .OrderBy(t => t.Id).Take(take).ToListAsync(ct);

    public void RemoveRange(IEnumerable<McpToken> tokens) => Set.RemoveRange(tokens);
}
