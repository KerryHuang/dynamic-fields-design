using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

public class PermissionRepository(DynamicDbContext db) : IPermissionRepository
{
    public async Task<PermissionInputs?> LoadInputsAsync(string userId, Module module, CancellationToken ct)
    {
        var (user, groupIds, groupPrivileges, userPrivilege) = await LoadProgramAsync(userId, module.ProgramCode, ct);
        if (user is null) return null;

        var scopes = await db.Set<ModuleDataScope>().AsNoTracking()
            .Where(s => s.ModuleId == module.Id && groupIds.Contains(s.GroupId)).ToListAsync(ct);
        var fieldIds = db.Set<Field>().Where(f => f.ModuleId == module.Id).Select(f => f.Id);
        var fieldPermissions = await db.Set<FieldPermission>().AsNoTracking()
            .Where(p => groupIds.Contains(p.GroupId) && fieldIds.Contains(p.FieldId)).ToListAsync(ct);
        var optionIds = db.Set<FieldOption>().Where(o => fieldIds.Contains(o.FieldId)).Select(o => o.Id);
        var optionScopes = await db.Set<FieldOptionScope>().AsNoTracking()
            .Where(s => optionIds.Contains(s.OptionId)).ToListAsync(ct);
        var mates = await db.Set<UserGroupMapping>().AsNoTracking()
            .Where(m => groupIds.Contains(m.GroupId)).Select(m => m.UserId).Distinct().ToListAsync(ct);

        return new PermissionInputs(user, groupIds, groupPrivileges, userPrivilege,
            scopes, fieldPermissions, optionScopes, new HashSet<string>(mates) { userId });
    }

    public async Task<PermissionInputs?> LoadProgramInputsAsync(string userId, string programCode, CancellationToken ct)
    {
        var (user, groupIds, groupPrivileges, userPrivilege) = await LoadProgramAsync(userId, programCode, ct);
        return user is null
            ? null
            : new PermissionInputs(user, groupIds, groupPrivileges, userPrivilege,
                [], [], [], new HashSet<string> { userId });
    }

    public Task<List<PermissionGroup>> ListGroupsAsync(CancellationToken ct) =>
        db.Set<PermissionGroup>().AsNoTracking().OrderBy(g => g.Id).ToListAsync(ct);

    public Task<List<UserGroupMapping>> ListMembershipsAsync(CancellationToken ct) =>
        db.Set<UserGroupMapping>().AsNoTracking().OrderBy(m => m.GroupId).ThenBy(m => m.UserId).ToListAsync(ct);

    public Task<List<AppUser>> ListUsersAsync(CancellationToken ct) =>
        db.Set<AppUser>().AsNoTracking().OrderBy(u => u.UserId).ToListAsync(ct);

    public Task<AppUser?> GetUserAsync(string userId, CancellationToken ct) =>
        db.Set<AppUser>().AsNoTracking().FirstOrDefaultAsync(u => u.UserId == userId, ct);

    public async Task<ModulePermissionRows> LoadModuleRowsAsync(Module module, CancellationToken ct)
    {
        var fieldIds = db.Set<Field>().Where(f => f.ModuleId == module.Id).Select(f => f.Id);
        var optionIds = db.Set<FieldOption>().Where(o => fieldIds.Contains(o.FieldId)).Select(o => o.Id);
        return new ModulePermissionRows(
            await db.Set<GroupProgramPrivilege>().AsNoTracking().Where(p => p.ProgramCode == module.ProgramCode).ToListAsync(ct),
            await db.Set<UserProgramPrivilege>().AsNoTracking().Where(p => p.ProgramCode == module.ProgramCode).ToListAsync(ct),
            await db.Set<ModuleDataScope>().AsNoTracking().Where(s => s.ModuleId == module.Id).ToListAsync(ct),
            await db.Set<FieldPermission>().AsNoTracking().Where(p => fieldIds.Contains(p.FieldId)).ToListAsync(ct),
            await db.Set<FieldOptionScope>().AsNoTracking().Where(s => optionIds.Contains(s.OptionId)).ToListAsync(ct));
    }

    public async Task ReplaceModuleRowsAsync(Module module, ModulePermissionRows rows, CancellationToken ct)
    {
        var fieldIds = db.Set<Field>().Where(f => f.ModuleId == module.Id).Select(f => f.Id);
        var optionIds = db.Set<FieldOption>().Where(o => fieldIds.Contains(o.FieldId)).Select(o => o.Id);
        await db.Set<GroupProgramPrivilege>().Where(p => p.ProgramCode == module.ProgramCode).ExecuteDeleteAsync(ct);
        await db.Set<UserProgramPrivilege>().Where(p => p.ProgramCode == module.ProgramCode).ExecuteDeleteAsync(ct);
        await db.Set<ModuleDataScope>().Where(s => s.ModuleId == module.Id).ExecuteDeleteAsync(ct);
        await db.Set<FieldPermission>().Where(p => fieldIds.Contains(p.FieldId)).ExecuteDeleteAsync(ct);
        await db.Set<FieldOptionScope>().Where(s => optionIds.Contains(s.OptionId)).ExecuteDeleteAsync(ct);

        // ExecuteDelete 不經變更追蹤；同一個 context 先前寫入的列要放掉，否則新列會撞到相同鍵
        // 只放掉 Unchanged，待寫入的 Added／Modified 不會被默默丟棄
        foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State == EntityState.Unchanged
                     && e.Entity is ProgramPrivilege or ModuleDataScope or FieldPermission or FieldOptionScope).ToList())
            entry.State = EntityState.Detached;

        db.Set<GroupProgramPrivilege>().AddRange(rows.GroupPrivileges);
        db.Set<UserProgramPrivilege>().AddRange(rows.UserPrivileges);
        db.Set<ModuleDataScope>().AddRange(rows.Scopes);
        db.Set<FieldPermission>().AddRange(rows.FieldPermissions);
        db.Set<FieldOptionScope>().AddRange(rows.OptionScopes);
    }

    private async Task<(AppUser? User, List<long> GroupIds, List<GroupProgramPrivilege> Privileges, UserProgramPrivilege? Personal)>
        LoadProgramAsync(string userId, string programCode, CancellationToken ct)
    {
        var user = await db.Set<AppUser>().AsNoTracking().FirstOrDefaultAsync(u => u.UserId == userId, ct);
        if (user is null) return (null, [], [], null);

        var groupIds = await db.Set<UserGroupMapping>().AsNoTracking()
            .Where(m => m.UserId == userId).Select(m => m.GroupId).ToListAsync(ct);
        var privileges = await db.Set<GroupProgramPrivilege>().AsNoTracking()
            .Where(p => p.ProgramCode == programCode && groupIds.Contains(p.GroupId)).ToListAsync(ct);
        var personal = await db.Set<UserProgramPrivilege>().AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId && p.ProgramCode == programCode, ct);
        return (user, groupIds, privileges, personal);
    }
}
