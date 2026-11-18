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
        return await fx.Host.Db.Context.Set<TaskItem>().AsNoTracking().Where(t => t.RecordId == record.Id && t.ParentTaskId == null).OrderBy(t => t.Id).ToListAsync();
    }

    [Fact]
    public async Task SpringExpo_PlanTask_HasFourSubtasks_WithProgressAndParentFollowing()
    {
        fx.Generated();
        var nameKey = await fx.Key("crm-campaign", "名稱");
        var record = (await fx.Records("crm-campaign")).Single(r => DemoGeneratedFixture.Text(r, nameKey) == "春季工具機展");
        var all = await fx.Host.Db.Context.Set<TaskItem>().AsNoTracking().Where(t => t.RecordId == record.Id).OrderBy(t => t.Id).ToListAsync();
        var plan = all.Single(t => t.RuleTaskCode == "plan");
        var subs = all.Where(t => t.ParentTaskId == plan.Id).OrderBy(t => t.Id).ToList();

        subs.Select(t => (t.Title, t.Status, t.AssigneeUserId, t.CreatedBy)).Should().Equal(
            ("確認展位與預算", TaskItemStatus.Done, "sales01", "sales01"),
            ("整理邀請客戶名單", TaskItemStatus.InProgress, "sales01", "sales01"),
            ("準備展示樣品清單", TaskItemStatus.Todo, "sales01", "sales01"),
            ("訂購展示傳單", TaskItemStatus.Cancelled, "sales01", "sales01"));
        // 第一個子任務開工時，待辦的父任務由連動改成進行中；其他兩個規則任務不受影響
        plan.Status.Should().Be(TaskItemStatus.InProgress);
        all.Where(t => t.ParentTaskId == null && t.Id != plan.Id).Should().OnlyContain(t => t.Status == TaskItemStatus.Todo);
        subs.Should().OnlyContain(t => t.RecordId == record.Id && t.ModuleId == plan.ModuleId);
    }

    [Fact]
    public async Task SpringExpo_TasksHaveStartDates_ForTheGanttChart()
    {
        // G62：預設甘特圖（sales01）看得到「規劃活動內容」的長條、子任務與跨度；規則任務補開始日不設覆寫旗標（GT-T23）
        fx.Generated();
        var nameKey = await fx.Key("crm-campaign", "名稱");
        var record = (await fx.Records("crm-campaign")).Single(r => DemoGeneratedFixture.Text(r, nameKey) == "春季工具機展");
        var all = await fx.Host.Db.Context.Set<TaskItem>().AsNoTracking().Where(t => t.RecordId == record.Id).OrderBy(t => t.Id).ToListAsync();

        all.Where(t => t.ParentTaskId == null).Select(t => (t.RuleTaskCode, t.StartDate, t.DueDate, t.DueOverridden)).Should().Equal(
            ("plan", new DateOnly(2026, 11, 3), new DateOnly(2026, 12, 1), false),
            ("setup", new DateOnly(2027, 1, 27), new DateOnly(2027, 1, 31), false),
            ("feedback", new DateOnly(2027, 2, 4), new DateOnly(2027, 2, 15), false));
        var plan = all.Single(t => t.RuleTaskCode == "plan");
        all.Where(t => t.ParentTaskId == plan.Id).OrderBy(t => t.Id).Select(t => (t.Title, t.StartDate, t.DueDate)).Should().Equal(
            ("確認展位與預算", new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 12)),
            ("整理邀請客戶名單", new DateOnly(2026, 11, 8), new DateOnly(2026, 11, 19)),
            ("準備展示樣品清單", new DateOnly(2026, 11, 15), new DateOnly(2026, 11, 26)),
            ("訂購展示傳單", new DateOnly(2026, 11, 19), new DateOnly(2026, 11, 26)));
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
        tasks.Should().OnlyContain(t => t.CreatedBy == null);
        // 「規劃活動內容」底下有子任務、第一個已開工，父任務由連動改成進行中；其餘維持待辦
        tasks.Single(t => t.RuleTaskCode == "plan").Status.Should().Be(TaskItemStatus.InProgress);
        tasks.Where(t => t.RuleTaskCode != "plan").Should().OnlyContain(t => t.Status == TaskItemStatus.Todo);
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

        // 白名單：sales01 收到的指派通知只能是設計案的兩種任務；活動情境自己建、自己負責的任務一律不通知自己
        mine.Where(n => n.Kind == NotificationKinds.TaskAssigned).Select(n => n.TitleSnapshot)
            .Should().OnlyContain(t => t == "確認需求並交設計" || t == "將設計圖交客戶確認", "自己建立的活動、自己是規劃負責人，不通知自己");
        mine.Should().ContainSingle(n => n.Kind == NotificationKinds.TaskMentioned && n.TitleSnapshot == "展場佈置");
    }
}
