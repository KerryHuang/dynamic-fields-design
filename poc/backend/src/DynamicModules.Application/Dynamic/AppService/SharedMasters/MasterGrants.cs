using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.AppService.SharedMasters;

/// <summary>
/// 主檔模組的權限是「來源記錄 → 重算有效值」（設計 §6）：動作取聯集，資料範圍依每一種動作分別取各來源中最寬的。
/// 關閉中的應用程式只計入讀取類動作（讀取、列印匯出、價格）；手動來源永遠計入。只看來源記錄，所以結果與開啟順序無關。
/// </summary>
public sealed class MasterGrants(
    ISharedMasterRepository shared, IPermissionRepository permissions, IModuleRepository modules, IAppRepository apps)
{
    private static readonly PermissionAction[] AllActions = Enum.GetValues<PermissionAction>();

    public static bool IsReadAction(PermissionAction a) => a is PermissionAction.Read or PermissionAction.PrintExport or PermissionAction.Pricing;

    /// <summary>這筆來源記錄目前是否計入有效值。</summary>
    public static bool Counts(AppGrant g, IReadOnlyDictionary<long, AppStatus> appStatus) =>
        g.SourceKind == GrantSource.Manual || IsReadAction(g.Action)
        || g.AppId is { } id && appStatus.TryGetValue(id, out var s) && s == AppStatus.Open;

    /// <summary>有效值：（群組, 動作）→ 最寬的範圍。</summary>
    public static Dictionary<(long Group, PermissionAction Action), DataScope> Effective(
        IEnumerable<AppGrant> grants, IReadOnlyDictionary<long, AppStatus> appStatus) =>
        grants.Where(g => Counts(g, appStatus)).GroupBy(g => (g.GroupId, g.Action))
            .ToDictionary(x => x.Key, x => x.Max(g => g.Scope));

    /// <summary>把一個來源授予的動作記成來源記錄（每個動作一列，已有的同鍵列就放寬範圍）。呼叫端負責提交與重算。</summary>
    public async Task GrantAsync(GrantSource kind, long? appId, long moduleId, long groupId, PrivilegesDto privileges, DataScope scope, CancellationToken ct)
    {
        var existing = await shared.ListGrantsAsync(moduleId, ct);
        foreach (var action in AllActions.Where(a => Has(privileges, a)))
        {
            var row = existing.FirstOrDefault(g => g.SourceKind == kind && g.AppId == appId && g.GroupId == groupId && g.Action == action);
            if (row is null)
            {
                row = new AppGrant { SourceKind = kind, AppId = appId, ModuleId = moduleId, GroupId = groupId, Action = action, Scope = scope };
                shared.AddGrant(row);
                existing.Add(row);
            }
            else if (scope > row.Scope) row.Scope = scope;
        }
    }

    public static bool Has(PrivilegesDto p, PermissionAction a) => a switch
    {
        PermissionAction.Read => p.Read,
        PermissionAction.Add => p.Add,
        PermissionAction.Edit => p.Edit,
        PermissionAction.Delete => p.Delete,
        PermissionAction.PrintExport => p.PrintExport,
        PermissionAction.Pricing => p.Pricing,
        _ => p.Approve
    };

    public async Task<IReadOnlyDictionary<long, AppStatus>> AppStatusAsync(CancellationToken ct) =>
        (await apps.ListAsync(ct)).ToDictionary(a => a.Id, a => a.Status);

    /// <summary>
    /// 依來源記錄重寫主檔模組的群組權限列、資料範圍列（讀取動作的範圍）與逐動作範圍列。
    /// 個人權限、欄位例外與選項範圍原樣保留。呼叫端負責提交。
    /// </summary>
    public async Task RecalcAsync(long moduleId, IReadOnlyDictionary<long, AppStatus>? appStatus, CancellationToken ct)
    {
        var module = await modules.GetByIdAsync(moduleId, includeChildren: false, ct);
        if (module is null) return;
        appStatus ??= await AppStatusAsync(ct);
        var (privileges, scopes, actionScopes) = Rows(module, await shared.ListGrantsAsync(moduleId, ct), appStatus);
        var rows = await permissions.LoadModuleRowsAsync(module, ct);
        await permissions.ReplaceModuleRowsAsync(module, rows with { GroupPrivileges = privileges, Scopes = scopes }, ct);
        await shared.ReplaceActionScopesAsync(moduleId, actionScopes, ct);
    }

    /// <summary>重算每個主檔的每個模組；在主檔狀態或任何應用程式狀態改變之後呼叫，並提交。</summary>
    public async Task RecalcAllAsync(CancellationToken ct)
    {
        var all = await apps.ListAsync(ct);
        var status = all.ToDictionary(a => a.Id, a => a.Status);
        foreach (var member in all.Where(a => a.Kind == AppKind.Master).SelectMany(a => a.Modules))
            await RecalcAsync(member.ModuleId, status, ct);
    }

    /// <summary>完整性：模組現在的群組權限列、資料範圍列、逐動作範圍列等於重算結果。</summary>
    public async Task<IReadOnlyList<string>> CheckAsync(long moduleId, CancellationToken ct)
    {
        var module = await modules.GetByIdAsync(moduleId, includeChildren: false, ct);
        if (module is null) return [];
        var (privileges, scopes, actionScopes) = Rows(module, await shared.ListGrantsAsync(moduleId, ct), await AppStatusAsync(ct));
        var rows = await permissions.LoadModuleRowsAsync(module, ct);
        var problems = new List<string>();
        static string P(ProgramPrivilege p) => $"{p.CanRead}{p.CanAdd}{p.CanEdit}{p.CanDelete}{p.CanPrint}{p.CanAmt}{p.CanApprove}";
        var want = privileges.ToDictionary(p => p.GroupId, P);
        var have = rows.GroupPrivileges.ToDictionary(p => p.GroupId, P);
        if (want.Count != have.Count || want.Any(kv => !have.TryGetValue(kv.Key, out var h) || h != kv.Value))
            problems.Add($"模組 {module.Key} 的群組權限列與來源記錄重算的結果不同");
        var wantScopes = scopes.Select(s => (s.GroupId, s.Scope)).ToHashSet();
        if (!wantScopes.SetEquals(rows.Scopes.Select(s => (s.GroupId, s.Scope))))
            problems.Add($"模組 {module.Key} 的資料範圍列與重算結果不同");
        var haveActions = await shared.ListActionScopesAsync(moduleId, ct);
        if (!actionScopes.Select(s => (s.GroupId, s.Action, s.Scope)).ToHashSet().SetEquals(haveActions.Select(s => (s.GroupId, s.Action, s.Scope))))
            problems.Add($"模組 {module.Key} 的逐動作範圍與重算結果不同");
        return problems;
    }

    private static (List<GroupProgramPrivilege>, List<ModuleDataScope>, List<ModuleActionScope>) Rows(
        Module module, IEnumerable<AppGrant> grants, IReadOnlyDictionary<long, AppStatus> appStatus)
    {
        var effective = Effective(grants, appStatus);
        var privileges = new List<GroupProgramPrivilege>();
        var scopes = new List<ModuleDataScope>();
        foreach (var group in effective.Keys.Select(k => k.Group).Distinct().Order())
        {
            bool On(PermissionAction a) => effective.ContainsKey((group, a));
            privileges.Add(new GroupProgramPrivilege
            {
                GroupId = group, ProgramCode = module.ProgramCode, CanRead = On(PermissionAction.Read), CanAdd = On(PermissionAction.Add),
                CanEdit = On(PermissionAction.Edit), CanDelete = On(PermissionAction.Delete), CanPrint = On(PermissionAction.PrintExport),
                CanAmt = On(PermissionAction.Pricing), CanApprove = On(PermissionAction.Approve)
            });
            if (effective.TryGetValue((group, PermissionAction.Read), out var read))
                scopes.Add(new ModuleDataScope { ModuleId = module.Id, GroupId = group, Scope = read });
        }
        var actionScopes = effective.Select(kv => new ModuleActionScope
            { ModuleId = module.Id, GroupId = kv.Key.Group, Action = kv.Key.Action, Scope = kv.Value }).ToList();
        return (privileges, scopes, actionScopes);
    }
}
