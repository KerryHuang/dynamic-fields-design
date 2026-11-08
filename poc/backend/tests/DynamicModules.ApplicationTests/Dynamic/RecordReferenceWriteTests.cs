using System.Text.Json;
using DynamicModules.Application;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddField;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Commands.CreateModule;
using DynamicModules.Application.Dynamic.ModuleService.Commands.DeactivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.ModuleService.Queries.GetModule;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.DeleteRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.ApplicationTests.Persistence;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Persistence;
using DynamicModules.Persistence.Seed;
using DynamicModules.TestSupport;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic;

public class RecordReferenceWriteTests : IAsyncLifetime
{
    private const string NotFound = "參照的資料不存在或無權選取";

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

    private static I18nText Text(string zh) => new([KeyValuePair.Create("zh-TW", zh)]);

    private static Dictionary<string, JsonElement?> Vals(params (string Key, object? Value)[] items) =>
        items.ToDictionary(i => i.Key, i => (JsonElement?)JsonSerializer.SerializeToElement(i.Value));

    private async Task<ModuleDetailResponse> Detail(string key) =>
        (ModuleDetailResponse)(await Send(new GetModuleRequest(key))).Data!;

    private sealed record Mod(string Key, string NameKey, string RefKey, long RefFieldId, string? ProgramCode);

    /// <summary>建立並啟用模組：標題欄、備註欄，refTarget 非 null 時再加一個參照欄位。</summary>
    private async Task<Mod> NewModule(string key, ReferenceTarget? refTarget = null)
    {
        _user.UserId = "admin";
        await Send(new CreateModuleRequest(key, Text(key), null, null));
        var block = (await Detail(key)).Blocks[0].Id;
        await Send(new AddFieldRequest(key, block, FieldType.Text, Text("備註"), new FieldConfig()));
        if (refTarget is not null)
        {
            var added = await Send(new AddFieldRequest(key, block, FieldType.Reference, Text("參照"), new FieldConfig { RefTarget = refTarget }));
            added.IsCreated.Should().BeTrue(added.Message + string.Join(";", added.Errors.Select(e => e.Key + e.Value[0])));
        }
        await Send(new ActivateModuleRequest(key));
        var m = await Detail(key);
        var fields = m.Blocks.SelectMany(b => b.Fields).ToList();
        var refField = fields.SingleOrDefault(f => f.Type == FieldType.Reference);
        return new Mod(key, fields.Single(f => f.Id == m.TitleFieldId).Key, refField?.Key ?? "", refField?.Id ?? 0, m.ProgramCode);
    }

    private async Task GrantSales(Mod m, DataScope scope, bool read = true)
    {
        var db = _db.Context;
        var sales = await db.Set<PermissionGroup>().SingleAsync(g => g.Key == "sales");
        db.Set<GroupProgramPrivilege>().Add(new GroupProgramPrivilege
        {
            GroupId = sales.Id, ProgramCode = m.ProgramCode!, CanRead = read, CanAdd = true, CanEdit = true, CanDelete = true, CanAmt = true
        });
        db.Set<ModuleDataScope>().Add(new ModuleDataScope { ModuleId = (await Detail(m.Key)).Id, GroupId = sales.Id, Scope = scope });
        await db.SaveChangesAsync();
    }

    private async Task<DynamicRecordResponse> Rec(string user, Mod m, string title)
    {
        _user.UserId = user;
        var r = await Send(new CreateRecordRequest(m.Key, Vals((m.NameKey, title))));
        r.IsCreated.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        return (DynamicRecordResponse)r.Data!;
    }

    private Task<ServiceResult> Create(string user, Mod m, string title, object? refValue)
    {
        _user.UserId = user;
        return Send(new CreateRecordRequest(m.Key, Vals((m.NameKey, title), (m.RefKey, refValue))));
    }

    private static void ShouldBeRefRejected(ServiceResult r, Mod m)
    {
        r.IsSuccess.Should().BeFalse();
        r.Errors.Should().ContainKey(m.RefKey);
        r.Errors[m.RefKey].Should().Equal(NotFound);
    }

    private async Task<(Mod target, Mod source)> ModuleRefPair()
    {
        var target = await NewModule("cust");
        var source = await NewModule("ord", new ReferenceTarget(ReferenceTarget.ModuleKind, "cust"));
        return (target, source);
    }

    private void SetEntityDeleted(string no) => _sp.GetRequiredService<TestEntityProvider>().Delete(no);

    [Fact]
    public async Task Create_RefToExistingRecord_Ok()
    {
        var (target, source) = await ModuleRefPair();
        var c = await Rec("admin", target, "客戶");

        var r = await Create("admin", source, "訂單", c.Id);

        r.IsCreated.Should().BeTrue(r.Message);
        ((DynamicRecordResponse)r.Data!).Values[source.RefKey]!.Value.ValueKind.Should().Be(JsonValueKind.Number);
    }

    [Fact]
    public async Task Create_RefToDeletedRecord_Rejected()
    {
        var (target, source) = await ModuleRefPair();
        var c = await Rec("admin", target, "客戶");
        (await Send(new DeleteRecordRequest(c.Id, c.RowVersion))).IsSuccess.Should().BeTrue();

        ShouldBeRefRejected(await Create("admin", source, "訂單", c.Id), source);
    }

    [Fact]
    public async Task Create_RefToNonexistent_Rejected()
    {
        var (_, source) = await ModuleRefPair();

        ShouldBeRefRejected(await Create("admin", source, "訂單", 99999), source);
    }

    [Fact]
    public async Task Create_RefToRecordOfAnotherModule_Rejected()
    {
        var (_, source) = await ModuleRefPair();
        var other = await NewModule("other");
        var o = await Rec("admin", other, "別的模組");

        ShouldBeRefRejected(await Create("admin", source, "訂單", o.Id), source);
    }

    [Fact]
    public async Task Create_RefOutOfScope_Rejected()
    {
        var (target, source) = await ModuleRefPair();
        await GrantSales(target, DataScope.Own);
        await GrantSales(source, DataScope.Own);
        var mine = await Rec("sales01", target, "我的客戶");
        var theirs = await Rec("sales02", target, "別人的客戶");

        ShouldBeRefRejected(await Create("sales01", source, "訂單", theirs.Id), source);
        (await Create("sales01", source, "訂單", mine.Id)).IsCreated.Should().BeTrue();
    }

    [Fact]
    public async Task Create_RefTargetNoRead_Rejected()
    {
        var (target, source) = await ModuleRefPair();
        var c = await Rec("admin", target, "客戶");
        await GrantSales(target, DataScope.All, read: false);
        await GrantSales(source, DataScope.All);

        ShouldBeRefRejected(await Create("sales01", source, "訂單", c.Id), source);
    }

    [Fact]
    public async Task Create_RefTargetInactive_Rejected()
    {
        var (target, source) = await ModuleRefPair();
        var c = await Rec("admin", target, "客戶");
        (await Send(new DeactivateModuleRequest("cust"))).IsSuccess.Should().BeTrue();

        ShouldBeRefRejected(await Create("admin", source, "訂單", c.Id), source);
    }

    [Fact]
    public async Task Create_RefToEntity_Ok()
    {
        var source = await NewModule("plan", new ReferenceTarget(ReferenceTarget.EntityKind, "test.entity"));

        var r = await Create("admin", source, "計畫", "E-0001");

        r.IsCreated.Should().BeTrue(r.Message);
        ((DynamicRecordResponse)r.Data!).Values[source.RefKey]!.Value.GetString().Should().Be("E-0001");
    }

    [Fact]
    public async Task Create_RefToUnknownEntityKey_Rejected()
    {
        var source = await NewModule("plan", new ReferenceTarget(ReferenceTarget.EntityKind, "test.entity"));

        ShouldBeRefRejected(await Create("admin", source, "計畫", "E-9999"), source);
    }

    [Fact]
    public async Task Update_StaleEntityRefUnchanged_Saves()
    {
        var source = await NewModule("plan", new ReferenceTarget(ReferenceTarget.EntityKind, "test.entity"));
        var rec = (DynamicRecordResponse)(await Create("admin", source, "計畫", "E-0003")).Data!;
        SetEntityDeleted("E-0003");

        var r = await Send(new UpdateRecordRequest(rec.Id, Vals((source.NameKey, "改名"), (source.RefKey, "E-0003")), rec.RowVersion));

        r.IsSuccess.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        ((DynamicRecordResponse)r.Data!).Title.Should().Be("改名");
    }

    [Fact]
    public async Task Update_ChangedToGoneEntity_Rejected()
    {
        var source = await NewModule("plan", new ReferenceTarget(ReferenceTarget.EntityKind, "test.entity"));
        var rec = (DynamicRecordResponse)(await Create("admin", source, "計畫", "E-0003")).Data!;
        SetEntityDeleted("E-0004");

        var r = await Send(new UpdateRecordRequest(rec.Id, Vals((source.RefKey, "E-0004")), rec.RowVersion));

        ShouldBeRefRejected(r, source);
    }

    [Fact]
    public async Task Update_RefCleared_Ok()
    {
        var source = await NewModule("plan", new ReferenceTarget(ReferenceTarget.EntityKind, "test.entity"));
        var rec = (DynamicRecordResponse)(await Create("admin", source, "計畫", "E-0003")).Data!;
        _db.Context.Set<RecordIndex>().Count(i => i.RecordId == rec.Id && i.FieldId == source.RefFieldId && i.RefKey == "E-0003").Should().Be(1);

        var r = await Send(new UpdateRecordRequest(rec.Id, new Dictionary<string, JsonElement?> { [source.RefKey] = null }, rec.RowVersion));

        r.IsSuccess.Should().BeTrue(r.Message);
        ((DynamicRecordResponse)r.Data!).Values.Should().NotContainKey(source.RefKey);
        _db.Context.Set<RecordIndex>().Count(i => i.RecordId == rec.Id && i.FieldId == source.RefFieldId).Should().Be(0);
    }
}
