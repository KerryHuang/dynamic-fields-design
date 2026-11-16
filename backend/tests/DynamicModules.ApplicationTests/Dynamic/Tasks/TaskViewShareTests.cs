using DynamicModules.Application.Dynamic.AccountService.Commands.DeleteGroup;
using DynamicModules.Application.Dynamic.OrganizationService;
using DynamicModules.Application.Dynamic.TaskViewService;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

/// <summary>看板分享給使用者、群組、部門（直屬成員，U8）；刪群組與部門時清分享（看板設計 §6.2、§6.3）。</summary>
public class TaskViewShareTests : TaskViewTestBase
{
    private long DepartmentId(string code) => Db.Context.Set<Department>().AsNoTracking().Single(d => d.Code == code).Id;

    private long GroupId(string key) => Db.Context.Set<PermissionGroup>().AsNoTracking().Single(g => g.Key == key).Id;

    [Fact]
    public async Task ShareToUser_Group_Department_EachSees()
    {
        var v = await CreateView("manager01");

        var r = await Share("manager01", v.Id, ("user", "sales02"), ("group", GroupId("floor").ToString()), ("department", DepartmentId("ops").ToString()));

        r.IsSuccess.Should().BeTrue(r.Message + string.Join(";", r.Errors.SelectMany(e => e.Value)));
        foreach (var user in new[] { "sales02", "floor01", "floor02", "ops01", "keeper01" })
            (await SharedIds(user)).Should().Contain(v.Id, user);
        foreach (var user in new[] { "sales01", "qa01", "designer01" })
            (await SharedIds(user)).Should().NotContain(v.Id, user);
        (await SendAs("floor01", new GetTaskViewRequest(v.Id))).IsSuccess.Should().BeTrue();
        var owner = (TaskViewDetail)(await SendAs("manager01", new GetTaskViewRequest(v.Id))).Data!;
        owner.Shares.Select(s => s.Kind).Should().BeEquivalentTo(["user", "group", "department"]);
        owner.Shares.Single(s => s.Kind == "user").DisplayName.Should().Be("業務二");
        (await ListViews("manager01")).Mine.Single(x => x.Id == v.Id).ShareCount.Should().Be(3);
    }

    [Fact]
    public async Task ShareToDepartment_DirectMembersOnly()
    {
        var v = await CreateView("manager01");
        (await Share("manager01", v.Id, ("department", DepartmentId("ops").ToString()))).IsSuccess.Should().BeTrue();

        (await SharedIds("keeper01")).Should().Contain(v.Id);
        // 採購部、生產部是營運部的子部門：不含
        (await SharedIds("buyer01")).Should().NotContain(v.Id);
        (await SharedIds("planner01")).Should().NotContain(v.Id);
    }

    [Fact]
    public async Task UserMovesDepartment_ShareFollows()
    {
        var v = await CreateView("manager01");
        (await Share("manager01", v.Id, ("department", DepartmentId("ops").ToString()))).IsSuccess.Should().BeTrue();
        (await SharedIds("buyer01")).Should().NotContain(v.Id);

        (await Send(new SetUserDepartmentRequest("buyer01", DepartmentId("ops")))).IsSuccess.Should().BeTrue();
        (await Send(new SetUserDepartmentRequest("keeper01", DepartmentId("purchasing")))).IsSuccess.Should().BeTrue();

        (await SharedIds("buyer01")).Should().Contain(v.Id);
        (await SharedIds("keeper01")).Should().NotContain(v.Id);
    }

    [Fact]
    public async Task DeleteGroup_RemovesBoardShares()
    {
        var group = new PermissionGroup { Key = "board-temp", NameI18n = Text("臨時組") };
        Db.Context.Add(group);
        await Db.Context.SaveChangesAsync();
        var v = await CreateView("manager01");
        (await Share("manager01", v.Id, ("group", group.Id.ToString()), ("user", "sales01"))).IsSuccess.Should().BeTrue();

        (await Send(new DeleteGroupRequest(group.Id))).IsSuccess.Should().BeTrue();

        (await Db.Context.Set<TaskViewShare>().AsNoTracking().Where(s => s.ViewId == v.Id).Select(s => s.TargetKind).ToListAsync())
            .Should().Equal("user");
    }

    [Fact]
    public async Task DeleteDepartment_RemovesBoardShares()
    {
        var dept = new Department { Code = "board-temp", NameI18n = Text("臨時部") };
        dept.StampCreated("admin", SeedTime);
        Db.Context.Add(dept);
        await Db.Context.SaveChangesAsync();
        var v = await CreateView("manager01");
        (await Share("manager01", v.Id, ("department", dept.Id.ToString()), ("department", DepartmentId("ops").ToString()))).IsSuccess.Should().BeTrue();

        var r = await Send(new DeleteDepartmentRequest(dept.Id));

        r.IsSuccess.Should().BeTrue(r.Message);
        (await Db.Context.Set<TaskViewShare>().AsNoTracking().Where(s => s.ViewId == v.Id).Select(s => s.TargetId).ToListAsync())
            .Should().Equal(DepartmentId("ops").ToString());
    }

    [Fact]
    public async Task Shares_Over200_400()
    {
        var v = await CreateView("manager01");

        var over = await Share("manager01", v.Id, Enumerable.Range(1, 201).Select(i => ("user", $"u{i}")).ToArray());
        var missing = await Share("manager01", v.Id, ("user", "nobody"));
        var badKind = await Share("manager01", v.Id, ("team", "1"));
        var missingGroup = await Share("manager01", v.Id, ("group", "999999"));

        over.Errors.Should().ContainKey("shares");
        over.Errors["shares"][0].Should().Contain("200");
        missing.Errors.Should().ContainKey("shares");
        badKind.Errors.Should().ContainKey("shares");
        missingGroup.Errors.Should().ContainKey("shares");
        (await Db.Context.Set<TaskViewShare>().AsNoTracking().CountAsync()).Should().Be(0);
        // 重複的對象只算一次
        (await Share("manager01", v.Id, ("user", "sales01"), ("user", "sales01"))).IsSuccess.Should().BeTrue();
        (await Db.Context.Set<TaskViewShare>().AsNoTracking().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ShareTargets_ListsActiveUsersDepartmentsGroups()
    {
        await Db.Context.Set<AppUser>().Where(u => u.UserId == "qa01").ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        await Db.Context.Set<Department>().Where(d => d.Code == "design").ExecuteUpdateAsync(s => s.SetProperty(d => d.Active, false));
        Db.Context.ChangeTracker.Clear();

        var r = await SendAs("sales01", new GetTaskViewShareTargetsRequest());

        r.IsSuccess.Should().BeTrue(r.Message);
        var t = (TaskViewShareTargets)r.Data!;
        t.Users.Select(u => u.Id).Should().Contain(["sales02", "manager01"]).And.NotContain("qa01");
        t.Users.Single(u => u.Id == "sales02").Name.Should().Be("業務二");
        t.Departments.Select(d => d.Id).Should().Contain(DepartmentId("ops")).And.NotContain(DepartmentId("design"));
        t.Groups.Select(g => g.Id).Should().Contain(GroupId("sales"));
        // 停用的使用者仍可留在分享清單裡
        var v = await CreateView("manager01");
        (await Share("manager01", v.Id, ("user", "qa01"))).IsSuccess.Should().BeTrue();
        var detail = (TaskViewDetail)(await SendAs("manager01", new GetTaskViewRequest(v.Id))).Data!;
        detail.Shares.Single().Inactive.Should().BeTrue();
    }

    [Fact]
    public async Task Owner_Deactivated_ViewAndSharesRemain()
    {
        var v = await CreateView("manager01");
        (await Share("manager01", v.Id, ("user", "sales01"))).IsSuccess.Should().BeTrue();

        await Db.Context.Set<AppUser>().Where(u => u.UserId == "manager01").ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        Db.Context.ChangeTracker.Clear();

        var shared = (await ListViews("sales01")).Shared.Single(x => x.Id == v.Id);
        shared.OwnerName.Should().Be("業務主管");
        shared.OwnerActive.Should().BeFalse();
        (await SendAs("sales01", new GetTaskViewRequest(v.Id))).IsSuccess.Should().BeTrue();
        (await SendAs("sales01", new CopyTaskViewRequest(v.Id))).IsCreated.Should().BeTrue();
    }
}
