using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.AccountService.SystemPrivileges;

/// <summary>安全事件明細的一筆：設計器動作固定依 read、add、edit、delete 的順序。</summary>
public sealed record PrivilegeHolderState(List<string> Designer, bool PermissionAdmin);

public sealed record PrivilegeHolderChange(string UserId, PrivilegeHolderState Before, PrivilegeHolderState After);

public sealed record PrivilegeHolderChanges(int Total, bool Truncated, List<PrivilegeHolderChange> Items);

/// <summary>持有者快照；<see cref="Admins"/> 是當時的系統管理員，比對時跳過（管理員旗標異動由 admin.changed 負責）。</summary>
public sealed record PrivilegeHolderSnapshot(IReadOnlyDictionary<string, SystemPrivilegesDto> Holders, IReadOnlySet<string> Admins);

/// <summary>
/// 系統權限的實際持有者（權限軸設計 §9，M9）：快照是「非系統管理員使用者 → 合併後的有效值（含 M1）」，不看啟用與否，兩者皆空的人不列。
/// 會讓人開始或不再持有的 handler 在寫入前後、同一交易內各取一次，再交給 <see cref="Report"/> 比對。
/// 一次查出全部持有者，不逐人查、不在列舉查詢結果時再發查詢（Npgsql 同一條連線不能同時開兩個讀取器）。
/// </summary>
public sealed class SystemPrivilegeHolders(IPermissionRepository permissions, AuditContext audit)
{
    /// <summary>安全事件明細最多列出的人數（技術限制，照 admin.changed 的「最近 20 筆」，§16）。</summary>
    private const int MaxItems = 20;

    public async Task<PrivilegeHolderSnapshot> SnapshotAsync(CancellationToken ct)
    {
        var users = await permissions.ListUsersAsync(ct);
        var memberships = await permissions.ListMembershipsAsync(ct);
        var rows = await permissions.LoadSystemPrivilegeRowsAsync(ct);
        var groupsOf = memberships.GroupBy(m => m.UserId).ToDictionary(g => g.Key, g => g.Select(m => m.GroupId).ToHashSet());
        var result = new Dictionary<string, SystemPrivilegesDto>(StringComparer.Ordinal);
        foreach (var user in users.Where(u => !u.IsSystemAdmin))
        {
            var mine = groupsOf.GetValueOrDefault(user.UserId) ?? [];
            var held = SystemPrivilegeRules.Merged(rows.Groups.Where(r => mine.Contains(r.GroupId)).Cast<ProgramPrivilege>()
                .Concat(rows.Users.Where(r => r.UserId == user.UserId)));
            if (held.Any) result[user.UserId] = held;
        }
        return new PrivilegeHolderSnapshot(result, users.Where(u => u.IsSystemAdmin).Select(u => u.UserId).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// 前後有效值不同的人就是異動者；有人就寫 <c>privilegeHolders</c> 明細並標記安全事件（任一人多了任一項就是 granted，只有收回才是 revoked）。
    /// 沒有異動就不發，只記稽核。
    /// </summary>
    public void Report(PrivilegeHolderSnapshot before, PrivilegeHolderSnapshot after)
    {
        var changes = new List<PrivilegeHolderChange>();
        var granted = false;
        foreach (var userId in before.Holders.Keys.Union(after.Holders.Keys).Order(StringComparer.Ordinal))
        {
            if (before.Admins.Contains(userId) || after.Admins.Contains(userId)) continue;
            var b = before.Holders.GetValueOrDefault(userId) ?? SystemPrivilegesDto.None;
            var a = after.Holders.GetValueOrDefault(userId) ?? SystemPrivilegesDto.None;
            if (b == a) continue;
            granted |= Gained(b, a);
            changes.Add(new PrivilegeHolderChange(userId, State(b), State(a)));
        }
        if (changes.Count == 0) return;
        audit.Detail("privilegeHolders", new PrivilegeHolderChanges(changes.Count, changes.Count > MaxItems, changes.Take(MaxItems).ToList()));
        audit.MarkPrivilegeChanged(granted);
    }

    private static PrivilegeHolderState State(SystemPrivilegesDto p) => new(p.Designer.Names(), p.PermissionAdmin);

    private static bool Gained(SystemPrivilegesDto b, SystemPrivilegesDto a) =>
        (a.PermissionAdmin && !b.PermissionAdmin) || a.Designer.Names().Except(b.Designer.Names()).Any();
}
