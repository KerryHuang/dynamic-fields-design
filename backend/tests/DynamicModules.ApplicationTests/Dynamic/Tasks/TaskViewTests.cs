using DynamicModules.Application.Dynamic.TaskViewService;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

/// <summary>看板存檔的建立、讀取、修改、刪除、另存與權限（看板設計 §6.1、§6.2）。</summary>
public class TaskViewTests : TaskViewTestBase
{
    [Fact]
    public async Task Create_Read_Update_Delete_ByOwner()
    {
        var created = await CreateView("manager01", "  業務部任務  ");
        created.Name.Should().Be("業務部任務");
        created.Kind.Should().Be("board");
        created.IsOwner.Should().BeTrue();
        created.CanEdit.Should().BeTrue();
        created.Config.GetProperty("groupBy").GetString().Should().Be("status");

        var read = await SendAs("manager01", new GetTaskViewRequest(created.Id));
        read.IsSuccess.Should().BeTrue();
        (await ListViews("manager01")).Mine.Should().ContainSingle(v => v.Id == created.Id);

        var updated = await SendAs("manager01", new UpdateTaskViewRequest(created.Id, "業務部負載",
            J("""{"version":1,"groupBy":"assignee","scope":"subordinates"}"""), created.RowVersion));
        updated.IsSuccess.Should().BeTrue(updated.Message);
        var detail = (TaskViewDetail)updated.Data!;
        detail.Name.Should().Be("業務部負載");
        detail.Config.GetProperty("scope").GetString().Should().Be("subordinates");
        detail.RowVersion.Should().NotBe(created.RowVersion);

        (await SendAs("manager01", new DeleteTaskViewRequest(created.Id))).IsSuccess.Should().BeTrue();
        (await SendAs("manager01", new GetTaskViewRequest(created.Id))).IsNotFound.Should().BeTrue();
    }

    [Fact]
    public async Task Update_StaleRowVersion_409()
    {
        var v = await CreateView("manager01");
        (await SendAs("manager01", new UpdateTaskViewRequest(v.Id, "第一次", J(StatusConfig), v.RowVersion))).IsSuccess.Should().BeTrue();

        var stale = await SendAs("manager01", new UpdateTaskViewRequest(v.Id, "第二次", J(StatusConfig), v.RowVersion));

        stale.IsConflict.Should().BeTrue();
        stale.Code.Should().Be("stale");
        (await Db.Context.Set<TaskView>().AsNoTracking().SingleAsync(x => x.Id == v.Id)).Name.Should().Be("第一次");
    }

    [Fact]
    public async Task NonOwner_Update403_Delete403()
    {
        var v = await CreateView("manager01");
        (await Share("manager01", v.Id, ("user", "sales01"))).IsSuccess.Should().BeTrue();

        (await SendAs("sales01", new UpdateTaskViewRequest(v.Id, "改名", J(StatusConfig), v.RowVersion))).IsForbidden.Should().BeTrue();
        (await SendAs("sales01", new DeleteTaskViewRequest(v.Id))).IsForbidden.Should().BeTrue();
        (await SendAs("sales01", new SetTaskViewSharesRequest(v.Id, []))).IsForbidden.Should().BeTrue();
        var detail = (TaskViewDetail)(await SendAs("sales01", new GetTaskViewRequest(v.Id))).Data!;
        detail.CanEdit.Should().BeFalse();
        detail.CanDelete.Should().BeFalse();
        detail.IsOwner.Should().BeFalse();
        detail.OwnerName.Should().Be("業務主管");
        // 不是擁有者也看不到 → 404（不洩漏存在）
        (await SendAs("sales02", new GetTaskViewRequest(v.Id))).IsNotFound.Should().BeTrue();
        (await SendAs("sales02", new DeleteTaskViewRequest(v.Id))).IsNotFound.Should().BeTrue();
    }

    [Fact]
    public async Task Admin_CanDeleteShared_CannotUpdate()
    {
        var v = await CreateView("manager01");
        (await Share("manager01", v.Id, ("user", "sales01"))).IsSuccess.Should().BeTrue();

        (await ListViews("admin")).Shared.Should().ContainSingle(x => x.Id == v.Id);
        var detail = (TaskViewDetail)(await SendAs("admin", new GetTaskViewRequest(v.Id))).Data!;
        detail.CanEdit.Should().BeFalse();
        detail.CanDelete.Should().BeTrue();
        (await SendAs("admin", new UpdateTaskViewRequest(v.Id, "管理員改", J(StatusConfig), detail.RowVersion))).IsForbidden.Should().BeTrue();
        (await SendAs("admin", new DeleteTaskViewRequest(v.Id))).IsSuccess.Should().BeTrue();
        (await Db.Context.Set<TaskView>().AsNoTracking().AnyAsync(x => x.Id == v.Id)).Should().BeFalse();
        (await Db.Context.Set<TaskViewShare>().AsNoTracking().AnyAsync(x => x.ViewId == v.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Admin_CannotDeleteUnsharedOfOthers()
    {
        var v = await CreateView("manager01");

        (await ListViews("admin")).Shared.Should().BeEmpty();
        (await SendAs("admin", new GetTaskViewRequest(v.Id))).IsNotFound.Should().BeTrue();
        (await SendAs("admin", new DeleteTaskViewRequest(v.Id))).IsNotFound.Should().BeTrue();
    }

    [Fact]
    public async Task Copy_ByShared_NewOwner_NoShares()
    {
        var v = await CreateView("manager01", new string('名', 100), """{"version":1,"groupBy":"dueBucket","scope":"visible"}""");
        (await Share("manager01", v.Id, ("user", "sales01"), ("group", GroupId("floor").ToString()))).IsSuccess.Should().BeTrue();

        var r = await SendAs("sales01", new CopyTaskViewRequest(v.Id));

        r.IsCreated.Should().BeTrue(r.Message);
        var copy = (TaskViewDetail)r.Data!;
        copy.OwnerUserId.Should().Be("sales01");
        copy.IsOwner.Should().BeTrue();
        copy.Shares.Should().BeEmpty();
        copy.Name.Should().HaveLength(100).And.EndWith("（副本）");
        copy.Config.GetProperty("groupBy").GetString().Should().Be("dueBucket");
        (await ListViews("sales01")).Mine.Should().ContainSingle(x => x.Id == copy.Id);
        (await SendAs("sales02", new CopyTaskViewRequest(v.Id))).IsNotFound.Should().BeTrue();
    }

    [Fact]
    public async Task Name_BlankOrOver100_400()
    {
        (await SendAs("manager01", new CreateTaskViewRequest("board", "   ", J(StatusConfig)))).Errors.Should().ContainKey("name");
        (await SendAs("manager01", new CreateTaskViewRequest("board", new string('名', 101), J(StatusConfig)))).Errors.Should().ContainKey("name");
        (await SendAs("manager01", new CreateTaskViewRequest("board", new string('名', 100), J(StatusConfig)))).IsCreated.Should().BeTrue();
        var v = await CreateView("manager01");
        (await SendAs("manager01", new UpdateTaskViewRequest(v.Id, "", J(StatusConfig), v.RowVersion))).Errors.Should().ContainKey("name");
    }

    [Fact]
    public async Task Kind_Gantt_400_ForNow()
    {
        var r = await SendAs("manager01", new CreateTaskViewRequest("gantt", "甘特", J(StatusConfig)));

        r.Errors.Should().ContainKey("kind");
        (await SendAs("manager01", new GetTaskViewsRequest("gantt"))).Errors.Should().ContainKey("kind");
        (await Db.Context.Set<TaskView>().AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Config_UnknownProperty_400()
    {
        var r = await SendAs("manager01", new CreateTaskViewRequest("board", "看板",
            J("""{"version":1,"groupBy":"status","scope":"assigned","colums":{}}""")));

        r.Errors.Should().ContainKey("config");
        r.Errors["config"][0].Should().Contain("colums");
        var v = await CreateView("manager01");
        (await SendAs("manager01", new UpdateTaskViewRequest(v.Id, "看板", J("""{"version":1,"groupBy":"status"}"""), v.RowVersion)))
            .Errors.Should().ContainKey("config");
    }

    private long GroupId(string key) => Db.Context.Set<PermissionGroup>().AsNoTracking().Single(g => g.Key == key).Id;
}
