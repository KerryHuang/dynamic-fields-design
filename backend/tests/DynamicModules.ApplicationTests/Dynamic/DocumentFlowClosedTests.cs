using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.ConversionService.Commands.ConvertRecord;
using DynamicModules.Application.Dynamic.ConversionService.Data;
using DynamicModules.Application.Dynamic.ConversionService.Queries.GetRecordConversions;
using DynamicModules.Application.Dynamic.AppService.Commands;
using DynamicModules.Application.Dynamic.DeleteService;
using DynamicModules.Application.Dynamic.DeleteService.Commands.DeleteModule;
using DynamicModules.Application.Dynamic.DeleteService.Data;
using DynamicModules.Application.Dynamic.ModuleService.Commands.DeactivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;
using DynamicModules.Application.Dynamic.WorkflowService.Commands.SaveWorkflow;
using DynamicModules.Application.Dynamic.WorkflowService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>
/// 三項單據能力遇到 Closed 模組（應用程式關閉：資料與設定保留、可查閱、不能寫入）。
/// 規則：寫入類一律 409；讀取照常；帶值不能對 Closed 的來源 fail-open；刪除盤點要認得轉換定義。
/// </summary>
public class DocumentFlowClosedTests : ConversionTestBase
{
    private async Task Close(params string[] keys)
    {
        foreach (var key in keys)
            (await Db.Context.Set<Module>().SingleAsync(m => m.Key == key)).Status = ModuleStatus.Closed;
        await Db.Context.SaveChangesAsync();
    }

    private static string Why(ServiceResult r) => r.Message + string.Join(";", r.Errors.Select(e => e.Key + ":" + e.Value[0]));

    private Task<ServiceResult> Convert(DynamicRecordResponse src, string key, long line, decimal qty) =>
        Send(new ConvertRecordRequest(src.Id, key, null, null, [new ConvertLineInput(line, qty)]));

    private async Task<ConversionAvailability> Availability(DynamicRecordResponse src, string key)
    {
        var r = await Send(new GetRecordConversionsRequest(src.Id));
        r.IsSuccess.Should().BeTrue(Why(r));
        return ((List<ConversionAvailability>)r.Data!).Single(a => a.Key == key);
    }

    private async Task<DynamicRecordResponse> Reload(long id) => (DynamicRecordResponse)(await Send(new GetRecordRequest(id))).Data!;

    private async Task<int> RecordCount(string module) =>
        await Db.Context.Set<Record>().AsNoTracking().CountAsync(r => r.ModuleId == Db.Context.Set<Module>().Single(m => m.Key == module).Id);

    private async Task<(Chain C, DynamicRecordResponse Src, long Line)> ApprovedSource(string p)
    {
        var c = await BuildChain(p);
        await DefineOk(c, $"{p}-to");
        var (src, lines) = await NewSource(c, ("螺絲", 10, 5));
        return (c, await Approve(c, src), lines[0]);
    }

    // ───── 單據轉換 ─────

    [Fact]
    public async Task Convert_SourceClosed_Conflict_AndNothingIsWritten()
    {
        var (c, src, line) = await ApprovedSource("k");
        await Close(c.Src, c.SrcLine);
        var before = await Reload(src.Id);

        var r = await Convert(src, "k-to", line, 4);

        r.IsConflict.Should().BeTrue(Why(r));
        r.Message.Should().Be(ModuleGate.ClosedMessage);
        (await RecordCount(c.Dst)).Should().Be(0);
        (await Reload(src.Id)).RowVersion.Should().Be(before.RowVersion, "來源是關閉的，連互斥用的版本號都不能動");
    }

    [Fact]
    public async Task Convert_TargetClosed_Conflict_AndNothingIsWritten()
    {
        var (c, src, line) = await ApprovedSource("k");
        await Close(c.Dst, c.DstLine);
        var before = await Reload(src.Id);

        var r = await Convert(src, "k-to", line, 4);

        r.IsConflict.Should().BeTrue(Why(r));
        (await RecordCount(c.Dst)).Should().Be(0);
        (await Reload(src.Id)).RowVersion.Should().Be(before.RowVersion);
    }

    [Fact]
    public async Task Availability_ClosedSource_IsReadable_ButNotAllowed_AndKeepsConvertedTargets()
    {
        var (c, src, line) = await ApprovedSource("k");
        (await Convert(src, "k-to", line, 4)).IsCreated.Should().BeTrue();
        await Close(c.Src, c.SrcLine);

        var a = await Availability(src, "k-to");

        a.Allowed.Should().BeFalse();
        a.Reason.Should().Be(ModuleGate.ClosedMessage);
        a.Targets.Should().HaveCount(1, "已轉出的單據仍可查閱");
        a.Lines!.Single().Should().Match<ConversionLineStatus>(l => l.Converted == 4 && l.Remaining == 6);
    }

    [Fact]
    public async Task Availability_ClosedTarget_IsNotAllowed_AndStillListsExistingTargets()
    {
        var (c, src, line) = await ApprovedSource("k");
        (await Convert(src, "k-to", line, 4)).IsCreated.Should().BeTrue();
        await Close(c.Dst, c.DstLine);

        var a = await Availability(src, "k-to");

        a.Allowed.Should().BeFalse();
        a.Targets.Should().HaveCount(1);
    }

    // ───── 狀態機 ─────

    [Fact]
    public async Task ClosedModule_CannotAdvanceTheStatus()
    {
        var c = await BuildChain("w");
        var (src, _) = await NewSource(c, ("螺絲", 10, 5));
        await Close(c.Src, c.SrcLine);

        var r = await Send(new UpdateRecordRequest(src.Id,
            new Dictionary<string, JsonElement?> { [K(c.SrcStatus)] = J($"\"{Opt(c.SrcApproved)}\"") }, src.RowVersion));

        r.IsConflict.Should().BeTrue(Why(r));
        (await Reload(src.Id)).Values[K(c.SrcStatus)]!.Value.GetString().Should().Be(Opt(c.SrcDraft));
    }

    [Fact]
    public async Task SavingTheWorkflowOfAClosedModule_DoesNotRewriteRecords()
    {
        var c = await BuildChain("w");
        var (src, _) = await NewSource(c, ("螺絲", 10, 5));
        await Close(c.Src, c.SrcLine);
        var before = (await Db.Context.Set<Record>().AsNoTracking().SingleAsync(r => r.Id == src.Id)).ValuesJson;

        var save = await Send(new SaveWorkflowRequest(c.Src, new WorkflowInput(c.SrcStatus, c.SrcDraft,
            [new WorkflowState(c.SrcDraft), new WorkflowState(c.SrcApproved, true)], [new WorkflowTransition(c.SrcDraft, c.SrcApproved, [])])));

        save.IsSuccess.Should().BeTrue(Why(save));
        (await Db.Context.Set<Record>().AsNoTracking().SingleAsync(r => r.Id == src.Id)).ValuesJson.Should().Be(before);
    }

    // ───── 帶值 ─────

    private async Task<(Chain C, FieldResponse Party)> ChainWithFill(string p)
    {
        var c = await BuildChain(p);
        var party = (await Detail(c.Dst)).Blocks.SelectMany(b => b.Fields).Single(f => f.Id == c.DstParty);
        var fill = new FillConfig(FillConfig.Default, [new FillMapping(c.PartyName, c.DstMemo)]);
        (await Update(party, party.Config with { Fill = fill })).IsSuccess.Should().BeTrue();
        return (c, party);
    }

    private async Task<ServiceResult> NewDst(Chain c, long partyId, string memo = "") =>
        await Send(new CreateRecordRequest(c.Dst, new Dictionary<string, JsonElement?>
        {
            [K(c.DstTitle)] = J("\"D\""), [K(c.DstParty)] = J(partyId.ToString()),
            [K(c.DstMemo)] = memo == "" ? null : J($"\"{memo}\"")
        }));

    private async Task<long> NewParty(Chain c, string name) =>
        ((DynamicRecordResponse)(await Send(new CreateRecordRequest(c.Party, new Dictionary<string, JsonElement?> { [K(c.PartyName)] = J($"\"{name}\"") }))).Data!).Id;

    [Fact]
    public async Task Fill_ClosedSource_ExistingSnapshotStaysReadable_AndNewRecordsAreRejected()
    {
        var (c, _) = await ChainWithFill("f");
        var party = await NewParty(c, "往來甲");
        var existing = await NewDst(c, party);
        existing.IsCreated.Should().BeTrue(Why(existing));
        var rec = (DynamicRecordResponse)existing.Data!;
        rec.Values[K(c.DstMemo)]!.Value.GetString().Should().Be("往來甲");

        await Close(c.Party);

        (await Reload(rec.Id)).Values[K(c.DstMemo)]!.Value.GetString().Should().Be("往來甲", "既有快照照常顯示");

        var fresh = await NewDst(c, party);
        fresh.IsSuccess.Should().BeFalse("新記錄選 Closed 模組的記錄：依參照驗證規則拒絕，不能 fail-open 地帶值");
        fresh.Errors.Should().ContainKey(K(c.DstParty));
        (await RecordCount(c.Dst)).Should().Be(1);
    }

    [Fact]
    public async Task Fill_ClosedSource_EditingAnExistingRecord_WithoutChangingTheReference_KeepsTheSnapshot()
    {
        var (c, _) = await ChainWithFill("f");
        var party = await NewParty(c, "往來甲");
        var rec = (DynamicRecordResponse)(await NewDst(c, party)).Data!;
        await Close(c.Party);

        var r = await Send(new UpdateRecordRequest(rec.Id,
            new Dictionary<string, JsonElement?> { [K(c.DstTitle)] = J("\"改名\""), [K(c.DstParty)] = J(party.ToString()) }, rec.RowVersion));

        r.IsSuccess.Should().BeTrue(Why(r));
        (await Reload(rec.Id)).Values[K(c.DstMemo)]!.Value.GetString().Should().Be("往來甲");
    }

    // ───── 刪除 ─────

    [Fact]
    public async Task DeletingTheModuleOfAConversion_IsBlocked_NotAServerError()
    {
        var c = await BuildChain("x");
        await DefineOk(c, "x-to");
        (await Send(new DeactivateModuleRequest(c.Dst))).IsSuccess.Should().BeTrue();

        var r = await Send(new DeleteModuleRequest(c.Dst, 0));

        r.IsConflict.Should().BeTrue(Why(r));
        ((DeleteCheckResponse)r.Data!).Blockers.Should().Contain(b => b.Kind == DeleteBlockerKind.Conversion);
        (await Db.Context.Set<Conversion>().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Planner_ConversionInsideTheDeletedSet_IsNotABlocker_ButOutsideIs()
    {
        var c = await BuildChain("x");
        await DefineOk(c, "x-to");
        var planner = Svc<DeletePlanner>();

        var all = new HashSet<string> { c.Party, c.Src, c.SrcLine, c.Dst, c.DstLine };
        var inside = await planner.PlanAsync(DeleteTarget.Module, c.Dst, 0, default, all);
        inside!.Check.Blockers.Should().NotContain(b => b.Kind == DeleteBlockerKind.Conversion);

        var partial = new HashSet<string> { c.Dst, c.DstLine };
        var outside = await planner.PlanAsync(DeleteTarget.Module, c.Dst, 0, default, partial);
        outside!.Check.Blockers.Should().Contain(b => b.Kind == DeleteBlockerKind.Conversion);
    }

    [Fact]
    public async Task ModuleRemover_RemovesTheConversionTogetherWithItsModules()
    {
        var c = await BuildChain("x");
        await DefineOk(c, "x-to");
        var remover = Svc<ModuleRemover>();

        foreach (var key in new[] { c.Dst, c.DstLine, c.Src, c.SrcLine })
        {
            var id = (await Db.Context.Set<Module>().AsNoTracking().SingleAsync(m => m.Key == key)).Id;
            await remover.RemoveAsync(id, default);
        }

        (await Db.Context.Set<Conversion>().AsNoTracking().CountAsync()).Should().Be(0);
        (await Db.Context.Set<Module>().AsNoTracking().CountAsync(m => m.Key == c.Src || m.Key == c.Dst)).Should().Be(0);
    }

    private async Task<string> NewClosedApp(string key, params string[] modules)
    {
        var app = new App { Key = key, NameI18n = Text(key), PackageVersion = "1.0.0", FormatVersion = 3, Source = "test", Status = AppStatus.Closed, RowVersion = Guid.NewGuid().ToString() };
        foreach (var m in modules)
            app.Modules.Add(new AppModule { Code = m, ModuleId = (await Db.Context.Set<Module>().SingleAsync(x => x.Key == m)).Id });
        Db.Context.Set<App>().Add(app);
        await Db.Context.SaveChangesAsync();
        await Close(modules);
        return key;
    }

    [Fact]
    public async Task DeleteApp_ContainingAWholeConversion_RemovesTheDefinitionAndTheModules()
    {
        var c = await BuildChain("a");
        await DefineOk(c, "a-to");
        var key = await NewClosedApp("app-a", c.Party, c.Src, c.SrcLine, c.Dst, c.DstLine);

        var r = await Send(new DeleteAppRequest(key, 0));

        r.IsSuccess.Should().BeTrue(Why(r));
        (await Db.Context.Set<Conversion>().AsNoTracking().CountAsync()).Should().Be(0);
        (await Db.Context.Set<Module>().AsNoTracking().CountAsync(m => m.Key == c.Src || m.Key == c.Dst)).Should().Be(0);
    }

    [Fact]
    public async Task DeleteApp_WhoseModulesAreUsedByAConversionOutsideIt_IsBlocked_NotAServerError()
    {
        var c = await BuildChain("a");
        await DefineOk(c, "a-to");
        var key = await NewClosedApp("app-b", c.Dst, c.DstLine);

        var r = await Send(new DeleteAppRequest(key, 0));

        r.IsConflict.Should().BeTrue(Why(r));
        ((DeleteAppCheck)r.Data!).Blockers.Should().Contain(b => b.Kind == DeleteBlockerKind.Conversion);
        (await Db.Context.Set<Conversion>().AsNoTracking().CountAsync()).Should().Be(1);
        (await Db.Context.Set<Module>().AsNoTracking().CountAsync(m => m.Key == c.Dst)).Should().Be(1);
    }

    // ───── 狀態欄位被清空 ─────

    private async Task<DynamicRecordResponse> NewSrcRecord(Chain c, JsonElement? status, bool includeStatus = true)
    {
        var values = new Dictionary<string, JsonElement?> { [K(c.SrcTitle)] = J("\"S\"") };
        if (includeStatus) values[K(c.SrcStatus)] = status;
        var r = await Send(new CreateRecordRequest(c.Src, values));
        r.IsCreated.Should().BeTrue(Why(r));
        return (DynamicRecordResponse)r.Data!;
    }

    private static string? StatusOf(Chain c, DynamicRecordResponse r) =>
        r.Values.TryGetValue(K(c.SrcStatus), out var v) && v is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    public async Task Create_WithTheStatusCleared_GetsTheInitialState_NeverNoState(string raw)
    {
        var c = await BuildChain("s");
        var rec = await NewSrcRecord(c, J(raw));
        StatusOf(c, rec).Should().Be(Opt(c.SrcDraft));
    }

    [Fact]
    public async Task Create_WithoutTheStatusKey_GetsTheInitialState()
    {
        var c = await BuildChain("s");
        StatusOf(c, await NewSrcRecord(c, null, includeStatus: false)).Should().Be(Opt(c.SrcDraft));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    public async Task Update_ClearingTheStatus_IsRejected_AndTheStateIsUnchanged(string raw)
    {
        var c = await BuildChain("s");
        var rec = await NewSrcRecord(c, null, includeStatus: false);

        var r = await Send(new UpdateRecordRequest(rec.Id, new Dictionary<string, JsonElement?> { [K(c.SrcStatus)] = J(raw) }, rec.RowVersion));

        r.IsSuccess.Should().BeFalse("清空狀態等於繞過轉移規則");
        r.Errors.Should().ContainKey(K(c.SrcStatus));
        StatusOf(c, await Reload(rec.Id)).Should().Be(Opt(c.SrcDraft));
    }

    [Fact]
    public async Task Update_ClearingTheStatus_OfAnApprovedRecord_IsRejected()
    {
        var c = await BuildChain("s");
        var rec = await Approve(c, await NewSrcRecord(c, null, includeStatus: false));

        var r = await Send(new UpdateRecordRequest(rec.Id, new Dictionary<string, JsonElement?> { [K(c.SrcStatus)] = null }, rec.RowVersion));

        r.IsSuccess.Should().BeFalse();
        StatusOf(c, await Reload(rec.Id)).Should().Be(Opt(c.SrcApproved));
    }

    [Fact]
    public async Task Update_OfARecordCreatedBeforeTheWorkflow_AssignsTheInitialState()
    {
        // 狀態機是後來才設定的：既有記錄沒有狀態，下一次存檔時補上起始狀態，不留沒有狀態的記錄
        var c = await BuildChain("s");
        var rec = await NewSrcRecord(c, null, includeStatus: false);
        var stored = await Db.Context.Set<Record>().SingleAsync(r => r.Id == rec.Id);
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(stored.ValuesJson)!;
        values.Remove(K(c.SrcStatus));
        stored.ValuesJson = JsonSerializer.Serialize(values);
        await Db.Context.SaveChangesAsync();
        StatusOf(c, await Reload(rec.Id)).Should().BeNull();

        var r = await Send(new UpdateRecordRequest(rec.Id,
            new Dictionary<string, JsonElement?> { [K(c.SrcMemo)] = J("\"補備註\"") }, (await Reload(rec.Id)).RowVersion));

        r.IsSuccess.Should().BeTrue(Why(r));
        StatusOf(c, await Reload(rec.Id)).Should().Be(Opt(c.SrcDraft));
    }
}
