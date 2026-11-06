using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Acting;
using DynamicModules.Application.Dynamic.DashboardService.Commands.AdminCreateDashboard;
using DynamicModules.Application.Dynamic.DashboardService.Commands.AdminSetDashboardAssignments;
using DynamicModules.Application.Dynamic.DashboardService.Data;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddField;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.PermissionService.Commands.SaveModulePermissions;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Application.Dynamic.PermissionService.Queries.GetModulePermissions;
using DynamicModules.Application.Dynamic.ReportService.Commands.CreateReport;
using DynamicModules.Application.Dynamic.ReportService.Commands.SetReportShares;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Application.Dynamic.TemplateService.Commands.InstantiateTemplate;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using MediatR;

namespace DynamicModules.Application.Dynamic.DemoService;

/// <summary>
/// 建 Demo 的結構（一律以 admin）：套用四個範本、在訂單加「品質異常筆數」、依序啟用、補範本帶不了的權限、建示範報表與預設儀表板。
/// 呼叫端要在交易內；任何一步失敗就回 Failed（訊息前面加步驟名），由外層回滾。
/// </summary>
public sealed class DemoStructure(ISender sender, IPermissionRepository permissions)
{
    public async Task<(DemoContext? Context, ServiceResult? Failed)> BuildAsync(DateOnly today, CancellationToken ct)
    {
        using var _ = ActingScope.Begin("admin");
        ServiceResult r;

        // 1. 套用範本，合併四份 id 對照（模組代號在四個範本間不重複）
        var maps = new List<TemplateIdMap>();
        foreach (var key in DemoCatalog.TemplateKeys)
        {
            r = await sender.Send(new InstantiateTemplateRequest(key, [], []), ct);
            if (!r.IsSuccess) return (null, Fail($"套用範本 {key}", r));
            maps.Add(((InstantiateTemplateResponse)r.Data!).Ids!);
        }
        var ids = Merge(maps);

        // 2. 訂單的品質異常筆數（彙總品質異常的「訂單」參照）
        var orderKey = ids.ModuleKeys["order"];
        r = await sender.Send(new AddFieldRequest(orderKey, ids.BlockIds["order.main"], FieldType.Rollup,
            new I18nText([KeyValuePair.Create("zh-TW", "品質異常筆數"), KeyValuePair.Create("en-US", "Quality issues")]),
            new FieldConfig { ShowInList = true, Rollup = new RollupConfig(ids.FieldIds["quality.order"], RollupFunction.Count, null, null) }), ct);
        if (!r.IsSuccess) return (null, Fail("加欄位 品質異常筆數", r));

        // 3. 啟用
        foreach (var key in DemoCatalog.ActivationOrder)
        {
            r = await sender.Send(new ActivateModuleRequest(key), ct);
            if (!r.IsSuccess) return (null, Fail($"啟用模組 {key}", r));
        }

        // 4. 權限補充：先取完整設定、加上去、整份存回（SaveModulePermissions 是整份取代）
        var groupIds = (await permissions.ListGroupsAsync(ct)).ToDictionary(g => g.Key, g => g.Id);
        var failed = await SupplementAsync(orderKey, p => p with
        {
            Groups = p.Groups.Select(g => g.GroupId == groupIds["qa"]
                ? g with { Privileges = g.Privileges with { Read = true }, Scope = DataScope.All }
                : g).ToList()
        }, ct);
        if (failed is not null) return (null, failed);

        var qualityKey = ids.ModuleKeys["quality"];
        failed = await SupplementAsync(qualityKey, p => p with
        {
            Fields =
            [
                .. p.Fields,
                new FieldPermissionDto(ids.FieldIds["quality.responsibleUnit"], groupIds["sales"], FieldAccess.ReadOnly),
                new FieldPermissionDto(ids.FieldIds["quality.defectRate"], groupIds["floor"], FieldAccess.Hidden)
            ],
            Options =
            [
                .. p.Options.Where(o => o.OptionId != ids.OptionIds["quality.critical"]),
                new OptionScopeDto(ids.OptionIds["quality.critical"], [groupIds["qa"]])
            ]
        }, ct);
        if (failed is not null) return (null, failed);

        var (reportIds, reportFailure) = await CreateReportsAsync(ids, groupIds, ct);
        if (reportFailure is not null) return (null, reportFailure);

        failed = await CreateDashboardsAsync(reportIds, groupIds, ct);
        if (failed is not null) return (null, failed);

        return (new DemoContext(today, sender, ids, groupIds), null);
    }

    /// <summary>5. 建示範報表並分享給各情境的主管群組（擁有者是 admin）。回報表名稱對應的 id，儀表板卡片用。</summary>
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

    /// <summary>6. 建預設的指派版儀表板並指派給群組（依 <see cref="DemoDashboards.All"/> 的順序）。</summary>
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

    private async Task<ServiceResult?> SupplementAsync(
        string moduleKey, Func<ModulePermissionsResponse, ModulePermissionsResponse> change, CancellationToken ct)
    {
        var step = $"補權限 {moduleKey}";
        var current = await sender.Send(new GetModulePermissionsRequest(moduleKey), ct);
        if (!current.IsSuccess) return Fail(step, current);
        var p = change((ModulePermissionsResponse)current.Data!);
        var saved = await sender.Send(new SaveModulePermissionsRequest(moduleKey, new ModulePermissionsDto(p.Groups, p.Users, p.Fields, p.Options)), ct);
        return saved.IsSuccess ? null : Fail(step, saved);
    }

    private static TemplateIdMap Merge(IReadOnlyList<TemplateIdMap> maps) => new(
        maps.SelectMany(m => m.FieldIds).ToDictionary(), maps.SelectMany(m => m.FieldKeys).ToDictionary(),
        maps.SelectMany(m => m.OptionIds).ToDictionary(), maps.SelectMany(m => m.BlockIds).ToDictionary(),
        maps.SelectMany(m => m.ModuleKeys).ToDictionary());

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
