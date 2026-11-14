using System.Net;
using System.Net.Http.Json;
using System.Text;
using DynamicModules.Domain.Data;
using DynamicModules.Persistence.Seed;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

public class TasksApiTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly HttpClient _admin;

    public TasksApiTests() => _admin = _app.AsUser();

    public void Dispose() => _app.Dispose();

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task TasksApi_PatchNullOverHttp_ClearsDueDate()
    {
        var created = await _admin.PostAsJsonAsync($"{Base}/tasks",
            new { title = "整理報價", assigneeUserId = "sales01", dueDate = "2026-12-01" });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var task = await JsonOf(created);
        task.GetProperty("status").GetString().Should().Be("todo");
        task.GetProperty("dueDate").GetString().Should().Be("2026-12-01");
        var id = task.GetProperty("id").GetInt64();

        var res = await _admin.PatchAsync($"{Base}/tasks/{id}", Body("""{"dueDate":null,"status":"inProgress"}"""));

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var after = await JsonOf(await _admin.GetAsync($"{Base}/tasks/{id}"));
        after.TryGetProperty("dueDate", out _).Should().BeFalse();   // null 不輸出
        after.GetProperty("status").GetString().Should().Be("inProgress");
        after.GetProperty("assigneeUserId").GetString().Should().Be("sales01");
    }

    [Fact]
    public async Task TasksApi_InvisibleTask_404_SameMessage()
    {
        var sales = _app.AsUser("sales01");
        var floor = _app.AsUser("floor01");
        var task = await JsonOf(await sales.PostAsJsonAsync($"{Base}/tasks", new { title = "私人待辦" }));
        var id = task.GetProperty("id").GetInt64();

        var hidden = await floor.GetAsync($"{Base}/tasks/{id}");
        var missing = await floor.GetAsync($"{Base}/tasks/{id + 1000}");
        var patch = await floor.PatchAsync($"{Base}/tasks/{id}", Body("""{"title":"x"}"""));

        hidden.StatusCode.Should().Be(HttpStatusCode.NotFound);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        patch.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await JsonOf(hidden)).GetProperty("message").GetString().Should().Be("找不到任務");
        (await JsonOf(missing)).GetProperty("message").GetString().Should().Be("找不到任務");
    }

    [Fact]
    public async Task TasksApi_TodayFilter_ReturnsOnlyTasksDueToday()
    {
        var module = await CreateModule(_admin);
        await Activate(_admin, module);
        var title = await TitleKey(_admin, module);
        var record = await JsonOf(await _admin.PostAsJsonAsync($"{Base}/modules/{module}/records",
            new { values = new Dictionary<string, object> { [title] = "案件乙" } }));
        var recordId = record.GetProperty("id").GetInt64();

        _app.Clock.Fixed = new DateTime(2026, 11, 4, 9, 0, 0);
        foreach (var (name, due) in new[] { ("昨天", "2026-11-03"), ("今天", "2026-11-04"), ("明天", "2026-11-05") })
        {
            var created = await _admin.PostAsJsonAsync($"{Base}/records/{recordId}/tasks", new { title = name, assigneeUserId = "admin", dueDate = due });
            created.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        var res = await _admin.GetAsync($"{Base}/tasks?tab=assigned&filter=today&currentPage=1&pagesSize=20");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await JsonOf(res);
        body.GetProperty("totalCount").GetInt32().Should().Be(1);
        body.GetProperty("items")[0].GetProperty("title").GetString().Should().Be("今天");
    }

    [Fact]
    public async Task TasksApi_RecordTasks_MyTasksAndSummary()
    {
        var module = await CreateModule(_admin);
        await Activate(_admin, module);
        var title = await TitleKey(_admin, module);
        var record = await JsonOf(await _admin.PostAsJsonAsync($"{Base}/modules/{module}/records",
            new { values = new Dictionary<string, object> { [title] = "案件甲" } }));
        var recordId = record.GetProperty("id").GetInt64();

        _app.Clock.Fixed = new DateTime(2026, 11, 4, 9, 0, 0);
        var created = await _admin.PostAsJsonAsync($"{Base}/records/{recordId}/tasks",
            new { title = "聯絡窗口", assigneeUserId = "admin", dueDate = "2026-11-04" });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var taskId = (await JsonOf(created)).GetProperty("id").GetInt64();
        var list = await JsonOf(await _admin.GetAsync($"{Base}/records/{recordId}/tasks"));
        list.GetProperty("canAdd").GetBoolean().Should().BeTrue();
        list.GetProperty("manualRules").GetArrayLength().Should().Be(0);
        var item = list.GetProperty("items")[0];
        item.GetProperty("title").GetString().Should().Be("聯絡窗口");
        item.GetProperty("record").GetProperty("moduleKey").GetString().Should().Be(module);

        var mine = await JsonOf(await _admin.GetAsync($"{Base}/tasks?tab=assigned&filter=all&currentPage=1&pagesSize=20"));
        mine.GetProperty("totalCount").GetInt32().Should().Be(1);
        var summary = await JsonOf(await _admin.GetAsync($"{Base}/tasks/summary"));
        summary.GetProperty("dueToday").GetInt32().Should().Be(1);
        summary.GetProperty("overdue").GetInt32().Should().Be(0);
        (await _admin.GetAsync($"{Base}/tasks?tab=bogus")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var delete = await _admin.DeleteAsync($"{Base}/tasks/{taskId}");
        delete.StatusCode.Should().Be(HttpStatusCode.OK);
        var after = await _admin.GetAsync($"{Base}/tasks/{taskId}");
        after.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await JsonOf(after)).GetProperty("message").GetString().Should().Be("找不到任務");
    }

    [Fact]
    public async Task TasksApi_CommentWithdrawAndCandidates_OverHttp()
    {
        var task = await JsonOf(await _admin.PostAsJsonAsync($"{Base}/tasks", new { title = "整理報價" }));
        var id = task.GetProperty("id").GetInt64();

        var added = await _admin.PostAsJsonAsync($"{Base}/tasks/{id}/comments", new { body = "請 @{sales01} 看一下" });
        added.StatusCode.Should().Be(HttpStatusCode.Created, await added.Content.ReadAsStringAsync());
        var comment = await JsonOf(added);
        comment.GetProperty("kind").GetString().Should().Be("comment");
        comment.GetProperty("mentions")[0].GetProperty("displayName").GetString().Should().Be("業務一");
        var cid = comment.GetProperty("id").GetInt64();

        (await _admin.PostAsync($"{Base}/tasks/{id}/comments/{cid}/withdraw", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await JsonOf(await _admin.GetAsync($"{Base}/tasks/{id}/comments"));
        list.GetArrayLength().Should().Be(2);
        list[0].GetProperty("kind").GetString().Should().Be("system");
        list[0].GetProperty("systemEvent").GetString().Should().Be("created");
        list[1].GetProperty("withdrawn").GetBoolean().Should().BeTrue();
        list[1].TryGetProperty("body", out _).Should().BeFalse();

        var blank = await _admin.PostAsJsonAsync($"{Base}/tasks/{id}/comments", new { body = " " });
        blank.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await blank.Content.ReadAsStringAsync()).Should().Contain("留言不能是空的");

        var candidates = await JsonOf(await _app.AsUser("floor01").GetAsync($"{Base}/users/mention-candidates?keyword=SALES"));
        candidates.EnumerateArray().Select(u => u.GetProperty("userId").GetString()).Should().Equal("sales01", "sales02", "sales03");
    }
    private async Task SeedOrganization()
    {
        using var scope = _app.Services.CreateScope();
        await DemoPeople.EnsureOrganizationAsync(scope.ServiceProvider.GetRequiredService<DynamicDbContext>(), DateTime.UtcNow);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task Patch_SupervisorReassign_RoundTrip_Undo()
    {
        await SeedOrganization();
        var keeper = _app.AsUser("keeper01");
        var ops = _app.AsUser("ops01");
        var created = await JsonOf(await keeper.PostAsJsonAsync($"{Base}/tasks", new { title = "盤點備品倉", assigneeUserId = "keeper01" }));
        var id = created.GetProperty("id").GetInt64();

        var otherField = await ops.PatchAsync($"{Base}/tasks/{id}", Body("""{"assigneeUserId":"sales01","title":"改標題"}"""));
        otherField.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var moved = await ops.PatchAsync($"{Base}/tasks/{id}", Body("""{"assigneeUserId":"sales01"}"""));
        moved.StatusCode.Should().Be(HttpStatusCode.OK, await moved.Content.ReadAsStringAsync());
        var body = await JsonOf(moved);
        body.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("id", "visible", "undoToken");
        body.GetProperty("visible").GetBoolean().Should().BeFalse();
        var token = body.GetProperty("undoToken").GetString()!;
        (await ops.GetAsync($"{Base}/tasks/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var tampered = token[..^2] + (token[^2] == 'A' ? "B" : "A") + token[^1];
        (await ops.PostAsync($"{Base}/tasks/{id}/undo-reassign", JsonContent.Create(new { undoToken = tampered }))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _app.AsUser("manager01").PostAsync($"{Base}/tasks/{id}/undo-reassign", JsonContent.Create(new { undoToken = token }))).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var undone = await ops.PostAsync($"{Base}/tasks/{id}/undo-reassign", JsonContent.Create(new { undoToken = token }));
        undone.StatusCode.Should().Be(HttpStatusCode.OK, await undone.Content.ReadAsStringAsync());
        (await JsonOf(undone)).GetProperty("assigneeUserId").GetString().Should().Be("keeper01");
        (await ops.PostAsync($"{Base}/tasks/{id}/undo-reassign", JsonContent.Create(new { undoToken = token }))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task Tasks_SubordinatesTab_Http()
    {
        await SeedOrganization();
        await _app.AsUser("keeper01").PostAsJsonAsync($"{Base}/tasks", new { title = "盤點備品倉", assigneeUserId = "keeper01" });
        await _app.AsUser("sales01").PostAsJsonAsync($"{Base}/tasks", new { title = "整理名片", assigneeUserId = "sales01" });

        var ops = await JsonOf(await _app.AsUser("ops01").GetAsync($"{Base}/tasks?tab=subordinates&filter=all"));
        ops.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()).Should().Equal("盤點備品倉");
        var manager = await JsonOf(await _app.AsUser("manager01").GetAsync($"{Base}/tasks?tab=subordinates&filter=all"));
        manager.GetProperty("totalCount").GetInt32().Should().Be(2);
        var sales = await JsonOf(await _app.AsUser("sales01").GetAsync($"{Base}/tasks?tab=subordinates&filter=all"));
        sales.GetProperty("totalCount").GetInt32().Should().Be(0);

        (await JsonOf(await _app.AsUser("manager01").GetAsync($"{Base}/tasks/capabilities"))).GetProperty("subordinates").GetBoolean().Should().BeTrue();
        (await JsonOf(await _app.AsUser("sales01").GetAsync($"{Base}/tasks/capabilities"))).GetProperty("subordinates").GetBoolean().Should().BeFalse();
    }
}
