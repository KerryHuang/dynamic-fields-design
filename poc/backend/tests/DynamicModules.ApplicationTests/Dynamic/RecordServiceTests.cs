using System.Text.Json;
using DynamicModules.Application;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddField;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddOption;
using DynamicModules.Application.Dynamic.FieldService.Commands.SetFieldStatus;
using DynamicModules.Application.Dynamic.FieldService.Commands.UpdateField;
using DynamicModules.Application.Dynamic.FieldService.Commands.UpdateOption;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Commands.CreateModule;
using DynamicModules.Application.Dynamic.ModuleService.Commands.DeactivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Commands.UpdateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.ModuleService.Queries.GetModule;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.DeleteRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetPagedRecords;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;
using DynamicModules.ApplicationTests.Persistence;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Persistence;
using DynamicModules.Persistence.Seed;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic;

public class RecordServiceTests : IAsyncLifetime
{
    private SqliteTestDb _db = null!;
    private ServiceProvider _sp = null!;

    private sealed class FakeUser : ICurrentUser { public string UserId => "admin"; }

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
        services.AddScoped<IWorkSavepoints, EfWorkSavepoints>();
        services.AddSingleton<ICurrentUser, FakeUser>();
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

    private static JsonElement? J(object? v) => v is null ? null : JsonSerializer.SerializeToElement(v);

    private static Dictionary<string, JsonElement?> Vals(params (string Key, object? Value)[] items) =>
        items.ToDictionary(i => i.Key, i => J(i.Value));

    private async Task<ModuleDetailResponse> Detail(string key) =>
        (ModuleDetailResponse)(await Send(new GetModuleRequest(key))).Data!;

    private async Task<FieldResponse> AddField(
        string key, long blockId, FieldType type, string label, FieldConfig? config = null) =>
        (FieldResponse)(await Send(new AddFieldRequest(key, blockId, type, Text(label), config ?? new FieldConfig()))).Data!;

    /// <summary>建立模組（含必填標題欄）並可選擇啟用；回傳詳情與標題欄 Key。</summary>
    private async Task<(ModuleDetailResponse Module, string NameKey)> Setup(string key = "order", bool activate = true)
    {
        await Send(new CreateModuleRequest(key, Text("訂單"), null, null));
        if (activate) await Send(new ActivateModuleRequest(key));
        var m = await Detail(key);
        return (m, m.Blocks[0].Fields.Single(f => f.Id == m.TitleFieldId).Key);
    }

    private async Task<DynamicRecordResponse> Create(string module, Dictionary<string, JsonElement?> values)
    {
        var r = await Send(new CreateRecordRequest(module, values));
        r.IsCreated.Should().BeTrue(string.Join(";", r.Errors.Select(e => $"{e.Key}:{string.Join(",", e.Value)}")) + r.Message);
        return (DynamicRecordResponse)r.Data!;
    }

    private async Task<DynamicRecordResponse> Get(long id) =>
        (DynamicRecordResponse)(await Send(new GetRecordRequest(id))).Data!;

    [Fact]
    public async Task AddField_Formula_RecalculatesExistingRecords()
    {
        var (m, nameKey) = await Setup(activate: false);
        var qty = await AddField("order", m.Blocks[0].Id, FieldType.Integer, "數量");
        var price = await AddField("order", m.Blocks[0].Id, FieldType.Decimal, "單價", new FieldConfig { Scale = 2 });
        await Send(new ActivateModuleRequest("order"));
        var r1 = await Create("order", Vals((nameKey, "A"), (qty.Key, 2), (price.Key, 3.5)));
        var r2 = await Create("order", Vals((nameKey, "B"), (qty.Key, 4), (price.Key, 10)));

        var total = await AddField("order", m.Blocks[0].Id, FieldType.Formula, "合計",
            new FieldConfig { Formula = $"{{{qty.Id}}}*{{{price.Id}}}", ResultType = "number", IsFilterable = true });

        var g1 = await Get(r1.Id);
        var g2 = await Get(r2.Id);
        g1.Values[total.Key]!.Value.GetDecimal().Should().Be(7m);
        g2.Values[total.Key]!.Value.GetDecimal().Should().Be(40m);
        g1.RowVersion.Should().NotBe(r1.RowVersion);
        _db.Context.Set<RecordIndex>().Where(i => i.FieldId == total.Id).Select(i => i.NumberValue).ToList()
            .Should().BeEquivalentTo(new double?[] { 7, 40 });
    }

    [Fact]
    public async Task SetFieldStatus_ReactivateFormula_RecalculatesRecordsAndIndex()
    {
        var (m, nameKey) = await Setup(activate: false);
        var qty = await AddField("order", m.Blocks[0].Id, FieldType.Integer, "數量");
        await Send(new ActivateModuleRequest("order"));
        var total = await AddField("order", m.Blocks[0].Id, FieldType.Formula, "兩倍",
            new FieldConfig { Formula = $"{{{qty.Id}}}*2", ResultType = "number", IsFilterable = true });
        var rec = await Create("order", Vals((nameKey, "A"), (qty.Key, 2)));
        await Send(new SetFieldStatusRequest(total.Id, FieldStatus.Inactive));
        var updated = await Send(new UpdateRecordRequest(rec.Id, Vals((qty.Key, 5)), rec.RowVersion));
        updated.IsSuccess.Should().BeTrue();

        await Send(new SetFieldStatusRequest(total.Id, FieldStatus.Active));

        (await Get(rec.Id)).Values[total.Key]!.Value.GetDecimal().Should().Be(10m);
        _db.Context.Set<RecordIndex>().Where(i => i.FieldId == total.Id).Select(i => i.NumberValue).ToList()
            .Should().BeEquivalentTo(new double?[] { 10 });
    }

    [Fact]
    public async Task Create_AutoNumberFormatCorrupted_ThrowsNamingField()
    {
        var (m, nameKey) = await Setup(activate: false);
        var no = await AddField("order", m.Blocks[0].Id, FieldType.AutoNumber, "編號",
            new FieldConfig { Format = "REP-{yyyyMM}-{0000}" });
        await Send(new ActivateModuleRequest("order"));
        _db.Context.Set<Field>().Single(f => f.Id == no.Id).Config = new FieldConfig { Format = "bad{" };
        await _db.Context.SaveChangesAsync();

        var act = () => Send(new CreateRecordRequest("order", Vals((nameKey, "A"))));

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain(no.Id.ToString());
    }

    [Fact]
    public async Task Create_FormulaComputed_InputIgnored()
    {
        var (m, nameKey) = await Setup(activate: false);
        var qty = await AddField("order", m.Blocks[0].Id, FieldType.Integer, "數量");
        var dbl = await AddField("order", m.Blocks[0].Id, FieldType.Formula, "兩倍",
            new FieldConfig { Formula = $"{{{qty.Id}}}*2", ResultType = "number" });
        await Send(new ActivateModuleRequest("order"));

        var rec = await Create("order", Vals((nameKey, "A"), (qty.Key, 5), (dbl.Key, 999)));
        rec.Values[dbl.Key]!.Value.GetDecimal().Should().Be(10m);

        var upd = await Send(new UpdateRecordRequest(rec.Id, Vals((qty.Key, 6), (dbl.Key, 1)), rec.RowVersion));
        ((DynamicRecordResponse)upd.Data!).Values[dbl.Key]!.Value.GetDecimal().Should().Be(12m);

        var cleared = await Send(new UpdateRecordRequest(rec.Id, Vals((qty.Key, null)), ((DynamicRecordResponse)upd.Data!).RowVersion));
        ((DynamicRecordResponse)cleared.Data!).Values.Should().NotContainKey(dbl.Key);
    }

    [Fact]
    public async Task SaveRecord_UnknownAndInactiveKeys_AreIgnored()
    {
        var (m, nameKey) = await Setup(activate: false);
        var gone = await AddField("order", m.Blocks[0].Id, FieldType.Text, "停用");
        await Send(new SetFieldStatusRequest(gone.Id, FieldStatus.Inactive));
        await Send(new ActivateModuleRequest("order"));

        var rec = await Create("order", Vals((nameKey, "A"), (gone.Key, "x"), ("f_99999", "y"), ("bogus", 1)));

        rec.Values.Keys.Should().Equal(nameKey);
    }

    [Fact]
    public async Task SaveRecord_KeepsExistingInactiveOption_RejectsNewOne()
    {
        var (m, nameKey) = await Setup(activate: false);
        var en = await AddField("order", m.Blocks[0].Id, FieldType.Enum, "狀態");
        var a = (OptionResponse)(await Send(new AddOptionRequest(en.Id, Text("A"), null))).Data!;
        var b = (OptionResponse)(await Send(new AddOptionRequest(en.Id, Text("B"), null))).Data!;
        await Send(new ActivateModuleRequest("order"));
        var first = await Create("order", Vals((nameKey, "one"), (en.Key, a.Value)));
        await Send(new UpdateOptionRequest(a.Id, Text("A"), null, false));

        var keep = await Send(new UpdateRecordRequest(first.Id, Vals((nameKey, "one-2"), (en.Key, a.Value)), first.RowVersion));
        keep.IsSuccess.Should().BeTrue();

        var create = await Send(new CreateRecordRequest("order", Vals((nameKey, "two"), (en.Key, a.Value))));
        create.IsSuccess.Should().BeFalse();
        create.Errors.Should().ContainKey(en.Key);

        var other = await Create("order", Vals((nameKey, "three"), (en.Key, b.Value)));
        var update = await Send(new UpdateRecordRequest(other.Id, Vals((en.Key, a.Value)), other.RowVersion));
        update.IsSuccess.Should().BeFalse();
        update.Errors.Should().ContainKey(en.Key);
    }

    [Fact]
    public async Task UpdateModule_ChangingTitleField_RecomputesRecordTitles()
    {
        var (m, nameKey) = await Setup(activate: false);
        var second = await AddField("order", m.Blocks[0].Id, FieldType.Text, "備註");
        await Send(new ActivateModuleRequest("order"));
        var rec = await Create("order", Vals((nameKey, "A"), (second.Key, "B")));
        var other = await Create("order", Vals((nameKey, "C")));
        var rowVersion = rec.RowVersion;

        var update = await Send(new UpdateModuleRequest("order", Text("訂單"), null, null, second.Id));

        update.IsSuccess.Should().BeTrue();
        (await Get(rec.Id)).Title.Should().Be("B");
        (await Get(other.Id)).Title.Should().Be("");
        (await Get(rec.Id)).RowVersion.Should().Be(rowVersion);
    }

    [Fact]
    public async Task CreateRecord_AppliesDefaultValues()
    {
        var (m, nameKey) = await Setup(activate: false);
        var qty = await AddField("order", m.Blocks[0].Id, FieldType.Integer, "數量", new FieldConfig { DefaultValue = J(5) });
        await Send(new ActivateModuleRequest("order"));

        var rec = await Create("order", Vals((nameKey, "A")));
        var explicitNull = await Create("order", Vals((nameKey, "B"), (qty.Key, null)));

        rec.Values[qty.Key]!.Value.GetInt32().Should().Be(5);
        explicitNull.Values.Should().NotContainKey(qty.Key);
    }

    [Fact]
    public async Task CreateRecord_DefaultThatNoLongerNormalizes_IsSkippedSilently()
    {
        var (m, nameKey) = await Setup(activate: false);
        var en = await AddField("order", m.Blocks[0].Id, FieldType.Enum, "狀態");
        var a = (OptionResponse)(await Send(new AddOptionRequest(en.Id, Text("A"), null))).Data!;
        await Send(new UpdateFieldRequest(en.Id, FieldType.Enum, Text("狀態"),
            new FieldConfig { DefaultValue = J(a.Value) }, m.Blocks[0].Id));
        await Send(new ActivateModuleRequest("order"));
        await Send(new UpdateOptionRequest(a.Id, Text("A"), null, false));

        var rec = await Create("order", Vals((nameKey, "A")));

        rec.Values.Should().NotContainKey(en.Key);
    }

    [Fact]
    public async Task CreateRecord_RequiredMultiEnumEmptyArray_ReturnsRequiredError()
    {
        var (m, nameKey) = await Setup(activate: false);
        var multi = await AddField("order", m.Blocks[0].Id, FieldType.MultiEnum, "標籤", new FieldConfig { Required = true });
        await Send(new AddOptionRequest(multi.Id, Text("A"), null));
        await Send(new ActivateModuleRequest("order"));

        var r = await Send(new CreateRecordRequest("order", Vals((nameKey, "A"), (multi.Key, new string[0]))));

        r.IsSuccess.Should().BeFalse();
        r.Errors[multi.Key].Should().Contain("此欄位必填");
    }

    [Fact]
    public async Task UpdateRecord_DoesNotReapplyDefaults()
    {
        var (m, nameKey) = await Setup(activate: false);
        var qty = await AddField("order", m.Blocks[0].Id, FieldType.Integer, "數量", new FieldConfig { DefaultValue = J(5) });
        await Send(new ActivateModuleRequest("order"));
        var rec = await Create("order", Vals((nameKey, "A"), (qty.Key, 9)));

        await Send(new UpdateRecordRequest(rec.Id, Vals((nameKey, "B")), rec.RowVersion));
        var after = await Get(rec.Id);
        after.Values[qty.Key]!.Value.GetInt32().Should().Be(9);

        await Send(new UpdateRecordRequest(rec.Id, Vals((qty.Key, null)), after.RowVersion));
        (await Get(rec.Id)).Values.Should().NotContainKey(qty.Key);
    }

    [Fact]
    public async Task CreateRecord_MissingRequired_ReturnsErrorUnderFieldKey()
    {
        var (_, nameKey) = await Setup();

        var r = await Send(new CreateRecordRequest("order", Vals()));

        r.IsSuccess.Should().BeFalse();
        r.Errors[nameKey].Should().Contain("此欄位必填");
    }

    [Fact]
    public async Task CreateRecord_SetsTitleAndOwner()
    {
        var (_, nameKey) = await Setup();

        var rec = await Create("order", Vals((nameKey, new string('字', 250))));

        rec.Title.Should().HaveLength(200);
        rec.OwnerUserId.Should().Be("admin");
        rec.CreatedUserId.Should().Be("admin");
        rec.ModuleKey.Should().Be("order");
        rec.RowVersion.Should().NotBeEmpty();
        (await Get(rec.Id)).Title.Should().Be(rec.Title);
    }

    [Fact]
    public async Task UpdateRecord_PartialUpdate_KeepsOmittedValues()
    {
        var (m, nameKey) = await Setup(activate: false);
        var qty = await AddField("order", m.Blocks[0].Id, FieldType.Integer, "數量");
        await Send(new ActivateModuleRequest("order"));
        var rec = await Create("order", Vals((nameKey, "A"), (qty.Key, 3)));

        var r = await Send(new UpdateRecordRequest(rec.Id, Vals((nameKey, "B")), rec.RowVersion));

        r.IsSuccess.Should().BeTrue();
        var after = await Get(rec.Id);
        after.Title.Should().Be("B");
        after.Values[qty.Key]!.Value.GetInt32().Should().Be(3);
        after.ModifiedUserId.Should().Be("admin");
    }

    [Fact]
    public async Task UpdateRecord_NullClearsOptionalValue_AndRequiredCheckUsesMergedValues()
    {
        var (m, nameKey) = await Setup(activate: false);
        var qty = await AddField("order", m.Blocks[0].Id, FieldType.Integer, "數量");
        await Send(new ActivateModuleRequest("order"));
        var rec = await Create("order", Vals((nameKey, "A"), (qty.Key, 3)));

        var cleared = await Send(new UpdateRecordRequest(rec.Id, Vals((qty.Key, null)), rec.RowVersion));
        cleared.IsSuccess.Should().BeTrue();
        (await Get(rec.Id)).Values.Should().NotContainKey(qty.Key);

        var fresh = await Get(rec.Id);
        var required = await Send(new UpdateRecordRequest(rec.Id, Vals((nameKey, null)), fresh.RowVersion));
        required.Errors[nameKey].Should().Contain("此欄位必填");
    }

    [Fact]
    public async Task UpdateRecord_PreservesInactiveFieldValues()
    {
        var (m, nameKey) = await Setup(activate: false);
        var extra = await AddField("order", m.Blocks[0].Id, FieldType.Text, "備註");
        await Send(new ActivateModuleRequest("order"));
        var rec = await Create("order", Vals((nameKey, "A"), (extra.Key, "keep")));
        await Send(new SetFieldStatusRequest(extra.Id, FieldStatus.Inactive));

        await Send(new UpdateRecordRequest(rec.Id, Vals((nameKey, "B"), (extra.Key, "hack")), rec.RowVersion));

        (await Get(rec.Id)).Values[extra.Key]!.Value.GetString().Should().Be("keep");
    }

    [Fact]
    public async Task UpdateRecord_StaleRowVersion_ReturnsConflict()
    {
        var (_, nameKey) = await Setup();
        var rec = await Create("order", Vals((nameKey, "A")));
        await Send(new UpdateRecordRequest(rec.Id, Vals((nameKey, "B")), rec.RowVersion));

        var stale = await Send(new UpdateRecordRequest(rec.Id, Vals((nameKey, "C")), rec.RowVersion));
        var staleDelete = await Send(new DeleteRecordRequest(rec.Id, rec.RowVersion));

        stale.IsConflict.Should().BeTrue();
        stale.Message.Should().Be("資料已被其他人修改，請重新載入");
        staleDelete.IsConflict.Should().BeTrue();
        (await Get(rec.Id)).Title.Should().Be("B");
    }

    [Fact]
    public async Task UpdateRecord_ChangesRowVersion()
    {
        var (_, nameKey) = await Setup();
        var rec = await Create("order", Vals((nameKey, "A")));

        await Send(new UpdateRecordRequest(rec.Id, Vals((nameKey, "B")), rec.RowVersion));

        (await Get(rec.Id)).RowVersion.Should().NotBe(rec.RowVersion);
    }

    [Fact]
    public async Task DeleteRecord_ThenGet_ReturnsNotFound()
    {
        var (_, nameKey) = await Setup();
        var rec = await Create("order", Vals((nameKey, "A")));

        (await Send(new DeleteRecordRequest(rec.Id, rec.RowVersion))).IsSuccess.Should().BeTrue();

        (await Send(new GetRecordRequest(rec.Id))).IsNotFound.Should().BeTrue();
        (await Send(new DeleteRecordRequest(rec.Id, rec.RowVersion))).IsNotFound.Should().BeTrue();
        (await Send(new UpdateRecordRequest(rec.Id, Vals((nameKey, "B")), rec.RowVersion))).IsNotFound.Should().BeTrue();
    }

    [Fact]
    public async Task CreateRecord_InactiveModule_ReturnsNotFound()
    {
        var (_, nameKey) = await Setup(activate: false);

        (await Send(new CreateRecordRequest("order", Vals((nameKey, "A"))))).IsNotFound.Should().BeTrue();
        (await Send(new CreateRecordRequest("missing", Vals()))).IsNotFound.Should().BeTrue();
    }

    [Fact]
    public async Task AllRecordOps_DeactivatedModule_ReturnNotFound()
    {
        var (_, nameKey) = await Setup();
        var rec = await Create("order", Vals((nameKey, "A")));
        await Send(new DeactivateModuleRequest("order"));

        (await Send(new GetRecordRequest(rec.Id))).IsNotFound.Should().BeTrue();
        (await Send(new GetPagedRecordsRequest("order", 1, 10, null, [], [], null))).IsNotFound.Should().BeTrue();
        (await Send(new UpdateRecordRequest(rec.Id, Vals((nameKey, "B")), rec.RowVersion))).IsNotFound.Should().BeTrue();
        (await Send(new DeleteRecordRequest(rec.Id, rec.RowVersion))).IsNotFound.Should().BeTrue();
    }

    [Fact]
    public async Task GetPaged_OrdersByIdDescAndExcludesDeleted()
    {
        var (_, nameKey) = await Setup();
        var r1 = await Create("order", Vals((nameKey, "1")));
        var r2 = await Create("order", Vals((nameKey, "2")));
        var r3 = await Create("order", Vals((nameKey, "3")));
        await Send(new DeleteRecordRequest(r2.Id, r2.RowVersion));

        var page = (PagedResponse<DynamicRecordResponse>)(await Send(new GetPagedRecordsRequest("order", 1, 10, null, [], [], null))).Data!;

        page.Items.Select(i => i.Id).Should().Equal(r3.Id, r1.Id);
        page.TotalCount.Should().Be(2);
    }

    [Fact]
    public async Task GetPaged_ClampsPagingArguments()
    {
        var (_, nameKey) = await Setup();
        await Create("order", Vals((nameKey, "1")));

        var page = (PagedResponse<DynamicRecordResponse>)(await Send(new GetPagedRecordsRequest("order", 0, 0, null, [], [], null))).Data!;
        var big = (PagedResponse<DynamicRecordResponse>)(await Send(new GetPagedRecordsRequest("order", 1, 9999, null, [], [], null))).Data!;

        (page.CurrentPage, page.PagesSize).Should().Be((1, 10));
        big.PagesSize.Should().Be(200);
    }

    [Theory]
    [InlineData("q", "關鍵字最多 100 個字元")]
    [InlineData("sortBy", "排序方向只能是 asc 或 desc")]
    [InlineData("sortBy3", "排序欄位最多 3 個")]
    public async Task GetPaged_InvalidQuery_ReturnsInvalidWithExactMessage(string scenario, string message)
    {
        await Setup();
        var req = scenario switch
        {
            "q" => new GetPagedRecordsRequest("order", 1, 10, null, [], [], new string('x', 101)),
            "sortBy" => new GetPagedRecordsRequest("order", 1, 10, null, ["f_1"], ["up"], null),
            _ => new GetPagedRecordsRequest("order", 1, 10, null, ["a", "b", "c", "d"], ["asc", "asc", "asc", "asc"], null)
        };

        var result = await Send(req);

        result.Errors.Should().ContainSingle().Which.Value.Should().Equal(message);
        result.Errors.Keys.Single().Should().Be(scenario == "q" ? "q" : "sortBy");
    }
}
