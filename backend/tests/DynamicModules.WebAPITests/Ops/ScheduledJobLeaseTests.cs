using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.OpsService.Scheduling;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DynamicModules.WebAPITests.Ops;

/// <summary>檔案型 SQLite 的測試主機（每個 scope 一個真的 DbContext），排程相關測試共用。</summary>
public sealed class FileDatabaseApp : TestApp
{
    public FileDatabaseApp() : base(fileDatabase: true) { }
}

/// <summary>排程工作表的補列與租約（維運設計 §10.2.8、§10.4）：全部是條件式更新，同一工作同一時間只有一個持有者。</summary>
public class ScheduledJobLeaseTests(FileDatabaseApp app) : IClassFixture<FileDatabaseApp>
{
    private static readonly DateTime Now = new(2026, 11, 4, 1, 0, 0, DateTimeKind.Utc);

    private async Task<ServiceResult> Send(IRequest<ServiceResult> request)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request);
    }

    private async Task<bool> SendBool(IRequest<ServiceResult> request)
    {
        var r = await Send(request);
        r.IsSuccess.Should().BeTrue(r.Message);
        return (bool)r.Data!;
    }

    private async Task<ScheduledJob?> Row(string key)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<ScheduledJob>().AsNoTracking()
            .SingleOrDefaultAsync(j => j.JobKey == key);
    }

    private async Task Set(string key, Action<ScheduledJob> change)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        change(await db.Set<ScheduledJob>().SingleAsync(j => j.JobKey == key));
        await db.SaveChangesAsync();
    }

    private static string Key() => $"test.lease{Guid.NewGuid():N}"[..30];

    [Fact]
    public async Task Ensure_Twice_OneRow()
    {
        var key = Key();
        (await Send(new EnsureScheduledJobRequest(key, Now))).IsSuccess.Should().BeTrue();
        (await Send(new EnsureScheduledJobRequest(key, Now.AddHours(1)))).IsSuccess.Should().BeTrue();

        var row = await Row(key);
        row!.NextDueUtc.Should().Be(Now, "已存在就不動");
        row.ConsecutiveFailures.Should().Be(0);
    }

    [Fact]
    public async Task Ensure_RaceViaDirectInsert_ReturnsConflict_NoRowChange()
    {
        using var raced = new FileDatabaseApp();
        raced.ExtraServices = s =>
        {
            s.RemoveAll<IScheduledJobRepository>();
            s.AddScoped<IScheduledJobRepository>(sp => new NeverExists(
                ActivatorUtilities.CreateInstance<DynamicModules.Persistence.Repositories.Dynamic.ScheduledJobRepository>(sp)));
        };
        var key = Key();
        await using (var scope = raced.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            db.Set<ScheduledJob>().Add(new ScheduledJob { JobKey = key, NextDueUtc = Now });
            await db.SaveChangesAsync();
        }

        ServiceResult result;
        await using (var scope = raced.Services.CreateAsyncScope())
            result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new EnsureScheduledJobRequest(key, Now.AddHours(1)));

        result.IsConflict.Should().BeTrue("另一個實例先插了同一鍵");
        await using (var scope = raced.Services.CreateAsyncScope())
        {
            var rows = await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<ScheduledJob>().AsNoTracking()
                .Where(j => j.JobKey == key).ToListAsync();
            rows.Should().ContainSingle().Which.NextDueUtc.Should().Be(Now);
        }
    }

    [Fact]
    public async Task Acquire_FreeAndDue_True_ThenOtherOwner_False()
    {
        var key = Key();
        await Send(new EnsureScheduledJobRequest(key, Now));

        (await SendBool(new AcquireJobLeaseRequest(key, "a", Now, Now.AddMinutes(5)))).Should().BeTrue();
        (await SendBool(new AcquireJobLeaseRequest(key, "b", Now, Now.AddMinutes(5)))).Should().BeFalse("租約還在 a 手上");

        var row = await Row(key);
        row!.LeaseOwner.Should().Be("a");
        row.LeaseUntilUtc.Should().Be(Now.AddMinutes(5));
        row.LastStartedUtc.Should().Be(Now);
    }

    [Fact]
    public async Task Acquire_NotDue_False()
    {
        var key = Key();
        await Send(new EnsureScheduledJobRequest(key, Now.AddMinutes(1)));

        (await SendBool(new AcquireJobLeaseRequest(key, "a", Now, Now.AddMinutes(5)))).Should().BeFalse();
        (await Row(key))!.LeaseOwner.Should().BeNull();
    }

    [Fact]
    public async Task Acquire_AfterOtherFinished_StaleReader_False()
    {
        var key = Key();
        await Send(new EnsureScheduledJobRequest(key, Now));
        (await SendBool(new AcquireJobLeaseRequest(key, "a", Now, Now.AddMinutes(5)))).Should().BeTrue();
        (await SendBool(new FinishJobRunRequest(key, "a",
            new JobRunResult(ScheduledJobStatus.Succeeded, null, Now.AddSeconds(10), Now.AddHours(1))))).Should().BeTrue();

        (await SendBool(new AcquireJobLeaseRequest(key, "b", Now.AddSeconds(11), Now.AddMinutes(5))))
            .Should().BeFalse("b 讀到「到期」時 a 還沒跑完；a 跑完後下次預定時間已推到一小時後，b 不能再跑一次");
    }

    [Fact]
    public async Task Acquire_ExpiredLease_TakenOver()
    {
        var key = Key();
        await Send(new EnsureScheduledJobRequest(key, Now));
        await Set(key, j => { j.LeaseOwner = "dead"; j.LeaseUntilUtc = Now.AddSeconds(-1); });

        (await SendBool(new AcquireJobLeaseRequest(key, "b", Now, Now.AddMinutes(5)))).Should().BeTrue();
        (await Row(key))!.LeaseOwner.Should().Be("b");
    }

    [Fact]
    public async Task Renew_ByNonOwner_False()
    {
        var key = Key();
        await Send(new EnsureScheduledJobRequest(key, Now));
        await SendBool(new AcquireJobLeaseRequest(key, "a", Now, Now.AddMinutes(5)));

        (await SendBool(new RenewJobLeaseRequest(key, "b", Now.AddMinutes(6)))).Should().BeFalse();
        (await SendBool(new RenewJobLeaseRequest(key, "a", Now.AddMinutes(6)))).Should().BeTrue();
        (await Row(key))!.LeaseUntilUtc.Should().Be(Now.AddMinutes(6));
    }

    [Fact]
    public async Task Finish_ByNonOwner_False_RowUnchanged()
    {
        var key = Key();
        await Send(new EnsureScheduledJobRequest(key, Now));
        await SendBool(new AcquireJobLeaseRequest(key, "a", Now, Now.AddMinutes(5)));

        (await SendBool(new FinishJobRunRequest(key, "b",
            new JobRunResult(ScheduledJobStatus.Failed, "x", Now.AddSeconds(1), Now.AddHours(1))))).Should().BeFalse();

        var row = await Row(key);
        row!.LeaseOwner.Should().Be("a");
        row.LastStatus.Should().BeNull();
        row.ConsecutiveFailures.Should().Be(0);
        row.NextDueUtc.Should().Be(Now);
    }

    [Fact]
    public async Task Finish_Failed_IncrementsConsecutive_SucceededResets()
    {
        var key = Key();
        await Send(new EnsureScheduledJobRequest(key, Now));
        var t = Now;

        async Task Run(ScheduledJobStatus status, string? error)
        {
            (await SendBool(new AcquireJobLeaseRequest(key, "a", t, t.AddMinutes(5)))).Should().BeTrue();
            (await SendBool(new FinishJobRunRequest(key, "a", new JobRunResult(status, error, t, t)))).Should().BeTrue();
            t = t.AddSeconds(1);
        }

        await Run(ScheduledJobStatus.Failed, new string('x', 1200));
        await Run(ScheduledJobStatus.PartiallyFailed, "1/2 位使用者失敗");
        (await Row(key))!.ConsecutiveFailures.Should().Be(2);
        await Run(ScheduledJobStatus.Cancelled, null);
        var cancelled = await Row(key);
        cancelled!.ConsecutiveFailures.Should().Be(2, "程式關閉造成的取消不算失敗");
        cancelled.LastStatus.Should().Be(ScheduledJobStatus.Cancelled);
        await Run(ScheduledJobStatus.Succeeded, null);

        var row = await Row(key);
        row!.ConsecutiveFailures.Should().Be(0);
        row.LastStatus.Should().Be(ScheduledJobStatus.Succeeded);
        row.LeaseOwner.Should().BeNull();
        row.LeaseUntilUtc.Should().BeNull();
    }

    [Fact]
    public async Task Finish_LongError_TruncatedTo1000()
    {
        var key = Key();
        await Send(new EnsureScheduledJobRequest(key, Now));
        await SendBool(new AcquireJobLeaseRequest(key, "a", Now, Now.AddMinutes(5)));
        await SendBool(new FinishJobRunRequest(key, "a", new JobRunResult(ScheduledJobStatus.Failed, new string('x', 1200), Now, Now)));

        (await Row(key))!.LastError.Should().HaveLength(ScheduledJob.LastErrorMax);
    }

    [Fact]
    public async Task ReadBack_DateTimeKindIsUtc()
    {
        var key = Key();
        await Send(new EnsureScheduledJobRequest(key, Now));
        await SendBool(new AcquireJobLeaseRequest(key, "a", Now, Now.AddMinutes(5)));

        var row = await Row(key);
        row!.NextDueUtc.Kind.Should().Be(DateTimeKind.Utc);
        row.LeaseUntilUtc!.Value.Kind.Should().Be(DateTimeKind.Utc);
        row.LastStartedUtc!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    /// <summary>模擬競態：讀的時候還沒有這一列，插入時才撞鍵。</summary>
    private sealed class NeverExists(IScheduledJobRepository inner) : IScheduledJobRepository
    {
        public Task<List<ScheduledJob>> ListAsync(CancellationToken ct) => inner.ListAsync(ct);
        public Task<bool> ExistsAsync(string jobKey, CancellationToken ct) => Task.FromResult(false);
        public void Add(ScheduledJob job) => inner.Add(job);
        public Task<bool> TryAcquireAsync(string jobKey, string owner, DateTime nowUtc, DateTime leaseUntilUtc, CancellationToken ct) =>
            inner.TryAcquireAsync(jobKey, owner, nowUtc, leaseUntilUtc, ct);
        public Task<bool> RenewAsync(string jobKey, string owner, DateTime leaseUntilUtc, CancellationToken ct) => inner.RenewAsync(jobKey, owner, leaseUntilUtc, ct);
        public Task<bool> FinishAsync(string jobKey, string owner, JobRunResult result, CancellationToken ct) => inner.FinishAsync(jobKey, owner, result, ct);
    }
}
