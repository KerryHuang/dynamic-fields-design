using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Demo;

/// <summary>CRM 行銷活動的派工情境（任務管理設計 §10）：Today = 2026-11-05，春季工具機展 2027-02-01 開展。</summary>
[Collection(DemoGeneratedCollection.Name)]
public class CampaignTaskScenarioTests(DemoGeneratedFixture fx)
{
    private async Task<List<TaskItem>> TasksOf(string campaignName)
    {
        fx.Generated();
        var nameKey = await fx.Key("crm-campaign", "名稱");
        var record = (await fx.Records("crm-campaign")).Single(r => DemoGeneratedFixture.Text(r, nameKey) == campaignName);
        return await fx.Host.Db.Context.Set<TaskItem>().AsNoTracking().Where(t => t.RecordId == record.Id).OrderBy(t => t.Id).ToListAsync();
    }

    private Task<List<Notification>> NotificationsOf(string userId) =>
        fx.Host.Db.Context.Set<Notification>().AsNoTracking().Where(n => n.UserId == userId).ToListAsync();

    [Fact]
    public async Task SpringExpo_HasThreeTasks_WithUserScenarioDates()
    {
        var tasks = await TasksOf("春季工具機展");

        tasks.Select(t => (t.RuleTaskCode, t.Title, t.DueDate, t.AssigneeUserId)).Should().Equal(
            ("plan", "規劃活動內容", new DateOnly(2026, 12, 1), "sales01"),
            ("setup", "展場佈置", new DateOnly(2027, 1, 31), "sales02"),
            ("feedback", "活動回饋", new DateOnly(2027, 2, 15), "sales03"));
        tasks.Should().OnlyContain(t => t.Status == TaskItemStatus.Todo && t.CreatedBy == null);
    }

    [Fact]
    public async Task Seminar_HasNoSetupTask()
    {
        var tasks = await TasksOf("智慧製造研討會");

        tasks.Should().HaveCount(2);
        tasks.Select(t => t.RuleTaskCode).Should().Equal("plan", "feedback");
    }

    [Fact]
    public async Task Sales02_HasAssignedAndMentionNotifications()
    {
        fx.Generated();
        var mine = await NotificationsOf("sales02");

        var assigned = mine.Where(n => n.Kind == NotificationKinds.TaskAssigned).Should().ContainSingle().Subject;
        assigned.TitleSnapshot.Should().Be("展場佈置");
        var mentioned = mine.Where(n => n.Kind == NotificationKinds.TaskMentioned).Should().ContainSingle().Subject;
        mentioned.TitleSnapshot.Should().Be("活動回饋");
        mine.Should().OnlyContain(n => n.CreatedTime > DateTime.MinValue && n.CreatedTime < DemoTestHost.Today.ToDateTime(TimeOnly.MinValue),
            "通知要帶建立時間（模擬的過去時間），鈴鐺才能顯示");

        // 留言的 @ 標記存在內文裡
        var comment = await fx.Host.Db.Context.Set<TaskComment>().AsNoTracking()
            .SingleAsync(c => c.TaskId == mentioned.SourceId && c.Kind == TaskCommentKind.Comment);
        comment.AuthorUserId.Should().Be("sales03");
        comment.Body.Should().Be("@{sales02} 回饋表單我先擬");
    }

    [Fact]
    public async Task Sales01_NotNotifiedForOwnPlanTask()
    {
        fx.Generated();
        var mine = await NotificationsOf("sales01");

        mine.Should().NotContain(n => n.Kind == NotificationKinds.TaskAssigned, "自己建立的活動、自己是規劃負責人，不通知自己");
        mine.Should().ContainSingle(n => n.Kind == NotificationKinds.TaskMentioned && n.TitleSnapshot == "展場佈置");
    }
}
