using System.Text.Json;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.TaskRuleService;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using static DynamicModules.Application.Dynamic.TemplateService.TemplateGraph;

namespace DynamicModules.Application.Dynamic.AppService;

// 格式 7 的 taskRules[]（任務管理設計 §5.8）。結構層看形狀與數量；引用層確認模組、欄位、選項都在套件內（或是 requires.masters
// 宣告的主檔符號），並且做只看套件就能判斷的型別檢查。和資料庫合併後的驗證（條件語法、每模組上限）開啟時由 CreateTaskRuleRequest 負責。
public static partial class AppPackageValidator
{
    public const int MaxTaskRules = 50;

    private static readonly string[] TaskTriggerKinds = ["onCreate", "onUpdateMatch", "onFieldBecomes", "manual"];

    private static string TaskRuleAt(AppTaskRule r, int i) => $"taskRules[{r.Code ?? i.ToString()}]";

    // ---- 結構層 ----

    private static void Format7Structure(AppPackageData p, List<string> errors)
    {
        if (p.TaskRules.Count == 0) return;
        // 舊讀取器靜默略過會讓客戶以為有自動派工卻沒有，一律整份拒絕
        if (p.FormatVersion < 7) { errors.Add("taskRules: formatVersion 7 才支援 taskRules"); return; }
        if (p.TaskRules.Count > MaxTaskRules) errors.Add($"taskRules: 最多 {MaxTaskRules} 個");

        var codes = new HashSet<string>();
        for (var i = 0; i < p.TaskRules.Count; i++)
        {
            var r = p.TaskRules[i];
            if (r is null) { errors.Add($"taskRules[{i}]: 不能是 null"); continue; }
            if (!AppId().IsMatch(r.Code ?? "")) errors.Add($"taskRules[{i}].code: 格式錯誤（小寫英文、數字、連字號，2 到 40 字，英文開頭）");
            else if (!codes.Add(r.Code!)) errors.Add($"taskRules[{i}].code: 任務規則代號重複 {r.Code}");
            var at = TaskRuleAt(r, i);
            CheckText(r.Name, $"{at}.name", errors);
            if (r.Repeat is not (null or AppTaskRule.Once or AppTaskRule.EveryTime)) errors.Add($"{at}.repeat: 必須是 once 或 everyTime");
            if (r.Trigger is null) { errors.Add($"{at}.trigger: 缺少觸發設定"); continue; }
            if (!TaskTriggerKinds.Contains(r.Trigger.On)) errors.Add($"{at}.trigger.on: {TaskRuleMessages.BadTriggerOn}");
            if (r.Trigger.On == "onCreate" && r.Repeat == AppTaskRule.EveryTime) errors.Add($"{at}.repeat: onCreate 只能 once");
            if (r.Trigger.On == "onUpdateMatch" && IsEmpty(r.When)) errors.Add($"{at}.when: {TaskRuleMessages.UpdateMatchNeedsWhen}");
            if (r.Trigger.On == "onFieldBecomes" && r.Trigger.Field is null) errors.Add($"{at}.trigger.field: {TaskRuleMessages.TriggerFieldKind}");
            if (r.Trigger.On != "onFieldBecomes" && (r.Trigger.Field is not null || !IsEmpty(r.Trigger.Value)))
                errors.Add($"{at}.trigger: 只有 onFieldBecomes 可以指定 field 與 value");

            if (r.Tasks.Count is 0 or > TaskRuleValidator.MaxTasks) errors.Add($"{at}.tasks: {TaskRuleMessages.TasksCount}");
            var taskCodes = new HashSet<string>();
            for (var j = 0; j < r.Tasks.Count; j++)
            {
                var t = r.Tasks[j];
                if (t is null) { errors.Add($"{at}.tasks[{j}]: 不能是 null"); continue; }
                var tat = $"{at}.tasks[{t.Code ?? j.ToString()}]";
                if (!Code().IsMatch(t.Code ?? "")) errors.Add($"{tat}.code: {TaskRuleMessages.BadTaskCode}");
                else if (!taskCodes.Add(t.Code!)) errors.Add($"{tat}.code: {TaskRuleMessages.DuplicateTaskCode}");
                CheckText(t.Title, $"{tat}.title", errors);
                if (t.Description is not null) CheckDescription(t.Description, $"{tat}.description", errors);
                if (t.Assignee is null) errors.Add($"{tat}.assignee: {TaskRuleMessages.AssigneeField}");
                if (t.Due is { } due && (due.Field is null || due.OffsetDays is < -TaskRuleValidator.MaxOffsetDays or > TaskRuleValidator.MaxOffsetDays))
                    errors.Add($"{tat}.due: {(due.Field is null ? TaskRuleMessages.DueField : TaskRuleMessages.DueOffsetRange)}");
            }
        }
    }

    private static bool IsEmpty(JsonElement? value) => value is null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined };

    /// <summary>說明和設計器一樣上限 4000 字（CheckText 的 200 字是名稱用的，套用在說明上會讓設計器存得下的規則匯出後讀不回來）。</summary>
    private static void CheckDescription(I18nText text, string at, List<string> errors)
    {
        if (!text.IsValid(out var error)) { errors.Add($"{at}: {error}"); return; }
        foreach (var (locale, value) in text.Values)
            if (value is null || value.Length > TaskEditParser.DescriptionMax) errors.Add($"{at}: {locale} 必須是 0 到 {TaskEditParser.DescriptionMax} 字");
    }

    // ---- 引用層 ----

    private static void TaskRuleReferences(AppPackageData p, Dictionary<string, TemplateField> fields, MasterScope scope, List<string> errors)
    {
        if (p.TaskRules.Count == 0) return;
        var modules = p.Modules.ToDictionary(m => m.Code, m => m);
        var optionNames = p.Modules.SelectMany(m => m.Blocks.SelectMany(b => b.Fields).SelectMany(f => f.Options.Select(o => $"{m.Code}.{o.Code}")))
            .ToHashSet();
        for (var i = 0; i < p.TaskRules.Count; i++)
        {
            var r = p.TaskRules[i];
            var at = TaskRuleAt(r, i);
            var masterErrors = new List<string>();
            var lookups = MasterLookups(scope, masterErrors,
                code => modules.ContainsKey(code) ? code : null,
                name => fields.ContainsKey(name) ? 1 : null,
                name => optionNames.Contains(name) ? 1 : null,
                AppReportCodec.TypesOf(p.Modules));

            // 模組：本套件的獨立模組，或 requires.masters 宣告的主檔模組
            var module = r.Module ?? "";
            if (MasterScope.Parse(module) is { } symbol)
            {
                if (symbol.Tail is not null) { errors.Add($"{at}.module: {module} 不是主檔模組符號（<主檔 id>/<模組代號>）"); continue; }
                if (scope.ModuleError(symbol.App, symbol.Module) is { } prefix) { errors.Add($"{at}.module: {prefix}"); continue; }
            }
            else if (!modules.TryGetValue(module, out var local)) { errors.Add($"{at}.module: 模組 {module} 不在套件內"); continue; }
            else if (FieldsOf(local).Any(IsDetailReference)) { errors.Add($"{at}.module: {TaskRuleMessages.DetailModule}"); continue; }

            // 欄位：必須是規則所在模組的欄位；主檔定義未知時型別回 null，只檢查前綴
            (bool Found, FieldType? Type, bool Active) Field(string name)
            {
                if (lookups.FieldId(name) is null) return (false, null, false);
                if (MasterScope.Parse(name) is not null)
                    return scope.Field(name).Field is { } mf ? (true, mf.Type, mf.Active != false) : (true, null, true);
                var f = fields[name];
                return (true, f.Type, f.Active != false);
            }
            bool Check(string? name, string key, Func<FieldType, bool> typeOk, string typeMessage)
            {
                if (name is null) return false;
                if (SplitQualified(name)?.Module != module) { errors.Add($"{at}{key}: {name} 必須是模組 {module} 的欄位"); return false; }
                var (found, type, active) = Field(name);
                if (!found) { errors.Add($"{at}: 引用了套件沒有的欄位 {name}"); return false; }
                if (type is { } ft && (!typeOk(ft) || !active)) { errors.Add($"{at}{key}: {typeMessage}"); return false; }
                return true;
            }

            if (r.Trigger is { On: "onFieldBecomes", Field: { } triggerField } trigger
                && Check(triggerField, ".trigger.field", t => t is FieldType.Enum or FieldType.Boolean, TaskRuleMessages.TriggerFieldKind))
            {
                var type = Field(triggerField).Type;
                var value = trigger.Value;
                var ok = type switch
                {
                    FieldType.Boolean => value is { ValueKind: JsonValueKind.True or JsonValueKind.False },
                    _ => value is { ValueKind: JsonValueKind.String } v && OptionCodeOf(v.GetString()) is { } option
                        && (type is null || lookups.OptionId($"{module}.{option}") is not null)
                };
                if (!ok) errors.Add($"{at}.trigger.value: {TaskRuleMessages.TriggerValueInvalid}（{value?.GetRawText() ?? "null"}）");
            }
            foreach (var t in r.Tasks.Where(t => t is not null))
            {
                var tat = $".tasks[{t.Code}]";
                Check(t.Assignee, $"{tat}.assignee", ft => ft == FieldType.User, TaskRuleMessages.AssigneeField);
                if (t.Due is { Field: { } due }) Check(due, $"{tat}.due", ft => ft is FieldType.Date or FieldType.DateTime, TaskRuleMessages.DueField);
                if (!IsEmpty(t.When))
                    foreach (var error in ConditionErrors(t.When!.Value, lookups)) errors.Add($"{at}{tat}.when: {error}");
            }
            if (!IsEmpty(r.When))
                foreach (var error in ConditionErrors(r.When!.Value, lookups)) errors.Add($"{at}.when: {error}");
            foreach (var error in masterErrors.Distinct()) errors.Add($"{at}: {error}");
        }
    }

    /// <summary>opt_&lt;選項代號&gt; 的代號；不是這個形狀回 null。</summary>
    internal static string? OptionCodeOf(string? value) =>
        value is { Length: > 4 } s && s.StartsWith("opt_", StringComparison.Ordinal) ? s[4..] : null;

    private static List<string> ConditionErrors(JsonElement when, AppReportCodec.Lookups lookups)
    {
        var errors = new List<string>();
        AppEffectsCodec.ImportCondition(when, lookups, errors);
        return errors.Distinct().ToList();
    }
}
