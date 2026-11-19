using DynamicModules.Application;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.AccountService.SystemPrivileges;
using DynamicModules.ApplicationTests.Persistence;
using DynamicModules.Domain.Authorization;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Persistence;
using DynamicModules.Persistence.Seed;
using DynamicModules.TestSupport;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.PermissionAxes;

/// <summary>
/// 權限軸 resolver 與持有者查詢（權限軸設計 §7.1、§9）：PAX-06（fail closed：不存在、停用、停用的管理員；回傳型別不是 ServiceResult 丟例外）、
/// PAX-25（IsPermissionAdminAsync、DesignerActionsAsync、SystemPrivilegeHolders.SnapshotAsync 對 fixture 全員，兩種資料庫相同）。
/// fixture 照 §18 的 persona（只建人與系統權限列，不建模組），另加 mix：G-perm 成員＋個人 DYN00000 新增（群組與個人合併）。
/// </summary>
[Trait("Category", "Postgres")]
public sealed class PermissionAxesGuardTests : IAsyncLifetime
{
    private TestDb _db = null!;
    private ServiceProvider _sp = null!;
    private readonly FakeUser _user = new();

    private sealed class FakeUser : ICurrentUser { public string UserId { get; set; } = "admin"; }

    private sealed class FakeTime : IBusinessTime
    {
        public DateTime Now => new(2026, 11, 20, 9, 0, 0);
        public DateOnly Today => DateOnly.FromDateTime(Now);
    }

    public async Task InitializeAsync()
    {
        _db = await TestDb.CreateAsync();
        await SeedData.EnsureSeededAsync(_db.Context);
        await SeedPeopleAsync();
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddPersistenceRepositories();
        TestDb.AddProviderServices(services);
        services.AddSingleton(_db.Context);   // 執行個體註冊：scope 結束時容器不會把共用的 DbContext 釋放掉
        services.AddScoped<IWorkSavepoints, EfWorkSavepoints>();
        services.AddSingleton<ICurrentUser>(_user);
        services.AddSingleton<IBusinessTime, FakeTime>();
        services.AddTransient<IRequestHandler<NotServiceResultRequest, int>, NotServiceResultHandler>();
        _sp = services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await _sp.DisposeAsync();
        await _db.DisposeAsync();
    }

    private static PermissionGroup Group(string key) => new() { Key = key, NameI18n = new I18nText([KeyValuePair.Create("zh-TW", key)]) };

    private async Task SeedPeopleAsync()
    {
        var db = _db.Context;
        db.Set<AppUser>().AddRange(
            new AppUser { UserId = "wang", DisplayName = "小王" }, new AppUser { UserId = "li", DisplayName = "小李" },
            new AppUser { UserId = "chen", DisplayName = "小陳" }, new AppUser { UserId = "zhang", DisplayName = "小張" },
            new AppUser { UserId = "zhou", DisplayName = "小周", IsActive = false },
            new AppUser { UserId = "lin", DisplayName = "老林", IsSystemAdmin = true, IsActive = false },
            new AppUser { UserId = "mix", DisplayName = "混合" });
        var (design, perm, both) = (Group("pax-g-design"), Group("pax-g-perm"), Group("pax-g-both"));
        db.Set<PermissionGroup>().AddRange(design, perm, both);
        await db.SaveChangesAsync();
        db.Set<UserGroupMapping>().AddRange(
            new UserGroupMapping { GroupId = design.Id, UserId = "wang" }, new UserGroupMapping { GroupId = perm.Id, UserId = "li" },
            new UserGroupMapping { GroupId = both.Id, UserId = "chen" }, new UserGroupMapping { GroupId = both.Id, UserId = "zhou" },
            new UserGroupMapping { GroupId = both.Id, UserId = "lin" }, new UserGroupMapping { GroupId = perm.Id, UserId = "mix" });
        db.Set<GroupProgramPrivilege>().AddRange(
            new GroupProgramPrivilege { GroupId = design.Id, ProgramCode = SystemProgramCodes.Designer, CanRead = true, CanAdd = true, CanEdit = true, CanDelete = true },
            new GroupProgramPrivilege { GroupId = perm.Id, ProgramCode = SystemProgramCodes.PermissionAdmin, CanEdit = true },
            new GroupProgramPrivilege { GroupId = both.Id, ProgramCode = SystemProgramCodes.Designer, CanRead = true, CanAdd = true, CanEdit = true },
            new GroupProgramPrivilege { GroupId = both.Id, ProgramCode = SystemProgramCodes.PermissionAdmin, CanEdit = true });
        db.Set<UserProgramPrivilege>().AddRange(
            new UserProgramPrivilege { UserId = "zhang", ProgramCode = SystemProgramCodes.Designer, CanRead = true, CanEdit = true },
            new UserProgramPrivilege { UserId = "mix", ProgramCode = SystemProgramCodes.Designer, CanAdd = true });
        await db.SaveForTestsAsync();
    }

    private async Task<(bool PermissionAdmin, string Designer)> As(string userId)
    {
        _user.UserId = userId;
        using var scope = _sp.CreateScope();
        var guard = scope.ServiceProvider.GetRequiredService<IDynamicAccessGuard>();
        var actions = await guard.DesignerActionsAsync(CancellationToken.None);
        var names = new[] { (PermissionAction.Read, "R"), (PermissionAction.Add, "A"), (PermissionAction.Edit, "E"), (PermissionAction.Delete, "D") }
            .Where(x => actions.Contains(x.Item1)).Select(x => x.Item2);
        return (await guard.IsPermissionAdminAsync(CancellationToken.None), string.Concat(names));
    }

    [Theory]
    [InlineData("nobody")]
    [InlineData("zhou")]
    [InlineData("lin")]
    public async Task PAX06_MissingOrInactive_IsNeitherPermissionAdminNorDesigner(string userId)
    {
        (await As(userId)).Should().Be((false, ""), "停用判斷在管理員捷徑之前");
    }

    /// <summary>測試用：宣告權限設定但回傳型別不是 ServiceResult（不進正式組件）。</summary>
    public sealed record NotServiceResultRequest : IRequest<int>, IPermissionAdminRequest;

    public sealed class NotServiceResultHandler : IRequestHandler<NotServiceResultRequest, int>
    {
        public Task<int> Handle(NotServiceResultRequest request, CancellationToken ct) => Task.FromResult(1);
    }

    [Fact]
    public async Task PAX06_PermissionAdminRequest_WithNonServiceResult_Throws()
    {
        _user.UserId = "admin";
        var act = () => _sp.GetRequiredService<IMediator>().Send(new NotServiceResultRequest());
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task PAX25_ResolverAndHolders_AgreeForEveryone()
    {
        var expected = new Dictionary<string, (bool, string)>
        {
            ["admin"] = (true, "RAED"), ["wang"] = (false, "RAED"), ["li"] = (true, "R"), ["chen"] = (true, "RAE"),
            ["zhang"] = (false, "RE"), ["zhou"] = (false, ""), ["lin"] = (false, ""), ["mix"] = (true, "RA"), ["nobody"] = (false, ""),
        };
        foreach (var (userId, want) in expected) (await As(userId)).Should().Be(want, userId);

        using var scope = _sp.CreateScope();
        var snapshot = await scope.ServiceProvider.GetRequiredService<SystemPrivilegeHolders>().SnapshotAsync(CancellationToken.None);
        snapshot.Holders.ToDictionary(h => h.Key, h => $"{string.Join(",", h.Value.Designer.Names())}{(h.Value.PermissionAdmin ? "+P" : "")}")
            .Should().BeEquivalentTo(new Dictionary<string, string>
            {
                // 快照不看啟用（zhou 照列）、排除系統管理員（admin、lin）、群組與個人合併（mix）、權限設定隱含讀取（li、mix）
                ["wang"] = "read,add,edit,delete", ["li"] = "read+P", ["chen"] = "read,add,edit+P", ["zhang"] = "read,edit",
                ["zhou"] = "read,add,edit+P", ["mix"] = "read,add+P",
            });
        snapshot.Admins.Should().Contain(["admin", "lin"]);
    }
}
