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
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetPagedRecords;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;
using DynamicModules.Application.Dynamic.References;
using DynamicModules.ApplicationTests.Persistence;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Entities.Mock;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Persistence;
using DynamicModules.Persistence.Seed;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.ApplicationTests.Dynamic;

public class ReferenceReadTests : IAsyncLifetime
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

    private async Task<(Mod target, Mod source)> ModuleRefPair()
    {
        var target = await NewModule("cust");
        var source = await NewModule("ord", new ReferenceTarget(ReferenceTarget.ModuleKind, "cust"));
        return (target, source);
    }

    private async Task SetMoldDeleted(string no)
    {
        (await _db.Context.Set<MockMold>().SingleAsync(x => x.MoldNo == no)).DeleteFlag = true;
        await _db.Context.SaveChangesAsync();
    }

    private async Task Hide(long fieldId, string group = "sales")
    {
        var g = await _db.Context.Set<PermissionGroup>().SingleAsync(x => x.Key == group);
        _db.Context.Set<FieldPermission>().Add(new FieldPermission { FieldId = fieldId, GroupId = g.Id, Access = FieldAccess.Hidden });
        await _db.Context.SaveChangesAsync();
    }

    private async Task<DynamicRecordResponse> Get(string user, long id)
    {
        _user.UserId = user;
        var r = await Send(new GetRecordRequest(id));
        r.IsSuccess.Should().BeTrue(r.Message);
        return (DynamicRecordResponse)r.Data!;
    }

    private async Task<long> SourceRec(Mod source, string title, object? refValue) =>
        ((DynamicRecordResponse)(await Create("admin", source, title, refValue)).Data!).Id;

    [Fact]
    public async Task Get_RefOk_HasTitle()
    {
        var (target, source) = await ModuleRefPair();
        var c = await Rec("admin", target, "客戶甲");
        var id = await SourceRec(source, "訂單", c.Id);

        var info = (await Get("admin", id)).References[source.RefKey];

        info.State.Should().Be("ok");
        info.Title.Should().Be("客戶甲");
        info.Key.Should().Be(c.Id.ToString());
    }

    [Fact]
    public async Task Get_RefTargetDeleted_Missing()
    {
        var (target, source) = await ModuleRefPair();
        var c = await Rec("admin", target, "客戶甲");
        var id = await SourceRec(source, "訂單", c.Id);
        // 預設 Restrict 會擋刪除（Task 6），這裡直接在 DB 軟刪目標模擬資料已刪
        (await _db.Context.Set<Record>().SingleAsync(r => r.Id == c.Id)).DeleteFlag = true;
        await _db.Context.SaveChangesAsync();

        var info = (await Get("admin", id)).References[source.RefKey];

        info.State.Should().Be("missing");
        info.Title.Should().BeNull();
    }

    [Fact]
    public async Task Get_EntityGone_Missing()
    {
        var source = await NewModule("plan", new ReferenceTarget(ReferenceTarget.EntityKind, "design.mold"));
        var id = await SourceRec(source, "計畫", "M-0003");
        (await Get("admin", id)).References[source.RefKey].State.Should().Be("ok");
        await SetMoldDeleted("M-0003");

        var info = (await Get("admin", id)).References[source.RefKey];

        info.State.Should().Be("missing");
        info.Title.Should().BeNull();
    }

    [Fact]
    public async Task Get_RefTargetOutOfScope_Restricted()
    {
        var (target, source) = await ModuleRefPair();
        await GrantSales(target, DataScope.Own);
        await GrantSales(source, DataScope.All);
        var theirs = await Rec("sales02", target, "別人的客戶");
        var id = await SourceRec(source, "訂單", theirs.Id);

        var info = (await Get("sales01", id)).References[source.RefKey];

        info.State.Should().Be("restricted");
        info.Title.Should().BeNull();
    }

    [Fact]
    public async Task Get_RefTargetTitleHidden_Restricted()
    {
        var (target, source) = await ModuleRefPair();
        await GrantSales(target, DataScope.All);
        await GrantSales(source, DataScope.All);
        var c = await Rec("admin", target, "客戶甲");
        var id = await SourceRec(source, "訂單", c.Id);
        await Hide((await Detail("cust")).TitleFieldId!.Value);

        var info = (await Get("sales01", id)).References[source.RefKey];

        info.State.Should().Be("restricted");
        info.Title.Should().BeNull();
    }

    [Fact]
    public async Task Get_RefTargetNoRead_Restricted()
    {
        var (target, source) = await ModuleRefPair();
        await GrantSales(target, DataScope.All, read: false);
        await GrantSales(source, DataScope.All);
        var c = await Rec("admin", target, "客戶甲");
        var id = await SourceRec(source, "訂單", c.Id);

        var info = (await Get("sales01", id)).References[source.RefKey];

        info.State.Should().Be("restricted");
        info.Title.Should().BeNull();
    }

    [Fact]
    public async Task Get_RefFieldHidden_NoReferenceEntry()
    {
        var (target, source) = await ModuleRefPair();
        await GrantSales(target, DataScope.All);
        await GrantSales(source, DataScope.All);
        var c = await Rec("admin", target, "客戶甲");
        var id = await SourceRec(source, "訂單", c.Id);
        await Hide(source.RefFieldId);

        var got = await Get("sales01", id);

        got.References.Should().BeEmpty();
        got.Values.Should().NotContainKey(source.RefKey);
    }

    [Fact]
    public async Task Paged_MixedTargets_AllResolved()
    {
        var c1 = await NewModule("cust1");
        var c2 = await NewModule("cust2");
        var m1 = await Rec("admin", c1, "客戶一");
        var m2 = await Rec("admin", c2, "客戶二");
        await Send(new CreateModuleRequest("ord", Text("ord"), null, null));
        var block = (await Detail("ord")).Blocks[0].Id;
        foreach (var t in new[] { new ReferenceTarget(ReferenceTarget.ModuleKind, "cust1"), new ReferenceTarget(ReferenceTarget.ModuleKind, "cust2"),
                     new ReferenceTarget(ReferenceTarget.EntityKind, "design.mold") })
            (await Send(new AddFieldRequest("ord", block, FieldType.Reference, Text("參照"), new FieldConfig { RefTarget = t }))).IsCreated.Should().BeTrue();
        await Send(new ActivateModuleRequest("ord"));
        var detail = await Detail("ord");
        var allFields = detail.Blocks.SelectMany(b => b.Fields).ToList();
        var refs = allFields.Where(f => f.Type == FieldType.Reference).OrderBy(f => f.Id).ToList();
        var nameKey = allFields.Single(f => f.Id == detail.TitleFieldId).Key;
        for (var i = 0; i < 20; i++)
            (await Send(new CreateRecordRequest("ord", Vals((nameKey, $"o{i:00}"), (refs[0].Key, m1.Id), (refs[1].Key, m2.Id), (refs[2].Key, i % 2 == 0 ? "M-0001" : "M-0002")))))
                .IsCreated.Should().BeTrue();

        _user.UserId = "admin";
        var page = (PagedResponse<DynamicRecordResponse>)(await Send(new GetPagedRecordsRequest("ord", 1, 20, null, [], [], null))).Data!;

        page.Items.Should().HaveCount(20);
        foreach (var item in page.Items)
        {
            item.References[refs[0].Key].Should().Be(new ReferenceInfo(m1.Id.ToString(), "客戶一", "ok"));
            item.References[refs[1].Key].Should().Be(new ReferenceInfo(m2.Id.ToString(), "客戶二", "ok"));
            var mold = item.References[refs[2].Key];
            mold.State.Should().Be("ok");
            mold.Title.Should().StartWith(item.Values[refs[2].Key]!.Value.GetString());
        }
    }
}
