using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.TemplateService;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using static DynamicModules.Application.Dynamic.TemplateService.TemplateGraph;

namespace DynamicModules.Application.Dynamic.AppService;

public record AppValidation(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

/// <summary>
/// v2 套件的結構層與引用層（純函式，啟動、匯入、開啟共用）。前一層有錯就不跑下一層。
/// 模組內部的檢查（代號、公式、彙總、規則、循環）交給既有的 TemplateValidator，經 AppPackageAdapter 轉接。
/// 錯誤格式「{位置}: {原因}」，模組內的位置沿用「{模組代號}.{欄位代號}」。
/// </summary>
public static partial class AppPackageValidator
{
    public const int MaxGroups = 20, MaxModules = 30, MaxFieldsPerModule = 300, MaxOptionsPerField = 200;
    public const int MaxConversions = 50, MaxReports = 50, MaxPrintTemplates = 100;
    public const int MaxRulesPerModule = 100, MaxViewsPerModule = 20, MaxAccessEntries = 2000;

    [GeneratedRegex("^[a-z][a-zA-Z0-9_]{0,39}$")]
    private static partial Regex Code();

    [GeneratedRegex(ModuleRules.ModuleKeyPattern)]
    private static partial Regex AppId();

    [GeneratedRegex(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")]
    private static partial Regex SemVer();

    private static readonly HashSet<string> ConfigKeys = new(StringComparer.Ordinal)
    {
        "required", "readOnly", "maxLength", "precision", "scale", "defaultValue", "defaultToday", "parentDefaultField",
        "fullWidth", "showInList", "isFilterable", "isSortable", "formula", "resultType", "format", "refTarget", "onDelete",
        "relation", "scope", "rollup", "fill", "refGuard"
    };

    private static readonly HashSet<string> RefTargetKeys = new(StringComparer.Ordinal) { "kind", "key", "app" };
    private static readonly HashSet<string> FillKeys = new(StringComparer.Ordinal) { "mode", "map" };
    private static readonly HashSet<string> RefGuardKeys = new(StringComparer.Ordinal) { "field", "in" };
    private static readonly HashSet<string> FillMapKeys = new(StringComparer.Ordinal) { "from", "to" };
    private static readonly HashSet<string> RollupKeys = new(StringComparer.Ordinal) { "sourceField", "function", "aggregateField", "condition" };

    public static AppValidation Validate(AppPackageData p)
    {
        var warnings = new List<string>();
        var errors = Structure(p);
        if (errors.Count > 0) return new AppValidation(errors, warnings);
        errors = References(p, warnings);
        return new AppValidation(errors, warnings);
    }

    // ---- 結構層 ----

    private static List<string> Structure(AppPackageData p)
    {
        var errors = new List<string>();
        if (p.Format != AppPackageData.FormatName) errors.Add($"format: 必須是 \"{AppPackageData.FormatName}\"");
        if (p.FormatVersion is < AppPackageData.MinFormatVersion or > AppPackageData.CurrentFormatVersion)
            errors.Add($"formatVersion: 必須是 {AppPackageData.MinFormatVersion} 或 {AppPackageData.CurrentFormatVersion}");
        foreach (var section in p.UnsupportedSections()) errors.Add($"{section}: 此版本不支援 {section}");
        // 格式 2 的讀取器對這幾個 section 一律 fail-closed：靜默略過 workflows 會讓客戶開出缺了控管的單據流程
        if (p.FormatVersion < 3)
        {
            if (p.Workflows.Count > 0) errors.Add("workflows: formatVersion 3 才支援 workflows");
            if (p.Conversions.Count > 0) errors.Add("conversions: formatVersion 3 才支援 conversions");
            if (p.Reports.Count > 0) errors.Add("reports: formatVersion 3 才支援 reports");
            if (p.PrintTemplates.Count > 0) errors.Add("printTemplates: formatVersion 3 才支援 printTemplates");
        }
        if (p.FormatVersion < 4)
        {
            if (p.Events.Count > 0) errors.Add("events: formatVersion 4 才支援 events");
            if (p.Checks.Count > 0) errors.Add("checks: formatVersion 4 才支援 checks");
            if (p.Ledgers.Count > 0) errors.Add("ledgers: formatVersion 4 才支援 ledgers");
        }
        if (p.Events.Count > MaxEvents) errors.Add($"events: 最多 {MaxEvents} 個");
        if (p.Checks.Count > MaxChecks) errors.Add($"checks: 最多 {MaxChecks} 個");
        if (p.Workflows.Count > MaxModules) errors.Add($"workflows: 最多 {MaxModules} 個");
        if (p.Conversions.Count > MaxConversions) errors.Add($"conversions: 最多 {MaxConversions} 個");
        if (p.Reports.Count > MaxReports) errors.Add($"reports: 最多 {MaxReports} 個");
        if (p.PrintTemplates.Count > MaxPrintTemplates) errors.Add($"printTemplates: 最多 {MaxPrintTemplates} 個");

        var m = p.Manifest;
        if (!AppId().IsMatch(m.Id ?? "")) errors.Add($"manifest.id: 格式錯誤（小寫英文、數字、連字號，2 到 40 字，英文開頭）");
        if (!SemVer().IsMatch(m.Version ?? "")) errors.Add("manifest.version: 必須是 MAJOR.MINOR.PATCH");
        CheckText(m.Name, "manifest.name", errors);
        if (m.Description is not null) CheckText(m.Description, "manifest.description", errors);
        if (m.Icon is { Length: > 100 }) errors.Add("manifest.icon: 不能超過 100 字");
        if (m.Vendor is { Length: > 100 }) errors.Add("manifest.vendor: 不能超過 100 字");
        if (m.Requires.Apps.Count > 10) errors.Add("manifest.requires.apps: 最多 10 個");
        if (m.Requires.Entities.Count > 20) errors.Add("manifest.requires.entities: 最多 20 個");
        for (var i = 0; i < m.Requires.Apps.Count; i++)
        {
            var r = m.Requires.Apps[i];
            if (r is null || !AppId().IsMatch(r.Id ?? "")) errors.Add($"manifest.requires.apps[{i}].id: 格式錯誤");
            else if (r.Modules.Count == 0 || r.Modules.Any(c => !AppId().IsMatch(c ?? "")))
                errors.Add($"manifest.requires.apps[{i}].modules: 至少要有一個模組代號，且格式正確");
            if (r is not null && r.Id == m.Id) errors.Add($"manifest.requires.apps[{i}]: 不能依賴自己");
        }

        if (p.Groups.Count > MaxGroups) errors.Add($"groups: 最多 {MaxGroups} 個");
        for (var i = 0; i < p.Groups.Count; i++)
        {
            var g = p.Groups[i];
            if (g is null) { errors.Add($"groups[{i}]: 不能是 null"); continue; }
            if (!Code().IsMatch(g.Code ?? "")) errors.Add($"groups[{i}].code: 格式錯誤");
            CheckText(g.Name, $"groups[{i}].name", errors);
        }

        if (p.Modules.Count == 0) errors.Add("modules: 至少要有一個模組");
        if (p.Modules.Count > MaxModules) errors.Add($"modules: 最多 {MaxModules} 個");
        if (p.FieldAccess.Count > MaxAccessEntries) errors.Add($"fieldAccess: 最多 {MaxAccessEntries} 筆");
        if (p.OptionScopes.Count > MaxAccessEntries) errors.Add($"optionScopes: 最多 {MaxAccessEntries} 筆");

        foreach (var mod in p.Modules.Where(x => x is not null))
        {
            var at = mod.Code ?? "?";
            CheckText(mod.Name, $"{at}.name", errors);
            var fields = mod.Blocks.Where(b => b is not null).SelectMany(b => b.Fields).Where(f => f is not null).ToList();
            if (fields.Count > MaxFieldsPerModule) errors.Add($"{at}: 欄位最多 {MaxFieldsPerModule} 個");
            if (mod.Rules.Count > MaxRulesPerModule) errors.Add($"{at}: 規則最多 {MaxRulesPerModule} 條");
            if (mod.Views.Count > MaxViewsPerModule) errors.Add($"{at}: 檢視最多 {MaxViewsPerModule} 個");
            foreach (var b in mod.Blocks.Where(b => b is not null)) CheckText(b.Label, $"{at}.blocks.{b.Code}.label", errors);
            foreach (var f in fields)
            {
                CheckText(f.Label, $"{at}.{f.Code}.label", errors);
                if (f.Options.Count > MaxOptionsPerField) errors.Add($"{at}.{f.Code}: 選項最多 {MaxOptionsPerField} 個");
                foreach (var o in f.Options.Where(o => o is not null)) CheckText(o.Label, $"{at}.{f.Code}.options.{o.Code}", errors);
                CheckConfigKeys(at, f, p.FormatVersion, errors);
            }
            foreach (var v in mod.Views.Where(v => v is not null)) CheckText(v.Name, $"{at}.views", errors);
        }
        return errors;
    }

    /// <summary>zh-TW 必填、語系鍵合法、每語系 1 到 200 字。v1 的 HasZhTw 不擋不支援的語系鍵，v2 在這一層就擋。</summary>
    private static void CheckText(I18nText? text, string at, List<string> errors)
    {
        if (text is null) { errors.Add($"{at}: 缺少名稱"); return; }
        if (!text.IsValid(out var error)) errors.Add($"{at}: {error}");
        foreach (var (locale, value) in text.Values)
            if (value.Length is 0 or > 200) errors.Add($"{at}: {locale} 必須是 1 到 200 字");
    }

    /// <summary>v1 的 FieldConfig 反序列化會靜默忽略未知鍵，reqired: true 這類拼錯不會被發現；v2 拒絕。</summary>
    private static void CheckConfigKeys(string module, TemplateField f, int formatVersion, List<string> errors)
    {
        var at = $"{module}.{f.Code}";
        if (f.Config.ValueKind != JsonValueKind.Object) return;
        foreach (var prop in f.Config.EnumerateObject())
        {
            if (!ConfigKeys.Contains(prop.Name)) { errors.Add($"{at}: config 不認得 \"{prop.Name}\""); continue; }
            if (prop.Name == "fill")
            {
                if (formatVersion < 3) { errors.Add($"{at}: config.fill 要 formatVersion 3"); continue; }
                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var inner in prop.Value.EnumerateObject().Where(x => !FillKeys.Contains(x.Name)))
                        errors.Add($"{at}: config.fill 不認得 \"{inner.Name}\"");
                    if (prop.Value.TryGetProperty("map", out var map) && map.ValueKind == JsonValueKind.Array)
                        foreach (var entry in map.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object))
                            foreach (var inner in entry.EnumerateObject().Where(x => !FillMapKeys.Contains(x.Name)))
                                errors.Add($"{at}: config.fill.map 不認得 \"{inner.Name}\"");
                }
            }
            if (prop.Name == "refGuard")
            {
                if (formatVersion < 5) { errors.Add($"{at}: config.refGuard 要 formatVersion 5"); continue; }
                if (prop.Value.ValueKind == JsonValueKind.Object)
                    foreach (var inner in prop.Value.EnumerateObject().Where(x => !RefGuardKeys.Contains(x.Name)))
                        errors.Add($"{at}: config.refGuard 不認得 \"{inner.Name}\"");
            }
            if (prop.Name == "refTarget" && prop.Value.ValueKind == JsonValueKind.Object)
                foreach (var inner in prop.Value.EnumerateObject().Where(x => !RefTargetKeys.Contains(x.Name)))
                    errors.Add($"{at}: config.refTarget 不認得 \"{inner.Name}\"");
            if (prop.Name == "rollup" && prop.Value.ValueKind == JsonValueKind.Object)
                foreach (var inner in prop.Value.EnumerateObject().Where(x => !RollupKeys.Contains(x.Name)))
                    errors.Add($"{at}: config.rollup 不認得 \"{inner.Name}\"");
        }
    }

    // ---- 引用層 ----

    private static List<string> References(AppPackageData p, List<string> warnings)
    {
        var errors = new List<string>(TemplateValidator.Validate(AppPackageAdapter.ToTemplate(p)));
        var modules = p.Modules.ToDictionary(m => m.Code, m => m);
        var groupCodes = new HashSet<string>();
        for (var i = 0; i < p.Groups.Count; i++)
            if (!groupCodes.Add(p.Groups[i].Code)) errors.Add($"groups[{i}].code: 群組代號重複 {p.Groups[i].Code}");

        void CheckGroup(string group, string at)
        {
            if (!groupCodes.Contains(group)) errors.Add($"{at}: 群組 {group} 沒有在 groups 宣告");
        }
        foreach (var m in p.Modules)
            foreach (var perm in m.Permissions) CheckGroup(perm.Group, $"{m.Code}.permissions");

        var requiredApps = p.Manifest.Requires.Apps;
        var usedApps = new HashSet<string>();
        var usedEntities = new HashSet<string>();
        var requiresEntities = p.Manifest.Requires.Entities.ToHashSet();

        foreach (var m in p.Modules)
        {
            if (m.Kind == ModuleKind.Extension)
            {
                if (string.IsNullOrWhiteSpace(m.HostEntityKey)) errors.Add($"{m.Code}: 擴充模組必須指定 hostEntityKey");
                else if (!requiresEntities.Contains(m.HostEntityKey)) errors.Add($"{m.Code}: hostEntityKey {m.HostEntityKey} 沒有列在 requires.entities");
                else usedEntities.Add(m.HostEntityKey);
            }
            else if (m.HostEntityKey is not null) errors.Add($"{m.Code}: 只有擴充模組可以有 hostEntityKey");

            foreach (var f in m.Blocks.SelectMany(b => b.Fields).Where(f => f.Type == FieldType.Reference))
            {
                var at = $"{m.Code}.{f.Code}";
                var target = ConfigObject(f.Config, "refTarget");
                if (target is null) { errors.Add($"{at}: 參照欄位缺少 refTarget"); continue; }
                var kind = ConfigString(target.Value, "kind");
                var key = ConfigString(target.Value, "key");
                var app = ConfigString(target.Value, "app");
                if (kind == ReferenceTarget.EntityKind)
                {
                    if (app is not null) errors.Add($"{at}: 既有實體目標不能指定 app");
                    else if (key is null || !requiresEntities.Contains(key)) errors.Add($"{at}: 實體 {key} 沒有列在 requires.entities");
                    else usedEntities.Add(key);
                }
                else if (kind == ReferenceTarget.ModuleKind)
                {
                    if (app is null)
                    {
                        if (key is null || !modules.ContainsKey(key))
                            errors.Add($"{at}: 目標模組 {key} 不在套件內（要引用其他應用程式請加 app 並列入 requires.apps）");
                    }
                    else if (IsDetailReference(f)) errors.Add($"{at}: 明細的表頭必須在本套件內");
                    else
                    {
                        var req = requiredApps.FirstOrDefault(r => r.Id == app);
                        if (req is null || key is null || !req.Modules.Contains(key))
                            errors.Add($"{at}: 另一個應用程式的模組 {app}.{key} 沒有列在 requires.apps");
                        else { usedApps.Add(app); usedApps.Add($"{app}/{key}"); }
                    }
                }
                else errors.Add($"{at}: refTarget.kind 必須是 module 或 entity");
            }
        }

        foreach (var r in requiredApps.Where(r => r is not null))
            foreach (var code in r.Modules.Where(c => !usedApps.Contains($"{r.Id}/{c}")))
                warnings.Add($"requires.apps: 宣告了 {r.Id}.{code}，但套件裡沒有欄位引用它");
        foreach (var e in requiresEntities.Where(e => !usedEntities.Contains(e)))
            warnings.Add($"requires.entities: 宣告了 {e}，但套件裡沒有使用它");

        var fieldNames = AllFields(AppPackageAdapter.ToTemplate(p));
        var optionNames = p.Modules.SelectMany(m => m.Blocks.SelectMany(b => b.Fields).SelectMany(f => f.Options)
            .Select(o => (Name: $"{m.Code}.{o.Code}", Field: (string?)null))).Select(x => x.Name).ToHashSet();

        var seenAccess = new HashSet<string>();
        for (var i = 0; i < p.FieldAccess.Count; i++)
        {
            var a = p.FieldAccess[i];
            var at = $"fieldAccess[{i}]";
            if (a is null) { errors.Add($"{at}: 不能是 null"); continue; }
            if (!fieldNames.ContainsKey(a.Field ?? "")) { errors.Add($"{at}.field: 欄位 {a.Field} 不在套件內"); continue; }
            CheckGroup(a.Group, $"{at}.group");
            if (!Enum.IsDefined(a.Access)) errors.Add($"{at}.access: 必須是 hidden 或 readOnly");
            if (!seenAccess.Add($"{a.Field}|{a.Group}")) errors.Add($"{at}: {a.Field} 對群組 {a.Group} 重複設定");
            var (modCode, fieldCode) = SplitQualified(a.Field)!.Value;
            if (a.Access == FieldAccess.Hidden && modules[modCode].TitleField == fieldCode)
                errors.Add($"{at}: 標題欄位不能是隱藏");
        }

        var seenScopes = new HashSet<string>();
        for (var i = 0; i < p.OptionScopes.Count; i++)
        {
            var s = p.OptionScopes[i];
            var at = $"optionScopes[{i}]";
            if (s is null) { errors.Add($"{at}: 不能是 null"); continue; }
            if (!optionNames.Contains(s.Option ?? "")) errors.Add($"{at}.option: 選項 {s.Option} 不在套件內");
            if (s.Groups.Count == 0) errors.Add($"{at}.groups: 至少要有一個群組");
            foreach (var g in s.Groups) CheckGroup(g, $"{at}.groups");
            if (!seenScopes.Add(s.Option ?? "")) errors.Add($"{at}: {s.Option} 重複設定");
        }
        V3References(p, fieldNames, groupCodes, errors);
        V4References(p, fieldNames, errors);
        PrintTemplateReferences(p, fieldNames, errors);
        return errors;
    }
}
