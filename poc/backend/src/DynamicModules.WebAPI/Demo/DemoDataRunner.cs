using System.Diagnostics;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Acting;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.DemoService;
using DynamicModules.Application.Dynamic.DemoService.Commands.GenerateDemoData;
using MediatR;

namespace DynamicModules.WebAPI.Demo;

/// <summary>
/// 在背景產生 Demo 資料（singleton）。每次產生開新的 DI scope；背景流程不帶觸發端的 ExecutionContext
/// （HttpContext、ActingScope 都是 AsyncLocal）。結果只記 log、寫回 Current，不丟出。
/// </summary>
public sealed class DemoDataRunner(
    IServiceScopeFactory scopes, IBusinessTime time, ILogger<DemoDataRunner> log, IHostApplicationLifetime life)
    : IDemoDataRunner
{
    private readonly object _gate = new();
    private int _running;
    private DemoRunSnapshot _current = DemoRunSnapshot.Idle;

    public DemoRunSnapshot Current
    {
        get { lock (_gate) return _current; }
    }

    public bool TryStart(bool startup = false)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return false;
        Set(_ => new DemoRunSnapshot(DemoRunState.Running, [], null, time.Now, null, 0, 0));
        using (ExecutionContext.SuppressFlow())
            _ = Task.Run(() => RunAsync(startup));
        return true;
    }

    private async Task RunAsync(bool startup)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            ServiceResult result;
            await using (var scope = scopes.CreateAsyncScope())
            using (ActingScope.Begin("admin"))
            {
                var sender = scope.ServiceProvider.GetRequiredService<ISender>();
                result = await sender.Send(
                    new GenerateDemoDataRequest(time.Today, new SnapshotProgress(this)), life.ApplicationStopping);
            }

            if (result.IsSuccess)
            {
                Finish(DemoRunState.Succeeded, [], null);
                var steps = result.Data is DemoGenerateResult r ? r.Steps : 0;
                log.LogInformation("Demo 資料產生完成：{Steps} 個步驟，耗時 {Seconds:F1} 秒", steps, watch.Elapsed.TotalSeconds);
            }
            else if (result.IsConflict)
            {
                var conflicts = result.Data is DemoConflicts c ? c.Items : [];
                Finish(DemoRunState.Conflict, conflicts, result.Message);
                if (startup) log.LogInformation("已有 Demo 資料或同代碼模組，略過自動產生");
                else log.LogWarning("已有同代碼的模組，未產生 Demo 資料：{Conflicts}", string.Join("；", conflicts));
            }
            else
            {
                var message = result.Message ?? string.Join("；", result.Errors.SelectMany(e => e.Value));
                Finish(DemoRunState.Failed, [], message);
                log.LogError("Demo 資料產生失敗：{Message}", message);
            }
        }
        catch (Exception ex)
        {
            Finish(DemoRunState.Failed, [], ex.Message);
            log.LogError(ex, "Demo 資料產生失敗");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0); // 先寫完結果再放鎖，下一次 TryStart 不會被蓋掉
        }
    }

    private void Finish(DemoRunState state, IReadOnlyList<string> conflicts, string? message) =>
        Set(s => s with { State = state, Conflicts = conflicts, Message = message, FinishedAt = time.Now });

    private void Set(Func<DemoRunSnapshot, DemoRunSnapshot> change)
    {
        lock (_gate) _current = change(_current);
    }

    /// <summary>同步寫回（Progress&lt;T&gt; 會丟到 thread pool，可能在完成後才到）。</summary>
    private sealed class SnapshotProgress(DemoDataRunner runner) : IProgress<(int Done, int Total)>
    {
        public void Report((int Done, int Total) value) =>
            runner.Set(s => s with { Done = value.Done, Total = value.Total });
    }
}
