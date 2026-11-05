using DynamicModules.Application.Dynamic.DemoService;
using DynamicModules.Application.Dynamic.DemoService.Commands.GenerateDemoData;
using DynamicModules.Application.Dynamic.ModuleService.Commands.CreateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.ModuleService.Queries.GetModule;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Application.Dynamic.PermissionService.Queries.GetModulePermissions;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Demo;

public class DemoStructureTests : IAsyncLifetime
{
    private DemoTestHost _host = null!;

    public async Task InitializeAsync() => _host = await DemoTestHost.CreateAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static I18nText Text(string zh) => new([KeyValuePair.Create("zh-TW", zh)]);

    private async Task Generate()
    {
        var r = await _host.Send(new GenerateDemoDataRequest(DemoTestHost.Today));
        r.IsSuccess.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => $"{e.Key}:{string.Join(",", e.Value)}")));
    }

    private async Task<ModuleDetailResponse> Module(string key) =>
        (ModuleDetailResponse)(await _host.Send(new GetModuleRequest(key))).Data!;

    private async Task<ModulePermissionsResponse> Permissions(string key) =>
        (ModulePermissionsResponse)(await _host.Send(new GetModulePermissionsRequest(key))).Data!;

    private async Task<Dictionary<string, long>> GroupIds() =>
        await _host.Db.Context.Set<PermissionGroup>().AsNoTracking().ToDictionaryAsync(g => g.Key, g => g.Id);

    private static FieldResponse Field(ModuleDetailResponse m, string label) =>
        m.Blocks.SelectMany(b => b.Fields).Single(f => f.Label.Resolve("zh-TW") == label);

    [Fact]
    public async Task Generate_BuildsAllModulesActive()
    {
        await Generate();

        foreach (var key in DemoCatalog.ActivationOrder)
            (await Module(key)).Status.Should().Be("Active", key);
        var order = await Module("order");
        var issues = Field(order, "品質異常筆數");
        issues.Type.Should().Be(FieldType.Rollup);
        issues.Config.ShowInList.Should().BeTrue();
        issues.Config.Rollup!.Function.Should().Be(RollupFunction.Count);
        issues.Config.Rollup.SourceFieldId.Should().Be(Field(await Module("quality"), "訂單").Id);
    }

    [Fact]
    public async Task Generate_SupplementsPermissions()
    {
        await Generate();
        var groups = await GroupIds();

        var quality = await Module("quality");
        var qp = await Permissions("quality");
        qp.Fields.Should().Contain(new FieldPermissionDto(Field(quality, "責任單位").Id, groups["sales"], FieldAccess.ReadOnly));
        qp.Fields.Should().Contain(new FieldPermissionDto(Field(quality, "不良率").Id, groups["floor"], FieldAccess.Hidden));
        var critical = Field(quality, "嚴重度").Options.Single(o => o.Label.Resolve("zh-TW") == "重大");
        qp.Options.Single(o => o.OptionId == critical.Id).GroupIds.Should().Equal(groups["qa"]);
        qp.Groups.Single(g => g.GroupId == groups["qa"]).Privileges.Add.Should().BeTrue("範本帶的 qa 權限仍在");

        var op = await Permissions("order");
        var qa = op.Groups.Single(g => g.GroupId == groups["qa"]);
        qa.Privileges.Should().Be(new PrivilegesDto(true, false, false, false, false, false, false));
        qa.Scope.Should().Be(DataScope.All);
        op.Groups.Single(g => g.GroupId == groups["sales"]).Scope.Should().Be(DataScope.Own);
        op.Groups.Single(g => g.GroupId == groups["sales"]).Privileges.Pricing.Should().BeTrue();
        op.Groups.Single(g => g.GroupId == groups["sales-manager"]).Privileges.Delete.Should().BeTrue();
        op.Groups.Single(g => g.GroupId == groups["floor"]).Privileges.Read.Should().BeTrue();
    }

    [Fact]
    public async Task Generate_ExistingOrderModule_Conflict_NothingChanged()
    {
        (await _host.Send(new CreateModuleRequest("order", Text("自建訂單"), null, null))).IsSuccess.Should().BeTrue();
        var db = _host.Db.Context;
        var modules = await db.Set<Module>().AsNoTracking().CountAsync();
        var sequences = await db.Set<NumberSequence>().AsNoTracking().CountAsync();
        var privileges = await db.Set<GroupProgramPrivilege>().AsNoTracking().CountAsync();

        var r = await _host.Send(new GenerateDemoDataRequest(DemoTestHost.Today));

        r.IsConflict.Should().BeTrue();
        r.Message.Should().Be("已有同代碼的模組，未產生 Demo 資料");
        ((DemoConflicts)r.Data!).Items.Should().Contain("模組代碼 order 已存在");
        (await db.Set<Module>().AsNoTracking().CountAsync()).Should().Be(modules);
        (await db.Set<NumberSequence>().AsNoTracking().CountAsync()).Should().Be(sequences);
        (await db.Set<GroupProgramPrivilege>().AsNoTracking().CountAsync()).Should().Be(privileges);
    }

    [Fact]
    public async Task Generate_ExistingCustomerExtension_Conflict()
    {
        (await _host.Send(new CreateModuleRequest("cust-x", Text("客戶擴充"), null, null, ModuleKind.Extension, "sales.customer")))
            .IsSuccess.Should().BeTrue();

        var r = await _host.Send(new GenerateDemoDataRequest(DemoTestHost.Today));

        r.IsConflict.Should().BeTrue();
        ((DemoConflicts)r.Data!).Items.Should().Contain("既有實體 sales.customer 已有擴充模組");
        (await _host.Db.Context.Set<Module>().AsNoTracking().AnyAsync(m => m.Key == "order")).Should().BeFalse();
    }

    [Fact]
    public async Task Preflight_AllPresent_AfterGenerate()
    {
        var preflight = _host.Services.GetRequiredService<DemoPreflight>();
        (await preflight.AllPresentAsync(CancellationToken.None)).Should().BeFalse();
        (await preflight.ConflictsAsync(CancellationToken.None)).Should().BeEmpty();

        await Generate();

        (await preflight.ConflictsAsync(CancellationToken.None)).Count.Should().BeGreaterThanOrEqualTo(8);
        (await preflight.AllPresentAsync(CancellationToken.None)).Should().BeTrue();
    }
}
