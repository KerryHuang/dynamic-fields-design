using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.FieldService.Commands.SetFieldStatus;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Commands.DeactivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.PermissionService.Commands.SaveModulePermissions;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.DeleteRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetLightRecords;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetPagedRecords;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;
using DynamicModules.Application.Dynamic.RelationService.Queries.GetRelatedPaged;
using DynamicModules.Application.Dynamic.RelationService.Queries.GetRelatedTabs;
using DynamicModules.Application.Dynamic.RuleService.Commands.SaveRules;
using DynamicModules.Application.Dynamic.RuleService.Data;
using DynamicModules.Application.Dynamic.SchemaService.Data;
using DynamicModules.Application.Dynamic.ViewService.Commands.CreateView;
using DynamicModules.Application.Dynamic.ViewService.Commands.DeleteView;
using DynamicModules.Application.Dynamic.ViewService.Commands.UpdateView;
using DynamicModules.Application.Dynamic.ViewService.Queries.GetViews;
using DynamicModules.Application.Dynamic.SchemaService.Queries.GetMenu;
using DynamicModules.Application.Dynamic.SchemaService.Queries.GetModuleSchema;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic;

public class DetailAccessTests : DesignerTestBase
{
    private sealed record S(
        string Parent, string Child, FieldResponse DetailField, FieldResponse Qty, FieldResponse Price,
        FieldResponse Total, FieldResponse Tax, long SalesGroupId);

    /// <summary>order（表頭，金額彙總與稅額公式）＋ line（明細，數量與單價）；sales 群組只被授權表頭。</summary>
    private async Task<S> Arrange(bool pricing = true, bool add = true, bool withRule = false)
    {
        var parent = await NewModule("order");
        var child = await NewModule("line");
        var detailField = await MustAdd(child, FieldType.Reference, RefTo(parent, ReferenceRelation.Detail), "表頭");
        var qty = await MustAdd(child, FieldType.Integer, label: "數量");
        var price = await MustAdd(child, FieldType.Currency, label: "單價");
        var total = await MustAdd(parent, FieldType.Rollup,
            new FieldConfig { Rollup = new RollupConfig(detailField.Id, RollupFunction.Sum, price.Id, null) }, "金額");
        var tax = await MustAdd(parent, FieldType.Formula,
            new FieldConfig { Formula = $"{{{total.Id}}}*0.05", ResultType = "number" }, "稅額");
        if (withRule)
            (await Send(new SaveRulesRequest(child, [new RuleInput(RuleKind.Required,
                J($"{{\"all\":[{{\"field\":{qty.Id},\"op\":\"gt\",\"value\":0}}]}}"), [$"field:{price.Id}"], null, null, true)])))
                .IsSuccess.Should().BeTrue();
        (await Send(new ActivateModuleRequest(parent))).IsSuccess.Should().BeTrue();
        (await Send(new ActivateModuleRequest(child))).IsSuccess.Should().BeTrue();

        var sales = await Db.Context.Set<PermissionGroup>().SingleAsync(g => g.Key == "sales");
        Db.Context.Set<GroupProgramPrivilege>().Add(new GroupProgramPrivilege
        {
            GroupId = sales.Id, ProgramCode = (await Detail(parent)).ProgramCode,
            CanRead = true, CanAdd = add, CanEdit = true, CanDelete = true, CanAmt = pricing
        });
        await Db.Context.SaveChangesAsync();
        return new S(parent, child, detailField, qty, price, total, tax, sales.Id);
    }

    private async Task<DynamicModules.Domain.Entities.Dynamic.Record> InsertRow(string moduleKey, string owner = "admin")
    {
        var module = await Detail(moduleKey);
        var row = new DynamicModules.Domain.Entities.Dynamic.Record
        {
            ModuleId = module.Id, Title = "row", OwnerUserId = owner, ValuesJson = "{}", RowVersion = "v1"
        };
        row.StampCreated(owner, new DateTime(2026, 11, 4));
        Db.Context.Add(row);
        await Db.Context.SaveChangesAsync();
        return row;
    }

    private async Task<DynamicRecordResponse> NewParentRecord(S s)
    {
        var m = await Detail(s.Parent);
        var r = await Send(new CreateRecordRequest(s.Parent,
            new Dictionary<string, JsonElement?> { [$"f_{m.TitleFieldId}"] = J("\"x\"") }));
        r.IsCreated.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        return (DynamicRecordResponse)r.Data!;
    }

    private async Task<ModuleSchemaResponse> Schema(string key)
    {
        var r = await Send(new GetModuleSchemaRequest(key));
        r.IsSuccess.Should().BeTrue(r.Message);
        return (ModuleSchemaResponse)r.Data!;
    }

    private static void ShouldBeModuleNotFound(ServiceResult r)
    {
        r.IsNotFound.Should().BeTrue();
        r.Message.Should().Be("模組不存在或未啟用");
    }

    private static void ShouldBeRecordNotFound(ServiceResult r)
    {
        r.IsNotFound.Should().BeTrue();
        r.Message.Should().Be("記錄不存在");
    }

    // ---- 通用端點看不到明細模組 ----

    [Fact]
    public async Task Menu_ExcludesDetailModule()
    {
        var s = await Arrange();
        var keys = (await Send(new GetMenuRequest())).Select(m => m.Key).ToList();
        keys.Should().Contain(s.Parent).And.NotContain(s.Child);
    }

    [Fact]
    public async Task Schema_DetailModule_NotFound()
    {
        var s = await Arrange();
        ShouldBeModuleNotFound(await Send(new GetModuleSchemaRequest(s.Child)));
    }

    [Fact]
    public async Task Paged_DetailModule_NotFound()
    {
        var s = await Arrange();
        ShouldBeModuleNotFound(await Send(new GetPagedRecordsRequest(s.Child, 1, 10, null, [], [], null)));
    }

    [Fact]
    public async Task Light_DetailModule_NotFound()
    {
        var s = await Arrange();
        ShouldBeModuleNotFound(await Send(new GetLightRecordsRequest(s.Child, null, 1, 20)));
    }

    [Fact]
    public async Task Create_DirectOnDetailModule_NotFound()
    {
        var s = await Arrange();
        var m = await Detail(s.Child);
        ShouldBeModuleNotFound(await Send(new CreateRecordRequest(s.Child,
            new Dictionary<string, JsonElement?> { [$"f_{m.TitleFieldId}"] = J("\"x\"") })));
    }

    [Fact]
    public async Task GetRecord_DetailRow_NotFound()
    {
        var s = await Arrange();
        var row = await InsertRow(s.Child);
        ShouldBeRecordNotFound(await Send(new GetRecordRequest(row.Id)));
    }

    [Fact]
    public async Task Update_DetailRow_NotFound()
    {
        var s = await Arrange();
        var row = await InsertRow(s.Child);
        ShouldBeRecordNotFound(await Send(new UpdateRecordRequest(row.Id, new Dictionary<string, JsonElement?>(), "v1")));
    }

    [Fact]
    public async Task Delete_DetailRow_NotFound()
    {
        var s = await Arrange();
        var row = await InsertRow(s.Child);
        ShouldBeRecordNotFound(await Send(new DeleteRecordRequest(row.Id, "v1")));
    }

    [Fact]
    public async Task Related_DetailRow_NotFound()
    {
        var s = await Arrange();
        var row = await InsertRow(s.Child);
        ShouldBeRecordNotFound(await Send(new GetRelatedTabsRequest(row.Id)));
        ShouldBeRecordNotFound(await Send(new GetRelatedPagedRequest(row.Id, s.DetailField.Id)));
    }

    [Fact]
    public async Task Related_ExcludesDetailField()
    {
        var s = await Arrange();
        var parentRecord = await NewParentRecord(s);
        var tabs = (List<DynamicModules.Application.Dynamic.RelationService.Data.RelatedTab>)
            (await Send(new GetRelatedTabsRequest(parentRecord.Id))).Data!;
        tabs.Should().BeEmpty();
        (await Send(new GetRelatedPagedRequest(parentRecord.Id, s.DetailField.Id))).IsNotFound.Should().BeTrue();
    }

    // ---- 表頭 schema 帶出明細 ----

    [Fact]
    public async Task Schema_Parent_IncludesDetails()
    {
        var s = await Arrange(withRule: true);
        var schema = await Schema(s.Parent);

        var d = schema.Details!.Should().ContainSingle().Subject;
        d.ModuleKey.Should().Be(s.Child);
        d.ParentFieldId.Should().Be(s.DetailField.Id);
        d.ParentFieldKey.Should().Be(s.DetailField.Key);
        d.Fields.Select(f => f.Key).Should().NotContain(s.DetailField.Key).And.Contain([s.Qty.Key, s.Price.Key]);
        d.TitleFieldKey.Should().Be($"f_{(await Detail(s.Child)).TitleFieldId}");
        d.Rules.Should().ContainSingle().Which.Targets.Should().Equal($"field:{s.Price.Id}");
        schema.Blocks.SelectMany(b => b.Fields).Select(f => f.Key).Should().NotContain([s.Qty.Key, s.Price.Key]);
    }

    [Fact]
    public async Task Schema_ParentWithoutDetails_DetailsIsNull()
    {
        var plain = await NewModule("plain", activate: true);
        (await Schema(plain)).Details.Should().BeNull();
    }

    [Fact]
    public async Task Schema_Parent_InactiveDetail_Excluded()
    {
        var s = await Arrange();
        (await Send(new DeactivateModuleRequest(s.Child))).IsSuccess.Should().BeTrue();
        (await Schema(s.Parent)).Details.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task Schema_ETag_ChangesWhenDetailModuleChanges()
    {
        var s = await Arrange();
        string Etag(ModuleSchemaResponse x) => SchemaETag.For(x.Key, x.ETagVersion, x.PermHash);
        var before = await Schema(s.Parent);
        before.ETagVersion.Split('.').Should().HaveCount(2);

        await MustAdd(s.Child, FieldType.Text, label: "備註");

        var after = await Schema(s.Parent);
        Etag(after).Should().NotBe(Etag(before));
        after.Version.Should().Be(before.Version);
    }

    [Fact]
    public async Task Schema_ETag_ChangesWhenDetailFieldPermissionChanges()
    {
        var s = await Arrange();
        As("sales01");
        var before = (await Schema(s.Parent)).PermHash;
        As("admin");
        await SaveFieldAccess(s.Child, s.Qty.Id, s.SalesGroupId, FieldAccess.Hidden);
        As("sales01");
        (await Schema(s.Parent)).PermHash.Should().NotBe(before);
    }

    private async Task<ServiceResult> SaveFieldAccess(string module, long fieldId, long groupId, FieldAccess access) =>
        await Send(new SaveModulePermissionsRequest(module, new ModulePermissionsDto(
            [], [], [new FieldPermissionDto(fieldId, groupId, access)], [])));

    // ---- Pricing 與欄位例外 ----

    [Fact]
    public async Task Schema_NoPricing_HidesCurrencyRollupAndDetailColumns()
    {
        var s = await Arrange(pricing: false);
        As("sales01");
        var schema = await Schema(s.Parent);

        var headerKeys = schema.Blocks.SelectMany(b => b.Fields).Select(f => f.Key).ToList();
        headerKeys.Should().NotContain([s.Total.Key, s.Tax.Key]);
        var detailKeys = schema.Details!.Single().Fields.Select(f => f.Key).ToList();
        detailKeys.Should().NotContain(s.Price.Key).And.Contain(s.Qty.Key);
        schema.Permissions.Pricing.Should().BeFalse();
    }

    [Fact]
    public async Task Schema_WithPricing_ShowsCurrencyRollupAndDetailColumns()
    {
        var s = await Arrange(pricing: true);
        As("sales01");
        var schema = await Schema(s.Parent);

        schema.Blocks.SelectMany(b => b.Fields).Select(f => f.Key).Should().Contain([s.Total.Key, s.Tax.Key]);
        schema.Details!.Single().Fields.Select(f => f.Key).Should().Contain(s.Price.Key);
    }

    [Fact]
    public async Task Schema_NoPricing_AggregatedFormulaBecomesCurrency_HidesRollupAndFormulas()
    {
        var s = await Arrange(pricing: false);
        var points = await MustAdd(s.Child, FieldType.Formula,
            new FieldConfig { Formula = $"{{{s.Qty.Id}}}*2", ResultType = "number" }, "點數");
        var sum = await MustAdd(s.Parent, FieldType.Rollup,
            new FieldConfig { Rollup = new RollupConfig(s.DetailField.Id, RollupFunction.Sum, points.Id, null) }, "點數合計");
        var plusOne = await MustAdd(s.Parent, FieldType.Formula,
            new FieldConfig { Formula = $"{{{sum.Id}}}+1", ResultType = "number" }, "點數加一");
        As("sales01");
        (await Schema(s.Parent)).Blocks.SelectMany(b => b.Fields).Select(f => f.Key).Should().Contain([sum.Key, plusOne.Key]);

        As("admin");
        (await Update(points, points.Config with { ResultType = "currency" })).IsSuccess.Should().BeTrue();

        As("sales01");
        var schema = await Schema(s.Parent);
        schema.Blocks.SelectMany(b => b.Fields).Select(f => f.Key).Should().NotContain([sum.Key, plusOne.Key]);
        schema.Details!.Single().Fields.Select(f => f.Key).Should().NotContain(points.Key);
    }

    [Fact]
    public async Task Schema_NoPricing_RollupReactivatedAfterAggregateBecameCurrency_RederivedAndHidden()
    {
        var s = await Arrange(pricing: false);
        var points = await MustAdd(s.Child, FieldType.Formula,
            new FieldConfig { Formula = $"{{{s.Qty.Id}}}*2", ResultType = "number" }, "點數");
        var sum = await MustAdd(s.Parent, FieldType.Rollup,
            new FieldConfig { Rollup = new RollupConfig(s.DetailField.Id, RollupFunction.Sum, points.Id, null) }, "點數合計");
        (await Deactivate(sum.Id)).IsSuccess.Should().BeTrue();
        (await Update(points, points.Config with { ResultType = "currency" })).IsSuccess.Should().BeTrue();
        (await Send(new SetFieldStatusRequest(sum.Id, FieldStatus.Active))).IsSuccess.Should().BeTrue();
        var plusOne = await MustAdd(s.Parent, FieldType.Formula,
            new FieldConfig { Formula = $"{{{sum.Id}}}+1", ResultType = "number" }, "點數加一");

        Db.Context.ChangeTracker.Clear();
        (await Detail(s.Parent)).Blocks.SelectMany(b => b.Fields).Single(f => f.Id == sum.Id)
            .Config.ResultType.Should().Be("currency");
        As("sales01");
        (await Schema(s.Parent)).Blocks.SelectMany(b => b.Fields).Select(f => f.Key).Should().NotContain([sum.Key, plusOne.Key]);
    }

    [Fact]
    public async Task Schema_NoPricing_RollupOverPricingHiddenNumberFormula_Hidden()
    {
        var s = await Arrange(pricing: false);
        var line = await MustAdd(s.Child, FieldType.Formula,
            new FieldConfig { Formula = $"{{{s.Price.Id}}}*1", ResultType = "number" }, "含稅單價");
        var sum = await MustAdd(s.Parent, FieldType.Rollup,
            new FieldConfig { Rollup = new RollupConfig(s.DetailField.Id, RollupFunction.Sum, line.Id, null) }, "含稅合計");
        var count = await MustAdd(s.Parent, FieldType.Rollup,
            new FieldConfig { Rollup = new RollupConfig(s.DetailField.Id, RollupFunction.Count, null, null) }, "筆數");
        var plusOne = await MustAdd(s.Parent, FieldType.Formula,
            new FieldConfig { Formula = $"{{{sum.Id}}}+1", ResultType = "number" }, "含稅加一");

        As("sales01");
        var keys = (await Schema(s.Parent)).Blocks.SelectMany(b => b.Fields).Select(f => f.Key).ToList();
        keys.Should().NotContain([sum.Key, plusOne.Key]).And.Contain(count.Key);

        var pricing = Db.Context.Set<GroupProgramPrivilege>().Single(p => p.GroupId == s.SalesGroupId);
        pricing.CanAmt = true;
        await Db.Context.SaveChangesAsync();
        (await Schema(s.Parent)).Blocks.SelectMany(b => b.Fields).Select(f => f.Key).Should().Contain([sum.Key, plusOne.Key, count.Key]);
    }

    [Fact]
    public async Task Schema_DetailFieldHidden_ByDetailModuleFieldPermission()
    {
        var s = await Arrange();
        (await Send(new SaveModulePermissionsRequest(s.Child, new ModulePermissionsDto([], [],
            [new FieldPermissionDto(s.Qty.Id, s.SalesGroupId, FieldAccess.Hidden),
             new FieldPermissionDto(s.Price.Id, s.SalesGroupId, FieldAccess.ReadOnly)], [])))).IsSuccess.Should().BeTrue();

        As("sales01");
        var d = (await Schema(s.Parent)).Details!.Single();
        d.Fields.Select(f => f.Key).Should().NotContain(s.Qty.Key).And.Contain(s.Price.Key);
        d.Fields.Single(f => f.Key == s.Price.Key).Config.ReadOnly.Should().BeTrue();
    }

    // ---- AuthorizeDetailAsync ----

    [Fact]
    public async Task AuthorizeDetail_UsesParentPrivileges()
    {
        var s = await Arrange(add: false);
        var modules = Svc<IModuleRepository>();
        var parent = (await modules.GetByKeyAsync(s.Parent, true, default))!;
        var child = (await modules.GetByKeyAsync(s.Child, true, default))!;
        var guard = Svc<IDynamicAccessGuard>();
        As("sales01");

        // 明細模組自己的 ProgramCode 沒有任何授權，結果照表頭
        var read = await guard.AuthorizeDetailAsync(parent, child, PermissionAction.Read, default);
        read.Permissions.Should().NotBeNull();
        read.Permissions!.Can(PermissionAction.Edit).Should().BeTrue();
        (await guard.AuthorizeDetailAsync(parent, child, PermissionAction.Add, default)).Denied!.IsForbidden.Should().BeTrue();
        (await guard.AuthorizeAsync(child, PermissionAction.Read, default)).Denied!.IsForbidden.Should().BeTrue();
    }

    [Fact]
    public async Task AuthorizeDetail_ParentProgramInactive_Denied()
    {
        var s = await Arrange();
        var modules = Svc<IModuleRepository>();
        var parent = (await modules.GetByKeyAsync(s.Parent, true, default))!;
        var child = (await modules.GetByKeyAsync(s.Child, true, default))!;
        (await Db.Context.Set<DynamicProgram>().SingleAsync(p => p.ProgramCode == parent.ProgramCode)).Active = false;
        await Db.Context.SaveChangesAsync();
        As("sales01");

        (await Svc<IDynamicAccessGuard>().AuthorizeDetailAsync(parent, child, PermissionAction.Read, default))
            .Denied!.IsForbidden.Should().BeTrue();
    }

    // ---- 權限儲存 ----

    [Fact]
    public async Task SavePermissions_DetailModuleWithGroups_Rejected()
    {
        var s = await Arrange();
        var priv = new PrivilegesDto(true, false, false, false, false, false, false);
        var r = await Send(new SaveModulePermissionsRequest(s.Child, new ModulePermissionsDto(
            [new GroupPermissionDto(s.SalesGroupId, priv, null)], [], [], [])));
        r.Errors.Should().NotBeEmpty();
        Err(r, "groups").Should().Be("明細模組的權限沿用表頭模組");

        var byUser = await Send(new SaveModulePermissionsRequest(s.Child, new ModulePermissionsDto(
            [], [new UserPermissionDto("sales01", priv)], [], [])));
        Err(byUser, "groups").Should().Be("明細模組的權限沿用表頭模組");
    }

    [Fact]
    public async Task SavePermissions_DetailModuleFieldsOnly_Ok()
    {
        var s = await Arrange();
        (await SaveFieldAccess(s.Child, s.Qty.Id, s.SalesGroupId, FieldAccess.Hidden)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task SavePermissions_Parent_StillAcceptsGroups()
    {
        var s = await Arrange();
        var priv = new PrivilegesDto(true, false, false, false, false, false, false);
        (await Send(new SaveModulePermissionsRequest(s.Parent, new ModulePermissionsDto(
            [new GroupPermissionDto(s.SalesGroupId, priv, null)], [], [], [])))).IsSuccess.Should().BeTrue();
    }

    // ---- 檢視（R9）----

    [Fact]
    public async Task Views_DetailModule_NotFound()
    {
        var s = await Arrange();
        ShouldBeModuleNotFound(await Send(new GetViewsRequest(s.Child)));
        var created = await Send(new CreateViewRequest(s.Child, Text("v"), false, [s.Qty.Id], null, [], false));
        created.IsNotFound.Should().BeTrue(created.Message + string.Join(";", created.Errors.Select(e => e.Key + e.Value[0])));
        ShouldBeModuleNotFound(created);
    }

    [Fact]
    public async Task UpdateAndDeleteView_OnDetailModuleView_NotFound()
    {
        var s = await Arrange();
        var view = new ListView
        {
            ModuleId = (await Detail(s.Child)).Id, OwnerUserId = "admin", NameI18n = Text("v"), Columns = [s.Qty.Id]
        };
        view.StampCreated("admin", new DateTime(2026, 11, 4));
        Db.Context.Add(view);
        await Db.Context.SaveChangesAsync();

        var upd = await Send(new UpdateViewRequest(view.Id, Text("w"), [s.Qty.Id], null, [], false));
        upd.IsNotFound.Should().BeTrue();
        upd.Message.Should().NotBe("模組不存在或未啟用");
        (await Send(new DeleteViewRequest(view.Id))).IsNotFound.Should().BeTrue();
        (await Db.Context.Set<ListView>().CountAsync(v => v.Id == view.Id)).Should().Be(1);
    }
}
