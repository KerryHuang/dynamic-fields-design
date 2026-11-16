using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes.Handlers;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.References;

/// <summary>State 為 ok／missing（目標不存在或已刪）／restricted（存在但看不到，不洩漏標題）。</summary>
public record ReferenceInfo(string Key, string? Title, string State)
{
    public const string Ok = "ok";
    public const string Missing = "missing";
    public const string Restricted = "restricted";
}

/// <summary>讀取時解析記錄上的參照欄位：依目標分組，每個目標一次查詢。</summary>
public class ReferenceResolver(
    IModuleRepository modules, IRecordRepository records, ReferenceProviderRegistry providers, IDynamicAccessGuard guard)
{
    /// <summary>鍵是 Record.Id，內層鍵是參照欄位 key；只含使用者看得到且有值的參照欄位。</summary>
    public async Task<IReadOnlyDictionary<long, Dictionary<string, ReferenceInfo>>> ResolveAsync(
        Module module, EffectivePermissions perms, IReadOnlyList<Record> rows, CancellationToken ct)
    {
        var result = new Dictionary<long, Dictionary<string, ReferenceInfo>>();
        var fields = module.Blocks.SelectMany(b => b.Fields)
            .Where(f => f is { Type: FieldType.Reference, Config.RefTarget: not null } && perms.CanSee(f)).ToList();
        if (fields.Count == 0 || rows.Count == 0) return result;

        // (目標, 記錄, 欄位 key, 參照鍵)
        var refs = new List<(ReferenceTarget Target, long RecordId, string FieldKey, string Key)>();
        foreach (var row in rows)
        {
            var values = RecordValueProcessor.Parse(row.ValuesJson);
            foreach (var f in fields)
                if (values.TryGetValue(f.Key, out var v) && v is { ValueKind: not System.Text.Json.JsonValueKind.Null } value
                    && ReferenceHandler.KeyOf(value) is { } key)
                    refs.Add((f.Config.RefTarget!, row.Id, f.Key, key));
        }

        foreach (var group in refs.GroupBy(r => r.Target))
        {
            var keys = group.Select(r => r.Key).Distinct().ToList();
            var infos = group.Key.Kind == ReferenceTarget.EntityKind
                ? await ResolveEntityAsync(group.Key.Key, keys, ct)
                : await ResolveModuleAsync(group.Key.Key, keys, ct);
            foreach (var r in group)
            {
                if (!result.TryGetValue(r.RecordId, out var map)) result[r.RecordId] = map = new();
                map[r.FieldKey] = infos[r.Key];
            }
        }
        return result;
    }

    private async Task<Dictionary<string, ReferenceInfo>> ResolveEntityAsync(string entityKey, List<string> keys, CancellationToken ct)
    {
        var titles = providers.Get(entityKey) is { } p
            ? await p.TitlesAsync(keys, ct)
            : new Dictionary<string, string>();
        return keys.ToDictionary(k => k, k => titles.TryGetValue(k, out var t)
            ? new ReferenceInfo(k, t, ReferenceInfo.Ok) : new ReferenceInfo(k, null, ReferenceInfo.Missing));
    }

    private async Task<Dictionary<string, ReferenceInfo>> ResolveModuleAsync(string moduleKey, List<string> keys, CancellationToken ct)
    {
        var restricted = keys.ToDictionary(k => k, k => new ReferenceInfo(k, null, ReferenceInfo.Restricted));
        var target = await modules.GetByKeyAsync(moduleKey, includeChildren: true, ct);
        if (!target.IsReadable()) return restricted;
        var access = await guard.AuthorizeAsync(target, PermissionAction.Read, ct);
        if (access.Permissions is not { } perms || !target.TitleVisibleTo(perms)) return restricted;

        var ids = keys.Select(k => long.TryParse(k, out var id) ? id : (long?)null).Where(i => i.HasValue).Select(i => i!.Value).ToList();
        var existing = (await records.ListExistingAsync(target.Id, ids, ct)).ToDictionary(r => r.Id);
        return keys.ToDictionary(k => k, k =>
        {
            if (!long.TryParse(k, out var id) || !existing.TryGetValue(id, out var rec))
                return new ReferenceInfo(k, null, ReferenceInfo.Missing);
            return perms.OwnerUserIds is not null && !perms.OwnerUserIds.Contains(rec.OwnerUserId)
                ? new ReferenceInfo(k, null, ReferenceInfo.Restricted)
                : new ReferenceInfo(k, rec.Title, ReferenceInfo.Ok);
        });
    }
}
