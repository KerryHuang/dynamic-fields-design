using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.EventService;

/// <summary>一次請求內事件效果寫入的記錄數（設計 §4.6 總量上限）。每個請求一份。</summary>
public class EffectBudget
{
    public const int Max = 500;
    public int Written { get; private set; }
    public bool TryUse() => ++Written <= Max;
}

/// <summary>
/// 事件效果的寫入（設計 §4.2）：以系統身分呼叫既有的新增與修改 handler，所以型別正規化、規則、必填、自動編號、索引、歷程都走同一條路，
/// 但不看目標的動作權限、資料範圍與欄位權限；列在 set 裡的欄位視為系統填入。直接呼叫 handler 而不是再送一次 MediatR，所以不會巢狀觸發事件階段。
/// </summary>
public class EffectWriter(
    IModuleRepository modules, IRecordRepository records, ISystemIdentity system, EffectBudget budget,
    IRequestHandler<CreateRecordRequest, ServiceResult> create, IRequestHandler<UpdateRecordRequest, ServiceResult> update)
{
    public const string TooMany = "連動事件寫入的記錄超過 500 筆，請縮小範圍";

    public async Task<ServiceResult> CreateAsync(
        string ownerUserId, string moduleKey, Dictionary<string, JsonElement?> values,
        Dictionary<string, List<DetailRowInput>>? details, CancellationToken ct)
    {
        if (!budget.TryUse()) return ServiceResult.Conflict(TooMany);
        using var _ = system.Enter(ownerUserId);
        return await create.Handle(new CreateRecordRequest(moduleKey, values, null, details, values.Keys.ToHashSet()), ct);
    }

    public async Task<ServiceResult> UpdateAsync(long recordId, Dictionary<string, JsonElement?> values, CancellationToken ct)
    {
        if (!budget.TryUse()) return ServiceResult.Conflict(TooMany);
        var record = await records.GetAsync(recordId, ct);
        if (record is null) return ServiceResult.Conflict("找不到要更新的目標記錄（可能已被刪除）");
        var module = await modules.GetByIdAsync(record.ModuleId, includeChildren: true, ct);
        if (module?.Workflow is { } wf && values.Keys.Any(k => k != wf.StatusFieldKey)
            && wf.IsLocked(RecordService.RecordValueProcessor.Parse(record.ValuesJson)))
            return ServiceResult.Conflict("目標記錄處於唯讀狀態，無法更新");
        using var _ = system.Enter(null);
        return await update.Handle(new UpdateRecordRequest(recordId, values, record.RowVersion, null, values.Keys.ToHashSet()), ct);
    }

    /// <summary>給外掛的寫入器：只放行宣告過的（模組，新增或更新）。</summary>
    public IEffectWriter Restrict(IReadOnlyList<EffectWrite> declared, string ownerUserId) => new Restricted(this, declared, ownerUserId, records, modules);

    private sealed class Restricted(
        EffectWriter inner, IReadOnlyList<EffectWrite> declared, string ownerUserId, IRecordRepository records, IModuleRepository modules)
        : IEffectWriter
    {
        public Task<ServiceResult> CreateAsync(string moduleKey, Dictionary<string, JsonElement?> values, CancellationToken ct) =>
            declared.Any(d => d.Create && d.ModuleKey == moduleKey)
                ? inner.CreateAsync(ownerUserId, moduleKey, values, null, ct)
                : Task.FromResult(ServiceResult.Conflict($"外掛沒有宣告要新增「{moduleKey}」的記錄，已擋下"));

        public async Task<ServiceResult> UpdateAsync(string moduleKey, long recordId, Dictionary<string, JsonElement?> values, CancellationToken ct)
        {
            var allowed = declared.FirstOrDefault(d => !d.Create && d.ModuleKey == moduleKey);
            if (allowed is null) return ServiceResult.Conflict($"外掛沒有宣告要更新「{moduleKey}」的記錄，已擋下");
            var record = await records.GetAsync(recordId, ct);
            var module = record is null ? null : await modules.GetByIdAsync(record.ModuleId, includeChildren: false, ct);
            if (module is null || module.Key != moduleKey) return ServiceResult.Conflict("記錄不屬於宣告的模組");
            var status = (await modules.GetByIdAsync(module.Id, includeChildren: true, ct))!.Workflow?.StatusFieldKey;
            if (!allowed.TouchesStatus && status is not null && values.ContainsKey(status))
                return ServiceResult.Conflict("外掛沒有宣告要變更狀態欄位，已擋下");
            return await inner.UpdateAsync(recordId, values, ct);
        }
    }
}
