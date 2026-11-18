using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.OpsService;
using DynamicModules.Application.Dynamic.OpsService.Scheduling;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicModules.WebAPITests.Ops;

/// <summary>排程器測試的共用工具：手動時鐘、自己建的 JobRunner（TestApp 的背景排程器預設關閉）、直接讀寫工作列。</summary>
internal static class SchedulerTestKit
{
    public static readonly DateTimeOffset Start = new(2026, 11, 4, 1, 0, 0, TimeSpan.Zero);

    /// <summary>防呆上限：只在真的卡住時才會用到，正常流程不靠它判斷對錯。</summary>
    public static Task Within(Task task) => task.WaitAsync(TimeSpan.FromMinutes(1));

    public static async Task<T> Within<T>(Task<T> task) => await task.WaitAsync(TimeSpan.FromMinutes(1));

    /// <summary>等到條件成立（背景流程處理完上一步才往下推時鐘）；上限同 <see cref="Within"/>。</summary>
    public static Task Until(Func<bool> condition) => Within(Task.Run(async () =>
    {
        while (!condition()) await Task.Delay(5);
    }));

    public static JobRunner Runner(TestApp app, TimeProvider clock, IEnumerable<IScheduledJob> jobs, SchedulerLimits? limits = null) =>
        new(app.Services.GetRequiredService<IServiceScopeFactory>(), jobs, clock, OpsInstance.Create(clock),
            app.Services.GetRequiredService<IDatabaseErrors>(), app.Services.GetRequiredService<ILogger<JobRunner>>(), limits);

    public static async Task<ScheduledJob> Row(TestApp app, string key)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<ScheduledJob>().AsNoTracking().SingleAsync(j => j.JobKey == key);
    }

    public static async Task Update(TestApp app, string key, Action<ScheduledJob> change)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        change(await db.Set<ScheduledJob>().SingleAsync(j => j.JobKey == key));
        await db.SaveChangesAsync();
    }

    /// <summary>直接寫系統政策值（測試不經政策 API）。</summary>
    public static async Task SetPolicy(TestApp app, PolicyDefinition def, string value)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        var row = await db.Set<SystemSetting>().SingleOrDefaultAsync(s => s.Key == def.SettingKey);
        if (row is null) db.Set<SystemSetting>().Add(new SystemSetting { Key = def.SettingKey, Value = value });
        else row.Value = value;
        await db.SaveChangesAsync();
    }

    public static IEnumerable<LogRecord> JobLogs(TestApp app) =>
        app.Logs.Records.Where(r => r.Category == typeof(JobRunner).FullName);
}

/// <summary>測試用工作：記錄執行次數；Body 決定要做什麼（預設立刻成功）。</summary>
internal sealed class CountingJob(string key, PolicyDefinition interval) : IScheduledJob
{
    private int _runs;

    public string Key => key;
    public PolicyDefinition Interval => interval;
    public int Runs => Volatile.Read(ref _runs);
    public Func<CancellationToken, Task<JobOutcome>>? Body { get; set; }
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<JobOutcome> RunAsync(IServiceProvider services, CancellationToken ct)
    {
        Interlocked.Increment(ref _runs);
        Started.TrySetResult();
        return Body is null ? new JobOutcome(ScheduledJobStatus.Succeeded) : await Body(ct);
    }

    /// <summary>一直等到被取消。</summary>
    public static async Task<JobOutcome> UntilCancelled(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return new JobOutcome(ScheduledJobStatus.Succeeded);
    }
}

/// <summary>攔續租：每次續租結束放一個訊號；可設定前幾次丟資料庫忙碌（模擬長交易占住寫入鎖）。</summary>
internal sealed class RenewSpy : IPipelineBehavior<RenewJobLeaseRequest, ServiceResult>
{
    private int _calls;

    public int FailFirst { get; set; }
    public SemaphoreSlim Done { get; } = new(0);

    public async Task<ServiceResult> Handle(RenewJobLeaseRequest request, RequestHandlerDelegate<ServiceResult> next, CancellationToken ct)
    {
        try
        {
            if (Interlocked.Increment(ref _calls) <= FailFirst) throw new DatabaseBusyException(DatabaseBusyException.DefaultMessage);
            return await next();
        }
        finally
        {
            Done.Release();
        }
    }
}
