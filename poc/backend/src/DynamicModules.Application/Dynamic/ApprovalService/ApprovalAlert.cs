using DynamicModules.Application.Common;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Application.Dynamic.PolicyService;
using MediatR;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>
/// 一個請求內待送的系統管理員警示（scoped）：執行期再驗失敗時 Raise，主交易回滾後由 <see cref="Common.Behaviors.ApprovalAlertBehavior{TReq,TRes}"/>
/// 另開一筆交易寫通知（設計 §5.2 末段、§8.3）。
/// </summary>
public sealed class ApprovalAlert
{
    public (long InstanceId, string Reason)? Pending { get; private set; }

    public void Raise(long instanceId, string reason) => Pending = (instanceId, reason);

    public void Clear() => Pending = null;
}

/// <summary>通知異常通知對象政策（出廠為全部啟用中的系統管理員）解析出的人 approvalAdminAlert；標題帶記錄標題與原因（通知沒有內文欄位）。</summary>
public record WriteApprovalAlertRequest(long InstanceId, string Reason) : IRequest<ServiceResult>, ITransactionalRequest;

public class WriteApprovalAlertHandler(
    IApprovalInstanceRepository repo, IPermissionRepository permissions, ApprovalPolicies policies, RecipientResolver recipients, ApprovalNotifier notifier,
    IUnitOfWork uow, ILogger<WriteApprovalAlertHandler> logger)
    : IRequestHandler<WriteApprovalAlertRequest, ServiceResult>
{
    private const int TitleMax = 200;

    public async Task<ServiceResult> Handle(WriteApprovalAlertRequest request, CancellationToken ct)
    {
        if (await repo.GetAsync(request.InstanceId, ct) is not { } inst) return ServiceResult.NotFound(ApprovalMessages.TaskNotFound);
        var to = await recipients.ResolveAsync(await policies.AlertRecipientItemsAsync(ct), ct);
        if (to.Count == 0)
        {
            // 異常不能沒人知道：設定的對象全部停用或不存在時改送全部系統管理員（寫死的安全網）
            logger.LogWarning("簽核異常通知對象政策解析為空，改通知全部系統管理員（實例 {InstanceId}）", request.InstanceId);
            to = (await permissions.ListUsersAsync(ct)).Where(u => u.IsActive && u.IsSystemAdmin).Select(u => u.UserId).ToList();
        }
        var title = $"{inst.TitleSnapshot}：{request.Reason}";
        await notifier.NotifyAsync(NotificationKinds.ApprovalAdminAlert, inst, to, null, ct, title.Length <= TitleMax ? title : title[..TitleMax]);
        await uow.CommitAsync(ct);
        return ServiceResult.Success();
    }
}
