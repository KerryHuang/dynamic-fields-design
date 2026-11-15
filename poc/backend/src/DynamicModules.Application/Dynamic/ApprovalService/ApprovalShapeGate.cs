using DynamicModules.Application.Common;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>
/// graph 執行入口（流程圖設計 §13 第 7 點；計畫 fg2 J1）。引擎上線前 graph 定義一律不執行（fail closed）：這道門是為了回退安全，
/// 避免舊程式讀 graph 的 <c>Stages</c>（空）而直接核准、少簽。正式環境 <see cref="Open"/> 恆為 false，graph 一律 409，與 fg1 相同：
/// 不讀設定檔、沒有環境變數，改不動；只有測試能以 DI 換成開啟版（ApprovalGraphTestKit.OpenGate）才跑得到引擎。
/// fg2g2 一次打開全部入口時，連同四個呼叫點與 DI 註冊一起刪除。
/// </summary>
public class ApprovalShapeGate
{
    public virtual bool Open => false;

    /// <summary>選到的流程不是 sequence 就回 409（Open 時一律放行）；沒有選到流程（null）或是 sequence 回 null。</summary>
    public ServiceResult? Reject(FlowSnapshotEntry? entry) =>
        !Open && entry is { Definition.Shape: not ApprovalShapes.Sequence } e
            ? ServiceResult.Conflict(ApprovalMessages.GraphNotSupported(e.Name.Resolve(I18nText.FallbackLocale)))
            : null;
}
