using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Organization;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.ReportService;

/// <summary>一個任務列：任務本身、觀看者看不看得到它的記錄（與看板卡片同一個判斷）、負責人目前的部門。</summary>
public sealed record TaskReportItem(TaskItem Task, bool RecordAccessible, long? AssigneeDepartmentId, bool HasSubtasks);

/// <summary>
/// 任務資料來源（看板設計 §7.5）：欄位目錄（虛擬模組，選項只列觀看者可讀的模組與其規則）與任務列。
/// 列在載入時就套可見條件（<see cref="TaskVisibility"/>），看不到的任務不進報表；記錄能不能看用 <see cref="TaskAccess.EvaluateManyAsync"/>。
/// 目錄依使用者與資料來源快取；一個 scope 跑多個請求時（Demo 產生器、測試）要呼叫 <see cref="Reset"/>。
/// </summary>
public sealed class TaskReportSource(ITaskRepository tasks, ITaskRuleRepository rules, IOrganizationRepository org,
    IModuleRepository modules, TaskVisibilityService visibility, TaskAccess access, ICurrentUser user)
{
    /// <summary>不帶隱藏欄位、動作含 Read 與 PrintExport、範圍 All：任務欄位沒有欄位權限，匯出看得到的任務不另查模組權限（U9）。</summary>
    public static EffectivePermissions Permissions { get; } = new(
        new HashSet<PermissionAction> { PermissionAction.Read, PermissionAction.PrintExport }, DataScope.All, null,
        new HashSet<long>(), new HashSet<long>(), new HashSet<long>(), "tasks");

    private readonly Dictionary<(string UserId, string Dataset), Module> _catalogs = [];

    public void Reset() => _catalogs.Clear();

    /// <summary>J14：一般使用者取可見條件裡的可讀模組；系統管理員取所有可讀的非明細模組。部門含停用部門。</summary>
    public async Task<Module> CatalogAsync(string dataset, CancellationToken ct)
    {
        var key = (user.UserId, dataset);
        if (_catalogs.TryGetValue(key, out var cached)) return cached;
        var v = await visibility.ForCurrentUserAsync(ct);
        var readable = (await modules.ListReadableAsync(ct)).Where(m => !ModuleRelations.IsDetail(m));
        if (!v.Admin)
        {
            var ids = v.Modules.Select(s => s.ModuleId).ToHashSet();
            readable = readable.Where(m => ids.Contains(m.Id));
        }
        var list = readable.OrderBy(m => m.Id).ToList();
        var ruleList = new List<TaskRule>();
        foreach (var m in list) ruleList.AddRange(await rules.ListByModuleAsync(m.Id, ct));
        var catalog = TaskReportFields.Build(dataset, await org.ListAsync(ct), list, ruleList);
        _catalogs[key] = catalog;
        return catalog;
    }

    /// <summary>部門篩選展開成部門 Id 集合（所選部門，含下層時整棵樹，停用部門照走）；整棵樹用全部部門，不看啟用狀態。</summary>
    public async Task<IReadOnlySet<long>> ExpandDepartmentsAsync(IEnumerable<long> selected, bool includeSub, CancellationToken ct)
    {
        var departments = (await org.ListAsync(ct)).ToDictionary(d => d.Id, d => new OrgDepartment(d.Id, d.ParentId, d.ManagerUserId, d.Active));
        return DepartmentScope.Expand(departments, selected, includeSub);
    }

    /// <summary>
    /// 可見條件＋縮小條件載入，最多 take 筆（呼叫端多取一筆判斷是否超過上限）。OverLimit 以縱深防禦丟列之前的原始筆數判斷，
    /// 丟過列也不會把「撈滿 take 筆」誤當成沒超過、回部分結果（kr1b 審查 Y1）。
    /// </summary>
    public async Task<(IReadOnlyList<TaskReportItem> Items, bool OverLimit)> LoadAsync(TaskReportNarrowing narrowing, int take, CancellationToken ct)
    {
        var v = await visibility.ForCurrentUserAsync(ct);
        var items = await tasks.ListForReportAsync(v, narrowing, take, ct);
        var overLimit = items.Count >= take;
        if (items.Count == 0) return ([], overLimit);
        var perms = await access.EvaluateManyAsync(items, ct);
        var departments = items.Any(t => t.AssigneeUserId is not null) ? await org.ListUserDepartmentsAsync(ct) : [];
        // 一次批次查哪些父任務有子任務（任何狀態），層級不隨觀看者變；子任務本身不必查
        var parents = await tasks.SubtaskProgressAsync(items.Where(t => t.ParentTaskId is null).Select(t => t.Id).ToList(), ct);
        var result = new List<TaskReportItem>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            // 縱深防禦：可見條件與逐筆判斷若分歧，一律當看不到
            if (!perms[i].CanSee) continue;
            var t = items[i];
            result.Add(new TaskReportItem(t, perms[i].Record?.Accessible == true,
                t.AssigneeUserId is { } a && departments.TryGetValue(a, out var dept) ? dept : null, parents.ContainsKey(t.Id)));
        }
        return (result, overLimit);
    }
}
