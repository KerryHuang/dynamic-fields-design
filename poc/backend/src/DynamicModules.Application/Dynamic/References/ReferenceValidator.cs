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
            result[field.Id] = await ResolveAsync(field.Config.RefTarget!, key, value, ct);
        }
        return result;
    }

    private async Task<IReadOnlySet<string>> ResolveAsync(ReferenceTarget target, string key, JsonElement value, CancellationToken ct)
    {
        if (target.Kind == ReferenceTarget.EntityKind)
        {
            var provider = providers.Get(target.Key);
            return provider is null ? new HashSet<string>() : (await provider.TitlesAsync([key], ct)).Keys.ToHashSet();
        }

        var targetModule = await modules.GetByKeyAsync(target.Key, includeChildren: true, ct);
        if (!targetModule.IsWritable() || !value.TryGetInt64(out var id)) return new HashSet<string>();
        var access = await guard.AuthorizeAsync(targetModule, PermissionAction.Read, ct);
        if (access.Permissions is not { } perms) return new HashSet<string>();
        var visible = await records.ListVisibleIdsAsync(targetModule.Id, [id], perms.OwnerUserIds, ct);
        return visible.Select(v => v.ToString()).ToHashSet();
    }
}
