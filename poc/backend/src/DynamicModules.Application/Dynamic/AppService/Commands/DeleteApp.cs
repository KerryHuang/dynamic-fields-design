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
/// 任一不成立整個回 409 並列出原因，什麼都不刪。不刪套件開啟時新建的群組（它們可能已有成員與其他授權）。
/// </summary>
public class DeleteAppHandler(
    IAppRepository apps, IModuleRepository modules, DeletePlanner planner, ModuleRemover remover,
    AppDependencyChecker dependencies, IUnitOfWork uow) : IRequestHandler<DeleteAppRequest, ServiceResult>
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

        var records = 0;
        var indexRows = 0;
        var historyRows = 0;
        foreach (var m in members)
        {
            var plan = await planner.PlanAsync(DeleteTarget.Module, m.Key, 0, ct, keys);
            if (plan is null) continue;
            blockers.AddRange(plan.Check.Blockers);
            records += plan.Check.Purge.Records;
            indexRows += plan.Check.Purge.IndexRows;
            historyRows += plan.Check.Purge.HistoryRows;
        }
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
        apps.Remove(app);
        await uow.CommitAsync(ct);
        foreach (var id in moduleIds) await remover.RemoveAsync(id, ct);
        return ServiceResult.Success();
    }
}
