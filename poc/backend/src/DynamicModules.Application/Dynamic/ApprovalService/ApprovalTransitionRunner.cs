using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>
/// 簽核造成的記錄寫入（設計 §8.2 第 1、4 步、§8.3）：以系統身分呼叫既有的更新 handler，事件、分錄、檢查照跑；
/// 寫入的鍵（狀態欄位與可編輯欄位）先登記豁免，後盾才放行。稽核欄位仍是目前使用者（按下同意的人）。
/// </summary>
public sealed class ApprovalTransitionRunner(
    IRecordRepository records, IModuleRepository modules, ISystemIdentity system, ApprovalWriteContext context,
    IRequestHandler<UpdateRecordRequest, ServiceResult> update)
{
    /// <summary>toOptionId 為 null 時不改狀態；values 為 null 時只改狀態。兩者都沒有就什麼都不做。</summary>
    public async Task<ServiceResult> RunAsync(ApprovalInstance inst, long? toOptionId, IReadOnlyDictionary<string, JsonElement?>? values, CancellationToken ct)
    {
        var record = await records.GetAsync(inst.RecordId, ct);
        if (record is null) return ServiceResult.Conflict("找不到簽核的記錄（可能已被刪除）");
        var module = await modules.GetByIdAsync(record.ModuleId, includeChildren: true, ct);
        if (module?.Workflow is not { } wf) return ServiceResult.Conflict("記錄所在模組沒有狀態機");

        var input = values is null ? new Dictionary<string, JsonElement?>() : new Dictionary<string, JsonElement?>(values);
        if (toOptionId is { } to) input[wf.StatusFieldKey] = JsonSerializer.SerializeToElement($"opt_{to}");
        if (input.Count == 0) return ServiceResult.Success();

        var keys = input.Keys.ToHashSet();
        context.Exempt(record.Id, inst.Id, keys);
        using var _ = system.Enter(null);
        return await update.Handle(new UpdateRecordRequest(record.Id, input, record.RowVersion, null, keys), ct);
    }
}
