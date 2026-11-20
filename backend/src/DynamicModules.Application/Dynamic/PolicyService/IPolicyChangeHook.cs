using DynamicModules.Application.Common;
using DynamicModules.Domain.Policies;

namespace DynamicModules.Application.Dynamic.PolicyService;

/// <summary>
/// 系統 PUT 改變生效值時、在同一個交易裡要跟著做的事（例：改營運時區時平移日期時間索引）。
/// 呼叫時新值已寫入（SaveChanges 已做、交易還沒提交）；回非 null 就以該結果回應並回滾整個交易，丟例外同樣回滾。
/// 新舊生效值相同時不呼叫。
/// </summary>
public interface IPolicyChangeHook
{
    Task<ServiceResult?> BeforeCommitAsync(PolicyDefinition def, string oldEffective, string newEffective, CancellationToken ct);
}
