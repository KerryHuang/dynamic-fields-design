using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>看板存檔、欄查詢與移動的 HTTP 往返（看板設計 §12）。</summary>
public class TaskBoardApiTests : IDisposable
{
    private readonly TestApp _app;

    public TaskBoardApiTests()
    {
        _app = new TestApp(fileDatabase: true);
        // EF 每條 SQL 都寫 console，console 的鎖會把請求排成一列，蓋掉真正的併發（同 ConcurrentWriteApiTests）
        _app.Settings["Logging:LogLevel:Default"] = "Warning";
    }

    public void Dispose() => _app.Dispose();

    private const string StatusBoard = """{"version":1,"groupBy":"status","scope":"assigned"}""";

    private static StringContent Json(string json) => new(json, System.Text.Encoding.UTF8, "application/json");

    private static async Task<JsonElement> Ok(HttpResponseMessage res, HttpStatusCode expected = HttpStatusCode.OK)
    {
        res.StatusCode.Should().Be(expected, await res.Content.ReadAsStringAsync());
        return await JsonOf(res);
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("""[{"kind":"user","id":"sales01"},null]""")]
    public async Task TaskViewShares_NullElement_400_NothingChanged(string body)
    {
        var manager = _app.AsUser("manager01");
        var created = await Ok(await manager.PostAsync($"{Base}/task-views", Json($$"""{"kind":"board","name":"業務部任務","config":{{StatusBoard}}}""")), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetInt64();
        var shared = await Ok(await manager.PutAsync($"{Base}/task-views/{id}/shares", Json("""[{"kind":"user","id":"sales01"}]""")));
        var rowVersion = shared.GetProperty("rowVersion").GetString();

        (await manager.PutAsync($"{Base}/task-views/{id}/shares", Json(body))).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var after = await Ok(await manager.GetAsync($"{Base}/task-views/{id}"));
        after.GetProperty("rowVersion").GetString().Should().Be(rowVersion);
        after.GetProperty("shares").GetArrayLength().Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task TaskViews_Crud_Http()
    {
        var manager = _app.AsUser("manager01");
        var sales = _app.AsUser("sales01");

        var created = await Ok(await manager.PostAsync($"{Base}/task-views", Json($$"""{"kind":"board","name":"業務部任務","config":{{StatusBoard}}}""")), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetInt64();
        created.GetProperty("config").GetProperty("groupBy").GetString().Should().Be("status");
        var rowVersion = created.GetProperty("rowVersion").GetString();

        (await manager.PostAsync($"{Base}/task-views", Json("""{"kind":"board","name":"x","config":{"version":1,"groupBy":"status","scope":"assigned","oops":1}}""")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await sales.GetAsync($"{Base}/task-views/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var shared = await Ok(await manager.PutAsync($"{Base}/task-views/{id}/shares", Json("""[{"kind":"user","id":"sales01"}]""")));
        shared.GetProperty("shares").GetArrayLength().Should().Be(1);
        var list = await Ok(await sales.GetAsync($"{Base}/task-views?kind=board"));
        list.GetProperty("shared").EnumerateArray().Select(v => v.GetProperty("id").GetInt64()).Should().Equal(id);
        (await sales.PutAsync($"{Base}/task-views/{id}", Json($$"""{"name":"改","config":{{StatusBoard}},"rowVersion":"{{rowVersion}}"}""")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await manager.PutAsync($"{Base}/task-views/{id}", Json($$"""{"name":"改","config":{{StatusBoard}},"rowVersion":"{{rowVersion}}"}""")))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);   // 分享換過 rowVersion
        var copy = await Ok(await sales.PostAsync($"{Base}/task-views/{id}/copy", null), HttpStatusCode.Created);
        copy.GetProperty("ownerUserId").GetString().Should().Be("sales01");
        var targets = await Ok(await sales.GetAsync($"{Base}/task-views/share-targets"));
        targets.GetProperty("users").GetArrayLength().Should().BeGreaterThan(0);
        (await manager.DeleteAsync($"{Base}/task-views/{id}")).IsSuccessStatusCode.Should().BeTrue();
        (await sales.GetAsync($"{Base}/task-views/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task Columns_RoundTrip_SavedAndInline()
    {
        var sales = _app.AsUser("sales01");
        _app.Clock.Fixed = new DateTime(2026, 11, 4, 9, 0, 0);
        await Ok(await sales.PostAsJsonAsync($"{Base}/tasks", new { title = "今天", assigneeUserId = "sales01", dueDate = "2026-11-04" }), HttpStatusCode.Created);
        await Ok(await sales.PostAsJsonAsync($"{Base}/tasks", new { title = "之後", assigneeUserId = "sales01", dueDate = "2026-11-20" }), HttpStatusCode.Created);

        var inline = await Ok(await sales.PostAsync($"{Base}/task-board/columns",
            Json("""{"config":{"version":1,"groupBy":"dueBucket","scope":"assigned"}}""")));
        inline.GetProperty("today").GetString().Should().Be("2026-11-04");
        inline.GetProperty("weekEnd").GetString().Should().Be("2026-11-08");
        inline.GetProperty("laterDefault").GetString().Should().Be("2026-11-15");
        var columns = inline.GetProperty("columns").EnumerateArray().ToList();
        columns.Select(c => c.GetProperty("key").GetString()).Should().Equal("overdue", "today", "thisWeek", "later", "none");
        var card = columns[1].GetProperty("cards")[0];
        card.GetProperty("title").GetString().Should().Be("今天");
        card.GetProperty("status").GetString().Should().Be("todo");
        card.GetProperty("dueToday").GetBoolean().Should().BeTrue();
        card.GetProperty("version").GetString().Should().NotBeNullOrEmpty();
        columns[0].GetProperty("droppable").GetBoolean().Should().BeFalse();

        var view = await Ok(await sales.PostAsync($"{Base}/task-views", Json($$"""{"name":"我的","config":{{StatusBoard}}}""")), HttpStatusCode.Created);
        var saved = await Ok(await sales.PostAsync($"{Base}/task-board/columns", Json($$"""{"viewId":{{view.GetProperty("id").GetInt64()}}}""")));
        saved.GetProperty("columns")[0].GetProperty("key").GetString().Should().Be("todo");
        saved.GetProperty("columns")[0].GetProperty("total").GetInt32().Should().Be(2);

        var page = await Ok(await sales.PostAsync($"{Base}/task-board/columns/user:sales01/page",
            Json("""{"config":{"version":1,"groupBy":"assignee","scope":"assigned"},"offset":0}""")));
        page.GetProperty("total").GetInt32().Should().Be(2);
        (await sales.PostAsync($"{Base}/task-board/columns", Json("""{"config":{"version":1}}""")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _app.AsUser("sales02").PostAsync($"{Base}/task-board/columns", Json($$"""{"viewId":{{view.GetProperty("id").GetInt64()}}}""")))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task MoveAndMove_Concurrent_OneWins()
    {
        var sales = _app.AsUser("sales01");
        var other = _app.AsUser("sales01");
        var task = await Ok(await sales.PostAsJsonAsync($"{Base}/tasks", new { title = "回覆樣品問題", assigneeUserId = "sales01" }), HttpStatusCode.Created);
        var id = task.GetProperty("id").GetInt64();
        var version = task.GetProperty("version").GetString();

        var moves = await Task.WhenAll(
            sales.PostAsJsonAsync($"{Base}/tasks/{id}/move", new { groupBy = "status", toColumn = "done", expectedVersion = version }),
            other.PostAsJsonAsync($"{Base}/tasks/{id}/move", new { groupBy = "status", toColumn = "inProgress", expectedVersion = version }));

        moves.Select(m => m.StatusCode).Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict]);
        var winner = await JsonOf(moves.Single(m => m.StatusCode == HttpStatusCode.OK));
        winner.GetProperty("visible").GetBoolean().Should().BeTrue();
        var loser = await JsonOf(moves.Single(m => m.StatusCode == HttpStatusCode.Conflict));
        loser.GetProperty("code").GetString().Should().Be("stale");
        var after = await Ok(await sales.GetAsync($"{Base}/tasks/{id}"));
        after.GetProperty("status").GetString().Should().Be(winner.GetProperty("card").GetProperty("status").GetString());
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task GanttView_Http_KindAndMismatch404()
    {
        var sales = _app.AsUser("sales01");

        var gantt = await Ok(await sales.PostAsync($"{Base}/task-views", Json("""{"kind":"gantt","name":"展覽"}""")), HttpStatusCode.Created);
        gantt.GetProperty("kind").GetString().Should().Be("gantt");
        gantt.GetProperty("config").GetProperty("scale").GetString().Should().Be("week");
        var id = gantt.GetProperty("id").GetInt64();
        var bad = await sales.PostAsync($"{Base}/task-views", Json("""{"kind":"gantt","name":"x","config":{"version":1,"groupBy":"parent","scope":"assigned","scale":"year"}}"""));
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var en = new HttpRequestMessage(HttpMethod.Post, $"{Base}/task-views")
        { Content = Json("""{"kind":"gantt","name":" "}""") };
        en.Headers.Add("X-Locale", "en-US");
        (await (await sales.SendAsync(en)).Content.ReadAsStringAsync()).Should().Contain("Gantt chart name is required");

        (await Ok(await sales.GetAsync($"{Base}/task-views?kind=gantt"))).GetProperty("mine").GetArrayLength().Should().Be(1);
        (await Ok(await sales.GetAsync($"{Base}/task-views"))).GetProperty("mine").GetArrayLength().Should().Be(0);
        (await sales.GetAsync($"{Base}/task-views?kind=list")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await sales.PostAsync($"{Base}/task-board/columns", Json($$"""{"viewId":{{id}}}"""))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await sales.PostAsync($"{Base}/task-board/columns/todo/page", Json($$"""{"viewId":{{id}},"offset":0}"""))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task Schedule_Http_RoundTrip_BadDate400()
    {
        var sales = _app.AsUser("sales01");
        var task = await Ok(await sales.PostAsJsonAsync($"{Base}/tasks", new { title = "佈置展場", assigneeUserId = "sales01" }), HttpStatusCode.Created);
        var id = task.GetProperty("id").GetInt64();
        var version = task.GetProperty("version").GetString();

        (await sales.PostAsync($"{Base}/tasks/{id}/schedule", Json($$"""{"startDate":"2026/11/21","dueDate":null,"expectedVersion":"{{version}}"}""")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var ok = await Ok(await sales.PostAsJsonAsync($"{Base}/tasks/{id}/schedule",
            new { startDate = "2026-11-21", dueDate = "2026-11-28", expectedVersion = version }));
        var card = ok.GetProperty("card");
        card.GetProperty("startDate").GetString().Should().Be("2026-11-21");
        card.GetProperty("dueDate").GetString().Should().Be("2026-11-28");
        ok.TryGetProperty("visible", out _).Should().BeFalse();
        var contradiction = await sales.PostAsJsonAsync($"{Base}/tasks/{id}/schedule",
            new { startDate = "2026-11-29", dueDate = "2026-11-28", expectedVersion = card.GetProperty("version").GetString() });
        contradiction.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await contradiction.Content.ReadAsStringAsync()).Should().Contain("startDate");
    }

    /// <summary>
    /// 兩個請求帶同一個 expectedVersion、依序送（可確定的交錯：第二個一定讀到第一個寫過的版本）→ 一個 200、一個 409 stale，
    /// 系統記錄只有贏的那筆。讀完後才被別人推進版本、靠條件式認領擋下的情況見 ApplicationTests 的 TaskScheduleTests.G29。TestApp 是 SQLite。
    /// </summary>
    [Fact]
    [Trait("Category", "Postgres")]
    public async Task Schedule_SameVersionTwice_SecondIsStale_EntriesOnce()
    {
        var sales = _app.AsUser("sales01");
        var other = _app.AsUser("sales01");
        var task = await Ok(await sales.PostAsJsonAsync($"{Base}/tasks", new { title = "回覆樣品問題", assigneeUserId = "sales01" }), HttpStatusCode.Created);
        var id = task.GetProperty("id").GetInt64();
        var version = task.GetProperty("version").GetString();

        var first = await sales.PostAsJsonAsync($"{Base}/tasks/{id}/schedule", new { startDate = "2026-11-21", dueDate = "2026-11-28", expectedVersion = version });
        var second = await other.PostAsJsonAsync($"{Base}/tasks/{id}/schedule", new { startDate = "2026-12-01", dueDate = "2026-12-05", expectedVersion = version });

        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await JsonOf(second)).GetProperty("code").GetString().Should().Be("stale");
        var after = await Ok(await sales.GetAsync($"{Base}/tasks/{id}"));
        after.GetProperty("startDate").GetString().Should().Be("2026-11-21");
        after.GetProperty("dueDate").GetString().Should().Be("2026-11-28");
        var events = (await Ok(await sales.GetAsync($"{Base}/tasks/{id}/comments"))).EnumerateArray()
            .Where(c => c.TryGetProperty("systemEvent", out var e) && e.ValueKind == JsonValueKind.String)
            .Select(c => c.GetProperty("systemEvent").GetString()).ToList();
        events.Count(e => e == "startChanged").Should().Be(1);
        events.Count(e => e == "dueChanged").Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task Gantt_Http_RoundTrip_WindowErrors_BoardId404()
    {
        _app.Clock.Fixed = new DateTime(2026, 11, 18, 9, 0, 0); // 星期三；A7 出廠為星期一，本週起始日 11/16
        var sales = _app.AsUser("sales01");
        var task = await Ok(await sales.PostAsJsonAsync($"{Base}/tasks",
            new { title = "佈置展場", assigneeUserId = "sales01", startDate = "2026-11-21", dueDate = "2026-11-28" }), HttpStatusCode.Created);
        await Ok(await sales.PostAsJsonAsync($"{Base}/tasks", new { title = "還沒排", assigneeUserId = "sales01" }), HttpStatusCode.Created);
        const string flat = """{"version":1,"groupBy":"none","scope":"assigned","scale":"week"}""";

        var g = await Ok(await sales.PostAsync($"{Base}/tasks/gantt", Json("""{"config":""" + flat + ""","window":{"from":"2026-11-16","to":"2026-11-29"}}""")));
        g.GetProperty("window").GetProperty("from").GetString().Should().Be("2026-11-16");
        g.GetProperty("weekStart").GetString().Should().Be("monday");
        g.GetProperty("window").GetProperty("to").GetString().Should().Be("2026-11-29");
        g.GetProperty("today").GetString().Should().Be("2026-11-18");
        var row = g.GetProperty("rows").EnumerateArray().Single();
        row.GetProperty("id").GetInt64().Should().Be(task.GetProperty("id").GetInt64());
        row.GetProperty("startDate").GetString().Should().Be("2026-11-21");
        g.GetProperty("total").GetInt32().Should().Be(1);
        g.GetProperty("truncated").GetBoolean().Should().BeFalse();
        g.GetProperty("parents").GetArrayLength().Should().Be(0);
        g.GetProperty("spans").GetArrayLength().Should().Be(0);
        g.GetProperty("unscheduled").EnumerateArray().Single().GetProperty("title").GetString().Should().Be("還沒排");
        g.GetProperty("unscheduledTotal").GetInt32().Should().Be(1);
        g.GetProperty("config").GetProperty("scale").GetString().Should().Be("week");
        var defaults = await Ok(await sales.PostAsync($"{Base}/tasks/gantt", Json("{}")));
        defaults.GetProperty("config").GetProperty("groupBy").GetString().Should().Be("parent");
        defaults.GetProperty("today").GetString().Should().Be("2026-11-18");
        defaults.GetProperty("window").GetProperty("from").GetString().Should().Be("2026-11-16");
        defaults.GetProperty("window").GetProperty("to").GetString().Should().Be("2027-02-14", "預設週刻度 13 週");

        (await sales.PostAsync($"{Base}/tasks/gantt", Json("""{"window":{"from":"2026/11/16","to":"2026-11-29"}}""")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var en = new HttpRequestMessage(HttpMethod.Post, $"{Base}/tasks/gantt") { Content = Json("""{"window":{"from":"2026-01-01","to":"2027-01-02"}}""") };
        en.Headers.Add("X-Locale", "en-US");
        var tooLong = await sales.SendAsync(en);
        tooLong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await tooLong.Content.ReadAsStringAsync()).Should().Contain("window").And.Contain("The time window can be at most 366 days");

        var board = await Ok(await sales.PostAsync($"{Base}/task-views", Json($$"""{"kind":"board","name":"看板","config":{{StatusBoard}}}""")), HttpStatusCode.Created);
        var notFound = await sales.PostAsync($"{Base}/tasks/gantt", Json($$"""{"viewId":{{board.GetProperty("id").GetInt64()}}}"""));
        notFound.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await notFound.Content.ReadAsStringAsync()).Should().Contain("這個甘特圖已刪除或不再分享給你");
    }
}
