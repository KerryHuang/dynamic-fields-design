using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Domain.Conditions;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.Rules;

namespace DynamicModules.Application.Dynamic.CheckService;

/// <summary>
/// 檢查引用的欄位一定要有索引（設計 §5.3）：儲存檢查時自動打開「可篩選」、分批回填既有記錄，
/// 並讓加總欄位的 ScaledValue 完整。回填與儲存檢查同一個交易，持有寫入鎖到結束。
/// </summary>
public class CheckIndexer(
    IRecordRepository records, IModuleRepository modules, IUnitOfWork uow, FieldTypeRegistry registry, ICurrentUser user, IBusinessTime time)
{
    public const int MaxRecords = 200_000;
    public const int BatchSize = 1000;

    /// <summary>需要索引的欄位：組鍵、加總欄位、where 用到的欄位（上限欄位從記錄值讀，不需要）。</summary>
    public static HashSet<long> IndexedFieldIds(CheckDefinition def)
    {
        var ids = new HashSet<long>(def.GroupBy ?? []);
        if (def.FieldId is { } f) ids.Add(f);
        if (def.Where is { ValueKind: System.Text.Json.JsonValueKind.Object } w) ids.UnionWith(RuleReferences.ConditionFieldIdsOf(w));
        return ids;
    }

    public async Task<(ServiceResult? Failure, List<string> Notices)> PrepareAsync(Module module, CheckDefinition def, CancellationToken ct)
    {
        var notices = new List<string>();
        var fields = module.Blocks.SelectMany(b => b.Fields).Where(f => f.Status == FieldStatus.Active).ToDictionary(f => f.Id);
        var used = IndexedFieldIds(def).Where(fields.ContainsKey).Select(id => fields[id]).ToList();

        var whereIds = def.Where is { ValueKind: System.Text.Json.JsonValueKind.Object } w0 ? RuleReferences.ConditionFieldIdsOf(w0) : new HashSet<long>();
        var toBackfill = new List<Field>();
        var enabled = new List<Field>();
        foreach (var field in used)
        {
            if (!FieldTypeCapabilities.SupportsFilter(field.Type))
                return (ServiceResult.Invalid(new Dictionary<string, string[]>
                    { ["definition"] = [$"欄位「{field.LabelI18n.Resolve("zh-TW")}」的型別不支援建立索引，不能用在檢查"] }), notices);
            var needsIndex = !RecordIndexer.ShouldIndex(field);
            // where 走列表篩選的條件規則，欄位一定要可篩選；只可排序的欄位雖然有索引，不開可篩選的話條件之後永遠解析失敗
            if (!field.Config.IsFilterable && (needsIndex || whereIds.Contains(field.Id)))
            {
                field.Config = field.Config with { IsFilterable = true };
                notices.Add($"已自動啟用欄位「{field.LabelI18n.Resolve("zh-TW")}」的可篩選");
                enabled.Add(field);
                if (needsIndex) toBackfill.Add(field);
            }
        }
        // 加總欄位的 ScaledValue 可能是舊資料：升級前建的索引沒有這欄，或欄位在沒有檢查引用時改過小數位數
        // （CheckFieldGuard 只在有檢查引用時才擋）。有缺或小數位不符才重建
        if (def.FieldId is { } sid && fields.TryGetValue(sid, out var sum) && !toBackfill.Contains(sum)
            && await records.NeedsScaledBackfillAsync(sum.Id, ScaledNumber.ScaleOf(sum), ct))
            toBackfill.Add(sum);

        if (toBackfill.Count > 0)
        {
            var count = await records.CountByModuleAsync(module.Id, ct);
            if (count > MaxRecords)
                return (ServiceResult.Conflict($"這個模組有 {count} 筆記錄，超過 {MaxRecords} 筆，一次回填索引會長時間占住寫入鎖；請在低峰時段分次處理"), notices);

            foreach (var field in toBackfill)
                if (await BackfillAsync(module, field, def.FieldId == field.Id, ct) is { } failure)
                    return (failure, notices);
        }
        foreach (var field in toBackfill.Union(enabled)) field.StampModified(user.UserId, time.Now);
        if (toBackfill.Count + enabled.Count > 0) module.TouchVersion(user.UserId, time.Now);

        // 欄位現在可篩選了：用列表篩選的規則再驗一次 where（不論這次有沒有改欄位，執行期就是用這套規則解析）
        if (def.Where is { ValueKind: System.Text.Json.JsonValueKind.Object } where)
        {
            var parsed = ConditionParser.Parse(where, module, registry, ConditionUsage.ListFilter, await modules.ListUserIdsAsync(ct));
            if (parsed.Error is not null)
                return (ServiceResult.Invalid(new Dictionary<string, string[]> { ["definition.where"] = [$"條件不正確：{parsed.Error}"] }), notices);
        }
        return (null, notices);
    }

    /// <summary>
    /// ScaledValue 為 NULL 有兩種原因：放大後超出 64 位元整數，或原值的小數位比欄位目前的設定多（Scale 被調低後留下的舊值）。
    /// 四捨五入後放得下就是後者，訊息要指出真正的原因與出路。
    /// </summary>
    private static string NotExactMessage(long recordId, Field field, System.Text.Json.JsonElement value)
    {
        var label = field.LabelI18n.Resolve("zh-TW");
        return ScaledNumber.Of(field, value) is null
            ? $"記錄 {recordId} 的「{label}」數值太大，無法精確加總；請先修正這筆記錄"
            : $"記錄 {recordId} 的「{label}」數值的小數位超過欄位設定（目前 {ScaledNumber.ScaleOf(field)} 位），無法精確加總；請先修正這筆記錄，或把欄位的小數位調回原值";
    }

    private async Task<ServiceResult?> BackfillAsync(Module module, Field field, bool isSum, CancellationToken ct)
    {
        await records.ClearFieldIndexAsync(field.Id, ct);
        long after = 0;
        while (true)
        {
            var batch = await records.ListBatchByModuleAsync(module.Id, after, BatchSize, ct);
            if (batch.Count == 0) break;
            var rows = new List<RecordIndex>();
            foreach (var record in batch)
            {
                var values = RecordValueProcessor.Parse(record.ValuesJson);
                foreach (var row in RecordIndexer.BuildField(field, values, registry))
                {
                    if (isSum && row.NumberValue is not null && row.ScaledValue is null)
                        return ServiceResult.Conflict(NotExactMessage(record.Id, field, values[field.Key]!.Value));
                    row.RecordId = record.Id;
                    rows.Add(row);
                }
            }
            records.AddIndexRows(rows);
            await uow.CommitAsync(ct);
            records.ReleaseTracked(batch);
            after = batch[^1].Id;
        }
        return null;
    }
}
