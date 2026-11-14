using DynamicModules.Application.Common;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>
/// 引擎上線前 graph 定義一律不執行（fail closed，流程圖設計 §13 第 7 點）：現在 API 寫不出 graph，
/// 這道門是為了回退安全，避免舊程式讀 graph 的 <c>Stages</c>（空）而直接核准、少簽。fg2 引擎上線時刪除這個類別與三個呼叫點。
/// </summary>
public static class ApprovalShapeGate
{
    /// <summary>選到的流程不是 sequence 就回 409；沒有選到流程（null）或是 sequence 回 null。</summary>
    public static ServiceResult? Reject(FlowSnapshotEntry? entry) =>
        entry is { Definition.Shape: not ApprovalShapes.Sequence } e
            ? ServiceResult.Conflict(ApprovalMessages.GraphNotSupported(e.Name.Resolve(I18nText.FallbackLocale)))
            : null;

}
