using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Domain.Conditions;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.CheckService;

/// <summary>跨記錄驗證定義的結構驗證（設計 §5.1）；錯誤鍵用 definition.* 路徑。</summary>
public class CheckValidator(IModuleRepository modules, FieldTypeRegistry registry)
{
    public const string BadKey = "檢查代碼只能用小寫英文、數字、連字號，2 到 40 字，英文開頭";

    /// <summary>組鍵只接受索引上可靠相等比較的型別；小數、金額、公式、彙總不行（索引是 double）。</summary>
    public static readonly FieldType[] GroupTypes = [FieldType.Reference, FieldType.Enum, FieldType.Text, FieldType.Integer, FieldType.Date];
    public static readonly FieldType[] NumericTypes = [FieldType.Integer, FieldType.Decimal, FieldType.Currency];
    private static readonly string[] Ops = ["gte", "gt", "lte", "lt", "eq"];

    public async Task<ServiceResult?> ValidateAsync(CheckInput input, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        void Err(string key, string message) => errors.TryAdd(key, [message]);

        if (!Regex.IsMatch(input.Key ?? "", ModuleRules.ModuleKeyPattern)) Err("key", BadKey);
        if (input.Name is null || !input.Name.IsValid(out _)) Err("name", "名稱必須有繁體中文");
        if (input.Definition?.Assert?.Limit is null) return ServiceResult.Invalid(new Dictionary<string, string[]> { ["definition"] = ["缺少檢查定義"] });

        var module = await modules.GetByKeyAsync(input.ModuleKey ?? "", includeChildren: true, ct);
        if (module is null || !module.IsWritable() || module.Kind != ModuleKind.Standalone)
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["moduleKey"] = ["模組必須是啟用中的模組（獨立或明細）"] });

        var def = input.Definition;
        var fields = module.Blocks.SelectMany(b => b.Fields).Where(f => f.Status == FieldStatus.Active).ToDictionary(f => f.Id);

        foreach (var id in def.GroupBy ?? [])
            if (!fields.TryGetValue(id, out var g) || !GroupTypes.Contains(g.Type))
            {
                Err("definition.groupBy", "分組欄位必須是本模組的文字、整數、日期、下拉或參照欄位（小數、金額、公式、彙總不能當組鍵）");
                break;
            }
        if ((def.GroupBy ?? []).Distinct().Count() != (def.GroupBy ?? []).Count) Err("definition.groupBy", "分組欄位不能重複");

        if (def.Function == "sum")
        {
            if (def.FieldId is not { } fid || !fields.TryGetValue(fid, out var sum) || !NumericTypes.Contains(sum.Type))
                Err("definition.fieldId", "加總欄位必須是本模組的整數、小數或金額欄位");
        }
        else if (def.Function != "count") Err("definition.function", "函數必須是 sum 或 count");

        if (def.Where is { ValueKind: not JsonValueKind.Null } where)
        {
            if (where.ValueKind != JsonValueKind.Object) Err("definition.where", "條件格式錯誤");
            else if (ContainsFieldRef(where)) Err("definition.where", "檢查的條件不支援欄位比對（fieldRef），請改用固定值");
            else
            {
                var parsed = ConditionParser.Parse(where, module, registry, ConditionUsage.Rule, await modules.ListUserIdsAsync(ct));
                if (parsed.Error is not null) Err("definition.where", $"條件不正確：{parsed.Error}");
            }
        }

        if (!Ops.Contains(def.Assert.Op)) Err("definition.assert.op", "運算子必須是 gte、gt、lte、lt 或 eq");
        await CheckLimitAsync(def.Assert.Limit, fields, errors, ct);

        if (def.Message is null || !def.Message.IsValid(out _)) Err("definition.message", "訊息必須有繁體中文");
        return errors.Count > 0 ? ServiceResult.Invalid(errors) : null;
    }

    private async Task CheckLimitAsync(CheckLimit limit, Dictionary<long, Field> fields, Dictionary<string, string[]> errors, CancellationToken ct)
    {
        void Err(string message) => errors.TryAdd("definition.assert.limit", [message]);
        var forms = (limit.Const is not null ? 1 : 0) + (limit.Field is not null && limit.Via is null ? 1 : 0) + (limit.Via is not null ? 1 : 0);
        if (forms != 1 || limit.Via is not null && limit.Field is null || limit.Const is not null && (limit.Field is not null || limit.Via is not null))
        {
            Err("上限必須且只能是 const、field 或 via＋field 其中一種");
            return;
        }
        if (limit.Via is { } via)
        {
            if (!fields.TryGetValue(via, out var refField) || refField is not { Type: FieldType.Reference }
                || refField.Config.RefTarget is not { Kind: ReferenceTarget.ModuleKind } rt)
            {
                Err("via 必須是本模組的模組參照欄位");
                return;
            }
            var target = await modules.GetByKeyAsync(rt.Key, includeChildren: true, ct);
            var tf = target?.Blocks.SelectMany(b => b.Fields).FirstOrDefault(f => f.Id == limit.Field && f.Status == FieldStatus.Active);
            if (tf is null || !NumericTypes.Contains(tf.Type)) Err("上限欄位必須是參照目標模組的整數、小數或金額欄位");
        }
        else if (limit.Field is { } own && (!fields.TryGetValue(own, out var f) || !NumericTypes.Contains(f.Type)))
            Err("上限欄位必須是本模組的整數、小數或金額欄位");
    }

    /// <summary>結構上有 fieldRef 屬性（頂層或 value 物件）；文字常數剛好寫成 "fieldRef" 不算。</summary>
    private static bool ContainsFieldRef(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().Any(p => p.NameEquals("fieldRef") || ContainsFieldRef(p.Value)),
        JsonValueKind.Array => e.EnumerateArray().Any(ContainsFieldRef),
        _ => false
    };
}
