using System.Globalization;
using System.Text.Json;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.FieldService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Formulas;
using DynamicModules.Domain.Rules;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.RecordService;

/// <summary>規則求值需要的環境；today 取自 IBusinessTime。</summary>
public sealed record RuleContext(DateOnly Today);

/// <summary>把輸入值依欄位型別正規化、套預設值、算公式，再套規則（必填、下拉連動、驗證），並與舊值合併。</summary>
public static class RecordValueProcessor
{
    public const string RequiredMessage = "此欄位必填";
    public const string ReadOnlyRecordMessage = "此記錄目前不可修改";
    public const string OptionOutOfRangeMessage = "選項不在可選範圍";
    /// <summary>整筆記錄層級的錯誤鍵：全單唯讀，或驗證規則的目標欄位使用者都看不到。</summary>
    public const string RecordErrorKey = "_record";
    public const int MaxTitleLength = 200;

    /// <summary>
    /// 順序（已定決策 18）：權限、Config.ReadOnly、規則唯讀（以舊值求值）決定哪些欄位可寫 → 正規化 → 公式 →
    /// 用合併後的值求規則 → 必填、下拉連動、驗證。自動編號不在這裡，由 handler 在全部通過後才取號。
    /// </summary>
    /// <param name="previous">null 表示新增；有值表示修改（沒帶的鍵保留舊值）。</param>
    /// <param name="parentValues">明細列新增時，表頭這次存檔後啟用中欄位的值（Field.Key → 值）；ParentDefaultFieldId 從這裡取。</param>
    /// <param name="systemKeys">值由系統填入（帶值、單據轉換）的欄位鍵：不受欄位唯讀、唯讀權限與規則唯讀限制，仍照型別正規化。</param>
    /// <param name="frozen">這筆記錄已有事件事實（已記帳，設計 §4.7）：除狀態欄位外不能寫，公式不重算、保留記帳當時的值。</param>
    /// <param name="rollupValues">這次存檔在記憶體算好的彙總值（null 值表示移除），在公式之前寫入，公式與規則看得到新值；沒給的彙總欄位保留舊值。</param>
    public static (Dictionary<string, JsonElement?> values, Dictionary<string, string[]> errors) Process(
        Module module,
        IReadOnlyDictionary<string, JsonElement?> input,
        IReadOnlyDictionary<string, JsonElement?>? previous,
        IReadOnlySet<string> userIds,
        FieldTypeRegistry registry,
        EffectivePermissions permissions,
        RuleContext rules,
        IReadOnlyDictionary<long, IReadOnlySet<string>>? validRefKeys = null,
        IReadOnlyDictionary<string, JsonElement?>? rollupValues = null,
        IReadOnlyDictionary<string, JsonElement?>? parentValues = null,
        IReadOnlySet<string>? systemKeys = null,
        bool frozen = false)
    {
        var values = previous is null ? [] : new Dictionary<string, JsonElement?>(previous);
        var errors = new Dictionary<string, string[]>();
        var fields = module.Blocks.SelectMany(b => b.Fields).Where(f => f.Status == FieldStatus.Active).ToList();
        RejectFrozenChanges(fields, input, previous, registry, permissions, userIds, validRefKeys, systemKeys);
        bool IsFrozen(Field f) => permissions.Frozen.ContainsKey(f.Id);

        var stored = previous is null ? null : RuleEngine.Evaluate(module, previous, previous, registry, userIds, rules.Today);
        if (stored is { ReadOnlyAll: true })
        {
            errors[RecordErrorKey] = [ReadOnlyRecordMessage];
            return (values, errors);
        }

        // 無權寫入（權限、Config.ReadOnly、規則唯讀、狀態機唯讀狀態）：忽略輸入、保留舊值、新增只套預設值、不檢查必填
        var userWritable = BuildWritable(permissions, stored);
        bool Writable(Field f) => !FieldRules.IsComputed(f.Type) && !IsWorkflowLocked(module, previous, f, frozen) && !IsFrozen(f)
            && (systemKeys?.Contains(f.Key) == true || userWritable(f));

        foreach (var field in fields.Where(f => !FieldRules.IsComputed(f.Type)))
        {
            JsonElement? raw = null;
            var provided = Writable(field) && input.TryGetValue(field.Key, out raw);
            var fromDefault = false;
            if (!provided && previous is null && !IsFrozen(field) && DynamicDefaultOf(field, rules.Today, parentValues) is { } dflt)
            {
                raw = dflt;
                provided = true;
                fromDefault = true;
            }
            if (!provided) continue;

            JsonElement? prev = previous is not null && previous.TryGetValue(field.Key, out var p) ? p : null;
            var ctx = new FieldTypeContext(
                field.Options.Where(o => o.Active && !permissions.UnselectableOptionIds.Contains(o.Id)).Select(o => o.Id).ToHashSet(),
                field.Options.Select(o => o.Id).ToHashSet(),
                field.Options.ToDictionary(o => o.Id, o => o.Sequence),
                prev, userIds, validRefKeys?.GetValueOrDefault(field.Id));
            var result = registry.Get(field.Type).Normalize(raw, field, ctx);
            if (!result.IsValid)
            {
                // 預設值已不合法（如預設選項被停用）：視同沒有預設值，必填檢查照常
                if (!fromDefault) errors[field.Key] = [result.Error!];
            }
            else if (result.Value is null) values.Remove(field.Key);
            else values[field.Key] = result.Value;
        }
        var normalizeFailed = errors.Count > 0;

        foreach (var (key, value) in rollupValues ?? new Dictionary<string, JsonElement?>())
            if (value is null) values.Remove(key);
            else values[key] = value;
        if (!frozen) ApplyFormulas(module, values, rules.Today, permissions.Frozen);
        var outcome = RuleEngine.Evaluate(module, values, previous, registry, userIds, rules.Today);

        foreach (var field in fields.Where(f => outcome.RequiredFieldIds.Contains(f.Id) && Writable(f)))
            if (!errors.ContainsKey(field.Key) && IsEmpty(values.GetValueOrDefault(field.Key)))
                errors[field.Key] = [RequiredMessage];

        // 無權寫入（含權限隱藏）的欄位不檢查範圍，值不會被這次存檔改動。可選範圍沒變（和舊值求值的結果相同）時，只檢查和舊值不同的選項
        // （multienum 即新加入的），舊的停用／超範圍值不擋存檔；範圍因這次存檔而變了（如上層被改掉），整個值都要重驗。
        foreach (var (fieldId, allowed) in outcome.AllowedOptions)
        {
            var field = fields.FirstOrDefault(f => f.Id == fieldId);
            if (field is null || !Writable(field) || errors.ContainsKey(field.Key)) continue;
            var rangeChanged = stored is null || !stored.AllowedOptions.TryGetValue(fieldId, out var before) || !before.SetEquals(allowed);
            var checkedIds = OptionIdsOf(values.GetValueOrDefault(field.Key));
            if (!rangeChanged) checkedIds = checkedIds.Except(OptionIdsOf(previous!.GetValueOrDefault(field.Key)));
            if (checkedIds.Any(id => !allowed.Contains(id))) errors[field.Key] = [OptionOutOfRangeMessage];
        }

        if (!normalizeFailed)
            foreach (var (targetIds, message) in outcome.FailedValidations)
            {
                var visible = targetIds.Select(id => fields.FirstOrDefault(f => f.Id == id)).FirstOrDefault(f => f is not null && permissions.CanSee(f) && !outcome.HiddenFieldIds.Contains(f.Id));
                var key = visible?.Key ?? RecordErrorKey;
                errors[key] = [.. errors.GetValueOrDefault(key) ?? [], message.Resolve(I18nText.FallbackLocale)];
            }

        return (values, errors);
    }

    /// <summary>
    /// 第一道鎖對原始請求的檢查：要在帶值（ReferenceFill）等改寫輸入的步驟之前呼叫。lock 帶值在參照沒變時會移除目的欄位的輸入，
    /// 只在 Process 裡檢查就看不到「明確給了不同的值」（審查必修 5）；帶值自己寫進去的變更由 Process 再檢查一次。
    /// </summary>
    public static void RejectFrozenInput(
        Module module, IReadOnlyDictionary<string, JsonElement?> input, IReadOnlyDictionary<string, JsonElement?>? previous,
        IReadOnlySet<string> userIds, FieldTypeRegistry registry, EffectivePermissions permissions,
        IReadOnlyDictionary<long, IReadOnlySet<string>>? validRefKeys, IReadOnlySet<string>? systemKeys) =>
        RejectFrozenChanges(module.Blocks.SelectMany(b => b.Fields).Where(f => f.Status == FieldStatus.Active), input, previous,
            registry, permissions, userIds, validRefKeys, systemKeys);

    /// <summary>
    /// 生命週期鎖的第一道（共用主檔設計 §7）：對凍結欄位明確給了和舊值不同的值，整個請求 409；值沒變或沒給就照常通過。
    /// 和狀態機唯讀同層、優先於 systemKeys，所以轉換、事件、整合事件、匯入要寫它時整個動作失敗。
    /// 使用者看不到的欄位不比對：輸入照「沒權限的欄位忽略輸入」處理，回應不因隱藏的舊值而不同（審查必修 1）；系統寫入（systemKeys）照樣檢查。
    /// </summary>
    private static void RejectFrozenChanges(
        IEnumerable<Field> fields, IReadOnlyDictionary<string, JsonElement?> input, IReadOnlyDictionary<string, JsonElement?>? previous,
        FieldTypeRegistry registry, EffectivePermissions permissions, IReadOnlySet<string> userIds,
        IReadOnlyDictionary<long, IReadOnlySet<string>>? validRefKeys, IReadOnlySet<string>? systemKeys)
    {
        if (permissions.Frozen.Count == 0) return;
        foreach (var field in fields)
        {
            // 公式等計算欄位本來就不收輸入（凍結時另由 ApplyFormulas 跳過），不算「給了值」
            if (FieldRules.IsComputed(field.Type) || !permissions.Frozen.TryGetValue(field.Id, out var owner)
                || !input.TryGetValue(field.Key, out var raw)) continue;
            if (!permissions.CanSee(field) && systemKeys?.Contains(field.Key) != true) continue;
            JsonElement? prev = previous is not null && previous.TryGetValue(field.Key, out var p) ? p : null;
            if (Same(raw, prev)) continue;
            var ctx = new FieldTypeContext(field.Options.Select(o => o.Id).ToHashSet(), field.Options.Select(o => o.Id).ToHashSet(),
                field.Options.ToDictionary(o => o.Id, o => o.Sequence), prev, userIds, validRefKeys?.GetValueOrDefault(field.Id));
            var result = registry.Get(field.Type).Normalize(raw, field, ctx);
            if (result.IsValid && Same(result.Value, prev)) continue;
            throw new FrozenFieldException(field, owner, permissions.CanSee(field));
        }
    }

    private static bool Same(JsonElement? a, JsonElement? b)
    {
        static bool Empty(JsonElement? v) => v is null || v.Value.ValueKind == JsonValueKind.Null;
        return Empty(a) ? Empty(b) : !Empty(b) && a!.Value.GetRawText() == b!.Value.GetRawText();
    }

    /// <summary>使用者自己能寫的欄位（不含系統填入）：權限、Config.ReadOnly、規則唯讀、狀態機唯讀狀態。</summary>
    public static Func<Field, bool> WritablePredicate(
        Module module, IReadOnlyDictionary<string, JsonElement?>? previous, EffectivePermissions permissions,
        FieldTypeRegistry registry, IReadOnlySet<string> userIds, DateOnly today)
    {
        var stored = previous is null ? null : RuleEngine.Evaluate(module, previous, previous, registry, userIds, today);
        var writable = BuildWritable(permissions, stored);
        return f => !FieldRules.IsComputed(f.Type) && !IsWorkflowLocked(module, previous, f) && writable(f);
    }

    private static Func<Field, bool> BuildWritable(EffectivePermissions permissions, RuleOutcome? stored) =>
        f => permissions.CanWrite(f) && !f.Config.ReadOnly && stored?.ReadOnlyFieldIds.Contains(f.Id) != true;

    /// <summary>修改一筆處於狀態機唯讀狀態的記錄：除了狀態欄位以外都不能寫。</summary>
    private static bool IsWorkflowLocked(Module module, IReadOnlyDictionary<string, JsonElement?>? previous, Field f, bool frozen = false) =>
        previous is not null && f.Id != module.Workflow?.StatusFieldId && (frozen || module.Workflow is { } wf && wf.IsLocked(previous));

    /// <summary>新增時沒提供值要帶的預設值：DefaultValue、DefaultToday、ParentDefaultFieldId 三者互斥；沒有（或表頭欄位無值）回 null。</summary>
    private static JsonElement? DynamicDefaultOf(Field field, DateOnly today, IReadOnlyDictionary<string, JsonElement?>? parentValues)
    {
        var config = field.Config;
        if (config.DefaultValue is not null) return config.DefaultValue;
        if (config.DefaultToday) return JsonSerializer.SerializeToElement(today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return config.ParentDefaultFieldId is { } id && parentValues?.GetValueOrDefault($"f_{id}") is { } v ? v : null;
    }

    /// <summary>enum 的值是 opt_&lt;id&gt; 字串，multienum 是它們的陣列；其他回空。</summary>
    private static IEnumerable<long> OptionIdsOf(JsonElement? v) => v switch
    {
        { ValueKind: JsonValueKind.String } s => ParseOption(s.GetString()),
        { ValueKind: JsonValueKind.Array } a => a.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).SelectMany(e => ParseOption(e.GetString())),
        _ => []
    };

    private static IEnumerable<long> ParseOption(string? s) =>
        s is not null && s.StartsWith("opt_", StringComparison.Ordinal) && long.TryParse(s.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? [id]
            : [];

    /// <summary>依相依順序算出每個啟用中的公式欄位並寫回 values；結果為 null 就移除鍵。凍結的公式（skip）保持最後的值、不重算。</summary>
    public static void ApplyFormulas(Module module, Dictionary<string, JsonElement?> values, DateOnly today,
        IReadOnlyDictionary<long, FieldLock>? skip = null)
    {
        foreach (var field in FormulaGraph.EvaluationOrder(module))
        {
            if (skip?.ContainsKey(field.Id) == true) continue;
            var node = FormulaParser.Parse(field.Config.Formula, module).Node;
            var scale = field.Config.ResultType == "date" ? null : field.Config.Scale;
            var result = node is null ? null : FormulaEvaluator.Evaluate(node, module, values, today, scale);
            if (result is null) values.Remove(field.Key);
            else values[field.Key] = result;
        }
    }

    private static bool IsEmpty(JsonElement? v) => v is null
        || v.Value.ValueKind == JsonValueKind.Null
        || (v.Value.ValueKind == JsonValueKind.Array && v.Value.GetArrayLength() == 0);

    /// <summary>鍵依 Field.Id 排序（f_&lt;Id&gt;），存檔結果固定。</summary>
    public static string Serialize(IReadOnlyDictionary<string, JsonElement?> values) =>
        JsonSerializer.Serialize(new SortedDictionary<string, JsonElement?>(
            values.ToDictionary(kv => kv.Key, kv => kv.Value), KeyOrder));

    private static readonly Comparer<string> KeyOrder = Comparer<string>.Create((x, y) =>
    {
        var c = IdOf(x).CompareTo(IdOf(y));
        return c != 0 ? c : string.CompareOrdinal(x, y);
    });

    public static Dictionary<string, JsonElement?> Parse(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>(json) ?? [];

    /// <summary>記錄標題：有標題樣板就照樣板組，否則是標題欄位的值；都截到 <see cref="MaxTitleLength"/>。</summary>
    public static string TitleOf(Module module, IReadOnlyDictionary<string, JsonElement?> values)
    {
        if (module.TitleTemplate is not null) return TruncateChars(DynamicModules.Domain.Modules.TitleTemplate.Render(module, values), MaxTitleLength);
        var key = module.Blocks.SelectMany(b => b.Fields).FirstOrDefault(f => f.Id == module.TitleFieldId)?.Key;
        if (key is null || !values.TryGetValue(key, out var v) || v is not { ValueKind: JsonValueKind.String } s)
            return "";
        return TruncateChars(s.GetString()!, MaxTitleLength);
    }

    private static string TruncateChars(string s, int max)
    {
        if (s.Length <= max) return s;
        var cut = char.IsHighSurrogate(s[max - 1]) ? max - 1 : max;
        return s[..cut];
    }

    private static long IdOf(string key) =>
        key.StartsWith("f_", StringComparison.Ordinal) && long.TryParse(key.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : long.MaxValue;
}
