using System.Text.Json;
using DynamicModules.Application;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddField;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Commands.CreateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.ModuleService.Queries.GetModule;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddOption;
using DynamicModules.Application.Dynamic.FieldService.Commands.UpdateField;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.DeleteRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetPagedRecords;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;
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

public class RecordPermissionTests : IAsyncLifetime
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

    private static Dictionary<string, JsonElement?> Vals(params (string Key, object? Value)[] items) =>
        items.ToDictionary(i => i.Key, i => (JsonElement?)JsonSerializer.SerializeToElement(i.Value));

    private sealed record Setup(string NameKey, FieldResponse Price, FieldResponse Memo, long ModuleId, string ProgramCode, long BlockId);

    /// <summary>以 admin 建立並啟用模組（標題、幣別、備註三欄），再給 sales 讀／新增／金額，資料範圍由 scope 指定。</summary>
    private async Task<Setup> Arrange(DataScope scope = DataScope.All, bool pricing = true, bool read = true, bool requiredPrice = false)
    {
        _user.UserId = "admin";
        await Send(new CreateModuleRequest("order", Text("訂單"), null, null));
        var m = (ModuleDetailResponse)(await Send(new GetModuleRequest("order"))).Data!;
        var block = m.Blocks[0].Id;
        var price = (FieldResponse)(await Send(new AddFieldRequest("order", block, FieldType.Currency, Text("金額"), new FieldConfig { IsFilterable = true, IsSortable = true, Required = requiredPrice }))).Data!;
        var memo = (FieldResponse)(await Send(new AddFieldRequest("order", block, FieldType.Text, Text("備註"), new FieldConfig()))).Data!;
        await Send(new ActivateModuleRequest("order"));
        m = (ModuleDetailResponse)(await Send(new GetModuleRequest("order"))).Data!;
        var nameKey = m.Blocks[0].Fields.Single(f => f.Id == m.TitleFieldId).Key;

        var db = _db.Context;
        var sales = await db.Set<PermissionGroup>().SingleAsync(g => g.Key == "sales");
        db.Set<GroupProgramPrivilege>().Add(new GroupProgramPrivilege
        {
            GroupId = sales.Id, ProgramCode = m.ProgramCode, CanRead = read, CanAdd = true, CanEdit = true, CanDelete = true, CanAmt = pricing
        });
        db.Set<ModuleDataScope>().Add(new ModuleDataScope { ModuleId = m.Id, GroupId = sales.Id, Scope = scope });
        await db.SaveChangesAsync();
        return new Setup(nameKey, price, memo, m.Id, m.ProgramCode, block);
    }

    private async Task Rec(string user, Setup s, string title, decimal price = 100, string memo = "m")
    {
        _user.UserId = user;
        var r = await Send(new CreateRecordRequest("order",
            Vals((s.NameKey, title), (s.Price.Key, price), (s.Memo.Key, memo))));
        r.IsCreated.Should().BeTrue(string.Join(";", r.Errors.Select(e => $"{e.Key}:{string.Join(",", e.Value)}")) + r.Message);
    }

    private async Task<ServiceResult> List(string user, string? filter = null, string[]? sortBy = null)
    {
        _user.UserId = user;
        return await Send(new GetPagedRecordsRequest("order", 1, 50, filter, sortBy ?? [], sortBy?.Select(_ => "asc").ToList() ?? [], null));
    }

    private static PagedResponse<DynamicRecordResponse> Page(ServiceResult r) => (PagedResponse<DynamicRecordResponse>)r.Data!;

    private async Task SeedFour(Setup s)
    {
        await Rec("sales01", s, "a1"); await Rec("sales01", s, "a2");
        await Rec("sales02", s, "b1"); await Rec("sales02", s, "b2");
    }

    [Fact]
    public async Task Paged_OwnScope_ListsOnlyOwnRecords()
    {
        var s = await Arrange(DataScope.Own);
        await SeedFour(s);

        var page = Page(await List("sales01"));

        page.TotalCount.Should().Be(2);
        page.Items.Should().OnlyContain(i => i.OwnerUserId == "sales01");
    }

    [Fact]
    public async Task Paged_GroupScope_ListsGroupMatesRecords()
    {
        var s = await Arrange(DataScope.Group);
        await SeedFour(s);
        var floor = await _db.Context.Set<PermissionGroup>().SingleAsync(g => g.Key == "floor");
        _db.Context.Set<GroupProgramPrivilege>().Add(new GroupProgramPrivilege
        {
            GroupId = floor.Id, ProgramCode = s.ProgramCode, CanRead = true, CanAdd = true
        });
        await _db.Context.SaveChangesAsync();
        await Rec("floor01", s, "f1");

        var page = Page(await List("sales01"));

        page.TotalCount.Should().Be(4);
        page.Items.Select(i => i.OwnerUserId).Should().NotContain("floor01");
    }

    [Fact]
    public async Task Paged_NoRead_Forbidden()
    {
        await Arrange(read: false);

        (await List("sales01")).IsForbidden.Should().BeTrue();
    }

    [Fact]
    public async Task Paged_PricingDenied_CurrencyNeverExposed()
    {
        var s = await Arrange(pricing: false);
        await Rec("sales01", s, "a1");

        var page = Page(await List("sales01"));
        page.Items.Single().Values.Keys.Should().NotContain(s.Price.Key);

        var filter = $$"""{"all":[{"field":{{s.Price.Id}},"op":"gt","value":1}]}""";
        var byFilter = await List("sales01", filter);
        byFilter.Errors["filter"].Should().Equal($"欄位 {s.Price.Id} 不存在或未啟用");

        var bySort = await List("sales01", sortBy: [s.Price.Key]);
        bySort.Errors.Should().ContainKey("sortBy");
    }

    [Fact]
    public async Task Paged_HiddenFieldFilterWithBadOperator_ReportsNotExist()
    {
        var s = await Arrange(pricing: false);

        var filter = $$"""{"all":[{"field":{{s.Price.Id}},"op":"contains","value":"x"}]}""";
        var r = await List("sales01", filter);

        r.Errors["filter"].Should().Equal($"欄位 {s.Price.Id} 不存在或未啟用");
    }

    [Fact]
    public void ToResponse_ModuleWithoutLoadedFields_YieldsEmptyValues()
    {
        var perms = new EffectivePermissions(new HashSet<PermissionAction>(), DataScope.All, null,
            new HashSet<long>(), new HashSet<long>(), new HashSet<long>(), "h");
        var record = new DynamicModules.Domain.Entities.Dynamic.Record { Id = 1, ValuesJson = """{"f_1":"x"}""", OwnerUserId = "a", CreatedUserId = "a", RowVersion = "v" };

        record.ToResponse("order", perms, new Module()).Values.Should().BeEmpty();
    }

    [Fact]
    public async Task Paged_HiddenFieldStrippedFromValues()
    {
        var s = await Arrange();
        await Rec("sales01", s, "a1");
        var sales = await _db.Context.Set<PermissionGroup>().SingleAsync(g => g.Key == "sales");
        _db.Context.Set<FieldPermission>().Add(new FieldPermission { FieldId = s.Memo.Id, GroupId = sales.Id, Access = FieldAccess.Hidden });
        await _db.Context.SaveChangesAsync();

        var values = Page(await List("sales01")).Items.Single().Values;

        values.Keys.Should().NotContain(s.Memo.Key).And.Contain(s.Price.Key);
        Page(await List("admin")).Items.Single().Values.Keys.Should().Contain(s.Memo.Key);
    }

    [Fact]
    public async Task Paged_TitleFieldHidden_TitleBlankAndKeywordIgnored()
    {
        var s = await Arrange();
        await Rec("sales01", s, "alpha");
        await Rec("sales01", s, "beta");
        // 直接寫 DB 造出「標題欄位對 sales 隱藏」：UpdateModule／SaveModulePermissions 已擋，這裡驗防禦層
        var sales = await _db.Context.Set<PermissionGroup>().SingleAsync(g => g.Key == "sales");
        var titleId = (await _db.Context.Set<Field>().SingleAsync(f => f.ModuleId == s.ModuleId && f.Key == s.NameKey)).Id;
        _db.Context.Set<FieldPermission>().Add(new FieldPermission { FieldId = titleId, GroupId = sales.Id, Access = FieldAccess.Hidden });
        await _db.Context.SaveChangesAsync();

        _user.UserId = "sales01";
        var r = await Send(new GetPagedRecordsRequest("order", 1, 50, null, [], [], "alpha"));

        var page = Page(r);
        page.TotalCount.Should().Be(2);
        page.Items.Should().OnlyContain(i => i.Title == "");
        Page(await List("admin")).Items.Select(i => i.Title).Should().BeEquivalentTo("alpha", "beta");
    }

    [Fact]
    public async Task GetRecord_OutOfScope_Returns404()
    {
        var s = await Arrange(DataScope.Own);
        await Rec("sales02", s, "b1");
        var id = Page(await List("admin")).Items.Single().Id;

        _user.UserId = "sales01";
        var r = await Send(new GetRecordRequest(id));

        r.IsNotFound.Should().BeTrue();
        r.Message.Should().Be(RecordMapper.RecordNotFound);
    }

    [Fact]
    public async Task GetRecord_StripsHiddenFields()
    {
        var s = await Arrange(pricing: false);
        await Rec("sales01", s, "a1");
        var id = Page(await List("admin")).Items.Single().Id;

        _user.UserId = "sales01";
        var rec = (DynamicRecordResponse)(await Send(new GetRecordRequest(id))).Data!;

        rec.Values.Keys.Should().NotContain(s.Price.Key).And.Contain(s.Memo.Key);
    }

    private async Task SetPriv(Setup s, Action<GroupProgramPrivilege> change)
    {
        var sales = await _db.Context.Set<PermissionGroup>().SingleAsync(g => g.Key == "sales");
        change(await _db.Context.Set<GroupProgramPrivilege>().SingleAsync(p => p.GroupId == sales.Id && p.ProgramCode == s.ProgramCode));
        await _db.Context.SaveChangesAsync();
    }

    private async Task<DynamicRecordResponse> Make(string user, Setup s, Dictionary<string, JsonElement?> values)
    {
        _user.UserId = user;
        var r = await Send(new CreateRecordRequest("order", values));
        r.IsCreated.Should().BeTrue(string.Join(";", r.Errors.Select(e => $"{e.Key}:{string.Join(",", e.Value)}")) + r.Message);
        return (DynamicRecordResponse)r.Data!;
    }

    private async Task<DynamicRecordResponse> Reload(long id)
    {
        _user.UserId = "admin";
        return (DynamicRecordResponse)(await Send(new GetRecordRequest(id))).Data!;
    }

    [Fact]
    public async Task Create_NoAdd_Forbidden()
    {
        var s = await Arrange();
        await SetPriv(s, p => p.CanAdd = false);
        _user.UserId = "sales01";

        (await Send(new CreateRecordRequest("order", Vals((s.NameKey, "x"))))).IsForbidden.Should().BeTrue();
    }

    [Fact]
    public async Task Update_NoEdit_Forbidden()
    {
        var s = await Arrange();
        var rec = await Make("sales01", s, Vals((s.NameKey, "x")));
        await SetPriv(s, p => p.CanEdit = false);

        var r = await Send(new UpdateRecordRequest(rec.Id, Vals((s.NameKey, "y")), rec.RowVersion));

        r.IsForbidden.Should().BeTrue();
    }

    [Fact]
    public async Task Delete_NoDelete_Forbidden()
    {
        var s = await Arrange();
        var rec = await Make("sales01", s, Vals((s.NameKey, "x")));
        await SetPriv(s, p => p.CanDelete = false);

        (await Send(new DeleteRecordRequest(rec.Id, rec.RowVersion))).IsForbidden.Should().BeTrue();
    }

    [Fact]
    public async Task Create_RequiredFieldHiddenForUser_Succeeds()
    {
        var s = await Arrange(pricing: false, requiredPrice: true);

        var rec = await Make("sales01", s, Vals((s.NameKey, "x")));

        rec.Values.Keys.Should().NotContain(s.Price.Key);
        (await Reload(rec.Id)).Values.Should().NotContainKey(s.Price.Key);
    }

    [Fact]
    public async Task Create_HiddenFieldInInput_Ignored()
    {
        var s = await Arrange(pricing: false);

        var rec = await Make("sales01", s, Vals((s.NameKey, "x"), (s.Price.Key, 999)));

        (await Reload(rec.Id)).Values.Should().NotContainKey(s.Price.Key);
    }

    [Fact]
    public async Task Update_ReadOnlyFieldIgnored_KeepsStoredValue()
    {
        var s = await Arrange();
        var rec = await Make("admin", s, Vals((s.NameKey, "x"), (s.Price.Key, 100)));
        var sales = await _db.Context.Set<PermissionGroup>().SingleAsync(g => g.Key == "sales");
        _db.Context.Set<FieldPermission>().Add(new FieldPermission { FieldId = s.Price.Id, GroupId = sales.Id, Access = FieldAccess.ReadOnly });
        await _db.Context.SaveChangesAsync();

        _user.UserId = "sales01";
        var r = await Send(new UpdateRecordRequest(rec.Id, Vals((s.NameKey, "x2"), (s.Price.Key, 999)), rec.RowVersion));

        r.IsSuccess.Should().BeTrue(r.Message);
        var stored = await Reload(rec.Id);
        stored.Values[s.Price.Key]!.Value.GetDecimal().Should().Be(100);
        stored.Values[s.NameKey]!.Value.GetString().Should().Be("x2");
    }

    [Fact]
    public async Task UpdateAndDelete_OutOfScope_Return404()
    {
        var s = await Arrange(DataScope.Own);
        var rec = await Make("sales02", s, Vals((s.NameKey, "b1")));

        _user.UserId = "sales01";
        var upd = await Send(new UpdateRecordRequest(rec.Id, Vals((s.NameKey, "hack")), rec.RowVersion));
        var del = await Send(new DeleteRecordRequest(rec.Id, rec.RowVersion));

        upd.IsNotFound.Should().BeTrue();
        upd.Message.Should().Be(RecordMapper.RecordNotFound);
        del.IsNotFound.Should().BeTrue();
        del.Message.Should().Be(RecordMapper.RecordNotFound);
        (await Reload(rec.Id)).Values[s.NameKey]!.Value.GetString().Should().Be("b1");
    }

    private async Task<(FieldResponse Field, OptionResponse Restricted)> AddRestrictedOption(Setup s)
    {
        _user.UserId = "admin";
        var m = (ModuleDetailResponse)(await Send(new GetModuleRequest("order"))).Data!;
        var en = (FieldResponse)(await Send(new AddFieldRequest("order", m.Blocks[0].Id, FieldType.Enum, Text("狀態"), new FieldConfig()))).Data!;
        var opt = (OptionResponse)(await Send(new AddOptionRequest(en.Id, Text("主管專用"), null))).Data!;
        var floor = await _db.Context.Set<PermissionGroup>().SingleAsync(g => g.Key == "floor");
        _db.Context.Set<FieldOptionScope>().Add(new FieldOptionScope { OptionId = opt.Id, GroupId = floor.Id });
        await _db.Context.SaveChangesAsync();
        return (en, opt);
    }

    [Fact]
    public async Task Create_UnselectableOption_Rejected()
    {
        var s = await Arrange();
        var (en, opt) = await AddRestrictedOption(s);

        _user.UserId = "sales01";
        var r = await Send(new CreateRecordRequest("order", Vals((s.NameKey, "x"), (en.Key, opt.Value))));

        r.IsSuccess.Should().BeFalse();
        r.Errors.Should().ContainKey(en.Key);
    }

    [Fact]
    public async Task Update_KeepsExistingUnselectableOption()
    {
        var s = await Arrange();
        var (en, opt) = await AddRestrictedOption(s);
        var rec = await Make("admin", s, Vals((s.NameKey, "x"), (en.Key, opt.Value)));

        _user.UserId = "sales01";
        var r = await Send(new UpdateRecordRequest(rec.Id, Vals((s.NameKey, "x2"), (en.Key, opt.Value)), rec.RowVersion));

        r.IsSuccess.Should().BeTrue(string.Join(";", r.Errors.Select(e => $"{e.Key}:{string.Join(",", e.Value)}")) + r.Message);
        (await Reload(rec.Id)).Values[en.Key]!.Value.GetRawText().Should().Be(JsonSerializer.SerializeToElement(opt.Value).GetRawText());
    }

    private async Task<FieldPermission> Restrict(long fieldId, FieldAccess access)
    {
        var sales = await _db.Context.Set<PermissionGroup>().SingleAsync(g => g.Key == "sales");
        var fp = new FieldPermission { FieldId = fieldId, GroupId = sales.Id, Access = access };
        _db.Context.Set<FieldPermission>().Add(fp);
        await _db.Context.SaveChangesAsync();
        return fp;
    }

    [Fact]
    public async Task Update_HiddenFieldInInput_KeepsStoredValue()
    {
        var s = await Arrange(pricing: false);
        var rec = await Make("admin", s, Vals((s.NameKey, "x"), (s.Price.Key, 100)));

        _user.UserId = "sales01";
        var r = await Send(new UpdateRecordRequest(rec.Id, Vals((s.NameKey, "x2"), (s.Price.Key, 999)), rec.RowVersion));

        r.IsSuccess.Should().BeTrue(r.Message);
        (await Reload(rec.Id)).Values[s.Price.Key]!.Value.GetDecimal().Should().Be(100);
    }

    [Fact]
    public async Task Update_RequiredReadOnlyFieldEmpty_Succeeds()
    {
        var s = await Arrange(requiredPrice: true);
        var rec = await Make("admin", s, Vals((s.NameKey, "x"), (s.Price.Key, 100)));
        await Restrict(s.Price.Id, FieldAccess.ReadOnly);
        // 以資料庫直接清空，模擬舊資料沒有該必填值
        var row = await _db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().SingleAsync(r => r.Id == rec.Id);
        row.ValuesJson = JsonSerializer.Serialize(new Dictionary<string, object?> { [s.NameKey] = "x" });
        await _db.Context.SaveChangesAsync();

        _user.UserId = "sales01";
        var r = await Send(new UpdateRecordRequest(rec.Id, Vals((s.NameKey, "x2")), rec.RowVersion));

        r.IsSuccess.Should().BeTrue(string.Join(";", r.Errors.Select(e => $"{e.Key}:{string.Join(",", e.Value)}")) + r.Message);
    }

    [Theory]
    [InlineData(FieldAccess.Hidden)]
    [InlineData(FieldAccess.ReadOnly)]
    public async Task Create_UnwritableFieldGetsDefault(FieldAccess access)
    {
        var s = await Arrange();
        _user.UserId = "admin";
        await Send(new UpdateFieldRequest(s.Memo.Id, FieldType.Text, Text("備註"),
            new FieldConfig { DefaultValue = JsonSerializer.SerializeToElement("預設") }, s.BlockId));
        await Restrict(s.Memo.Id, access);

        var rec = await Make("sales01", s, Vals((s.NameKey, "x"), (s.Memo.Key, "自訂")));

        (await Reload(rec.Id)).Values[s.Memo.Key]!.Value.GetString().Should().Be("預設");
    }

    private async Task<(FieldResponse Field, OptionResponse Restricted)> AddRestrictedMultiOption(Setup s)
    {
        _user.UserId = "admin";
        var m = (ModuleDetailResponse)(await Send(new GetModuleRequest("order"))).Data!;
        var en = (FieldResponse)(await Send(new AddFieldRequest("order", m.Blocks[0].Id, FieldType.MultiEnum, Text("標籤"), new FieldConfig()))).Data!;
        var opt = (OptionResponse)(await Send(new AddOptionRequest(en.Id, Text("主管專用"), null))).Data!;
        var floor = await _db.Context.Set<PermissionGroup>().SingleAsync(g => g.Key == "floor");
        _db.Context.Set<FieldOptionScope>().Add(new FieldOptionScope { OptionId = opt.Id, GroupId = floor.Id });
        await _db.Context.SaveChangesAsync();
        return (en, opt);
    }

    [Fact]
    public async Task Create_UnselectableMultiEnumOption_Rejected()
    {
        var s = await Arrange();
        var (en, opt) = await AddRestrictedMultiOption(s);

        _user.UserId = "sales01";
        var r = await Send(new CreateRecordRequest("order", Vals((s.NameKey, "x"), (en.Key, new[] { opt.Value }))));

        r.IsSuccess.Should().BeFalse();
        r.Errors.Should().ContainKey(en.Key);
    }

    [Fact]
    public async Task Update_KeepsExistingUnselectableMultiEnumOption()
    {
        var s = await Arrange();
        var (en, opt) = await AddRestrictedMultiOption(s);
        var rec = await Make("admin", s, Vals((s.NameKey, "x"), (en.Key, new[] { opt.Value })));

        _user.UserId = "sales01";
        var r = await Send(new UpdateRecordRequest(rec.Id, Vals((s.NameKey, "x2"), (en.Key, new[] { opt.Value })), rec.RowVersion));

        r.IsSuccess.Should().BeTrue(string.Join(";", r.Errors.Select(e => $"{e.Key}:{string.Join(",", e.Value)}")) + r.Message);
        (await Reload(rec.Id)).Values[en.Key]!.Value.GetRawText().Should().Be(JsonSerializer.SerializeToElement(new[] { opt.Value }).GetRawText());
    }

    [Fact]
    public async Task Delete_PersonalPrivilegeGrantsDelete()
    {
        var s = await Arrange();
        await SetPriv(s, p => p.CanDelete = false);
        var rec = await Make("sales01", s, Vals((s.NameKey, "x")));
        _db.Context.Set<UserProgramPrivilege>().Add(new UserProgramPrivilege { UserId = "sales01", ProgramCode = s.ProgramCode, CanDelete = true });
        await _db.Context.SaveChangesAsync();

        _user.UserId = "sales01";
        (await Send(new DeleteRecordRequest(rec.Id, rec.RowVersion))).IsSuccess.Should().BeTrue();
    }
}
