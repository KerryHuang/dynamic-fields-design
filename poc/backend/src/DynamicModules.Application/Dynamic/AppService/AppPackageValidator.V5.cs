using System.Text.Json;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using static DynamicModules.Application.Dynamic.TemplateService.TemplateGraph;

namespace DynamicModules.Application.Dynamic.AppService;

// 格式 5 的整合（integrations）：只看套件就能判斷的檢查。夥伴的欄位與選項此時不一定已知，
// 這裡只檢查夥伴符號的前綴（應用程式 id 在 with、模組在該夥伴的 modules）；欄位層級在安裝時解析。
public static partial class AppPackageValidator
{
    public const int MaxIntegrations = 20, MaxPartners = 5, MaxIntegrationFields = 20;

    private static readonly HashSet<FieldType> IntegrationFieldTypes =
        [FieldType.Text, FieldType.Integer, FieldType.Decimal, FieldType.Date, FieldType.Reference];

    // 整合欄位的設定只收這幾個鍵：沒有公式、彙總、帶值、守衛、預設值，避免整合欄位牽動擁有者其他欄位
    private static readonly HashSet<string> IntegrationConfigKeys = new(StringComparer.Ordinal)
    {
        "required", "readOnly", "maxLength", "precision", "scale", "fullWidth", "showInList", "isFilterable", "isSortable", "refTarget", "onDelete"
    };

    /// <summary>夥伴符號 &lt;應用程式 id&gt;/&lt;模組代號&gt;[.&lt;欄位或選項代號&gt;] 拆成 (id, 其餘)；不是夥伴符號回 null。</summary>
    public static (string App, string Remainder)? SplitPartner(string? symbol)
    {
        var slash = symbol?.IndexOf('/') ?? -1;
        return slash <= 0 || slash == symbol!.Length - 1 ? null : (symbol[..slash], symbol[(slash + 1)..]);
    }

    private static void IntegrationStructure(AppPackageData p, List<string> errors)
    {
        if (p.Integrations.Count > 0 && p.FormatVersion < 5) errors.Add("integrations: formatVersion 5 才支援 integrations");
        if (p.Integrations.Count > MaxIntegrations) errors.Add($"integrations: 最多 {MaxIntegrations} 個");
    }

    private static void V5References(AppPackageData p, Dictionary<string, TemplateField> fields, MasterScope scope, List<string> errors)
    {
        if (p.Integrations.Count == 0) return;
        var modules = p.Modules.ToDictionary(m => m.Code, m => m);
        var optionNames = p.Modules.SelectMany(m => m.Blocks.SelectMany(b => b.Fields).SelectMany(f => f.Options.Select(o => $"{m.Code}.{o.Code}")))
            .ToHashSet();
        var workflowOf = p.Workflows.Where(w => w is not null && w.Module is not null).GroupBy(w => w.Module).ToDictionary(g => g.Key, g => g.First());
        var baseTypes = AppReportCodec.TypesOf(p.Modules);
        var usedCodes = new HashSet<string>(p.Events.Select(e => e.Code).Concat(p.Checks.Select(c => c.Code)));
        var integrationCodes = new HashSet<string>();

        for (var i = 0; i < p.Integrations.Count; i++)
        {
            var g = p.Integrations[i];
            var at = $"integrations[{i}]";
            if (g is null) { errors.Add($"{at}: 不能是 null"); continue; }
            if (!Code().IsMatch(g.Code ?? "")) errors.Add($"{at}.code: 格式錯誤");
            else if (!integrationCodes.Add(g.Code!)) errors.Add($"{at}.code: 整合代號重複 {g.Code}");
            CheckText(g.Name, $"{at}.name", errors);
            if (g.OnPartnerClosed is not (null or "skip" or "block")) errors.Add($"{at}.onPartnerClosed: 必須是 skip 或 block");

            // ---- with ----
            if (g.With.Count is 0 or > MaxPartners) errors.Add($"{at}.with: 夥伴要 1 到 {MaxPartners} 個");
            var partners = new Dictionary<string, HashSet<string>>();
            foreach (var w in g.With)
            {
                if (w is null || !AppId().IsMatch(w.Id ?? "")) { errors.Add($"{at}.with: 夥伴 id 格式錯誤"); continue; }
                if (w.Id == p.Manifest.Id) errors.Add($"{at}.with: 不能以自己為夥伴");
                if (w.Modules.Count == 0 || w.Modules.Any(m => !AppId().IsMatch(m ?? ""))) errors.Add($"{at}.with[{w.Id}].modules: 至少要有一個模組代號，且格式正確");
                if (!partners.TryAdd(w.Id!, [.. w.Modules])) errors.Add($"{at}.with: 夥伴 {w.Id} 重複");
            }

            // ---- fields ----
            if (g.Fields.Count > MaxIntegrationFields) errors.Add($"{at}.fields: 最多 {MaxIntegrationFields} 個");
            var ownFields = new Dictionary<string, FieldType>();
            foreach (var f in g.Fields)
            {
                if (f is null) { errors.Add($"{at}.fields: 不能有 null"); continue; }
                var fat = $"{at}.fields[{f.Module}.{f.Code}]";
                if (!modules.TryGetValue(f.Module ?? "", out var module)) { errors.Add($"{fat}: 擁有者模組 {f.Module} 不在套件內"); continue; }
                if (!module.Blocks.Any(b => b.Code == f.Block)) errors.Add($"{fat}.block: 區塊 {f.Block} 不在模組 {f.Module}");
                if (!Code().IsMatch(f.Code ?? "")) errors.Add($"{fat}.code: 格式錯誤");
                else if (fields.ContainsKey(Qualify(f.Module, f.Code)) || ownFields.ContainsKey(Qualify(f.Module, f.Code)))
                    errors.Add($"{fat}.code: 與模組既有欄位重複");
                else ownFields[Qualify(f.Module, f.Code)] = f.Type;
                CheckText(f.Label, $"{fat}.label", errors);
                if (!IntegrationFieldTypes.Contains(f.Type)) errors.Add($"{fat}.type: 整合欄位只能是文字、整數、小數、日期或參照");
                IntegrationFieldConfig(fat, f, partners, scope, errors);
            }

            // ---- events、checks：符號空間 = 擁有者（含本整合新增的欄位）+ 夥伴符號 ----
            var typeOf = (string name) => ownFields.TryGetValue(name, out var t) ? t : baseTypes(name);
            var partnerErrors = new List<string>();
            void CheckPartnerModule(string module)
            {
                if (SplitPartner(module) is not { } sp) return;
                if (!partners.TryGetValue(sp.App, out var allowed)) partnerErrors.Add($"夥伴 {sp.App} 沒有在 with 宣告");
                else if (!allowed.Contains(sp.Remainder.Split('.')[0])) partnerErrors.Add($"夥伴模組 {sp.Remainder.Split('.')[0]} 沒有列在 with[{sp.App}].modules");
            }
            // 主檔符號（requires.masters）先交給 MasterScope；其餘帶 / 的才是夥伴符號
            var lookups = MasterLookups(scope, partnerErrors,
                code =>
                {
                    if (SplitPartner(code) is not null) { CheckPartnerModule(code); return code; }
                    return modules.ContainsKey(code) ? code : null;
                },
                name =>
                {
                    if (SplitPartner(name) is not null) { CheckPartnerModule(name); return 1; }
                    return fields.ContainsKey(name) || ownFields.ContainsKey(name) ? 1 : null;
                },
                name =>
                {
                    if (SplitPartner(name) is not null) { CheckPartnerModule(name); return 1; }
                    return optionNames.Contains(name) ? 1 : null;
                },
                typeOf);

            foreach (var e in g.Events)
            {
                var eat = $"{at}.events[{e?.Code}]";
                if (e is null) { errors.Add($"{at}.events: 不能有 null"); continue; }
                if (!AppId().IsMatch(e.Code ?? "")) errors.Add($"{eat}.code: 格式錯誤");
                else if (!usedCodes.Add(e.Code!)) errors.Add($"{eat}.code: 事件代號與套件內其他事件或檢查重複");
                CheckText(e.Name, $"{eat}.name", errors);
                if (e.Trigger is null || e.Effects.Count == 0) { errors.Add($"{eat}: 缺少觸發設定或效果"); continue; }
                if (!modules.ContainsKey(e.Source ?? "")) { errors.Add($"{eat}.source: 事件的來源必須是擁有者的模組"); continue; }
                if (e.Effects.Any(x => x is null || x.Kind == "custom")) { errors.Add($"{eat}.effects: 不收自訂效果，也不能有 null"); continue; }
                if (e.Trigger.On == "transition" && !workflowOf.ContainsKey(e.Source!)) errors.Add($"{eat}.trigger: 來源模組 {e.Source} 沒有狀態機");
                foreach (var w in e.Trigger.Watch ?? [])
                    if (!(fields.ContainsKey(w ?? "") || ownFields.ContainsKey(w ?? "")) || SplitQualified(w!)!.Value.Module != e.Source)
                        errors.Add($"{eat}.trigger.watch: {w} 必須是來源模組 {e.Source} 的欄位");
                foreach (var error in AppEffectsCodec.ToInput(e, lookups).Errors) errors.Add($"{eat}: {error}");
            }
            foreach (var c in g.Checks)
            {
                var cat = $"{at}.checks[{c?.Code}]";
                if (c is null) { errors.Add($"{at}.checks: 不能有 null"); continue; }
                if (!AppId().IsMatch(c.Code ?? "")) errors.Add($"{cat}.code: 格式錯誤");
                else if (!usedCodes.Add(c.Code!)) errors.Add($"{cat}.code: 檢查代號與套件內其他事件或檢查重複");
                CheckText(c.Name, $"{cat}.name", errors);
                if (c.Assert?.Limit is null) { errors.Add($"{cat}.assert: 缺少上限設定"); continue; }
                if (!modules.ContainsKey(c.Module ?? "")) { errors.Add($"{cat}.module: 檢查的模組必須是擁有者的模組"); continue; }
                foreach (var error in AppEffectsCodec.ToInput(c, lookups).Errors) errors.Add($"{cat}: {error}");
            }
            foreach (var error in partnerErrors.Distinct()) errors.Add($"{at}: {error}");
        }

        // 沖銷的 ofEvent 必須是同一個整合或套件內的事件
        var allEventCodes = p.Events.Select(e => e.Code).Concat(p.Integrations.SelectMany(g => g.Events.Select(e => e.Code))).ToHashSet();
        foreach (var g in p.Integrations.Where(g => g is not null))
            foreach (var e in g.Events.Where(e => e is not null))
                foreach (var fx in e.Effects.Where(x => x is { Kind: "reverse" }))
                    if (!allEventCodes.Contains(fx.OfEvent ?? "")) errors.Add($"integrations[{g.Code}].events[{e.Code}]: 沖銷的事件 {fx.OfEvent} 不在套件內");
    }

    private static void IntegrationFieldConfig(
        string at, AppIntegrationField f, Dictionary<string, HashSet<string>> partners, MasterScope scope, List<string> errors)
    {
        if (f.Config.ValueKind != JsonValueKind.Object)
        {
            if (f.Type == FieldType.Reference) errors.Add($"{at}: 參照欄位缺少 refTarget");
            return;
        }
        foreach (var prop in f.Config.EnumerateObject())
        {
            if (!IntegrationConfigKeys.Contains(prop.Name)) errors.Add($"{at}: 整合欄位的 config 不收 \"{prop.Name}\"");
            else if (prop.Name == "required" && prop.Value.ValueKind == JsonValueKind.True)
                errors.Add($"{at}: 整合欄位不得必填（夥伴關閉後擁有者的表單會存不了）");
            else if (prop.Name == "refTarget" && prop.Value.ValueKind == JsonValueKind.Object)
                foreach (var inner in prop.Value.EnumerateObject().Where(x => !RefTargetKeys.Contains(x.Name)))
                    errors.Add($"{at}: config.refTarget 不認得 \"{inner.Name}\"");
        }
        if (f.Type != FieldType.Reference) return;
        var target = ConfigObject(f.Config, "refTarget");
        if (target is null) { errors.Add($"{at}: 參照欄位缺少 refTarget"); return; }
        var app = ConfigString(target.Value, "app");
        var key = ConfigString(target.Value, "key");
        if (ConfigString(target.Value, "kind") != ReferenceTarget.ModuleKind || app is null)
            errors.Add($"{at}: 整合的參照欄位必須指向夥伴應用程式的模組（refTarget 要有 kind = module 與 app）");
        else if (!partners.ContainsKey(app) && scope.Declares(app))
        {
            // 也可以指向 requires.masters 的主檔模組（§10），with 不必列主檔
            if (scope.ModuleError(app, key ?? "") is { } error) errors.Add($"{at}: {error}");
        }
        else if (!partners.TryGetValue(app, out var modulesOfPartner) || key is null || !modulesOfPartner.Contains(key))
            errors.Add($"{at}: 夥伴模組 {app}/{key} 沒有列在 with");
    }
}
