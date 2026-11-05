using System.Text.Json;
using DynamicModules.Application;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddField;
using DynamicModules.Application.Dynamic.FieldService.Commands.SetFieldStatus;
using DynamicModules.Application.Dynamic.FieldService.Commands.UpdateField;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Commands.CreateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.ModuleService.Queries.GetModule;
using DynamicModules.Application.Dynamic.ViewService.Commands.CreateView;
using DynamicModules.Application.Dynamic.ViewService.Commands.DeleteView;
using DynamicModules.Application.Dynamic.ViewService.Commands.UpdateView;
using DynamicModules.Application.Dynamic.ViewService.Data;
using DynamicModules.Application.Dynamic.ViewService.Queries.GetViews;
using DynamicModules.ApplicationTests.Persistence;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Persistence;
using DynamicModules.Persistence.Seed;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic;

public class ViewServiceTests : IAsyncLifetime
{
    private SqliteTestDb _db = null!;
    private ServiceProvider _sp = null!;
    private readonly FakeUser _user = new();

    private sealed class FakeUser : ICurrentUser { public string UserId { get; set; } = "sales01"; }

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

    /// <summary>設計器操作需要設計器權限；以 admin 送出後還原目前使用者。</summary>
    private async Task<TRes> SendAsAdmin<TRes>(IRequest<TRes> request)
    {
        var previous = _user.UserId;
        _user.UserId = "admin";
        try { return await Send(request); }
        finally { _user.UserId = previous; }
    }

    private static I18nText Text(string zh = "檢視") => new([KeyValuePair.Create("zh-TW", zh)]);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private async Task<FieldResponse> AddField(long blockId, FieldType type, FieldConfig config) =>
        (FieldResponse)(await SendAsAdmin(new AddFieldRequest("order", blockId, type, Text("欄位"), config))).Data!;

    /// <summary>啟用中的模組：標題欄（title）、兩個可篩選可排序的文字欄（a、b）。</summary>
    private async Task<(long Title, FieldResponse A, FieldResponse B)> Setup(bool designerEdit = true, bool pricing = true)
    {
        _user.UserId = "admin"; // 設計器操作需要設計器權限
        await Send(new CreateModuleRequest("order", Text("訂單"), null, null));
        var m = (ModuleDetailResponse)(await Send(new GetModuleRequest("order"))).Data!;
        var cfg = new FieldConfig { IsFilterable = true, IsSortable = true };
        var a = await AddField(m.Blocks[0].Id, FieldType.Text, cfg);
        var b = await AddField(m.Blocks[0].Id, FieldType.Text, cfg);
        await Send(new ActivateModuleRequest("order"));
        if (designerEdit) await GrantSales("DYN00000", edit: true, pricing: false);
        await GrantSales(m.ProgramCode, edit: false, pricing);
        _user.UserId = "sales01";
        return (m.TitleFieldId!.Value, a, b);
    }

    /// <summary>模組沒有預設授權；給 sales 群組該程式的讀／新增，以及指定的 Edit、Pricing。</summary>
    private async Task GrantSales(string programCode, bool edit, bool pricing)
    {
        var sales = await _db.Context.Set<PermissionGroup>().SingleAsync(g => g.Key == "sales");
        _db.Context.Set<GroupProgramPrivilege>().Add(new GroupProgramPrivilege
        {
            GroupId = sales.Id, ProgramCode = programCode, CanRead = true, CanAdd = true, CanEdit = edit, CanAmt = pricing
        });
        await _db.Context.SaveChangesAsync();
    }

    private async Task<ViewResponse> Create(
        string name, bool shared, List<long> columns, JsonElement? filter = null, List<ViewSort>? sort = null,
        bool isDefault = false)
    {
        var r = await Send(new CreateViewRequest("order", Text(name), shared, columns, filter, sort ?? [], isDefault));
        r.IsCreated.Should().BeTrue(string.Join(";", r.Errors.Select(e => $"{e.Key}:{string.Join(",", e.Value)}")) + r.Message);
        return (ViewResponse)r.Data!;
    }

    private async Task<List<ViewResponse>> Views() =>
        (List<ViewResponse>)(await Send(new GetViewsRequest("order"))).Data!;

    private Task<ListView> Row(long id) => _db.Context.Set<ListView>().AsNoTracking().SingleAsync(v => v.Id == id);

    [Fact]
    public async Task CreateView_Personal_OwnedByCurrentUser()
    {
        var (title, a, _) = await Setup();

        var v = await Create("我的", shared: false, [title, a.Id], sort: [new ViewSort(a.Id, "desc")]);

        v.Shared.Should().BeFalse();
        v.IsOwner.Should().BeTrue();
        v.Columns.Should().Equal(title, a.Id);
        v.Sort.Should().Equal(new ViewSort(a.Id, "desc"));
        var row = await Row(v.Id);
        row.OwnerUserId.Should().Be("sales01");
        row.CreatedUserId.Should().Be("sales01");
        row.NameI18n.Values["zh-TW"].Should().Be("我的");
    }

    [Fact]
    public async Task CreateView_Shared_HasNullOwner()
    {
        var (title, _, _) = await Setup();

        var v = await Create("共用", shared: true, [title]);

        v.Shared.Should().BeTrue();
        (await Row(v.Id)).OwnerUserId.Should().BeNull();
    }

    [Fact]
    public async Task CreateView_DoesNotBumpModuleVersion()
    {
        var (title, _, _) = await Setup();
        var before = (await _db.Context.Set<Module>().AsNoTracking().SingleAsync(m => m.Key == "order")).Version;

        await Create("共用", shared: true, [title]);

        (await _db.Context.Set<Module>().AsNoTracking().SingleAsync(m => m.Key == "order")).Version.Should().Be(before);
    }

    [Fact]
    public async Task CreateView_InvalidColumn_ReturnsErrorUnderColumns()
    {
        var (title, a, _) = await Setup();
        await SendAsAdmin(new SetFieldStatusRequest(a.Id, FieldStatus.Inactive));

        var r = await Send(new CreateViewRequest("order", Text(), false, [title, a.Id], null, [], false));

        r.IsSuccess.Should().BeFalse();
        r.Errors["Columns"].Should().Equal($"欄位 {a.Id} 不存在或未啟用");
    }

    [Fact]
    public async Task CreateView_InvalidFilter_ReturnsErrorUnderFilter()
    {
        var (title, a, _) = await Setup();
        var filter = Json($$"""{"all":[{"field":{{a.Id}},"op":"nope","value":"x"}]}""");

        var r = await Send(new CreateViewRequest("order", Text(), false, [title], filter, [], false));

        r.IsSuccess.Should().BeFalse();
        r.Errors["Filter"].Should().ContainSingle().Which.Should().Contain($"欄位 {a.Id}");
    }

    [Fact]
    public async Task CreateView_InvalidSortAndCounts_ReturnErrors()
    {
        var (title, a, _) = await Setup();
        var tooMany = Enumerable.Range(0, 51).Select(i => (long)i).ToList();

        (await Send(new CreateViewRequest("order", Text(), false, [], null, [], false))).Errors.Should().ContainKey("Columns");
        (await Send(new CreateViewRequest("order", Text(), false, tooMany, null, [], false))).Errors.Should().ContainKey("Columns");
        (await Send(new CreateViewRequest("order", Text(), false, [title], null,
            [new(a.Id, "asc"), new(a.Id, "asc"), new(a.Id, "asc"), new(a.Id, "asc")], false))).Errors.Should().ContainKey("Sort");
        (await Send(new CreateViewRequest("order", Text(), false, [title], null, [new(a.Id, "up")], false)))
            .Errors.Should().ContainKey("Sort");
        (await Send(new CreateViewRequest("order", Text(), false, [title], null, [new(999999, "asc")], false)))
            .Errors.Should().ContainKey("Sort");
        (await Send(new CreateViewRequest("order", new I18nText(), false, [title], null, [], false)))
            .Errors.Should().ContainKey("Name");
    }

    [Fact]
    public async Task CreateView_ModuleNotActive_ReturnsNotFound()
    {
        await SendAsAdmin(new CreateModuleRequest("order", Text("訂單"), null, null));

        var r = await Send(new CreateViewRequest("order", Text(), false, [1], null, [], false));

        r.IsNotFound.Should().BeTrue();
        r.Message.Should().Be("模組不存在或未啟用");
    }

    [Fact]
    public async Task CreateView_SetDefault_ClearsPreviousDefaultInSameScopeOnly()
    {
        var (title, _, _) = await Setup();
        var shared1 = await Create("共用1", shared: true, [title], isDefault: true);
        var mine1 = await Create("個人1", shared: false, [title], isDefault: true);
        (await Row(shared1.Id)).IsDefault.Should().BeTrue();

        var mine2 = await Create("個人2", shared: false, [title], isDefault: true);

        (await Row(mine1.Id)).IsDefault.Should().BeFalse();
        (await Row(mine2.Id)).IsDefault.Should().BeTrue();
        (await Row(shared1.Id)).IsDefault.Should().BeTrue();

        var shared2 = await Create("共用2", shared: true, [title], isDefault: true);
        (await Row(shared1.Id)).IsDefault.Should().BeFalse();
        (await Row(shared2.Id)).IsDefault.Should().BeTrue();
        (await Row(mine2.Id)).IsDefault.Should().BeTrue();

        _user.UserId = "sales02";
        var other = await Create("別人個人", shared: false, [title], isDefault: true);
        (await Row(other.Id)).IsDefault.Should().BeTrue();
        (await Row(mine2.Id)).IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task GetViews_ReturnsSharedAndOwnOnly()
    {
        var (title, _, _) = await Setup();
        var mine = await Create("sales01 個人", shared: false, [title]);
        var shared = await Create("共用", shared: true, [title]);
        _user.UserId = "sales02";
        var theirs = await Create("sales02 個人", shared: false, [title]);

        var forSales02 = await Views();
        _user.UserId = "sales01";
        var forSales01 = await Views();

        forSales01.Select(v => v.Id).Should().Equal(shared.Id, mine.Id);
        forSales01.Select(v => v.IsOwner).Should().Equal(true, true);
        forSales02.Select(v => v.IsOwner).Should().Equal(false, true);
        forSales02.Select(v => v.Id).Should().Equal(shared.Id, theirs.Id);
    }

    [Fact]
    public async Task GetViews_StripsInactiveAndNonFilterableFields()
    {
        var (title, a, b) = await Setup();
        var c = await AddField(a.BlockId, FieldType.Text, new FieldConfig { IsFilterable = true, IsSortable = true });
        var filter = Json($$"""
            {"all":[
              {"field":{{a.Id}},"op":"eq","value":"x"},
              {"any":[{"field":{{b.Id}},"op":"eq","value":"y"}]},
              {"field":{{c.Id}},"op":"eq","value":"z"}
            ]}
            """);
        var view = await Create("v", shared: true, [title, a.Id, b.Id, c.Id], filter,
            [new ViewSort(a.Id, "asc"), new ViewSort(c.Id, "desc")]);
        await SendAsAdmin(new SetFieldStatusRequest(a.Id, FieldStatus.Inactive));
        await SendAsAdmin(new UpdateFieldRequest(b.Id, FieldType.Text, Text("欄位"), new FieldConfig { IsSortable = true }, b.BlockId));

        var got = (await Views()).Single();

        got.StaleFieldIds.Should().BeEquivalentTo([a.Id, b.Id]);
        got.Columns.Should().Equal(title, b.Id, c.Id);
        got.Sort.Should().Equal(new ViewSort(c.Id, "desc"));
        JsonSerializer.Serialize(got.Filter).Should().Be($$"""{"all":[{"field":{{c.Id}},"op":"eq","value":"z"}]}""");
        (await Row(view.Id)).Columns.Should().HaveCount(4, "資料庫內容不改");
    }

    [Fact]
    public async Task GetViews_AllConditionsStale_FilterBecomesNull()
    {
        var (title, a, _) = await Setup();
        await Create("v", shared: true, [title], Json($$"""{"all":[{"field":{{a.Id}},"op":"eq","value":"x"}]}"""));
        await SendAsAdmin(new SetFieldStatusRequest(a.Id, FieldStatus.Inactive));

        var got = (await Views()).Single();

        got.Filter.Should().BeNull();
        got.StaleFieldIds.Should().Equal(a.Id);
    }

    [Fact]
    public async Task UpdateView_ChangesContent_AndKeepsScope()
    {
        var (title, a, _) = await Setup();
        var v = await Create("舊", shared: true, [title]);

        var r = await Send(new UpdateViewRequest(v.Id, Text("新"), [title, a.Id], null, [], true));

        r.IsSuccess.Should().BeTrue();
        var row = await Row(v.Id);
        row.NameI18n.Values["zh-TW"].Should().Be("新");
        row.Columns.Should().Equal(title, a.Id);
        row.IsDefault.Should().BeTrue();
        row.OwnerUserId.Should().BeNull();
        row.ModifiedUserId.Should().Be("sales01");
    }

    [Fact]
    public async Task UpdateView_OthersPersonal_ReturnsNotFound()
    {
        var (title, _, _) = await Setup();
        var v = await Create("個人", shared: false, [title]);
        _user.UserId = "sales02";

        var r = await Send(new UpdateViewRequest(v.Id, Text("改"), [title], null, [], false));

        r.IsNotFound.Should().BeTrue();
        r.Message.Should().Be("檢視不存在");
        (await Row(v.Id)).NameI18n.Values["zh-TW"].Should().Be("個人");
    }

    [Fact]
    public async Task UpdateView_OthersShared_Succeeds()
    {
        var (title, _, _) = await Setup();
        var v = await Create("共用", shared: true, [title]);
        _user.UserId = "sales02";

        (await Send(new UpdateViewRequest(v.Id, Text("改"), [title], null, [], false))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteView_OthersPersonal_ReturnsNotFound()
    {
        var (title, _, _) = await Setup();
        var v = await Create("個人", shared: false, [title]);
        _user.UserId = "sales02";

        (await Send(new DeleteViewRequest(v.Id))).IsNotFound.Should().BeTrue();
        (await _db.Context.Set<ListView>().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task DeleteView_Removes()
    {
        var (title, _, _) = await Setup();
        var v = await Create("個人", shared: false, [title]);

        var r = await Send(new DeleteViewRequest(v.Id));

        r.IsSuccess.Should().BeTrue();
        (await _db.Context.Set<ListView>().CountAsync()).Should().Be(0);
        (await Send(new DeleteViewRequest(v.Id))).IsNotFound.Should().BeTrue();
    }

    [Fact]
    public async Task GetViews_HiddenFieldStrippedSilently()
    {
        var (title, a, _) = await Setup(pricing: false);
        var price = await SendAsAdmin(new AddFieldRequest("order", a.BlockId, FieldType.Currency, Text("金額"),
            new FieldConfig { IsFilterable = true, IsSortable = true }));
        var priceId = ((FieldResponse)price.Data!).Id;
        _user.UserId = "admin";
        var filter = Json($$"""
            {"all":[{"field":{{a.Id}},"op":"eq","value":"x"},{"field":{{priceId}},"op":"gt","value":5}]}
            """);
        await Create("共用", shared: true, [title, a.Id, priceId], filter, [new ViewSort(priceId, "asc")]);

        var admin = (await Views()).Single();
        _user.UserId = "sales01";
        var got = (await Views()).Single();

        admin.StaleFieldIds.Should().BeEmpty();
        admin.Columns.Should().Equal(title, a.Id, priceId);
        JsonSerializer.Serialize(admin.Filter).Should().Contain($"\"field\":{priceId}");
        // 看不到的欄位（沒有金額軸）靜靜剔除，不算「已停用或不再開放篩選、排序」
        got.StaleFieldIds.Should().BeEmpty();
        got.Columns.Should().Equal(title, a.Id);
        got.Sort.Should().BeEmpty();
        JsonSerializer.Serialize(got.Filter).Should().Be($$"""{"all":[{"field":{{a.Id}},"op":"eq","value":"x"}]}""");
    }

    [Fact]
    public async Task GetViews_WithoutRead_Forbidden()
    {
        await Setup();
        _user.UserId = "floor01";

        (await Send(new GetViewsRequest("order"))).IsForbidden.Should().BeTrue();
    }

    [Fact]
    public async Task CreateSharedView_WithoutDesignerEdit_Forbidden()
    {
        var (title, _, _) = await Setup(designerEdit: false);

        var r = await Send(new CreateViewRequest("order", Text(), true, [title], null, [], false));

        r.IsForbidden.Should().BeTrue();
        (await _db.Context.Set<ListView>().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CreatePersonalView_WithReadOnly_Succeeds()
    {
        var (title, _, _) = await Setup(designerEdit: false);

        (await Create("個人", shared: false, [title])).Shared.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateAndDeleteSharedView_WithoutDesignerEdit_Forbidden()
    {
        var (title, _, _) = await Setup(designerEdit: false);
        var shared = new ListView
        {
            ModuleId = (await _db.Context.Set<Module>().SingleAsync(m => m.Key == "order")).Id, OwnerUserId = null,
            NameI18n = Text("共用"), Columns = [title], Sort = []
        };
        shared.StampCreated("admin", new DateTime(2026, 11, 3));
        _db.Context.Set<ListView>().Add(shared);
        await _db.Context.SaveChangesAsync();

        (await Send(new UpdateViewRequest(shared.Id, Text("改"), [title], null, [], false))).IsForbidden.Should().BeTrue();
        (await Send(new DeleteViewRequest(shared.Id))).IsForbidden.Should().BeTrue();
        (await _db.Context.Set<ListView>().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task UpdateSharedView_ReferencingHiddenField_Forbidden()
    {
        var (title, a, _) = await Setup(pricing: false);
        var priceId = ((FieldResponse)(await SendAsAdmin(new AddFieldRequest("order", a.BlockId, FieldType.Currency, Text("金額"),
            new FieldConfig { IsFilterable = true, IsSortable = true }))).Data!).Id;
        _user.UserId = "admin";
        var filter = Json($$"""{"all":[{"field":{{priceId}},"op":"gt","value":5}]}""");
        var v = await Create("共用", shared: true, [title, priceId], filter, [new ViewSort(priceId, "asc")]);
        _user.UserId = "sales01";

        var r = await Send(new UpdateViewRequest(v.Id, Text("改"), [title], null, [], false));

        r.IsForbidden.Should().BeTrue();
        _user.UserId = "admin";
        var stored = (await Views()).Single();
        stored.Name.Values["zh-TW"].Should().Be("共用");
        stored.Columns.Should().Equal(title, priceId);
        stored.Sort.Should().Equal(new ViewSort(priceId, "asc"));
        stored.Filter.Should().NotBeNull();
        (await Send(new DeleteViewRequest(v.Id))).IsSuccess.Should().BeTrue();
    }
}
