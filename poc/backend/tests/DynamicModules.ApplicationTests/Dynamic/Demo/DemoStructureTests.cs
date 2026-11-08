using DynamicModules.Application.Dynamic.AppService;
using DynamicModules.Application.Dynamic.DemoService;
using DynamicModules.Application.Dynamic.DemoService.Commands.GenerateDemoData;
using DynamicModules.Application.Dynamic.ModuleService.Commands.CreateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Application.Dynamic.PermissionService.Queries.GetModulePermissions;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Demo;

/// <summary>產生後的結構用共用的產生結果；衝突與「產生前」的檢查各自建新庫（不產生）。</summary>
[Collection(DemoGeneratedCollection.Name)]
public class DemoStructureTests(DemoGeneratedFixture fx)
{
    private static I18nText Text(string zh) => new([KeyValuePair.Create("zh-TW", zh)]);

    private Task<ModuleDetailResponse> Module(string key) => fx.Module(key);

    private async Task<ModulePermissionsResponse> Permissions(string key) =>
        (ModulePermissionsResponse)(await fx.Host.Send(new GetModulePermissionsRequest(key))).Data!;

    private async Task<Dictionary<string, long>> GroupIds() =>
        await fx.Host.Db.Context.Set<PermissionGroup>().AsNoTracking().ToDictionaryAsync(g => g.Key, g => g.Id);

    private static FieldResponse Field(ModuleDetailResponse m, string label) =>
        m.Blocks.SelectMany(b => b.Fields).Single(f => f.Label.Resolve("zh-TW") == label);

    [Fact]
    public async Task Generate_BuildsAllModulesActive()
    {
        fx.Generated();

        var apps = fx.Host.Services.GetRequiredService<AppCatalog>();
        foreach (var id in DemoCatalog.AppIds)
            foreach (var module in apps.BuiltIns.Single(p => p.Manifest.Id == id).Modules)
                (await Module(module.Key)).Status.Should().Be("Active", module.Key);
        var order = await Module("mes-order");
        var issues = Field(order, "品質異常筆數");
        issues.Type.Should().Be(FieldType.Rollup);
        issues.Config.ShowInList.Should().BeTrue();
        issues.Config.Rollup!.Function.Should().Be(RollupFunction.Count);
        issues.Config.Rollup.SourceFieldId.Should().Be(Field(await Module("mes-quality"), "訂單").Id);
    }

    [Fact]
    public async Task Generate_SupplementsPermissions()
    {
        fx.Generated();
        var groups = await GroupIds();

        var quality = await Module("mes-quality");
        var qp = await Permissions("mes-quality");
        qp.Fields.Should().Contain(new FieldPermissionDto(Field(quality, "責任單位").Id, groups["sales"], FieldAccess.ReadOnly));
        qp.Fields.Should().Contain(new FieldPermissionDto(Field(quality, "不良率").Id, groups["floor"], FieldAccess.Hidden));
        var critical = Field(quality, "嚴重度").Options.Single(o => o.Label.Resolve("zh-TW") == "重大");
        qp.Options.Single(o => o.OptionId == critical.Id).GroupIds.Should().Equal(groups["qa"]);
        qp.Groups.Single(g => g.GroupId == groups["qa"]).Privileges.Add.Should().BeTrue("範本帶的 qa 權限仍在");

        var op = await Permissions("mes-order");
        var qa = op.Groups.Single(g => g.GroupId == groups["qa"]);
        qa.Privileges.Should().Be(new PrivilegesDto(true, false, false, false, false, false, false));
        qa.Scope.Should().Be(DataScope.All);
        op.Groups.Single(g => g.GroupId == groups["sales"]).Scope.Should().Be(DataScope.Own);
        op.Groups.Single(g => g.GroupId == groups["sales"]).Privileges.Pricing.Should().BeTrue();
        op.Groups.Single(g => g.GroupId == groups["sales-manager"]).Privileges.Delete.Should().BeTrue();
        op.Groups.Single(g => g.GroupId == groups["floor"]).Privileges.Read.Should().BeTrue();
    }

    [Fact]
    public async Task Generate_InventoryViewers_OnlyReadItemsAndWarehouses()
    {
        fx.Generated();
        var groups = await GroupIds();
        var readOnly = new PrivilegesDto(true, false, false, false, false, false, false);
        foreach (var key in new[] { "inv-item", "inv-warehouse" })
            (await Permissions(key)).Groups.Single(g => g.GroupId == groups["inv-viewer"]).Privileges.Should().Be(readOnly, key);
        foreach (var key in new[] { "inv-adjust", "inv-move", "inv-receipt", "inv-shipment", "inv-po" })
            (await Permissions(key)).Groups.Where(g => g.GroupId == groups["inv-viewer"]).Should().OnlyContain(g => g.Privileges.IsEmpty, key);

        // 生管不再是倉管組成員（倉管能過帳調整單）
        var members = await fx.Host.Db.Context.Set<UserGroupMapping>().AsNoTracking().Where(m => m.GroupId == groups["warehouse"]).Select(m => m.UserId).ToListAsync();
        members.Should().NotContain("planner01");
    }

    [Fact]
    public async Task Generate_ExistingOrderModule_Conflict_NothingChanged()
    {
        await using var host = await DemoTestHost.CreateAsync();
        (await host.Send(new CreateModuleRequest("mes-order", Text("自建訂單"), null, null))).IsSuccess.Should().BeTrue();
        var db = host.Db.Context;
        var modules = await db.Set<Module>().AsNoTracking().CountAsync();
        var sequences = await db.Set<NumberSequence>().AsNoTracking().CountAsync();
        var privileges = await db.Set<GroupProgramPrivilege>().AsNoTracking().CountAsync();

        var r = await host.Send(new GenerateDemoDataRequest(DemoTestHost.Today));

        r.IsConflict.Should().BeTrue();
        r.Message.Should().Be("已有相同的資料，未產生 Demo 資料");
        ((DemoConflicts)r.Data!).Items.Should().Contain("模組代碼 mes-order 已存在");
        (await db.Set<Module>().AsNoTracking().CountAsync()).Should().Be(modules);
        (await db.Set<NumberSequence>().AsNoTracking().CountAsync()).Should().Be(sequences);
        (await db.Set<GroupProgramPrivilege>().AsNoTracking().CountAsync()).Should().Be(privileges);
    }

    [Fact]
    public async Task Generate_ExistingCustomerExtension_Conflict()
    {
        await using var host = await DemoTestHost.CreateAsync();
        (await host.Send(new CreateModuleRequest("cust-x", Text("客戶擴充"), null, null, ModuleKind.Extension, "sales.customer")))
            .IsSuccess.Should().BeTrue();

        var r = await host.Send(new GenerateDemoDataRequest(DemoTestHost.Today));

        r.IsConflict.Should().BeTrue();
        ((DemoConflicts)r.Data!).Items.Should().Contain("既有實體 sales.customer 已有擴充模組");
        (await host.Db.Context.Set<Module>().AsNoTracking().AnyAsync(m => m.Key == "mes-order")).Should().BeFalse();
    }

    [Fact]
    public async Task Preflight_NothingBeforeGenerate()
    {
        await using var host = await DemoTestHost.CreateAsync();
        var preflight = host.Services.GetRequiredService<DemoPreflight>();

        (await preflight.AllPresentAsync(CancellationToken.None)).Should().BeFalse();
        (await preflight.ConflictsAsync(CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Preflight_AllPresent_AfterGenerate()
    {
        fx.Generated();
        var preflight = fx.Host.Services.GetRequiredService<DemoPreflight>();

        (await preflight.ConflictsAsync(CancellationToken.None)).Count.Should().BeGreaterThanOrEqualTo(8);
        (await preflight.AllPresentAsync(CancellationToken.None)).Should().BeTrue();
    }
}
