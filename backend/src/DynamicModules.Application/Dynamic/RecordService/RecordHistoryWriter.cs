using System.Text.Json;
using DynamicModules.Application.Common.Acting;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.FieldService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.RecordService;

/// <summary>
/// 寫入點呼叫的歷程（第 7 階段決策 1）：只加進 change tracker，由呼叫端的 CommitAsync 一起寫入、失敗時一起回滾。
/// 同一個實例（一次請求）的所有列用同一個時間；ActingScope 換了模擬時間就重取
/// （Demo 產生器在同一個 scope 內依時間軸送多個指令）。
/// </summary>
public class RecordHistoryWriter(IRecordHistoryRepository histories, ICurrentUser user, IBusinessTime time)
{
    private DateTime? _now;
    private DateTime? _actingNow;

    public void Created(Record r) => Add(r, RecordHistoryOperation.Create, null, null, null);

    public void Deleted(Record r) => Add(r, RecordHistoryOperation.Delete, null, null, null);

    /// <summary>每個實際有變的欄位一列；公式、彙總、自動編號不記。鍵不存在與 JSON null 視為相同。</summary>
    public void Updated(Record r, Module module, IReadOnlyDictionary<string, JsonElement?>? before,
        IReadOnlyDictionary<string, JsonElement?> after)
    {
        foreach (var field in module.Blocks.SelectMany(b => b.Fields).Where(f => !FieldRules.IsComputed(f.Type)).OrderBy(f => f.Id))
        {
            var old = RawOf(before, field.Key);
            var @new = RawOf(after, field.Key);
            if (old != @new) Add(r, RecordHistoryOperation.Update, field.Id, old, @new);
        }
    }

    private static string? RawOf(IReadOnlyDictionary<string, JsonElement?>? values, string key) =>
        values is not null && values.TryGetValue(key, out var v) && v is { ValueKind: not JsonValueKind.Null } e ? e.GetRawText() : null;

    private void Add(Record r, RecordHistoryOperation op, long? fieldId, string? oldValue, string? newValue)
    {
        if (_now is null || ActingScope.Now != _actingNow)
        {
            _now = time.Now;
            _actingNow = ActingScope.Now;
        }
        histories.Add(new RecordHistory
        {
            // 新記錄還沒有 Id：掛導覽屬性，由 EF 補上 RecordId
            RecordId = r.Id,
            Record = r.Id == 0 ? r : null,
            FieldId = fieldId,
            Operation = op,
            OldValue = oldValue,
            NewValue = newValue,
            ChangedUserId = user.UserId,
            ChangedTime = _now.Value
        });
    }
}
