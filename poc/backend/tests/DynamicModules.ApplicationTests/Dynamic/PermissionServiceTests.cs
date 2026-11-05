using System.Text.Json;
using DynamicModules.Application;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddField;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddOption;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Commands.CreateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.ModuleService.Queries.GetModule;
using DynamicModules.Application.Dynamic.PermissionService.Commands.SaveModulePermissions;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Application.Dynamic.PermissionService.Queries.GetGroups;
using DynamicModules.Application.Dynamic.PermissionService.Queries.GetMe;
using DynamicModules.Application.Dynamic.PermissionService.Queries.GetModulePermissions;
using DynamicModules.Application.Dynamic.PermissionService.Queries.GetUsers;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetPagedRecords;
using DynamicModules.ApplicationTests.Persistence;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Persistence;
using DynamicModules.Persistence.Seed;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic;

public class PermissionServiceTests : IAsyncLifetime
{
    private SqliteTestDb _db = null!;
    private ServiceProvider _sp = null!;
    private readonly FakeUser _user = new();

    private sealed class FakeUser : ICurrentUser { public string UserId { get; set; } = "admin"; }

    private sealed class FakeTime : IBusinessTime
    {
        public DateTime Now => new(2026, 11, 3, 9, 0, 0);
        public DateOnly Today => DateOnly.FromDateTime(Now);
    }

    public async Task InitializeAsync()
    {
        _db = await SqliteTestDb.CreateAsync();
        await SeedData.EnsureSeededAsync(_db.Context);
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddPersistenceRepositories();
        services.AddScoped(_ => _db.Context);
        services.AddScoped<ITransactionManager, EfTransactionManager>();
        services.AddSingleton<ICurrentUser>(_user);
        services.AddSingleton<IBusinessTime, FakeTime>();
        _sp = services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await _sp.DisposeAsync();
        await _db.DisposeAsync();
    }

    private Task<TRes> Send<TRes>(IRequest<TRes> request) => _sp.GetRequiredService<IMediator>().Send(request);

    private static I18nText Text(string zh) => new([KeyValuePair.Create("zh-TW", zh)]);

    private static PrivilegesDto Priv(bool read = false, bool add = false, bool edit = false, bool delete = false,
        bool printExport = false, bool pricing = false, bool approve = false) =>
        new(read, add, edit, delete, printExport, pricing, approve);

    private static ModulePermissionsDto Body(
        GroupPermissionDto[]? groups = null, UserPermissionDto[]? users = null,
        FieldPermissionDto[]? fields = null, OptionScopeDto[]? options = null) =>
        new(groups?.ToList() ?? [], users?.ToList() ?? [], fields?.ToList() ?? [], options?.ToList() ?? []);

    private sealed record Setup(ModuleDetailResponse Module, FieldResponse Title, FieldResponse Memo, long OptionId, long SalesId, long ManagerId);

    private async Task<Setup> Arrange(string key = "order")
    {
        _user.UserId = "admin";
        await Send(new CreateModuleRequest(key, Text("訂單"), null, null));
        var m = (ModuleDetailResponse)(await Send(new GetModuleRequest(key))).Data!;
        var block = m.Blocks[0].Id;
        var memo = (FieldResponse)(await Send(new AddFieldRequest(key, block, FieldType.Text, Text("備註"), new FieldConfig()))).Data!;
        var pick = (FieldResponse)(await Send(new AddFieldRequest(key, block, FieldType.Enum, Text("等級"), new FieldConfig()))).Data!;
        var opt = (OptionResponse)(await Send(new AddOptionRequest(pick.Id, Text("A"), null))).Data!;
        await Send(new ActivateModuleRequest(key));
        m = (ModuleDetailResponse)(await Send(new GetModuleRequest(key))).Data!;
        var title = m.Blocks[0].Fields.Single(f => f.Id == m.TitleFieldId);
        var groups = await _db.Context.Set<PermissionGroup>().ToListAsync();
        return new Setup(m, title, memo, opt.Id, groups.Single(g => g.Key == "sales").Id, groups.Single(g => g.Key == "sales-manager").Id);
    }

    private Task<ServiceResult> Save(string key, ModulePermissionsDto body) => Send(new SaveModulePermissionsRequest(key, body));

    private async Task<ModulePermissionsResponse> Get(string key) =>
        (ModulePermissionsResponse)(await Send(new GetModulePermissionsRequest(key))).Data!;

    private static string Json(object o) => JsonSerializer.Serialize(o);

    [Fact]
    public async Task SavePermissions_RoundTrips()
    {
        var s = await Arrange();
        var other = await Arrange("plan");
        var planFieldId = other.Memo.Id;
        var planPayload = Body(
            groups: [new(other.SalesId, Priv(read: true), DataScope.Group)],
            users: [new("floor01", Priv(read: true, edit: true))],
            fields: [new(planFieldId, other.SalesId, FieldAccess.ReadOnly)],
            options: [new(other.OptionId, [other.ManagerId])]);
        await Save("plan", planPayload);
        var planBefore = Json(await Get("plan"));
        var body = Body(
            groups: [new(s.SalesId, Priv(read: true, add: true, edit: true), DataScope.Own),
                     new(s.ManagerId, Priv(read: true, pricing: true), null)],
            users: [new("floor01", Priv(read: true))],
            fields: [new(s.Memo.Id, s.SalesId, FieldAccess.Hidden)],
            options: [new(s.OptionId, [s.ManagerId])]);

        var put = await Save("order", body);
        put.IsSuccess.Should().BeTrue(string.Join(";", put.Errors.Select(e => $"{e.Key}:{string.Join(",", e.Value)}")));
        var got = await Get("order");

        got.Groups.Should().HaveCount(6, "列出所有群組");
        var sales = got.Groups.Single(g => g.GroupId == s.SalesId);
        sales.Privileges.Should().Be(Priv(read: true, add: true, edit: true));
        sales.Scope.Should().Be(DataScope.Own);
        got.Groups.Single(g => g.GroupId == s.ManagerId).Privileges.Pricing.Should().BeTrue();
        got.Groups.Single(g => g.GroupId == s.ManagerId).Scope.Should().BeNull();
        var none = Priv();
        got.Groups.Where(g => g.GroupId != s.SalesId && g.GroupId != s.ManagerId)
            .Should().OnlyContain(g => g.Privileges == none && g.Scope == null);
        got.Users.Should().ContainSingle().Which.UserId.Should().Be("floor01");
        got.Fields.Should().ContainSingle().Which.Should().Be(new FieldPermissionDto(s.Memo.Id, s.SalesId, FieldAccess.Hidden));
        got.Options.Should().ContainSingle().Which.GroupIds.Should().Equal(s.ManagerId);
        Json(put.Data!).Should().Be(Json(got));

        Json(await Get("plan")).Should().Be(planBefore, "order 的 PUT 不得動到 plan 的任何一種權限列");
        var planAfter = await Get("plan");
        planAfter.Users.Should().Equal(planPayload.Users);
        planAfter.Fields.Should().Equal(planPayload.Fields);
        planAfter.Options.Should().ContainSingle().Which.GroupIds.Should().Equal(other.ManagerId);
        planAfter.Groups.Single(g => g.GroupId == other.SalesId).Scope.Should().Be(DataScope.Group);
    }

    [Fact]
    public async Task SavePermissions_ReplacesPreviousRows()
    {
        var s = await Arrange();
        await Save("order", Body(
            groups: [new(s.SalesId, Priv(read: true), DataScope.Own)],
            users: [new("floor01", Priv(read: true))],
            fields: [new(s.Memo.Id, s.SalesId, FieldAccess.ReadOnly)],
            options: [new(s.OptionId, [s.SalesId])]));

        var put = await Save("order", Body(groups: [new(s.SalesId, Priv(read: true), null)]));

        put.IsSuccess.Should().BeTrue();
        var got = await Get("order");
        got.Fields.Should().BeEmpty();
        got.Users.Should().BeEmpty();
        got.Options.Should().BeEmpty();
        got.Groups.Single(g => g.GroupId == s.SalesId).Scope.Should().BeNull();
        (await _db.Context.Set<ModuleDataScope>().CountAsync(x => x.ModuleId == s.Module.Id)).Should().Be(0);
    }

    [Fact]
    public async Task SavePermissions_TitleFieldHidden_Rejected()
    {
        var s = await Arrange();

        var r = await Save("order", Body(fields: [new(s.Title.Id, s.SalesId, FieldAccess.Hidden)]));

        r.Errors["fields[0].access"].Should().Equal("標題欄位不能設為隱藏");
    }

    [Fact]
    public async Task SavePermissions_ForeignFieldOrOption_Rejected()
    {
        var s = await Arrange();
        var other = await Arrange("plan");

        var r = await Save("order", Body(
            fields: [new(s.Memo.Id, s.SalesId, FieldAccess.Hidden), new(other.Memo.Id, s.SalesId, FieldAccess.Hidden)],
            options: [new(other.OptionId, [s.SalesId])]));

        r.Errors["fields[1].fieldId"].Should().Equal("欄位不屬於此模組");
        r.Errors["options[0].optionId"].Should().Equal("選項不屬於此模組");
        r.Errors.Keys.Should().NotContain("fields[0].fieldId");
    }

    [Fact]
    public async Task SavePermissions_UnknownGroupOrUser_Rejected()
    {
        var s = await Arrange();

        var r = await Save("order", Body(
            groups: [new(9999, Priv(read: true), null)],
            users: [new("nobody", Priv(read: true))],
            fields: [new(s.Memo.Id, 9999, FieldAccess.Hidden)]));

        r.Errors["groups[0].groupId"].Should().Equal("群組不存在");
        r.Errors["users[0].userId"].Should().Equal("使用者不存在");
        r.Errors["fields[0].groupId"].Should().Equal("群組不存在");
    }

    [Fact]
    public async Task SavePermissions_Duplicates_Rejected()
    {
        var s = await Arrange();

        var r = await Save("order", Body(
            groups: [new(s.SalesId, Priv(read: true), null), new(s.SalesId, Priv(add: true), null)],
            users: [new("floor01", Priv(read: true)), new("floor01", Priv(add: true))],
            fields: [new(s.Memo.Id, s.SalesId, FieldAccess.Hidden), new(s.Memo.Id, s.SalesId, FieldAccess.ReadOnly)],
            options: [new(s.OptionId, [s.SalesId]), new(s.OptionId, [s.ManagerId])]));

        r.Errors["groups[1].groupId"].Should().Equal("設定重複");
        r.Errors["users[1].userId"].Should().Equal("設定重複");
        r.Errors["fields[1].fieldId"].Should().Equal("設定重複");
        r.Errors["options[1].optionId"].Should().Equal("設定重複");
    }

    [Fact]
    public async Task SavePermissions_TakesEffectImmediately()
    {
        var s = await Arrange();
        await Save("order", Body(groups: [new(s.SalesId, Priv(read: true, add: true), null)]));
        foreach (var u in new[] { "sales01", "sales01", "sales02", "sales02" })
        {
            _user.UserId = u;
            (await Send(new CreateRecordRequest("order",
                new Dictionary<string, JsonElement?> { [s.Title.Key] = JsonSerializer.SerializeToElement("r") })))
                .IsCreated.Should().BeTrue();
        }
        _user.UserId = "sales01";
        var before = (PagedResponse<DynamicRecordResponse>)(await Send(new GetPagedRecordsRequest("order", 1, 50, null, [], [], null))).Data!;
        before.TotalCount.Should().Be(4);

        _user.UserId = "admin";
        (await Save("order", Body(groups: [new(s.SalesId, Priv(read: true, add: true), DataScope.Own)]))).IsSuccess.Should().BeTrue();

        _user.UserId = "sales01";
        var after = (PagedResponse<DynamicRecordResponse>)(await Send(new GetPagedRecordsRequest("order", 1, 50, null, [], [], null))).Data!;
        after.TotalCount.Should().Be(2);
        after.Items.Should().OnlyContain(i => i.OwnerUserId == "sales01");
    }

    [Fact]
    public async Task SavePermissions_RequiresDesignerEdit_AndReadsRequireRead()
    {
        await Arrange();
        _user.UserId = "sales01";

        (await Save("order", Body())).IsForbidden.Should().BeTrue();
        (await Send(new GetModulePermissionsRequest("order"))).IsForbidden.Should().BeTrue();
        (await Send(new GetGroupsRequest())).IsForbidden.Should().BeTrue();
    }

    [Fact]
    public async Task GetGroups_ListsMembers()
    {
        _user.UserId = "admin";

        var groups = (List<GroupResponse>)(await Send(new GetGroupsRequest())).Data!;

        groups.Single(g => g.Key == "sales").Members.Should().Equal("sales01", "sales02");
    }

    [Fact]
    public async Task GetUsers_SortedByUserId()
    {
        var users = (List<UserResponse>)(await Send(new GetUsersRequest())).Data!;

        var ids = users.Select(u => u.UserId).ToList();
        ids.Should().BeInAscendingOrder();
        ids.Should().Contain("admin").And.Contain("sales01");
    }

    [Fact]
    public async Task GetMe_ReturnsGroupsAndDesignerActions()
    {
        _user.UserId = "admin";
        var admin = (MeResponse)(await Send(new GetMeRequest())).Data!;
        admin.IsSystemAdmin.Should().BeTrue();
        admin.Designer.Should().Be(new DesignerActionsResponse(true, true, true, true));
        admin.Groups.Select(g => g.Key).Should().Equal("mis");

        _user.UserId = "sales01";
        var me = (MeResponse)(await Send(new GetMeRequest())).Data!;
        me.UserId.Should().Be("sales01");
        me.IsSystemAdmin.Should().BeFalse();
        me.Groups.Select(g => g.Key).Should().Equal("sales");
        me.Designer.Should().Be(new DesignerActionsResponse(false, false, false, false));
    }
}
