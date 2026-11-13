using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.AccountService.Commands.CreateGroup;
using DynamicModules.Application.Dynamic.AccountService.Data;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.AppService.Integrations;
using DynamicModules.Application.Dynamic.AppService.SharedMasters;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.TemplateService;
using DynamicModules.Application.Dynamic.TemplateService.Commands.InstantiateTemplate;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.AppService.Commands;

/// <summary>開啟、關閉、重新開啟、刪除都要設計器對應動作軸加系統管理員：開啟會替群組授權，不該再放大「設計器權限能替自己提權」的風險。</summary>
public record OpenAppRequest(string PackageId, OpenAppBody Body)
    : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, ISystemAdminRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Add;
}

public class OpenAppHandler(AppOpener opener) : IRequestHandler<OpenAppRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(OpenAppRequest request, CancellationToken ct) =>
        (await opener.OpenAsync(request.PackageId, request.Body, ct)).Result;

    /// <summary>模組代號依「被本套件內其他模組參照的先啟用」排序；循環時照宣告順序。</summary>
    internal static IReadOnlyList<string> ActivationOrder(AppPackageData package) => AppOpener.ActivationOrder(package);
}

/// <summary>開啟的結果。Ids 是代號到實際 id／鍵的對照（成功才有），給需要依代號找欄位的呼叫端（Demo 產生器）用。</summary>
public sealed record AppOpenOutcome(ServiceResult Result, TemplateIdMap? Ids);

/// <summary>
/// 開啟應用程式的實作，從 <see cref="OpenAppHandler"/> 抽出來，讓 Demo 產生器在已經是系統管理員的交易內直接開啟，
/// 並拿到欄位、選項的對照。呼叫端負責權限檢查（HTTP 路徑由 <see cref="OpenAppRequest"/> 的 pipeline 檢查）。
/// </summary>
public class AppOpener(
    AppOpenPlanner planner, TemplateInstantiator instantiator, ISender sender, IAppRepository apps,
    AppResponseBuilder builder, AppFlowInstaller flows, IUnitOfWork uow, ICurrentUser user, IBusinessTime time,
    IntegrationReconciler integrations, ISharedMasterRepository shared, IModuleRepository modules, IPermissionRepository permissions,
    MasterSymbols symbols, MasterGrants grants, MasterLifecycle lifecycle, ContributionOrphans orphans)
{
    public async Task<AppOpenOutcome> OpenAsync(string packageId, OpenAppBody body, CancellationToken ct)
    {
        TemplateIdMap? ids = null;
        var result = await OpenCoreAsync(packageId, body, asMaster: false, i => ids = i, ct);
        return new AppOpenOutcome(result, result.IsSuccess ? ids : null);
    }

    private async Task<ServiceResult> OpenCoreAsync(string packageId, OpenAppBody body, bool asMaster, Action<TemplateIdMap> onIds, CancellationToken ct)
    {
        var plan = await planner.PlanAsync(packageId, body, ct, asMaster);
        if (plan.Failure is { } failure) return failure;
        var preview = plan.Preview;
        if (preview.Errors.Count > 0)
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["package"] = [.. preview.Errors] });
        if (!preview.CanOpen) return ServiceResult.ConflictWith("無法開啟應用程式", preview);

        var package = plan.Source!.Package;
        var warnings = new List<string>(preview.Warnings);

        // 共用主檔（§7）：依相依順序，不存在的自動開啟、已關閉的重新開啟、已開啟的直接綁定；全部在同一個交易內
        var masters = new Dictionary<string, App>();
        foreach (var id in plan.MasterOrder)
        {
            var master = await apps.GetByKeyAsync(id, ct);
            if (master is null)
            {
                var opened = await OpenCoreAsync(id, new OpenAppBody(null, null, body.Masters.GetValueOrDefault(id), body.Locale, body.Masters),
                    asMaster: true, _ => { }, ct);
                if (!opened.IsSuccess) return opened;
                if (opened.Data is AppResponse r) warnings.AddRange(r.Warnings.Select(w => $"共用主檔 {id}：{w}"));
                master = (await apps.GetByKeyAsync(id, ct))!;
            }
            else if (master.Status == AppStatus.Closed)
            {
                await lifecycle.SetStatusAsync(master, AppStatus.Open, ct);
                await uow.CommitAsync(ct);
            }
            masters[id] = master;
        }
        var direct = package.Manifest.Requires.Masters.Select(r => masters[r.Id]).ToList();
        var seeds = await symbols.SeedsAsync(direct, ct);
        // 貢獻：同一個應用程式重開時接回它留下的孤兒；型別不同的舊孤兒列刪掉（§7 刪除第 6 點）
        var matched = await orphans.MatchAsync(package, ct);
        var contributions = matched.Inputs;
        warnings.AddRange(matched.Warnings.Where(w => !warnings.Contains(w)));
        foreach (var orphan in matched.Consumed.Concat(matched.Stale)) shared.RemoveOrphan(orphan);

        // 群組：map 用既有的、create 建新的（Key 由 CreateGroup 自動產生）、skip 與未列出的一律不給權限
        var groupIds = new Dictionary<string, long>(plan.MappedGroups);
        foreach (var (code, entry) in body.GroupMap.Where(kv => kv.Value.Mode == "create"))
        {
            var name = package.Groups.First(g => g.Code == code).Name.Resolve(body.Locale);
            var created = await sender.Send(new CreateGroupRequest(name, body.Locale), ct);
            if (!created.IsSuccess) return created;
            groupIds[code] = ((AdminGroupResponse)created.Data!).Id;
        }

        var overrides = package.Modules.ToDictionary(m => m.Code,
            m => new InstantiateModuleInput(m.Code, plan.FinalKeys[m.Code], null));
        var external = new Dictionary<string, string>(plan.ExternalModuleKeys);
        foreach (var (symbol, module) in seeds.Modules) external[symbol] = module.Key;
        var instantiated = await instantiator.InstantiateAsync(AppPackageAdapter.ToTemplate(package), overrides,
            body.ExcludeFields.ToHashSet(), ct,
            new AppInstantiation(groupIds, external, package.FieldAccess, package.OptionScopes, seeds, contributions));
        if (!instantiated.IsSuccess) return instantiated;
        var result = (InstantiateTemplateResponse)instantiated.Data!;
        onIds(result.Ids!);
        warnings.AddRange(result.Warnings);

        // 啟用：被參照的先啟用。套件的模組開啟後直接是 Active，沒有開了但還是草稿的中間狀態；
        // 啟用必須排在建立檢視之後（建立檢視在實例化的步驟 9）
        foreach (var code in ActivationOrder(package))
        {
            var module = result.Modules.First(m => m.Code == code);
            var activated = await sender.Send(new ActivateModuleRequest(module.Key), ct);
            if (!activated.IsSuccess) return activated;
        }

        // 單據能力：狀態機、轉換、報表（群組代號用上面解析好的 id）
        var installed = await flows.InstallAsync(package, result.Ids!, groupIds, body.Locale, ct);
        if (installed.Failure is { } flowFailure) return flowFailure;
        warnings.AddRange(installed.Warnings);

        var app = new App
        {
            Key = package.Manifest.Id, Kind = package.Manifest.IsMaster ? AppKind.Master : AppKind.App,
            NameI18n = package.Manifest.Name, Icon = package.Manifest.Icon,
            PackageVersion = package.Manifest.Version, FormatVersion = package.FormatVersion, Source = plan.Source.Source,
            Status = AppStatus.Open, Requires = AppOpenPlanner.RequiresOf(package),
            Modules = result.Modules.Select(m => new AppModule { Code = m.Code, ModuleId = m.Id }).ToList(),
            Reports = installed.Reports,
            IntegrationsJson = package.Integrations.Count == 0 ? null : IntegrationDeclarations.ToJson(package.Integrations)
        };
        app.TouchRowVersion();
        app.StampCreated(user.UserId, time.Now);
        apps.Add(app);
        await uow.CommitAsync(ct);
        if (await flows.InstallTaskRulesAsync(package, result.Ids!, app.Id, warnings, ct) is { } ruleFailure) return ruleFailure;

        // 代號對照（整合靠它解析夥伴的符號，匯出靠它保留原始代號）與整合的執行狀態；內建套件的整合預設啟用，匯入的要管理員逐一啟用。
        // 主檔的符號只是借來解析的（在主檔名下），不寫進本應用程式；貢獻的對照名稱含 /，記在本應用程式名下（§8）
        var ids2 = result.Ids!;
        apps.AddBindings(
            ids2.FieldIds.Where(x => !seeds.Fields.ContainsKey(x.Key)).Select(x => new AppField { AppId = app.Id, Name = x.Key, FieldId = x.Value }),
            ids2.OptionIds.Where(x => !seeds.Options.ContainsKey(x.Key)).Select(x => new AppOption { AppId = app.Id, Name = x.Key, OptionId = x.Value }),
            ids2.BlockIds.Select(x => new AppBlock { AppId = app.Id, Name = x.Key, BlockId = x.Value }));
        foreach (var g in package.Integrations)
            apps.AddIntegration(new AppIntegration
            {
                AppId = app.Id, Code = g.Code, Enabled = plan.Source.Source == "builtIn",
                Status = plan.Source.Source == "builtIn" ? IntegrationStatus.Waiting : IntegrationStatus.Disabled
            });
        foreach (var r in package.Manifest.Requires.Masters)
            shared.AddUse(new AppMasterUse { AppId = app.Id, MasterAppId = masters[r.Id].Id });
        await uow.CommitAsync(ct);

        await RecordGrantsAsync(package, app, result, contributions, ids2, groupIds, ct);
        foreach (var moduleId in contributions.Select(c => seeds.Modules[c.Prefix].Id).Distinct())
            await lifecycle.ResequenceBlocksAsync(moduleId, ct);
        await uow.CommitAsync(ct);
        if (!asMaster) await lifecycle.RecomputeAsync(ct);

        // 調和要在自己的 SaveChanges 之後（回滾會清掉 change tracker）；整合失敗只回報，不讓開啟失敗
        if (package.Integrations.Count > 0 || (await apps.ListIntegrationsAsync(ct)).Count > 0)
            warnings.AddRange(await integrations.ReconcileAsync(ct));
        return ServiceResult.Created(await builder.BuildAsync(app, withDependents: false, warnings, ct) with { ApprovalSubstitutions = installed.ApprovalSubstitutions });
    }

    /// <summary>
    /// 權限來源記錄（§6）：主檔自己的模組權限記成 master 來源，貢獻的 permissions 記成 app 來源；之後由重算寫出有效值。
    /// 貢獻欄位標「限定可見」，替貢獻者映射到的群組寫可見列；貢獻的 fieldAccess 照一般欄位例外寫入。
    /// </summary>
    private async Task RecordGrantsAsync(
        AppPackageData package, App app, InstantiateTemplateResponse result, IReadOnlyList<ContributionInput> contributions,
        TemplateIdMap ids, IReadOnlyDictionary<string, long> groupIds, CancellationToken ct)
    {
        if (package.Manifest.IsMaster)
            foreach (var m in package.Modules)
            {
                var moduleId = result.Modules.First(x => x.Code == m.Code).Id;
                await grants.GrantAsync(GrantSource.Master, app.Id, moduleId, m.Permissions.Where(p => groupIds.ContainsKey(p.Group))
                    .Select(p => (groupIds[p.Group], p.Privileges, p.Scope ?? DataScope.All)), ct);
            }

        foreach (var c in package.Contributions)
        {
            var input = contributions.First(x => x.Prefix == $"{c.Master}/{c.Module}");
            var module = (await modules.GetByKeyAsync(input.ModuleKey, includeChildren: true, ct))!;
            var mapped = c.Permissions.Where(p => groupIds.ContainsKey(p.Group)).ToList();
            await grants.GrantAsync(GrantSource.App, app.Id, module.Id, mapped.Select(p => (groupIds[p.Group], p.Privileges, p.Scope ?? DataScope.All)), ct);

            var fieldIds = c.Fields.Select(f => ids.FieldIds[TemplateService.TemplateGraph.Qualify(input.Prefix, f.Code)]).ToHashSet();
            var visibleTo = mapped.Select(p => groupIds[p.Group]).Distinct().ToList();
            var existing = await shared.ListFieldGrantsAsync(fieldIds, ct);
            foreach (var field in module.Blocks.SelectMany(b => b.Fields).Where(f => fieldIds.Contains(f.Id)))
            {
                field.VisibilityRestricted = true;
                foreach (var group in visibleTo.Where(g => !existing.Any(x => x.FieldId == field.Id && x.GroupId == g && x.AppId == app.Id)))
                    shared.AddFieldGrant(new FieldGrant { SourceKind = GrantSource.App, FieldId = field.Id, GroupId = group, AppId = app.Id });
            }
            module.TouchVersion(user.UserId, time.Now);

            if (c.FieldAccess.Count > 0)
            {
                var rows = await permissions.LoadModuleRowsAsync(module, ct);
                var access = rows.FieldPermissions.ToList();
                // 多個套件群組映射到同一個群組時，同一個欄位取最嚴的限制（Hidden 優先於 ReadOnly），不是最後一筆覆蓋（審查必修 8）
                var strictest = c.FieldAccess.Where(a => groupIds.ContainsKey(a.Group) && ids.FieldIds.ContainsKey(a.Field))
                    .GroupBy(a => (FieldId: ids.FieldIds[a.Field], GroupId: groupIds[a.Group]))
                    .Select(g => new FieldPermission
                    {
                        FieldId = g.Key.FieldId, GroupId = g.Key.GroupId,
                        Access = g.Any(a => a.Access == FieldAccess.Hidden) ? FieldAccess.Hidden : FieldAccess.ReadOnly
                    });
                foreach (var p in strictest)
                {
                    access.RemoveAll(x => x.FieldId == p.FieldId && x.GroupId == p.GroupId);
                    access.Add(p);
                }
                await permissions.ReplaceModuleRowsAsync(module, rows with { FieldPermissions = access }, ct);
            }
        }
        await uow.CommitAsync(ct);
    }

    /// <summary>模組代號依「被本套件內其他模組參照的先啟用」排序；循環時照宣告順序。</summary>
    internal static IReadOnlyList<string> ActivationOrder(AppPackageData package)
    {
        var codes = package.Modules.Select(m => m.Code).ToList();
        var targets = package.Modules.ToDictionary(m => m.Code, m => m.Blocks.SelectMany(b => b.Fields)
            .Where(f => f.Type == FieldType.Reference)
            .Select(f => TemplateGraph.ConfigObject(f.Config, "refTarget"))
            .Where(t => t is not null && TemplateGraph.ConfigString(t.Value, "app") is null
                && TemplateGraph.ConfigString(t.Value, "kind") == "module")
            .Select(t => TemplateGraph.ConfigString(t!.Value, "key")!)
            .Where(k => codes.Contains(k) && k != m.Code).Distinct().ToList());
        var order = new List<string>();
        var visiting = new HashSet<string>();
        void Visit(string code)
        {
            if (order.Contains(code) || !visiting.Add(code)) return;
            foreach (var target in targets[code]) Visit(target);
            order.Add(code);
        }
        foreach (var code in codes) Visit(code);
        return order;
    }
}
