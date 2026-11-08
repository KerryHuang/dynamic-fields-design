using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.DeleteService;
using DynamicModules.Application.Dynamic.DeleteService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.AppService.Commands;

/// <summary>Purged 是呼叫端看到的「將清除的軟刪除記錄筆數」（全部成員模組合計），與交易內重算的結果不符就拒絕。</summary>
public record DeleteAppRequest(string Key, int? Purged)
    : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, ISystemAdminRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Delete;
}

public record DeleteAppCheck(bool CanDelete, IReadOnlyList<DeleteBlocker> Blockers, PurgeCounts Purge);

/// <summary>
/// 刪除整個應用程式：必須先關閉（兩步是刻意的）。逐一用安全刪除的 DeletePlanner 重新判斷每個成員模組
/// （沒有未軟刪除的記錄、沒有被應用程式外部引用；成員之間互相引用視為內部），再加上沒有開啟中的應用程式依賴它。
/// 它的報表若還在儀表板卡片上也不行。任一不成立整個回 409 並列出原因，什麼都不刪。不刪套件開啟時新建的群組（它們可能已有成員與其他授權）。
/// </summary>
public class DeleteAppHandler(
    IAppRepository apps, IModuleRepository modules, DeletePlanner planner, ModuleRemover remover,
    AppDependencyChecker dependencies, IReportRepository reports, IDashboardRepository dashboards, IUnitOfWork uow,
    Integrations.IntegrationReconciler integrations) : IRequestHandler<DeleteAppRequest, ServiceResult>
{
    public const string DependentKind = "appDependent";
    public const string NotClosedKind = "appNotClosed";

    public async Task<ServiceResult> Handle(DeleteAppRequest request, CancellationToken ct)
    {
        var app = await apps.GetByKeyAsync(request.Key, ct);
        if (app is null) return ServiceResult.NotFound(AppMessages.NotFound);

        var blockers = new List<DeleteBlocker>();
        if (app.Status != AppStatus.Closed)
            blockers.Add(new(NotClosedKind, null, null, app.Key));

        var members = new List<Module>();
        foreach (var member in app.Modules)
            if (await modules.GetByIdAsync(member.ModuleId, includeChildren: false, ct) is { } m) members.Add(m);
        var keys = members.Select(m => m.Key).ToHashSet();

        // 應用程式自己的報表（AppReport）不算外部引用：它們跟著一起刪
        var ownReportIds = app.Reports.Select(r => r.ReportId).ToHashSet();
        var records = 0;
        var indexRows = 0;
        var historyRows = 0;
        foreach (var m in members)
        {
            var plan = await planner.PlanAsync(DeleteTarget.Module, m.Key, 0, ct, keys);
            if (plan is null) continue;
            blockers.AddRange(plan.Check.Blockers.Where(b => !(b.Kind == DeleteBlockerKind.Report && b.Id is { } id && ownReportIds.Contains(id))));
            records += plan.Check.Purge.Records;
            indexRows += plan.Check.Purge.IndexRows;
            historyRows += plan.Check.Purge.HistoryRows;
        }
        // 應用程式自己的報表若還在任何儀表板（指派版、自己的、個人版）的卡片上，刪掉會留下「報表已不存在」的卡片：
        // 不論卡片有沒有指到欄位（SortKey）一律擋下，要先把卡片移掉
        foreach (var reportId in ownReportIds)
            foreach (var reference in await dashboards.ListReferencesAsync(reportId, ct))
                blockers.Add(new(reference.IsPersonalVersion ? DeleteBlockerKind.PersonalDashboard : DeleteBlockerKind.Dashboard,
                    null, reference.DashboardId, reference.DashboardName));
        foreach (var dependent in (await dependencies.DependentsOfAsync(app, ct)).Apps)
            blockers.Add(new(DependentKind, null, null, dependent.Key));

        var purge = new PurgeCounts(records, indexRows, historyRows);
        var check = new DeleteAppCheck(blockers.Count == 0, blockers.DistinctBy(b => (b.Kind, b.ModuleKey, b.Id, b.Name)).ToList(), purge);
        if (!check.CanDelete) return ServiceResult.ConflictWith(DeleteMessages.Blocked, check);
        if (request.Purged is null)
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["purged"] = [DeleteMessages.PurgedRequired] });
        if (request.Purged != records) return ServiceResult.ConflictWith(DeleteMessages.PurgedMismatch, check);

        // 先移除應用程式（連帶它的 AppModule 列），再移除模組：反過來的話模組的級聯刪除會先把 AppModule 列刪掉，EF 再刪一次就會失敗
        var moduleIds = members.Select(m => m.Id).ToList();
        foreach (var reportId in ownReportIds)
            if (await reports.GetAsync(reportId, ct) is { } report) reports.Remove(report);
        apps.Remove(app);
        await uow.CommitAsync(ct);
        foreach (var id in moduleIds) await remover.RemoveAsync(id, ct);
        await integrations.ReconcileAsync(ct);
        return ServiceResult.Success();
    }
}
