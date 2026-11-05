using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddBlock;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddField;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddOption;
using DynamicModules.Application.Dynamic.FieldService.Commands.ReorderFields;
using DynamicModules.Application.Dynamic.FieldService.Commands.UpdateBlock;
using DynamicModules.Application.Dynamic.FieldService.Commands.UpdateField;
using DynamicModules.Application.Dynamic.ModuleService.Commands.CreateModule;
using DynamicModules.Application.Dynamic.ModuleService.Commands.UpdateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.PermissionService.Commands.SaveModulePermissions;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Application.Dynamic.RuleService.Commands.SaveRules;
using DynamicModules.Application.Dynamic.RuleService.Data;
using DynamicModules.Application.Dynamic.TemplateService.Commands.InstantiateTemplate;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Application.Dynamic.ViewService.Commands.CreateView;
using DynamicModules.Application.Dynamic.ViewService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using MediatR;

namespace DynamicModules.Application.Dynamic.TemplateService;

/// <summary>
/// 從範本建立：依序重播設計器指令（決策 3 的十個步驟），每一步把範本代號換成剛配發的 id。
/// 呼叫端要在交易內（InstantiateTemplateRequest 是 ITransactionalRequest）；任何一步失敗就停下並回傳，由外層回滾。
/// </summary>
public sealed class TemplateInstantiator(
    ISender sender, IModuleRepository modules, IPermissionRepository permissions, IListViewRepository views,
    IUnitOfWork uow, FieldTypeRegistry registry, ICurrentUser user, IBusinessTime time)
{
    private const string ExcludeKey = "excludeFields";

    public async Task<ServiceResult> InstantiateAsync(
        TemplatePackage p, IReadOnlyDictionary<string, InstantiateModuleInput> overrides, IReadOnlySet<string> exclude,
        CancellationToken ct)
    {
        var unknownModule = overrides.Keys.FirstOrDefault(c => p.Modules.All(m => m.Code != c));
        if (unknownModule is not null) return Invalid("modules", $"範本沒有模組「{unknownModule}」");
        var graph = TemplateGraph.Build(p);
        var excludeErrors = CheckExclude(p, graph, exclude);
        if (excludeErrors.Count > 0) return ServiceResult.Invalid(new Dictionary<string, string[]> { [ExcludeKey] = [.. excludeErrors] });

        var map = new IdMap(p);
        var kept = p.Modules.ToDictionary(m => m.Code, m => m.Blocks.ToDictionary(b => b.Code,
            b => b.Fields.Where(f => !exclude.Contains(TemplateGraph.Qualify(m.Code, f.Code))).ToList()));
        IEnumerable<(TemplateModule M, TemplateBlock B, TemplateField F)> KeptFields() =>
            from m in p.Modules from b in m.Blocks from f in kept[m.Code][b.Code] select (m, b, f);
        ServiceResult? failed;

        // 1. 建模組（不建預設標題欄位）
        foreach (var m in p.Modules)
        {
            var o = overrides.GetValueOrDefault(m.Code);
            var key = o?.Key ?? m.Key;
            var r = await sender.Send(new CreateModuleRequest(key, o?.Name ?? m.Name, m.Icon, m.Section, m.Kind, m.HostEntityKey,
                CreateTitleField: false), ct);
            if ((failed = Fail(r, m.Code, $"{m.Code}.")) is not null) return failed;
            var created = (ModuleResponse)r.Data!;
            map.Modules[m.Code] = (created.Id, created.Key);
            var block = (await modules.GetByKeyAsync(created.Key, includeChildren: true, ct))!.Blocks.Single();
            map.Blocks[$"{m.Code}.{m.Blocks[0].Code}"] = block.Id;
        }

        // 2. 區塊：預設區塊改成第一個，其餘新增
        foreach (var m in p.Modules)
            for (var i = 0; i < m.Blocks.Count; i++)
            {
                var b = m.Blocks[i];
                if (i > 0)
                {
                    var added = await sender.Send(new AddBlockRequest(map.Key(m.Code), b.Label), ct);
                    if ((failed = Fail(added, m.Code, $"{m.Code}.")) is not null) return failed;
                    map.Blocks[$"{m.Code}.{b.Code}"] = ((BlockResponse)added.Data!).Id;
                }
                if (i > 0 && !b.Collapsed && b.Columns == 2) continue;
                var r = await sender.Send(new UpdateBlockRequest(map.Blocks[$"{m.Code}.{b.Code}"], b.Label, b.Collapsed, b.Columns), ct);
                if ((failed = Fail(r, m.Code, $"{m.Code}.")) is not null) return failed;
            }

        // 3. 非計算欄位與選項（先不帶預設值、表頭欄位）；4. 計算欄位依跨模組依賴排序
        var fieldsByName = KeptFields().ToDictionary(x => TemplateGraph.Qualify(x.M.Code, x.F.Code));
        var computed = graph.ComputedOrder().Where(fieldsByName.ContainsKey).Select(n => fieldsByName[n]);
        foreach (var (m, b, f) in KeptFields().Where(x => !IsComputed(x.F)).Concat(computed))
        {
            var r = await sender.Send(new AddFieldRequest(map.Key(m.Code), map.Blocks[$"{m.Code}.{b.Code}"], f.Type, f.Label,
                map.Config(m.Code, f, withDefaults: false)), ct);
            if ((failed = Fail(r, m.Code, $"{m.Code}.{f.Code}.")) is not null) return failed;
            var fieldId = ((FieldResponse)r.Data!).Id;
            map.Fields[TemplateGraph.Qualify(m.Code, f.Code)] = fieldId;
            foreach (var option in f.Options)
            {
                var added = await sender.Send(new AddOptionRequest(fieldId, option.Label, option.Color), ct);
                if ((failed = Fail(added, m.Code, $"{m.Code}.{f.Code}.")) is not null) return failed;
                map.Options[$"{m.Code}.{option.Code}"] = ((OptionResponse)added.Data!).Id;
            }
        }

        // 5. 預設值與帶表頭的值
        foreach (var (m, b, f) in KeptFields().Where(x => HasDefaults(x.F)))
        {
            var r = await sender.Send(new UpdateFieldRequest(map.Fields[TemplateGraph.Qualify(m.Code, f.Code)], f.Type, f.Label,
                map.Config(m.Code, f, withDefaults: true), map.Blocks[$"{m.Code}.{b.Code}"]), ct);
            if ((failed = Fail(r, m.Code, $"{m.Code}.{f.Code}.")) is not null) return failed;
        }

        // 6. 恢復範本的欄位順序
        foreach (var m in p.Modules)
            foreach (var b in m.Blocks)
            {
                var ids = kept[m.Code][b.Code].Select(f => map.Fields[TemplateGraph.Qualify(m.Code, f.Code)]).ToList();
                var r = await sender.Send(new ReorderFieldsRequest(map.Blocks[$"{m.Code}.{b.Code}"], ids), ct);
                if ((failed = Fail(r, m.Code, $"{m.Code}.")) is not null) return failed;
            }

        // 7. 標題欄位、圖示、分區、名稱
        foreach (var m in p.Modules)
        {
            var r = await sender.Send(new UpdateModuleRequest(map.Key(m.Code), overrides.GetValueOrDefault(m.Code)?.Name ?? m.Name,
                m.Icon, m.Section, m.TitleField is null ? null : map.Fields[TemplateGraph.Qualify(m.Code, m.TitleField)]), ct);
            if ((failed = Fail(r, m.Code, $"{m.Code}.")) is not null) return failed;
        }

        // 8. 規則（引用被排除欄位的整條不建）
        foreach (var m in p.Modules)
        {
            var rules = m.Rules.Where(r => !RuleTouches(m, r, exclude, map)).Select(r => map.Rule(m.Code, r)).ToList();
            if (rules.Count == 0) continue;
            var res = await sender.Send(new SaveRulesRequest(map.Key(m.Code), rules), ct);
            if ((failed = Fail(res, m.Code, $"{m.Code}.rules.")) is not null) return failed;
        }

        // 9. 共用檢視
        foreach (var m in p.Modules)
            foreach (var v in m.Views)
                if ((failed = await CreateViewAsync(m, v, exclude, map, ct)) is not null) return failed;

        // 10. 權限建議（目標庫沒有的群組略過並警告）
        var warnings = new List<string>();
        var groupIds = (await permissions.ListGroupsAsync(ct)).ToDictionary(g => g.Key, g => g.Id);
        foreach (var m in p.Modules.Where(m => m.Permissions.Count > 0))
        {
            var groups = new List<GroupPermissionDto>();
            foreach (var perm in m.Permissions)
                if (groupIds.TryGetValue(perm.Group, out var id)) groups.Add(new GroupPermissionDto(id, perm.Privileges, perm.Scope));
                else warnings.Add($"群組「{perm.Group}」不存在，已略過它的權限建議");
            if (groups.Count == 0) continue;
            var r = await sender.Send(new SaveModulePermissionsRequest(map.Key(m.Code), new ModulePermissionsDto(groups, [], [], [])), ct);
            if ((failed = Fail(r, m.Code, $"{m.Code}.")) is not null) return failed;
        }

        return ServiceResult.Created(new InstantiateTemplateResponse(
            p.Modules.Select(m => new InstantiatedModule(m.Code, map.Key(m.Code), map.Modules[m.Code].Id)).ToList(), warnings));
    }

    /// <summary>
    /// CreateView 只接受已啟用的模組（它是記錄端的指令，要模組授權），草稿用不了；
    /// 這裡用同一個驗證器與 ViewRules.Check 檢查後直接寫入共用檢視。
    /// </summary>
    private async Task<ServiceResult?> CreateViewAsync(
        TemplateModule m, TemplateView v, IReadOnlySet<string> exclude, IdMap map, CancellationToken ct)
    {
        bool Kept(string code) => !exclude.Contains(TemplateGraph.Qualify(m.Code, code));
        var filterTouches = v.Filter is { } raw && TemplateGraph.ConditionLeaves(raw)
            .Any(l => TemplateGraph.LeafField(l) is { } f && !Kept(f) || TemplateGraph.LeafFieldRef(l) is { } r && !Kept(r));
        JsonElement? filter = v.Filter is { ValueKind: JsonValueKind.Object } && !filterTouches
            ? JsonSerializer.SerializeToElement(map.Condition(m.Code, JsonNode.Parse(v.Filter.Value.GetRawText())))
            : null;
        var request = new CreateViewRequest(map.Key(m.Code), v.Name, Shared: true,
            v.Columns.Where(Kept).Select(c => map.Fields[TemplateGraph.Qualify(m.Code, c)]).ToList(), filter,
            v.Sort.Where(s => Kept(s.Field)).Select(s => new ViewSort(map.Fields[TemplateGraph.Qualify(m.Code, s.Field)], s.Order)).ToList(),
            v.IsDefault);

        var shape = await new CreateViewValidator().ValidateAsync(request, ct);
        var errors = shape.Errors.GroupBy(e => e.PropertyName).ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).Distinct().ToArray());
        var module = (await modules.GetByKeyAsync(request.ModuleKey, includeChildren: true, ct))!;
        if (errors.Count == 0)
            errors = ViewRules.Check(module, registry, await modules.ListUserIdsAsync(ct), request.Columns, request.Filter, request.Sort);
        if (errors.Count > 0) return Fail(ServiceResult.Invalid(errors), m.Code, $"{m.Code}.views.");

        var now = time.Now;
        if (request.IsDefault) await views.ClearDefaultsAsync(module.Id, null, except: null, user.UserId, now, ct);
        var view = new ListView
        {
            ModuleId = module.Id, OwnerUserId = null, NameI18n = request.Name, Columns = request.Columns,
            Filter = ViewRules.NormalizeFilter(request.Filter), Sort = request.Sort, IsDefault = request.IsDefault
        };
        view.StampCreated(user.UserId, now);
        views.Add(view);
        await uow.CommitAsync(ct);
        return null;
    }

    /// <summary>鎖定的欄位不能排除；保留的欄位依賴被排除的欄位時要一起排除。</summary>
    private static List<string> CheckExclude(TemplatePackage p, TemplateGraph graph, IReadOnlySet<string> exclude)
    {
        var fields = TemplateGraph.AllFields(p);
        string Label(string name) => fields[name].Label.Resolve(I18nText.FallbackLocale);
        var errors = new List<string>();
        foreach (var name in exclude)
        {
            if (!fields.ContainsKey(name)) errors.Add($"範本沒有欄位「{name}」");
            else if (graph.IsLocked(name)) errors.Add($"「{Label(name)}」是必要欄位，不能排除");
        }
        foreach (var name in fields.Keys.Where(n => !exclude.Contains(n)))
            foreach (var dep in graph.DependsOn(name).Where(exclude.Contains))
                errors.Add($"「{Label(name)}」引用了「{Label(dep)}」，要一起排除");
        return errors;
    }

    /// <summary>規則的條件、目標或選項任何一處引用被排除的欄位（或它的選項）。區塊目標不受影響。</summary>
    private static bool RuleTouches(TemplateModule m, TemplateRule r, IReadOnlySet<string> exclude, IdMap map)
    {
        bool Excluded(string? code) => code is not null && exclude.Contains(TemplateGraph.Qualify(m.Code, code));
        return r.Targets.Any(t => t.StartsWith("field:") && Excluded(t["field:".Length..]))
            || r.Options.Any(o => map.OptionOwner.TryGetValue($"{m.Code}.{o}", out var owner) && exclude.Contains(owner))
            || TemplateGraph.ConditionLeaves(r.Condition).Any(l => Excluded(TemplateGraph.LeafField(l)) || Excluded(TemplateGraph.LeafFieldRef(l)));
    }

    private static bool IsComputed(TemplateField f) => f.Type is FieldType.Formula or FieldType.Rollup;

    private static bool HasDefaults(TemplateField f) =>
        f.Config.ValueKind == JsonValueKind.Object
        && (f.Config.TryGetProperty("defaultValue", out var d) && d.ValueKind != JsonValueKind.Null
            || TemplateGraph.ConfigString(f.Config, "parentDefaultField") is not null);

    /// <summary>內層指令失敗：400 的鍵加前綴，404／409 的訊息前面加模組代號，其他原樣。</summary>
    private static ServiceResult? Fail(ServiceResult r, string moduleCode, string keyPrefix)
    {
        if (r.IsSuccess) return null;
        if (r.IsForbidden) return r;
        var message = $"模組「{moduleCode}」：{r.Message}";
        if (r.IsNotFound) return ServiceResult.NotFound(message);
        if (r.IsConflict) return r.Data is null ? ServiceResult.Conflict(message) : ServiceResult.ConflictWith(message, r.Data);
        return ServiceResult.Invalid(r.Errors.ToDictionary(e => keyPrefix + e.Key, e => e.Value));
    }

    private static ServiceResult Invalid(string key, string message) =>
        ServiceResult.Invalid(new Dictionary<string, string[]> { [key] = [message] });

    /// <summary>範本代號對剛配發的 id；欄位一律用限定名，選項、區塊用「模組代號.代號」（選項代號在模組內唯一）。</summary>
    private sealed class IdMap(TemplatePackage p)
    {
        private readonly Dictionary<string, TemplateField> _templateFields = TemplateGraph.AllFields(p);

        public Dictionary<string, (long Id, string Key)> Modules { get; } = new();
        public Dictionary<string, long> Fields { get; } = new();
        public Dictionary<string, long> Options { get; } = new();
        public Dictionary<string, long> Blocks { get; } = new();

        /// <summary>「模組代號.選項代號」對擁有它的欄位限定名。</summary>
        public Dictionary<string, string> OptionOwner { get; } = (
            from m in p.Modules from f in m.Blocks.SelectMany(b => b.Fields) from o in f.Options
            select (Key: $"{m.Code}.{o.Code}", Owner: TemplateGraph.Qualify(m.Code, f.Code)))
            .DistinctBy(x => x.Key).ToDictionary(x => x.Key, x => x.Owner);

        public string Key(string moduleCode) => Modules[moduleCode].Key;

        /// <summary>範本的 config 換成 FieldConfig；withDefaults 為 false 時拿掉 defaultValue、parentDefaultField。</summary>
        public FieldConfig Config(string m, TemplateField f, bool withDefaults)
        {
            var node = JsonNode.Parse(f.Config.GetRawText()) as JsonObject ?? new JsonObject();
            if (!withDefaults)
            {
                node.Remove("defaultValue");
                node.Remove("parentDefaultField");
            }
            // formula、rollup 只在公式、彙總欄位才是引用（TemplateGraph 的定義）
            if (TemplateGraph.FormulaOf(f) is not null && Str(node["formula"]) is { } formula)
                node["formula"] = TemplateGraph.FormulaReference().Replace(formula, x => $"{{{Fields[TemplateGraph.Qualify(m, x.Groups[1].Value)]}}}");
            if (node["defaultValue"] is { } dv && TemplateGraph.IsOptionField(f)) node["defaultValue"] = OptionValue(m, dv);
            if (node["refTarget"] is JsonObject target && Str(target["kind"]) == ReferenceTarget.ModuleKind
                && Str(target["key"]) is { } targetKey && Modules.TryGetValue(targetKey, out var targetModule))
                target["key"] = targetModule.Key;
            if (TemplateGraph.RollupOf(f) is not null && node["rollup"] is JsonObject rollup && Str(rollup["sourceField"]) is { } source)
            {
                var converted = new JsonObject { ["sourceFieldId"] = Fields[source], ["function"] = rollup["function"]?.DeepClone() };
                if (Str(rollup["aggregateField"]) is { } aggregate) converted["aggregateFieldId"] = Fields[aggregate];
                if (rollup["condition"] is JsonObject condition && TemplateGraph.SplitQualified(source) is { } src)
                    converted["condition"] = Condition(src.Module, condition);
                node["rollup"] = converted;
            }
            if (Str(node["parentDefaultField"]) is { } parent)
            {
                node.Remove("parentDefaultField");
                node["parentDefaultFieldId"] = Fields[parent];
            }
            return node.Deserialize<FieldConfig>(TemplateJson.Options)!;
        }

        public RuleInput Rule(string m, TemplateRule r)
        {
            var targets = r.Targets.Select(t =>
                t.StartsWith("field:") ? $"field:{Fields[TemplateGraph.Qualify(m, t["field:".Length..])]}"
                : t.StartsWith("block:") ? $"block:{Blocks[$"{m}.{t["block:".Length..]}"]}"
                : t).ToList();
            // 沒有條件＝永遠成立，SaveRules 要一個物件：空的 all 群組
            var condition = r.Condition.ValueKind == JsonValueKind.Object
                ? Condition(m, JsonNode.Parse(r.Condition.GetRawText()))
                : new JsonObject { ["all"] = new JsonArray() };
            return new RuleInput(r.Kind, JsonSerializer.SerializeToElement(condition), targets,
                r.Options.Select(o => Options[$"{m}.{o}"]).ToList(), r.Message, r.Active);
        }

        /// <summary>條件裡的欄位、fieldRef 換成 id；下拉欄位的 opt_&lt;代號&gt; 換成 opt_&lt;id&gt;。m 是條件欄位所在的模組。</summary>
        public JsonNode? Condition(string m, JsonNode? node)
        {
            if (node is not JsonObject obj) return node?.DeepClone();
            if (obj.Count == 1 && obj.First() is { Key: "all" or "any", Value: JsonArray children } group)
                return new JsonObject { [group.Key] = new JsonArray(children.Select(c => Condition(m, c)).ToArray()) };

            var leaf = (JsonObject)obj.DeepClone();
            var field = Str(obj["field"]) is { } code ? TemplateGraph.Qualify(m, code) : null;
            if (field is not null) leaf["field"] = Fields[field];
            if (obj["value"] is JsonObject value && Str(value["fieldRef"]) is { } refCode)
                leaf["value"] = new JsonObject { ["fieldRef"] = Fields[TemplateGraph.Qualify(m, refCode)] };
            else if (obj["value"] is { } v && field is not null && _templateFields.TryGetValue(field, out var tf) && TemplateGraph.IsOptionField(tf))
                leaf["value"] = OptionValue(m, v);
            return leaf;
        }

        private JsonNode? OptionValue(string m, JsonNode node) => node switch
        {
            JsonArray array => new JsonArray(array.Select(e => e is null ? null : OptionValue(m, e)).ToArray()),
            JsonValue when Str(node) is { } s && TemplateGraph.OptionReference().Match(s) is { Success: true } match
                && Options.TryGetValue($"{m}.{match.Groups[1].Value}", out var id) => JsonValue.Create($"opt_{id}"),
            _ => node.DeepClone()
        };

        private static string? Str(JsonNode? node) =>
            node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }
}
