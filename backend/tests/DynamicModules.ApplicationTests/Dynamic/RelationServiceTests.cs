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
using DynamicModules.Application.Dynamic.RecordService.Queries.GetPagedRecords;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;
using DynamicModules.Application.Dynamic.RelationService.Data;
using DynamicModules.Application.Dynamic.RelationService.Queries.GetRelatedPaged;
using DynamicModules.Application.Dynamic.RelationService.Queries.GetRelatedTabs;
using DynamicModules.Application.Dynamic.FieldService.Commands.SetFieldStatus;
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

namespace DynamicModules.ApplicationTests.Dynamic;

public class RelationServiceTests : IAsyncLifetime
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

    private async Task<IReadOnlyList<RelatedTab>> Tabs(string user, long id)
    {
        _user.UserId = user;
        var r = await Send(new GetRelatedTabsRequest(id));
        r.IsSuccess.Should().BeTrue(r.Message);
        return (IReadOnlyList<RelatedTab>)r.Data!;
    }

    private async Task<ServiceResult> Paged(string user, long id, long fieldId)
    {
        _user.UserId = user;
        return await Send(new GetRelatedPagedRequest(id, fieldId, 1, 20));
    }

    private async Task<(Mod target, Mod a, Mod b)> TwoSources()
    {
        var target = await NewModule("app");
        var a = await NewModule("repair", new ReferenceTarget(ReferenceTarget.ModuleKind, "app"));
        var b = await NewModule("maint", new ReferenceTarget(ReferenceTarget.ModuleKind, "app"));
        return (target, a, b);
    }

    [Fact]
    public async Task Related_TwoIncomingFields_TwoTabs()
    {
        var (target, a, _) = await TwoSources();
        await GrantSales(target, DataScope.All);
        await GrantSales(a, DataScope.All);
        var rec = await Rec("admin", target, "申請一");

        var tabs = await Tabs("sales01", rec.Id);

        tabs.Select(t => t.FieldId).Should().BeEquivalentTo([a.RefFieldId]); // sales 沒有 maint 的 Read
        var admin = await Tabs("admin", rec.Id);
        admin.Should().HaveCount(2);
        admin.Should().OnlyContain(t => t.CanAdd);
        admin.Single(t => t.FieldId == a.RefFieldId).Should().Match<RelatedTab>(t => t.ModuleKey == "repair" && t.FieldKey == a.RefKey);
    }

    [Fact]
    public async Task Related_SourceFieldReadOnly_CanAddFalse()
    {
        var (target, a, _) = await TwoSources();
        await GrantSales(target, DataScope.All);
        await GrantSales(a, DataScope.All);
        var rec = await Rec("admin", target, "申請一");
        var g = await _db.Context.Set<PermissionGroup>().SingleAsync(x => x.Key == "sales");
        _db.Context.Set<FieldPermission>().Add(new FieldPermission { FieldId = a.RefFieldId, GroupId = g.Id, Access = FieldAccess.ReadOnly });
        await _db.Context.SaveChangesAsync();

        (await Tabs("sales01", rec.Id)).Single().CanAdd.Should().BeFalse();
    }

    [Fact]
    public async Task Related_SourceModuleInactive_NoTab()
    {
        var (target, a, _) = await TwoSources();
        var rec = await Rec("admin", target, "申請一");
        _user.UserId = "admin";
        (await Send(new DeactivateModuleRequest("maint"))).IsSuccess.Should().BeTrue();

        (await Tabs("admin", rec.Id)).Select(t => t.FieldId).Should().BeEquivalentTo([a.RefFieldId]);
    }

    [Fact]
    public async Task Related_SourceFieldInactive_NoTab()
    {
        var (target, a, b) = await TwoSources();
        var rec = await Rec("admin", target, "申請一");
        _user.UserId = "admin";
        (await Send(new SetFieldStatusRequest(b.RefFieldId, FieldStatus.Inactive))).IsSuccess.Should().BeTrue();

        (await Tabs("admin", rec.Id)).Select(t => t.FieldId).Should().BeEquivalentTo([a.RefFieldId]);
    }

    [Fact]
    public async Task Related_FieldHidden_NoTab()
    {
        var (target, a, _) = await TwoSources();
        await GrantSales(target, DataScope.All);
        await GrantSales(a, DataScope.All);
        var g = await _db.Context.Set<PermissionGroup>().SingleAsync(x => x.Key == "floor");
        _db.Context.Set<GroupProgramPrivilege>().Add(new GroupProgramPrivilege
            { GroupId = g.Id, ProgramCode = target.ProgramCode!, CanRead = true });
        _db.Context.Set<GroupProgramPrivilege>().Add(new GroupProgramPrivilege
            { GroupId = g.Id, ProgramCode = a.ProgramCode!, CanRead = true });
        _db.Context.Set<ModuleDataScope>().Add(new ModuleDataScope { ModuleId = (await Detail("app")).Id, GroupId = g.Id, Scope = DataScope.All });
        await _db.Context.SaveChangesAsync();
        var rec = await Rec("admin", target, "申請一");
        (await Tabs("floor01", rec.Id)).Should().ContainSingle();
        await Hide(a.RefFieldId, "floor");

        (await Tabs("floor01", rec.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Related_NoSourceRead_NoTab()
    {
        var (target, a, _) = await TwoSources();
        await GrantSales(target, DataScope.All);
        await GrantSales(a, DataScope.All, read: false);
        var rec = await Rec("admin", target, "申請一");

        (await Tabs("sales01", rec.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Related_RecordNotReadable_NotFound()
    {
        var (target, a, _) = await TwoSources();
        await GrantSales(target, DataScope.Own);
        await GrantSales(a, DataScope.All);
        var theirs = await Rec("sales02", target, "別人的");

        _user.UserId = "sales01";
        var r = await Send(new GetRelatedTabsRequest(theirs.Id));
        r.IsNotFound.Should().BeTrue();
        r.Message.Should().Be("記錄不存在");
        (await Paged("sales01", theirs.Id, a.RefFieldId)).Message.Should().Be("記錄不存在");
    }

    [Fact]
    public async Task RelatedPaged_ListsSources_DescById()
    {
        var (target, a, _) = await TwoSources();
        var rec = await Rec("admin", target, "申請一");
        var other = await Rec("admin", target, "申請二");
        var s1 = await SourceRec(a, "r1", rec.Id);
        var s2 = await SourceRec(a, "r2", rec.Id);
        await SourceRec(a, "r3", other.Id);

        var r = await Paged("admin", rec.Id, a.RefFieldId);

        r.IsSuccess.Should().BeTrue(r.Message);
        var page = (PagedResponse<DynamicRecordResponse>)r.Data!;
        page.TotalCount.Should().Be(2);
        page.Items.Select(i => i.Id).Should().Equal(s2, s1);
        page.Items[0].References[a.RefKey].Should().Be(new ReferenceInfo(rec.Id.ToString(), "申請一", "ok"));
    }

    [Fact]
    public async Task RelatedPaged_ExcludesDeleted()
    {
        var (target, a, _) = await TwoSources();
        var rec = await Rec("admin", target, "申請一");
        var s1 = ((DynamicRecordResponse)(await Create("admin", a, "r1", rec.Id)).Data!);
        await SourceRec(a, "r2", rec.Id);
        (await Send(new DeleteRecordRequest(s1.Id, s1.RowVersion))).IsSuccess.Should().BeTrue();

        var page = (PagedResponse<DynamicRecordResponse>)(await Paged("admin", rec.Id, a.RefFieldId)).Data!;

        page.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task RelatedPaged_OutOfScope_Excluded()
    {
        var (target, a, _) = await TwoSources();
        await GrantSales(target, DataScope.All);
        await GrantSales(a, DataScope.Own);
        var rec = await Rec("admin", target, "申請一");
        var m = ((DynamicRecordResponse)(await Create("sales01", a, "mine", rec.Id)).Data!);
        await Create("sales02", a, "theirs", rec.Id);

        var page = (PagedResponse<DynamicRecordResponse>)(await Paged("sales01", rec.Id, a.RefFieldId)).Data!;

        page.Items.Select(i => i.Id).Should().Equal(m.Id);
    }

    [Fact]
    public async Task RelatedPaged_FieldHidden_NotFound()
    {
        var (target, a, _) = await TwoSources();
        await GrantSales(target, DataScope.All);
        await GrantSales(a, DataScope.All);
        var rec = await Rec("admin", target, "申請一");
        await Hide(a.RefFieldId);

        var r = await Paged("sales01", rec.Id, a.RefFieldId);

        r.IsNotFound.Should().BeTrue();
        r.Message.Should().Be("關聯不存在");
    }

    [Fact]
    public async Task RelatedPaged_FieldNotTargetingModule_NotFound()
    {
        var (target, a, _) = await TwoSources();
        var rec = await Rec("admin", target, "申請一");
        var elsewhere = await NewModule("cust");
        var unrelated = await NewModule("ord", new ReferenceTarget(ReferenceTarget.ModuleKind, "cust"));

        var r = await Paged("admin", rec.Id, unrelated.RefFieldId);

        r.IsNotFound.Should().BeTrue();
        r.Message.Should().Be("關聯不存在");
        _ = (elsewhere, a);
    }
}
