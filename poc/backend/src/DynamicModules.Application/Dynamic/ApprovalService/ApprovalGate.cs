using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>
/// 一般寫入的前置擋點（設計 §5.3、§8.5）：狀態改成需簽核轉移的目標 → 400（含系統管理員）；記錄簽核中 → 409。
/// 系統身分（事件、簽核動作自己的轉移）不在這裡擋，交給後盾判斷。狀態機沒有需簽核轉移時不查資料庫。
/// </summary>
public sealed class ApprovalGate(IApprovalInstanceRepository instances, ISystemIdentity system)
{
    public async Task<ServiceResult?> RejectAsync(Module module, Record record, IReadOnlyDictionary<string, JsonElement?> previous,
        IReadOnlyDictionary<string, JsonElement?>? input, bool isDelete, CancellationToken ct, bool detailsChanged = false)
    {
        if (system.Active || module.Workflow is not { } wf || ApprovalTopology.ApprovalTargets(wf).Count == 0) return null;
        var key = wf.StatusFieldKey;
        var from = wf.StateOf(previous) ?? wf.InitialOptionId;
        var to = input is not null && input.ContainsKey(key) ? wf.StateOf(input) : null;
        if (to is { } t && t != from && wf.RequiresApproval(from, t))
            return ServiceResult.Invalid(key, ApprovalMessages.NeedsSubmit);

        if (await instances.GetPendingByRecordAsync(record.Id, ct) is null) return null;
        if (isDelete) return ServiceResult.Conflict(ApprovalMessages.Pending);
        var changed = (input ?? new Dictionary<string, JsonElement?>()).Where(kv => Raw(kv.Value) != Raw(previous.GetValueOrDefault(kv.Key)))
            .Select(kv => kv.Key).ToList();
        var onlyStatus = !detailsChanged && changed.Count > 0 && changed.All(k => k == key);
        return ServiceResult.Conflict(onlyStatus ? ApprovalMessages.WithdrawFirst : ApprovalMessages.Pending);
    }

    private static string? Raw(JsonElement? v) => v is { ValueKind: not JsonValueKind.Null } e ? e.GetRawText() : null;
}
