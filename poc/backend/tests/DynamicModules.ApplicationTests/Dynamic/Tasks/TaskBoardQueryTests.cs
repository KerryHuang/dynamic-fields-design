using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

/// <summary>看板欄查詢（看板設計 §5.1、§5.4、§5.7、§5.10）：範圍、篩選、欄、總數、分頁。</summary>
public class TaskBoardQueryTests : TaskBoardTestBase
{
    private static DateOnly D(int day) => new(2026, 11, day);

    [Fact]
    public async Task StatusBoard_ColumnsAndTotals()
    {
        await SeedTask("乙", createdBy: "sales01", assignee: "sales01", due: D(9));
        await SeedTask("甲", createdBy: "sales01", assignee: "sales01", due: D(5));
        await SeedTask("無日期", createdBy: "sales01", assignee: "sales01", due: null);
        await SeedTask("進行", createdBy: "sales01", assignee: "sales01", status: TaskItemStatus.InProgress);
        var done = await SeedTask("完成", createdBy: "sales01", assignee: "sales01", status: TaskItemStatus.Done);
        await SeedTask("取消", createdBy: "sales01", assignee: "sales01", status: TaskItemStatus.Cancelled);
        await SeedTask("別人的", createdBy: "sales02", assignee: "sales02");

        var b = await Board("sales01");   // 沒帶看板 → 預設看板

        b.Columns.Select(c => c.Key).Should().Equal("todo", "inProgress", "done");
        b.Today.Should().Be(Today);
        b.WeekEnd.Should().Be(D(8));
        b.LaterDefault.Should().Be(D(15));
        b.ScopeAvailable.Should().BeTrue();
        Col(b, "todo").Total.Should().Be(3);
        Col(b, "todo").Cards.Select(c => c.Title).Should().Equal("甲", "乙", "無日期");
        Col(b, "inProgress").Total.Should().Be(1);
        Col(b, "done").Cards.Single().Id.Should().Be(done.Id);
        b.Columns.Should().OnlyContain(c => c.Droppable && c.Kind == "status" && !c.HasMore && !c.Truncated);

        var all = await Board("sales01", """{"version":1,"groupBy":"status","scope":"assigned","columns":{"order":["done"]},"sort":{"key":"title","dir":"desc"}}""");
        all.Columns.Select(c => c.Key).Should().Equal("done", "todo", "inProgress", "cancelled");
        Col(all, "todo").Cards.Select(c => c.Title).Should().Equal(new[] { "甲", "乙", "無日期" }.OrderByDescending(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task AssigneeBoard_ExplicitInactiveAutoAppend_Capped50()
    {
        var tasks = new List<TaskItem>();
        for (var i = 1; i <= 55; i++)
            for (var n = 0; n < (i <= 3 ? 4 - i : 1) + 0; n++)
                tasks.Add(NewTask($"任務 {i}-{n}", $"u{i:00}"));
        tasks.Add(NewTask("未指派", null));
        tasks.Add(NewTask("停用者的", "qa01"));
        await AddAll(tasks);
        await Db.Context.Set<AppUser>().Where(u => u.UserId == "qa01").ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));

        var b = await Board("admin", """{"version":1,"groupBy":"assignee","scope":"visible","columns":{"assignees":["qa01"]}}""");

        b.Columns[0].Key.Should().Be("user:qa01");
        b.Columns[0].Inactive.Should().BeTrue();
        b.Columns[0].Droppable.Should().BeFalse();
        b.Columns[0].DropReason.Should().NotBeNullOrEmpty();
        b.Columns[0].Name.Should().Be("品保人員");
        b.Columns[1].Key.Should().Be("unassigned");
        b.Columns[1].Total.Should().Be(1);
        // 自動附加依任務數多到少：u01（3 筆）、u02（2 筆），其餘 1 筆依帳號
        b.Columns.Skip(2).Take(3).Select(c => c.Key).Should().Equal("user:u01", "user:u02", "user:u03");
        b.Columns.Count(c => c.Kind == "user").Should().Be(50);
        b.OmittedAssignees.Should().Be(55 - 49);
        Col(b, "user:u01").Total.Should().Be(3);
    }

    [Fact]
    public async Task SubordinateScope_ListsAllSubordinateColumns()
    {
        await SeedTask("整理名片", createdBy: "sales01", assignee: "sales01");
        await SeedTask("自己的", createdBy: "manager01", assignee: "manager01");

        var b = await Board("manager01", """{"version":1,"groupBy":"assignee","scope":"subordinates","columns":{"autoAppendAssignees":false}}""");

        b.ScopeAvailable.Should().BeTrue();
        b.Columns.Where(c => c.Kind == "user").Select(c => c.UserId).Should().BeEquivalentTo(
            ["sales01", "sales02", "sales03", "designer01", "ops01", "keeper01", "buyer01", "planner01", "floor01", "floor02", "maint01", "qa01"]);
        b.Columns.Where(c => c.Kind == "user").First().UserId.Should().Be("sales01");   // 有任務的排前面
        Col(b, "user:sales01").Total.Should().Be(1);
        b.Columns.Should().NotContain(c => c.UserId == "manager01");
    }

    [Fact]
    public async Task SubordinateScope_NonManager_EmptyWithFlag()
    {
        await SeedTask("自己的", createdBy: "sales01", assignee: "sales01");

        var b = await Board("sales01", """{"version":1,"groupBy":"status","scope":"subordinates"}""");

        b.ScopeAvailable.Should().BeFalse();
        b.Columns.Select(c => c.Key).Should().Equal("todo", "inProgress", "done", "cancelled");
        b.Columns.Should().OnlyContain(c => c.Total == 0 && c.Cards.Count == 0);
    }

    [Fact]
    public async Task DueBoard_OnlyOpen()
    {
        await SeedTask("逾期", createdBy: "sales01", assignee: "sales01", due: D(3));
        await SeedTask("今天", createdBy: "sales01", assignee: "sales01", due: D(4), status: TaskItemStatus.InProgress);
        await SeedTask("本週", createdBy: "sales01", assignee: "sales01", due: D(8));
        await SeedTask("之後", createdBy: "sales01", assignee: "sales01", due: D(9));
        await SeedTask("無", createdBy: "sales01", assignee: "sales01");
        await SeedTask("完成逾期", createdBy: "sales01", assignee: "sales01", due: D(1), status: TaskItemStatus.Done);
        await SeedTask("取消今天", createdBy: "sales01", assignee: "sales01", due: D(4), status: TaskItemStatus.Cancelled);

        var b = await Board("sales01", """{"version":1,"groupBy":"dueBucket","scope":"assigned","filters":{"statuses":["done"]}}""");

        b.Columns.Select(c => (c.Key, c.Total)).Should().Equal(("overdue", 1), ("today", 1), ("thisWeek", 1), ("later", 1), ("none", 1));
        Col(b, "overdue").Droppable.Should().BeFalse();
        Col(b, "overdue").DropReason.Should().NotBeNullOrEmpty();
        Col(b, "thisWeek").Droppable.Should().BeTrue();
        Col(b, "today").Cards.Single().DueToday.Should().BeTrue();
        Col(b, "overdue").Cards.Single().Overdue.Should().BeTrue();
    }

    [Fact]
    public async Task ListThisWeek_EqualsTodayPlusThisWeek_ForEveryWeekStart()
    {
        for (var day = 1; day <= 15; day++)
            await SeedTask($"到期 {day}", createdBy: "sales01", assignee: "sales01", due: D(day));

        foreach (var ws in new[] { "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday" })
        {
            (await SetWeekStart(ws)).IsSuccess.Should().BeTrue();
            var list = await SendAs("sales01", new GetMyTasksRequest("assigned", "thisWeek", 1, 100));
            var listTotal = ((PagedResponse<TaskSummaryResponse>)list.Data!).TotalCount;

            var b = await Board("sales01", """{"version":1,"groupBy":"dueBucket","scope":"assigned"}""");

            (Col(b, "today").Total + Col(b, "thisWeek").Total).Should().Be(listTotal, ws);
            Col(b, "thisWeek").Droppable.Should().Be(b.WeekEnd > b.Today, ws);
        }
    }

    [Fact]
    public async Task Paging_50_UpTo500_ThenTruncated()
    {
        await AddAll(Enumerable.Range(1, 520).Select(i => NewTask($"任務 {i:000}", "sales01")).ToList());
        const string config = """{"version":1,"groupBy":"status","scope":"assigned","sort":{"key":"title"}}""";

        var b = await Board("sales01", config);
        var todo = Col(b, "todo");
        todo.Total.Should().Be(520);
        todo.Cards.Should().HaveCount(50);
        todo.HasMore.Should().BeTrue();
        todo.Truncated.Should().BeTrue();

        async Task<TaskBoardPageResponse> Page(int offset)
        {
            var r = await SendAs("sales01", new GetTaskBoardColumnPageRequest("todo", null, J(config), offset));
            r.IsSuccess.Should().BeTrue(r.Message);
            return (TaskBoardPageResponse)r.Data!;
        }

        var second = await Page(50);
        second.Cards.Should().HaveCount(50);
        second.Cards[0].Title.Should().Be("任務 051");
        second.HasMore.Should().BeTrue();
        var last = await Page(450);
        last.Cards.Should().HaveCount(50);
        last.Cards[^1].Title.Should().Be("任務 500");
        last.HasMore.Should().BeFalse();
        last.Truncated.Should().BeTrue();
        var beyond = await Page(500);
        beyond.Cards.Should().BeEmpty();
        beyond.Truncated.Should().BeTrue();
        (await SendAs("sales01", new GetTaskBoardColumnPageRequest("soon", null, J(config), 0))).Errors.Should().ContainKey("column");
    }

    [Fact]
    public async Task Card_RecordNoAccess_NoModuleKeyOrTitle()
    {
        var t = await BuildTarget();
        var rec = await NewRecord(t, "sales02", "機密案件");
        await SeedTask("掛在別人記錄上", createdBy: "sales02", assignee: "sales01", target: t, recordId: rec);

        var b = await Board("sales01");

        var card = Col(b, "todo").Cards.Single();
        card.Record.Should().NotBeNull();
        card.Record!.Accessible.Should().BeFalse();
        card.Record.ModuleKey.Should().BeNull();
        card.Record.RecordId.Should().BeNull();
        card.Record.Title.Should().BeNull();
        var json = JsonSerializer.Serialize(b, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.Should().NotContain("機密案件").And.NotContain(t.Key).And.NotContain("案件甲");
    }

    [Fact]
    public async Task SharedBoard_EachViewerSeesOwnScope()
    {
        await SeedTask("業務一的", createdBy: "sales01", assignee: "sales01");
        await SeedTask("業務二的", createdBy: "sales02", assignee: "sales02");
        await SeedTask("提到業務一", createdBy: "sales03", assignee: "sales03", mentioned: ["sales01"]);
        await SeedTask("主管自己的", createdBy: "manager01", assignee: "manager01");
        var view = await CreateView("manager01", "全部看得到的", """{"version":1,"groupBy":"status","scope":"visible"}""");
        (await Share("manager01", view.Id, ("user", "sales01"))).IsSuccess.Should().BeTrue();

        var manager = await Board("manager01", viewId: view.Id);
        var sales = await Board("sales01", viewId: view.Id);

        var managerIds = manager.Columns.SelectMany(c => c.Cards).Select(c => c.Title).ToList();
        var salesIds = sales.Columns.SelectMany(c => c.Cards).Select(c => c.Id).ToList();
        managerIds.Should().BeEquivalentTo(["業務一的", "業務二的", "提到業務一", "主管自己的"]);
        sales.Columns.SelectMany(c => c.Cards).Select(c => c.Title).Should().BeEquivalentTo(["業務一的", "提到業務一"]);
        As("sales01");
        using (var scope = NewScope())
        {
            var visibility = await scope.ServiceProvider.GetRequiredService<TaskVisibilityService>().ForCurrentUserAsync(default);
            var visible = await scope.ServiceProvider.GetRequiredService<ITaskRepository>().ListVisibleIdsAsync(visibility, default);
            salesIds.Should().OnlyContain(id => visible.Contains(id));
        }
        As("admin");
        (await SendAs("sales02", new GetTaskBoardColumnsRequest(view.Id, null))).IsNotFound.Should().BeTrue();
        (await SendAs("sales01", new GetTaskBoardColumnsRequest(view.Id, J("""{"version":1,"groupBy":"status","scope":"visible"}""")))).IsInvalid.Should().BeTrue();
    }

    [Fact]
    public async Task Config_ReferencesInactiveUser_StillLoads()
    {
        await SeedTask("品保的", createdBy: "admin", assignee: "qa01");
        var view = await CreateView("admin", "品保看板",
            """{"version":1,"groupBy":"assignee","scope":"visible","filters":{"assigneeUserIds":["qa01","ghost"]},"columns":{"assignees":["qa01","ghost"]}}""");
        await Db.Context.Set<AppUser>().Where(u => u.UserId == "qa01").ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));

        var b = await Board("admin", viewId: view.Id);

        Col(b, "user:qa01").Inactive.Should().BeTrue();
        Col(b, "user:qa01").Total.Should().Be(1);
        Col(b, "user:qa01").Cards.Single().AssigneeActive.Should().BeFalse();
        Col(b, "user:ghost").Inactive.Should().BeTrue();
        Col(b, "user:ghost").Name.Should().BeNull();
    }

    [Fact]
    public async Task QueryCount_DependsOnColumnsNotCards()
    {
        async Task<int> Measure()
        {
            await Board("sales01");   // 暖身：政策、權限快取不算
            var before = Db.Counter.Count;
            await Board("sales01");
            return Db.Counter.Count - before;
        }

        await AddAll(Enumerable.Range(1, 3).Select(i => NewTask($"少 {i}", "sales01", (TaskItemStatus)(i % 3))).ToList());
        var few = await Measure();
        await AddAll(Enumerable.Range(1, 60).Select(i => NewTask($"多 {i}", "sales01", (TaskItemStatus)(i % 3))).ToList());
        var many = await Measure();

        many.Should().Be(few);
    }

    private static TaskItem NewTask(string title, string? assignee, TaskItemStatus status = TaskItemStatus.Todo)
    {
        var t = new TaskItem { Title = title, CreatedBy = "admin", AssigneeUserId = assignee, Status = status, CreatedTime = SeedTime };
        t.Participants.Add(new TaskParticipant { UserId = "admin", Reason = TaskParticipantReason.Creator });
        if (assignee is not null) t.Participants.Add(new TaskParticipant { UserId = assignee, Reason = TaskParticipantReason.Assignee });
        return t;
    }

    private async Task AddAll(List<TaskItem> tasks)
    {
        Db.Context.AddRange(tasks);
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
    }
}
