using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>子任務 API（看板設計 §10.7.2）：路由、狀態碼、錯誤鍵與代碼、回應形狀、刪除連帶、記錄頁只列頂層。</summary>
public class SubtasksApiTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly HttpClient _admin;

    public SubtasksApiTests() => _admin = _app.AsUser();

    public void Dispose() => _app.Dispose();

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<long> IdOf(HttpResponseMessage res) => (await JsonOf(res)).GetProperty("id").GetInt64();

    [Fact]
    public async Task Create_201_ShapeOfChildAndParent()
    {
        var sales = _app.AsUser("sales01");
        var parentId = await IdOf(await sales.PostAsJsonAsync($"{Base}/tasks", new { title = "辦行銷活動", assigneeUserId = "sales01" }));

        var res = await sales.PostAsJsonAsync($"{Base}/tasks/{parentId}/subtasks", new { title = "訂場地", assigneeUserId = "sales01" });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var child = await JsonOf(res);
        child.GetProperty("parentTaskId").GetInt64().Should().Be(parentId);
        child.GetProperty("parentTitle").GetString().Should().Be("辦行銷活動");
        child.GetProperty("parentVisible").GetBoolean().Should().BeTrue();
        child.GetProperty("level").GetString().Should().Be("child");
        child.GetProperty("canAddSubtask").GetBoolean().Should().BeFalse();
        child.GetProperty("subtasks").GetArrayLength().Should().Be(0);

        var parent = await JsonOf(await sales.GetAsync($"{Base}/tasks/{parentId}"));
        parent.GetProperty("level").GetString().Should().Be("parent");
        parent.TryGetProperty("parentTaskId", out _).Should().BeFalse("null 不輸出");
        var progress = parent.GetProperty("progress");
        (progress.GetProperty("done").GetInt32(), progress.GetProperty("cancelled").GetInt32(), progress.GetProperty("total").GetInt32())
            .Should().Be((0, 0, 1));
        var sub = parent.GetProperty("subtasks")[0];
        sub.GetProperty("id").GetInt64().Should().Be(child.GetProperty("id").GetInt64());
        sub.GetProperty("level").GetString().Should().Be("child");
        parent.GetProperty("canAddSubtask").GetBoolean().Should().BeTrue();

        var comments = await JsonOf(await sales.GetAsync($"{Base}/tasks/{parentId}/comments"));
        comments.EnumerateArray().Select(c => c.TryGetProperty("systemEvent", out var e) ? e.GetString() : null)
            .Should().Contain("subtaskAdded");
    }

    [Fact]
    public async Task Errors_404_403_400_409()
    {
        var sales = _app.AsUser("sales01");
        var floor = _app.AsUser("floor01");
        var parentId = await IdOf(await sales.PostAsJsonAsync($"{Base}/tasks", new { title = "辦行銷活動", assigneeUserId = "sales01" }));
        var childId = await IdOf(await sales.PostAsJsonAsync($"{Base}/tasks/{parentId}/subtasks", new { title = "訂場地" }));

        var hidden = await floor.PostAsJsonAsync($"{Base}/tasks/{parentId}/subtasks", new { title = "x" });
        hidden.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await JsonOf(hidden)).GetProperty("message").GetString().Should().Be("找不到任務");

        var nested = await sales.PostAsJsonAsync($"{Base}/tasks/{childId}/subtasks", new { title = "x" });
        nested.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(nested)).GetProperty("errors").GetProperty("parent")[0].GetString().Should().Be("子任務不能再有子任務");

        var blank = await sales.PostAsJsonAsync($"{Base}/tasks/{parentId}/subtasks", new { title = " " });
        blank.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(blank)).GetProperty("errors").TryGetProperty("title", out _).Should().BeTrue();

        // st1b 起父任務改完成時有未完成的子任務要帶 cascade
        (await sales.PatchAsync($"{Base}/tasks/{parentId}", Body("""{"status":"done","cascade":"complete"}"""))).StatusCode
            .Should().Be(HttpStatusCode.OK);
        var closed = await sales.PostAsJsonAsync($"{Base}/tasks/{parentId}/subtasks", new { title = "x" });
        closed.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var closedBody = await JsonOf(closed);
        closedBody.GetProperty("code").GetString().Should().Be("parentClosed");
        closedBody.GetProperty("message").GetString().Should().Be("已完成或已取消的任務不能新增子任務");

        // 只被 @ 的人看得到父任務、但不能改：403（看板設計 §10.7.2 第 4 項）
        var otherParent = await IdOf(await sales.PostAsJsonAsync($"{Base}/tasks", new { title = "整理名片" }));
        (await sales.PostAsJsonAsync($"{Base}/tasks/{otherParent}/comments", new { body = "請 @{floor01} 協助" })).StatusCode
            .Should().Be(HttpStatusCode.Created);
        var notEditor = await floor.PostAsJsonAsync($"{Base}/tasks/{otherParent}/subtasks", new { title = "x" });
        notEditor.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await JsonOf(notEditor)).GetProperty("message").GetString().Should().Be("要能修改這個任務才能新增子任務");
    }

    [Fact]
    public async Task Cascade_PatchAndMove_409SubtasksOpenWithCount_ThenComplete()
    {
        var sales = _app.AsUser("sales01");
        var parentId = await IdOf(await sales.PostAsJsonAsync($"{Base}/tasks", new { title = "辦行銷活動", assigneeUserId = "sales01" }));
        var childId = await IdOf(await sales.PostAsJsonAsync($"{Base}/tasks/{parentId}/subtasks", new { title = "訂場地" }));
        await sales.PostAsJsonAsync($"{Base}/tasks/{parentId}/subtasks", new { title = "印海報" });

        var open = await sales.PatchAsync($"{Base}/tasks/{parentId}", Body("""{"status":"done"}"""));
        open.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var openBody = await JsonOf(open);
        openBody.GetProperty("code").GetString().Should().Be("subtasksOpen");
        openBody.GetProperty("params").GetProperty("count").GetInt32().Should().Be(2);
        openBody.GetProperty("message").GetString().Should().Be("還有 2 個未完成的子任務");

        var bad = await sales.PatchAsync($"{Base}/tasks/{parentId}", Body("""{"status":"done","cascade":"later"}"""));
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(bad)).GetProperty("errors").GetProperty("cascade")[0].GetString().Should().Be("連動方式必須是 complete 或 cancel");

        var version = (await JsonOf(await sales.GetAsync($"{Base}/tasks/{parentId}"))).GetProperty("version").GetString();
        var move = new { groupBy = "status", toColumn = "done", expectedVersion = version };
        (await sales.PostAsJsonAsync($"{Base}/tasks/{parentId}/move", move)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var moved = await sales.PostAsJsonAsync($"{Base}/tasks/{parentId}/move",
            new { groupBy = "status", toColumn = "done", expectedVersion = version, cascade = "complete" });
        moved.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(moved)).GetProperty("card").GetProperty("progress").GetProperty("done").GetInt32().Should().Be(2);

        // 父任務已完成，子任務重開把父任務帶回進行中；父任務取消後重開子任務 409
        (await sales.PatchAsync($"{Base}/tasks/{childId}", Body("""{"status":"todo"}"""))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(await sales.GetAsync($"{Base}/tasks/{parentId}"))).GetProperty("status").GetString().Should().Be("inProgress");
        (await sales.PatchAsync($"{Base}/tasks/{parentId}", Body("""{"status":"cancelled"}"""))).StatusCode.Should().Be(HttpStatusCode.OK);
        var reopen = await sales.PatchAsync($"{Base}/tasks/{childId}", Body("""{"status":"todo"}"""));
        reopen.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await JsonOf(reopen)).GetProperty("message").GetString().Should().Be("父任務已取消，請先恢復父任務");
    }

    [Fact]
    public async Task Delete_ParentRemovesChildren_ChildDeleteNeedsCreator()
    {
        var sales = _app.AsUser("sales01");
        var floor = _app.AsUser("floor01");
        var parentId = await IdOf(await sales.PostAsJsonAsync($"{Base}/tasks", new { title = "辦行銷活動", assigneeUserId = "floor01" }));
        var a = await IdOf(await floor.PostAsJsonAsync($"{Base}/tasks/{parentId}/subtasks", new { title = "訂場地" }));
        var b = await IdOf(await sales.PostAsJsonAsync($"{Base}/tasks/{parentId}/subtasks", new { title = "印海報", assigneeUserId = "floor01" }));

        // 父任務負責人（floor01）不能刪 sales01 建的子任務，但能刪自己建的
        (await floor.DeleteAsync($"{Base}/tasks/{b}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await floor.DeleteAsync($"{Base}/tasks/{a}")).StatusCode.Should().Be(HttpStatusCode.OK);
        var parent = await JsonOf(await sales.GetAsync($"{Base}/tasks/{parentId}"));
        parent.GetProperty("progress").GetProperty("total").GetInt32().Should().Be(1);

        (await sales.DeleteAsync($"{Base}/tasks/{parentId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _admin.GetAsync($"{Base}/tasks/{b}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RecordTasks_ListsTopLevelOnly()
    {
        var module = await CreateModule(_admin);
        await Activate(_admin, module);
        var title = await TitleKey(_admin, module);
        var recordId = (await JsonOf(await _admin.PostAsJsonAsync($"{Base}/modules/{module}/records",
            new { values = new Dictionary<string, object> { [title] = "案件乙" } }))).GetProperty("id").GetInt64();
        var parentId = await IdOf(await _admin.PostAsJsonAsync($"{Base}/records/{recordId}/tasks", new { title = "辦說明會" }));

        var created = await _admin.PostAsJsonAsync($"{Base}/tasks/{parentId}/subtasks", new { title = "訂場地" });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        (await JsonOf(created)).GetProperty("record").GetProperty("recordId").GetInt64().Should().Be(recordId);

        var list = await JsonOf(await _admin.GetAsync($"{Base}/records/{recordId}/tasks"));
        var items = list.GetProperty("items");
        items.GetArrayLength().Should().Be(1);
        items[0].GetProperty("id").GetInt64().Should().Be(parentId);
        items[0].GetProperty("level").GetString().Should().Be("parent");
        items[0].GetProperty("progress").GetProperty("total").GetInt32().Should().Be(1);
    }
}
