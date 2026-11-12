using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.Rollups;

/// <summary>
/// 載入整批重算要固定的記錄（<see cref="FrozenRecords"/>）：本模組的事實與進行中實例、鎖定規則；明細模組再加上表頭的事實、
/// 進行中與鎖定狀態（只讀 rows 裡出現的表頭，不讀整個表頭模組）。rows 由呼叫端傳入，避免多讀一次。
/// </summary>
public sealed class FrozenRecordsLoader(
    IEventFactRepository facts, IApprovalInstanceRepository instances, IRecordRepository records, IModuleRepository modules,
    ApprovalLockService locks)
{
    public async Task<FrozenRecords> LoadAsync(Module module, IReadOnlyCollection<Record> rows, CancellationToken ct)
    {
        var withFacts = await facts.ListSourceIdsInModuleAsync(module.Id, ct);
        var frozenHeaders = new HashSet<long>();
        var moduleIds = new List<long> { module.Id };

        Module? header = null;
        if (ModuleRelations.ParentKeyOf(module) is { } headerKey)
        {
            frozenHeaders.UnionWith(await facts.ListSourceIdsInModuleKeyAsync(headerKey, ct));
            header = await modules.GetByKeyAsync(headerKey, includeChildren: true, ct);
            if (header is not null) moduleIds.Add(header.Id);
        }

        var pending = (await instances.ListPendingByModulesAsync(moduleIds, ct)).Select(p => p.RecordId).ToHashSet();
        if (header is not null && ModuleRelations.DetailFieldOf(module) is { } headerField)
        {
            frozenHeaders.UnionWith(pending);
            var headerIds = rows
                .Select(r => RecordService.RecordValueProcessor.Parse(r.ValuesJson))
                .Select(v => v.TryGetValue(headerField.Key, out var raw) && raw is { ValueKind: System.Text.Json.JsonValueKind.Number } n
                    && n.TryGetInt64(out var id) ? id : (long?)null)
                .OfType<long>().ToHashSet();
            if (headerIds.Count > 0 && header.Workflow is { } wf)
            {
                var headerLock = await locks.ForModuleAsync(header, null, ct);
                foreach (var h in await records.ListByIdsAsync(header.Id, headerIds, ct))
                    if (headerLock.IsLockedState(wf.StateOf(RecordService.RecordValueProcessor.Parse(h.ValuesJson)))) frozenHeaders.Add(h.Id);
            }
        }
        return new FrozenRecords(module, withFacts, frozenHeaders, pending, await locks.ForModuleAsync(module, null, ct));
    }
}
