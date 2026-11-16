using System.Text.Json;
using DynamicModules.Domain.AutoNumbers;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.RecordService;

/// <summary>驗證與公式都通過後才取號，最後才算標題與索引；交易回滾時號碼一併退回。</summary>
public static class RecordAutoNumbers
{
    public const string ParentScope = "parent";

    /// <param name="parentRecordId">明細列所屬表頭的 Id；Scope = parent 的欄位用它當期別（p + 表頭 Id），每張表頭從 1 起算。</param>
    public static async Task AssignAsync(
        INumberSequenceRepository sequences, Module module, Dictionary<string, JsonElement?> values, DateTime now,
        long? parentRecordId, CancellationToken ct)
    {
        foreach (var field in module.Blocks.SelectMany(b => b.Fields)
                     .Where(f => f.Status == FieldStatus.Active && f.Type == FieldType.AutoNumber))
        {
            // 設計器已驗證過格式；解析不了代表設定損毀，不能默默存出沒有編號的記錄
            if (!AutoNumberFormat.TryParse(field.Config.Format, out var format))
                throw new InvalidOperationException($"自動編號欄位 {field.Id} 的格式損毀：{field.Config.Format}");
            var period = field.Config.Scope == ParentScope
                ? $"p{parentRecordId ?? throw new InvalidOperationException($"自動編號欄位 {field.Id} 在表頭內編號，需要表頭 Id")}"
                : format.PeriodKey(now);
            var number = await sequences.NextAsync(module.Id, field.Id, period, ct);
            values[field.Key] = JsonSerializer.SerializeToElement(format.Render(now, number));
        }
    }
}
