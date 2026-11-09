using System.Text.Json;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using static DynamicModules.Application.Dynamic.TemplateService.TemplateGraph;

namespace DynamicModules.Application.Dynamic.AppService;

// 格式 6 的共用主檔（設計 2026-11-10-shared-master-design §4）：主檔套件、requires.masters、contributions[]、主檔符號。
// 結構層與「只看套件」的引用層一律檢查；主檔欄位層級（基本欄位是否存在、貢獻代號是否撞基本代號）
// 要有主檔套件的定義，呼叫端給了 masters 才檢查（目錄層與匯入），開啟時的實際資料由開啟流程再解析一次。
public static partial class AppPackageValidator
{
    public const int MaxMasters = 5, MaxContributions = 20, MaxContributionFields = 50;
    public const string MasterModuleKeyPrefix = "master-";

    /// <summary>開啟流程建立轉換時還不帶固定值（設計 §4.5 的實作在第 4 步），收下會讓轉出的記錄靜默少了值，所以先整份拒絕。</summary>
    public const string HeaderSetNotYetSupported = "這個版本還不能執行轉換的固定值（header.set），請先拿掉";

    private static readonly HashSet<FieldType> ContributionFieldTypes =
    [
        FieldType.Text, FieldType.Textarea, FieldType.Email, FieldType.Phone, FieldType.Url,
        FieldType.Integer, FieldType.Decimal, FieldType.Currency, FieldType.Percent,
        FieldType.Date, FieldType.DateTime, FieldType.Time,
        FieldType.Enum, FieldType.Boolean, FieldType.User, FieldType.Reference, FieldType.Formula
    ];

    // ---- 結構層 ----

    private static void Format6Structure(AppPackageData p, List<string> errors)
    {
        var m = p.Manifest;
        var masters = m.Requires.Masters;
        if (p.FormatVersion < 6)
        {
            // 舊讀取器靜默略過會讓客戶以為有共用主檔卻沒有（同整合 §3.1），一律整份拒絕
            if (m.Kind is not null) errors.Add("manifest.kind: formatVersion 6 才支援 kind");
            if (masters.Count > 0) errors.Add("manifest.requires.masters: formatVersion 6 才支援 requires.masters");
            if (p.Contributions.Count > 0) errors.Add("contributions: formatVersion 6 才支援 contributions");
            foreach (var mod in p.Modules.Where(x => x is not null))
            {
                if (mod.Extensible is not null) errors.Add($"{mod.Code}.extensible: formatVersion 6 才支援 extensible");
                if (mod.TitleTemplate is not null) errors.Add($"{mod.Code}.titleTemplate: formatVersion 6 才支援標題樣板");
                foreach (var f in FieldsOf(mod).Where(f => f.Active is not null))
                    errors.Add($"{mod.Code}.{f.Code}.active: formatVersion 6 才支援 active");
            }
            foreach (var c in p.Conversions.Where(c => c?.Header?.Set is { Count: > 0 }))
                errors.Add($"conversions[{c.Code}].header.set: formatVersion 6 才支援 header.set");
            return;
        }

        if (m.Kind is not (null or AppManifest.AppKindName or AppManifest.MasterKindName))
            errors.Add("manifest.kind: 必須是 app 或 master");

        if (masters.Count > MaxMasters) errors.Add($"manifest.requires.masters: 最多 {MaxMasters} 個");
        var seen = new HashSet<string>();
        for (var i = 0; i < masters.Count; i++)
        {
            var r = masters[i];
            var at = $"manifest.requires.masters[{i}]";
            if (r is null || !AppId().IsMatch(r.Id ?? "")) { errors.Add($"{at}.id: 格式錯誤"); continue; }
            if (r.Modules.Count == 0 || r.Modules.Any(c => !AppId().IsMatch(c ?? "")))
                errors.Add($"{at}.modules: 至少要有一個模組代號，且格式正確");
            if (r.Id == m.Id) errors.Add($"{at}: 不能依賴自己");
            if (!seen.Add(r.Id!)) errors.Add($"{at}: 主檔 {r.Id} 重複");
            if (m.Requires.Apps.Any(a => a?.Id == r.Id)) errors.Add($"{at}: {r.Id} 不能同時列在 requires.apps 與 requires.masters");
        }

        var isMaster = m.IsMaster;
        foreach (var mod in p.Modules.Where(x => x is not null))
        {
            if (mod.Extensible == true && !isMaster) errors.Add($"{mod.Code}.extensible: 只有主檔套件的模組可以設 extensible");
            if (!isMaster)
                foreach (var f in FieldsOf(mod).Where(f => f.Active is not null))
                    errors.Add($"{mod.Code}.{f.Code}.active: 只有主檔套件的模組與 contributions 的欄位可以設 active");
        }

        if (isMaster) MasterStructure(p, errors);
        else ContributionStructure(p, errors);
    }

    /// <summary>主檔是穩定的基礎資料，不帶流程（§4.1）。</summary>
    private static void MasterStructure(AppPackageData p, List<string> errors)
    {
        void Reject(int count, string section)
        {
            if (count > 0) errors.Add($"{section}: 主檔套件不收 {section}");
        }
        Reject(p.Workflows.Count, "workflows");
        Reject(p.Conversions.Count, "conversions");
        Reject(p.Events.Count, "events");
        Reject(p.Checks.Count, "checks");
        Reject(p.Ledgers.Count, "ledgers");
        Reject(p.PrintTemplates.Count, "printTemplates");
        Reject(p.Integrations.Count, "integrations");
        Reject(p.Contributions.Count, "contributions");
        if (p.Manifest.Requires.Apps.Count > 0 || p.Manifest.Requires.Entities.Count > 0)
            errors.Add("manifest.requires: 主檔套件的 requires 只能有 masters");
        if (!p.Modules.Any(x => x?.Extensible == true)) errors.Add("modules: 主檔套件至少要有一個模組標 extensible");
        foreach (var mod in p.Modules.Where(x => x is not null && !(x.Key ?? "").StartsWith(MasterModuleKeyPrefix, StringComparison.Ordinal)))
            errors.Add($"{mod.Code}.key: 主檔模組的 key 必須以 {MasterModuleKeyPrefix} 開頭");
    }

    private static void ContributionStructure(AppPackageData p, List<string> errors)
    {
        if (p.Contributions.Count > MaxContributions) errors.Add($"contributions: 最多 {MaxContributions} 個");
        for (var i = 0; i < p.Contributions.Count; i++)
        {
            var c = p.Contributions[i];
            var at = $"contributions[{i}]";
            if (c is null) { errors.Add($"{at}: 不能是 null"); continue; }
            if (c.Block is null) errors.Add($"{at}.block: 缺少區塊");
            else
            {
                if (!Code().IsMatch(c.Block.Code ?? "")) errors.Add($"{at}.block.code: 格式錯誤");
                CheckText(c.Block.Name, $"{at}.block.name", errors);
            }
            if (c.Fields.Count == 0) errors.Add($"{at}.fields: 至少要有一個欄位");
            if (c.Fields.Count > MaxContributionFields) errors.Add($"{at}.fields: 最多 {MaxContributionFields} 個");
            foreach (var f in c.Fields)
            {
                if (f is null) { errors.Add($"{at}.fields: 不能有 null"); continue; }
                var fat = $"{at}.fields[{f.Code}]";
                CheckText(f.Label, $"{fat}.label", errors);
                if (f.Options.Count > MaxOptionsPerField) errors.Add($"{fat}: 選項最多 {MaxOptionsPerField} 個");
                if (f.Options.Any(o => o is null)) errors.Add($"{fat}.options: 不能有 null");
                foreach (var o in f.Options.Where(o => o is not null)) CheckText(o.Label, $"{fat}.options.{o.Code}", errors);
                CheckConfigKeys($"{at}.fields", f, p.FormatVersion, errors);
                if (!ContributionFieldTypes.Contains(f.Type))
                    errors.Add($"{fat}.type: 貢獻欄位不能是 {LowerName(f.Type)}（不收自動編號、複選、附件、彙總）");
                if (f.Config.ValueKind != JsonValueKind.Object) continue;
                if (f.Config.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.True)
                    errors.Add($"{fat}: 貢獻欄位不得必填（別的應用程式建立主檔記錄時不該被擋住）");
                if (ConfigString(f.Config, "relation") is "detail") errors.Add($"{fat}: 貢獻欄位不能是明細的表頭參照");
                if (f.Type == FieldType.Reference && ConfigString(f.Config, "onDelete") == "cascade")
                    errors.Add($"{fat}.onDelete: 貢獻的參照欄位不能是 cascade（主檔記錄不能因為別的應用程式刪資料而消失）");
            }
            foreach (var perm in c.Permissions.Where(x => x is null)) errors.Add($"{at}.permissions: 不能有 null");
            foreach (var a in c.FieldAccess.Where(x => x is null)) errors.Add($"{at}.fieldAccess: 不能有 null");
        }
    }

    private static string LowerName(FieldType t) => t.ToString().ToLowerInvariant();

    private static IEnumerable<TemplateField> FieldsOf(TemplateModule m) =>
        m.Blocks.Where(b => b is not null).SelectMany(b => b.Fields).Where(f => f is not null);

    // ---- 引用層 ----

    /// <summary>
    /// 套件模組（含主檔模組）的停用欄位被同模組啟用中的公式或規則引用：開啟時停用會被設計器的規則擋下，匯入時就擋，訊息和貢獻欄位相同（審查待決 8）。
    /// </summary>
    private static void InactiveFieldReferences(AppPackageData p, List<string> errors)
    {
        foreach (var m in p.Modules.Where(m => m is not null))
        {
            var fields = m.Blocks.SelectMany(b => b.Fields).Where(f => f is not null && f.Code is not null).ToList();
            foreach (var f in fields.Where(f => f.Active == false))
            {
                var user = fields.FirstOrDefault(g => g.Active != false && g.Code != f.Code && FormulaOf(g) is { } fx && FormulaCodes(fx).Contains(f.Code))?.Code
                    ?? (m.Rules.Any(r => r.Active && (r.Targets.Contains($"field:{f.Code}")
                        || ConditionLeaves(r.Condition).Any(l => LeafField(l) == f.Code || LeafFieldRef(l) == f.Code))) ? "rules" : null);
                if (user is not null) errors.Add($"modules[{m.Code}].fields[{f.Code}].active: 此欄位被公式或規則引用，不能停用（{user}）");
            }
        }
    }

    private static void V6References(AppPackageData p, HashSet<string> groupCodes, MasterScope scope, List<string> errors)
    {
        var modules = p.Modules.ToDictionary(m => m.Code, m => m);
        var seen = new HashSet<string>();
        InactiveFieldReferences(p, errors);
        for (var i = 0; i < p.Contributions.Count; i++)
        {
            var c = p.Contributions[i];
            var at = $"contributions[{i}]";
            if (scope.ModuleError(c.Master ?? "", c.Module ?? "") is { } prefix) { errors.Add($"{at}: {prefix}"); continue; }
            if (!seen.Add($"{c.Master}/{c.Module}")) errors.Add($"{at}: 對 {c.Master}/{c.Module} 的貢獻重複（同一個主檔模組至多一筆）");
            var definition = scope.Definition(c.Master!, c.Module!);
            if (definition is not null && definition.Extensible != true) errors.Add($"{at}: 主檔模組 {c.Master}/{c.Module} 不接受貢獻（沒有標 extensible）");
            var baseFields = definition is null ? [] : FieldsOf(definition).Select(f => f.Code).ToHashSet();
            var baseOptions = definition is null ? [] : FieldsOf(definition).SelectMany(f => f.Options).Select(o => o.Code).ToHashSet();

            var codes = new Dictionary<string, TemplateField>();
            var optionCodes = new HashSet<string>();
            foreach (var f in c.Fields)
            {
                var fat = $"{at}.fields[{f.Code}]";
                if (!Code().IsMatch(f.Code ?? "")) { errors.Add($"{fat}.code: 格式錯誤"); continue; }
                if (!codes.TryAdd(f.Code!, f)) errors.Add($"{fat}.code: 欄位代號在這個貢獻裡重複");
                if (baseFields.Contains(f.Code!)) errors.Add($"{fat}.code: 與主檔 {c.Master} 的基本欄位代號相同");
                foreach (var o in f.Options)
                {
                    if (!OptionCodePattern().IsMatch(o.Code ?? "")) errors.Add($"{fat}: 選項代號 {o.Code} 格式錯誤");
                    else if (!optionCodes.Add(o.Code!)) errors.Add($"{fat}: 選項代號 {o.Code} 在這個貢獻裡重複");
                    else if (baseOptions.Contains(o.Code!)) errors.Add($"{fat}: 選項代號 {o.Code} 與主檔 {c.Master} 的基本選項代號相同");
                }
            }

            // 貢獻欄位的符號空間：本貢獻的欄位，加上主檔模組的基本欄位（有定義時）
            bool Local(string code) => codes.ContainsKey(code) || baseFields.Contains(code);
            foreach (var f in codes.Values)
                ContributionFieldReferences($"{at}.fields[{f.Code}]", c, f, modules, scope, definition is not null, Local, errors);

            // 停用的欄位被啟用中的公式引用：開啟時停用會被設計器的規則擋下，匯入時就擋，訊息照設計器（貢獻不收規則，只看公式）
            foreach (var f in codes.Values.Where(f => f.Active == false))
                if (codes.Values.FirstOrDefault(g => g.Active != false && g.Code != f.Code && FormulaOf(g) is { } fx && FormulaCodes(fx).Contains(f.Code)) is { } user)
                    errors.Add($"{at}.fields[{f.Code}].active: 此欄位被公式或規則引用，不能停用（{user.Code}）");

            foreach (var perm in c.Permissions)
            {
                if (!groupCodes.Contains(perm.Group ?? "")) errors.Add($"{at}.permissions: 群組 {perm.Group} 沒有在 groups 宣告");
                if (perm.Privileges is null) errors.Add($"{at}.permissions: 權限 {perm.Group} 缺少 privileges");
                if (perm.Scope is { } dataScope && !Enum.IsDefined(dataScope)) errors.Add($"{at}.permissions: 權限 {perm.Group} 的資料範圍不正確");
            }
            var seenAccess = new HashSet<string>();
            for (var j = 0; j < c.FieldAccess.Count; j++)
            {
                var a = c.FieldAccess[j];
                var aat = $"{at}.fieldAccess[{j}]";
                var own = MasterScope.Parse(a.Field) is { Tail: { } code } s && s.App == c.Master && s.Module == c.Module && codes.ContainsKey(code);
                if (!own) errors.Add($"{aat}.field: 只能限制本貢獻自己的欄位（{c.Master}/{c.Module}.<欄位代號>），{a.Field} 不是");
                if (!groupCodes.Contains(a.Group ?? "")) errors.Add($"{aat}.group: 群組 {a.Group} 沒有在 groups 宣告");
                if (!Enum.IsDefined(a.Access)) errors.Add($"{aat}.access: 必須是 hidden 或 readOnly");
                if (!seenAccess.Add($"{a.Field}|{a.Group}")) errors.Add($"{aat}: {a.Field} 對群組 {a.Group} 重複設定");
            }
        }
    }

    private static void ContributionFieldReferences(
        string at, AppContribution c, TemplateField f, Dictionary<string, TemplateModule> modules, MasterScope scope,
        bool masterKnown, Func<string, bool> local, List<string> errors)
    {
        var config = f.Config;
        if (config.ValueKind != JsonValueKind.Object) return;
        if (f.Type != FieldType.Formula && config.TryGetProperty("formula", out var fx) && fx.ValueKind != JsonValueKind.Null)
            errors.Add($"{at}: 只有公式欄位可以設定公式");
        if (config.TryGetProperty("rollup", out var rx) && rx.ValueKind != JsonValueKind.Null) errors.Add($"{at}: 貢獻欄位不能設定彙總");
        if (ConfigString(config, "parentDefaultField") is not null) errors.Add($"{at}: 貢獻欄位不能設定 parentDefaultField");

        // 公式只能引用主檔基本欄位與本貢獻的欄位；主檔定義未知時，不是本貢獻的代號無從判斷，留給開啟時解析
        if (FormulaOf(f) is { } formula)
            foreach (var code in FormulaCodes(formula).Distinct().Where(code => !local(code) && masterKnown))
                errors.Add($"{at}: 公式引用了 {code}，貢獻的公式只能引用主檔的基本欄位與本貢獻的欄位");

        if (config.TryGetProperty("defaultValue", out var dv) && IsOptionField(f) && dv.ValueKind == JsonValueKind.String
            && OptionReference().Match(dv.GetString()!) is { Success: true } om && f.Options.All(o => o.Code != om.Groups[1].Value))
            errors.Add($"{at}: 預設值引用了不存在的選項 {dv.GetString()}");

        if (f.Type != FieldType.Reference) return;
        if (ConfigObject(config, "refTarget") is not { } target) { errors.Add($"{at}: 參照欄位缺少 refTarget"); return; }
        var key = ConfigString(target, "key");
        var app = ConfigString(target, "app");
        string? targetModule = null;   // 帶值、守衛的來源模組：本套件模組代號，或主檔模組符號
        if (ConfigString(target, "kind") != ReferenceTarget.ModuleKind)
            errors.Add($"{at}: 貢獻的參照欄位只能指向本套件的模組或 requires.masters 的主檔模組");
        else if (app is null)
        {
            if (key is null || !modules.ContainsKey(key)) errors.Add($"{at}: 目標模組 {key} 不在套件內");
            else targetModule = key;
            // 指向自己模組的貢獻參照：刪除自己的記錄時清掉追溯值，不然轉換過一次之後那筆記錄就刪不掉（§4.3）
            if (ConfigString(config, "onDelete") != "setNull")
                errors.Add($"{at}.onDelete: 指向本套件模組的貢獻參照欄位必須是 setNull");
        }
        else if (!scope.Declares(app)) errors.Add($"{at}: 貢獻的參照欄位只能指向本套件的模組或 requires.masters 的主檔模組（{app} 不是）");
        else if (scope.ModuleError(app, key ?? "") is { } error) errors.Add($"{at}: {error}");
        else targetModule = $"{app}/{key}";
        if (targetModule is null) return;

        (TemplateField? Field, string? Error) SourceField(string? name)
        {
            if (name is null) return (null, "缺少欄位");
            if (targetModule.Contains('/'))
                return MasterScope.Parse(name) is { } s && $"{s.App}/{s.Module}" == targetModule
                    ? scope.Field(name)
                    : (null, $"{name} 必須是 {targetModule} 的欄位");
            return SplitQualified(name) is { } q && q.Module == targetModule
                && modules[targetModule].Blocks.SelectMany(b => b.Fields).FirstOrDefault(x => x.Code == q.Field) is { } hit
                ? (hit, null)
                : (null, $"{name} 必須是 {targetModule} 的欄位（<模組代號>.<欄位代號>）");
        }

        if (ConfigObject(config, "fill") is { } fill && fill.TryGetProperty("map", out var map) && map.ValueKind == JsonValueKind.Array)
            foreach (var entry in map.EnumerateArray())
            {
                if (SourceField(ConfigString(entry, "from")).Error is { } fromError) errors.Add($"{at}.fill.map: 來源欄位 {fromError}");
                var to = ConfigString(entry, "to");
                // 主檔定義未知時基本欄位無從比對，只擋得掉格式不對的代號
                var ok = to is not null && Code().IsMatch(to) && (local(to) || !masterKnown);
                if (!ok)
                    errors.Add($"{at}.fill.map: 目的欄位 {to} 必須是本貢獻或主檔 {c.Master}/{c.Module} 的欄位");
            }

        if (ConfigObject(config, "refGuard") is { } guard)
        {
            var (statusField, statusError) = SourceField(ConfigString(guard, "field"));
            if (statusError is not null) errors.Add($"{at}.refGuard.field: {statusError}");
            else if (statusField is not null && statusField.Type != FieldType.Enum) errors.Add($"{at}.refGuard.field: 必須是下拉欄位");
            if (!guard.TryGetProperty("in", out var allowed) || allowed.ValueKind != JsonValueKind.Array || allowed.GetArrayLength() == 0)
                errors.Add($"{at}.refGuard.in: 至少要列一個允許的狀態（選項代號）");
            else if (statusField is not null)
                foreach (var option in allowed.EnumerateArray())
                    if (option.ValueKind != JsonValueKind.String || statusField.Options.All(o => o.Code != option.GetString()))
                        errors.Add($"{at}.refGuard.in: {option} 不是 {ConfigString(guard, "field")} 的選項代號");
        }
    }

    /// <summary>
    /// 報表、事件、檢查的代號查表加上主檔符號（§4.4）：帶 / 的模組、欄位、選項交給 MasterScope，前綴或欄位錯誤記進 masterErrors；
    /// 其餘照原本的查表。主檔定義未知時欄位型別回 null（同整合的夥伴符號）。
    /// </summary>
    private static AppReportCodec.Lookups MasterLookups(
        MasterScope scope, List<string> masterErrors, Func<string, string?> module, Func<string, long?> field, Func<string, long?> option,
        Func<string, FieldType?> typeOf)
    {
        bool IsMaster(string symbol) => MasterScope.Parse(symbol) is { } s && scope.Declares(s.App);
        return new AppReportCodec.Lookups(
            code =>
            {
                if (!IsMaster(code)) return module(code);
                var s = MasterScope.Parse(code)!.Value;
                var error = s.Tail is not null ? $"{code} 不是主檔模組符號（<主檔 id>/<模組代號>）" : scope.ModuleError(s.App, s.Module);
                if (error is not null) { masterErrors.Add(error); return null; }
                return code;
            },
            name =>
            {
                if (!IsMaster(name)) return field(name);
                if (scope.Field(name).Error is { } error) { masterErrors.Add(error); return null; }
                return 1;
            },
            name =>
            {
                if (!IsMaster(name)) return option(name);
                if (scope.OptionError(name) is { } error) { masterErrors.Add(error); return null; }
                return 1;
            },
            name => IsMaster(name) ? scope.TypeOf(name) : typeOf(name));
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z][a-z0-9_-]{0,39}$")]
    private static partial System.Text.RegularExpressions.Regex OptionCodePattern();

    /// <summary>
    /// 主檔符號 &lt;主檔 id&gt;/&lt;模組代號&gt;[.&lt;欄位或選項代號&gt;] 的解析範圍：requires.masters 宣告的主檔與模組、
    /// 本套件自己的貢獻；有主檔定義時再加上主檔的基本欄位與基本選項。別的應用程式的貢獻永遠看不到（§4.4）。
    /// </summary>
    internal sealed class MasterScope
    {
        private readonly Dictionary<string, HashSet<string>> required = [];
        private readonly IReadOnlyDictionary<string, AppPackageData>? known;
        private readonly Dictionary<string, AppContribution> own = [];

        public MasterScope(AppPackageData p, IReadOnlyDictionary<string, AppPackageData>? masters)
        {
            foreach (var r in p.Manifest.Requires.Masters.Where(r => r?.Id is not null)) required.TryAdd(r.Id, [.. r.Modules]);
            known = masters;
            foreach (var c in p.Contributions.Where(c => c?.Master is not null && c.Module is not null))
                own.TryAdd($"{c.Master}/{c.Module}", c);
        }

        public bool Declares(string? app) => app is not null && required.ContainsKey(app);

        /// <summary>主檔 id 不在目錄時回 null（只能檢查前綴）。</summary>
        public TemplateModule? Definition(string app, string module) =>
            known is not null && known.TryGetValue(app, out var master) ? master.Modules.FirstOrDefault(m => m.Code == module) : null;

        public bool IsKnown(string app) => known is not null && known.ContainsKey(app);

        /// <summary>「主檔 id/模組代號」的前綴錯誤；沒錯回 null。</summary>
        public string? ModuleError(string app, string module)
        {
            if (!required.TryGetValue(app, out var modules)) return $"主檔 {app} 沒有列在 requires.masters";
            if (!modules.Contains(module)) return $"主檔模組 {app}/{module} 沒有列在 requires.masters 的 modules";
            if (IsKnown(app) && Definition(app, module) is null) return $"主檔 {app} 沒有模組 {module}";
            return null;
        }

        public AppContribution? Contribution(string app, string module) => own.GetValueOrDefault($"{app}/{module}");

        /// <summary>解析主檔欄位符號。Field 是 null 而 Error 也是 null 代表主檔不在目錄、只檢查了前綴。</summary>
        public (TemplateField? Field, string? Error) Field(string symbol)
        {
            if (Parse(symbol) is not { Tail: { } code } s) return (null, $"{symbol} 不是主檔欄位符號（<主檔 id>/<模組代號>.<欄位代號>）");
            if (ModuleError(s.App, s.Module) is { } error) return (null, error);
            if (Contribution(s.App, s.Module)?.Fields.FirstOrDefault(f => f?.Code == code) is { } mine) return (mine, null);
            if (Definition(s.App, s.Module) is { } def)
                return FieldsOf(def).FirstOrDefault(f => f.Code == code) is { } baseField
                    ? (baseField, null)
                    : (null, $"{symbol} 不是主檔 {s.App} 的基本欄位，也不是本套件貢獻的欄位");
            return (null, null);
        }

        public string? OptionError(string symbol)
        {
            if (Parse(symbol) is not { Tail: { } code } s) return $"{symbol} 不是主檔選項符號（<主檔 id>/<模組代號>.<選項代號>）";
            if (ModuleError(s.App, s.Module) is { } error) return error;
            if (Contribution(s.App, s.Module)?.Fields.Any(f => f?.Options.Any(o => o?.Code == code) == true) == true) return null;
            if (Definition(s.App, s.Module) is { } def && !FieldsOf(def).Any(f => f.Options.Any(o => o.Code == code)))
                return $"{symbol} 不是主檔 {s.App} 的基本選項，也不是本套件貢獻的選項";
            return null;
        }

        public FieldType? TypeOf(string symbol) => Field(symbol).Field?.Type;

        public static (string App, string Module, string? Tail)? Parse(string? symbol)
        {
            if (SplitPartner(symbol) is not { } sp) return null;
            var dot = sp.Remainder.IndexOf('.');
            return dot < 0 ? (sp.App, sp.Remainder, null) : (sp.App, sp.Remainder[..dot], sp.Remainder[(dot + 1)..]);
        }
    }
}
