using DynamicModules.Application;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddBlock;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddField;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddOption;
using DynamicModules.Application.Dynamic.FieldService.Commands.ReorderFields;
using DynamicModules.Application.Dynamic.FieldService.Commands.SetFieldStatus;
using DynamicModules.Application.Dynamic.FieldService.Commands.UpdateOption;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Commands.CreateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.ModuleService.Queries.GetModule;
using DynamicModules.Application.Dynamic.SchemaService.Data;
using DynamicModules.Application.Dynamic.SchemaService.Queries.GetMenu;
using DynamicModules.Application.Dynamic.SchemaService.Queries.GetModuleSchema;
using DynamicModules.Application.Dynamic.SchemaService.Queries.GetPreviewSchema;
using DynamicModules.ApplicationTests.Persistence;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Persistence;
using DynamicModules.Persistence.Seed;
using Microsoft.EntityFrameworkCore;
using DynamicModules.TestSupport;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic;

public class SchemaServiceTests : IAsyncLifetime
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
        await DemoPeople.EnsureAsync(_db.Context);
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddPersistenceRepositories();
        services.AddTestEntities();
        services.AddScoped(_ => _db.Context);
        services.AddScoped<ITransactionManager, EfTransactionManager>();
        services.AddScoped<IWorkSavepoints, EfWorkSavepoints>();
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

    private static I18nText Text(string zh = "欄位") => new([KeyValuePair.Create("zh-TW", zh)]);

    private async Task<ModuleDetailResponse> Detail(string key) =>
        (ModuleDetailResponse)(await Send(new GetModuleRequest(key))).Data!;

    private async Task<ModuleDetailResponse> CreateModule(string key = "order", string name = "訂單")
    {
        await Send(new CreateModuleRequest(key, Text(name), null, null));
        return await Detail(key);
    }

    private async Task<FieldResponse> AddField(string key, long blockId, FieldType type = FieldType.Text, string label = "欄位") =>
        (FieldResponse)(await Send(new AddFieldRequest(key, blockId, type, Text(label), new FieldConfig()))).Data!;

    private async Task<ModuleSchemaResponse> Schema(string key = "order") =>
        (ModuleSchemaResponse)(await Send(new GetModuleSchemaRequest(key))).Data!;

    [Fact]
    public async Task Schema_DraftModule_ReturnsNotFound()
    {
        await CreateModule();

        var draft = await Send(new GetModuleSchemaRequest("order"));
        draft.IsNotFound.Should().BeTrue();
        draft.Message.Should().Be("模組不存在或未啟用");
        (await Send(new GetModuleSchemaRequest("missing"))).IsNotFound.Should().BeTrue();
    }

    [Fact]
    public async Task Schema_ActiveModule_ReturnsHeaderAndTitleFieldKey()
    {
        var m = await CreateModule();
        await Send(new ActivateModuleRequest("order"));
        var active = await Detail("order");

        var s = await Schema();

        s.Key.Should().Be("order");
        s.ProgramCode.Should().Be(m.ProgramCode);
        s.Version.Should().Be(active.Version);
        s.TitleFieldKey.Should().Be(m.Blocks[0].Fields.Single(f => f.Id == m.TitleFieldId).Key);
    }

    [Fact]
    public async Task GetSchema_FieldsCarryId()
    {
        var m = await CreateModule();
        await Send(new ActivateModuleRequest("order"));

        var s = await Schema();

        s.Blocks[0].Fields[0].Id.Should().Be(m.TitleFieldId!.Value);
    }

    [Fact]
    public async Task Schema_ExcludesInactiveFields_IncludesInactiveOptions()
    {
        var m = await CreateModule();
        var inactive = await AddField("order", m.Blocks[0].Id, label: "停用欄位");
        await Send(new SetFieldStatusRequest(inactive.Id, FieldStatus.Inactive));
        var en = await AddField("order", m.Blocks[0].Id, FieldType.Enum, "狀態");
        var a = (OptionResponse)(await Send(new AddOptionRequest(en.Id, Text("A"), "#f00"))).Data!;
        var b = (OptionResponse)(await Send(new AddOptionRequest(en.Id, Text("B"), null))).Data!;
        await Send(new UpdateOptionRequest(a.Id, Text("A"), "#f00", false));
        var plain = await AddField("order", m.Blocks[0].Id, label: "備註");
        await Send(new ActivateModuleRequest("order"));

        var fields = (await Schema()).Blocks.Single().Fields;

        fields.Select(f => f.Key).Should().NotContain(inactive.Key);
        var enumField = fields.Single(f => f.Key == en.Key);
        enumField.Options.Select(o => (o.Value, o.Active)).Should().Equal(($"opt_{a.Id}", false), ($"opt_{b.Id}", true));
        enumField.Options[0].Color.Should().Be("#f00");
        fields.Single(f => f.Key == plain.Key).Options.Should().BeEmpty();
    }

    [Fact]
    public async Task Schema_OrdersBlocksAndFieldsBySequence()
    {
        var m = await CreateModule();
        var b2 = (BlockResponse)(await Send(new AddBlockRequest("order", Text("明細")))).Data!;
        var f1 = await AddField("order", m.Blocks[0].Id, label: "一");
        var f2 = await AddField("order", m.Blocks[0].Id, label: "二");
        await AddField("order", b2.Id, label: "明細欄");
        await Send(new ReorderFieldsRequest(m.Blocks[0].Id, [f2.Id, f1.Id, m.TitleFieldId!.Value]));
        await Send(new ActivateModuleRequest("order"));

        var s = await Schema();

        s.Blocks.Select(b => b.Id).Should().Equal(m.Blocks[0].Id, b2.Id);
        s.Blocks[0].Fields.Select(f => f.Key).Should().Equal(f2.Key, f1.Key, s.TitleFieldKey);
    }

    [Fact]
    public async Task Schema_OmitsBlocksWithoutActiveFields()
    {
        var m = await CreateModule();
        await Send(new AddBlockRequest("order", Text("空區塊")));
        await Send(new ActivateModuleRequest("order"));

        (await Schema()).Blocks.Select(b => b.Id).Should().Equal(m.Blocks[0].Id);
    }

    [Fact]
    public async Task Menu_OnlyActiveModules()
    {
        await CreateModule("draft-one", "草稿");
        await CreateModule("active-one", "啟用");
        await Send(new ActivateModuleRequest("active-one"));

        var menu = await Send(new GetMenuRequest());

        menu.Select(i => i.Key).Should().Equal("active-one");
    }

    [Fact]
    public async Task Menu_SortsByZhTwNameThenKey()
    {
        foreach (var (key, name) in new[] { ("k-b", "乙"), ("k-a", "乙"), ("k-c", "丙") })
        {
            await CreateModule(key, name);
            await Send(new ActivateModuleRequest(key));
        }

        var menu = await Send(new GetMenuRequest());

        menu.Select(i => i.Key).Should().Equal("k-c", "k-a", "k-b");
    }

    [Fact]
    public void SchemaETag_QuotesKeyVersionAndPermHash() =>
        SchemaETag.For("order", "3", "ab12cd34").Should().Be("\"order-v3-ab12cd34\"");

    // ---- 權限 ----

    /// <summary>給 sales 群組 order 模組的動作；回傳模組與 sales 群組。</summary>
    private async Task<(ModuleDetailResponse Module, PermissionGroup Sales)> Grant(
        bool read = true, bool add = true, bool delete = false, bool pricing = true)
    {
        var m = await Detail("order");
        var sales = await _db.Context.Set<PermissionGroup>().SingleAsync(g => g.Key == "sales");
        _db.Context.Set<GroupProgramPrivilege>().Add(new GroupProgramPrivilege
        {
            GroupId = sales.Id, ProgramCode = m.ProgramCode, CanRead = read, CanAdd = add, CanDelete = delete, CanAmt = pricing
        });
        await _db.Context.SaveChangesAsync();
        return (m, sales);
    }

    [Fact]
    public async Task Schema_PricingDenied_OmitsCurrency()
    {
        var m = await CreateModule();
        await AddField("order", m.Blocks[0].Id, FieldType.Currency, "金額");
        await AddField("order", m.Blocks[0].Id, FieldType.Text, "備註");
        await Send(new ActivateModuleRequest("order"));
        await Grant(pricing: false);

        _user.UserId = "sales01";
        (await Schema()).Blocks.Single().Fields.Select(f => f.Type).Should().NotContain(FieldType.Currency);
        _user.UserId = "admin";
        (await Schema()).Blocks.Single().Fields.Select(f => f.Type).Should().Contain(FieldType.Currency);
    }

    [Fact]
    public async Task Schema_HiddenField_Omitted_ReadOnlyFlagged()
    {
        var m = await CreateModule();
        var hidden = await AddField("order", m.Blocks[0].Id, FieldType.Text, "隱藏");
        var ro = await AddField("order", m.Blocks[0].Id, FieldType.Text, "唯讀");
        await Send(new ActivateModuleRequest("order"));
        var (_, sales) = await Grant();
        _db.Context.Set<FieldPermission>().AddRange(
            new FieldPermission { FieldId = hidden.Id, GroupId = sales.Id, Access = FieldAccess.Hidden },
            new FieldPermission { FieldId = ro.Id, GroupId = sales.Id, Access = FieldAccess.ReadOnly });
        await _db.Context.SaveChangesAsync();

        _user.UserId = "sales01";
        var fields = (await Schema()).Blocks.Single().Fields;

        fields.Select(f => f.Id).Should().NotContain(hidden.Id);
        fields.Single(f => f.Id == ro.Id).Config.ReadOnly.Should().BeTrue();
        fields.Where(f => f.Id != ro.Id).Should().OnlyContain(f => !f.Config.ReadOnly);
        // metadata 本身沒被改到
        _user.UserId = "admin";
        (await Schema()).Blocks.Single().Fields.Single(f => f.Id == ro.Id).Config.ReadOnly.Should().BeFalse();
    }

    [Fact]
    public async Task Schema_UnselectableOption_IsInactive()
    {
        var m = await CreateModule();
        var f = await AddField("order", m.Blocks[0].Id, FieldType.Enum, "狀態");
        var open = (OptionResponse)(await Send(new AddOptionRequest(f.Id, Text("開放"), null))).Data!;
        var restricted = (OptionResponse)(await Send(new AddOptionRequest(f.Id, Text("限定"), null))).Data!;
        await Send(new ActivateModuleRequest("order"));
        var (_, sales) = await Grant();
        var floor = await _db.Context.Set<PermissionGroup>().SingleAsync(g => g.Key == "floor");
        _db.Context.Set<FieldOptionScope>().Add(new FieldOptionScope { OptionId = restricted.Id, GroupId = floor.Id });
        await _db.Context.SaveChangesAsync();

        _user.UserId = "sales01";
        var options = (await Schema()).Blocks.Single().Fields.Single(x => x.Id == f.Id).Options;

        options.Single(o => o.Value == $"opt_{open.Id}").Active.Should().BeTrue();
        options.Single(o => o.Value == $"opt_{restricted.Id}").Active.Should().BeFalse();
        _user.UserId = "admin";
        (await Schema()).Blocks.Single().Fields.Single(x => x.Id == f.Id).Options.Should().OnlyContain(o => o.Active);
    }

    [Fact]
    public async Task Schema_CarriesPermissions()
    {
        await CreateModule();
        await Send(new ActivateModuleRequest("order"));
        await Grant(read: true, add: true, delete: false, pricing: false);

        _user.UserId = "sales01";
        (await Schema()).Permissions.Should().Be(new SchemaPermissions(Read: true, Add: true, Edit: false, Delete: false, PrintExport: false, Pricing: false));
        _user.UserId = "admin";
        (await Schema()).Permissions.Should().Be(new SchemaPermissions(true, true, true, true, true, true));
    }

    [Fact]
    public async Task Schema_NoRead_Forbidden()
    {
        await CreateModule();
        await Send(new ActivateModuleRequest("order"));
        await Grant(read: false);

        _user.UserId = "sales01";
        (await Send(new GetModuleSchemaRequest("order"))).IsForbidden.Should().BeTrue();
        _user.UserId = "floor01"; // 完全沒授權
        (await Send(new GetModuleSchemaRequest("order"))).IsForbidden.Should().BeTrue();
    }

    [Fact]
    public async Task Schema_PermHashDiffersByUser_ChangesWithPermissions()
    {
        await CreateModule();
        await Send(new ActivateModuleRequest("order"));
        var (m, sales) = await Grant();
        _user.UserId = "sales01";
        var before = (await Schema()).PermHash;
        _user.UserId = "admin";
        (await Schema()).PermHash.Should().NotBe(before);

        _db.Context.Set<GroupProgramPrivilege>().Single(p => p.GroupId == sales.Id && p.ProgramCode == m.ProgramCode).CanEdit = true;
        await _db.Context.SaveChangesAsync();
        _user.UserId = "sales01";
        (await Schema()).PermHash.Should().NotBe(before);
    }

    [Fact]
    public async Task Menu_ListsOnlyReadableModules()
    {
        await CreateModule("readable", "可讀");
        await CreateModule("secret", "不可讀");
        await Send(new ActivateModuleRequest("readable"));
        await Send(new ActivateModuleRequest("secret"));
        var code = (await Detail("readable")).ProgramCode;
        var sales = await _db.Context.Set<PermissionGroup>().SingleAsync(g => g.Key == "sales");
        _db.Context.Set<GroupProgramPrivilege>().Add(new GroupProgramPrivilege { GroupId = sales.Id, ProgramCode = code, CanRead = true });
        await _db.Context.SaveChangesAsync();

        _user.UserId = "sales01";
        (await Send(new GetMenuRequest())).Select(i => i.Key).Should().Equal("readable");
        _user.UserId = "floor01";
        (await Send(new GetMenuRequest())).Should().BeEmpty();
        _user.UserId = "admin";
        (await Send(new GetMenuRequest())).Select(i => i.Key).Should().BeEquivalentTo("readable", "secret");
    }

    [Fact]
    public async Task Schema_HiddenTitleField_TitleKeyNull()
    {
        var m = await CreateModule();
        await Send(new ActivateModuleRequest("order"));
        var (_, sales) = await Grant();
        _db.Context.Set<FieldPermission>().Add(new FieldPermission { FieldId = m.TitleFieldId!.Value, GroupId = sales.Id, Access = FieldAccess.Hidden });
        await _db.Context.SaveChangesAsync();
        var titleKey = m.Blocks[0].Fields.Single(f => f.Id == m.TitleFieldId).Key;

        _user.UserId = "sales01";
        var s = await Schema();
        s.TitleFieldKey.Should().BeNull();
        s.Blocks.SelectMany(b => b.Fields).Select(f => f.Key).Should().NotContain(titleKey);
        _user.UserId = "admin";
        (await Schema()).TitleFieldKey.Should().Be(titleKey);
    }

    [Fact]
    public async Task Schema_ReferenceField_HasReferenceInfo()
    {
        var m = await CreateModule();
        await CreateModule("customer", "客戶");
        var toModule = (FieldResponse)(await Send(new AddFieldRequest("order", m.Blocks[0].Id, FieldType.Reference, Text("客戶"),
            new FieldConfig { RefTarget = new ReferenceTarget(ReferenceTarget.ModuleKind, "customer") }))).Data!;
        var toEntity = (FieldResponse)(await Send(new AddFieldRequest("order", m.Blocks[0].Id, FieldType.Reference, Text("模具"),
            new FieldConfig { RefTarget = new ReferenceTarget(ReferenceTarget.EntityKind, "test.entity") }))).Data!;
        var plain = await AddField("order", m.Blocks[0].Id);
        await Send(new ActivateModuleRequest("order"));

        var fields = (await Schema()).Blocks.SelectMany(b => b.Fields).ToDictionary(f => f.Id);

        var mod = fields[toModule.Id].Reference!;
        mod.Kind.Should().Be("module");
        mod.Key.Should().Be("customer");
        mod.LightUrl.Should().Be("/modules/customer/records/light");
        mod.Name.Resolve("zh-TW").Should().Be("客戶");
        var ent = fields[toEntity.Id].Reference!;
        ent.Kind.Should().Be("entity");
        ent.LightUrl.Should().Be("/entities/test.entity/light");
        ent.Name.Resolve("zh-TW").Should().Be("測試實體");
        fields[plain.Id].Reference.Should().BeNull();
    }

    // ---- 設計器預覽（第 7 階段決策 8） ----

    private async Task<ModuleSchemaResponse> Preview(string key = "order") =>
        (ModuleSchemaResponse)(await Send(new GetPreviewSchemaRequest(key))).Data!;

    [Fact]
    public async Task Preview_DraftModule_ReturnsSchema()
    {
        var m = await CreateModule();
        await AddField("order", m.Blocks[0].Id, FieldType.Text, "備註");
        var inactive = await AddField("order", m.Blocks[0].Id, FieldType.Text, "停用");
        await Send(new SetFieldStatusRequest(inactive.Id, FieldStatus.Inactive));

        (await Send(new GetModuleSchemaRequest("order"))).IsNotFound.Should().BeTrue();
        var preview = await Preview();

        preview.Key.Should().Be("order");
        preview.Blocks.Single().Fields.Select(f => f.Id).Should().NotContain(inactive.Id);
        preview.Blocks.Single().Fields.Should().HaveCountGreaterThan(0);
    }

    [Fact]
    public async Task Preview_IgnoresFieldPermissions()
    {
        var m = await CreateModule();
        var hidden = await AddField("order", m.Blocks[0].Id, FieldType.Text, "隱藏");
        await Send(new ActivateModuleRequest("order"));
        var (_, sales) = await Grant();
        _db.Context.Set<FieldPermission>().Add(new FieldPermission { FieldId = hidden.Id, GroupId = sales.Id, Access = FieldAccess.Hidden });
        await _db.Context.SaveChangesAsync();

        _user.UserId = "admin"; // 設計器 Read：系統管理員
        var preview = await Preview();

        preview.Blocks.Single().Fields.Select(f => f.Id).Should().Contain(hidden.Id);
        preview.Permissions.Should().Be(new SchemaPermissions(true, true, true, true, true, true));
    }

    [Fact]
    public async Task Preview_DetailModule_Allowed()
    {
        await CreateModule("order");
        var line = await CreateModule("line", "明細");
        await AddField("order", (await Detail("order")).Blocks[0].Id);
        await Send(new AddFieldRequest("line", line.Blocks[0].Id, FieldType.Reference, Text("表頭"),
            new FieldConfig { RefTarget = new ReferenceTarget(ReferenceTarget.ModuleKind, "order"), Relation = ReferenceRelation.Detail }));
        await AddField("line", line.Blocks[0].Id, FieldType.Integer, "數量");
        await Send(new ActivateModuleRequest("order"));

        (await Send(new GetModuleSchemaRequest("line"))).IsNotFound.Should().BeTrue();
        (await Preview("line")).Key.Should().Be("line");
        // 表頭預覽只列啟用中的明細（草稿明細看不到）
        (await Preview("order")).Details.Should().BeNullOrEmpty();
        await Send(new ActivateModuleRequest("line"));
        (await Preview("order")).Details.Should().ContainSingle().Which.ModuleKey.Should().Be("line");
    }

    [Fact]
    public async Task Preview_WithoutDesignerRead_403()
    {
        await CreateModule();
        _user.UserId = "sales01";

        (await Send(new GetPreviewSchemaRequest("order"))).IsForbidden.Should().BeTrue();
    }

    [Fact]
    public async Task Preview_UnknownModule_404() =>
        (await Send(new GetPreviewSchemaRequest("nope"))).IsNotFound.Should().BeTrue();
}
