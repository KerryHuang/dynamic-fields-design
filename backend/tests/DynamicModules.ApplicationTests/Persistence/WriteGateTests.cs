using DynamicModules.Application.Common;
using DynamicModules.Persistence;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Persistence;

public class WriteGateTests
{
    // 整套測試並行時執行緒池會被擠壓；這些等待上限不是測試對象，放寬避免誤判
    private static readonly TimeSpan Patient = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Waiters_AreServedInArrivalOrder()
    {
        var gate = new WriteGate();
        var first = await gate.EnterAsync(Patient, default);
        var order = new List<int>();
        var waiters = new List<Task>();
        for (var i = 0; i < 8; i++)
        {
            var n = i;
            var entered = new TaskCompletionSource();
            waiters.Add(Task.Run(async () =>
            {
                var t = gate.EnterAsync(Patient, default);
                entered.SetResult();
                using var _ = await t;
                lock (order) order.Add(n);
            }));
            await entered.Task;
            await Task.Delay(20); // 確保 EnterAsync 已登記排隊再放下一個
        }

        first.Dispose();
        await Task.WhenAll(waiters);

        order.Should().Equal(Enumerable.Range(0, 8), "先到先得，不能被後到的搶先");
    }

    [Fact]
    public async Task WaitingTooLong_ThrowsDatabaseBusy_AndDoesNotBlockTheNextWaiter()
    {
        var gate = new WriteGate();
        var holder = await gate.EnterAsync(Patient, default);

        var timedOut = gate.EnterAsync(TimeSpan.FromMilliseconds(100), default);
        var next = gate.EnterAsync(Patient, default);
        await ((Func<Task>)(async () => await timedOut)).Should().ThrowAsync<DatabaseBusyException>();

        holder.Dispose();
        using var granted = await next.WaitAsync(Patient);
    }

    [Fact]
    public async Task CallerCancellation_StaysACancellation_NotBusy()
    {
        var gate = new WriteGate();
        using var holder = await gate.EnterAsync(Patient, default);
        using var cts = new CancellationTokenSource(50);

        var act = async () => await gate.EnterAsync(Patient, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
    [Fact]
    public async Task TimeoutBeyondWhatTimersAccept_IsClamped_AndTheGateStaysUsable()
    {
        // 連線字串 Default Timeout=5000000（秒）超過 CancelAfter 的上限（約 24.8 天）
        var huge = WriteGate.TimeoutOf("Data Source=x.db;Default Timeout=5000000");
        var gate = new WriteGate();
        var holder = await gate.EnterAsync(Patient, default);

        var waiter = gate.EnterAsync(huge, default);
        await Task.Delay(50);
        waiter.IsCompleted.Should().BeFalse("逾時被夾到上限，仍在正常排隊，不是丟例外");

        holder.Dispose();
        using var granted = await waiter.WaitAsync(Patient);
        // 閘門沒有卡死：拿到的人放開後，下一個人進得來
        granted.Dispose();
        using var again = await gate.EnterAsync(Patient, default).WaitAsync(Patient);
    }

    [Fact]
    public async Task AnInvalidTimeout_DoesNotLeaveADeadNodeThatSwallowsTheLock()
    {
        var gate = new WriteGate();
        var holder = await gate.EnterAsync(Patient, default);

        var bad = gate.EnterAsync(TimeSpan.FromSeconds(-5), default); // 不合法的逾時值：當作不設上限，照常排隊
        holder.Dispose();

        using var granted = await bad.WaitAsync(Patient); // 改前：丟 ArgumentOutOfRange，節點留在佇列，寫入權被交給它
        granted.Dispose();
        using var next = await gate.EnterAsync(Patient, default).WaitAsync(Patient);
    }

    [Theory]
    [InlineData("Data Source=x.db;Default Timeout=0")]
    public async Task ZeroTimeout_MeansNoLimit_NotInstantBusy(string connectionString)
    {
        var infinite = WriteGate.TimeoutOf(connectionString);
        var gate = new WriteGate();
        var holder = await gate.EnterAsync(Patient, default);

        var waiter = gate.EnterAsync(infinite, default);
        await Task.Delay(100);
        waiter.IsCompleted.Should().BeFalse("0 在 SQLite 是不設上限：應該一直排隊，不是立刻回忙碌");

        holder.Dispose();
        using var granted = await waiter.WaitAsync(Patient);
    }

    [Fact]
    public async Task EnterAsync_WithTimeSpanZero_AlsoMeansNoLimit()
    {
        var gate = new WriteGate();
        var holder = await gate.EnterAsync(Patient, default);

        var waiter = gate.EnterAsync(TimeSpan.Zero, default);
        await Task.Delay(100);
        waiter.IsCompleted.Should().BeFalse();

        holder.Dispose();
        using var granted = await waiter.WaitAsync(Patient);
    }
}
