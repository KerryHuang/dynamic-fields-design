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
using DynamicModules.Application.Dynamic.ModuleService.Commands.UpdateModule;
using DynamicModules.Application.Dynamic.ModuleService.Queries.GetModule;
using DynamicModules.Application.Dynamic.SchemaService.Data;
using DynamicModules.Application.Dynamic.SchemaService.Queries.GetModuleSchema;
using DynamicModules.Application.Dynamic.RecordService.Commands.DeleteRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetPagedRecords;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecordByHost;
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

public class ExtensionModuleTests : IAsyncLifetime
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

    private async Task<string> NewExt(string key, string host = "design.mold", bool activate = true)
    {
        _user.UserId = "admin";
        var r = await Send(new CreateModuleRequest(key, Text(key), null, null, ModuleKind.Extension, host));
        r.IsCreated.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        var m = await Detail(key);
        await Send(new AddFieldRequest(key, m.Blocks[0].Id, FieldType.Text, Text("備註"), new FieldConfig()));
        if (activate) (await Send(new ActivateModuleRequest(key))).IsSuccess.Should().BeTrue();
        return (await Detail(key)).Blocks.SelectMany(b => b.Fields).Single().Key;
    }

    private Task<ServiceResult> CreateExtRec(string key, string noteKey, string? hostKey, string note = "x")
        => Send(new CreateRecordRequest(key, Vals((noteKey, note)), hostKey));

    private static string Err(ServiceResult r, string key) => r.Errors[key][0];

    [Fact]
    public async Task CreateModule_Extension_NoTitleField()
    {
        await NewExt("ext1", activate: false);
        var m = await Detail("ext1");
        m.TitleFieldId.Should().BeNull();
        m.Blocks.Should().HaveCount(1);
        m.Kind.Should().Be(ModuleKind.Extension);
        m.HostEntityKey.Should().Be("design.mold");
    }

    [Fact]
    public async Task CreateModule_Extension_NoHost_Rejected()
    {
        var r = await Send(new CreateModuleRequest("ext2", Text("x"), null, null, ModuleKind.Extension));
        Err(r, "HostEntityKey").Should().Be("擴充模組必須指定既有實體");
    }

    [Fact]
    public async Task CreateModule_Extension_UnknownHost_Rejected()
    {
        var r = await Send(new CreateModuleRequest("ext3", Text("x"), null, null, ModuleKind.Extension, "no.such"));
        Err(r, "HostEntityKey").Should().Be("擴充模組必須指定既有實體");
    }

    [Fact]
    public async Task CreateModule_Extension_Duplicate_Conflict()
    {
        await NewExt("ext4");
        var r = await Send(new CreateModuleRequest("ext5", Text("x"), null, null, ModuleKind.Extension, "design.mold"));
        r.IsConflict.Should().BeTrue();
        r.Message.Should().Be("這個既有實體已經有擴充模組");
    }

    [Fact]
    public async Task CreateModule_Standalone_HostEntityKeyIgnored()
    {
        await Send(new CreateModuleRequest("std1", Text("x"), null, null, ModuleKind.Standalone, "design.mold"));
        (await Detail("std1")).HostEntityKey.Should().BeNull();
    }

    [Fact]
    public async Task UpdateModule_Extension_SetTitle_Rejected()
    {
        await NewExt("ext6");
        var m = await Detail("ext6");
        var r = await Send(new UpdateModuleRequest("ext6", Text("x"), null, null, m.Blocks[0].Fields[0].Id));
        Err(r, "TitleFieldId").Should().Be("擴充模組的標題取自既有資料");
    }

    [Fact]
    public async Task UpdateModule_Extension_ActiveWithoutTitle_Succeeds()
    {
        await NewExt("ext7");
        var r = await Send(new UpdateModuleRequest("ext7", Text("改"), null, null, null));
        r.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CreateRecord_Extension_TitleIsHostSnapshot()
    {
        var note = await NewExt("ext8");
        var r = await CreateExtRec("ext8", note, "M-0001");
        r.IsCreated.Should().BeTrue(r.Message);
        var rec = (DynamicRecordResponse)r.Data!;
        rec.Title.Should().Be("M-0001 外殼上蓋模");
        rec.HostKey.Should().Be("M-0001");

        var upd = await Send(new UpdateRecordRequest(rec.Id, Vals((note, "改")), rec.RowVersion));
        var updated = (DynamicRecordResponse)upd.Data!;
        updated.Title.Should().Be("M-0001 外殼上蓋模");
        updated.HostKey.Should().Be("M-0001");
    }

    [Fact]
    public async Task CreateRecord_Extension_NoHostKey_Rejected()
    {
        var note = await NewExt("ext9");
        Err(await CreateExtRec("ext9", note, null), "hostKey").Should().Be("擴充模組的記錄必須指定既有資料");
    }

    [Fact]
    public async Task CreateRecord_Extension_UnknownHost_Rejected()
    {
        var note = await NewExt("ext10");
        Err(await CreateExtRec("ext10", note, "M-9999"), "hostKey").Should().Be("既有資料不存在");
    }

    [Fact]
    public async Task CreateRecord_Extension_DuplicateHost_Conflict()
    {
        var note = await NewExt("ext11");
        (await CreateExtRec("ext11", note, "M-0001")).IsCreated.Should().BeTrue();
        var r = await CreateExtRec("ext11", note, "M-0001");
        r.IsConflict.Should().BeTrue();
        r.Message.Should().Be("這筆既有資料已經有擴充資料");
    }

    [Fact]
    public async Task CreateRecord_Extension_AfterDelete_Allowed()
    {
        var note = await NewExt("ext12");
        var first = (DynamicRecordResponse)(await CreateExtRec("ext12", note, "M-0001")).Data!;
        (await Send(new DeleteRecordRequest(first.Id, first.RowVersion))).IsSuccess.Should().BeTrue();
        (await CreateExtRec("ext12", note, "M-0001")).IsCreated.Should().BeTrue();
    }

    [Fact]
    public async Task GetByHost_Found()
    {
        var note = await NewExt("ext13");
        var created = (DynamicRecordResponse)(await CreateExtRec("ext13", note, "M-0002")).Data!;
        var r = await Send(new GetRecordByHostRequest("ext13", "M-0002"));
        ((DynamicRecordResponse)r.Data!).Id.Should().Be(created.Id);
    }

    [Fact]
    public async Task GetByHost_Missing_NotFound()
    {
        await NewExt("ext14");
        var r = await Send(new GetRecordByHostRequest("ext14", "M-0003"));
        r.IsNotFound.Should().BeTrue();
        r.Message.Should().Be("記錄不存在");
    }

    [Fact]
    public async Task GetByHost_OutOfScope_NotFound()
    {
        var note = await NewExt("ext15");
        await CreateExtRec("ext15", note, "M-0004");
        var db = _db.Context;
        var sales = await db.Set<PermissionGroup>().SingleAsync(g => g.Key == "sales");
        var module = await Detail("ext15");
        db.Set<GroupProgramPrivilege>().Add(new GroupProgramPrivilege
        {
            GroupId = sales.Id, ProgramCode = module.ProgramCode, CanRead = true, CanAdd = true, CanEdit = true, CanDelete = true, CanAmt = true
        });
        db.Set<ModuleDataScope>().Add(new ModuleDataScope { ModuleId = module.Id, GroupId = sales.Id, Scope = DataScope.Own });
        await db.SaveChangesAsync();
        _user.UserId = "sales01";
        (await Send(new GetRecordByHostRequest("ext15", "M-0004"))).IsNotFound.Should().BeTrue();
    }

    [Fact]
    public async Task Schema_ExtensionModule_HasHostEntity()
    {
        await NewExt("ext16");
        var schema = (ModuleSchemaResponse)(await Send(new GetModuleSchemaRequest("ext16"))).Data!;
        schema.Kind.Should().Be(ModuleKind.Extension);
        schema.HostEntityKey.Should().Be("design.mold");
        schema.TitleFieldKey.Should().BeNull();
    }

    [Fact]
    public async Task CreateRecord_Standalone_HostKeyIgnored()
    {
        _user.UserId = "admin";
        await Send(new CreateModuleRequest("std2", Text("x"), null, null));
        await Send(new ActivateModuleRequest("std2"));
        var m = await Detail("std2");
        var title = m.Blocks.SelectMany(b => b.Fields).Single().Key;
        var r = await Send(new CreateRecordRequest("std2", Vals((title, "a")), "M-0001"));
        ((DynamicRecordResponse)r.Data!).HostKey.Should().BeNull();
    }
}
