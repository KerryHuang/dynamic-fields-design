using System.Security.Cryptography;
using System.Text;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Formulas;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Common.Security;

/// <summary>把個人與群組權限合併成有效權限（規則見第 3 階段「已定決策」1、3、9）。純函式，不碰資料庫。</summary>
public static class PermissionResolver
{
    private static readonly PermissionAction[] AllActions = Enum.GetValues<PermissionAction>();

    /// <param name="accessModule">動作軸、資料範圍取自哪個模組的 ProgramCode 與資料範圍；明細模組傳表頭，其餘為 null（取 module 自己）。</param>
    /// <param name="hiddenSourceFieldIds">彙總來源模組（明細）裡，這位使用者看不到的欄位；以這些欄位為加總對象的彙總一併隱藏。</param>
    /// <param name="forAction">
    /// 要執行的動作。模組有逐動作範圍列（共用主檔模組，設計 §6）時，資料範圍取「使用者所屬群組中授予這個動作者」的最寬範圍；
    /// 沒指定時用讀取的範圍。沒有逐動作範圍列的一般模組不看它，維持所有動作共用讀取範圍。
    /// </param>
    public static EffectivePermissions Resolve(
        Module module, PermissionInputs inputs, Module? accessModule = null, IReadOnlySet<long>? hiddenSourceFieldIds = null,
        PermissionAction? forAction = null)
    {
        var grantSource = accessModule ?? module;
        if (inputs.User.IsSystemAdmin)
            return ForSystemAdmin();

        var actions = Actions(grantSource.ProgramCode, inputs);
        var groups = inputs.GroupIds.ToHashSet();

        var actionScopes = (inputs.ActionScopes ?? []).Where(s => s.ModuleId == grantSource.Id && groups.Contains(s.GroupId)).ToList();
        var readScopes = actionScopes.Count > 0
            ? actionScopes.Where(s => s.Action == (forAction ?? PermissionAction.Read)).Select(s => s.Scope).ToList()
            : inputs.GroupPrivileges
                .Where(p => p.ProgramCode == grantSource.ProgramCode && p.CanRead)
                .Select(p => inputs.Scopes.FirstOrDefault(s => s.ModuleId == grantSource.Id && s.GroupId == p.GroupId)?.Scope
                             ?? DataScope.All)
                .ToList();
        var scope = readScopes.Count > 0 ? readScopes.Max() : DataScope.Own;
        var owners = scope switch
        {
            DataScope.All => null,
            DataScope.Group => inputs.GroupMateUserIds,
            _ => new HashSet<string> { inputs.User.UserId }
        };

        // 金額遮罩是整個模組一起判斷、沒有逐筆遮罩：逐動作範圍下價格的範圍比這次動作的範圍窄（例如讀取 all、價格 own）時，
        // 解開遮罩會讓範圍外的記錄也露出金額，所以視同沒有價格權限（fail closed，審查必修 3）。一般模組沒有逐動作範圍，不受影響
        // 個人 Pricing 沒有逐動作範圍，套用這位使用者的讀取範圍（模組層級的遮罩本來就只放行讀取範圍內的記錄），schema 回報與實際一致
        var personalPricing = inputs.UserPrivilege is { CanAmt: true } up && up.ProgramCode == grantSource.ProgramCode;
        var pricing = actions.Contains(PermissionAction.Pricing)
            && (actionScopes.Count == 0 || personalPricing
                || actionScopes.Where(s => s.Action == PermissionAction.Pricing).Select(s => (DataScope?)s.Scope).Max() is { } pricingScope
                   && pricingScope >= scope);

        var access = inputs.FieldPermissions.Where(p => groups.Contains(p.GroupId)).ToList();
        var hidden = access.Where(p => p.Access == FieldAccess.Hidden).Select(p => p.FieldId).ToHashSet();
        // 限定可見的貢獻欄位：使用者的群組都不在可見列裡就隱藏（fail closed，設計 §6）；可見列只解除這一層，Hidden 仍優先
        var granted = (inputs.FieldGrants ?? []).Where(g => groups.Contains(g.GroupId)).Select(g => g.FieldId).ToHashSet();
        hidden.UnionWith(module.Blocks.SelectMany(b => b.Fields).Where(f => f.VisibilityRestricted && !granted.Contains(f.Id)).Select(f => f.Id));
        if (!pricing)
            hidden.UnionWith(module.Blocks.SelectMany(b => b.Fields)
                .Where(f => f.Type == FieldType.Currency
                            || (f is { Type: FieldType.Rollup, Config.ResultType: "currency" })).Select(f => f.Id));
        if (hiddenSourceFieldIds is { Count: > 0 })
            hidden.UnionWith(module.Blocks.SelectMany(b => b.Fields)
                .Where(f => f is { Type: FieldType.Rollup, Config.Rollup.AggregateFieldId: { } agg } && hiddenSourceFieldIds.Contains(agg))
                .Select(f => f.Id));
        // 公式不洩漏看不到的值：引用任一隱藏欄位（含隱藏的公式）或金額結果但無 Pricing，就一併隱藏。
        foreach (var f in FormulaGraph.EvaluationOrder(module))
            if (FormulaGraph.RefsOf(f).Overlaps(hidden)
                || (f.Config.ResultType == "currency" && !pricing))
                hidden.Add(f.Id);
        var readOnly = access.Where(p => p.Access == FieldAccess.ReadOnly).Select(p => p.FieldId)
            .Where(id => !hidden.Contains(id)).ToHashSet();

        var unselectable = inputs.OptionScopes.GroupBy(s => s.OptionId)
            .Where(g => !g.Any(s => groups.Contains(s.GroupId))).Select(g => g.Key).ToHashSet();

        return Build(actions, scope, owners, hidden, readOnly, unselectable);
    }

    /// <summary>系統管理員的有效權限：全部動作、資料範圍 All、沒有隱藏或唯讀欄位（設計器預覽也用它）。</summary>
    public static EffectivePermissions ForSystemAdmin() =>
        Build(new HashSet<PermissionAction>(AllActions), DataScope.All, null, [], [], []);

    /// <summary>設計器（DYN00000）只需要動作軸。</summary>
    public static IReadOnlySet<PermissionAction> ResolveProgram(string programCode, PermissionInputs inputs) =>
        inputs.User.IsSystemAdmin ? new HashSet<PermissionAction>(AllActions) : Actions(programCode, inputs);

    private static HashSet<PermissionAction> Actions(string programCode, PermissionInputs inputs)
    {
        var rows = inputs.GroupPrivileges.Where(p => p.ProgramCode == programCode).Cast<ProgramPrivilege>().ToList();
        if (inputs.UserPrivilege is { } u && u.ProgramCode == programCode) rows.Add(u);
        return AllActions.Where(a => rows.Any(r => Has(r, a))).ToHashSet();
    }

    private static bool Has(ProgramPrivilege p, PermissionAction a) => a switch
    {
        PermissionAction.Read => p.CanRead,
        PermissionAction.Add => p.CanAdd,
        PermissionAction.Edit => p.CanEdit,
        PermissionAction.Delete => p.CanDelete,
        PermissionAction.PrintExport => p.CanPrint,
        PermissionAction.Pricing => p.CanAmt,
        _ => p.CanApprove
    };

    private static EffectivePermissions Build(
        HashSet<PermissionAction> actions, DataScope scope, IReadOnlySet<string>? owners,
        HashSet<long> hidden, HashSet<long> readOnly, HashSet<long> unselectable)
    {
        static string J<T>(IEnumerable<T> xs) => string.Join(",", xs.Order());
        var canonical = $"a:{J(actions.Select(a => (int)a))}|s:{(int)scope}|o:{(owners is null ? "*" : J(owners))}"
                        + $"|h:{J(hidden)}|r:{J(readOnly)}|u:{J(unselectable)}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..8].ToLowerInvariant();
        return new EffectivePermissions(actions, scope, owners, hidden, readOnly, unselectable, hash);
    }
}
