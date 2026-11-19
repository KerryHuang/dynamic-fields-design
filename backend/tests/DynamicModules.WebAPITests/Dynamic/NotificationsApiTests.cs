using System.Net;
using System.Net.Http.Json;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

[Trait("Category", "Postgres")]
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

    /// <summary>維運兩個種類帶標題代碼與參數給鈴鐺組字（維運設計 §17.2.5）；其他種類不輸出 DataJson。</summary>
    [Fact]
    public async Task OpsKinds_ExposeTitleKeyAndParams_OtherKindsDoNot()
    {
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            db.Set<Notification>().AddRange(
                new Notification
                {
                    UserId = "sales01", Kind = NotificationKinds.OpsRulePaused, TitleSnapshot = "任務規則「r1」連續失敗 5 次，已自動暫停",
                    Link = "/system/dynamic-modules/crm?tab=taskRules&rule=r1",
                    DataJson = """{"titleKey":"ops.title.rulePaused","params":{"ruleKey":"r1","failures":5},"incidentId":7}""",
                    CreatedTime = DateTime.Now,
                },
                new Notification
                {
                    UserId = "sales01", Kind = NotificationKinds.TaskAssigned, TitleSnapshot = "寄送合約", Link = "/tasks?open=1",
                    DataJson = """{"titleKey":"x"}""", CreatedTime = DateTime.Now.AddMinutes(-1),
                });
            await db.SaveChangesAsync();
        }

        var items = (await JsonOf(await _app.AsUser("sales01").GetAsync($"{Base}/notifications"))).GetProperty("items");
        var ops = items.EnumerateArray().Single(i => i.GetProperty("kind").GetString() == "opsRulePaused");
        ops.GetProperty("titleKey").GetString().Should().Be("ops.title.rulePaused");
        ops.GetProperty("titleParams").GetProperty("ruleKey").GetString().Should().Be("r1");
        ops.GetProperty("titleParams").GetProperty("failures").GetInt64().Should().Be(5);
        var task = items.EnumerateArray().Single(i => i.GetProperty("kind").GetString() == "taskAssigned");
        task.TryGetProperty("titleKey", out var k).Should().BeFalse(k.ToString());
    }
}
