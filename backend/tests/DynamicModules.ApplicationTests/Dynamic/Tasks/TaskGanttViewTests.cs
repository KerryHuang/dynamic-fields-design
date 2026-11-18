using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Application.Dynamic.TaskViewService;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

/// <summary>甘特檢視存檔（看板設計 §11.5.2.7，G19–G25）：依種類解析設定、列表分種類、修改另存分享、種類不符 404、寬鬆讀取。</summary>
public class TaskGanttViewTests : TaskBoardTestBase
{
    private const string GanttConfig = """{"version":1,"groupBy":"assignee","scope":"visible","scale":"month"}""";

    private async Task<TaskViewDetail> CreateGantt(string owner, string name = "展覽", string? config = GanttConfig)
    {
        var r = await SendAs(owner, new CreateTaskViewRequest("gantt", name, config is null ? null : J(config)));
        r.IsCreated.Should().BeTrue(r.Message + string.Join(";", r.Errors.SelectMany(e => e.Value)));
        return (TaskViewDetail)r.Data!;
    }

    private async Task<TaskViewListResponse> List(string user, string kind)
    {
        var r = await SendAs(user, new GetTaskViewsRequest(kind));
        r.IsSuccess.Should().BeTrue(r.Message);
        return (TaskViewListResponse)r.Data!;
    }

    [Fact]
    public async Task G19_Create_NoConfig_DefaultGantt()
    {
        var v = await CreateGantt("sales01", config: null);

        v.Kind.Should().Be("gantt");
        v.Config.GetRawText().Should().Be(
            """{"version":1,"groupBy":"parent","scope":"assigned","filters":{"statuses":["todo","inProgress","done"]},"scale":"week"}""");
        (await Db.Context.Set<TaskView>().AsNoTracking().SingleAsync(x => x.Id == v.Id)).Kind.Should().Be("gantt");
    }

    [Theory]
    [InlineData("""{"version":1,"groupBy":"status","scope":"assigned","scale":"week"}""", "甘特圖分組依據必須是 none、parent 或 assignee")]
    [InlineData("""{"version":1,"groupBy":"parent","scope":"assigned","scale":"week","filters":{"dueRange":{"kind":"thisWeek"}}}""", "甘特圖用時間範圍，不用到期日區間")]
    [InlineData("""{"version":1,"groupBy":"parent","scope":"assigned","scale":"week","colour":"red"}""", "colour")]
    [InlineData("""{"version":2,"groupBy":"parent","scope":"assigned","scale":"week"}""", "甘特圖設定版本必須是 1")]
    [InlineData("""{"version":1,"groupBy":"parent","scope":"assigned","scale":"year"}""", "甘特圖刻度必須是 day、week 或 month")]
    public async Task G20_StrictWrite_400_Config(string config, string message)
    {
        var r = await SendAs("sales01", new CreateTaskViewRequest("gantt", "展覽", J(config)));

        r.Errors.Should().ContainKey("config");
        r.Errors["config"].Should().ContainSingle().Which.Should().Contain(message);
        (await Db.Context.Set<TaskView>().AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task G20_BoardConfigOnGantt_400_GanttNameMessages()
    {
        (await SendAs("sales01", new CreateTaskViewRequest("gantt", "展覽", J(StatusConfig)))).Errors.Should().ContainKey("config");
        (await SendAs("sales01", new CreateTaskViewRequest("gantt", " ", null))).Errors["name"].Should().Equal("甘特圖名稱必填");
        (await SendAs("sales01", new CreateTaskViewRequest("gantt", new string('名', 101), null))).Errors["name"].Should().Equal("甘特圖名稱最多 100 字");
        (await SendAs("sales01", new CreateTaskViewRequest("board", " ", null))).Errors["name"].Should().Equal("看板名稱必填");
    }

    [Fact]
    public async Task G21_ListByKind()
    {
        await CreateView("sales01", "看板一");
        await CreateView("sales01", "看板二");
        var gantt = await CreateGantt("sales01");

        (await List("sales01", "gantt")).Mine.Select(v => v.Id).Should().Equal(gantt.Id);
        (await List("sales01", "board")).Mine.Should().HaveCount(2).And.OnlyContain(v => v.Kind == "board");
        (await SendAs("sales01", new GetTaskViewsRequest("list"))).Errors.Should().ContainKey("kind");
    }

    [Fact]
    public async Task G22_Update_GanttFormat_Stale409_Shared403_Copy()
    {
        var v = await CreateGantt("manager01");
        (await SendAs("manager01", new UpdateTaskViewRequest(v.Id, "展覽", J(StatusConfig), v.RowVersion))).Errors.Should().ContainKey("config");
        var ok = await SendAs("manager01", new UpdateTaskViewRequest(v.Id, "展覽二",
            J("""{"version":1,"groupBy":"none","scope":"involved","scale":"day","showSubtasks":false}"""), v.RowVersion));
        ok.IsSuccess.Should().BeTrue(ok.Message + string.Join(";", ok.Errors.SelectMany(e => e.Value)));
        var updated = (TaskViewDetail)ok.Data!;
        updated.Kind.Should().Be("gantt");
        updated.Config.GetProperty("scale").GetString().Should().Be("day");
        // 只改名、不帶設定：保留現有的甘特設定
        var renamed = (TaskViewDetail)(await SendAs("manager01", new UpdateTaskViewRequest(v.Id, "展覽三", null, updated.RowVersion))).Data!;
        renamed.Config.GetProperty("groupBy").GetString().Should().Be("none");
        var stale = await SendAs("manager01", new UpdateTaskViewRequest(v.Id, "展覽四", null, v.RowVersion));
        stale.IsConflict.Should().BeTrue();
        stale.Code.Should().Be("stale");

        (await Share("manager01", v.Id, ("user", "sales01"))).IsSuccess.Should().BeTrue();
        (await SendAs("sales01", new UpdateTaskViewRequest(v.Id, "改", null, renamed.RowVersion))).IsForbidden.Should().BeTrue();
        var copied = await SendAs("sales01", new CopyTaskViewRequest(v.Id));
        copied.IsCreated.Should().BeTrue(copied.Message);
        var copy = (TaskViewDetail)copied.Data!;
        copy.Kind.Should().Be("gantt");
        copy.OwnerUserId.Should().Be("sales01");
        copy.Shares.Should().BeEmpty();
        copy.Config.GetProperty("scale").GetString().Should().Be("day");
    }

    [Fact]
    public async Task G23_ShareToDepartment_AdminCanDelete()
    {
        var v = await CreateGantt("manager01");
        var sales = Db.Context.Set<Department>().AsNoTracking().Single(d => d.Code == "sales").Id;
        (await Share("manager01", v.Id, ("department", sales.ToString()))).IsSuccess.Should().BeTrue();

        (await List("sales02", "gantt")).Shared.Select(x => x.Id).Should().Equal(v.Id);
        (await List("sales02", "board")).Shared.Should().BeEmpty();
        (await SendAs("admin", new DeleteTaskViewRequest(v.Id))).IsSuccess.Should().BeTrue();
        (await Db.Context.Set<TaskView>().AsNoTracking().AnyAsync(x => x.Id == v.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task G23_TooManyShares_GanttMessage()
    {
        var v = await CreateGantt("manager01");
        var targets = Enumerable.Range(0, 201).Select(i => ("user", $"u{i}")).ToArray();

        (await Share("manager01", v.Id, targets)).Errors["shares"].Should().Equal("每個甘特圖最多分享給 200 個對象");
    }

    [Fact]
    public async Task G24_BoardEndpoints_WithGanttId_404()
    {
        var v = await CreateGantt("sales01");

        var columns = await SendAs("sales01", new GetTaskBoardColumnsRequest(v.Id, null));
        var page = await SendAs("sales01", new GetTaskBoardColumnPageRequest("todo", v.Id, null, 0));

        columns.IsNotFound.Should().BeTrue();
        columns.Message.Should().Be(TaskViewMessages.NotFound);
        page.IsNotFound.Should().BeTrue();
        var board = await CreateView("sales01");
        (await SendAs("sales01", new GetTaskBoardColumnsRequest(board.Id, null))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task G25_LenientRead_UnknownIgnored_BadScaleDefaults()
    {
        var view = new TaskView
        {
            Kind = "gantt", OwnerUserId = "sales01", Name = "舊格式", RowVersion = Guid.NewGuid().ToString(),
            ConfigJson = """{"version":1,"groupBy":"assignee","scope":"visible","scale":"year","colour":"red","filters":{"dueRange":{"kind":"thisWeek"},"keyword":" expo "}}"""
        };
        view.StampCreated("sales01", SeedTime);
        Db.Context.Add(view);
        await Db.Context.SaveChangesAsync();

        var r = await SendAs("sales01", new GetTaskViewRequest(view.Id));

        r.IsSuccess.Should().BeTrue(r.Message);
        ((TaskViewDetail)r.Data!).Config.GetRawText().Should().Be(
            """{"version":1,"groupBy":"assignee","scope":"visible","filters":{"keyword":"expo"},"scale":"week"}""");
    }
}
