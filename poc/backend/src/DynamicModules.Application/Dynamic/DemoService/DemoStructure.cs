using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Acting;
using DynamicModules.Application.Dynamic.AppService.Commands;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.DashboardService.Commands.AdminCreateDashboard;
using DynamicModules.Application.Dynamic.DashboardService.Commands.AdminSetDashboardAssignments;
using DynamicModules.Application.Dynamic.DashboardService.Data;
using DynamicModules.Application.Dynamic.ReportService.Commands.CreateReport;
using DynamicModules.Application.Dynamic.ReportService.Commands.SetReportShares;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.DemoService;

/// <summary>
/// 建 Demo 的結構（一律以 admin）：開啟內建的「MES 模具廠」「CRM 與銷售」「進銷存」三個應用程式（群組對應到 Demo 的既有群組），
/// 再建示範報表與預設儀表板。呼叫端要在交易內；任何一步失敗就回 Failed（訊息前面加步驟名），由外層回滾。
/// </summary>
public sealed class DemoStructure(
    ISender sender, IPermissionRepository permissions, AppOpener appOpener, IAppRepository apps, IReportRepository reports,
    EventService.EffectBudget budget, IWriteJournal journal)
{
    public const string CrmAppId = DemoCatalog.CrmAppId;

    public async Task<(DemoContext? Context, ServiceResult? Failed)> BuildAsync(DateOnly today, CancellationToken ct)
    {
        using var _ = ActingScope.Begin("admin");
        var groupIds = (await permissions.ListGroupsAsync(ct)).ToDictionary(g => g.Key, g => g.Id);

        // 套件的群組代號 → Demo 既有的群組，所以示範帳號一登入就有權限走完整條流程
        var opened = new Dictionary<string, TemplateIdMap>();
        foreach (var (appId, map) in new (string, (string Code, string Group)[])[]
        {
            (DemoCatalog.MesAppId, [("sales", "sales"), ("salesManager", "sales-manager"), ("floor", "floor"), ("maint", "maint"), ("qa", "qa"), ("planner", "planner")]),
            (DemoCatalog.CrmAppId, [("sales", "sales"), ("manager", "sales-manager")]),
            (DemoCatalog.InventoryAppId, [("buyer", "purchasing"), ("keeper", "warehouse"), ("manager", "ops-manager")])
        })
        {
            var groupMap = map.ToDictionary(m => m.Code, m => new GroupMapEntry("map", groupIds[m.Group]));
            var app = await appOpener.OpenAsync(appId, new OpenAppBody(null, null, groupMap, null), ct);
            if (!app.Result.IsSuccess) return (null, Fail($"開啟應用程式 {appId}", app.Result));
            opened[appId] = app.Ids!;
        }

        var ids = opened[DemoCatalog.MesAppId];
        var (reportIds, reportFailure) = await CreateReportsAsync(ids, groupIds, ct);
        if (reportFailure is not null) return (null, reportFailure);
        foreach (var appId in DemoCatalog.AppIds)
            foreach (var report in (await apps.GetByKeyAsync(appId, ct))!.Reports)
                reportIds[(await reports.GetAsync(report.ReportId, ct))!.Name] = report.ReportId;

        var failed = await CreateDashboardsAsync(reportIds, groupIds, ct);
        if (failed is not null) return (null, failed);

        return (new DemoContext(today, sender, ids, groupIds)
        {
            BeginStep = () => { journal.Clear(); budget.Reset(); },
            Crm = new DemoContext(today, sender, opened[DemoCatalog.CrmAppId], groupIds),
            Inventory = new DemoContext(today, sender, opened[DemoCatalog.InventoryAppId], groupIds)
        }, null);
    }

    /// <summary>建示範報表並分享給各情境的主管群組（擁有者是 admin）。回報表名稱對應的 id，儀表板卡片用。</summary>
    private async Task<(Dictionary<string, long> Ids, ServiceResult? Failed)> CreateReportsAsync(
        TemplateIdMap ids, IReadOnlyDictionary<string, long> groupIds, CancellationToken ct)
    {
        var reportIds = new Dictionary<string, long>();
        foreach (var spec in DemoReports.All)
        {
            var step = $"建報表 {spec.Name}";
            var created = await sender.Send(new CreateReportRequest(spec.Name, spec.Build(ids)), ct);
            if (!created.IsSuccess) return (reportIds, Fail(step, created));
            var id = ((ReportDetail)created.Data!).Id;
            var shared = await sender.Send(new SetReportSharesRequest(id, [.. spec.GroupKeys.Select(k => groupIds[k])]), ct);
            if (!shared.IsSuccess) return (reportIds, Fail(step, shared));
            reportIds[spec.Name] = id;
        }
        return (reportIds, null);
    }

    /// <summary>建預設的指派版儀表板並指派給群組（依 <see cref="DemoDashboards.All"/> 的順序）。</summary>
    private async Task<ServiceResult?> CreateDashboardsAsync(
        IReadOnlyDictionary<string, long> reportIds, IReadOnlyDictionary<string, long> groupIds, CancellationToken ct)
    {
        foreach (var spec in DemoDashboards.All)
        {
            var step = $"建儀表板 {spec.Name}";
            var layout = new DashboardLayout([.. spec.Cards.Select((c, i) =>
                new DashboardCard($"c{i + 1}", reportIds[c.ReportName], c.Title, c.Viz, c.X, c.Y, c.W, c.H, c.Options))]);
            var created = await sender.Send(new AdminCreateDashboardRequest(spec.Name, layout), ct);
            if (!created.IsSuccess) return Fail(step, created);
            var assigned = await sender.Send(
                new AdminSetDashboardAssignmentsRequest(((AdminDashboardDetail)created.Data!).Id, [.. spec.GroupKeys.Select(k => groupIds[k])]), ct);
            if (!assigned.IsSuccess) return Fail(step, assigned);
        }
        return null;
    }

    /// <summary>失敗結果的訊息前面加步驟名：404／409 改 Message，400 改每則錯誤訊息；403 原樣。</summary>
    private static ServiceResult Fail(string step, ServiceResult r)
    {
        if (r.IsForbidden) return r;
        var message = $"{step}：{r.Message}";
        if (r.IsNotFound) return ServiceResult.NotFound(message);
        if (r.IsConflict) return r.Data is null ? ServiceResult.Conflict(message) : ServiceResult.ConflictWith(message, r.Data);
        return ServiceResult.Invalid(r.Errors.ToDictionary(e => e.Key, e => e.Value.Select(v => $"{step}：{v}").ToArray()));
    }
}
