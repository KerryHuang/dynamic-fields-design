using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.ModuleService.Queries.GetModules;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

public class DetailFieldRulesTests : DesignerTestBase
{
    private const string Key = "Config.Relation";

    private static string Reason(string reason) => $"這個欄位不能設為明細：{reason}";

    private async Task<ServiceResult> AddDetail(string child, string parent) =>
        await AddField(child, FieldType.Reference, RefTo(parent, ReferenceRelation.Detail), "表頭");

    private async Task<(string Parent, string Child)> Pair()
    {
        var parent = await NewModule("order");
        var child = await NewModule("line");
        return (parent, child);
    }

    [Fact]
    public async Task Detail_EntityTarget_Rejected()
    {
        var child = await NewModule("line");
        var r = await AddField(child, FieldType.Reference, new FieldConfig
        {
            RefTarget = new ReferenceTarget(ReferenceTarget.EntityKind, "test.entity"), Relation = ReferenceRelation.Detail
        });
        Err(r, Key).Should().Be(Reason("只能參照動態模組"));
    }

    [Fact]
    public async Task Detail_TargetIsExtension_Rejected()
    {
        var ext = await NewModule("ext", kind: ModuleKind.Extension, host: "test.entity");
        var child = await NewModule("line");
        Err(await AddDetail(child, ext), Key).Should().Be(Reason("表頭模組不能是擴充模組或其他模組的明細"));
    }

    [Fact]
    public async Task Detail_TargetIsDetail_Rejected()
    {
        var (parent, child) = await Pair();
        (await AddDetail(child, parent)).IsCreated.Should().BeTrue();
        var grandChild = await NewModule("sub");
        Err(await AddDetail(grandChild, child), Key).Should().Be(Reason("表頭模組不能是擴充模組或其他模組的明細"));
    }

    [Fact]
    public async Task Detail_TargetIsSelf_Rejected()
    {
        var child = await NewModule("line");
        Err(await AddDetail(child, child), Key).Should().Be(Reason("表頭模組不能是擴充模組或其他模組的明細"));
    }

    [Fact]
    public async Task Detail_SecondDetailField_Rejected()
    {
        var (parent, child) = await Pair();
        var other = await NewModule("order2");
        (await AddDetail(child, parent)).IsCreated.Should().BeTrue();
        Err(await AddDetail(child, other), Key).Should().Be(Reason("這個模組已經有表頭"));
    }

    [Fact]
    public async Task Detail_ModuleHasRecords_Rejected()
    {
        var (parent, child) = await Pair();
        await Send(new ActivateModuleRequest(child));
        await CreateRecord(child);
        Err(await AddDetail(child, parent), Key).Should().Be(Reason("這個模組已經有資料"));
    }

    [Fact]
    public async Task Detail_ModuleHasOwnDetails_Rejected()
    {
        var (parent, child) = await Pair();
        var grandChild = await NewModule("sub");
        (await AddDetail(grandChild, child)).IsCreated.Should().BeTrue(); // sub 是 line 的明細
        Err(await AddDetail(child, parent), Key).Should().Be(Reason("這個模組有自己的明細，不能再當明細"));
    }

    [Fact]
    public async Task Detail_ModuleIsReferenced_Rejected()
    {
        var (parent, child) = await Pair();
        var other = await NewModule("memo");
        await MustAdd(other, FieldType.Reference, RefTo(child), "參照明細候選");
        Err(await AddDetail(child, parent), Key).Should().Be(Reason("這個模組被其他欄位參照"));
    }

    [Fact]
    public async Task Detail_ModuleIsReferenced_ByInactiveField_Rejected()
    {
        var (parent, child) = await Pair();
        var other = await NewModule("memo");
        var f = await MustAdd(other, FieldType.Reference, RefTo(child));
        (await Deactivate(f.Id)).IsSuccess.Should().BeTrue();
        Err(await AddDetail(child, parent), Key).Should().Be(Reason("這個模組被其他欄位參照"));
    }

    [Fact]
    public async Task Detail_OnExtensionModule_Rejected()
    {
        var parent = await NewModule("order");
        var ext = await NewModule("ext", kind: ModuleKind.Extension, host: "test.entity");
        Err(await AddDetail(ext, parent), Key).Should().Be(Reason("擴充模組不能當明細"));
    }

    [Fact]
    public async Task Detail_ForcesRequiredAndCascade()
    {
        var (parent, child) = await Pair();
        var r = await AddField(child, FieldType.Reference, RefTo(parent, ReferenceRelation.Detail, ReferenceOnDelete.Restrict), "表頭");
        r.IsCreated.Should().BeTrue();
        var f = (FieldResponse)r.Data!;
        f.Config.Required.Should().BeTrue();
        f.Config.OnDelete.Should().Be(ReferenceOnDelete.Cascade);
        f.Config.Relation.Should().Be(ReferenceRelation.Detail);
    }

    [Fact]
    public async Task Detail_RelationLocked_OnUpdate()
    {
        var (parent, child) = await Pair();
        var detail = await MustAdd(child, FieldType.Reference, RefTo(parent, ReferenceRelation.Detail));
        var lookup = await MustAdd(await NewModule("memo"), FieldType.Reference, RefTo(parent));

        Err(await Update(detail, detail.Config with { Relation = ReferenceRelation.Lookup }), Key).Should().Be("明細設定建立後不能更改");
        Err(await Update(lookup, lookup.Config with { Relation = ReferenceRelation.Detail }), Key).Should().Be("明細設定建立後不能更改");

        // null 與 lookup 視同相同，不算變更
        (await Update(lookup, lookup.Config with { Relation = ReferenceRelation.Lookup })).IsSuccess.Should().BeTrue();
        // 明細欄位更新時仍強制必填與連帶刪除
        var ok = await Update(detail, detail.Config with { OnDelete = ReferenceOnDelete.Restrict });
        ((FieldResponse)ok.Data!).Config.OnDelete.Should().Be(ReferenceOnDelete.Cascade);
    }

    [Fact]
    public async Task Detail_CannotDeactivate()
    {
        var (parent, child) = await Pair();
        var detail = await MustAdd(child, FieldType.Reference, RefTo(parent, ReferenceRelation.Detail));
        Err(await Deactivate(detail.Id), "Status").Should().Be("明細的表頭欄位不能停用");
    }

    [Fact]
    public async Task Reference_ToDetailModule_Rejected()
    {
        var (parent, child) = await Pair();
        (await AddDetail(child, parent)).IsCreated.Should().BeTrue();
        var other = await NewModule("memo");
        Err(await AddField(other, FieldType.Reference, RefTo(child)), "Config.RefTarget").Should().Be("參照目標不正確");
    }

    [Fact]
    public async Task Scope_Parent_OnlyDetailAutonumber()
    {
        var (parent, child) = await Pair();
        var scoped = new FieldConfig { Format = "{000}", Scope = "parent" };

        Err(await AddField(child, FieldType.AutoNumber, scoped), "Config.Scope").Should().Be("只有明細模組的自動編號可以在表頭內編號");
        (await AddDetail(child, parent)).IsCreated.Should().BeTrue();
        var ok = await AddField(child, FieldType.AutoNumber, scoped, "項次");
        ok.IsCreated.Should().BeTrue();
        Err(await AddField(child, FieldType.Integer, new FieldConfig { Scope = "parent" }), "Config.Scope")
            .Should().Be("只有明細模組的自動編號可以在表頭內編號");
        Err(await AddField(parent, FieldType.AutoNumber, scoped), "Config.Scope").Should().Be("只有明細模組的自動編號可以在表頭內編號");
        Err(await AddField(child, FieldType.AutoNumber, new FieldConfig { Format = "{000}", Scope = "global" }), "Config.Scope")
            .Should().Be("只有明細模組的自動編號可以在表頭內編號");

        var f = (FieldResponse)ok.Data!;
        Err(await Update(f, f.Config with { Scope = "other" }), "Config.Scope").Should().Be("只有明細模組的自動編號可以在表頭內編號");
    }

    [Fact]
    public async Task Scope_LockedAfterCreate()
    {
        var (parent, child) = await Pair();
        (await AddDetail(child, parent)).IsCreated.Should().BeTrue();
        var scoped = await MustAdd(child, FieldType.AutoNumber, new FieldConfig { Format = "{000}", Scope = "parent" }, "項次");
        var global = await MustAdd(child, FieldType.AutoNumber, new FieldConfig { Format = "L{000}" }, "流水號");

        Err(await Update(scoped, scoped.Config with { Scope = null }), "Config.Scope").Should().Be("在表頭內編號建立後不能更改");
        Err(await Update(global, global.Config with { Scope = "parent" }), "Config.Scope").Should().Be("在表頭內編號建立後不能更改");
        (await Update(scoped, scoped.Config with { Format = "{0000}" })).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Scope_Parent_FormatWithDate_Rejected()
    {
        var (parent, child) = await Pair();
        (await AddDetail(child, parent)).IsCreated.Should().BeTrue();
        Err(await AddField(child, FieldType.AutoNumber, new FieldConfig { Format = "L{yyyyMM}-{000}", Scope = "parent" }), "Config.Format")
            .Should().Be("在表頭內編號的格式不能含日期");
        (await AddField(child, FieldType.AutoNumber, new FieldConfig { Format = "L{yyyyMM}-{000}" })).IsCreated.Should().BeTrue();
    }

    [Fact]
    public async Task Modules_ListAndDetail_ReportDetailOf()
    {
        var (parent, child) = await Pair();
        (await AddDetail(child, parent)).IsCreated.Should().BeTrue();

        (await Detail(child)).DetailOf.Should().Be(parent);
        (await Detail(parent)).DetailOf.Should().BeNull();
        var list = (List<ModuleResponse>)(await Send(new GetModulesRequest())).Data!;
        list.Single(m => m.Key == child).DetailOf.Should().Be(parent);
        list.Single(m => m.Key == parent).DetailOf.Should().BeNull();
    }
}
