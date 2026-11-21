using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Application.Dynamic.TaskViewService;
using DynamicModules.Domain.Tasks;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

/// <summary>
/// 負責人看板的隱藏欄：直接送請求（不經畫面）時的負面案例（kb2 審查 S2，票 #17）。
/// 負責人看板只能隱藏「未指派」欄（設計 §6.1）：欄位清單不列它，但看得到的任務範圍不因隱藏而放寬，
/// 也不能用 columns.hidden 藏起某個人的欄，或拿隱藏當繞過可見條件的手段。
/// </summary>
[Trait("Category", "Postgres")]
public class TaskBoardHiddenColumnTests : TaskBoardTestBase
{
    private const string HiddenUnassigned = """{"version":1,"groupBy":"assignee","scope":"visible","columns":{"hidden":["unassigned"]}}""";

    private static string Msg(ServiceResult r) => r.Message + string.Join(";", r.Errors.SelectMany(e => e.Value));

    private async Task SeedUnassigned()
    {
        await SeedTask("sales01 的未指派", createdBy: "sales01", assignee: null);
        await SeedTask("sales02 的機密未指派", createdBy: "sales02", assignee: null);
    }

    [Fact]
    public async Task HiddenUnassigned_ColumnsRequest_OmitsColumnAndItsCards()
    {
        await SeedUnassigned();

        var b = await Board("sales01", HiddenUnassigned);

        b.Columns.Should().NotContain(c => c.Key == TaskBoardConfig.Unassigned);
        WireText(b).Should().NotContain("sales01 的未指派").And.NotContain("sales02 的機密未指派");
    }

    [Fact]
    public async Task HiddenUnassigned_DirectPageRequest_NeverLeaksTasksTheCallerCannotSee()
    {
        await SeedUnassigned();

        var r = await SendAs("sales01", new GetTaskBoardColumnPageRequest(TaskBoardConfig.Unassigned, null, J(HiddenUnassigned), 0));

        r.IsSuccess.Should().BeTrue(Msg(r));
        var page = (TaskBoardPageResponse)r.Data!;
        page.Cards.Select(c => c.Title).Should().Equal("sales01 的未指派");
        page.Total.Should().Be(1, "總數也不能把看不到的任務算進去");
        WireText(page).Should().NotContain("機密");
    }

    [Theory]
    [InlineData("""{"version":1,"groupBy":"assignee","scope":"visible","columns":{"hidden":["user:sales02"]}}""")]
    [InlineData("""{"version":1,"groupBy":"assignee","scope":"visible","columns":{"hidden":["done"]}}""")]
    [InlineData("""{"version":1,"groupBy":"status","scope":"visible","columns":{"hidden":["unassigned"]}}""")]
    public async Task HiddenColumn_NotAFixedColumnOfThisBoard_Rejected400_ForEveryEntryPoint(string config)
    {
        await SeedUnassigned();

        var columns = await SendAs("admin", new GetTaskBoardColumnsRequest(null, J(config)));
        var page = await SendAs("admin", new GetTaskBoardColumnPageRequest(TaskBoardConfig.Unassigned, null, J(config), 0));
        var save = await SendAs("admin", new CreateTaskViewRequest("board", "藏人的看板", J(config)));

        foreach (var r in new[] { columns, page, save })
        {
            r.IsSuccess.Should().BeFalse();
            r.IsCreated.Should().BeFalse();
            r.Errors.Should().ContainKey("config", Msg(r));
        }
        (await ListViews("admin")).Mine.Should().BeEmpty("被拒的設定不能留下存檔");
    }

    [Fact]
    public async Task Move_ToUnassigned_OnTaskTheCallerCannotSee_404_NothingChanges()
    {
        var hidden = await SeedTask("別人的任務", createdBy: "sales02", assignee: "sales02");

        var r = await SendAs("sales01", new MoveTaskRequest(hidden.Id, "assignee", TaskBoardConfig.Unassigned, null, TaskVersion.Of(await Load(hidden.Id))));

        r.IsNotFound.Should().BeTrue(Msg(r));
        (await Load(hidden.Id)).AssigneeUserId.Should().Be("sales02");
    }

    [Fact]
    public async Task Move_ToUnassigned_ByNonOwnerWithoutReassignRight_403_NothingChanges()
    {
        var task = await SeedTask("參與者看得到的", createdBy: "sales02", assignee: "sales02", mentioned: ["sales01"]);

        var r = await SendAs("sales01", new MoveTaskRequest(task.Id, "assignee", TaskBoardConfig.Unassigned, null, TaskVersion.Of(await Load(task.Id))));

        r.IsForbidden.Should().BeTrue(Msg(r));
        (await Load(task.Id)).AssigneeUserId.Should().Be("sales02");
    }
}
