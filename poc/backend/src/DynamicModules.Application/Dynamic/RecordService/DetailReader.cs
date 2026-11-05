using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.References;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.RecordService;

/// <summary>表頭回應裡的明細（已定決策 3）：每個啟用中明細模組的未刪除列，依 Id 遞增，照明細模組的權限過濾欄位。</summary>
public class DetailReader(IModuleRepository modules, IRecordRepository records, IDynamicAccessGuard guard, ReferenceResolver resolver)
{
    /// <returns>表頭沒有啟用中的明細模組時回 null（回應省略 details）。</returns>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<DynamicRecordResponse>>?> ReadAsync(
        Module parent, Record parentRecord, PermissionAction action, CancellationToken ct)
    {
        var children = await DetailWriter.ActiveDetailModulesAsync(modules, parent, ct);
        if (children.Count == 0) return null;
        var result = new Dictionary<string, IReadOnlyList<DynamicRecordResponse>>();
        foreach (var child in children)
        {
            var access = await guard.AuthorizeDetailAsync(parent, child, action, ct);
            if (access.Permissions is not { } p) continue;
            var parentField = ModuleRelations.DetailFieldOf(child)!;
            var rows = await records.ListByRefAsync(parentField.Id, parentRecord.Id.ToString(), ct);
            var refs = await resolver.ResolveAsync(child, p, rows, ct);
            result[child.Key] = rows.Select(r => r.ToResponse(child.Key, p, child, refs.GetValueOrDefault(r.Id))).ToList();
        }
        return result;
    }
}
