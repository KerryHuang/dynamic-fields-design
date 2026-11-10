using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
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
    public async Task TasksApi_RecordTasks_MyTasksAndSummary()
    {
        var module = await CreateModule(_admin);
        await Activate(_admin, module);
        var title = await TitleKey(_admin, module);
        var record = await JsonOf(await _admin.PostAsJsonAsync($"{Base}/modules/{module}/records",
            new { values = new Dictionary<string, object> { [title] = "案件甲" } }));
        var recordId = record.GetProperty("id").GetInt64();

        var created = await _admin.PostAsJsonAsync($"{Base}/records/{recordId}/tasks", new { title = "聯絡窗口", assigneeUserId = "admin" });
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
        summary.GetProperty("dueToday").GetInt32().Should().Be(0);
        (await _admin.GetAsync($"{Base}/tasks?tab=bogus")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var delete = await _admin.DeleteAsync($"{Base}/tasks/{taskId}");
        delete.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
