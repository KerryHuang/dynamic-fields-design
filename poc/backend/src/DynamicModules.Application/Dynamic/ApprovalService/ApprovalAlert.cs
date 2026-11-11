using DynamicModules.Application.Common;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

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

/// <summary>通知全部啟用中的系統管理員 approvalAdminAlert；標題帶記錄標題與原因（通知沒有內文欄位）。</summary>
public record WriteApprovalAlertRequest(long InstanceId, string Reason) : IRequest<ServiceResult>, ITransactionalRequest;

public class WriteApprovalAlertHandler(IApprovalInstanceRepository repo, IPermissionRepository permissions, ApprovalNotifier notifier, IUnitOfWork uow)
    : IRequestHandler<WriteApprovalAlertRequest, ServiceResult>
{
    private const int TitleMax = 200;

    public async Task<ServiceResult> Handle(WriteApprovalAlertRequest request, CancellationToken ct)
    {
        if (await repo.GetAsync(request.InstanceId, ct) is not { } inst) return ServiceResult.NotFound(ApprovalMessages.TaskNotFound);
        var admins = (await permissions.ListUsersAsync(ct)).Where(u => u.IsActive && u.IsSystemAdmin).Select(u => u.UserId).ToList();
        var title = $"{inst.TitleSnapshot}：{request.Reason}";
        await notifier.NotifyAsync(NotificationKinds.ApprovalAdminAlert, inst, admins, null, ct, title.Length <= TitleMax ? title : title[..TitleMax]);
        await uow.CommitAsync(ct);
        return ServiceResult.Success();
    }
}
