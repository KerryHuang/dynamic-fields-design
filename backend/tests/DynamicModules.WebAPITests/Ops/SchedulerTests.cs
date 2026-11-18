using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.OpsService.Scheduling;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.WebAPI.Ops;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static DynamicModules.WebAPITests.Ops.SchedulerTestKit;

namespace DynamicModules.WebAPITests.Ops;

/// <summary>
/// 背景排程器（維運設計 §10.2.8）：同一到期工作只跑一次、持有者當掉後租約過期由別人接手、失敗計數、逾時、關閉、租約遺失、續租暫時失敗。
/// 用手動時鐘（計時器也跟著它）與手動 tick，不靠真實等待。
/// 雙跑集（PostgreSQL provider 設計 §6.4、§7）；PostgreSQL 上另由 <see cref="SchedulerNoLockTests"/> 在無鎖模式整組重跑。
/// </summary>
[Trait("Category", "Postgres")]
public class SchedulerTests : IDisposable
{
    private static readonly PolicyDefinition Interval = PolicyCatalog.NotificationReminderScanMinutes;
    private const int IntervalMinutes = 60;

    private readonly FileDatabaseApp _app;
    private readonly RenewSpy _spy = new();
    private readonly ManualClock _clock = new(Start);

    public SchedulerTests() : this(new FileDatabaseApp()) { }

    protected SchedulerTests(FileDatabaseApp app)
    {
        _app = app;
        _app.ExtraServices = s => s.AddSingleton<IPipelineBehavior<RenewJobLeaseRequest, ServiceResult>>(_spy);
    }

    public void Dispose() => _app.Dispose();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private int RenewFailures() => JobLogs(_app).Count(r => r.Level == LogLevel.Warning && r.Message.Contains("續租失敗"));

    private async Task<(JobRunner Runner, CountingJob Job)> Single(string key = "test.counting", SchedulerLimits? limits = null,
        Action? renewRound = null)
    {
        var job = new CountingJob(key, Interval);
        var runner = Runner(_app, _clock, [job], limits, renewRound);
        await runner.EnsureJobsAsync(CancellationToken.None);
        return (runner, job);
    }

    [Fact]
    public async Task TwoRunners_SameDueJob_RunsOnce()
    {
        var job = new CountingJob("test.counting", Interval);
        var a = Runner(_app, _clock, [job]);
        var b = Runner(_app, _clock, [job]);
        await Task.WhenAll(a.EnsureJobsAsync(CancellationToken.None), b.EnsureJobsAsync(CancellationToken.None));

        await Within(Task.WhenAll(a.TickAsync(CancellationToken.None), b.TickAsync(CancellationToken.None)));

        job.Runs.Should().Be(1);
        (await Row(_app, job.Key)).LastStatus.Should().Be(ScheduledJobStatus.Succeeded);
    }

    [Fact]
    public async Task AfterRun_NextDueIsFinishPlusInterval_NotRunAgainSameTick()
    {
        var (runner, job) = await Single();

        await runner.TickAsync(CancellationToken.None);
        var row = await Row(_app, job.Key);
        row.NextDueUtc.Should().Be(row.LastFinishedUtc!.Value.AddMinutes(IntervalMinutes));
        row.LeaseOwner.Should().BeNull();

        await runner.TickAsync(CancellationToken.None);
        job.Runs.Should().Be(1, "還沒到下次預定時間");

        _clock.Advance(TimeSpan.FromMinutes(IntervalMinutes + 1));
        await runner.TickAsync(CancellationToken.None);
        job.Runs.Should().Be(2);
    }

    [Fact]
    public async Task HolderDied_LeaseExpires_OtherTakesOver()
    {
        var (runner, job) = await Single();
        await Update(_app, job.Key, j => { j.LeaseOwner = "dead"; j.LeaseUntilUtc = Now.AddMinutes(5); j.NextDueUtc = Now; });

        await runner.TickAsync(CancellationToken.None);
        job.Runs.Should().Be(0, "租約還沒過期");

        _clock.Advance(TimeSpan.FromMinutes(6));
        await runner.TickAsync(CancellationToken.None);
        job.Runs.Should().Be(1);
        (await Row(_app, job.Key)).LeaseOwner.Should().BeNull();
    }

    [Fact]
    public async Task JobThrows_FailedAndCounted_OtherJobsRun()
    {
        var bad = new CountingJob("test.a", Interval) { Body = _ => throw new InvalidOperationException("壞掉了 12345 \"客戶甲\"") };
        var good = new CountingJob("test.b", Interval);
        var runner = Runner(_app, _clock, [bad, good]);
        await runner.EnsureJobsAsync(CancellationToken.None);

        await runner.TickAsync(CancellationToken.None);

        var failed = await Row(_app, bad.Key);
        failed.LastStatus.Should().Be(ScheduledJobStatus.Failed);
        failed.ConsecutiveFailures.Should().Be(1);
        failed.LastError.Should().StartWith("System.InvalidOperationException: ").And.NotContain("12345").And.NotContain("客戶甲");
        failed.LastError!.Length.Should().BeLessThanOrEqualTo(ScheduledJob.LastErrorMax);
        failed.NextDueUtc.Should().Be(failed.LastFinishedUtc!.Value.AddMinutes(IntervalMinutes), "失敗也照間隔再試");
        (await Row(_app, good.Key)).LastStatus.Should().Be(ScheduledJobStatus.Succeeded);
        JobLogs(_app).Should().Contain(r => r.Level == LogLevel.Error && r.EventId.Id == JobRunner.JobFinished.Id
            && (string?)r.Values["JobKey"] == bad.Key && (string?)r.Values["jobKey"] == bad.Key && r.Values.ContainsKey("runId"));
    }

    [Fact]
    public async Task Timeout_Failed()
    {
        // 續租與租約都拉長到逾時之後，只看逾時本身
        var limits = SchedulerLimits.Default with { Lease = TimeSpan.FromHours(2), Renew = TimeSpan.FromHours(1), MaxRun = TimeSpan.FromMinutes(10) };
        var (runner, job) = await Single(limits: limits);
        job.Body = CountingJob.UntilCancelled;

        var tick = runner.TickAsync(CancellationToken.None);
        await Within(job.Started.Task);
        _clock.Advance(TimeSpan.FromMinutes(10));
        await Within(tick);

        var row = await Row(_app, job.Key);
        row.LastStatus.Should().Be(ScheduledJobStatus.Failed);
        row.LastError.Should().Be("timeout");
        row.ConsecutiveFailures.Should().Be(1);
        JobLogs(_app).Should().Contain(r => r.EventId.Id == JobRunner.JobFinished.Id && (string?)r.Values["Result"] == "timeout");
    }

    [Fact]
    public async Task Shutdown_MidRun_CancelledAndLeaseReleased()
    {
        var (runner, job) = await Single();
        job.Body = CountingJob.UntilCancelled;
        using var shutdown = new CancellationTokenSource();

        var tick = runner.TickAsync(shutdown.Token);
        await Within(job.Started.Task);
        shutdown.Cancel();
        await Within(tick);

        var row = await Row(_app, job.Key);
        row.LastStatus.Should().Be(ScheduledJobStatus.Cancelled);
        row.ConsecutiveFailures.Should().Be(0);
        row.LeaseOwner.Should().BeNull();
        row.NextDueUtc.Should().Be(row.LastFinishedUtc!.Value, "重啟後下一個 tick 就接手");
    }

    [Fact]
    public async Task LeaseLostMidRun_WorkCancelled_DoesNotOverwriteNewOwner()
    {
        var (runner, job) = await Single();
        job.Body = CountingJob.UntilCancelled;

        var tick = runner.TickAsync(CancellationToken.None);
        await Within(job.Started.Task);
        await Update(_app, job.Key, j => { j.LeaseOwner = "other"; j.LeaseUntilUtc = Now.AddMinutes(5); });
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Within(tick);

        var row = await Row(_app, job.Key);
        row.LeaseOwner.Should().Be("other");
        row.LastStatus.Should().BeNull("這次執行不寫結果");
        row.ConsecutiveFailures.Should().Be(0);
        JobLogs(_app).Should().Contain(r => r.Level == LogLevel.Warning && r.EventId.Id == JobRunner.JobFinished.Id
            && (string?)r.Values["Result"] == "leaseLost");
    }

    [Fact]
    public async Task RenewTransientFailure_WorkCompletes_LeaseKept()
    {
        _spy.FailFirst = 2;
        var rounds = new RenewRounds();
        var (runner, job) = await Single(renewRound: rounds.Completed);
        var release = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        job.Body = _ => release.Task;

        var tick = runner.TickAsync(CancellationToken.None);
        await Within(job.Started.Task);
        // 租約 5 分、續租每 1 分：前兩次失敗，截止仍是第 4 分；第 3 分續租成功把截止延到第 7 分，之後每分往後延
        for (var i = 0; i < 6; i++)
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            await rounds.Next();   // 這一輪判斷完（成功已延後截止、失敗已記 log）才往下推時鐘
            tick.IsCompleted.Should().BeFalse($"第 {i + 1} 輪後租約仍有效");
        }
        var other = Runner(_app, _clock, [job]);
        await other.TickAsync(CancellationToken.None);
        job.Runs.Should().Be(1, "租約仍有效，別的實例搶不到");

        release.SetResult(new JobOutcome(ScheduledJobStatus.Succeeded));
        await Within(tick);

        (await Row(_app, job.Key)).LastStatus.Should().Be(ScheduledJobStatus.Succeeded);
        RenewFailures().Should().Be(2);
        JobLogs(_app).Should().NotContain(r => r.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task RenewFailsUntilLeaseNearlyExpired_CancelledAsLeaseLost()
    {
        _spy.FailFirst = int.MaxValue;
        var rounds = new RenewRounds();
        var (runner, job) = await Single(renewRound: rounds.Completed);
        job.Body = CountingJob.UntilCancelled;

        var tick = runner.TickAsync(CancellationToken.None);
        await Within(job.Started.Task);
        // 租約 5 分、續租每 1 分、一直續不上：截止在租約到期前一個續租週期（第 4 分）
        for (var i = 0; i < 3; i++)
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            await rounds.Next();
            tick.IsCompleted.Should().BeFalse($"第 {i + 1} 分租約還夠");
        }
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Within(tick);

        JobLogs(_app).Should().Contain(r => r.EventId.Id == JobRunner.JobFinished.Id && (string?)r.Values["Result"] == "leaseLost");
        (await Row(_app, job.Key)).LastStatus.Should().BeNull();
    }

    /// <summary>
    /// 續租卡住不回（不丟錯）超過租期（審查 P1）：舊持有者在租約到期前被租約截止中止，別的實例等租約過期才接手，
    /// 兩邊不會同時在跑，工作也只完成一次。
    /// </summary>
    [Fact]
    public async Task RenewHangs_PastLease_HolderCancelledBeforeOtherTakesOver_RunsOnce()
    {
        _spy.Hang = true;
        var (a, job) = await Single();
        var running = 0;
        var overlapped = false;
        var completed = 0;
        job.Body = async ct =>
        {
            overlapped |= Interlocked.Increment(ref running) > 1;
            try
            {
                if (job.Runs == 1) await Task.Delay(Timeout.Infinite, ct);   // A：一直做到被取消
                Interlocked.Increment(ref completed);
                return new JobOutcome(ScheduledJobStatus.Succeeded);
            }
            finally
            {
                Interlocked.Decrement(ref running);
            }
        };

        var tickA = a.TickAsync(CancellationToken.None);
        await Within(job.Started.Task);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Within(_spy.Hanging.Task);   // 第一次續租卡住，不會再延後截止
        _clock.Advance(TimeSpan.FromMinutes(3));
        await Within(tickA);   // 第 4 分：租約截止，A 中止

        JobLogs(_app).Should().Contain(r => r.EventId.Id == JobRunner.JobFinished.Id && (string?)r.Values["Result"] == "leaseLost");
        var b = Runner(_app, _clock, [job]);
        await b.TickAsync(CancellationToken.None);
        job.Runs.Should().Be(1, "A 的租約到第 5 分才過期，B 還搶不到");

        _spy.Hang = false;
        _clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        await Within(b.TickAsync(CancellationToken.None));

        job.Runs.Should().Be(2);
        completed.Should().Be(1, "只有 B 做完");
        overlapped.Should().BeFalse("A 停下來之後 B 才開始");
        var row = await Row(_app, job.Key);
        row.LastStatus.Should().Be(ScheduledJobStatus.Succeeded);
        row.LeaseOwner.Should().BeNull();
    }

    [Fact]
    public async Task IntervalPolicyChange_AppliesFromNextSchedule()
    {
        var (runner, job) = await Single();
        await runner.TickAsync(CancellationToken.None);
        var first = await Row(_app, job.Key);

        await SetPolicy(_app, Interval, "5");
        (await Row(_app, job.Key)).NextDueUtc.Should().Be(first.NextDueUtc, "改值不動已排好的下一次");

        _clock.Advance(TimeSpan.FromMinutes(IntervalMinutes + 1));
        await runner.TickAsync(CancellationToken.None);
        var second = await Row(_app, job.Key);
        second.NextDueUtc.Should().Be(second.LastFinishedUtc!.Value.AddMinutes(5));
    }

    [Fact]
    public async Task HostedService_Disabled_ExitsWithoutRunning()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [SchedulerHostedService.EnabledKey] = "false" }).Build();
        var job = new CountingJob("test.counting", Interval);
        var service = new SchedulerHostedService(Runner(_app, _clock, [job]), config, _clock, NullLogger<SchedulerHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);

        service.ExecuteTask!.IsCompleted.Should().BeTrue();
        job.Runs.Should().Be(0);
        await service.StopAsync(CancellationToken.None);
    }
}

/// <summary>
/// 無鎖模式（PostgreSQL provider 設計 §5.6、§7，主控判斷 T4）：閘門與全庫鎖都換成不做事的版本，兩個排程器實例才是真的同時，
/// 證明「只跑一次」「租約過期才接手」靠的是條件式更新本身、不是被序列化蓋掉。SQLite 沒有無鎖模式，這組在 SQLite 上等同重跑一次。
/// </summary>
[Trait("Category", "Postgres")]
public sealed class SchedulerNoLockTests() : SchedulerTests(new FileDatabaseApp { WithoutWriteSerialization = true });
