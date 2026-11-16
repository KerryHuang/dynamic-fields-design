using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
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

    public Task<PermissionGroup?> GetGroupTrackedAsync(long groupId, CancellationToken ct) =>
        db.Set<PermissionGroup>().SingleOrDefaultAsync(g => g.Id == groupId, ct);

    public void AddGroup(PermissionGroup group) => db.Set<PermissionGroup>().Add(group);

    public async Task<bool> GroupNameExistsAsync(string locale, string name, long? excludeId, CancellationToken ct)
    {
        // NameI18n 是 JSON 欄位，群組數量很少，載入後在記憶體比對
        var groups = await db.Set<PermissionGroup>().AsNoTracking().ToListAsync(ct);
        return groups.Any(g => g.Id != excludeId
            && g.NameI18n.Values.TryGetValue(locale, out var n)
            && string.Equals(n.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public async Task<bool> UsersExistAsync(IReadOnlyCollection<string> userIds, CancellationToken ct)
    {
        var distinct = userIds.Distinct().ToList();
        return await db.Set<AppUser>().CountAsync(u => distinct.Contains(u.UserId), ct) == distinct.Count;
    }

    public async Task SetGroupMembersAsync(long groupId, IReadOnlyCollection<string> userIds, CancellationToken ct)
    {
        var set = db.Set<UserGroupMapping>();
        set.RemoveRange(await set.Where(m => m.GroupId == groupId).ToListAsync(ct));
        set.AddRange(userIds.Distinct().Select(id => new UserGroupMapping { GroupId = groupId, UserId = id }));
    }

    public async Task<GroupImpact> GetGroupImpactAsync(long groupId, CancellationToken ct)
    {
        var locale = I18nText.FallbackLocale;
        var members = await db.Set<UserGroupMapping>().CountAsync(m => m.GroupId == groupId, ct);
        var codes = db.Set<GroupProgramPrivilege>().Where(p => p.GroupId == groupId).Select(p => p.ProgramCode);
        var modules = (await db.Set<Module>().AsNoTracking().Where(m => codes.Contains(m.ProgramCode)).OrderBy(m => m.Id).ToListAsync(ct))
            .Select(m => m.NameI18n.Resolve(locale)).ToList();
        var fieldPerms = await db.Set<FieldPermission>().CountAsync(p => p.GroupId == groupId, ct);
        var scopes = await db.Set<ModuleDataScope>().CountAsync(s => s.GroupId == groupId, ct);

        var rows = await (from s in db.Set<FieldOptionScope>().Where(s => s.GroupId == groupId)
                          join o in db.Set<FieldOption>() on s.OptionId equals o.Id
                          join f in db.Set<Field>() on o.FieldId equals f.Id
                          join m in db.Set<Module>() on f.ModuleId equals m.Id
                          orderby m.Id, f.Sequence, o.Sequence
                          select new { Module = m.NameI18n, Field = f.LabelI18n, Option = o.LabelI18n })
            .AsNoTracking().ToListAsync(ct);
        var blocking = rows.Select(r => new BlockingOption(r.Module.Resolve(locale), r.Field.Resolve(locale), r.Option.Resolve(locale))).ToList();
        var reports = await db.Set<Report>().AsNoTracking()
            .Where(r => r.Shares.Any(s => s.GroupId == groupId)).OrderBy(r => r.Id).Select(r => r.Name).ToListAsync(ct);
        var dashboards = await db.Set<Dashboard>().AsNoTracking()
            .Where(d => d.Assignments.Any(a => a.GroupId == groupId)).OrderBy(d => d.Id).Select(d => d.Name).ToListAsync(ct);
        return new GroupImpact(members, modules, fieldPerms, scopes, blocking, reports, dashboards);
    }

    public async Task DeleteGroupAsync(PermissionGroup group, CancellationToken ct)
    {
        var id = group.Id;
        db.Set<UserGroupMapping>().RemoveRange(await db.Set<UserGroupMapping>().Where(x => x.GroupId == id).ToListAsync(ct));
        db.Set<GroupProgramPrivilege>().RemoveRange(await db.Set<GroupProgramPrivilege>().Where(x => x.GroupId == id).ToListAsync(ct));
        db.Set<FieldPermission>().RemoveRange(await db.Set<FieldPermission>().Where(x => x.GroupId == id).ToListAsync(ct));
        db.Set<ModuleDataScope>().RemoveRange(await db.Set<ModuleDataScope>().Where(x => x.GroupId == id).ToListAsync(ct));
        db.Set<FieldOptionScope>().RemoveRange(await db.Set<FieldOptionScope>().Where(x => x.GroupId == id).ToListAsync(ct));
        db.Set<ReportShare>().RemoveRange(await db.Set<ReportShare>().Where(x => x.GroupId == id).ToListAsync(ct));
        var target = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        db.Set<TaskViewShare>().RemoveRange(await db.Set<TaskViewShare>()
            .Where(x => x.TargetKind == TaskViewShareKinds.Group && x.TargetId == target).ToListAsync(ct));
        db.Set<DashboardAssignment>().RemoveRange(await db.Set<DashboardAssignment>().Where(x => x.GroupId == id).ToListAsync(ct));
        db.Set<PermissionGroup>().Remove(group);
    }
}
