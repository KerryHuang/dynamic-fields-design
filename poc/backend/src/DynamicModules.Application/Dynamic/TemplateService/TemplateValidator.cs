using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.ValueObjects;
using static DynamicModules.Application.Dynamic.TemplateService.TemplateGraph;

namespace DynamicModules.Application.Dynamic.TemplateService;

/// <summary>
/// 第 8 階段決策 7：範本的結構驗證（純函式，啟動與匯入共用）。只檢查格式、代號與引用；
/// 型別設定是否合法留給套用時的既有指令。錯誤格式「{模組代號}.{欄位代號}: {原因}」，無欄位時省略。
/// </summary>
public static partial class TemplateValidator
{
    [GeneratedRegex(ModuleRules.ModuleKeyPattern)]
    private static partial Regex ModuleKey();

    // 決策 1 寫 ^[a-z][a-z0-9_]{0,39}$，但決策 9 的內建範本用 camelCase（orderDate、taxRate）；放寬為英文小寫開頭、其後可大寫
    [GeneratedRegex("^[a-z][a-zA-Z0-9_]{0,39}$")]
    private static partial Regex Code();

    [GeneratedRegex("^[a-z][a-z0-9_-]{0,39}$")]
    private static partial Regex OptionCode();

    public static IReadOnlyList<string> Validate(TemplatePackage p)
    {
        var errors = new List<string>();
        if (HasNullEntries(p))
        {
            errors.Add("範本格式錯誤：清單裡不能有 null");
            return errors;
        }

        if (p.FormatVersion != 1) errors.Add($"formatVersion 必須是 1（目前是 {p.FormatVersion}）");
        if (!ModuleKey().IsMatch(p.Key ?? "")) errors.Add($"範本代號 {p.Key} 格式錯誤（小寫英文、數字、連字號，2 到 40 字，英文開頭）");
        if (!HasZhTw(p.Name)) errors.Add("範本名稱必須有繁體中文");
        if (p.Modules.Count == 0) errors.Add("範本至少要有一個模組");

        var modules = new Dictionary<string, TemplateModule>();
        var keys = new HashSet<string>();
        foreach (var m in p.Modules)
        {
            if (!ModuleKey().IsMatch(m.Code ?? "")) errors.Add($"{m.Code}: 模組代號格式錯誤");
            else if (!modules.TryAdd(m.Code!, m)) errors.Add($"{m.Code}: 模組代號重複");
            if (!ModuleKey().IsMatch(m.Key ?? "")) errors.Add($"{m.Code}: 模組代碼 {m.Key} 格式錯誤");
            else if (!keys.Add(m.Key!)) errors.Add($"{m.Code}: 模組代碼重複 {m.Key}");
        }

        foreach (var m in p.Modules) ValidateModule(m, modules, errors);

        var graph = Build(p);
        if (graph.HasCycle)
        {
            var sorted = graph.ComputedOrder().ToHashSet();
            var cycle = AllFields(p).Where(kv => kv.Value.Type is FieldType.Formula or FieldType.Rollup && !sorted.Contains(kv.Key))
                .Select(kv => kv.Key);
            errors.Add($"計算欄位有循環依賴：{string.Join("、", cycle)}");
        }
        return errors;
    }

    /// <summary>標題樣板的 {} 內是本模組的欄位代號，型別見 <see cref="TitleTemplate.AllowedTypes"/>。</summary>
    private static void ValidateTitleTemplate(TemplateModule m, string template, Dictionary<string, TemplateField> fields, List<string> errors)
    {
        var mc = m.Code;
        if (m.Kind == ModuleKind.Extension) { errors.Add($"{mc}: 擴充模組的標題取自既有資料，不能設標題樣板"); return; }
        var (parts, error) = TitleTemplate.Parse(template);
        if (error is not null) { errors.Add($"{mc}: {error}"); return; }
        foreach (var code in parts.Where(p => p.Token is not null).Select(p => p.Token!).Distinct())
        {
            if (!fields.TryGetValue(code, out var f)) errors.Add($"{mc}: 標題樣板引用了不存在的欄位 {code}");
            else if (!TitleTemplate.AllowedTypes.Contains(f.Type)) errors.Add($"{mc}: 標題樣板不能引用 {code}（{TitleTemplate.AllowedTypesText}）");
        }
    }

    private static void ValidateModule(TemplateModule m, Dictionary<string, TemplateModule> modules, List<string> errors)
    {
        var mc = m.Code;
        if (!HasZhTw(m.Name)) errors.Add($"{mc}: 名稱必須有繁體中文");
        if (!Enum.IsDefined(m.Kind)) errors.Add($"{mc}: 模組種類不正確");
        if (m.Blocks.Count == 0) errors.Add($"{mc}: 至少要有一個區塊");

        var blockCodes = new HashSet<string>();
        var fields = new Dictionary<string, TemplateField>();
        var optionCodes = new HashSet<string>();
        foreach (var b in m.Blocks)
        {
            if (!Code().IsMatch(b.Code ?? "")) errors.Add($"{mc}: 區塊代號 {b.Code} 格式錯誤");
            else if (!blockCodes.Add(b.Code!)) errors.Add($"{mc}: 區塊代號重複 {b.Code}");
            if (!HasZhTw(b.Label)) errors.Add($"{mc}: 區塊 {b.Code} 的名稱必須有繁體中文");
            if (b.Columns is < 1 or > 3) errors.Add($"{mc}: 區塊 {b.Code} 的每列欄數必須是 1 到 3");

            foreach (var f in b.Fields)
            {
                var at = $"{mc}.{f.Code}";
                if (!Code().IsMatch(f.Code ?? "")) errors.Add($"{at}: 欄位代號格式錯誤");
                else if (!fields.TryAdd(f.Code!, f)) errors.Add($"{at}: 欄位代號重複");
                if (!Enum.IsDefined(f.Type)) errors.Add($"{at}: 欄位型別不正確");
                if (!HasZhTw(f.Label)) errors.Add($"{at}: 標籤必須有繁體中文");
                if (f.Config.ValueKind != JsonValueKind.Object) errors.Add($"{at}: 設定必須是物件");
                foreach (var o in f.Options)
                {
                    if (!OptionCode().IsMatch(o.Code ?? "")) errors.Add($"{at}: 選項代號 {o.Code} 格式錯誤");
                    else if (!optionCodes.Add(o.Code!)) errors.Add($"{at}: 選項代號重複 {o.Code}");
                    if (!HasZhTw(o.Label)) errors.Add($"{at}: 選項 {o.Code} 的名稱必須有繁體中文");
                }
            }
        }

        if (m.TitleField is null)
        {
            // 擴充模組的標題取自既有資料，沒有自己的標題欄位
            if (m.Kind != ModuleKind.Extension) errors.Add($"{mc}: 標題欄位 {m.TitleField} 不存在");
        }
        else if (!fields.TryGetValue(m.TitleField, out var title))
            errors.Add($"{mc}: 標題欄位 {m.TitleField} 不存在");
        else if (title.Type is not (FieldType.Text or FieldType.AutoNumber))
            errors.Add($"{mc}: 標題欄位 {m.TitleField} 必須是 text 或 autonumber");
        if (m.TitleTemplate is { } template) ValidateTitleTemplate(m, template, fields, errors);

        foreach (var f in m.Blocks.SelectMany(b => b.Fields))
            ValidateFieldConfig(m, f, fields, modules, errors);

        for (var i = 0; i < m.Rules.Count; i++)
            ValidateRule(m, m.Rules[i], $"{mc}: 規則 {i + 1}", fields, blockCodes, errors);

        foreach (var v in m.Views)
        {
            var at = $"{mc}: 檢視「{v.Name?.Resolve(I18nText.FallbackLocale)}」";
            if (!HasZhTw(v.Name)) errors.Add($"{at}的名稱必須有繁體中文");
            foreach (var c in v.Columns.Where(c => !fields.ContainsKey(c)))
                errors.Add($"{at}的欄 {c} 不存在");
            foreach (var s in v.Sort)
            {
                if (s.Field is null || !fields.ContainsKey(s.Field)) errors.Add($"{at}的排序欄位 {s.Field} 不存在");
                if (s.Order is not ("asc" or "desc")) errors.Add($"{at}的排序方向必須是 asc 或 desc");
            }
            if (v.Filter is { } filter) ValidateCondition(filter, fields, $"{at}的篩選", errors);
        }

        var isDetail = m.Blocks.SelectMany(b => b.Fields).Any(IsDetailReference);
        if (isDetail && m.Permissions.Count > 0) errors.Add($"{mc}: 明細模組不能設定權限（沿用表頭）");
        foreach (var perm in m.Permissions)
        {
            if (string.IsNullOrWhiteSpace(perm.Group)) errors.Add($"{mc}: 權限的群組代碼不能空白");
            if (perm.Privileges is null) errors.Add($"{mc}: 權限 {perm.Group} 缺少 privileges");
            if (perm.Scope is { } scope && !Enum.IsDefined(scope)) errors.Add($"{mc}: 權限 {perm.Group} 的資料範圍不正確");
        }
    }

    private static void ValidateFieldConfig(
        TemplateModule m, TemplateField f, Dictionary<string, TemplateField> fields,
        Dictionary<string, TemplateModule> modules, List<string> errors)
    {
        var at = $"{m.Code}.{f.Code}";
        var config = f.Config;
        if (config.ValueKind != JsonValueKind.Object) return;

        if (f.Type != FieldType.Formula && HasValue(config, "formula")) errors.Add($"{at}: 只有公式欄位可以設定公式");
        if (f.Type != FieldType.Rollup && HasValue(config, "rollup")) errors.Add($"{at}: 只有彙總欄位可以設定彙總");

        if (FormulaOf(f) is { } formula)
            foreach (var code in FormulaCodes(formula).Where(c => !fields.ContainsKey(c)).Distinct())
                errors.Add($"{at}: 公式引用了不存在的欄位 {code}");

        if (config.TryGetProperty("defaultValue", out var dv))
            foreach (var code in OptionRefs(f, dv).Where(c => f.Options.All(o => o.Code != c)))
                errors.Add($"{at}: 預設值引用了不存在的選項 opt_{code}");

        if (RollupOf(f) is { } rollup)
        {
            var sourceName = ConfigString(rollup, "sourceField");
            var source = Resolve(sourceName, modules);
            if (source is null) errors.Add($"{at}: 彙總來源欄位 {sourceName} 不存在");
            else if (source.Value.Field.Type != FieldType.Reference) errors.Add($"{at}: 彙總來源欄位 {sourceName} 不是參照欄位");

            var aggregateName = ConfigString(rollup, "aggregateField");
            if (aggregateName is not null)
            {
                var aggregate = Resolve(aggregateName, modules);
                if (aggregate is null) errors.Add($"{at}: 彙總對象欄位 {aggregateName} 不存在");
                else if (source is not null && aggregate.Value.Module != source.Value.Module)
                    errors.Add($"{at}: 彙總對象欄位 {aggregateName} 必須在來源模組");
            }

            if (source is not null && ConfigObject(rollup, "condition") is { } condition)
                ValidateCondition(condition, FieldsOf(source.Value.Module), $"{at}: 彙總條件", errors);
        }

        if (ConfigString(config, "parentDefaultField") is { } parent && Resolve(parent, modules) is null)
            errors.Add($"{at}: 表頭欄位 {parent} 不存在");
    }

    private static void ValidateRule(
        TemplateModule m, TemplateRule rule, string at, Dictionary<string, TemplateField> fields,
        HashSet<string> blockCodes, List<string> errors)
    {
        if (!Enum.IsDefined(rule.Kind)) errors.Add($"{at}的種類不正確");
        if (rule.Targets.Count == 0) errors.Add($"{at}沒有目標");
        var targetFields = new List<TemplateField>();
        foreach (var t in rule.Targets)
        {
            if (t == "*") continue;
            if (t.StartsWith("field:") && fields.TryGetValue(t[6..], out var tf)) targetFields.Add(tf);
            else if (!(t.StartsWith("block:") && blockCodes.Contains(t[6..]))) errors.Add($"{at}的目標 {t} 不存在");
        }

        // 選項限制：選項要屬於目標欄位；沒有欄位目標時退回整個模組
        var allowed = (targetFields.Count > 0 ? targetFields : m.Blocks.SelectMany(b => b.Fields))
            .SelectMany(f => f.Options).Select(o => o.Code).ToHashSet();
        foreach (var o in rule.Options.Where(o => !allowed.Contains(o)))
            errors.Add($"{at}的選項 {o} 不存在");

        if (rule.Condition.ValueKind != JsonValueKind.Undefined)
            ValidateCondition(rule.Condition, fields, $"{at}的條件", errors);
    }

    /// <summary>條件裡的 field、fieldRef 是 fields 的代號；opt_ 值要屬於該條件欄位的選項。</summary>
    private static void ValidateCondition(JsonElement condition, Dictionary<string, TemplateField> fields, string at, List<string> errors)
    {
        if (condition.ValueKind == JsonValueKind.Null) return;
        if (condition.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{at}格式錯誤");
            return;
        }
        foreach (var leaf in ConditionLeaves(condition))
        {
            var code = LeafField(leaf);
            if (code is null || !fields.TryGetValue(code, out var field))
            {
                errors.Add($"{at}引用了不存在的欄位 {code}");
                continue;
            }
            if (LeafFieldRef(leaf) is { } r && !fields.ContainsKey(r))
                errors.Add($"{at}引用了不存在的欄位 {r}");
            if (leaf.TryGetProperty("value", out var value))
                foreach (var o in OptionRefs(field, value).Where(o => field.Options.All(x => x.Code != o)))
                    errors.Add($"{at}引用了欄位 {code} 不存在的選項 opt_{o}");
        }
    }

    /// <summary>值（或陣列元素）裡 opt_&lt;代號&gt; 的選項代號；只有 enum、multienum 的值才是選項引用，其他型別的 "opt_…" 是一般文字。</summary>
    private static IEnumerable<string> OptionRefs(TemplateField field, JsonElement value)
    {
        if (!IsOptionField(field)) return [];
        var items = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToList() : [value];
        return items.Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => OptionReference().Match(e.GetString()!)).Where(m => m.Success).Select(m => m.Groups[1].Value);
    }

    private static bool HasValue(JsonElement config, string name) =>
        config.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;

    private static (TemplateModule Module, TemplateField Field)? Resolve(string? qualified, Dictionary<string, TemplateModule> modules)
    {
        if (SplitQualified(qualified) is not { } q || !modules.TryGetValue(q.Module, out var module)) return null;
        var field = module.Blocks.SelectMany(b => b.Fields).FirstOrDefault(f => f.Code == q.Field);
        return field is null ? null : (module, field);
    }

    private static Dictionary<string, TemplateField> FieldsOf(TemplateModule module)
    {
        var fields = new Dictionary<string, TemplateField>();
        foreach (var f in module.Blocks.SelectMany(b => b.Fields).Where(f => f.Code is not null)) fields.TryAdd(f.Code, f);
        return fields;
    }

    private static bool HasZhTw(I18nText? text) =>
        !string.IsNullOrWhiteSpace(text?.Values.GetValueOrDefault(I18nText.FallbackLocale));

    private static bool HasNullEntries(TemplatePackage p) =>
        p.Modules.Any(m => m is null
            || m.Blocks.Any(b => b is null || b.Fields.Any(f => f is null || f.Options.Any(o => o is null)))
            || m.Rules.Any(r => r is null || r.Targets.Any(t => t is null) || r.Options.Any(o => o is null))
            || m.Views.Any(v => v is null || v.Columns.Any(c => c is null) || v.Sort.Any(s => s is null))
            || m.Permissions.Any(x => x is null));
}
