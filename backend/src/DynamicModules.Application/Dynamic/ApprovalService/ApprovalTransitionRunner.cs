using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>
/// 簽核造成的記錄寫入（設計 §8.2 第 1、4 步、§8.3）：以系統身分呼叫既有的更新 handler，事件、分錄、檢查照跑；
/// 寫入的鍵（狀態欄位與可編輯欄位）連同寫入後的值登記豁免，後盾才放行。稽核欄位仍是目前使用者（按下同意的人）。
/// </summary>
public sealed class ApprovalTransitionRunner(
    IRecordRepository records, IModuleRepository modules, ISystemIdentity system, ApprovalWriteContext context,
    IRequestHandler<UpdateRecordRequest, ServiceResult> update, IUnitOfWork uow, IWriteJournal journal)
{
    /// <summary>
    /// toOptionId 為 null 時不改狀態；values 為 null 時只改狀態。兩者都沒有就什麼都不做。
    /// 前提：呼叫前不得有同一筆記錄的未存檔修改。「自己寫的那幾筆」是以 seqBefore 之後、同一筆記錄的日誌認定，
    /// 未存檔的修改會在下面的 CommitAsync 一起落成那幾筆而被豁免，等於繞過後盾少簽（審查建議 7）。
    /// </summary>
    public async Task<ServiceResult> RunAsync(ApprovalInstance inst, long? toOptionId, IReadOnlyDictionary<string, JsonElement?>? values, CancellationToken ct)
    {
        var record = await records.GetAsync(inst.RecordId, ct);
        if (record is null) return ServiceResult.Conflict(ApprovalMessages.RecordGone);
        var module = await modules.GetByIdAsync(record.ModuleId, includeChildren: true, ct);
        if (module?.Workflow is not { } wf) return ServiceResult.Conflict(ApprovalMessages.NoWorkflow);

        var input = values is null ? new Dictionary<string, JsonElement?>() : new Dictionary<string, JsonElement?>(values);
        if (toOptionId is { } to) input[wf.StatusFieldKey] = JsonSerializer.SerializeToElement($"opt_{to}");
        if (input.Count == 0) return ServiceResult.Success();

        var keys = input.Keys.ToHashSet();
        var seqBefore = journal.Entries.Count == 0 ? 0 : journal.Entries.Max(e => e.Seq);
        ServiceResult result;
        using (system.Enter(null))
            result = await update.Handle(new UpdateRecordRequest(record.Id, input, record.RowVersion, null, keys), ct);
        if (!result.IsSuccess) return result;
        // 豁免登記寫入後的實際值（update 已改了同一個追蹤中的記錄）：同一請求裡其他寫入再改這些鍵，後盾照擋
        var after = RecordValueProcessor.Parse(record.ValuesJson);
        context.Exempt(record.Id, inst.Id, keys.ToDictionary(k => k, k => ApprovalBackstopStage.Raw(after, k)));
        // 立刻存檔，讓這次寫入成為寫入日誌裡自己的一筆，記下 Seq：後盾的逐筆轉移檢查只豁免這幾筆（仍在同一個交易內）
        await uow.CommitAsync(ct);
        context.ExemptEntries(record.Id, journal.Entries.Where(e => e.Seq > seqBefore && e.RecordId == record.Id).Select(e => e.Seq));
        return result;
    }
}
