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
        LatencyTrace.Mark("#72 並行案例開始");
        try
        {
            await SchedulerTestKit.Within(Task.WhenAll(branches.Select(b => b.Task)));
            LatencyTrace.Mark("#72 並行案例結束");
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

    /// <summary>
    /// 票 #70／#72 的整輪證據：第一個 TestApp 建立時（在 xUnit 的同步內容上）開始，每 500 ms 量一次同步內容與執行緒池的排隊延遲，
    /// 以 5 秒為一格記最大值；設了 FORMWRIGHT_LATENCY_TRACE（檔案路徑）才記，行程結束時附加寫入（CI 的 PostgreSQL job 之後印出）。通過的輪次也留證據。
    /// </summary>
    internal static class LatencyTrace
    {
        private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (double Context, double Pool)> Buckets = new();
        private static readonly System.Collections.Concurrent.ConcurrentQueue<string> Marks = new();
        private static SynchronizationContext? _context;
        private static Timer? _timer;
        private static int _contextPending;

        public static void EnsureStarted()
        {
            var summary = Environment.GetEnvironmentVariable("FORMWRIGHT_LATENCY_TRACE");
            if (string.IsNullOrEmpty(summary) || SynchronizationContext.Current is not { } ctx) return;
            if (Interlocked.CompareExchange(ref _context, ctx, null) is not null) return;
            _timer = new Timer(_ => Tick(), null, 0, 500);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Write(summary);
        }

        public static void Mark(string what) { if (_context is not null) Marks.Enqueue($"{Clock.Elapsed.TotalSeconds:F1}s {what}"); }

        private static void Tick()
        {
            var sent = Clock.Elapsed.TotalMilliseconds;
            ThreadPool.UnsafeQueueUserWorkItem(_ => Record(sent, pool: true), null);
            if (Interlocked.CompareExchange(ref _contextPending, 1, 0) != 0) return;
            _context!.Post(_ =>
            {
                Record(sent, pool: false);
                Volatile.Write(ref _contextPending, 0);
            }, null);
        }

        private static void Record(double sent, bool pool)
        {
            var delay = Clock.Elapsed.TotalMilliseconds - sent;
            Buckets.AddOrUpdate((int)(sent / 5000), _ => pool ? (0, delay) : (delay, 0),
                (_, v) => pool ? (v.Context, Math.Max(v.Pool, delay)) : (Math.Max(v.Context, delay), v.Pool));
        }

        private static void Write(string path)
        {
            try
            {
                var sb = new StringBuilder("### WebAPITests 排隊延遲（票 #70／#72 診斷）\n\n| 時間 (s) | xUnit 同步內容最大延遲 (s) | 執行緒池最大延遲 (s) |\n|---|---|---|\n");
                foreach (var (k, v) in Buckets.OrderBy(b => b.Key).Where(b => b.Value.Context > 500 || b.Value.Pool > 500))
                    sb.AppendLine($"| {k * 5}–{k * 5 + 5} | {v.Context / 1000:F1} | {v.Pool / 1000:F1} |");
                sb.AppendLine().AppendLine($"（只列超過 0.5 秒的格；共 {Buckets.Count} 格）").AppendLine();
                foreach (var m in Marks) sb.AppendLine($"- {m}");
                File.AppendAllText(path, sb.ToString());
            }
            catch
            {
                // 診斷寫不出來不影響測試結果
            }
        }
    }
}

