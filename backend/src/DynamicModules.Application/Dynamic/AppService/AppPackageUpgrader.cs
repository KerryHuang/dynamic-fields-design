using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using static DynamicModules.Application.Dynamic.TemplateService.TemplateGraph;

namespace DynamicModules.Application.Dynamic.AppService;

/// <summary>其他已知套件的模組代號與預設 Module.Key，供 v1 升級時把「字面 Module.Key」的引用改寫成跨應用程式引用。</summary>
public record KnownPackage(string Id, IReadOnlyList<(string Code, string Key)> Modules);

/// <summary>
/// v1（範本）到 v2 的升級，只在記憶體內做、不改檔案。v1 允許 refTarget.key 不在套件內（當成既有的 Module.Key），
/// v2 不允許：找得到對應的已知套件就改寫成 {key: 模組代號, app: 套件 id} 並補進 requires.apps，找不到就升級失敗。
/// </summary>
public static partial class AppPackageUpgrader
{
    [GeneratedRegex("^[a-z][a-zA-Z0-9_]{0,39}$")]
    private static partial Regex GroupCode();

    public static (AppPackageData? Package, IReadOnlyList<string> Errors) FromTemplate(
        TemplatePackage t, IReadOnlyList<KnownPackage> known)
    {
        var errors = new List<string>();
        var codes = t.Modules.Select(m => m.Code).ToHashSet();
        var apps = new Dictionary<string, List<string>>();
        var entities = new List<string>();

        // 群組：v1 的 permissions[].group 是群組 Key；Key 不合 v2 代號格式時把 '-' 換成 '_'
        var groupCodes = new Dictionary<string, string>();
        foreach (var g in t.Modules.SelectMany(m => m.Permissions).Select(p => p.Group).Distinct())
        {
            var code = g.Replace('-', '_');
            if (!GroupCode().IsMatch(code)) errors.Add($"群組代號 {g} 不能當 v2 的群組代號");
            else groupCodes[g] = code;
        }

        var modules = new List<TemplateModule>();
        foreach (var m in t.Modules)
        {
            if (m.HostEntityKey is { } host && !entities.Contains(host)) entities.Add(host);
            var blocks = new List<TemplateBlock>();
            foreach (var b in m.Blocks)
            {
                var fields = new List<TemplateField>();
                foreach (var f in b.Fields)
                {
                    fields.Add(f.Type == FieldType.Reference ? Rewrite(m.Code, f, codes, known, apps, entities, errors) : f);
                }
                blocks.Add(b with { Fields = fields });
            }
            var permissions = m.Permissions
                .Where(p => groupCodes.ContainsKey(p.Group))
                .Select(p => p with { Group = groupCodes[p.Group] }).ToList();
            modules.Add(m with { Blocks = blocks, Permissions = permissions });
        }
        if (errors.Count > 0) return (null, errors);

        var requires = new AppManifestRequires(
            apps.Select(kv => new AppRequirementData(kv.Key, kv.Value)).ToList(), entities);
        var manifest = new AppManifest(t.Key, "1.0.0", t.Name, t.Description,
            t.Modules.Select(m => m.Icon).FirstOrDefault(i => i is not null), null, requires);
        var groups = groupCodes.Select(kv => new AppGroup(kv.Value, new I18nText([KeyValuePair.Create("zh-TW", kv.Key)]))).ToList();
        return (new AppPackageData(AppPackageData.FormatName, AppPackageData.CurrentFormatVersion, manifest, groups, modules,
            null, null, null, null, null, null, null, null), []);
    }

    private static TemplateField Rewrite(
        string module, TemplateField f, HashSet<string> codes, IReadOnlyList<KnownPackage> known,
        Dictionary<string, List<string>> apps, List<string> entities, List<string> errors)
    {
        if (f.Config.ValueKind != JsonValueKind.Object || JsonNode.Parse(f.Config.GetRawText()) is not JsonObject config) return f;
        if (config["refTarget"] is not JsonObject target || target["key"]?.GetValue<string>() is not { } key) return f;

        switch (target["kind"]?.GetValue<string>())
        {
            case ReferenceTarget.EntityKind:
                if (!entities.Contains(key)) entities.Add(key);
                return f;
            case ReferenceTarget.ModuleKind when !codes.Contains(key):
                var hit = known.SelectMany(k => k.Modules.Select(m => (Package: k.Id, m.Code, m.Key)))
                    .FirstOrDefault(x => x.Key == key);
                if (hit.Package is null)
                {
                    errors.Add($"{module}.{f.Code}: 目標模組 {key} 不在套件內，也找不到提供它的其他套件");
                    return f;
                }
                target["key"] = hit.Code;
                target["app"] = hit.Package;
                if (!apps.TryGetValue(hit.Package, out var list)) apps[hit.Package] = list = [];
                if (!list.Contains(hit.Code)) list.Add(hit.Code);
                return f with { Config = JsonSerializer.SerializeToElement(config) };
            default:
                return f;
        }
    }
}
