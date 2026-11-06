using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetPagedRecords;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;
using DynamicModules.Application.Dynamic.ReportService.Queries.PreviewReport;
using DynamicModules.Domain.Reports;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace LoadTest;

/// <summary>負載操作集。每個操作拿一個新 scope（等同一次 HTTP 請求），成功回 null，失敗回訊息。</summary>
public sealed class Workload(IServiceProvider sp, SeedInfo seed)
{
    private readonly SchemaInfo _s = seed.Schema;
    private readonly ConcurrentBag<long> _created = [];

    public sealed record Op(string Name, int Weight, bool Write, Func<IMediator, Random, Task<string?>> Run);

    private static string Filter(params object[] conditions) => JsonSerializer.Serialize(new { all = conditions });

    private static string D(DateOnly d) => d.ToString("yyyy-MM-dd");

    private DateOnly RandomDay(Random r) => seed.FirstDate.AddDays(r.Next(seed.LastDate.DayNumber - seed.FirstDate.DayNumber + 1));

    /// <summary>最近 36 個月內的一個區間（使用者最常看的是近期資料）；資料不足 36 個月時縮在資料範圍內。</summary>
    private (DateOnly From, DateOnly To) Window(Random r, int days)
    {
        var span = seed.LastDate.DayNumber - seed.FirstDate.DayNumber;
        var to = seed.LastDate.AddDays(-r.Next(Math.Max(1, Math.Min(1080, span - days))));
        return (to.AddDays(-days), to);
    }

    private long RandomAccount(Random r) => seed.AccountFirstId + (long)(seed.Accounts * Math.Pow(r.NextDouble(), 3));

    private static async Task<string?> Paged(IMediator m, string? filter, string? sortKey, string order, int page, string? q = null)
    {
        var res = await m.Send(new GetPagedRecordsRequest("ledger", page, 20, filter,
            sortKey is null ? [] : [sortKey], sortKey is null ? [] : [order], q));
        return res.IsSuccess ? null : res.Message;
    }

    private static string? ReportState(DynamicModules.Application.Common.ServiceResult res)
    {
        if (!res.IsSuccess) return res.Message;
        // 報表的上限、權限等狀況是 HTTP 200 加 State，不是錯誤；State 不是 ok 或 empty 就當作沒有產出結果
        var state = ((DynamicModules.Application.Dynamic.ReportService.Data.ReportResult)res.Data!).State;
        return state is "ok" or "empty" ? null : "state=" + state;
    }

    private async Task<string?> Report(IMediator m, DateOnly from, DateOnly to)
    {
        var date = new ReportFieldRef("main", _s.LedgerDateId, null);
        var def = new ReportDefinition(ReportDefinition.CurrentVersion, "ledger", null, [], [new ReportGroup(date, "month")],
            [new("n", "countMain", null, null), new("d", "sum", new ReportFieldRef("main", _s.LedgerDebitId, null), null)],
            [new ReportFilter(date, "between", JsonSerializer.SerializeToElement(new[] { D(from), D(to) }))],
            null, null, [], "table");
        return ReportState(await m.Send(new PreviewReportRequest(def, "zh-TW")));
    }

    /// <summary>主檔報表：客戶依區域計數，來源模組筆數在報表上限之內，量這條路徑本身的成本。</summary>
    private async Task<string?> ReportCustomers(IMediator m)
    {
        var region = new ReportFieldRef("main", long.Parse(_s.CustomerRegion[2..]), null);
        var def = new ReportDefinition(ReportDefinition.CurrentVersion, "customer", null, [], [new ReportGroup(region, null)],
            [new("n", "countMain", null, null)], [], null, null, [], "table");
        return ReportState(await m.Send(new PreviewReportRequest(def, "zh-TW")));
    }

    private Dictionary<string, JsonElement?> NewEntry(Random r) => new()
    {
        [_s.LedgerTitle] = Seeder.Json($"壓測傳票 {r.Next():X}"),
        [_s.LedgerDate] = Seeder.Json(D(seed.LastDate)),
        [_s.LedgerAccount] = JsonSerializer.SerializeToElement(RandomAccount(r)),
        [_s.LedgerDebit] = JsonSerializer.SerializeToElement(r.Next(1, 100000) / 100m),
        [_s.LedgerCredit] = JsonSerializer.SerializeToElement(0m),
        [_s.LedgerStatus] = Seeder.Json(_s.Statuses[0]),
    };

    public List<Op> All() =>
    [
        new("list_default", 30, false, (m, r) => Paged(m, null, null, "asc", 1)),
        new("list_date_month", 20, false, (m, r) =>
        {
            var (f, t) = Window(r, 30);
            return Paged(m, Filter(new { field = _s.LedgerDateId, op = "between", value = new[] { D(f), D(t) } }), _s.LedgerDate, "desc", 1);
        }),
        new("list_account_quarter", 15, false, (m, r) =>
        {
            var (f, t) = Window(r, 90);
            return Paged(m, Filter(new { field = _s.LedgerAccountId, op = "eq", value = RandomAccount(r) },
                new { field = _s.LedgerDateId, op = "between", value = new[] { D(f), D(t) } }), _s.LedgerDate, "desc", 1);
        }),
        new("list_status_draft", 5, false, (m, r) =>
            Paged(m, Filter(new { field = _s.LedgerStatusId, op = "eq", value = _s.Statuses[1] }), _s.LedgerDate, "desc", 1)),
        new("list_sort_amount_all", 2, false, (m, r) => Paged(m, null, _s.LedgerDebit, "desc", 1)),
        new("list_deep_page", 3, false, (m, r) => Paged(m, null, null, "asc", 5000)),
        new("list_keyword", 3, false, (m, r) => Paged(m, null, null, "asc", 1, $"傳票 {r.Next(0, 9)}00{r.Next(10, 99)}")),
        new("report_month", 5, false, (m, r) => { var (f, t) = Window(r, 30); return Report(m, f, t); }),
        new("report_quarter", 2, false, (m, r) => { var (f, t) = Window(r, 90); return Report(m, f, t); }),
        new("report_customers", 3, false, (m, r) => ReportCustomers(m)),
        new("create_entry", 14, true, async (m, r) =>
        {
            var res = await m.Send(new CreateRecordRequest("ledger", NewEntry(r)));
            if (!res.IsCreated) return res.Message;
            _created.Add(((DynamicRecordResponse)res.Data!).Id);
            return null;
        }),
        new("update_entry", 6, true, async (m, r) =>
        {
            var id = _created.IsEmpty ? seed.LedgerFirstId + r.NextInt64(seed.Ledger) : _created.ElementAt(r.Next(_created.Count));
            var got = await m.Send(new GetRecordRequest(id));
            if (!got.IsSuccess) return got.Message;
            var rec = (DynamicRecordResponse)got.Data!;
            var values = new Dictionary<string, JsonElement?>(rec.Values)
            {
                [_s.LedgerStatus] = Seeder.Json(_s.Statuses[r.Next(_s.Statuses.Length)]),
            };
            var res = await m.Send(new UpdateRecordRequest(id, values, rec.RowVersion));
            return res.IsSuccess ? null : res.Message;
        }),
    ];

    public static List<Op> Only(List<Op> all, params string[] names) => all.Where(o => names.Contains(o.Name)).ToList();

    private async Task Execute(Op op, Random rnd, Recorder rec)
    {
        using var scope = sp.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();
        var sw = Stopwatch.StartNew();
        try
        {
            var err = await op.Run(m, rnd);
            if (err is null) rec.Ok(op.Name, sw.Elapsed.TotalMilliseconds);
            else rec.Error(op.Name, sw.Elapsed.TotalMilliseconds, err);
        }
        catch (Exception e)
        {
            var inner = e;
            while (inner.InnerException is not null) inner = inner.InnerException;
            rec.Error(op.Name, sw.Elapsed.TotalMilliseconds, inner.Message);
        }
    }

    /// <summary>單人：每個操作循序跑 n 次（第一次當暖機，不計）。</summary>
    public async Task<PhaseResult> Solo(List<Op> ops, int n)
    {
        var rec = new Recorder();
        var rnd = new Random(1);
        var sw = Stopwatch.StartNew();
        var count = 0;
        foreach (var op in ops)
        {
            await Execute(op, rnd, new Recorder());
            for (var i = 0; i < n; i++) { await Execute(op, rnd, rec); count++; }
        }
        return new PhaseResult("solo", 1, sw.Elapsed.TotalSeconds, count / sw.Elapsed.TotalSeconds, rec.Summarize());
    }

    /// <summary>多人：每人依權重抽操作；thinkMs 為 (min, max) 的思考時間，(0,0) 表示不停手。</summary>
    public async Task<PhaseResult> Crowd(string name, List<Op> ops, int users, TimeSpan duration, (int Min, int Max) thinkMs)
    {
        var rec = new Recorder();
        var total = ops.Sum(o => o.Weight);
        var done = 0;
        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, users).Select(u => Task.Run(async () =>
        {
            var rnd = new Random(1000 + u);
            await Task.Delay(rnd.Next(0, Math.Max(1, thinkMs.Max)));
            while (sw.Elapsed < duration)
            {
                var pick = rnd.Next(total);
                var op = ops.First(o => (pick -= o.Weight) < 0);
                await Execute(op, rnd, rec);
                Interlocked.Increment(ref done);
                if (thinkMs.Max > 0) await Task.Delay(rnd.Next(thinkMs.Min, thinkMs.Max + 1));
            }
        })).ToArray();
        await Task.WhenAll(tasks);
        return new PhaseResult(name, users, sw.Elapsed.TotalSeconds, done / sw.Elapsed.TotalSeconds, rec.Summarize());
    }
}
