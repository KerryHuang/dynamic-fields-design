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
}
