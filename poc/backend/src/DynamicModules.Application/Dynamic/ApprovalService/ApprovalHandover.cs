using DynamicModules.Application.Common.Time;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>
/// 交接（設計 §7.5、§7.8）：轉交與管理員改派共用。舊任務改 transferred（ActedTime = 現在）；新任務同席位、同期限，
/// Reason = transferred，OriginalUserId 沿用最初的人；寫歷程並通知新負責人。新任務不套代理（點名的人就是那個人）。
/// </summary>
public sealed class ApprovalHandover(IApprovalInstanceRepository repo, ApprovalEngine engine, ApprovalNotifier notifier, IBusinessTime time)
{
    public async Task<ApprovalTask> HandOverAsync(ApprovalInstance inst, ApprovalTask from, string toUserId, string action, string actor,
        string? onBehalfOf, string? comment, CancellationToken ct)
    {
        from.Status = ApprovalTaskStatus.Transferred;
        from.ActedTime = time.Now;
        var added = new ApprovalTask
        {
            StageRunId = from.StageRunId, InstanceId = from.InstanceId, RecordId = from.RecordId, AssigneeUserId = toUserId,
            OriginalUserId = from.OriginalUserId, SeatKey = from.SeatKey, Reason = ApprovalTaskReason.Transferred,
            Status = ApprovalTaskStatus.Open, CreatedTime = time.Now, DueTime = from.DueTime
        };
        repo.AddTask(added);
        engine.Log(inst, action, actor, from.StageRunId, from.Id, comment, targets: [toUserId], onBehalfOf: onBehalfOf);
        await notifier.NotifyAsync(NotificationKinds.ApprovalTransferred, inst, [toUserId], actor, ct);
        return added;
    }
}
