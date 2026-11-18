using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

/// <summary>子任務在看板與清單上（看板設計 §10.7）：showSubtasks（C39）、我的任務與摘要卡（C40）、列表不洩漏（C19）、查詢次數（C18）。</summary>
public class SubtaskBoardTests : TaskBoardTestBase
{
    private const string Outsider = "outsider01";

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        Db.Context.Add(new AppUser { UserId = Outsider, DisplayName = "外部人員", IsActive = true });
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
    }

    private async Task<TaskItem> SeedChild(TaskItem parent, string title, string? createdBy = "sales01", string? assignee = null,
        TaskItemStatus status = TaskItemStatus.Todo, DateOnly? due = null)
    {
        var child = await SeedTask(title, createdBy: createdBy, assignee: assignee, status: status, due: due,
            recordId: parent.RecordId, moduleId: parent.ModuleId);
        await Db.Context.Set<TaskItem>().Where(t => t.Id == child.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.ParentTaskId, parent.Id));
        return child;
    }

    private static IEnumerable<TaskCardResponse> Cards(TaskBoardResponse b) => b.Columns.SelectMany(c => c.Cards);

    [Fact]
    public async Task C39_ShowSubtasks_FalseHidesChildren_DefaultShowsThem()
    {
        var parent = await SeedTask("辦行銷活動", createdBy: "sales01", assignee: "sales01");
        var child = await SeedChild(parent, "訂場地", assignee: "sales01");

        var hidden = await Board("sales01", """{"version":1,"groupBy":"status","scope":"assigned","showSubtasks":false}""");
        var shown = await Board("sales01", """{"version":1,"groupBy":"status","scope":"assigned","showSubtasks":true}""");
        var omitted = await Board("sales01", """{"version":1,"groupBy":"status","scope":"assigned"}""");

        Cards(hidden).Select(c => c.Id).Should().Equal(parent.Id);
        Col(hidden, "todo").Total.Should().Be(1, "欄計數也只算頂層");
        hidden.Config.GetProperty("showSubtasks").GetBoolean().Should().BeFalse();
        Cards(shown).Select(c => c.Id).Should().BeEquivalentTo([parent.Id, child.Id]);
        Col(omitted, "todo").Total.Should().Be(2);
        var childCard = Cards(omitted).Single(c => c.Id == child.Id);
        childCard.ParentTaskId.Should().Be(parent.Id);
        childCard.ParentTitle.Should().Be("辦行銷活動");
        childCard.Level.Should().Be(TaskLevel.Child);
        var parentCard = Cards(omitted).Single(c => c.Id == parent.Id);
        parentCard.Level.Should().Be(TaskLevel.Parent);
        parentCard.Progress.Should().Be(new TaskProgress(0, 0, 1));
        // 舊設定（沒有這個欄位）：版本不變、回應不多出欄位
        omitted.Config.GetProperty("version").GetInt32().Should().Be(TaskBoardConfig.CurrentVersion);
        omitted.Config.TryGetProperty("showSubtasks", out _).Should().BeFalse();
        TaskBoardConfig.Read("""{"version":1,"groupBy":"status","scope":"assigned"}""").ShowSubtasks.Should().BeNull();
        TaskBoardConfig.Parse("""{"version":1,"groupBy":"status","scope":"assigned","showSubtasks":"no"}""").Config.Should().BeNull();
    }

    [Fact]
    public async Task C40_MyTasksAndSummary_IncludeChildren()
    {
        var parent = await SeedTask("辦行銷活動", createdBy: "sales01", assignee: "sales01");
        var child = await SeedChild(parent, "訂便當", assignee: "sales02", due: Today);

        var assigned = await SendAs("sales02", new GetMyTasksRequest("assigned", "open", 1, 50));
        var summary = await SendAs("sales02", new GetTaskSummaryRequest());

        ((PagedResponse<TaskSummaryResponse>)assigned.Data!).Items.Select(i => i.Id).Should().Equal(child.Id);
        ((TaskSummaryCounts)summary.Data!).DueToday.Should().Be(1);
    }

    [Fact]
    public async Task C19_Outsider_NeverSeesParentOrChild()
    {
        var parent = await SeedTask("辦行銷活動", createdBy: "sales01", assignee: "sales01");
        var child = await SeedChild(parent, "訂場地", assignee: "sales02");
        var hiddenIds = new[] { parent.Id, child.Id };

        foreach (var tab in new[] { "assigned", "created", "mentioned", "subordinates" })
        {
            var r = await SendAs(Outsider, new GetMyTasksRequest(tab, "all", 1, 50));
            ((PagedResponse<TaskSummaryResponse>)r.Data!).Items.Select(i => i.Id).Should().NotContain(hiddenIds, tab);
        }
        var b = await Board(Outsider, """{"version":1,"groupBy":"status","scope":"visible"}""");
        Cards(b).Select(c => c.Id).Should().NotContain(hiddenIds);
        b.Columns.Sum(c => c.Total).Should().Be(0);
        (await SendAs(Outsider, new GetTaskRequest(child.Id))).IsNotFound.Should().BeTrue();
    }

    [Fact]
    public async Task C18_BoardQueryCount_DoesNotGrowWithSubtasks()
    {
        async Task<int> CountFor(int pairs)
        {
            await Db.Context.Set<TaskItem>().ExecuteDeleteAsync();
            for (var i = 0; i < pairs; i++)
            {
                var parent = await SeedTask($"父{i}", createdBy: "sales01", assignee: "sales01");
                await SeedChild(parent, $"子{i}", createdBy: "sales02", assignee: "sales01");
            }
            Db.Context.ChangeTracker.Clear();
            var before = Db.Counter.Count;
            var b = await Board("sales01", """{"version":1,"groupBy":"status","scope":"visible"}""");
            var used = Db.Counter.Count - before;
            Cards(b).Should().HaveCount(pairs * 2);
            return used;
        }

        var small = await CountFor(2);
        var large = await CountFor(25);   // 50 張卡：25 個父任務、25 個子任務

        large.Should().Be(small, "父任務、進度都批次查，查詢次數不隨卡片數成長");
    }
}
