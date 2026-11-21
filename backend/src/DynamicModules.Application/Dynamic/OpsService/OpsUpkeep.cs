using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Dynamic.OpsService.Alerts;
using DynamicModules.Application.Dynamic.OpsService.Scheduling;
using DynamicModules.Application.Dynamic.OpsService.Incidents;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

using DynamicModules.Domain.Mcp;

namespace DynamicModules.Application.Dynamic.OpsService;

/// <summary>
/// 維運的維護工作 <c>ops.upkeep</c>（維運設計 §17.2.8）：自動結案（<c>expired</c>）、嚴重事件重複提醒、各表依保留天數清理。
/// 清理分批：每批最多 <see cref="OpsUpkeepOptions.PurgeBatchSize"/> 列、各自一個交易（帶租約檢查），在交易型請求內做，
/// PostgreSQL 上也經過全庫寫入鎖。每批有刪到列時在同一交易寫一列 <c>ops.retention.purge</c> 稽核，摘要帶該批各表刪除筆數。
/// 規則失敗殘留列（o2d）的清理由後續子單元加。
/// </summary>
public sealed class OpsUpkeepJob(OpsUpkeepOptions options) : IScheduledJob
{
    public string Key => "ops.upkeep";

    public PolicyDefinition Interval => PolicyCatalog.OpsUpkeepMinutes;

    public async Task<JobOutcome> RunAsync(JobRunContext context, CancellationToken ct)
    {
        var sender = context.Services.GetRequiredService<ISender>();
        var policy = context.Services.GetRequiredService<CachedSystemPolicy>();
        // 以排程器的時鐘為準（與租約同一個時間來源）
        var now = context.Fence().NowUtc;

        // 自動結案：缺陷、前端錯誤、客戶設定的已知錯誤碼、安全事件連續 N 小時沒再出現
        var expireCutoff = now.AddHours(-await policy.IntAsync(PolicyCatalog.OpsIncidentAutoResolveHours, ct));
        while ((int)Lease(await sender.Send(new ExpireIncidentsBatchRequest(context.Fence(), expireCutoff, options.PurgeBatchSize), ct)).Data!
               >= options.PurgeBatchSize)
            ct.ThrowIfCancellationRequested();

        // 嚴重事件沒人確認：每 ops.incidentRenotifyHours 再提醒一次（每件一個交易）
        var renotifyCutoff = now.AddHours(-await policy.IntAsync(PolicyCatalog.OpsIncidentRenotifyHours, ct));
        var candidates = await context.Services.GetRequiredService<IOpsIncidentRepository>()
            .ListRenotifyCandidatesAsync(renotifyCutoff, options.PurgeBatchSize, ct);
        foreach (var id in candidates)
        {
            ct.ThrowIfCancellationRequested();
            Lease(await sender.Send(new RenotifyIncidentRequest(context.Fence(), id, renotifyCutoff), ct));
        }

        await PurgeAsync(context, sender, OpsPurgeTarget.AuditLog, now.AddDays(-await policy.IntAsync(PolicyCatalog.OpsAuditRetentionDays, ct)), ct);
        // 健康檢查紀錄與已寄完的告警寄送紀錄共用一個天數（§17.4、§17.2.8）
        var healthCutoff = now.AddDays(-await policy.IntAsync(PolicyCatalog.OpsHealthResultRetentionDays, ct));
        await PurgeAsync(context, sender, OpsPurgeTarget.HealthResult, healthCutoff, ct);
        await PurgeAsync(context, sender, OpsPurgeTarget.AlertOutbox, healthCutoff, ct);
        await PurgeAsync(context, sender, OpsPurgeTarget.IncidentSample,
            now.AddDays(-await policy.IntAsync(PolicyCatalog.OpsIncidentSampleRetentionDays, ct)), ct);
        await PurgeAsync(context, sender, OpsPurgeTarget.Incident, now.AddDays(-await policy.IntAsync(PolicyCatalog.OpsIncidentRetentionDays, ct)), ct);
        // 規則失敗列只在滑動窗口內有用；用窗口的上限算，設定調大時不會先被清掉（維運設計 §17.2.7、§17.4）
        await PurgeAsync(context, sender, OpsPurgeTarget.RuleFailure, now.AddMinutes(-PolicyCatalog.OpsRulePauseWindowMinutes.Max), ct);
        // MCP（mcp1a 判斷 22）：確認碼在結束後 1 天清；撤銷或到期的權杖隨稽核保留天數清（保留列供稽核關聯）
        var confirmationCutoff = now.AddDays(-McpLimits.ConfirmationPurgeDays);
        while ((int)Lease(await sender.Send(new McpService.Upkeep.PurgeMcpConfirmationsBatchRequest(
                   context.Fence(), confirmationCutoff, options.PurgeBatchSize), ct)).Data! >= options.PurgeBatchSize) { }
        var tokenCutoff = now.AddDays(-await policy.IntAsync(PolicyCatalog.OpsAuditRetentionDays, ct));
        while ((int)Lease(await sender.Send(new McpService.Upkeep.PurgeMcpTokensBatchRequest(
                   context.Fence(), tokenCutoff, options.PurgeBatchSize), ct)).Data! >= options.PurgeBatchSize) { }
        return new JobOutcome(ScheduledJobStatus.Succeeded);
    }

    private async Task PurgeAsync(JobRunContext context, ISender sender, OpsPurgeTarget target, DateTime cutoff, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var batch = Lease(await sender.Send(new PurgeOpsBatchRequest(context.Fence(), target, cutoff, options.PurgeBatchSize), ct));
            if ((int)batch.Data! < options.PurgeBatchSize) break;
        }
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

/// <summary>保留天數清理的對象；摘要的鍵（<c>deleted.auditLog</c>…）與列舉名稱的 camelCase 相同。</summary>
public enum OpsPurgeTarget { AuditLog, HealthResult, Incident, IncidentSample, AlertOutbox, RuleFailure }

/// <summary>
/// 刪掉一批超過保留天數的列，有刪到時同一交易寫一列 <c>ops.retention.purge</c>（維運設計 §17.2.1「不經 AuditBehavior 的寫入點」）；
/// 後面的批次失敗、取消或丟了租約，已提交的批次仍有紀錄。回傳刪除筆數（int）。事件只刪已結案的（樣本隨外鍵串聯刪除）。
/// </summary>
public sealed record PurgeOpsBatchRequest(JobLeaseFence Fence, OpsPurgeTarget Target, DateTime CutoffUtc, int BatchSize)
    : IRequest<ServiceResult>, ITransactionalRequest;

public class PurgeOpsBatchHandler(
    IScheduledJobRepository jobs, IAuditLogRepository auditLog, IHealthCheckRepository checks, IOpsIncidentRepository incidents,
    IAlertOutboxRepository outbox, IRuleHealthRepository ruleHealth, IAuditLog audit, IUnitOfWork uow)
    : IRequestHandler<PurgeOpsBatchRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(PurgeOpsBatchRequest request, CancellationToken ct)
    {
        var fence = request.Fence;
        if (!await jobs.HoldsLeaseAsync(fence.JobKey, fence.Owner, fence.NowUtc, ct)) return ServiceResult.Conflict(null, JobLeaseFence.LostCode);
        var deleted = request.Target switch
        {
            OpsPurgeTarget.AuditLog => await auditLog.PurgeBatchAsync(request.CutoffUtc, request.BatchSize, ct),
            OpsPurgeTarget.HealthResult => await checks.PurgeResultsBatchAsync(request.CutoffUtc, request.BatchSize, ct),
            OpsPurgeTarget.Incident => await incidents.PurgeResolvedBatchAsync(request.CutoffUtc, request.BatchSize, ct),
            OpsPurgeTarget.IncidentSample => await incidents.PurgeSamplesBatchAsync(request.CutoffUtc, request.BatchSize, ct),
            OpsPurgeTarget.AlertOutbox => await outbox.PurgeBatchAsync(request.CutoffUtc, request.BatchSize, ct),
            OpsPurgeTarget.RuleFailure => await ruleHealth.PurgeFailuresBatchAsync(request.CutoffUtc, request.BatchSize, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
        if (deleted > 0)
        {
            var key = request.Target.ToString();
            audit.Add(new AuditEntry("ops.retention.purge", AuditResult.Success, AuditTargets.Job, fence.JobKey,
                Summary: new AuditSummary().Add("deleted", new Dictionary<string, int> { [char.ToLowerInvariant(key[0]) + key[1..]] = deleted }).ToJson()));
            await uow.CommitAsync(ct);
        }
        return ServiceResult.Success(deleted);
    }
}

/// <summary>一批自動結案（<c>expired</c>）；回傳結案數。來源依 §17.2.2 結案方式表：server、client、config 的已知錯誤碼、security。</summary>
public sealed record ExpireIncidentsBatchRequest(JobLeaseFence Fence, DateTime CutoffUtc, int BatchSize) : IRequest<ServiceResult>, ITransactionalRequest;

public class ExpireIncidentsBatchHandler(IScheduledJobRepository jobs, IOpsIncidentRepository incidents)
    : IRequestHandler<ExpireIncidentsBatchRequest, ServiceResult>
{
    private static readonly IncidentSource[] Sources = [IncidentSource.Server, IncidentSource.Client, IncidentSource.Config, IncidentSource.Security];
    /// <summary>暫停事件只在恢復或刪規則時結案（o2d 的錯誤碼）。</summary>
    private static readonly string[] Excluded = [RulePause.RuleErrorCodes.AutoPaused];

    public async Task<ServiceResult> Handle(ExpireIncidentsBatchRequest request, CancellationToken ct)
    {
        var fence = request.Fence;
        if (!await jobs.HoldsLeaseAsync(fence.JobKey, fence.Owner, fence.NowUtc, ct)) return ServiceResult.Conflict(null, JobLeaseFence.LostCode);
        return ServiceResult.Success(await incidents.ExpireBatchAsync(Sources, Excluded, request.CutoffUtc, fence.NowUtc, request.BatchSize, ct));
    }
}

/// <summary>
/// 嚴重事件的重複提醒：條件式更新搶到（影響 1 列）才發通知，兩台不會各發一次。收件人照事件類別（客戶設定類走 <c>ops.configIssueRecipients</c>，
/// 任務規則的 <c>ruleEditor</c> 取規則目前的最後修改者）；規則暫停事件沿用暫停通知的種類與連結。
/// </summary>
public sealed record RenotifyIncidentRequest(JobLeaseFence Fence, long IncidentId, DateTime CutoffUtc) : IRequest<ServiceResult>, ITransactionalRequest;

public class RenotifyIncidentHandler(
    IScheduledJobRepository jobs, IOpsIncidentRepository incidents, OpsNotifier notifier, AlertOutboxWriter alerts, IUnitOfWork uow,
    ITaskRuleRepository taskRules) : IRequestHandler<RenotifyIncidentRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(RenotifyIncidentRequest request, CancellationToken ct)
    {
        var fence = request.Fence;
        if (!await jobs.HoldsLeaseAsync(fence.JobKey, fence.Owner, fence.NowUtc, ct)) return ServiceResult.Conflict(null, JobLeaseFence.LostCode);
        if (!await incidents.TryClaimRenotifyAsync(request.IncidentId, request.CutoffUtc, fence.NowUtc, ct)) return ServiceResult.Success(false);
        var i = (await incidents.GetAsync(request.IncidentId, ct))!;
        var rule = i is { RuleKind: Common.Observability.RuleFailureTag.TaskRule, RuleId: { } ruleId } ? await taskRules.GetByIdAsync(ruleId, ct) : null;
        var paused = i.Code == RulePause.RuleErrorCodes.AutoPaused && rule is not null && i.ModuleKey is not null;
        await notifier.NotifyAsync(i.Id, i.TitleKey, IncidentTitles.FromJson(i.TitleParamsJson), paused ? RulePause.RuleIncidents.RuleLink(i.ModuleKey!, rule!.Key) : null,
            ct, i.Category, rule is null ? null : RulePause.RuleIncidents.EditorOf(rule), paused ? NotificationKinds.OpsRulePaused : null);
        await alerts.EnqueueAsync(i.Id, AlertTransition.Renotify, null, ct);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(true);
    }
}
