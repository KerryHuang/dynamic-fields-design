using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

public class AccountRepository(DynamicDbContext db) : IAccountRepository
{
    public Task<AppUser?> GetTrackedAsync(string userId, CancellationToken ct) =>
        db.Set<AppUser>().SingleOrDefaultAsync(u => u.UserId == userId, ct);

    public async Task<IReadOnlyList<AppUser>> ListDemoLoginAccountsAsync(CancellationToken ct) =>
        await db.Set<AppUser>().AsNoTracking()
            .Where(u => u.IsActive && (u.IsDemoAccount || u.UserId == "admin"))
            .OrderBy(u => u.UserId == "admin" ? 0 : 1).ThenBy(u => u.UserId)
            .ToListAsync(ct);
}
