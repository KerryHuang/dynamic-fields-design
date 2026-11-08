using System.Text.Json;
using DynamicModules.Application;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddField;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddOption;
using DynamicModules.Application.Dynamic.FieldService.Commands.SetFieldStatus;
using DynamicModules.Application.Dynamic.FieldService.Commands.UpdateField;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Commands.CreateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.ModuleService.Queries.GetModule;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
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

public class RecordIndexTests : IAsyncLifetime
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

    private static Dictionary<string, JsonElement?> Vals(params (string Key, object? Value)[] items) =>
        items.ToDictionary(i => i.Key, i => i.Value is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(i.Value));

    private async Task<(ModuleDetailResponse Module, string NameKey)> Setup()
    {
        await Send(new CreateModuleRequest("order", Text("訂單"), null, null));
        var m = (ModuleDetailResponse)(await Send(new GetModuleRequest("order"))).Data!;
        return (m, m.Blocks[0].Fields.Single(f => f.Id == m.TitleFieldId).Key);
    }

    private async Task<FieldResponse> AddField(ModuleDetailResponse m, FieldType type, string label, FieldConfig? config = null) =>
        (FieldResponse)(await Send(new AddFieldRequest("order", m.Blocks[0].Id, type, Text(label), config ?? new FieldConfig()))).Data!;

    private async Task<DynamicRecordResponse> Create(Dictionary<string, JsonElement?> values)
    {
        var r = await Send(new CreateRecordRequest("order", values));
        r.IsCreated.Should().BeTrue(r.Message);
        return (DynamicRecordResponse)r.Data!;
    }

    private Task<List<RecordIndex>> Rows() =>
        _db.Context.Set<RecordIndex>().AsNoTracking().OrderBy(x => x.RecordId).ThenBy(x => x.FieldId).ThenBy(x => x.Seq).ToListAsync();

    [Fact]
    public async Task CreateRecord_WritesIndexForFilterableFieldsOnly()
    {
        var (m, nameKey) = await Setup();
        var price = await AddField(m, FieldType.Decimal, "價格", new FieldConfig { IsFilterable = true });
        var note = await AddField(m, FieldType.Text, "備註");
        await Send(new ActivateModuleRequest("order"));

        var rec = await Create(Vals((nameKey, "A"), (price.Key, 12.5), (note.Key, "x")));

        var rows = await Rows();
        rows.Should().ContainSingle();
        rows[0].RecordId.Should().Be(rec.Id);
        rows[0].FieldId.Should().Be(price.Id);
        rows[0].NumberValue.Should().Be(12.5);
    }

    [Fact]
    public async Task CreateRecord_MultiEnum_AlwaysIndexed()
    {
        var (m, nameKey) = await Setup();
        var multi = await AddField(m, FieldType.MultiEnum, "標籤");
        var a = (OptionResponse)(await Send(new AddOptionRequest(multi.Id, Text("A"), null))).Data!;
        var b = (OptionResponse)(await Send(new AddOptionRequest(multi.Id, Text("B"), null))).Data!;
        await Send(new ActivateModuleRequest("order"));

        await Create(Vals((nameKey, "A"), (multi.Key, new[] { a.Value, b.Value })));

        var rows = await Rows();
        rows.Select(r => r.Seq).Should().Equal(0, 1);
        rows.Select(r => r.TextValue).Should().BeEquivalentTo([a.Value, b.Value]);
    }

    [Fact]
    public async Task CreateRecord_NullValue_WritesNoRow()
    {
        var (m, nameKey) = await Setup();
        var price = await AddField(m, FieldType.Decimal, "價格", new FieldConfig { IsFilterable = true });
        await Send(new ActivateModuleRequest("order"));

        await Create(Vals((nameKey, "A"), (price.Key, null)));
        await Create(Vals((nameKey, "B")));

        (await Rows()).Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateRecord_ReplacesIndexRows()
    {
        var (m, nameKey) = await Setup();
        var qty = await AddField(m, FieldType.Integer, "數量", new FieldConfig { IsSortable = true });
        await Send(new ActivateModuleRequest("order"));
        var rec = await Create(Vals((nameKey, "A"), (qty.Key, 3)));

        var r = await Send(new UpdateRecordRequest(rec.Id, Vals((qty.Key, 5)), rec.RowVersion));

        r.IsSuccess.Should().BeTrue(r.Message);
        var rows = await Rows();
        rows.Should().ContainSingle();
        rows[0].NumberValue.Should().Be(5);
    }

    [Fact]
    public async Task UpdateRecord_InvalidPayload_DoesNotTouchIndex()
    {
        var (m, nameKey) = await Setup();
        var qty = await AddField(m, FieldType.Integer, "數量", new FieldConfig { IsSortable = true });
        await Send(new ActivateModuleRequest("order"));
        var rec = await Create(Vals((nameKey, "A"), (qty.Key, 3)));

        var r = await Send(new UpdateRecordRequest(rec.Id, Vals((qty.Key, "abc")), rec.RowVersion));

        r.IsSuccess.Should().BeFalse();
        var rows = await Rows();
        rows.Should().ContainSingle();
        rows[0].NumberValue.Should().Be(3);
    }

    [Fact]
    public async Task UpdateField_EnableFilterable_BackfillsExistingRecords()
    {
        var (m, nameKey) = await Setup();
        var qty = await AddField(m, FieldType.Integer, "數量");
        await Send(new ActivateModuleRequest("order"));
        for (var i = 0; i < 30; i++) await Create(Vals((nameKey, $"R{i}"), (qty.Key, i)));
        var gone = await Create(Vals((nameKey, "gone"), (qty.Key, 99)));
        _db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().Single(r => r.Id == gone.Id).DeleteFlag = true;
        await _db.Context.SaveChangesAsync();
        (await Rows()).Should().BeEmpty();

        var r = await Send(new UpdateFieldRequest(qty.Id, FieldType.Integer, Text("數量"),
            new FieldConfig { IsFilterable = true }, m.Blocks[0].Id));

        r.IsSuccess.Should().BeTrue(r.Message);
        var rows = await Rows();
        rows.Should().HaveCount(30);
        rows.Select(x => x.NumberValue).Should().BeEquivalentTo(Enumerable.Range(0, 30).Select(i => (double?)i));
    }

    [Fact]
    public async Task UpdateField_DisableFilterable_RemovesRows()
    {
        var (m, nameKey) = await Setup();
        var qty = await AddField(m, FieldType.Integer, "數量", new FieldConfig { IsFilterable = true });
        await Send(new ActivateModuleRequest("order"));
        await Create(Vals((nameKey, "A"), (qty.Key, 1)));
        (await Rows()).Should().ContainSingle();

        var r = await Send(new UpdateFieldRequest(qty.Id, FieldType.Integer, Text("數量"), new FieldConfig(), m.Blocks[0].Id));

        r.IsSuccess.Should().BeTrue(r.Message);
        (await Rows()).Should().BeEmpty();
    }

    [Fact]
    public async Task SetFieldStatus_Reactivate_BackfillsRowsLostWhileInactive()
    {
        var (m, nameKey) = await Setup();
        var qty = await AddField(m, FieldType.Integer, "數量", new FieldConfig { IsFilterable = true });
        await Send(new ActivateModuleRequest("order"));
        var first = await Create(Vals((nameKey, "A"), (qty.Key, 1)));
        await Create(Vals((nameKey, "B"), (qty.Key, 2)));
        await Send(new SetFieldStatusRequest(qty.Id, FieldStatus.Inactive));
        (await Send(new UpdateRecordRequest(first.Id, Vals((nameKey, "A2")), first.RowVersion))).IsSuccess.Should().BeTrue();
        (await Rows()).Should().ContainSingle();

        await Send(new SetFieldStatusRequest(qty.Id, FieldStatus.Active));

        var rows = await Rows();
        rows.Select(r => r.NumberValue).Should().BeEquivalentTo(new double?[] { 1, 2 });
        rows.Select(r => r.RecordId).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task SetFieldStatus_MadeFilterableWhileInactive_BackfillsOnReactivate()
    {
        var (m, nameKey) = await Setup();
        var qty = await AddField(m, FieldType.Integer, "數量");
        await Send(new ActivateModuleRequest("order"));
        await Create(Vals((nameKey, "A"), (qty.Key, 7)));
        await Send(new SetFieldStatusRequest(qty.Id, FieldStatus.Inactive));
        await Send(new UpdateFieldRequest(qty.Id, FieldType.Integer, Text("數量"),
            new FieldConfig { IsFilterable = true }, m.Blocks[0].Id));
        (await Rows()).Should().BeEmpty();

        await Send(new SetFieldStatusRequest(qty.Id, FieldStatus.Active));

        var rows = await Rows();
        rows.Should().ContainSingle();
        rows[0].NumberValue.Should().Be(7);
    }

    [Fact]
    public async Task Index_ReferenceNotFilterable_StillIndexed()
    {
        var (m, nameKey) = await Setup();
        var mold = (FieldResponse)(await Send(new AddFieldRequest("order", m.Blocks[0].Id, FieldType.Reference, Text("模具"),
            new FieldConfig { RefTarget = new ReferenceTarget(ReferenceTarget.EntityKind, "design.mold") }))).Data!;
        await Send(new ActivateModuleRequest("order"));

        var rec = await Create(Vals((nameKey, "A"), (mold.Key, "M-0001")));

        var rows = await Rows();
        rows.Should().ContainSingle();
        rows[0].RecordId.Should().Be(rec.Id);
        rows[0].FieldId.Should().Be(mold.Id);
        rows[0].RefKey.Should().Be("M-0001");
    }
}
