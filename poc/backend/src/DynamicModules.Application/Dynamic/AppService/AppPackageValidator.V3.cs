using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using static DynamicModules.Application.Dynamic.TemplateService.TemplateGraph;

namespace DynamicModules.Application.Dynamic.AppService;

// 格式 3 的引用層：workflows、conversions、reports、config.fill 的代號都要在套件內找得到。
// 這裡只做「存在」的檢查（純函式）；欄位型別、群組權限這類要看資料庫的檢查，開啟時由既有的設計器指令負責，失敗整批回滾。
public static partial class AppPackageValidator
{
    private static void V3References(
        AppPackageData p, Dictionary<string, TemplateField> fields, HashSet<string> groupCodes, MasterScope scope, List<string> errors)
    {
        var modules = p.Modules.ToDictionary(m => m.Code, m => m);
        // 「模組代號.選項代號」→ 擁有它的欄位限定名（選項代號在模組內唯一）
        var optionOwner = p.Modules.SelectMany(m => m.Blocks.SelectMany(b => b.Fields)
                .SelectMany(f => f.Options.Select(o => (Key: $"{m.Code}.{o.Code}", Owner: Qualify(m.Code, f.Code)))))
            .DistinctBy(x => x.Key).ToDictionary(x => x.Key, x => x.Owner);

        bool Field(string? name, string? inModule = null) =>
            name is not null && fields.ContainsKey(name) && (inModule is null || SplitQualified(name)!.Value.Module == inModule);

        // 轉換目標可以是 requires.masters 的主檔模組（§4.5）：目標上的欄位寫主檔符號，解析交給 MasterScope
        string? MasterModule(string? symbol) =>
            MasterScope.Parse(symbol) is { Tail: null } s && scope.Declares(s.App) ? symbol : null;
        // Error 是主檔符號解析失敗的原因（例如不是基本欄位也不是本套件貢獻的欄位）；一般欄位找不到時是 null，由呼叫端給訊息
        (bool Ok, TemplateField? Def, string? Error) TargetField(string? name, string target)
        {
            if (!target.Contains('/')) return (Field(name, target), name is not null && fields.TryGetValue(name, out var f) ? f : null, null);
            if (MasterScope.Parse(name) is not { Tail: not null } s || $"{s.App}/{s.Module}" != target) return (false, null, null);
            var (def, error) = scope.Field(name!);
            return (error is null, def, error);
        }
        string Why(string? error, string fallback) => error ?? fallback;

        // ---- workflows ----
        var workflowOf = new Dictionary<string, AppWorkflow>();
        for (var i = 0; i < p.Workflows.Count; i++)
        {
            var w = p.Workflows[i];
            var at = $"workflows[{i}]";
            if (w is null) { errors.Add($"{at}: 不能是 null"); continue; }
            if (w.Module is null || !modules.TryGetValue(w.Module, out var module)) { errors.Add($"{at}.module: 模組 {w.Module} 不在套件內"); continue; }
            if (!workflowOf.TryAdd(w.Module, w)) { errors.Add($"{at}: 模組 {w.Module} 已有狀態機"); continue; }
            var status = Qualify(w.Module, w.StatusField);
            if (!Field(status) || fields[status].Type != FieldType.Enum) { errors.Add($"{at}.statusField: {status} 必須是本模組的單選下拉欄位"); continue; }
            var options = fields[status].Options.Select(o => o.Code).ToHashSet();
            if (!options.Contains(w.Initial ?? "")) errors.Add($"{at}.initial: 選項 {w.Initial} 不是 {status} 的選項");
            var states = w.States.Select(s => s?.Option ?? "").ToList();
            foreach (var s in states.Where(s => !options.Contains(s))) errors.Add($"{at}.states: 選項 {s} 不是 {status} 的選項");
            if (states.Distinct().Count() != states.Count) errors.Add($"{at}.states: 狀態重複");
            if (!states.Contains(w.Initial ?? "")) errors.Add($"{at}.initial: 起始狀態必須在 states 內");
            foreach (var t in w.Transitions)
            {
                if (t is null) { errors.Add($"{at}.transitions: 不能有 null"); continue; }
                if (!states.Contains(t.From ?? "") || !states.Contains(t.To ?? "") || t.From == t.To)
                    errors.Add($"{at}.transitions: {t.From} → {t.To} 的兩端必須是 states 內不同的狀態");
                foreach (var g in t.Groups)
                    if (!groupCodes.Contains(g)) errors.Add($"{at}.transitions: 群組 {g} 沒有在 groups 宣告");
            }
            _ = module;
        }

        // ---- conversions ----
        var conversionCodes = new HashSet<string>();
        for (var i = 0; i < p.Conversions.Count; i++)
        {
            var c = p.Conversions[i];
            var at = $"conversions[{i}]";
            if (c is null) { errors.Add($"{at}: 不能是 null"); continue; }
            if (!AppId().IsMatch(c.Code ?? "")) errors.Add($"{at}.code: 格式錯誤（小寫英文、數字、連字號，2 到 40 字，英文開頭）");
            else if (!conversionCodes.Add(c.Code!)) errors.Add($"{at}.code: 轉換代碼重複 {c.Code}");
            CheckText(c.Name, $"{at}.name", errors);
            if (!modules.ContainsKey(c.Source ?? "")) { errors.Add($"{at}: 來源或目標模組不在套件內"); continue; }
            if (MasterModule(c.Target) is { } masterTarget)
            {
                var sp = MasterScope.Parse(masterTarget)!.Value;
                if (scope.ModuleError(sp.App, sp.Module) is { } masterError) { errors.Add($"{at}.target: {masterError}"); continue; }
            }
            else if (!modules.ContainsKey(c.Target ?? "")) { errors.Add($"{at}: 來源或目標模組不在套件內"); continue; }
            if (c.SourceStates.Count > 0)
            {
                if (!workflowOf.TryGetValue(c.Source!, out var wf)) errors.Add($"{at}.sourceStates: 來源模組 {c.Source} 沒有狀態機");
                else
                {
                    var owner = Qualify(c.Source, wf.StatusField);
                    foreach (var s in c.SourceStates.Where(s => !wf.States.Any(x => x.Option == s)))
                        errors.Add($"{at}.sourceStates: {s} 不是 {c.Source} 狀態機裡的狀態 (欄位 {owner})");
                }
            }
            if (c.Header is null) { errors.Add($"{at}.header: 缺少表頭設定"); continue; }
            var source = TargetField(c.Header.SourceField, c.Target!);
            if (!source.Ok) errors.Add($"{at}.header.sourceField: {Why(source.Error, $"{c.Header.SourceField} 必須是目標模組 {c.Target} 的欄位")}");
            CheckCopies(c.Header.Copy, c.Source, c.Target, $"{at}.header.copy",
                (name, module) => module == c.Target ? TargetField(name, c.Target!).Ok : Field(name, module), errors);
            foreach (var set in c.Header.Set ?? [])
            {
                if (set is null) { errors.Add($"{at}.header.set: 不能有 null"); continue; }
                var (ok, def, why) = TargetField(set.To, c.Target!);
                if (!ok) errors.Add($"{at}.header.set: {Why(why, $"欄位 {set.To} 必須是目標模組 {c.Target} 的欄位")}");
                else if (def is not null && def.Type != FieldType.Enum) errors.Add($"{at}.header.set: 固定值只能寫入下拉欄位，{set.To} 不是");
                else if (def is not null && def.Options.All(o => o.Code != set.Option)) errors.Add($"{at}.header.set: {set.Option} 不是 {set.To} 的選項代號");
            }
            if (c.Lines is { } lines)
            {
                if (!modules.ContainsKey(lines.SourceModule ?? "") || !modules.ContainsKey(lines.TargetModule ?? ""))
                    errors.Add($"{at}.lines: 來源或目標明細模組不在套件內");
                else
                {
                    if (!Field(lines.SourceLineField, lines.TargetModule))
                        errors.Add($"{at}.lines.sourceLineField: {lines.SourceLineField} 必須是目標明細模組 {lines.TargetModule} 的欄位");
                    if (lines.Quantity is null || !Field(lines.Quantity.From, lines.SourceModule) || !Field(lines.Quantity.To, lines.TargetModule))
                        errors.Add($"{at}.lines.quantity: 數量欄位必須分別是來源與目標明細模組的欄位");
                    CheckCopies(lines.Copy, lines.SourceModule, lines.TargetModule, $"{at}.lines.copy", Field, errors);
                }
            }
        }

        // ---- config.fill ----
        foreach (var m in p.Modules)
            foreach (var f in m.Blocks.SelectMany(b => b.Fields).Where(f => f.Type == FieldType.Reference))
            {
                if (ConfigObject(f.Config, "fill") is not { } fill) continue;
                var at = $"{m.Code}.{f.Code}.fill";
                var mode = ConfigString(fill, "mode");
                if (mode is not ("lock" or "default")) errors.Add($"{at}.mode: 必須是 lock 或 default");
                var rtApp = ConfigObject(f.Config, "refTarget") is { } rt0 ? ConfigString(rt0, "app") : null;
                var target = ConfigObject(f.Config, "refTarget") is { } rt && ConfigString(rt, "kind") == "module" ? ConfigString(rt, "key") : null;
                if (rtApp is not null && scope.Declares(rtApp) && target is not null)
                    target = $"{rtApp}/{target}";   // 主檔模組：來源欄位寫主檔符號
                else if (rtApp is not null || target is null || !modules.ContainsKey(target))
                {
                    errors.Add($"{at}: 帶值只能用在指向本套件模組或 requires.masters 主檔模組的參照欄位");
                    continue;
                }
                if (!fill.TryGetProperty("map", out var map) || map.ValueKind != System.Text.Json.JsonValueKind.Array || map.GetArrayLength() == 0)
                {
                    errors.Add($"{at}.map: 至少要有一組對應");
                    continue;
                }
                foreach (var entry in map.EnumerateArray())
                {
                    var from = ConfigString(entry, "from");
                    var to = ConfigString(entry, "to");
                    var resolved = TargetField(from, target);
                    if (!resolved.Ok) errors.Add($"{at}.map: 來源欄位 {Why(resolved.Error, $"{from} 必須是 {target} 的欄位（<模組代號>.<欄位代號>）")}");
                    if (!Field(Qualify(m.Code, to))) errors.Add($"{at}.map: 目的欄位 {to} 不在 {m.Code}");
                }
            }

        // ---- config.refGuard ----
        foreach (var m in p.Modules)
            foreach (var f in m.Blocks.SelectMany(b => b.Fields).Where(f => ConfigObject(f.Config, "refGuard") is not null))
            {
                var at = $"{m.Code}.{f.Code}.refGuard";
                var guard = ConfigObject(f.Config, "refGuard")!.Value;
                var target = f.Type == FieldType.Reference && ConfigObject(f.Config, "refTarget") is { } rt && ConfigString(rt, "kind") == "module"
                    ? ConfigString(rt, "key") : null;
                if (target is null) { errors.Add($"{at}: 目標狀態守衛只能用在指向動態模組的參照欄位"); continue; }
                var statusField = ConfigString(guard, "field");
                var split = SplitQualified(statusField);
                var statusType = split is null ? null : modules.GetValueOrDefault(split.Value.Module)?.Blocks.SelectMany(b => b.Fields)
                    .FirstOrDefault(x => x.Code == split.Value.Field)?.Type;
                var externalApp = ConfigString(ConfigObject(f.Config, "refTarget")!.Value, "app");
                if (externalApp is not null && scope.Declares(externalApp))
                {
                    // 主檔模組的守衛：field 寫主檔符號，in 是該欄位的選項代號（主檔定義未知時只檢查前綴）
                    var (guardOk, guardDef, guardWhy) = TargetField(statusField, $"{externalApp}/{target}");
                    if (!guardOk) errors.Add($"{at}.field: {Why(guardWhy, $"必須是 {externalApp}/{target} 的下拉欄位（<主檔 id>/<模組代號>.<欄位代號>）")}");
                    else if (guardDef is not null && guardDef.Type != FieldType.Enum) errors.Add($"{at}.field: {statusField} 不是下拉欄位");
                    if (!guard.TryGetProperty("in", out var masterIn) || masterIn.ValueKind != System.Text.Json.JsonValueKind.Array || masterIn.GetArrayLength() == 0)
                        errors.Add($"{at}.in: 至少要列一個允許的狀態（選項代號）");
                    else if (guardDef is not null)
                        foreach (var option in masterIn.EnumerateArray())
                            if (option.ValueKind != System.Text.Json.JsonValueKind.String || guardDef.Options.All(o => o.Code != option.GetString()))
                                errors.Add($"{at}.in: 狀態 {option} 不是 {statusField} 的選項代號");
                    continue;
                }
                if (externalApp is not null) { errors.Add($"{at}: 目標狀態守衛只能用在指向本套件模組或 requires.masters 主檔模組的參照欄位"); continue; }
                if (split is null || split.Value.Module != target || statusType != FieldType.Enum)
                    errors.Add($"{at}.field: 必須是 {target} 的下拉欄位（<模組代號>.<欄位代號>）");
                if (!guard.TryGetProperty("in", out var allowed) || allowed.ValueKind != System.Text.Json.JsonValueKind.Array || allowed.GetArrayLength() == 0)
                    errors.Add($"{at}.in: 至少要列一個允許的狀態（選項代號）");
                else if (split is not null)
                    foreach (var option in allowed.EnumerateArray())
                        if (option.ValueKind != System.Text.Json.JsonValueKind.String || !(optionOwner.TryGetValue($"{split.Value.Module}.{option.GetString()}", out var owner) && owner == statusField))
                            errors.Add($"{at}.in: 狀態 {option} 不是 {target} 的選項代號");
            }

        // ---- reports ----
        var reportCodes = new HashSet<string>();
        var masterErrors = new List<string>();
        var lookups = MasterLookups(scope, masterErrors,
            code => modules.ContainsKey(code) ? code : null,
            name => Field(name) ? 1 : null,
            name => optionOwner.ContainsKey(name) ? 1 : null,
            AppReportCodec.TypesOf(p.Modules));
        for (var i = 0; i < p.Reports.Count; i++)
        {
            var r = p.Reports[i];
            var at = $"reports[{i}]";
            if (r is null) { errors.Add($"{at}: 不能是 null"); continue; }
            if (!Code().IsMatch(r.Code ?? "")) errors.Add($"{at}.code: 格式錯誤");
            else if (!reportCodes.Add(r.Code!)) errors.Add($"{at}.code: 報表代號重複 {r.Code}");
            if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 200) errors.Add($"{at}.name: 必須是 1 到 200 字");
            foreach (var g in r.ShareWith)
                if (!groupCodes.Contains(g)) errors.Add($"{at}.shareWith: 群組 {g} 沒有在 groups 宣告");
            if (r.Definition is null) { errors.Add($"{at}.definition: 缺少報表定義"); continue; }
            foreach (var e in AppReportCodec.ToDefinition(r.Definition, lookups).Errors) errors.Add($"{at}.definition: {e}");
            foreach (var e in masterErrors.Distinct()) errors.Add($"{at}.definition: {e}");
            masterErrors.Clear();
        }
    }

    private static void CheckCopies(
        List<AppCopy> copies, string? sourceModule, string? targetModule, string at, Func<string?, string?, bool> field, List<string> errors)
    {
        foreach (var copy in copies)
        {
            if (copy is null) { errors.Add($"{at}: 不能有 null"); continue; }
            if (!field(copy.From, sourceModule)) errors.Add($"{at}: 來源欄位 {copy.From} 必須是 {sourceModule} 的欄位");
            if (!field(copy.To, targetModule)) errors.Add($"{at}: 目的欄位 {copy.To} 必須是 {targetModule} 的欄位");
        }
    }
}
