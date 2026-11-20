using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.OrganizationService;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Organization;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Organization;

public class DepartmentTests : DesignerTestBase
{
    private static DepartmentInput Input(string code, long? parentId = null, string? manager = null, bool active = true, int sort = 0) =>
        new(code, Text(code), parentId, manager, sort, active);

    private async Task<long> Create(string code, long? parentId = null, string? manager = null, bool active = true)
    {
        var r = await Send(new CreateDepartmentRequest(Input(code, parentId, manager, active)));
        r.IsCreated.Should().BeTrue(string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        return ((DepartmentResponse)r.Data!).Id;
    }

    private async Task<List<DepartmentResponse>> List() =>
        (List<DepartmentResponse>)(await Send(new ListDepartmentsRequest())).Data!;

    [Fact]
    public async Task Create_ThreeLevels_ListReturnsTree()
    {
        var a = await Create("hq", manager: "manager01");
        var b = await Create("sales", a, "manager01");
        var c = await Create("sales-1", b, "sales02");
        (await Send(new SetUserDepartmentRequest("sales01", c))).IsSuccess.Should().BeTrue();

        var list = await List();

        list.Select(d => (d.Code, d.ParentId)).Should().BeEquivalentTo(new[] { ("hq", (long?)null), ("sales", a), ("sales-1", b) });
        list.Single(d => d.Id == c).MemberUserIds.Should().Equal("sales01");
        list.Single(d => d.Id == c).ManagerUserId.Should().Be("sales02");
        list.Single(d => d.Id == a).Name.Resolve("zh-TW").Should().Be("hq");
    }

    [Fact]
    public async Task Code_InvalidOrDuplicate_400()
    {
        foreach (var bad in new[] { "", "Sales", "1abc", "a_b", new string('a', 41) })
        {
            var r = await Send(new CreateDepartmentRequest(Input(bad)));
            Err(r, "code").Should().Be(OrganizationMessages.CodeInvalid, bad);
        }
        await Create("sales");

        var dup = await Send(new CreateDepartmentRequest(Input("sales")));

        Err(dup, "code").Should().Be(OrganizationMessages.CodeExists);
        var other = await Create("ops");
        Err(await Send(new UpdateDepartmentRequest(other, Input("sales"))), "code").Should().Be(OrganizationMessages.CodeExists);
        (await Send(new UpdateDepartmentRequest(other, Input("ops", sort: 3)))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task MoveUnderOwnDescendant_400_Cycle()
    {
        var a = await Create("a");
        var b = await Create("b", a);
        var c = await Create("c", b);

        Err(await Send(new UpdateDepartmentRequest(a, Input("a", c))), "parentId").Should().Be(OrganizationMessages.Cycle);
        Err(await Send(new UpdateDepartmentRequest(a, Input("a", a))), "parentId").Should().Be(OrganizationMessages.Cycle);
        (await List()).Single(d => d.Id == a).ParentId.Should().BeNull();
    }

    [Fact]
    public async Task Depth11_400()
    {
        long? parent = null;
        for (var i = 1; i <= 10; i++) parent = await Create($"d{i}", parent);

        Err(await Send(new CreateDepartmentRequest(Input("d11", parent))), "parentId").Should().Be("部門最多 10 層");

        // 搬移整棵子樹也算深度：x→y 兩層搬到第 9 層底下就是 11 層
        var x = await Create("x");
        await Create("y", x);
        var ninth = (await List()).Single(d => d.Code == "d9").Id;
        Err(await Send(new UpdateDepartmentRequest(x, Input("x", ninth))), "parentId").Should().Be("部門最多 10 層");
    }

    private async Task SetMaxDepth(int value) =>
        (await Send(new UpdateSystemPolicyRequest(PolicyCatalog.OrganizationMaxDepth.Code, System.Text.Json.JsonSerializer.SerializeToElement(value))))
            .IsSuccess.Should().BeTrue();

    [Fact]
    public async Task Depth_UsesPolicy()
    {
        await SetMaxDepth(2);
        var a = await Create("a");
        var b = await Create("b", a);

        Err(await Send(new CreateDepartmentRequest(Input("c", b))), "parentId").Should().Be("部門最多 2 層");
    }

    [Fact]
    public async Task Depth_Lowered_RenameDeepDepartmentAllowed()
    {
        var a = await Create("a");
        var b = await Create("b", a);
        var c = await Create("c", b);
        await SetMaxDepth(2);

        // 已超過上限的第 3 層部門：上級沒變，照常可改其他欄位
        (await Send(new UpdateDepartmentRequest(c, Input("c", b, sort: 5)))).IsSuccess.Should().BeTrue();

        // 把一個第 1 層部門移到第 2 層底下（變第 3 層）→ 擋
        var x = await Create("x");
        Err(await Send(new UpdateDepartmentRequest(x, Input("x", b))), "parentId").Should().Be("部門最多 2 層");
    }

    [Fact]
    public async Task Depth_MoveToRoot_StillChecksSubtreeHeight()
    {
        var a = await Create("a");
        var b = await Create("b", a);
        var c = await Create("c", b);
        await Create("d", c);
        await SetMaxDepth(2);

        // 第 2 層的 b 移到根：上級改了，子樹 b→c→d 有 3 層，超過上限 → 擋，訊息帶上限
        Err(await Send(new UpdateDepartmentRequest(b, Input("b", null))), "parentId").Should().Be("部門最多 2 層");
        // 子樹只有 2 層的 c 移到根 → 可以
        (await Send(new UpdateDepartmentRequest(c, Input("c", null)))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Cycle_StillCheckedWhenParentUnchanged()
    {
        // 上級沒變卻已形成循環的資料無法經 API 構造；改測拆段後「改上級成自己的子部門」仍回循環錯誤，且不被層數條件吃掉
        await SetMaxDepth(1);
        var a = await Create("a");
        await SetMaxDepth(10);
        var b = await Create("b", a);
        await SetMaxDepth(1);

        Err(await Send(new UpdateDepartmentRequest(a, Input("a", b))), "parentId").Should().Be(OrganizationMessages.Cycle);
    }

    [Fact]
    public async Task Parent_NotFound_400()
    {
        Err(await Send(new CreateDepartmentRequest(Input("a", 9999))), "parentId").Should().Be(OrganizationMessages.NotFound);
    }

    [Fact]
    public async Task Delete_WithChildOrMember_409()
    {
        var a = await Create("a");
        var b = await Create("b", a);

        var r = await Send(new DeleteDepartmentRequest(a));

        r.IsConflict.Should().BeTrue();
        r.Message.Should().Be("部門還有 1 個子部門、0 位成員，請先搬移");
        (await Send(new SetUserDepartmentRequest("sales01", b))).IsSuccess.Should().BeTrue();
        (await Send(new DeleteDepartmentRequest(b))).Message.Should().Be(OrganizationMessages.HasDependents(0, 1));
    }

    [Fact]
    public async Task Delete_Empty_200()
    {
        var a = await Create("a");

        (await Send(new DeleteDepartmentRequest(a))).IsSuccess.Should().BeTrue();

        (await List()).Should().BeEmpty();
        (await Send(new DeleteDepartmentRequest(a))).IsNotFound.Should().BeTrue();
    }

    [Fact]
    public async Task AssignUser_ToInactiveDepartment_400()
    {
        var off = await Create("off", active: false);

        var r = await Send(new SetUserDepartmentRequest("sales01", off));

        Err(r, "departmentId").Should().Be(OrganizationMessages.InactiveDepartment);
        (await Db.Context.Set<UserDepartment>().AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task UserDepartment_MoveAndClear()
    {
        var a = await Create("a");
        var b = await Create("b");
        await Send(new SetUserDepartmentRequest("sales01", a));
        await Send(new SetUserDepartmentRequest("sales01", b));
        (await List()).Single(d => d.Id == b).MemberUserIds.Should().Equal("sales01");
        (await List()).Single(d => d.Id == a).MemberUserIds.Should().BeEmpty();

        (await Send(new SetUserDepartmentRequest("sales01", null))).IsSuccess.Should().BeTrue();

        (await List()).SelectMany(d => d.MemberUserIds).Should().BeEmpty();
        (await Send(new SetUserDepartmentRequest("nobody", a))).IsNotFound.Should().BeTrue();
        Err(await Send(new SetUserDepartmentRequest("sales01", 9999)), "departmentId").Should().Be(OrganizationMessages.NotFound);
    }

    [Fact]
    public async Task ManagerInactiveUser_400()
    {
        Db.Context.Set<AppUser>().Single(u => u.UserId == "sales02").IsActive = false;
        await Db.Context.SaveChangesAsync();

        Err(await Send(new CreateDepartmentRequest(Input("a", manager: "sales02"))), "managerUserId")
            .Should().Be(OrganizationMessages.ManagerInvalid);
        Err(await Send(new CreateDepartmentRequest(Input("b", manager: "nobody"))), "managerUserId")
            .Should().Be(OrganizationMessages.ManagerInvalid);
    }

    private async Task Deactivate(params string[] userIds)
    {
        foreach (var id in userIds) Db.Context.Set<AppUser>().Single(u => u.UserId == id).IsActive = false;
        await Db.Context.SaveChangesAsync();
    }

    [Fact]
    public async Task ManagerDeactivatedLater_UnchangedManager_RenameAndMoveStillWork()
    {
        var hq = await Create("hq");
        var other = await Create("other");
        var sales = await Create("sales", hq, "manager01");
        await Deactivate("manager01");

        (await Send(new UpdateDepartmentRequest(sales, new("sales", Text("renamed"), hq, "manager01", 0, true)))).IsSuccess.Should().BeTrue();
        (await Send(new UpdateDepartmentRequest(sales, new("sales", Text("renamed"), other, "manager01", 0, true)))).IsSuccess.Should().BeTrue();

        var d = (await List()).Single(x => x.Id == sales);
        d.ParentId.Should().Be(other);
        d.ManagerUserId.Should().Be("manager01");
    }

    [Fact]
    public async Task ManagerChangedToAnotherInactiveUser_400()
    {
        var sales = await Create("sales", manager: "manager01");
        await Deactivate("manager01", "sales02");

        Err(await Send(new UpdateDepartmentRequest(sales, Input("sales", manager: "sales02"))), "managerUserId")
            .Should().Be(OrganizationMessages.ManagerInvalid);
        (await List()).Single().ManagerUserId.Should().Be("manager01");
    }

    [Fact]
    public async Task ManagerBlankString_TreatedAsVacant_AndPaddedIdIsTrimmed()
    {
        var a = await Create("a", manager: "   ");
        var b = await Create("b", manager: " sales02 ");

        var list = await List();

        list.Single(d => d.Id == a).ManagerUserId.Should().BeNull();
        list.Single(d => d.Id == b).ManagerUserId.Should().Be("sales02");
        (await Send(new UpdateDepartmentRequest(b, Input("b", manager: "  ")))).IsSuccess.Should().BeTrue();
        (await List()).Single(d => d.Id == b).ManagerUserId.Should().BeNull();
    }

    [Fact]
    public async Task Name_MissingZhTw_400()
    {
        var r = await Send(new CreateDepartmentRequest(new DepartmentInput("a", new(), null, null, 0, true)));

        r.Errors.Should().ContainKey("name");
    }

    [Fact]
    public async Task NonAdmin_403()
    {
        var a = await Create("a");
        As("sales01");

        (await Send(new CreateDepartmentRequest(Input("b")))).IsForbidden.Should().BeTrue();
        (await Send(new UpdateDepartmentRequest(a, Input("a")))).IsForbidden.Should().BeTrue();
        (await Send(new DeleteDepartmentRequest(a))).IsForbidden.Should().BeTrue();
        (await Send(new SetUserDepartmentRequest("sales01", a))).IsForbidden.Should().BeTrue();
        (await Send(new ListDepartmentsRequest())).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task SnapshotLoader_ReflectsDepartmentsMembersAndActiveUsers()
    {
        var a = await Create("a", manager: "manager01");
        var b = await Create("b", a, "sales02", active: false);
        await Send(new SetUserDepartmentRequest("sales01", a));
        Db.Context.Set<AppUser>().Single(u => u.UserId == "manager01").IsActive = false;
        await Db.Context.SaveChangesAsync();

        var org = await Svc<OrgSnapshotLoader>().LoadAsync(default);

        org.Departments[b].Should().Be(new OrgDepartment(b, a, "sales02", false));
        org.UserDepartment["sales01"].Should().Be(a);
        org.ActiveUsers.Should().Contain("sales01").And.NotContain("manager01");
        ManagerChain.Resolve(org, "sales01", 1).Should().BeNull();
    }
}
