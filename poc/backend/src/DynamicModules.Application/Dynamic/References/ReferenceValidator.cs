using System.Text.Json;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes.Handlers;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.References;

/// <summary>寫入前查出每個「有變動」的參照欄位的合法鍵集合（已定決策 4），結果交給 RecordValueProcessor。</summary>
public class ReferenceValidator(
    IModuleRepository modules, IRecordRepository records, ReferenceProviderRegistry providers, IDynamicAccessGuard guard)
{
    /// <summary>鍵是 Field.Id；只含有送值、值非 null、且和 previous 不同的啟用中參照欄位。目標模組未啟用或 Read 被拒時集合為空。</summary>
    public async Task<IReadOnlyDictionary<long, IReadOnlySet<string>>> ValidKeysAsync(
        Module module, IReadOnlyDictionary<string, JsonElement?> input,
        IReadOnlyDictionary<string, JsonElement?>? previous, CancellationToken ct)
    {
        var result = new Dictionary<long, IReadOnlySet<string>>();
        var fields = module.Blocks.SelectMany(b => b.Fields)
            .Where(f => f is { Type: FieldType.Reference, Status: FieldStatus.Active, Config.RefTarget: not null });
        foreach (var field in fields)
        {
            if (!input.TryGetValue(field.Key, out var raw) || raw is not { ValueKind: not JsonValueKind.Null } value) continue;
            var key = ReferenceHandler.KeyOf(value);
            if (key is null) continue; // 型別不對，由 ReferenceHandler 回錯
            if (previous is not null && previous.TryGetValue(field.Key, out var prev) && prev is { } p && ReferenceHandler.KeyOf(p) == key) continue;
            result[field.Id] = await ResolveAsync(field.Config.RefTarget!, field.Config.RefGuard, key, ct);
        }
        return result;
    }

    private async Task<IReadOnlySet<string>> ResolveAsync(ReferenceTarget target, RefGuardConfig? refGuard, string key, CancellationToken ct)
    {
        if (target.Kind == ReferenceTarget.EntityKind)
        {
            var provider = providers.Get(target.Key);
            return provider is null ? new HashSet<string>() : (await provider.TitlesAsync([key], ct)).Keys.ToHashSet();
        }

        var targetModule = await modules.GetByKeyAsync(target.Key, includeChildren: true, ct);
        // 值可能是數字或字串形式的 Id（"123"）；字串轉不成整數就沒有合法鍵，由 ReferenceHandler 回 400（不能對字串呼叫 TryGetInt64）
        if (!targetModule.IsWritable()
            || !long.TryParse(key, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id))
            return new HashSet<string>();
        var access = await guard.AuthorizeAsync(targetModule, PermissionAction.Read, ct);
        if (access.Permissions is not { } perms) return new HashSet<string>();
        var visible = await records.ListVisibleIdsAsync(targetModule.Id, [id], perms.OwnerUserIds, ct);
        if (refGuard is not null && visible.Count > 0 && !await InAllowedStateAsync(targetModule, refGuard, id, ct)) return ReferenceHandler.RejectedByGuard;
        return visible.Select(v => v.ToString()).ToHashSet();
    }

    /// <summary>目標記錄的守衛欄位值是否在允許的選項內；守衛欄位已不存在或停用時一律不允許（fail closed）。</summary>
    private async Task<bool> InAllowedStateAsync(Module targetModule, RefGuardConfig guard, long id, CancellationToken ct)
    {
        var status = targetModule.Blocks.SelectMany(b => b.Fields).FirstOrDefault(f => f.Id == guard.FieldId && f.Status == FieldStatus.Active);
        if (status is null) return false;
        var rows = await records.ListByIdsAsync(targetModule.Id, [id], ct);
        if (rows.Count == 0) return false;
        using var doc = JsonDocument.Parse(rows[0].ValuesJson);
        if (!doc.RootElement.TryGetProperty(status.Key, out var v) || v.ValueKind != JsonValueKind.String) return false;
        return guard.OptionIds.Any(o => v.GetString() == $"opt_{o}");
    }
}
