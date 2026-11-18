using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.OpsService.Scheduling;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static DynamicModules.WebAPITests.Ops.SchedulerTestKit;

namespace DynamicModules.WebAPITests.Ops;

/// <summary>
/// 排程器與 PostgreSQL 全庫寫入鎖的互動（PostgreSQL provider 設計 §7）：續租走 <see cref="ITransactionalRequest"/>，
/// 遇到占住寫入權超過 <c>Database:LockTimeoutSeconds</c> 的長交易（匯入、開應用程式、刪模組、產生 Demo 資料）會等滿逾時後失敗；
/// 失敗只記 log、下一輪再試，工作照常完成，租約也沒有被別的實例搶走。只在 PostgreSQL 有意義（SQLite 的同一情境由
/// <see cref="SchedulerTests.RenewTransientFailure_WorkCompletes_LeaseKept"/> 以模擬的忙碌例外涵蓋）。
/// </summary>
[Trait("Category", "Postgres")]
public sealed class SchedulerWriteLockTests : IDisposable
{
    private readonly FileDatabaseApp _app = new();
    private readonly ManualClock _clock = new(Start);

    public SchedulerWriteLockTests()
    {
        // 等寫入權最多 1 秒，測試才不必等預設的 30 秒
        _app.Settings["Database:LockTimeoutSeconds"] = "1";
    }

    public void Dispose() => _app.Dispose();

    [PostgresFact]
    public async Task LongTransactionHoldsWriteLock_RenewFails_WorkCompletes_LeaseKept()
    {
        var rounds = new RenewRounds();
        var job = new CountingJob("test.counting", PolicyCatalog.NotificationReminderScanMinutes);
        var runner = Runner(_app, _clock, [job], renewRound: rounds.Completed);
        await runner.EnsureJobsAsync(CancellationToken.None);
        var release = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        job.Body = _ => release.Task;

        var tick = runner.TickAsync(CancellationToken.None);
        await Within(job.Started.Task);

        // 同一實例的長交易：先過閘門、再取全庫鎖，持有超過 LockTimeoutSeconds
        await using (var longScope = _app.Services.CreateAsyncScope())
        await using (await longScope.ServiceProvider.GetRequiredService<ITransactionManager>().BeginAsync(CancellationToken.None))
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            await rounds.Next();   // 這一輪續租等滿 1 秒後放棄
        }
        RenewFailures().Should().Be(1, "長交易占住寫入權時續租失敗、記 log");

        _clock.Advance(TimeSpan.FromMinutes(1));
        await rounds.Next();
        tick.IsCompleted.Should().BeFalse("續租失敗一次不放棄工作");
        var other = Runner(_app, _clock, [job]);
        await other.TickAsync(CancellationToken.None);
        job.Runs.Should().Be(1, "租約仍有效，別的實例搶不到");

        release.SetResult(new JobOutcome(ScheduledJobStatus.Succeeded));
        await Within(tick);

        (await Row(_app, job.Key)).LastStatus.Should().Be(ScheduledJobStatus.Succeeded);
        RenewFailures().Should().Be(1);
        JobLogs(_app).Should().NotContain(r => r.Level >= LogLevel.Error);
    }

    private int RenewFailures() => JobLogs(_app).Count(r => r.Level == LogLevel.Warning && r.Message.Contains("續租失敗"));
}
