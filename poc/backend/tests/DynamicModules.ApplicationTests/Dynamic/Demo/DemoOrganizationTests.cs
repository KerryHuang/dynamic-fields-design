using DynamicModules.Application.Dynamic.DemoService;
using DynamicModules.Application.Dynamic.DemoService.Commands.GenerateDemoData;
using DynamicModules.Application.Dynamic.OrganizationService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Organization;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Demo;

/// <summary>Demo 產生器建立的組織（簽核計畫 a1 Task 5 的表，主控裁決 6）。</summary>
[Collection(DemoGeneratedCollection.Name)]
public class DemoOrganizationTests(DemoGeneratedFixture fx)
{
    private async Task<OrgSnapshot> Org()
    {
        using var scope = fx.Host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OrgSnapshotLoader>().LoadAsync(default);
    }

    [Fact]
    public async Task DemoOrg_MatchesTable()
    {
        fx.Result.IsSuccess.Should().BeTrue();
        var db = fx.Host.Db.Context;
        var departments = await db.Set<Department>().AsNoTracking().ToListAsync();
        var members = await db.Set<UserDepartment>().AsNoTracking().ToListAsync();
        var code = departments.ToDictionary(d => d.Id, d => d.Code);

        departments.Select(d => (d.Code, d.NameI18n.Resolve("zh-TW"), d.ParentId is { } p ? code[p] : null, d.ManagerUserId))
            .Should().BeEquivalentTo(new (string, string, string?, string?)[]
            {
                ("exec", "總經理室", null, "manager01"),
                ("sales", "業務部", "exec", "manager01"),
                ("ops", "營運部", "exec", "ops01"),
                ("purchasing", "採購部", "ops", null),
                ("production", "生產部", "ops", "planner01"),
            });
        members.GroupBy(m => code[m.DepartmentId]).ToDictionary(g => g.Key, g => g.Select(m => m.UserId).Order().ToArray())
            .Should().BeEquivalentTo(new Dictionary<string, string[]>
            {
                ["exec"] = ["manager01"],
                ["sales"] = ["sales01", "sales02"],
                ["ops"] = ["keeper01", "ops01"],
                ["purchasing"] = ["buyer01"],
                ["production"] = ["floor01", "floor02", "maint01", "planner01", "qa01"],
            });
        departments.Should().OnlyContain(d => d.Active);
    }

    [Fact]
    public async Task DemoOrg_ManagerResolution()
    {
        var org = await Org();

        ManagerChain.Resolve(org, "sales01", 1).Should().Be("manager01");
        ManagerChain.Resolve(org, "buyer01", 1).Should().Be("ops01");
        ManagerChain.Resolve(org, "buyer01", 2).Should().Be("manager01");
        ManagerChain.Resolve(org, "floor01", 1).Should().Be("planner01");
        ManagerChain.Resolve(org, "floor01", 2).Should().Be("ops01");
    }
}

public class DemoOrganizationSetupTests : DesignerTestBase
{
    [Fact]
    public async Task TestBase_HasNoDepartments()
    {
        (await Db.Context.Set<Department>().AsNoTracking().CountAsync()).Should().Be(0);
        (await Db.Context.Set<UserDepartment>().AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Generate_ExistingDepartmentCode_Conflict()
    {
        await using var host = await DemoTestHost.CreateAsync();
        host.Db.Context.Set<Department>().Add(new Department { Code = "sales", CreatedUserId = "admin" });
        await host.Db.Context.SaveChangesAsync();

        var r = await host.Send(new GenerateDemoDataRequest(DemoTestHost.Today));

        r.IsConflict.Should().BeTrue();
        ((DemoConflicts)r.Data!).Items.Should().Contain("部門 sales 已存在");
    }
}
