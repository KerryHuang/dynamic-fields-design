using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.DeleteService;
using DynamicModules.Application.Dynamic.DeleteService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using MediatR;

namespace DynamicModules.Application.Dynamic.AppService.Commands;

/// <summary>Purged 是呼叫端看到的「將清除的軟刪除記錄筆數」（全部成員模組合計），與交易內重算的結果不符就拒絕。</summary>
public record DeleteAppRequest(string Key, int? Purged)
    : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, ISystemAdminRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Delete;
}

/// <param name="ClearedValues">共用主檔上要連同欄位清除的值筆數（參照自己模組的貢獻欄位，共用主檔設計 §7 刪除第 4 點）。</param>
/// <param name="PurgedTotal">確認時 purged 要送的數字：軟刪除記錄筆數加上 ClearedValues。</param>
/// <param name="Cleared">逐欄列出要清除的貢獻欄位與值筆數。</param>
/// <param name="Orphaned">有值、改為停用保留的貢獻欄位（主檔模組 key 與欄位名稱）。</param>
public record DeleteAppCheck(bool CanDelete, IReadOnlyList<DeleteBlocker> Blockers, PurgeCounts Purge,
    int ClearedValues = 0, int PurgedTotal = 0, IReadOnlyList<ClearedField>? Cleared = null, IReadOnlyList<ClearedField>? Orphaned = null);

public record ClearedField(string ModuleKey, string Field, int Count);

/// <summary>刪除成功：Warnings 提醒保留下來的共用主檔（最後一個使用者被刪除時）。</summary>
public record DeleteAppResponse(List<string> Warnings);

/// <summary>
/// 刪除整個應用程式：必須先關閉（兩步是刻意的）。逐一用安全刪除的 DeletePlanner 重新判斷每個成員模組
/// （沒有未軟刪除的記錄、沒有被應用程式外部引用；成員之間互相引用視為內部），再加上沒有開啟中的應用程式依賴它。
/// 它的報表若還在儀表板卡片上也不行。任一不成立整個回 409 並列出原因，什麼都不刪。不刪套件開啟時新建的群組（它們可能已有成員與其他授權）。
/// 共用主檔（設計 §7）：貢獻的欄位參照自己模組的連同值清除、其他有值的停用並記成孤兒、沒值的刪掉；自己擁有、目標是主檔的轉換與事件一起刪；
/// 貢獻欄位被外部引用就 409。主檔本身只有沒有任何使用者時才能刪。刪完重算主檔狀態與權限。
/// </summary>
public class DeleteAppHandler(
    IAppRepository apps, IModuleRepository modules, DeletePlanner planner, ModuleRemover remover,
    AppDependencyChecker dependencies, IReportRepository reports, IDashboardRepository dashboards, IUnitOfWork uow,
    Integrations.IntegrationReconciler integrations, ISharedMasterRepository shared, IDeletionRepository deletion,
    SharedMasters.MasterLifecycle lifecycle, ICurrentUser user, Common.Time.IBusinessTime time) : IRequestHandler<DeleteAppRequest, ServiceResult>
{
    public const string DependentKind = "appDependent";
    public const string NotClosedKind = "appNotClosed";
    public const string MasterInUseKind = "masterInUse";

    /// <summary>一個貢獻欄位要怎麼處理。</summary>
    private enum Fate { Purge, Orphan, Drop }

    private sealed record Contribution(Module Module, Field Field, string Name, Fate Fate, int Values);

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

        // 共用主檔：它的使用者還在就不能刪；它使用的主檔模組與它貢獻的欄位，在盤點時豁免（設計 §7 刪除第 1～4 點）
        var uses = await shared.ListUsesAsync(ct);
        if (app.Kind == AppKind.Master)
            foreach (var use in uses.Where(u => u.MasterAppId == app.Id))
                blockers.Add(new(MasterInUseKind, null, use.AppId, (await apps.ListAsync(ct)).FirstOrDefault(a => a.Id == use.AppId)?.Key ?? ""));
        var bindings = await apps.GetBindingsAsync(app.Id, ct);
        var masterModules = new List<Module>();
        foreach (var use in uses.Where(u => u.AppId == app.Id))
            if ((await apps.ListAsync(ct)).FirstOrDefault(a => a.Id == use.MasterAppId) is { } master)
                foreach (var member in master.Modules)
                    if (await modules.GetByIdAsync(member.ModuleId, includeChildren: true, ct) is { } mm) masterModules.Add(mm);
        var contributions = await ContributionsAsync(bindings, masterModules, keys, ct);
        var scope = new AppDeleteScope(masterModules.Select(m => m.Key).ToHashSet(), contributions.Select(c => c.Field.Id).ToHashSet());

        // 應用程式自己的報表（AppReport）不算外部引用：它們跟著一起刪
        var ownReportIds = app.Reports.Select(r => r.ReportId).ToHashSet();
        var records = 0;
        var indexRows = 0;
        var historyRows = 0;
        foreach (var m in members)
        {
            var plan = await planner.PlanAsync(DeleteTarget.Module, m.Key, 0, ct, keys, scope);
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
        blockers.AddRange(await planner.ContributionBlockersAsync(scope.ExemptFieldIds, ownReportIds, keys, scope, ct));

        var purge = new PurgeCounts(records, indexRows, historyRows);
        var cleared = contributions.Where(c => c.Fate == Fate.Purge).Sum(c => c.Values);
        static List<ClearedField> List(IEnumerable<Contribution> xs) =>
            xs.Select(c => new ClearedField(c.Module.Key, c.Field.LabelI18n.Resolve(Domain.ValueObjects.I18nText.FallbackLocale), c.Values)).ToList();
        var check = new DeleteAppCheck(blockers.Count == 0, blockers.DistinctBy(b => (b.Kind, b.ModuleKey, b.Id, b.Name)).ToList(), purge,
            cleared, records + cleared, List(contributions.Where(c => c.Fate == Fate.Purge)), List(contributions.Where(c => c.Fate == Fate.Orphan)));
        if (!check.CanDelete) return ServiceResult.ConflictWith(DeleteMessages.Blocked, check);
        if (request.Purged is null)
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["purged"] = [DeleteMessages.PurgedRequired] });
        if (request.Purged != records + cleared) return ServiceResult.ConflictWith(DeleteMessages.PurgedMismatch, check);

        // 孤兒與清除要在應用程式的對照列被 cascade 刪掉之前做（設計 §15.1 實作注意）
        await SettleContributionsAsync(app, bindings, contributions, ct);
        var usedMasters = SharedMasters.MasterLifecycle.Reachable([app.Id], uses);

        // 先移除應用程式（連帶它的 AppModule 列），再移除模組：反過來的話模組的級聯刪除會先把 AppModule 列刪掉，EF 再刪一次就會失敗
        var moduleIds = members.Select(m => m.Id).ToList();
        foreach (var reportId in ownReportIds)
            if (await reports.GetAsync(reportId, ct) is { } report) reports.Remove(report);
        apps.Remove(app);
        await uow.CommitAsync(ct);
        foreach (var id in moduleIds) await remover.RemoveAsync(id, ct);
        await integrations.ReconcileAsync(ct);

        // 權限重算要在對照列與來源記錄隨應用程式刪除之後（設計 §15.1 實作注意）；主檔狀態跟著閉包走
        await lifecycle.RecomputeAsync(ct);
        foreach (var m in masterModules) await lifecycle.ResequenceBlocksAsync(m.Id, ct);
        await uow.CommitAsync(ct);
        var warnings = new List<string>();
        // 直接或經其他主檔間接用到、而剩下的應用程式（不論開關）都不再用到的主檔
        var all = await apps.ListAsync(ct);
        var stillUsed = SharedMasters.MasterLifecycle.Reachable(all.Where(a => a.Kind == AppKind.App).Select(a => a.Id), await shared.ListUsesAsync(ct));
        foreach (var master in all.Where(a => usedMasters.Contains(a.Id) && !stillUsed.Contains(a.Id)))
        {
            var count = 0;
            foreach (var member in master.Modules) count += await deletion.CountRecordsAsync(member.ModuleId, deleted: false, ct);
            warnings.Add($"共用主檔「{master.NameI18n.Resolve(Domain.ValueObjects.I18nText.FallbackLocale)}」已沒有應用程式使用，仍有 {count} 筆資料，已保留");
        }
        return ServiceResult.Success(new DeleteAppResponse(warnings));
    }

    /// <summary>
    /// 盤點它貢獻的欄位（對照列名稱含 /）：參照自己模組的列入清除（連同值）；其他有值（含軟刪除記錄）的停用保留成孤兒；沒有值的直接刪。
    /// </summary>
    private async Task<List<Contribution>> ContributionsAsync(
        AppBindings bindings, IReadOnlyList<Module> masterModules, IReadOnlySet<string> ownKeys, CancellationToken ct)
    {
        var result = new List<Contribution>();
        foreach (var (name, fieldId) in bindings.Fields.Where(x => x.Key.Contains('/')))
        {
            var module = masterModules.FirstOrDefault(m => m.Blocks.Any(b => b.Fields.Any(f => f.Id == fieldId)));
            if (module is null) continue;
            var field = module.Blocks.SelectMany(b => b.Fields).First(f => f.Id == fieldId);
            var values = (await deletion.ListRecordsWithKeysAsync(module.Id, [field.Key], ct)).Count;
            var ownRef = field is { Type: FieldType.Reference, Config.RefTarget: { Kind: ReferenceTarget.ModuleKind } t } && ownKeys.Contains(t.Key);
            result.Add(new(module, field, name, ownRef ? Fate.Purge : values > 0 ? Fate.Orphan : Fate.Drop, values));
        }
        return result;
    }

    /// <summary>寫孤兒列、停用孤兒欄位；清除與刪除的欄位、空了的貢獻區塊交給 FieldPurge（以系統身分，不經 ClosedDataGuard）。</summary>
    private async Task SettleContributionsAsync(App app, AppBindings bindings, List<Contribution> contributions, CancellationToken ct)
    {
        if (contributions.Count == 0) return;
        var now = time.Now;
        var gone = contributions.Where(c => c.Fate != Fate.Orphan).Select(c => c.Field.Id).ToHashSet();
        foreach (var c in contributions.Where(c => c.Fate == Fate.Orphan))
        {
            c.Field.Status = FieldStatus.Inactive;
            shared.AddOrphan(new AppOrphan { FormerAppKey = app.Key, Kind = OrphanKind.Field, Name = c.Name, FieldId = c.Field.Id });
            foreach (var (name, optionId) in bindings.Options.Where(x => x.Key.Contains('/') && c.Field.Options.Any(o => o.Id == x.Value)))
                shared.AddOrphan(new AppOrphan { FormerAppKey = app.Key, Kind = OrphanKind.Option, Name = name, OptionId = optionId });
        }
        foreach (var module in contributions.Select(c => c.Module).DistinctBy(m => m.Id))
        {
            var emptyBlocks = new List<long>();
            foreach (var (name, blockId) in bindings.Blocks.Where(x => x.Key.Contains('/')))
            {
                if (module.Blocks.FirstOrDefault(b => b.Id == blockId) is not { } block) continue;
                if (block.Fields.All(f => gone.Contains(f.Id))) emptyBlocks.Add(blockId);
                else shared.AddOrphan(new AppOrphan { FormerAppKey = app.Key, Kind = OrphanKind.Block, Name = name, BlockId = blockId });
            }
            module.TouchVersion(user.UserId, now);
            await uow.CommitAsync(ct);
            var ids = contributions.Where(c => c.Module.Id == module.Id && c.Fate != Fate.Orphan).Select(c => c.Field.Id).ToList();
            if (ids.Count > 0 || emptyBlocks.Count > 0)
                await FieldPurge.RemoveAsync(module, ids, emptyBlocks, deletion, uow, user, time, ct);
        }
    }
}
