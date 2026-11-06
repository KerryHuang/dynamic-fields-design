using DynamicModules.Application.Dynamic.DashboardService.Data;
using DynamicModules.Application.Dynamic.ReportService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.DashboardService;

/// <summary>一張儀表板與觀看者的關係：自己的，或指派給他的群組（可能有他的個人版）。</summary>
public sealed record DashboardScope(Dashboard Dashboard, DashboardPersonalVersion? Personal)
{
    public bool IsAssigned => Dashboard.OwnerUserId is null;

    public DashboardLayout EffectiveLayout => DashboardLayout.Parse(Personal?.LayoutJson ?? Dashboard.LayoutJson);

    public bool HasUpdate => Personal is not null && Personal.BaseVersion < Dashboard.Version;

}

public class DashboardAccess(IDashboardRepository dashboards, IReportRepository reports)
{
    /// <summary>追蹤中的儀表板；不是自己的、也沒指派給觀看者的群組一律回 null（呼叫端回 404），管理員也一樣走自己的視角。</summary>
    public async Task<DashboardScope?> FindAsync(long id, ReportViewer viewer, CancellationToken ct)
    {
        var dashboard = await dashboards.GetAsync(id, ct);
        if (dashboard is null) return null;
        if (dashboard.OwnerUserId is null)
        {
            if (!dashboard.Assignments.Any(a => viewer.GroupIds.Contains(a.GroupId))) return null;
            return new DashboardScope(dashboard, await dashboards.GetPersonalAsync(id, viewer.UserId, ct));
        }
        return dashboard.OwnerUserId == viewer.UserId ? new DashboardScope(dashboard, null) : null;
    }

    /// <summary>回應用的詳情：版面加上卡片引用、且觀看者看得到定義的報表名稱（指派版管理員放上的卡片也算看得到）。</summary>
    public async Task<DashboardDetail> DetailAsync(DashboardScope scope, ReportViewer viewer, CancellationToken ct)
    {
        var layout = scope.EffectiveLayout;
        var fromAssigned = scope.IsAssigned ? DashboardLayout.Parse(scope.Dashboard.LayoutJson).ReportIds() : new HashSet<long>();
        var names = new Dictionary<long, string>();
        foreach (var id in layout.ReportIds())
            if (await reports.GetAsync(id, ct) is { } r && (fromAssigned.Contains(id) || ReportAccess.CanView(r, viewer)))
                names[id] = r.Name;
        var d = scope.Dashboard;
        return new DashboardDetail(d.Id, d.Name, scope.IsAssigned ? "assigned" : "mine", d.Version, scope.Personal is not null, scope.HasUpdate, layout, names);
    }

    /// <summary>觀看者看得到定義的報表 id：自己的、分享給他的群組的；管理員另含所有有分享的。</summary>
    public async Task<HashSet<long>> VisibleReportIdsAsync(ReportViewer viewer, CancellationToken ct)
    {
        var visible = (await reports.ListVisibleAsync(viewer.UserId, viewer.GroupIds, ct)).Select(r => r.Id).ToHashSet();
        if (viewer.IsAdmin) visible.UnionWith((await reports.ListSharedAsync(ct)).Select(r => r.Id));
        return visible;
    }

    /// <summary>
    /// 存檔時每張卡片的報表都要是觀看者看得到的，或原本就在這份版面裡（取消分享後還留著的卡片、指派版的卡片）。
    /// 不讓人拿任意報表 id 放上卡片再透過卡片查詢繞過定義可見性（spec §7）。回 null 表示通過。
    /// </summary>
    public async Task<string?> CheckReportsAsync(DashboardLayout layout, IEnumerable<long> alreadyThere, ReportViewer viewer, CancellationToken ct)
    {
        var allowed = await VisibleReportIdsAsync(viewer, ct);
        allowed.UnionWith(alreadyThere);
        return layout.ReportIds().All(allowed.Contains) ? null : "有卡片使用你看不到的報表";
    }
}
