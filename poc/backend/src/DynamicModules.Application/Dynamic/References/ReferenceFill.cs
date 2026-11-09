using System.Text.Json;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes.Handlers;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.References;

/// <summary>
/// 選參照後帶值（Config.Fill）。在 RecordValueProcessor.Process 之前呼叫：依參照欄位的變動，把目標記錄的欄位值寫進 input，
/// 並回傳這些「由系統填入」的欄位鍵。語意是快照：只在參照被設定或改成另一筆的那一刻複製，之後目標怎麼變都不影響本筆。
/// 複製時用使用者對目標模組的有效權限，看不到的來源欄位當成空值（lock）或略過（default）。
/// </summary>
public class ReferenceFill(IModuleRepository modules, IRecordRepository records, IDynamicAccessGuard guard)
{
    /// <param name="input">呼叫端持有的可變副本；lock 欄位的使用者輸入一律移除，換成帶入的值。</param>
    /// <param name="userCanWrite">使用者自己能不能寫該欄位；參照欄位本身寫不進去（唯讀、唯讀狀態）時不帶值，但 lock 欄位的輸入仍然移除。</param>
    /// <param name="validRefKeys">ReferenceValidator 的結果：有變動且使用者可讀的參照鍵。</param>
    public async Task<IReadOnlySet<string>> ApplyAsync(
        Module module, Dictionary<string, JsonElement?> input, IReadOnlyDictionary<string, JsonElement?>? previous,
        IReadOnlyDictionary<long, IReadOnlySet<string>> validRefKeys, Func<Field, bool> userCanWrite, CancellationToken ct)
    {
        var systemKeys = new HashSet<string>();
        var fields = module.Blocks.SelectMany(b => b.Fields).Where(f => f.Status == FieldStatus.Active).ToList();
        foreach (var reference in fields.Where(f => f is { Type: FieldType.Reference, Config: { Fill: not null, RefTarget.Kind: ReferenceTarget.ModuleKind } }))
        {
            var fill = reference.Config.Fill!;
            var maps = fill.Map
                .Select(m => (To: fields.FirstOrDefault(f => f.Id == m.To), m.From))
                .Where(x => x.To is not null).ToList();
            var isLock = fill.Mode == FillConfig.Lock;

            IReadOnlyDictionary<string, JsonElement?>? sourceValues = null;
            Module? sourceModule = null;
            EffectivePermissions? sourcePerms = null;
            var changed = false;
            var cleared = false;
            var refWritable = userCanWrite(reference);

            if (refWritable && validRefKeys.TryGetValue(reference.Id, out var valid)
                && input.TryGetValue(reference.Key, out var raw) && raw is { } rawValue && ReferenceHandler.KeyOf(rawValue) is { } rawKey
                && long.TryParse(rawKey, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var sourceId)
                && valid.Contains(sourceId.ToString())) // "0042" 存成 42，鍵也要用整數的正規形式比對（第三輪 2）
            {
                sourceModule = await modules.GetByKeyAsync(reference.Config.RefTarget!.Key, includeChildren: true, ct);
                if (sourceModule is { Status: ModuleStatus.Active })
                {
                    var access = await guard.AuthorizeAsync(sourceModule, PermissionAction.Read, ct);
                    sourcePerms = access.Permissions;
                    var record = sourcePerms is null ? null : await records.GetAsync(sourceId, ct);
                    if (record is not null && record.ModuleId == sourceModule.Id)
                    {
                        sourceValues = RecordValueProcessor.Parse(record.ValuesJson);
                        changed = true;
                    }
                }
            }
            else if (refWritable && input.TryGetValue(reference.Key, out var after) && IsEmpty(after)
                     && previous is not null && previous.TryGetValue(reference.Key, out var before) && !IsEmpty(before))
                cleared = true;

            foreach (var (to, fromId) in maps)
            {
                var toKey = to!.Key;
                if (changed)
                {
                    var from = sourceModule!.Blocks.SelectMany(b => b.Fields).FirstOrDefault(f => f.Id == fromId && f.Status == FieldStatus.Active);
                    JsonElement? value = from is not null && sourcePerms!.CanSee(from) && sourceValues!.TryGetValue(from.Key, out var v) && !IsEmpty(v)
                        ? v : null;
                    if (isLock)
                    {
                        input[toKey] = value;
                        systemKeys.Add(toKey);
                    }
                    else if (value is not null)
                    {
                        input.TryGetValue(toKey, out var typed);
                        JsonElement? old = null;
                        var untouched = IsEmpty(typed) || (previous is not null && previous.TryGetValue(toKey, out old) && Same(typed, old));
                        if (!untouched) continue;
                        input[toKey] = value;
                        systemKeys.Add(toKey);
                    }
                }
                else if (cleared && isLock)
                {
                    input[toKey] = null;
                    systemKeys.Add(toKey);
                }
                else if (isLock)
                    input.Remove(toKey); // 參照沒變：lock 欄位維持原值，使用者送的值不採用
            }
        }
        return systemKeys;
    }

    private static bool IsEmpty(JsonElement? v) => v is null || v.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
        || (v.Value.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(v.Value.GetString()));

    /// <summary>以值比較（已存的 JSON 會跳脫非 ASCII 字元，不能比原文）。</summary>
    private static bool Same(JsonElement? a, JsonElement? b)
    {
        if (a is not { } x || b is not { } y || x.ValueKind != y.ValueKind) return false;
        return x.ValueKind switch
        {
            JsonValueKind.String => x.GetString() == y.GetString(),
            JsonValueKind.Number => x.GetDecimal() == y.GetDecimal(),
            JsonValueKind.True or JsonValueKind.False => true,
            _ => x.GetRawText() == y.GetRawText()
        };
    }
}
