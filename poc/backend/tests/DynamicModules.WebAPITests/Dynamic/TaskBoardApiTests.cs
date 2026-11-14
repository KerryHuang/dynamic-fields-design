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
}
