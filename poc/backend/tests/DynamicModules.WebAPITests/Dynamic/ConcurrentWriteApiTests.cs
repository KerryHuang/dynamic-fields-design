using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit.Abstractions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 真正併發的寫入：檔案型 SQLite（與正式站相同的鎖行為），多個請求同時新增與修改不同記錄。
/// 寫入彼此序列化，但不得有任何請求因為「database is locked」回 500；等不到鎖時只能回可重試的 503 或 409。
/// </summary>
public class ConcurrentWriteApiTests : IDisposable
{
    private readonly TestApp _app;

    public ConcurrentWriteApiTests(ITestOutputHelper output)
    {
        this.output = output;
        _app = new TestApp(fileDatabase: true);
        // EF 每條 SQL 都寫 console，console 的鎖會把請求排成一列，蓋掉真正的併發
        _app.Settings["Logging:LogLevel:Default"] = "Warning";
    }

    private readonly ITestOutputHelper output;

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task ConcurrentCreatesAndUpdates_NeverReturn500()
    {
        var admin = _app.AsUser();
        var module = await CreateModule(admin);
        var amount = await AddField(admin, module, "decimal", new { scale = 2, isFilterable = true });
        await Activate(admin, module);
        _app.GrantBusinessGroups(module);
        var title = await TitleKey(admin, module);

        const int workers = 16;
        var clients = Enumerable.Range(0, workers).Select(_ => _app.AsUser("sales01")).ToArray();
        var rows = new List<(long Id, string RowVersion)>();
        for (var i = 0; i < workers; i++)
        {
            var created = await JsonOf(await clients[i].PostAsJsonAsync($"{Base}/modules/{module}/records",
                new { values = new Dictionary<string, object> { [title] = $"t{i}", [amount.Key] = 1 } }));
            rows.Add((created.GetProperty("id").GetInt64(), created.GetProperty("rowVersion").GetString()!));
        }

        var statuses = new List<HttpStatusCode>();
        for (var round = 0; round < 4; round++)
        {
            var tasks = new List<Task<(HttpStatusCode Status, long Id, string? RowVersion)>>();
            for (var i = 0; i < workers; i++)
            {
                var (id, rowVersion) = rows[i];
                var n = round * workers + i;
                tasks.Add(i % 2 == 0
                    ? Update(clients[i], id, rowVersion, title, amount.Key, n)
                    : Create(clients[i], module, title, amount.Key, n));
            }
            var results = await Task.WhenAll(tasks);
            statuses.AddRange(results.Select(r => r.Status));
            for (var i = 0; i < workers; i += 2)
                if (results[i].RowVersion is { } fresh) rows[i] = (rows[i].Id, fresh);
        }

        var summary = string.Join(", ", statuses.GroupBy(s => (int)s).OrderBy(g => g.Key).Select(g => $"{g.Key}×{g.Count()}"));
        output.WriteLine(summary);
        statuses.Where(s => s is not (HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.Conflict or HttpStatusCode.ServiceUnavailable))
            .Should().BeEmpty("併發寫入不得回 500；實際分佈：" + summary);
        statuses.Count(s => s is HttpStatusCode.OK or HttpStatusCode.Created).Should().BeGreaterThan(statuses.Count / 2);
    }

    /// <summary>
    /// 鎖被別的行程（這裡用一條外部連線代替）占住、等不到時：回可重試的 503 並帶 Retry-After，不是 500；
    /// 鎖放開後同一個請求可以成功。
    /// </summary>
    [Fact]
    public async Task WhenTheWriteLockCannotBeObtained_Returns503WithRetryAfter()
    {
        using var app = new TestApp(fileDatabase: true);
        app.Settings["Logging:LogLevel:Default"] = "Warning";
        // 等鎖最多 1 秒，測試才不必等預設的 30 秒
        app.Settings["ConnectionStrings:Dynamic"] = $"Data Source={app.DatabaseFile};Default Timeout=1";
        var admin = app.AsUser();
        var module = await CreateModule(admin);
        await Activate(admin, module);
        app.GrantBusinessGroups(module);
        var title = await TitleKey(admin, module);
        var sales = app.AsUser("sales01");
        var body = new { values = new Dictionary<string, object> { [title] = "t" } };

        // 一條外部連線（等同另一個行程）占住寫入鎖
        var other = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={app.DatabaseFile}");
        other.Open();
        using (var begin = other.CreateCommand()) { begin.CommandText = "BEGIN IMMEDIATE"; begin.ExecuteNonQuery(); }
        HttpResponseMessage blocked;
        try { blocked = await sales.PostAsJsonAsync($"{Base}/modules/{module}/records", body); }
        finally
        {
            using var rollback = other.CreateCommand();
            rollback.CommandText = "ROLLBACK";
            rollback.ExecuteNonQuery();
            other.Dispose();
        }

        blocked.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        blocked.Headers.RetryAfter.Should().NotBeNull();
        (await sales.PostAsJsonAsync($"{Base}/modules/{module}/records", body)).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private static async Task<(HttpStatusCode, long, string?)> Update(HttpClient c, long id, string rowVersion, string title, string amount, int n)
    {
        var res = await c.PutAsJsonAsync($"{Base}/records/{id}",
            new { values = new Dictionary<string, object> { [title] = "u" + n, [amount] = n }, rowVersion });
        return res.IsSuccessStatusCode
            ? (res.StatusCode, id, (await JsonOf(res)).GetProperty("rowVersion").GetString())
            : (res.StatusCode, id, null);
    }

    private static async Task<(HttpStatusCode, long, string?)> Create(HttpClient c, string module, string title, string amount, int n)
    {
        var res = await c.PostAsJsonAsync($"{Base}/modules/{module}/records",
            new { values = new Dictionary<string, object> { [title] = "c" + n, [amount] = n } });
        return (res.StatusCode, 0, null);
    }
}
