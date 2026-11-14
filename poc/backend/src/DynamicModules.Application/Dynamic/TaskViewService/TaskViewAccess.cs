using System.Globalization;
using System.Text.Json;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.Tasks;

namespace DynamicModules.Application.Dynamic.TaskViewService;

/// <summary>目前使用者、他所在的群組與直屬部門（看得到哪些看板用）。</summary>
public sealed record TaskViewViewer(string UserId, bool IsAdmin, IReadOnlySet<long> GroupIds, long? DepartmentId);

/// <summary>
/// 看板看得到誰（計畫 J13）：擁有者、分享給本人、本人所在任一群組、本人的直屬部門（不含下層，U8）；
/// 系統管理員另外看得到所有有分享的（只能刪，同報表）。分享只決定看不看得到設定，不授予任何任務權限（§6.2）。
/// </summary>
public sealed class TaskViewAccess(ITaskViewRepository views, IPermissionRepository permissions, IOrganizationRepository org, ICurrentUser user)
{
    public async Task<TaskViewViewer> ViewerAsync(CancellationToken ct)
    {
        var me = user.UserId;
        var groups = (await permissions.ListMembershipsAsync(ct)).Where(m => m.UserId == me).Select(m => m.GroupId).ToHashSet();
        var account = await permissions.GetUserAsync(me, ct);
        var department = (await org.ListUserDepartmentsAsync(ct)).TryGetValue(me, out var d) ? d : (long?)null;
        return new TaskViewViewer(me, account is { IsActive: true, IsSystemAdmin: true }, groups, department);
    }

    public static bool CanView(TaskView v, TaskViewViewer viewer) =>
        v.OwnerUserId == viewer.UserId || (viewer.IsAdmin && v.Shares.Count > 0) || v.Shares.Any(s => SharedTo(s, viewer));

    private static bool SharedTo(TaskViewShare s, TaskViewViewer viewer) => s.TargetKind switch
    {
        TaskViewShareKinds.User => s.TargetId == viewer.UserId,
        TaskViewShareKinds.Group => long.TryParse(s.TargetId, NumberStyles.None, CultureInfo.InvariantCulture, out var g) && viewer.GroupIds.Contains(g),
        TaskViewShareKinds.Department => viewer.DepartmentId is { } d && s.TargetId == d.ToString(CultureInfo.InvariantCulture),
        _ => false,
    };

    /// <summary>追蹤中的看板；看不到一律回 null（呼叫端回 404，不洩漏存在）。</summary>
    public async Task<(TaskView? View, TaskViewViewer Viewer)> FindAsync(long id, CancellationToken ct)
    {
        var viewer = await ViewerAsync(ct);
        var view = await views.GetAsync(id, ct);
        return (view is not null && CanView(view, viewer) ? view : null, viewer);
    }

    public static bool CanDelete(TaskView v, TaskViewViewer viewer) =>
        v.OwnerUserId == viewer.UserId || (viewer.IsAdmin && v.Shares.Count > 0);

    public async Task<TaskViewDetail> DetailAsync(TaskView v, TaskViewViewer viewer, CancellationToken ct)
    {
        var users = (await permissions.ListUsersAsync(ct)).ToDictionary(u => u.UserId);
        var owner = users.GetValueOrDefault(v.OwnerUserId);
        var isOwner = v.OwnerUserId == viewer.UserId;
        IReadOnlyList<TaskViewShareResponse> shares = isOwner || viewer.IsAdmin ? await SharesAsync(v, users, ct) : [];
        var config = JsonDocument.Parse(TaskBoardConfig.Read(v.ConfigJson).Serialize()).RootElement.Clone();
        return new TaskViewDetail(v.Id, v.Kind, v.Name, v.OwnerUserId, owner?.DisplayName, owner is { IsActive: true }, isOwner,
            isOwner, CanDelete(v, viewer), config, shares, v.RowVersion);
    }

    public static TaskViewSummary Summary(TaskView v, TaskViewViewer viewer, IReadOnlyDictionary<string, AppUser> users)
    {
        var owner = users.GetValueOrDefault(v.OwnerUserId);
        return new TaskViewSummary(v.Id, v.Kind, v.Name, v.OwnerUserId, owner?.DisplayName, owner is { IsActive: true },
            v.OwnerUserId == viewer.UserId, v.Shares.Count, v.ModifiedTime ?? v.CreatedTime);
    }

    private async Task<IReadOnlyList<TaskViewShareResponse>> SharesAsync(TaskView v, IReadOnlyDictionary<string, AppUser> users, CancellationToken ct)
    {
        if (v.Shares.Count == 0) return [];
        var groups = (await permissions.ListGroupsAsync(ct)).ToDictionary(g => g.Id.ToString(CultureInfo.InvariantCulture));
        var departments = (await org.ListAsync(ct)).ToDictionary(d => d.Id.ToString(CultureInfo.InvariantCulture));
        return v.Shares.OrderBy(s => s.TargetKind).ThenBy(s => s.TargetId).Select(s => s.TargetKind switch
        {
            TaskViewShareKinds.User => users.TryGetValue(s.TargetId, out var u)
                ? new TaskViewShareResponse(s.TargetKind, s.TargetId, u.DisplayName, null, !u.IsActive)
                : new TaskViewShareResponse(s.TargetKind, s.TargetId, null, null, true),
            TaskViewShareKinds.Group => new TaskViewShareResponse(s.TargetKind, s.TargetId, null,
                groups.GetValueOrDefault(s.TargetId)?.NameI18n, !groups.ContainsKey(s.TargetId)),
            _ => departments.TryGetValue(s.TargetId, out var d)
                ? new TaskViewShareResponse(s.TargetKind, s.TargetId, null, d.NameI18n, !d.Active)
                : new TaskViewShareResponse(s.TargetKind, s.TargetId, null, null, true),
        }).ToList();
    }
}
