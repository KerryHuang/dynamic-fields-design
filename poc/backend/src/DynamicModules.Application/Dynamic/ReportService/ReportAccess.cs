using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.ReportService;

/// <summary>目前使用者與其群組；分享只決定看不看得到報表定義，不授予資料權限（spec §7）。</summary>
public sealed record ReportViewer(string UserId, bool IsAdmin, IReadOnlySet<long> GroupIds);

public class ReportAccess(IReportRepository reports, IPermissionRepository permissions, ICurrentUser user, ReportModuleLoader loader)
{
    public async Task<ReportViewer> ViewerAsync(CancellationToken ct)
    {
        var groups = (await permissions.ListMembershipsAsync(ct)).Where(m => m.UserId == user.UserId).Select(m => m.GroupId).ToHashSet();
        var account = await permissions.GetUserAsync(user.UserId, ct);
        return new ReportViewer(user.UserId, account is { IsActive: true, IsSystemAdmin: true }, groups);
    }

    public async Task<IReadOnlyDictionary<string, string>> UserNamesAsync(CancellationToken ct) =>
        (await permissions.ListUsersAsync(ct)).ToDictionary(u => u.UserId, u => u.DisplayName);

    /// <summary>擁有者、分享給我的群組；系統管理員另可看所有有分享的報表（避免離職者留下的共用報表沒人能管）。</summary>
    public static bool CanView(Report r, ReportViewer v) =>
        r.OwnerUserId == v.UserId || r.Shares.Any(s => v.GroupIds.Contains(s.GroupId)) || (v.IsAdmin && r.Shares.Count > 0);

    /// <summary>追蹤中的報表；看不到一律回 null（呼叫端回 404）。</summary>
    public async Task<(Report? Report, ReportViewer Viewer)> FindAsync(long id, CancellationToken ct)
    {
        var viewer = await ViewerAsync(ct);
        var report = await reports.GetAsync(id, ct);
        return (report is not null && CanView(report, viewer) ? report : null, viewer);
    }

    /// <summary>回應用的詳情：條件值依觀看者遮蔽，擁有者也一樣。</summary>
    public async Task<ReportDetail> DetailAsync(Report report, ReportViewer viewer, CancellationToken ct)
    {
        var definition = ReportDefinition.Parse(report.DefinitionJson);
        var (set, _) = await loader.LoadAsync(definition, PermissionAction.Read, ct, lenient: true);
        // 與 ExportReportHandler 同一個載入與授權路徑：任何涉及的模組沒有 PrintExport 就不能匯出
        var (_, exportDenied) = await loader.LoadAsync(definition, PermissionAction.PrintExport, ct);
        return report.ToDetail(viewer, await UserNamesAsync(ct), ReportMasking.Mask(report.DefinitionJson, definition, set!), exportDenied is null);
    }
}
