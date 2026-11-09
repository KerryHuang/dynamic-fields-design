using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DynamicModules.Application.Dynamic.AppService;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.Rules;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.TemplateService;

/// <summary>
/// 匯出範本（決策 6）：模組加上以它為表頭的明細模組，id 換成代號（模組＝鍵、欄位＝Field.Key、區塊 b_&lt;id&gt;、選項 o_&lt;id&gt;），
/// 是 TemplateInstantiator 代號→id 替換的反向。表達不了的引用不匯出，記在 warnings。
/// </summary>
public sealed partial class TemplateExporter(IModuleRepository modules, IPermissionRepository permissions, IListViewRepository views)
{
    /// <summary>module 要含子層（GetByKeyAsync includeChildren）。</summary>
    public async Task<TemplatePackage> ExportAsync(Module module, CancellationToken ct)
    {
        var members = new List<Module> { module };
        foreach (var (child, _) in await modules.ListDetailModulesAsync(module.Key, ct))
            if (members.All(m => m.Key != child.Key) && await modules.GetByKeyAsync(child.Key, includeChildren: true, ct) is { } full)
                members.Add(full);

        var exported = await ExportMembersAsync(members, m => m.Key, null, ct);
        return new TemplatePackage(1, module.Key, module.NameI18n, null, exported.Modules,
            exported.Warnings.Count > 0 ? exported.Warnings : null);
    }

    /// <summary>
    /// 匯出一組模組。codeOf：模組在套件內的代號（範本用 Module.Key，應用程式用 AppModule.Code）。
    /// refTargets：參照目標的模組 Key → 套件內的 (代號, 應用程式) ；null 代表照範本的老做法保留 Module.Key，
    /// 回傳 null 代表表達不了（該欄位不匯出）。
    /// includeInactive：停用的欄位也匯出並標 active: false（共用主檔模組依歸屬匯出，§9）。
    /// external：套件外、但檔案裡能用符號表達的欄位與選項（共用主檔的基本欄位，例如帶值來源 bp/partner.phone、守衛 bp/partner.role）。
    /// </summary>
    public async Task<ExportedModules> ExportMembersAsync(
        List<Module> members, Func<Module, string> codeOf, Func<string, (string Code, string? App)?>? refTargets, CancellationToken ct,
        ExportCodes? codes = null, bool includeInactive = false, AppReportCodec.Names? external = null)
    {
        var ctx = new Context(members, codeOf, refTargets, codes ?? ExportCodes.Default, includeInactive, external);
        var groups = (await permissions.ListGroupsAsync(ct)).OrderBy(g => g.Id).ToList();
        var result = new List<TemplateModule>();
        foreach (var m in members)
        {
            var blocks = Blocks(m).Select(b => new TemplateBlock(ctx.BlockCode(b.Id), b.LabelI18n, b.Collapsed,
                ctx.FieldsOf(b).Where(f => ctx.Exported.Contains(f.Id))
                    .Select(f => new TemplateField(ctx.FieldCode(f), f.Type, f.LabelI18n, ctx.Config(f), ActiveOptions(f)
                        .Select(o => new TemplateOption(ctx.OptionCode(o.Id), o.LabelI18n, o.Color)).ToList(),
                        f.Status == FieldStatus.Active ? null : false)).ToList(), b.Columns)).ToList();
            var rules = m.Rules.OrderBy(r => r.Sequence).Select((r, i) => ctx.Rule(m, r, i)).OfType<TemplateRule>().ToList();
            var shared = (await views.ListByScopeAsync(m.Id, null, ct)).OrderBy(v => v.Id).Select(ctx.View).ToList();
            var title = m.TitleFieldId is { } t && ctx.Exported.Contains(t) ? ctx.FieldCode(ctx.Fields[t]) : null;
            // 標題樣板存欄位 Id，匯出換成代號；樣板用到的欄位不能停用，所以一定都有匯出
            var titleTemplate = m.TitleTemplate is null ? null : Domain.Modules.TitleTemplate.Map(m.TitleTemplate,
                id => long.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var fid) && ctx.Exported.Contains(fid) ? ctx.FieldCode(ctx.Fields[fid]) : null);
            result.Add(new TemplateModule(codeOf(m), m.Key, m.NameI18n, m.Icon, m.Section, m.Kind, m.HostEntityKey, title,
                blocks, rules, shared, IsDetail(m) ? [] : await PermissionsAsync(m, groups, ct), TitleTemplate: titleTemplate));
        }
        return new ExportedModules(result, ctx.Warnings, ctx.Names());
    }

    /// <summary>只有群組層的動作權限與資料範圍；個人、欄位層、選項範圍不匯出。</summary>
    private async Task<List<TemplatePermission>> PermissionsAsync(Module m, List<PermissionGroup> groups, CancellationToken ct)
    {
        var rows = await permissions.LoadModuleRowsAsync(m, ct);
        var none = new PrivilegesDto(false, false, false, false, false, false, false);
        return groups
            .Select(g => (g.Key, Privileges: rows.GroupPrivileges.FirstOrDefault(p => p.GroupId == g.Id)?.ToDto() ?? none,
                rows.Scopes.FirstOrDefault(s => s.GroupId == g.Id)?.Scope))
            .Where(x => !x.Privileges.IsEmpty || x.Scope is not null)
            .Select(x => new TemplatePermission(x.Key, x.Privileges, x.Scope)).ToList();
    }

    public sealed record ExportedModules(List<TemplateModule> Modules, List<string> Warnings, AppReportCodec.Names Names)
    {
        /// <summary>欄位 Id → 限定欄位名（只含實際匯出的）。</summary>
        public Func<long, string?> Field => Names.Field;
    }

    private static bool IsDetail(Module m) =>
        m.Blocks.SelectMany(b => b.Fields).Any(f => f is { Type: FieldType.Reference, Status: FieldStatus.Active }
            && f.Config.Relation == ReferenceRelation.Detail);

    private static IEnumerable<Block> Blocks(Module m) => m.Blocks.OrderBy(b => b.Sequence).ThenBy(b => b.Id);

    private static IEnumerable<Field> ActiveFields(Block b) =>
        b.Fields.Where(f => f.Status == FieldStatus.Active).OrderBy(f => f.Sequence).ThenBy(f => f.Id);

    private static IEnumerable<FieldOption> ActiveOptions(Field f) => f.Options.Where(o => o.Active).OrderBy(o => o.Sequence).ThenBy(o => o.Id);

    [GeneratedRegex(@"\{(\d+)\}")]
    private static partial Regex FormulaReference();

    [GeneratedRegex(@"^opt_(\d+)$")]
    private static partial Regex OptionReference();

    private sealed class Context
    {
        private readonly HashSet<long> _allFieldIds;
        private readonly Dictionary<long, Module> _moduleOf = new();
        private readonly HashSet<long> _options;
        private readonly HashSet<long> _blocks;

        /// <summary>套件內匯出候選的欄位：啟用中的（includeInactive 時含停用的）。</summary>
        public Dictionary<long, Field> Fields { get; } = new();

        /// <summary>實際匯出的欄位：啟用中，且沒有因為無法表達的引用被略過。</summary>
        public HashSet<long> Exported { get; }

        public List<string> Warnings { get; } = [];

        private readonly Func<Module, string> _codeOf;
        private readonly Func<string, (string Code, string? App)?>? _refTargets;
        private readonly Dictionary<long, Field> _allFields = new();
        private readonly Dictionary<long, long> _optionOwner = new();

        private readonly ExportCodes _codes;
        private readonly bool _includeInactive;
        private readonly AppReportCodec.Names? _external;

        public IEnumerable<Field> FieldsOf(Block b) => _includeInactive
            ? b.Fields.Where(f => f.Status is FieldStatus.Active or FieldStatus.Inactive).OrderBy(f => f.Sequence).ThenBy(f => f.Id)
            : ActiveFields(b);

        public string FieldCode(Field f) => _codes.Field(f);
        public string BlockCode(long id) => _codes.Block(id);
        public string OptionCode(long id) => _codes.Option(id);

        public Context(List<Module> members, Func<Module, string> codeOf, Func<string, (string Code, string? App)?>? refTargets, ExportCodes codes,
            bool includeInactive = false, AppReportCodec.Names? external = null)
        {
            _codes = codes;
            _includeInactive = includeInactive;
            _external = external;
            _codeOf = codeOf;
            _refTargets = refTargets;
            var all = members.SelectMany(m => m.Blocks.SelectMany(b => b.Fields).Select(f => (m, f))).ToList();
            _allFieldIds = all.Select(x => x.f.Id).ToHashSet();
            foreach (var (m, f) in all.Where(x => x.f.Status == FieldStatus.Active || includeInactive && x.f.Status == FieldStatus.Inactive))
            {
                Fields[f.Id] = f;
                _moduleOf[f.Id] = m;
            }
            _options = Fields.Values.SelectMany(ActiveOptions).Select(o => o.Id).ToHashSet();
            foreach (var f in Fields.Values)
                foreach (var o in ActiveOptions(f)) _optionOwner[o.Id] = f.Id;
            _blocks = members.SelectMany(m => m.Blocks).Select(b => b.Id).ToHashSet();
            Exported = Fields.Keys.Where(id => !codes.Excluded.Contains(id)).ToHashSet();

            // 略過一個欄位可能讓依賴它的欄位也表達不了，重複到穩定
            var ordered = members.SelectMany(m => Blocks(m).SelectMany(FieldsOf)).ToList();
            for (var changed = true; changed;)
            {
                changed = false;
                foreach (var f in ordered.Where(f => Exported.Contains(f.Id)))
                    if (WhyOmit(f) is { } reason)
                    {
                        Exported.Remove(f.Id);
                        Warnings.Add(reason);
                        changed = true;
                    }
            }
        }

        private string Label(Field f) => f.LabelI18n.Resolve(I18nText.FallbackLocale);

        private string Code(long fieldId) => _codes.Field(Fields[fieldId]);

        private string Qualified(long fieldId) => $"{_codeOf(_moduleOf[fieldId])}.{_codes.Field(Fields[fieldId])}";

        /// <summary>報表、狀態機、轉換用的 Id → 代號查表；只認實際匯出的欄位，其餘交給 external（沒有就是表達不了）。</summary>
        public AppReportCodec.Names Names()
        {
            var own = new AppReportCodec.Names(
                key => _moduleOf.Values.FirstOrDefault(m => m.Key == key) is { } m ? _codeOf(m) : null,
                id => Exported.Contains(id) ? Qualified(id) : null,
                (fieldId, optionId) => _optionOwner.TryGetValue(optionId, out var owner) && owner == fieldId ? _codes.Option(optionId) : null,
                id => Fields.TryGetValue(id, out var f) && IsOptionField(f),
                id => Fields.TryGetValue(id, out var f) && f.Type is FieldType.Reference or FieldType.User ? f.Type : null);
            return _external is null ? own : AppReportCodec.Names.Or(own, _external);
        }

        /// <summary>帶值來源、守衛欄位：套件內匯出的用限定名，套件外的看 external。</summary>
        private string? Symbol(long fieldId) => Exported.Contains(fieldId) ? Qualified(fieldId) : _external?.Field(fieldId);

        public string? OptionCodeOrNull(long optionId) => _options.Contains(optionId) ? _codes.Option(optionId) : null;

        private string? WhyOmit(Field f)
        {
            var unresolved = $"「{Label(f)}」引用了停用或未匯出的欄位，未匯出";
            if (_refTargets is not null && f.Type == FieldType.Reference && f.Config.RefTarget is { Kind: ReferenceTarget.ModuleKind } target
                && _refTargets(target.Key) is null)
                return $"「{Label(f)}」指向不屬於任何應用程式的模組，未匯出";
            if (f.Config.Formula is { } formula
                && FormulaReference().Matches(formula).Any(x => !Exported.Contains(long.Parse(x.Groups[1].Value))))
                return unresolved;
            if (f.Type == FieldType.Rollup && f.Config.Rollup is { } rollup)
            {
                if (!_allFieldIds.Contains(rollup.SourceFieldId)) return $"「{Label(f)}」彙總了範本外的模組，未匯出";
                if (!Exported.Contains(rollup.SourceFieldId)
                    || rollup.AggregateFieldId is { } agg && !Exported.Contains(agg)
                    || rollup.Condition is { ValueKind: JsonValueKind.Object } c && Condition(c) is null)
                    return unresolved;
            }
            return null;
        }

        /// <summary>FieldConfig 的 JSON，id 換成代號：formula、下拉欄位的 defaultValue、rollup、parentDefaultFieldId。</summary>
        public JsonElement Config(Field f)
        {
            var node = JsonSerializer.SerializeToNode(f.Config, TemplateJson.Options)!.AsObject();
            if (f.Config.Formula is { } formula)
                node["formula"] = FormulaReference().Replace(formula, x => $"{{{Code(long.Parse(x.Groups[1].Value))}}}");
            if (node["defaultValue"] is { } dv && IsOptionField(f))
            {
                if (OptionValue(dv) is { } converted) node["defaultValue"] = converted;
                else
                {
                    node.Remove("defaultValue");
                    Warnings.Add($"「{Label(f)}」的預設值是停用的選項，未匯出這個設定");
                }
            }
            if (f.Config.Rollup is { } rollup)
            {
                var converted = new JsonObject
                {
                    ["sourceField"] = Qualified(rollup.SourceFieldId),
                    ["function"] = node["rollup"]!["function"]!.DeepClone()
                };
                if (rollup.AggregateFieldId is { } agg) converted["aggregateField"] = Qualified(agg);
                if (rollup.Condition is { ValueKind: JsonValueKind.Object } condition) converted["condition"] = Condition(condition);
                node["rollup"] = converted;
            }
            if (_refTargets is not null && node["refTarget"] is JsonObject refTarget
                && refTarget["kind"]?.GetValue<string>() == ReferenceTarget.ModuleKind && refTarget["key"]?.GetValue<string>() is { } targetKey
                && _refTargets(targetKey) is { } resolved)
            {
                refTarget["key"] = resolved.Code;
                if (resolved.App is not null) refTarget["app"] = resolved.App;
            }
            if (f.Config.Fill is { } fill)
            {
                var map = new JsonArray();
                foreach (var entry in fill.Map)
                    if (Symbol(entry.From) is { } from && Exported.Contains(entry.To) && _moduleOf[entry.To] == _moduleOf[f.Id])
                        map.Add(new JsonObject { ["from"] = from, ["to"] = Code(entry.To) });
                if (map.Count == fill.Map.Count) node["fill"] = new JsonObject { ["mode"] = fill.Mode, ["map"] = map };
                else
                {
                    node.Remove("fill");
                    Warnings.Add($"「{Label(f)}」的帶值引用了停用、未匯出或套件外的欄位，未匯出這個設定");
                }
            }
            if (f.Config.RefGuard is { } guard)
            {
                node.Remove("refGuard");
                var codes = guard.OptionIds.Select(id => OptionCodeOrNull(id) ?? _external?.Option(guard.FieldId, id)).ToList();
                if (Symbol(guard.FieldId) is { } guarded && codes.All(c => c is not null))
                    node["refGuard"] = new JsonObject { ["field"] = guarded, ["in"] = new JsonArray(codes.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()) };
                else Warnings.Add($"「{Label(f)}」的目標狀態守衛引用了停用、未匯出或套件外的欄位或選項，未匯出這個設定，匯入後不再限制目標狀態");
            }
            if (f.Config.ParentDefaultFieldId is { } parent)
            {
                node.Remove("parentDefaultFieldId");
                if (Exported.Contains(parent)) node["parentDefaultField"] = Qualified(parent);
                else Warnings.Add($"「{Label(f)}」帶入的表頭欄位在範本外，未匯出這個設定");
            }
            return JsonSerializer.SerializeToElement(node);
        }

        /// <summary>引用停用或未匯出的欄位、區塊、選項時回 null（整條不匯出）。</summary>
        public TemplateRule? Rule(Module m, Rule r, int index)
        {
            var targets = new List<string>();
            var ok = true;
            foreach (var t in r.Targets)
            {
                if (t.StartsWith("field:") && long.TryParse(t["field:".Length..], out var fid) && Exported.Contains(fid))
                    targets.Add($"field:{Code(fid)}");
                else if (t.StartsWith("block:") && long.TryParse(t["block:".Length..], out var bid) && _blocks.Contains(bid))
                    targets.Add($"block:{_codes.Block(bid)}");
                else if (t == "*") targets.Add(t);
                else ok = false;
            }
            ok &= r.OptionIds.All(_options.Contains);
            var condition = r.Condition.ValueKind == JsonValueKind.Object
                ? Condition(r.Condition)
                : new JsonObject { ["all"] = new JsonArray() };
            if (!ok || condition is null)
            {
                Warnings.Add($"模組「{m.Key}」的規則 {index + 1} 引用了停用或未匯出的欄位，未匯出");
                return null;
            }
            return new TemplateRule(r.Kind, targets, JsonSerializer.SerializeToElement(condition),
                r.OptionIds.Select(_codes.Option).ToList(), r.MessageI18n, r.Active);
        }

        /// <summary>只留匯出的欄與排序；篩選引用到沒匯出的欄位就拿掉篩選。</summary>
        public TemplateView View(ListView v)
        {
            var columns = v.Columns.Where(Exported.Contains).Select(Code).ToList();
            var sort = v.Sort.Where(s => Exported.Contains(s.Field)).Select(s => new TemplateSort(Code(s.Field), s.Order)).ToList();
            var filter = v.Filter is { ValueKind: JsonValueKind.Object } raw ? Condition(raw) : null;
            var filterLost = v.Filter is { ValueKind: JsonValueKind.Object } && filter is null;
            if (columns.Count < v.Columns.Count || sort.Count < v.Sort.Count || filterLost)
                Warnings.Add($"檢視「{v.NameI18n.Resolve(I18nText.FallbackLocale)}」引用了停用或未匯出的欄位，已略過");
            return new TemplateView(v.NameI18n, columns, filter is null ? null : JsonSerializer.SerializeToElement(filter), sort, v.IsDefault);
        }

        /// <summary>條件的 field、fieldRef 換成欄位代號，下拉欄位的 opt_&lt;id&gt; 換成 opt_o_&lt;id&gt;；有解析不了的引用回 null。</summary>
        private JsonNode? Condition(JsonElement el)
        {
            var props = el.EnumerateObject().ToList();
            if (props is [{ Name: "all" or "any", Value.ValueKind: JsonValueKind.Array } group])
            {
                var children = group.Value.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.Object ? Condition(c) : null).ToList();
                return children.Any(c => c is null) ? null : new JsonObject { [group.Name] = new JsonArray([.. children]) };
            }

            var leaf = JsonNode.Parse(el.GetRawText())!.AsObject();
            if (!el.TryGetProperty("field", out var fieldEl) || fieldEl.ValueKind != JsonValueKind.Number || !fieldEl.TryGetInt64(out var id) || !Exported.Contains(id)) return null;
            leaf["field"] = Code(id);
            if (!el.TryGetProperty("value", out var value)) return leaf;
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("fieldRef", out var r))
            {
                if (r.ValueKind != JsonValueKind.Number || !r.TryGetInt64(out var refId) || !Exported.Contains(refId)) return null;
                leaf["value"] = new JsonObject { ["fieldRef"] = Code(refId) };
            }
            else if (value.ValueKind != JsonValueKind.Null && IsOptionField(Fields[id]))
            {
                if (OptionValue(leaf["value"]) is not { } converted) return null;
                leaf["value"] = converted;
            }
            return leaf;
        }

        /// <summary>opt_&lt;id&gt; → opt_o_&lt;id&gt;（陣列逐一）；選項停用或不在套件裡回 null。</summary>
        private JsonNode? OptionValue(JsonNode? node)
        {
            switch (node)
            {
                case JsonArray array:
                    var items = array.Select(OptionValue).ToList();
                    return items.Any(i => i is null) ? null : new JsonArray([.. items]);
                case JsonValue v when v.TryGetValue<string>(out var s) && OptionReference().Match(s) is { Success: true } match:
                    var optionId = long.Parse(match.Groups[1].Value);
                    return _options.Contains(optionId) ? JsonValue.Create($"opt_{_codes.Option(optionId)}") : null;
                default:
                    return node?.DeepClone();
            }
        }

        private static bool IsOptionField(Field f) => f.Type is FieldType.Enum or FieldType.MultiEnum;
    }
}

/// <summary>
/// 匯出時欄位、區塊、選項的代號。Fields／Blocks／Options 是 Id → 代號（沒有的退回 <c>f_&lt;id&gt;</c>／<c>b_&lt;id&gt;</c>／<c>o_&lt;id&gt;</c>，
/// 由呼叫端建立時避開與保留代號的撞名）；Excluded 是不匯出的欄位 Id（例如整合新增的欄位）。
/// </summary>
public sealed class ExportCodes(
    IReadOnlyDictionary<long, string>? fields = null, IReadOnlyDictionary<long, string>? blocks = null,
    IReadOnlyDictionary<long, string>? options = null, IReadOnlySet<long>? excluded = null)
{
    public static readonly ExportCodes Default = new();

    public IReadOnlySet<long> Excluded { get; } = excluded ?? new HashSet<long>();

    public string Field(Field f) => fields is not null && fields.TryGetValue(f.Id, out var code) ? code : f.Key;

    public string Block(long id) => blocks is not null && blocks.TryGetValue(id, out var code) ? code : $"b_{id}";

    public string Option(long id) => options is not null && options.TryGetValue(id, out var code) ? code : $"o_{id}";
}
