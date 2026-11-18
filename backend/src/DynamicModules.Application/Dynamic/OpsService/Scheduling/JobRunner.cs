using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Observability;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Ops;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.OpsService.Scheduling;

/// <summary>
/// 排程器核心（維運設計 §10.2.8）：每次 <see cref="TickAsync"/> 找出到期的工作，搶到租約才執行，同一 tick 內依序跑。
/// 執行中每個續租週期續租一次；結束時寫結果、清租約、排下次時間。租約、續租、結束各開一個短 DI scope（O1 計畫判斷 J16），
/// 工作本身另開一個 scope。時間一律取自 <see cref="TimeProvider"/>（UTC），計時器也用它，測試可以手動快轉。
/// </summary>
public sealed class JobRunner(
    IServiceScopeFactory scopes, IEnumerable<IScheduledJob> jobs, TimeProvider time, OpsInstance instance, IDatabaseErrors errors,
    ILogger<JobRunner> logger, SchedulerLimits? limits = null)
{
    public static readonly EventId JobFinished = new(5200, "JobFinished");

    private readonly SchedulerLimits _limits = limits ?? SchedulerLimits.Default;

    /// <summary>每一輪續租處理完（成功已延後截止、失敗已記 log、遺失已中止）時呼叫；給測試當同步屏障，正式環境不設。</summary>
    public Action? RenewRoundCompleted { get; init; }
    private readonly IReadOnlyList<IScheduledJob> _jobs = [.. jobs.OrderBy(j => j.Key, StringComparer.Ordinal)];

    private enum StopReason { None, Shutdown, Timeout, LeaseLost }

    private DateTime UtcNow => time.GetUtcNow().UtcDateTime;

    /// <summary>補齊缺的工作列（新列立刻到期）；撞鍵（別的實例先補）當成成功。失敗只記 log，下一次 tick 再補。</summary>
    public async Task EnsureJobsAsync(CancellationToken ct)
    {
        foreach (var job in _jobs) await EnsureAsync(job.Key, ct);
    }

    private async Task EnsureAsync(string key, CancellationToken ct)
    {
        try
        {
            await SendAsync(new EnsureScheduledJobRequest(key, UtcNow), ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            LogDatabaseFailure(e, "補排程工作列失敗：{JobKey}", key);
        }
    }

    public async Task TickAsync(CancellationToken ct)
    {
        try
        {
            List<ScheduledJob> rows;
            await using (var scope = scopes.CreateAsyncScope())
                rows = await scope.ServiceProvider.GetRequiredService<Domain.Repositories.Dynamic.IScheduledJobRepository>().ListAsync(ct);
            var byKey = rows.ToDictionary(r => r.JobKey, StringComparer.Ordinal);
            foreach (var job in _jobs)
            {
                // 關閉中不再領新工作
                if (ct.IsCancellationRequested) return;
                if (!byKey.TryGetValue(job.Key, out var row))
                {
                    await EnsureAsync(job.Key, ct);
                    continue;
                }
                var now = UtcNow;
                if (row.NextDueUtc > now) continue;
                var leaseUntil = now + _limits.Lease;
                var acquired = await SendAsync(new AcquireJobLeaseRequest(job.Key, instance.InstanceId, now, leaseUntil), ct);
                if (acquired.Data is true) await RunAsync(job, now, leaseUntil, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            LogDatabaseFailure(e, "排程檢查失敗");
        }
    }

    private async Task RunAsync(IScheduledJob job, DateTime startedUtc, DateTime leaseUntil, CancellationToken ct)
    {
        var runId = Guid.NewGuid().ToString("N");
        using var logScope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["jobKey"] = job.Key, ["runId"] = runId, ["instanceId"] = instance.InstanceId,
        });
        var reason = (int)StopReason.None;
        void Stop(StopReason why) => Interlocked.CompareExchange(ref reason, (int)why, (int)StopReason.None);

        using var timeout = new CancellationTokenSource(_limits.MaxRun, time);
        // 租約截止：租約到期前一個續租週期就中止工作，只有續租成功才往後延（續租卡住不回也一樣會到期），
        // 免得租約過期、別的實例接手時這裡還在跑
        using var leaseDeadline = new CancellationTokenSource(DeadlineDelay(leaseUntil), time);
        using var work = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token, leaseDeadline.Token);
        using var shutdownHook = ct.Register(() => Stop(StopReason.Shutdown));
        using var timeoutHook = timeout.Token.Register(() => Stop(StopReason.Timeout));
        using var deadlineHook = leaseDeadline.Token.Register(() => Stop(StopReason.LeaseLost));
        using var renewStop = new CancellationTokenSource();
        var renewal = RenewLoopAsync(job.Key,
            renewedUntil => leaseDeadline.CancelAfter(DeadlineDelay(renewedUntil)),
            () => { Stop(StopReason.LeaseLost); work.Cancel(); }, renewStop.Token);

        JobOutcome outcome;
        Exception? failure = null;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            outcome = await job.RunAsync(new JobRunContext(scope.ServiceProvider, job.Key, instance.InstanceId, time), work.Token);
        }
        catch (JobLeaseLostException e)
        {
            // 工作的寫入交易發現租約已失效（已回滾）
            Stop(StopReason.LeaseLost);
            outcome = new JobOutcome(ScheduledJobStatus.Cancelled);
            failure = e;
        }
        catch (Exception e) when (work.IsCancellationRequested)
        {
            outcome = new JobOutcome(ScheduledJobStatus.Cancelled);
            failure = e;
        }
        catch (Exception e)
        {
            outcome = new JobOutcome(ScheduledJobStatus.Failed, Summary(e));
            failure = e;
        }
        finally
        {
            // 先停續租再寫結果，免得續租讀到「不是持有者」而記一則假的租約遺失
            renewStop.Cancel();
            await renewal;
        }

        var why = (StopReason)Volatile.Read(ref reason);
        var finished = UtcNow;
        var duration = (finished - startedUtc).TotalMilliseconds;
        if (why == StopReason.LeaseLost)
        {
            // 別的實例已接手：不寫結果（條件式結束本來就會落空），也不能蓋掉新持有者的列
            Record(job.Key, "leaseLost", duration);
            logger.Log(LogLevel.Warning, JobFinished, failure, "排程工作 {JobKey} 租約遺失，已中止 {Result} {DurationMs}", job.Key, "leaseLost", duration);
            return;
        }
        if (outcome.Status == ScheduledJobStatus.Cancelled && why == StopReason.Timeout)
            outcome = new JobOutcome(ScheduledJobStatus.Failed, "timeout");
        var result = MetricResult(outcome, why);

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var next = finished;
            if (outcome.Status != ScheduledJobStatus.Cancelled)
            {
                var minutes = await scope.ServiceProvider.GetRequiredService<IPolicyReader>().IntAsync(job.Interval, null, CancellationToken.None);
                next = finished.AddMinutes(minutes);
            }
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await sender.Send(new FinishJobRunRequest(job.Key, instance.InstanceId,
                new JobRunResult(outcome.Status, outcome.Error, finished, next)), CancellationToken.None);
        }
        catch (Exception e)
        {
            // 寫不回結果：租約到期後下一次 tick 會再跑一次
            LogDatabaseFailure(e, "排程工作 {JobKey} 結果寫回失敗", job.Key);
        }

        Record(job.Key, result, duration);
        var level = outcome.Status switch
        {
            ScheduledJobStatus.Succeeded or ScheduledJobStatus.Cancelled => LogLevel.Information,
            ScheduledJobStatus.PartiallyFailed => LogLevel.Warning,
            _ => LogLevel.Error,
        };
        logger.Log(level, JobFinished, outcome.Status == ScheduledJobStatus.Failed ? failure : null,
            "排程工作 {JobKey} 結束 {Result} {DurationMs} {JobError}", job.Key, result, duration, outcome.Error);
    }

    /// <summary>租約截止＝租約到期前一個續租週期；已經過了就立刻截止。</summary>
    private TimeSpan DeadlineDelay(DateTime leaseUntil)
    {
        var delay = leaseUntil - _limits.Renew - UtcNow;
        return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
    }

    /// <summary>
    /// 每個續租週期續租一次，成功就把租約截止往後延。不是持有者 → 租約遺失、中止工作。資料庫暫時失敗只記 Warning、下一週期再試
    /// （不判工作失敗）；一直續不上（失敗或卡住不回）時由租約截止中止工作（O1 計畫判斷 J19；審查 P1）。
    /// </summary>
    private async Task RenewLoopAsync(string key, Action<DateTime> onRenewed, Action onLost, CancellationToken stop)
    {
        try
        {
            using var timer = new PeriodicTimer(_limits.Renew, time);
            while (await timer.WaitForNextTickAsync(stop))
            {
                var now = UtcNow;
                try
                {
                    var renewed = await SendAsync(new RenewJobLeaseRequest(key, instance.InstanceId, now + _limits.Lease), stop);
                    if (renewed.Data is true)
                    {
                        onRenewed(now + _limits.Lease);
                        continue;
                    }
                    logger.LogWarning("排程工作 {JobKey} 續租時已不是持有者", key);
                    onLost();
                    return;
                }
                catch (Exception e) when (!stop.IsCancellationRequested)
                {
                    LogDatabaseFailure(e, "排程工作 {JobKey} 續租失敗", key);
                }
                finally
                {
                    RenewRoundCompleted?.Invoke();
                }
            }
        }
        catch (Exception) when (stop.IsCancellationRequested)
        {
            // 停止續租時還在路上的那次續租：不論怎麼結束都不算數
        }
    }

    private async Task<ServiceResult> SendAsync(IRequest<ServiceResult> request, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request, ct);
    }

    /// <summary>例外型別＋正規化訊息（不含記錄值），截斷到 LastError 上限。</summary>
    internal static string Summary(Exception e)
    {
        var text = $"{e.GetType().FullName}: {ErrorFingerprint.NormalizeMessage(e.Message)}";
        return text.Length > ScheduledJob.LastErrorMax ? text[..ScheduledJob.LastErrorMax] : text;
    }

    private static string MetricResult(JobOutcome outcome, StopReason why) => outcome.Status switch
    {
        ScheduledJobStatus.Succeeded => "succeeded",
        ScheduledJobStatus.PartiallyFailed => "partiallyFailed",
        ScheduledJobStatus.Cancelled => "cancelled",
        _ when why == StopReason.Timeout => "timeout",
        _ => "failed",
    };

    private static void Record(string key, string result, double durationMs)
    {
        var tags = new KeyValuePair<string, object?>[] { new("job", key), new("result", result) };
        OpsMetrics.JobRuns.Add(1, tags);
        OpsMetrics.JobDurationMs.Record(durationMs, new KeyValuePair<string, object?>("job", key));
    }

    private void LogDatabaseFailure(Exception e, string message, params object?[] args)
    {
        if (errors.IsBusyOrConflict(e)) logger.LogWarning(e, message, args);
        else logger.LogError(e, message, args);
    }
}
