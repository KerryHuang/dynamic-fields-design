using System.Globalization;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.EventService;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.LedgerService;

public sealed record ReversalPlan(
    Dictionary<string, JsonElement?> Values, Dictionary<string, List<DetailRowInput>> Details,
    Dictionary<string, string[]> Errors, string? Conflict)
{
    public bool Failed => Errors.Count > 0 || Conflict is not null;
}

/// <summary>
/// 算出沖銷分錄的值（設計 §6.3）：複製原分錄，negate 取負、swap 互換，明細列同樣處理；公式、彙總、自動編號不複製（照定義重算）。
/// 使用者沖銷與事件的 reverse 效果共用：事件以系統身分執行（perms 為 null，不受欄位可見性限制）。
/// </summary>
public class LedgerReverser(
    IModuleRepository modules, IRecordRepository records, ICheckRepository checks, IEventRepository events, IDynamicAccessGuard guard)
{
    public const string HiddenNeeded = "你沒有權限看到沖銷所需的欄位";
    public const string SelfCheckFailed = "沖銷分錄與原分錄不一致，已取消";

    private static readonly FieldType[] Descriptive =
    [
        FieldType.Text, FieldType.Textarea, FieldType.Date, FieldType.DateTime, FieldType.Time, FieldType.Boolean,
        FieldType.Email, FieldType.Phone, FieldType.Url, FieldType.Attachment
    ];
    private static readonly FieldType[] Numeric = [FieldType.Integer, FieldType.Decimal, FieldType.Currency, FieldType.Percent];

    public async Task<ReversalPlan> PlanAsync(
        Module module, ModuleLedger ledger, Record original, EffectivePermissions? perms,
        IReadOnlyDictionary<string, JsonElement?>? overrides, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var def = ledger.Definition;
        var fields = Active(module);
        var negate = def.Negate.ToHashSet();
        var swapOf = new Dictionary<long, long>();
        foreach (var pair in def.Swap) { swapOf[pair[0]] = pair[1]; swapOf[pair[1]] = pair[0]; }
        var reversalKey = $"f_{def.ReversalFieldId}";

        // 決定「抵銷哪個組、抵銷多少」的欄位：被檢查或事件引用、negate、swap、沖銷欄位。使用者必須都看得到，也都不能覆寫
        var guarded = new HashSet<long>(negate.Concat(swapOf.Keys)) { def.ReversalFieldId };
        foreach (var c in await checks.ListByModuleAsync(module.Id, ct)) guarded.UnionWith(EffectReferences.Of(c));
        foreach (var e in await events.ListBySourceAsync(module.Id, ct)) guarded.UnionWith(EffectReferences.Of(e, module.Key).FieldIds);

        var needed = fields.Values.Where(f => guarded.Contains(f.Id) && f.Id != def.ReversalFieldId);
        if (perms is not null && needed.Any(f => !perms.CanSee(f)))
            return new ReversalPlan([], [], new() { [RecordValueProcessor.RecordErrorKey] = [HiddenNeeded] }, null);

        var source = RecordValueProcessor.Parse(original.ValuesJson);
        var values = new Dictionary<string, JsonElement?>();
        foreach (var f in fields.Values)
        {
            if (FieldRulesComputed(f.Type) || f.Id == def.ReversalFieldId) continue;
            if (perms is not null && !perms.CanSee(f)) continue;
            // 互換要在「自己是空值」的判斷之前：這一邊空、另一邊有值時，這一邊要拿到另一邊的值；另一邊空則這一邊清空
            if (swapOf.TryGetValue(f.Id, out var other))
            {
                values[f.Key] = source.TryGetValue($"f_{other}", out var o) && o is { ValueKind: not JsonValueKind.Null } ? o : null;
                continue;
            }
            if (!source.TryGetValue(f.Key, out var v) || v is not { ValueKind: not JsonValueKind.Null }) continue;
            values[f.Key] = negate.Contains(f.Id) ? Negated(v.Value) : v;
        }
        values[reversalKey] = JsonDocument.Parse(original.Id.ToString(CultureInfo.InvariantCulture)).RootElement.Clone();

        foreach (var (key, value) in overrides ?? new Dictionary<string, JsonElement?>())
        {
            var field = fields.Values.FirstOrDefault(f => f.Key == key);
            if (field is null) errors[$"values.{key}"] = ["欄位不存在或未啟用"];
            else if (guarded.Contains(field.Id) || !Descriptive.Contains(field.Type))
                errors[$"values.{key}"] = ["沖銷必須精確抵銷原分錄，這個欄位不能覆寫；只能覆寫說明性欄位（文字、日期、布林、附件等）"];
            else if (perms is not null && !perms.CanWrite(field)) errors[$"values.{key}"] = ["沒有權限修改這個欄位"];
            else values[key] = value;
        }
        if (errors.Count > 0) return new ReversalPlan([], [], errors, null);

        var (details, hiddenDetail) = await DetailsAsync(module, original, negate, swapOf, perms, ct);
        if (hiddenDetail) return new ReversalPlan([], [], new() { [RecordValueProcessor.RecordErrorKey] = [HiddenNeeded] }, null);
        if (!SelfCheck(fields, source, values, negate, swapOf)) return new ReversalPlan([], [], [], SelfCheckFailed);
        return new ReversalPlan(values, details, [], null);
    }

    /// <summary>
    /// 明細列同樣複製：negate 取負、swap 互換。使用者看不到（或整個明細模組沒有讀取權限）決定抵銷的 negate／swap 欄位時，
    /// 沖銷無法精確抵銷，回 hiddenNeeded（整個請求 400），不是略過那個欄位。
    /// </summary>
    private async Task<(Dictionary<string, List<DetailRowInput>> Details, bool HiddenNeeded)> DetailsAsync(
        Module header, Record original, HashSet<long> negate, Dictionary<long, long> swapOf, EffectivePermissions? perms, CancellationToken ct)
    {
        var result = new Dictionary<string, List<DetailRowInput>>();
        foreach (var (child, parentField) in await modules.ListDetailModulesAsync(header.Key, ct))
        {
            var childModule = await modules.GetByIdAsync(child.Id, includeChildren: true, ct);
            if (childModule is null || !childModule.IsWritable()) continue;
            var fields = Active(childModule);
            var decisive = fields.Values.Where(f => negate.Contains(f.Id) || swapOf.ContainsKey(f.Id)).ToList();
            EffectivePermissions? childPerms = null;
            if (perms is not null)
            {
                var access = await guard.AuthorizeDetailAsync(header, childModule, PermissionAction.Read, ct);
                if ((childPerms = access.Permissions) is null)
                {
                    if (decisive.Count > 0) return ([], true);
                    continue;
                }
                if (decisive.Any(f => !childPerms.CanSee(f))) return ([], true);
            }
            var rows = (await records.ListByRefAsync(parentField.Id, original.Id.ToString(CultureInfo.InvariantCulture), ct))
                .Where(r => !r.DeleteFlag).OrderBy(r => r.Id);
            var list = new List<DetailRowInput>();
            foreach (var row in rows)
            {
                var src = RecordValueProcessor.Parse(row.ValuesJson);
                var vals = new Dictionary<string, JsonElement?>();
                foreach (var f in fields.Values.Where(f => !FieldRulesComputed(f.Type) && f.Id != parentField.Id))
                {
                    if (childPerms is not null && !childPerms.CanSee(f)) continue;
                    if (swapOf.TryGetValue(f.Id, out var other))
                    {
                        vals[f.Key] = src.TryGetValue($"f_{other}", out var o) && o is { ValueKind: not JsonValueKind.Null } ? o : null;
                        continue;
                    }
                    if (!src.TryGetValue(f.Key, out var v) || v is not { ValueKind: not JsonValueKind.Null }) continue;
                    vals[f.Key] = negate.Contains(f.Id) ? Negated(v.Value) : v;
                }
                // 這些值是照原分錄鏡射出來的，使用者看得到才會走到這裡；欄位對他唯讀也要照寫，不然預設值會蓋掉沖銷值
                list.Add(new DetailRowInput(null, vals, null, SystemKeys: vals.Keys.ToHashSet()));
            }
            if (list.Count > 0) result[child.Key] = list;
        }
        return (result, false);
    }

    /// <summary>逐一檢查：negate 欄位 = −原值、swap 欄位已互換、其餘輸入型數字欄位與原值相同；公式、彙總、自動編號不比較（照公式重算）。</summary>
    private static bool SelfCheck(
        Dictionary<long, Field> fields, IReadOnlyDictionary<string, JsonElement?> source, Dictionary<string, JsonElement?> built,
        HashSet<long> negate, Dictionary<long, long> swapOf)
    {
        decimal? Num(IReadOnlyDictionary<string, JsonElement?> d, string key) =>
            d.TryGetValue(key, out var v) && v is { ValueKind: JsonValueKind.Number } n ? n.GetDecimal() : null;
        foreach (var f in fields.Values.Where(f => Numeric.Contains(f.Type) && !FieldRulesComputed(f.Type)))
        {
            var original = Num(source, f.Key);
            var actual = Num(built, f.Key);
            decimal? expected = swapOf.TryGetValue(f.Id, out var other) ? Num(source, $"f_{other}")
                : negate.Contains(f.Id) ? -original : original;
            if (expected != actual && !(built.ContainsKey(f.Key) is false && expected is null)) return false;
        }
        return true;
    }

    private static JsonElement Negated(JsonElement v) =>
        v.ValueKind == JsonValueKind.Number
            ? JsonDocument.Parse((-v.GetDecimal()).ToString(CultureInfo.InvariantCulture)).RootElement.Clone()
            : v;

    private static bool FieldRulesComputed(FieldType t) => t is FieldType.Formula or FieldType.Rollup or FieldType.AutoNumber;

    private static Dictionary<long, Field> Active(Module m) =>
        m.Blocks.SelectMany(b => b.Fields).Where(f => f.Status == FieldStatus.Active).ToDictionary(f => f.Id);
}
