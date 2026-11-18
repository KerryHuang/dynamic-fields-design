using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Dynamic.OpsService.Scheduling;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.Application.Dynamic.OpsService;

/// <summary>
/// 維運的維護工作 <c>ops.upkeep</c>（維運設計 §17.2.8）。o2a 只做稽核的保留天數清理；自動結案、重複提醒、其他表的清理由 o2b–o2d 加。
/// 清理分批：每批最多 <see cref="OpsUpkeepOptions.PurgeBatchSize"/> 列、各自一個交易（帶租約檢查），在交易型請求內做，
/// PostgreSQL 上也經過全庫寫入鎖。每批有刪到列時在同一交易寫一列 <c>ops.retention.purge</c> 稽核，摘要帶該批各表刪除筆數。
/// </summary>
public sealed class OpsUpkeepJob(OpsUpkeepOptions options) : IScheduledJob
{
    public string Key => "ops.upkeep";

    public PolicyDefinition Interval => PolicyCatalog.OpsUpkeepMinutes;

    public async Task<JobOutcome> RunAsync(JobRunContext context, CancellationToken ct)
    {
        var sender = context.Services.GetRequiredService<ISender>();
        var days = await context.Services.GetRequiredService<CachedSystemPolicy>().IntAsync(PolicyCatalog.OpsAuditRetentionDays, ct);
        // 以排程器的時鐘為準（與租約同一個時間來源）
        var cutoff = context.Fence().NowUtc.AddDays(-days);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var batch = Lease(await sender.Send(new PurgeAuditBatchRequest(context.Fence(), cutoff, options.PurgeBatchSize), ct));
            if ((int)batch.Data! < options.PurgeBatchSize) break;
        }
        return new JobOutcome(ScheduledJobStatus.Succeeded);
    }

    private ServiceResult Lease(ServiceResult result)
    {
        if (result is { IsConflict: true, Code: JobLeaseFence.LostCode }) throw new JobLeaseLostException(Key);
        if (!result.IsSuccess) throw new InvalidOperationException($"{Key} 寫入失敗：{result.Message}");
        return result;
    }
}

/// <summary>清理的批次大小（技術限制，§17.9：每批 5000 列）；測試可換小一點。</summary>
public sealed class OpsUpkeepOptions
{
    public int PurgeBatchSize { get; init; } = 5000;
}

/// <summary>
/// 刪掉一批超過保留天數的稽核列，有刪到時同一交易寫一列 <c>ops.retention.purge</c>（維運設計 §17.2.1「不經 AuditBehavior 的寫入點」）；
/// 後面的批次失敗、取消或丟了租約，已提交的批次仍有紀錄。回傳刪除筆數（int）。
/// </summary>
public sealed record PurgeAuditBatchRequest(JobLeaseFence Fence, DateTime CutoffUtc, int BatchSize) : IRequest<ServiceResult>, ITransactionalRequest;

public class PurgeAuditBatchHandler(IScheduledJobRepository jobs, IAuditLogRepository auditLog, IAuditLog audit, IUnitOfWork uow)
    : IRequestHandler<PurgeAuditBatchRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(PurgeAuditBatchRequest request, CancellationToken ct)
    {
        var fence = request.Fence;
        if (!await jobs.HoldsLeaseAsync(fence.JobKey, fence.Owner, fence.NowUtc, ct)) return ServiceResult.Conflict(null, JobLeaseFence.LostCode);
        var deleted = await auditLog.PurgeBatchAsync(request.CutoffUtc, request.BatchSize, ct);
        if (deleted > 0)
        {
            audit.Add(new AuditEntry("ops.retention.purge", AuditResult.Success, AuditTargets.Job, fence.JobKey,
                Summary: new AuditSummary().Add("deleted", new Dictionary<string, int> { ["auditLog"] = deleted }).ToJson()));
            await uow.CommitAsync(ct);
        }
        return ServiceResult.Success(deleted);
    }
}
