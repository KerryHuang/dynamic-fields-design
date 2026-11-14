using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Demo;

/// <summary>任務看板的示範資料（看板設計 §4）：Today = 2026-11-05（星期四），預設一週從星期一開始。</summary>
[Collection(DemoGeneratedCollection.Name)]
public class TaskBoardScenarioTests(DemoGeneratedFixture fx)
{
    private static readonly DateOnly Today = new(2026, 11, 5);

    private static readonly (string Owner, string Title)[] Expected =
    [
        ("sales01", "整理嵐岳精密報價比較"), ("sales01", "回覆恆川機械樣品問題"), ("sales01", "準備週會業績摘要"),
        ("sales01", "更新客戶聯絡人資料"), ("sales01", "整理名片"), ("sales01", "寄送曜陽電子型錄"),
        ("sales01", "確認標準導柱庫存"), ("sales02", "確認岳峰精機交期"), ("sales02", "季度目標草稿"),
        ("sales03", "整理展場報名資料"), ("keeper01", "盤點備品倉")
    ];

    private async Task<List<TaskItem>> SetTasks()
    {
        fx.Generated();
        var titles = Expected.Select(e => e.Title).Append("整理樣品櫃").ToList();
        return await fx.Host.Db.Context.Set<TaskItem>().AsNoTracking().Include(t => t.Participants)
            .Where(t => titles.Contains(t.Title)).OrderBy(t => t.Id).ToListAsync();
    }

    /// <summary>逐頁讀到底：Demo 任務變多時，這批任務可能落在第 2 頁之後。</summary>
    private async Task<List<TaskSummaryResponse>> Subordinates(string user)
    {
        var all = new List<TaskSummaryResponse>();
        for (var page = 1; ; page++)
        {
            var r = await fx.As(user, new GetMyTasksRequest("subordinates", "all", page, 100));
            r.IsSuccess.Should().BeTrue(r.Message);
            var data = (PagedResponse<TaskSummaryResponse>)r.Data!;
            all.AddRange(data.Items);
            if (data.Items.Count == 0 || all.Count >= data.TotalCount) return all;
        }
    }

    [Fact]
    public async Task PersonalTasks_CreatedByAssignee_NoAssignedNotifications()
    {
        var tasks = await SetTasks();

        tasks.Should().HaveCount(12);
        foreach (var (owner, title) in Expected)
            tasks.Single(t => t.Title == title).Should().Match<TaskItem>(t => t.CreatedBy == owner && t.AssigneeUserId == owner, title);
        var cabinet = tasks.Single(t => t.Title == "整理樣品櫃");
        cabinet.CreatedBy.Should().Be("sales02");
        cabinet.AssigneeUserId.Should().BeNull();
        var ids = tasks.Select(t => (long?)t.Id).ToHashSet();
        (await fx.Host.Db.Context.Set<Notification>().AsNoTracking()
            .Where(n => n.Kind == NotificationKinds.TaskAssigned && ids.Contains(n.SourceId)).CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task DueDatesAndStatuses_FollowTheTable()
    {
        var tasks = await SetTasks();
        TaskItem T(string title) => tasks.Single(t => t.Title == title);

        T("整理嵐岳精密報價比較").DueDate.Should().Be(Today.AddDays(-2));
        T("整理嵐岳精密報價比較").Status.Should().Be(TaskItemStatus.InProgress);
        T("回覆恆川機械樣品問題").DueDate.Should().Be(Today);
        T("準備週會業績摘要").DueDate.Should().Be(new DateOnly(2026, 11, 8));   // 星期日：本週最後一天
        T("更新客戶聯絡人資料").DueDate.Should().Be(Today.AddDays(12));
        T("整理名片").DueDate.Should().BeNull();
        T("寄送曜陽電子型錄").Status.Should().Be(TaskItemStatus.Done);
        T("寄送曜陽電子型錄").CompletedTime.Should().NotBeNull();
        T("確認岳峰精機交期").DueDate.Should().Be(Today.AddDays(-1));
        T("季度目標草稿").Status.Should().Be(TaskItemStatus.InProgress);
        T("整理樣品櫃").DueDate.Should().Be(Today.AddDays(5));
        T("盤點備品倉").DueDate.Should().Be(Today.AddDays(1));
        tasks.Where(t => t.Title != "寄送曜陽電子型錄").Select(t => t.Status).Should().OnlyContain(s => s != TaskItemStatus.Done);
    }

    [Fact]
    public async Task Manager01_SubordinatesTab_ListsSalesAndOthers()
    {
        fx.Generated();
        var listed = await Subordinates("manager01");

        var titles = listed.Select(t => t.Title).ToHashSet();
        titles.Should().Contain(Expected.Select(e => e.Title).Append("整理樣品櫃"));
        listed.Single(t => t.Title == "整理樣品櫃").AssigneeUserId.Should().BeNull();
    }

    [Fact]
    public async Task Ops01_SubordinatesTab_ListsKeeperOnlyFromThisSet()
    {
        fx.Generated();
        var titles = (await Subordinates("ops01")).Select(t => t.Title).ToHashSet();

        titles.Should().Contain("盤點備品倉");
        titles.Should().NotContain(Expected.Where(e => e.Owner != "keeper01").Select(e => e.Title).Append("整理樣品櫃"));
    }

    [Fact]
    public async Task Manager01_InventoryTask_RecordHidden()
    {
        fx.Generated();
        var task = (await Subordinates("manager01")).Single(t => t.Title == "確認標準導柱庫存");

        task.Record.Should().Be(new TaskRecordRef(false, null, null, null, false));
        task.Supervised.Should().BeTrue();
        // 掛記錄的人（業務一）自己看得到記錄
        var mine = await fx.As("sales01", new GetMyTasksRequest("assigned", "all", 1, 100));
        ((PagedResponse<TaskSummaryResponse>)mine.Data!).Items.Single(t => t.Title == "確認標準導柱庫存").Record!.Accessible.Should().BeTrue();
    }
}
