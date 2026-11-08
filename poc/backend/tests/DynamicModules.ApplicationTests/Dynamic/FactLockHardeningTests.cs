using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.EventService;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.PermissionService.Commands.SaveModulePermissions;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.DeleteRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>事實鎖的漏洞回歸（第一輪實作審查 a）：同 key 的他人請求、連帶刪除明細、明細公式重算、彙總連動公式。</summary>
public class FactLockHardeningTests : EffectsTestBase
{
    protected override bool ScopePerRequest => true;

    private static Dictionary<string, JsonElement?> Vals(params (long Field, string Json)[] v) =>
        v.ToDictionary(x => K(x.Field), x => (JsonElement?)J(x.Json));

    private async Task<ServiceResult> Run(IRequest<ServiceResult> request)
    {
        var result = await Send(request);
        if (!result.IsSuccess) Db.Context.ChangeTracker.Clear();
        return result;
    }

    private async Task<DynamicRecordResponse> MustCreate(string module, Dictionary<string, JsonElement?> values,
        Dictionary<string, List<DetailRowInput>>? details = null)
    {
        var r = await Send(new CreateRecordRequest(module, values, Details: details));
        r.IsCreated.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        return (DynamicRecordResponse)r.Data!;
    }

    private async Task<DynamicRecordResponse> Reload(long id) => (DynamicRecordResponse)(await Send(new GetRecordRequest(id))).Data!;

    private IReadOnlyDictionary<string, JsonElement?> Stored(long id) =>
        RecordValueProcessor.Parse(Db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().AsNoTracking().Single(r => r.Id == id).ValuesJson);

    private async Task<long> TitleOf(string module) => (await Detail(module)).TitleFieldId!.Value;

    private int CountOf(string module) =>
        Db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().AsNoTracking()
            .Count(r => !r.DeleteFlag && r.ModuleId == Db.Context.Set<Module>().Single(m => m.Key == module).Id);

    [Fact]
    public async Task A_request_reusing_another_users_idempotency_key_cannot_edit_a_booked_source()
    {
        await NewModule("flk-master", activate: true);
        await NewModule("flk-src");
        var masterRef = await MustAdd("flk-src", FieldType.Reference, RefTo("flk-master", onDelete: ReferenceOnDelete.SetNull), "主檔");
        await Send(new ActivateModuleRequest("flk-src"));
        await NewModule("flk-log", activate: true);
        (await Send(new CreateEventRequest(Event("flk-post", "flk-src", new EventTrigger("create", null, null, null),
            Create("post", "flk-log", null, Const(await TitleOf("flk-log"), "\"x\"")))))).IsCreated.Should().BeTrue();
        foreach (var m in new[] { "flk-master", "flk-src" })   // 第二位使用者要有刪除、讀取權限，不然被擋的原因是權限而不是事實鎖
            (await Send(new SaveModulePermissionsRequest(m, new ModulePermissionsDto([],
                [new UserPermissionDto("sales01", new PrivilegesDto(true, true, true, true, false, false, false))], [], [])))).IsSuccess.Should().BeTrue();

        var masterTitle = await TitleOf("flk-master");
        var srcTitle = await TitleOf("flk-src");
        As("sales01");   // 主檔屬於第二位使用者（資料範圍只看自己的）
        var master = await MustCreate("flk-master", Vals((masterTitle, "\"m\"")));
        As("admin");
        ClientKey = "book0001";
        var src = await MustCreate("flk-src", Vals((srcTitle, "\"s\""), (masterRef.Id, master.Id.ToString())));

        As("sales01");
        var delete = await Run(new DeleteRecordRequest(master.Id, master.RowVersion));   // 同一把 key，SetNull 會清掉已記帳來源的參照

        delete.IsConflict.Should().BeTrue(delete.Message);
        Stored(src.Id).Should().ContainKey(K(masterRef.Id));
    }
}
