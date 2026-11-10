using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

public class NotificationsApiTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Get_ReturnsItemsAndUnreadCount()
    {
        var admin = _app.AsUser();
        var sales = _app.AsUser("sales01");
        var task = await JsonOf(await admin.PostAsJsonAsync($"{Base}/tasks", new { title = "寄送合約", assigneeUserId = "sales01" }));
        var taskId = task.GetProperty("id").GetInt64();

        var list = await JsonOf(await sales.GetAsync($"{Base}/notifications?take=20"));

        list.GetProperty("unreadCount").GetInt32().Should().Be(1);
        var item = list.GetProperty("items")[0];
        item.GetProperty("kind").GetString().Should().Be("taskAssigned");
        item.GetProperty("title").GetString().Should().Be("寄送合約");
        item.GetProperty("link").GetString().Should().Be($"/tasks?open={taskId}");
        item.GetProperty("read").GetBoolean().Should().BeFalse();
        item.TryGetProperty("createdTime", out _).Should().BeTrue();
        var id = item.GetProperty("id").GetInt64();

        var others = await admin.PostAsync($"{Base}/notifications/{id}/read", null);
        others.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await JsonOf(others)).GetProperty("message").GetString().Should().Be("找不到通知");
        (await sales.PostAsync($"{Base}/notifications/{id}/read", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(await sales.GetAsync($"{Base}/notifications"))).GetProperty("unreadCount").GetInt32().Should().Be(0);

        await admin.PostAsJsonAsync($"{Base}/tasks", new { title = "第二件", assigneeUserId = "sales01" });
        (await sales.PostAsync($"{Base}/notifications/read-all", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(await sales.GetAsync($"{Base}/notifications"))).GetProperty("unreadCount").GetInt32().Should().Be(0);

        // 刪除任務後，指向它的通知一起刪除
        (await admin.DeleteAsync($"{Base}/tasks/{taskId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        var after = await JsonOf(await sales.GetAsync($"{Base}/notifications"));
        after.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()).Should().Equal("第二件");
    }
}
