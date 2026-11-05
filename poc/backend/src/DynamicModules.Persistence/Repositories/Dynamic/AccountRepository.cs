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

    public void AddUser(AppUser user) => db.Set<AppUser>().Add(user);

    public Task<bool> UserExistsAsync(string userId, CancellationToken ct) =>
        db.Set<AppUser>().AnyAsync(u => u.UserId == userId, ct);

    public Task<int> CountActiveAdminsAsync(CancellationToken ct) =>
        db.Set<AppUser>().CountAsync(u => u.IsActive && u.IsSystemAdmin, ct);

    public async Task SetMembershipsAsync(string userId, IReadOnlyCollection<long> groupIds, CancellationToken ct)
    {
        var set = db.Set<UserGroupMapping>();
        set.RemoveRange(await set.Where(m => m.UserId == userId).ToListAsync(ct));
        set.AddRange(groupIds.Distinct().Select(id => new UserGroupMapping { GroupId = id, UserId = userId }));
    }

    public async Task<bool> GroupsExistAsync(IReadOnlyCollection<long> groupIds, CancellationToken ct)
    {
        var distinct = groupIds.Distinct().ToList();
        return await db.Set<PermissionGroup>().CountAsync(g => distinct.Contains(g.Id), ct) == distinct.Count;
    }
}
