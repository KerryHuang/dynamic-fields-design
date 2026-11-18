using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Domain.Data.Configurations;

namespace DynamicModules.Application.Dynamic.PermissionService.Commands.SaveModulePermissions;

/// <summary>
/// 權限矩陣的稽核差異（維運設計 §17.2.1：大矩陣只記變動的格子）。每一格一個鍵，例如 <c>group:3.edit</c>、
/// <c>user:sales01.read</c>、<c>field:12/group:3</c>、<c>option:40</c>；沒有授予的格子視為 false／null。
/// </summary>
public static class PermissionAuditCells
{
    /// <summary>摘要最多列的格子數（技術限制：摘要上限 16,000 字元，§17.9）；超過的只計入 <c>changeCount</c>，摘要另帶 <c>truncated</c>。</summary>
    public const int MaxCells = 200;

    public sealed record Cell(string Key, string? Before, string? After);

    public static List<Cell> Diff(ModulePermissionsResponse before, ModulePermissionsResponse after)
    {
        var b = Flatten(before);
        var a = Flatten(after);
        return [.. b.Keys.Union(a.Keys).Order(StringComparer.Ordinal)
            .Select(k => new Cell(k, b.GetValueOrDefault(k), a.GetValueOrDefault(k)))
            .Where(c => c.Before != c.After)];
    }

    private static Dictionary<string, string?> Flatten(ModulePermissionsResponse r)
    {
        var cells = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var g in r.Groups)
        {
            Privileges(cells, $"group:{g.GroupId}", g.Privileges);
            if (g.Scope is { } scope) cells[$"group:{g.GroupId}.scope"] = CamelEnum.Text(scope);
            foreach (var s in g.ActionScopes ?? []) cells[$"group:{g.GroupId}.scope.{CamelEnum.Text(s.Action)}"] = CamelEnum.Text(s.Scope);
        }
        foreach (var u in r.Users) Privileges(cells, $"user:{u.UserId}", u.Privileges);
        foreach (var f in r.Fields) cells[$"field:{f.FieldId}/group:{f.GroupId}"] = CamelEnum.Text(f.Access);
        foreach (var o in r.Options) cells[$"option:{o.OptionId}"] = Ids(o.GroupIds);
        foreach (var v in r.FieldGrants ?? []) cells[$"fieldGrant:{v.FieldId}"] = Ids(v.GroupIds);
        return cells;
    }

    private static void Privileges(Dictionary<string, string?> cells, string prefix, PrivilegesDto p)
    {
        void Set(string action, bool on) { if (on) cells[$"{prefix}.{action}"] = "true"; }
        Set("read", p.Read); Set("add", p.Add); Set("edit", p.Edit); Set("delete", p.Delete);
        Set("printExport", p.PrintExport); Set("pricing", p.Pricing); Set("approve", p.Approve);
    }

    private static string? Ids(List<long> ids) => ids.Count == 0 ? null : string.Join(",", ids.Distinct().Order());
}
