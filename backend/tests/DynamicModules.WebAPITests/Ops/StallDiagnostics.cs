using System.Text;
using DynamicModules.TestSupport;
using Npgsql;

namespace DynamicModules.WebAPITests.Ops;

/// <summary>
/// 票 #72 的診斷：並行案例逾時時，把各分支的狀態、執行緒池、xUnit 同步內容與 PostgreSQL 端的連線與鎖一起印進例外訊息，
/// 讓 CI 上偶發的卡住留下證據。只在真的逾時才查資料庫。
/// </summary>
internal static class StallDiagnostics
{
    public static async Task WithinAsync(TestApp app, params (string Name, Task Task)[] branches)
    {
        using var probe = new ContextProbe(SynchronizationContext.Current);
        try
        {
            await SchedulerTestKit.Within(Task.WhenAll(branches.Select(b => b.Task)));
        }
        catch (TimeoutException e)
        {
            throw new TimeoutException(await DescribeAsync(app, branches, probe.Describe()), e);
        }
    }

    /// <summary>每 500 ms 往測試的同步內容與執行緒池各丟一個空工作，記排隊延遲：分辨「xUnit 的執行緒被占滿」與「執行緒池飢餓」。</summary>
    internal sealed class ContextProbe : IDisposable
    {
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _slow = new();
        private readonly Timer _timer;
        private readonly SynchronizationContext? _context;
        private double _maxContext, _maxPool;
        private int _contextPending;

        public ContextProbe(SynchronizationContext? context)
        {
            _context = context;
            _timer = new Timer(_ => Tick(), null, 0, 500);
        }

        private void Tick()
        {
            var sent = _clock.Elapsed.TotalMilliseconds;
            ThreadPool.UnsafeQueueUserWorkItem(_ => Record(ref _maxPool, "pool", sent), null);
            if (_context is null || Interlocked.CompareExchange(ref _contextPending, 1, 0) != 0) return;
            _context.Post(_ =>
            {
                Record(ref _maxContext, "context", sent);
                Volatile.Write(ref _contextPending, 0);
            }, null);
        }

        private void Record(ref double max, string what, double sent)
        {
            var now = _clock.Elapsed.TotalMilliseconds;
            var delay = now - sent;
            if (delay > max) max = delay;
            if (delay > 1000 && _slow.Count < 40) _slow.Enqueue($"{what} 於 {sent / 1000:F1}s 排入、等了 {delay / 1000:F1}s");
        }

        public string Describe() =>
            $"  同步內容探針：{_context?.GetType().Name ?? "(none)"} 最大延遲 {_maxContext / 1000:F1}s（尚未執行={Volatile.Read(ref _contextPending)}）；" +
            $"執行緒池最大延遲 {_maxPool / 1000:F1}s\n" + string.Concat(_slow.Select(x => $"    {x}\n"));

        public void Dispose() => _timer.Dispose();
    }

    private static async Task<string> DescribeAsync(TestApp app, (string Name, Task Task)[] branches, string probe)
    {
        var sb = new StringBuilder("並行案例逾時（票 #72 診斷）\n").Append(probe);
        foreach (var (name, task) in branches) sb.AppendLine($"  分支 {name}：{task.Status}");
        ThreadPool.GetAvailableThreads(out var worker, out var io);
        ThreadPool.GetMinThreads(out var minWorker, out _);
        sb.AppendLine($"  執行緒池：threads={ThreadPool.ThreadCount} pending={ThreadPool.PendingWorkItemCount} available={worker}/{io} min={minWorker}");
        sb.AppendLine($"  同步內容：{SynchronizationContext.Current?.GetType().FullName ?? "(none)"}");
        foreach (var r in app.Logs.Records.Where(r => r.Level >= Microsoft.Extensions.Logging.LogLevel.Warning).TakeLast(20))
            sb.AppendLine($"  log {r.Level} {r.Category}：{r.Message}");
        if (!TestDatabase.IsPostgres) return sb.ToString();
        try
        {
            await using var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(app.ConnectionString) { Pooling = false, Timeout = 5 }.ConnectionString);
            await conn.OpenAsync();
            await DumpAsync(conn, sb, "pg_stat_activity",
                """
                SELECT pid, state, wait_event_type, wait_event, pg_blocking_pids(pid)::text AS blocked_by,
                       round(extract(epoch FROM now() - xact_start)::numeric, 1) AS xact_s,
                       round(extract(epoch FROM now() - state_change)::numeric, 1) AS state_s, left(query, 300) AS query
                FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid() ORDER BY pid
                """);
            await DumpAsync(conn, sb, "pg_locks",
                """
                SELECT l.pid, l.locktype, l.mode, l.granted, c.relname, l.page, l.tuple, l.transactionid::text AS xid
                FROM pg_locks l LEFT JOIN pg_class c ON c.oid = l.relation
                WHERE l.pid <> pg_backend_pid() AND (l.database IS NULL OR l.database = (SELECT oid FROM pg_database WHERE datname = current_database()))
                  AND l.locktype <> 'virtualxid' ORDER BY l.pid, l.granted
                """);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  查 PostgreSQL 失敗：{ex.GetType().Name} {ex.Message}");
        }
        return sb.ToString();
    }

    private static async Task DumpAsync(NpgsqlConnection conn, StringBuilder sb, string title, string sql)
    {
        sb.AppendLine($"  -- {title}");
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var cols = Enumerable.Range(0, reader.FieldCount).Select(i => $"{reader.GetName(i)}={(reader.IsDBNull(i) ? "null" : reader.GetValue(i))}");
            sb.AppendLine("  " + string.Join(" | ", cols));
        }
    }
}
