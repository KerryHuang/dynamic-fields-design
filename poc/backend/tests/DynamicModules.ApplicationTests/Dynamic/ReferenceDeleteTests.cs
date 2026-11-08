using System.Text.Json;
using DynamicModules.Application;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddField;
using DynamicModules.Application.Dynamic.FieldService.Commands.SetFieldStatus;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Commands.CreateModule;
using DynamicModules.Application.Dynamic.ModuleService.Commands.DeactivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.ModuleService.Queries.GetModule;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.DeleteRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.References;
using DynamicModules.ApplicationTests.Persistence;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Persistence;
using DynamicModules.Persistence.Seed;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.ApplicationTests.Dynamic;

public class ReferenceDeleteTests : IAsyncLifetime
{
    private const string Blocked = "還有其他資料參照這筆記錄，不能刪除";
    private const string TooDeep = "連帶刪除超過 5 層，請先手動刪除下層資料";

    private SqliteTestDb _db = null!;
    private ServiceProvider _sp = null!;
    private readonly FakeUser _user = new();

    private sealed class FakeUser : ICurrentUser { public string UserId { get; set; } = "admin"; }

    private sealed class FakeTime : IBusinessTime
    {
        public DateTime Now => new(2026, 11, 4, 9, 0, 0);
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

    private sealed record Mod(string Key, string NameKey, string RefKey, long RefFieldId, string ProgramCode);

    private async Task<(long Id, string Key)> AddRef(string moduleKey, string target, ReferenceOnDelete? onDelete)
    {
        _user.UserId = "admin";
        var block = (await Detail(moduleKey)).Blocks[0].Id;
        var added = await Send(new AddFieldRequest(moduleKey, block, FieldType.Reference, Text("參照"),
            new FieldConfig { RefTarget = new ReferenceTarget(ReferenceTarget.ModuleKind, target), OnDelete = onDelete }));
        added.IsCreated.Should().BeTrue(added.Message + string.Join(";", added.Errors.Select(e => e.Key + e.Value[0])));
        var f = (await Detail(moduleKey)).Blocks.SelectMany(b => b.Fields).Single(x => x.Type == FieldType.Reference && x.Config.RefTarget!.Key == target);
        return (f.Id, f.Key);
    }

    /// <summary>建立並啟用模組；refTarget 非 null 時加一個參照欄位（可指向自己）。</summary>
    private async Task<Mod> NewModule(string key, string? refTarget = null, ReferenceOnDelete? onDelete = null)
    {
        _user.UserId = "admin";
        await Send(new CreateModuleRequest(key, Text(key), null, null));
        var (refId, refKey) = refTarget is null ? (0L, "") : await AddRef(key, refTarget, onDelete);
        await Send(new ActivateModuleRequest(key));
        var m = await Detail(key);
        var titleKey = m.Blocks.SelectMany(b => b.Fields).Single(f => f.Id == m.TitleFieldId).Key;
        return new Mod(key, titleKey, refKey, refId, m.ProgramCode!);
    }

    private async Task<DynamicRecordResponse> Rec(Mod m, string title, object? refValue = null, string user = "admin")
    {
        _user.UserId = user;
        var values = refValue is null ? Vals((m.NameKey, title)) : Vals((m.NameKey, title), (m.RefKey, refValue));
        var r = await Send(new CreateRecordRequest(m.Key, values));
        r.IsCreated.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        return (DynamicRecordResponse)r.Data!;
    }

    private async Task<ServiceResult> Delete(DynamicRecordResponse r, string user = "admin")
    {
        _user.UserId = user;
        var row = await Fresh(r.Id);
        return await Send(new DeleteRecordRequest(r.Id, row.RowVersion));
    }

    /// <summary>直接讀 DB（不經 change tracker 快取），含已刪除的記錄。</summary>
    private Task<Record> Fresh(long id) => _db.Context.Set<Record>().AsNoTracking().SingleAsync(r => r.Id == id);

    private async Task GrantSales(Mod m, DataScope scope, bool read = true, bool delete = true)
    {
        var db = _db.Context;
        var sales = await db.Set<PermissionGroup>().SingleAsync(g => g.Key == "sales");
        db.Set<GroupProgramPrivilege>().Add(new GroupProgramPrivilege
        {
            GroupId = sales.Id, ProgramCode = m.ProgramCode, CanRead = read, CanAdd = true, CanEdit = true, CanDelete = delete, CanAmt = true
        });
        db.Set<ModuleDataScope>().Add(new ModuleDataScope { ModuleId = (await Detail(m.Key)).Id, GroupId = sales.Id, Scope = scope });
        await db.SaveChangesAsync();
    }

    private static JsonElement Body(ServiceResult r) =>
        JsonDocument.Parse(JsonSerializer.Serialize(r.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web))).RootElement;

    [Fact]
    public async Task Delete_NoReferences_Deletes()
    {
        var cust = await NewModule("cust");
        var c = await Rec(cust, "客戶一");

        (await Delete(c)).IsSuccess.Should().BeTrue();

        (await Fresh(c.Id)).DeleteFlag.Should().BeTrue();
    }

    [Fact]
    public async Task Delete_Restrict_Blocked409()
    {
        var cust = await NewModule("cust");
        var ord = await NewModule("ord", "cust");
        var c = await Rec(cust, "客戶一");
        var o = await Rec(ord, "訂單一", c.Id);

        var r = await Delete(c);

        r.IsConflict.Should().BeTrue();
        r.Message.Should().Be(Blocked);
        var body = Body(r);
        body.GetProperty("total").GetInt32().Should().Be(1);
        var first = body.GetProperty("references")[0];
        first.GetProperty("moduleKey").GetString().Should().Be("ord");
        first.GetProperty("recordId").GetInt64().Should().Be(o.Id);
        first.GetProperty("title").GetString().Should().Be("訂單一");
        first.GetProperty("moduleName").GetProperty("zh-TW").GetString().Should().Be("ord");
        first.GetProperty("fieldLabel").GetProperty("zh-TW").GetString().Should().Be("參照");
        (await Fresh(c.Id)).DeleteFlag.Should().BeFalse();
    }

    [Fact]
    public async Task Delete_Restrict_SourceUnreadable_TitleNull()
    {
        var cust = await NewModule("cust");
        var ord = await NewModule("ord", "cust");
        await GrantSales(cust, DataScope.All);
        await GrantSales(ord, DataScope.All, read: false);
        var c = await Rec(cust, "客戶一");
        await Rec(ord, "訂單一", c.Id);

        var r = await Delete(c, "sales01");

        r.IsConflict.Should().BeTrue();
        r.Message.Should().Be(Blocked);
        Body(r).GetProperty("references")[0].GetProperty("title").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Delete_Restrict_SourceOutOfScope_TitleNull()
    {
        var cust = await NewModule("cust");
        var ord = await NewModule("ord", "cust");
        await GrantSales(cust, DataScope.All);
        await GrantSales(ord, DataScope.Own);
        var c = await Rec(cust, "客戶一");
        await Rec(ord, "別人的", c.Id, user: "sales02");
        await Rec(ord, "我的", c.Id, user: "sales01");

        var refs = Body(await Delete(c, "sales01")).GetProperty("references").EnumerateArray()
            .ToDictionary(x => x.GetProperty("recordId").GetInt64(), x => x.GetProperty("title"));

        refs.Values.Select(t => t.ValueKind == JsonValueKind.Null ? null : t.GetString())
            .Should().BeEquivalentTo([null, "我的"]);
    }

    [Fact]
    public async Task Delete_Restrict_MoreThan10_ListCapped()
    {
        var cust = await NewModule("cust");
        var ord = await NewModule("ord", "cust");
        var c = await Rec(cust, "客戶一");
        for (var i = 0; i < 12; i++) await Rec(ord, $"訂單{i}", c.Id);

        var body = Body(await Delete(c));

        body.GetProperty("references").GetArrayLength().Should().Be(10);
        body.GetProperty("total").GetInt32().Should().Be(12);
    }

    [Fact]
    public async Task Delete_RestrictSourceDeleted_NotBlocking()
    {
        var cust = await NewModule("cust");
        var ord = await NewModule("ord", "cust");
        var c = await Rec(cust, "客戶一");
        var o = await Rec(ord, "訂單一", c.Id);
        (await Delete(o)).IsSuccess.Should().BeTrue();

        (await Delete(c)).IsSuccess.Should().BeTrue();
        (await Fresh(c.Id)).DeleteFlag.Should().BeTrue();
    }

    [Fact]
    public async Task Delete_RestrictFieldInactive_NotBlocking()
    {
        var cust = await NewModule("cust");
        var ord = await NewModule("ord", "cust");
        var c = await Rec(cust, "客戶一");
        await Rec(ord, "訂單一", c.Id);
        _user.UserId = "admin";
        (await Send(new SetFieldStatusRequest(ord.RefFieldId, FieldStatus.Inactive))).IsSuccess.Should().BeTrue();

        (await Delete(c)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Delete_Restrict_SourceModuleInactive_StillBlocks()
    {
        var cust = await NewModule("cust");
        var ord = await NewModule("ord", "cust");
        var c = await Rec(cust, "客戶一");
        await Rec(ord, "訂單一", c.Id);
        _user.UserId = "admin";
        (await Send(new DeactivateModuleRequest("ord"))).IsSuccess.Should().BeTrue();

        var r = await Delete(c);

        r.IsConflict.Should().BeTrue();
        r.Message.Should().Be(Blocked);
        (await Fresh(c.Id)).DeleteFlag.Should().BeFalse();
    }

    [Fact]
    public async Task Delete_SetNull_ClearsValueAndIndex()
    {
        var cust = await NewModule("cust");
        var ord = await NewModule("ord", "cust", ReferenceOnDelete.SetNull);
        var c = await Rec(cust, "客戶一");
        var o = await Rec(ord, "訂單一", c.Id);
        var before = await Fresh(o.Id);
        (await _db.Context.Set<RecordIndex>().AnyAsync(x => x.RecordId == o.Id && x.FieldId == ord.RefFieldId)).Should().BeTrue();

        (await Delete(c)).IsSuccess.Should().BeTrue();

        var after = await Fresh(o.Id);
        after.DeleteFlag.Should().BeFalse();
        JsonDocument.Parse(after.ValuesJson).RootElement.TryGetProperty(ord.RefKey, out _).Should().BeFalse();
        JsonDocument.Parse(after.ValuesJson).RootElement.GetProperty(ord.NameKey).GetString().Should().Be("訂單一");
        (await _db.Context.Set<RecordIndex>().AsNoTracking().AnyAsync(x => x.RecordId == o.Id && x.FieldId == ord.RefFieldId)).Should().BeFalse();
        after.RowVersion.Should().NotBe(before.RowVersion);
        after.ModifiedTime.Should().Be(new DateTime(2026, 11, 4, 9, 0, 0));
        (await Fresh(c.Id)).DeleteFlag.Should().BeTrue();
    }

    [Fact]
    public async Task Delete_Cascade_DeletesSources()
    {
        var cust = await NewModule("cust");
        var ord = await NewModule("ord", "cust", ReferenceOnDelete.Cascade);
        var c = await Rec(cust, "客戶一");
        var other = await Rec(cust, "客戶二");
        var o1 = await Rec(ord, "訂單一", c.Id);
        var o2 = await Rec(ord, "訂單二", c.Id);
        var o3 = await Rec(ord, "訂單三", other.Id);
        var before = await Fresh(o1.Id);

        (await Delete(c)).IsSuccess.Should().BeTrue();

        (await Fresh(c.Id)).DeleteFlag.Should().BeTrue();
        var after = await Fresh(o1.Id);
        after.DeleteFlag.Should().BeTrue();
        after.RowVersion.Should().NotBe(before.RowVersion);
        after.ModifiedTime.Should().Be(new DateTime(2026, 11, 4, 9, 0, 0));
        (await Fresh(o2.Id)).DeleteFlag.Should().BeTrue();
        (await Fresh(o3.Id)).DeleteFlag.Should().BeFalse();
    }

    [Fact]
    public async Task Delete_CascadeChainHitsRestrict_NothingChanged()
    {
        var a = await NewModule("moda");
        var b = await NewModule("modb", "moda", ReferenceOnDelete.Cascade);
        var c = await NewModule("modc", "modb"); // restrict
        var a1 = await Rec(a, "A1");
        var b1 = await Rec(b, "B1", a1.Id);
        var c1 = await Rec(c, "C1", b1.Id);
        var before = new[] { await Fresh(a1.Id), await Fresh(b1.Id), await Fresh(c1.Id) };

        var r = await Delete(a1);

        r.IsConflict.Should().BeTrue();
        r.Message.Should().Be(Blocked);
        var body = Body(r);
        body.GetProperty("total").GetInt32().Should().Be(1);
        body.GetProperty("references")[0].GetProperty("recordId").GetInt64().Should().Be(c1.Id);
        var after = new[] { await Fresh(a1.Id), await Fresh(b1.Id), await Fresh(c1.Id) };
        after.Should().OnlyContain(x => !x.DeleteFlag);
        after.Select(x => (x.RowVersion, x.ValuesJson, x.ModifiedTime))
            .Should().Equal(before.Select(x => (x.RowVersion, x.ValuesJson, x.ModifiedTime)));
    }

    [Fact]
    public async Task Delete_CascadeCycle_Terminates()
    {
        var x = await NewModule("modx");
        var y = await NewModule("mody", "modx", ReferenceOnDelete.Cascade);
        var (xRefId, xRefKey) = await AddRef("modx", "mody", ReferenceOnDelete.Cascade);
        x = x with { RefKey = xRefKey, RefFieldId = xRefId };
        var x1 = await Rec(x, "X1");
        var y1 = await Rec(y, "Y1", x1.Id);
        var x2 = await Rec(x, "X2", y1.Id); // x1 ← y1 ← x2 ← y2，再讓 x1 指向 y2 成環
        var y2 = await Rec(y, "Y2", x2.Id);
        _user.UserId = "admin";
        var upd = await Send(new UpdateRecordRequest(x1.Id, Vals((x.NameKey, "X1"), (xRefKey, y2.Id)), (await Fresh(x1.Id)).RowVersion));
        upd.IsSuccess.Should().BeTrue(upd.Message + string.Join(";", upd.Errors.Select(e => e.Key + e.Value[0])));

        var planner = _sp.CreateScope().ServiceProvider.GetRequiredService<ReferenceDeletePlanner>();
        var root = await _db.Context.Set<Record>().SingleAsync(r => r.Id == x1.Id);
        var module = await _db.Context.Set<Module>().SingleAsync(m => m.Key == "modx");
        var plan = await planner.PlanAsync(root, module, CancellationToken.None);
        plan.DepthExceeded.Should().BeFalse();
        plan.Blocking.Should().BeEmpty();
        plan.ToDelete.Select(r => r.Id).Should().BeEquivalentTo([y1.Id, x2.Id, y2.Id]);
        plan.ToDelete.Select(r => r.Id).Should().OnlyHaveUniqueItems();

        (await Delete(x1)).IsSuccess.Should().BeTrue();

        foreach (var id in new[] { x1.Id, y1.Id, x2.Id, y2.Id })
            (await Fresh(id)).DeleteFlag.Should().BeTrue();
    }

    [Fact]
    public async Task Delete_SelfReferenceCascade_DeletesChain()
    {
        var equip = await NewModule("equip", "equip", ReferenceOnDelete.Cascade); // 上層設備
        var e0 = await Rec(equip, "廠房");
        var e1 = await Rec(equip, "產線", e0.Id);
        var e2 = await Rec(equip, "機台", e1.Id);
        var unrelated = await Rec(equip, "倉庫");

        (await Delete(e0)).IsSuccess.Should().BeTrue();

        foreach (var id in new[] { e0.Id, e1.Id, e2.Id })
            (await Fresh(id)).DeleteFlag.Should().BeTrue();
        (await Fresh(unrelated.Id)).DeleteFlag.Should().BeFalse();
    }

    [Fact]
    public async Task Delete_CascadeDepthOver5_Rejected()
    {
        var equip = await NewModule("equip", "equip", ReferenceOnDelete.Cascade);
        var chain = new List<DynamicRecordResponse> { await Rec(equip, "e0") };
        for (var i = 1; i < 7; i++) chain.Add(await Rec(equip, $"e{i}", chain[^1].Id)); // e6 在第 6 層
        var before = new List<Record>();
        foreach (var e in chain) before.Add(await Fresh(e.Id));

        var r = await Delete(chain[0]);

        r.IsConflict.Should().BeTrue();
        r.Message.Should().Be(TooDeep);
        r.Data.Should().BeNull();
        for (var i = 0; i < chain.Count; i++)
        {
            var now = await Fresh(chain[i].Id);
            now.DeleteFlag.Should().BeFalse();
            now.RowVersion.Should().Be(before[i].RowVersion);
        }

        // 邊界：從 e1 刪，e6 在第 5 層，可以刪
        (await Delete(chain[1])).IsSuccess.Should().BeTrue();
        (await Fresh(chain[6].Id)).DeleteFlag.Should().BeTrue();
        (await Fresh(chain[0].Id)).DeleteFlag.Should().BeFalse();
    }

    [Fact]
    public async Task Delete_TwoRestrictFieldsSameRecord_BlockingDeduped()
    {
        var cust = await NewModule("cust");
        var ord = await NewModule("ord", "cust");
        var block = (await Detail("ord")).Blocks[0].Id;
        var added = await Send(new AddFieldRequest("ord", block, FieldType.Reference, Text("參照二"),
            new FieldConfig { RefTarget = new ReferenceTarget(ReferenceTarget.ModuleKind, "cust") }));
        added.IsCreated.Should().BeTrue();
        var fields = (await Detail("ord")).Blocks.SelectMany(b => b.Fields).Where(f => f.Type == FieldType.Reference).ToList();
        var c = await Rec(cust, "客戶一");
        _user.UserId = "admin";
        var create = await Send(new CreateRecordRequest("ord", Vals((ord.NameKey, "訂單一"), (fields[0].Key, c.Id), (fields[1].Key, c.Id))));
        create.IsCreated.Should().BeTrue();

        var r = await Delete(c);

        r.IsConflict.Should().BeTrue();
        var body = Body(r);
        body.GetProperty("total").GetInt32().Should().Be(1);
        body.GetProperty("references").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Delete_DepthExceededAndRestrictBlocking_ReportsTooDeep()
    {
        var equip = await NewModule("equip", "equip", ReferenceOnDelete.Cascade);
        var ord = await NewModule("ord", "equip"); // restrict
        var chain = new List<DynamicRecordResponse> { await Rec(equip, "e0") };
        for (var i = 1; i < 7; i++) chain.Add(await Rec(equip, $"e{i}", chain[^1].Id));
        await Rec(ord, "訂單", chain[0].Id);

        var r = await Delete(chain[0]);

        r.IsConflict.Should().BeTrue();
        r.Message.Should().Be(TooDeep);
    }

    [Fact]
    public async Task Delete_CascadeIgnoresSourceDeletePermission()
    {
        var cust = await NewModule("cust");
        var ord = await NewModule("ord", "cust", ReferenceOnDelete.Cascade);
        await GrantSales(cust, DataScope.All);
        await GrantSales(ord, DataScope.All, delete: false);
        var c = await Rec(cust, "客戶一");
        var o = await Rec(ord, "訂單一", c.Id);

        (await Delete(c, "sales01")).IsSuccess.Should().BeTrue();

        (await Fresh(o.Id)).DeleteFlag.Should().BeTrue();
    }
}
