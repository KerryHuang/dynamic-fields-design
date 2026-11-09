using System.Text.Json;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.References;
using DynamicModules.Application.Dynamic.Rollups;
using DynamicModules.Application.Dynamic.WorkflowService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.RecordService;

/// <summary>
/// 新增記錄「明細以外」的步驟（第 7 階段決策 7），POST records 與匯入共用：
/// ValidateAsync 不寫入任何東西；InsertAsync 取號並加進 change tracker，由呼叫端 commit。
/// </summary>
public class RecordCreator(
    IModuleRepository modules, IRecordRepository records, INumberSequenceRepository sequences, FieldTypeRegistry registry,
    ICurrentUser user, ISystemIdentity system, IBusinessTime time, ReferenceValidator refs, ReferenceFill fill, WorkflowEnforcer workflow, RollupRecalc rollups, RecordHistoryWriter history,
    FieldLocks locks)
{
    /// <param name="rollupValues">這次存檔在記憶體算好的彙總值（明細彙總）；沒給的一般參照彙總取空集合的值（第 6 階段 R13）。</param>
    public async Task<(Dictionary<string, JsonElement?> Values, Dictionary<string, string[]> Errors)> ValidateAsync(
        Module module, EffectivePermissions perms, IReadOnlyDictionary<string, JsonElement?> input,
        IReadOnlyDictionary<string, JsonElement?>? rollupValues, CancellationToken ct, IReadOnlySet<string>? extraSystemKeys = null)
    {
        // 一般參照的彙總：新記錄還沒有子記錄，取空集合的值
        var allRollups = rollupValues is null ? [] : new Dictionary<string, JsonElement?>(rollupValues);
        foreach (var (key, value) in await rollups.EmptyValuesAsync(module, time.Today, ct)) allRollups.TryAdd(key, value);
        var userIds = await modules.ListUserIdsAsync(ct);
        var validRefKeys = await refs.ValidKeysAsync(module, input, null, ct);
        RecordValueProcessor.RejectFrozenInput(module, input, null, userIds, registry, perms, validRefKeys, extraSystemKeys);
        var filled = new Dictionary<string, JsonElement?>(input);
        var systemKeys = new HashSet<string>(await fill.ApplyAsync(
            module, filled, null, validRefKeys, RecordValueProcessor.WritablePredicate(module, null, perms, registry, userIds, time.Today), ct));
        if (extraSystemKeys is not null) systemKeys.UnionWith(extraSystemKeys);
        WorkflowEnforcer.SeedInitial(module, filled, systemKeys);
        var result = RecordValueProcessor.Process(
            module, filled, null, userIds, registry, perms, new RuleContext(time.Today), validRefKeys, allRollups, systemKeys: systemKeys);
        foreach (var (key, messages) in await workflow.CheckAsync(module, null, result.values, ct)) result.errors[key] = messages;
        return result;
    }

    /// <summary>取自動編號、建記錄與索引、寫歷程 Create；擁有者是目前使用者。不 commit。</summary>
    public async Task<Record> InsertAsync(
        Module module, Dictionary<string, JsonElement?> values, string? hostKey, string? hostTitle, CancellationToken ct)
    {
        await RecordAutoNumbers.AssignAsync(sequences, module, values, time.Now, null, ct);
        await locks.RestoreAsync(module, null, values, ct);   // 生命週期鎖第二道：新增時凍結欄位沒有值
        var record = new Record
        {
            ModuleId = module.Id,
            Title = hostTitle ?? RecordValueProcessor.TitleOf(module, values),
            HostKey = hostKey,
            OwnerUserId = system.OwnerUserId ?? user.UserId,
            ValuesJson = RecordValueProcessor.Serialize(values),
            RowVersion = Guid.NewGuid().ToString()
        };
        record.StampCreated(user.UserId, time.Now);
        await records.ReplaceIndexAsync(record, RecordIndexer.Build(module, values, registry), ct);
        records.Add(record);
        history.Created(record);
        return record;
    }
}
