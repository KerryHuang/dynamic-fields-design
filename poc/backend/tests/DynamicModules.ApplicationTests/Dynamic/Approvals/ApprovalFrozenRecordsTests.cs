using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddField;
using DynamicModules.Application.Dynamic.FieldService.Commands.UpdateField;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.Rollups;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>整批重算固定的記錄（設計 §8.6 第 4 點、§10.4）：事實、簽核中、鎖定狀態、鎖定表頭的明細列；以及重算回報保留筆數。</summary>
public class ApprovalFrozenRecordsTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private PurchaseRig _r = null!;
    private ApprovalFlow _flow = null!;

    /// <summary>草稿 → 已確認 需簽核（流程條件引用折扣後總額）；已確認 → 草稿 是解鎖轉移。</summary>
    private async Task Rig()
    {
        _r = await BuildPurchase();
        _flow = await SeedFlow(_r, "po-a", _r.Confirmed, 1,
            Def(Cond(_r.NetTotal, "gte", 0), Stage("lead", Group(PurchaseLeadGroup))));
        (await SaveWorkflow(_r, [Needs(_r.Draft, _r.Confirmed), Plain(_r.Confirmed, _r.Draft), Plain(_r.Confirmed, _r.Partial)]))
            .IsSuccess.Should().BeTrue();
        await GrantEveryone(_r.Module, _r.Extra);
    }

    /// <summary>建單（折扣 100）、送簽、簽核人同意；單據停在已確認。</summary>
    private async Task<DynamicRecordResponse> ApprovedPo(int lines = 1)
    {
        As("buyer1");
        var po = await NewPo(_r, extra: V((_r.Discount, 100)));
        if (lines > 1) await AddLines(po.Id, lines - 1);
        var instanceId = (await MustSubmit(po.Id, _r.Confirmed)).InstanceId!.Value;
        As("buyer2");
        var task = TasksOf(instanceId).Single(t => t.AssigneeUserId == "buyer2" && t.Status == ApprovalTaskStatus.Open);
        (await Send(new ApproveTaskRequest(task.Id, Inst(instanceId).RowVersion, null, null))).IsSuccess.Should().BeTrue();
        As("admin");
        StateOf(_r, po.Id).Should().Be(_r.Confirmed);
        return po;
    }

    /// <summary>直接在資料庫替單據加明細列（不經寫入管線，鎖定的單據也能加）。</summary>
    private async Task AddLines(long poId, int count)
    {
        var lineModule = Db.Context.Set<Module>().AsNoTracking().Single(m => m.Key == _r.Line);
        for (var i = 0; i < count; i++)
        {
            var values = new Dictionary<string, JsonElement?>
            {
                [$"f_{_r.LineHead}"] = JsonSerializer.SerializeToElement(poId),
                [$"f_{_r.LineQty}"] = JsonSerializer.SerializeToElement(1), [$"f_{_r.LinePrice}"] = JsonSerializer.SerializeToElement(500),
                [$"f_{_r.LineSubtotal}"] = JsonSerializer.SerializeToElement(500)
            };
            Db.Context.Add(new Record
            {
                ModuleId = lineModule.Id, ValuesJson = RecordValueProcessor.Serialize(values), RowVersion = Guid.NewGuid().ToString(),
                CreatedUserId = "admin", CreatedTime = SeedTime
            });
        }
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
    }

    private async Task<FrozenRecords> Load(string moduleKey)
    {
        using var scope = NewScope();
        var sp = scope.ServiceProvider;
        var module = (await sp.GetRequiredService<IModuleRepository>().GetByKeyAsync(moduleKey, true, CancellationToken.None))!;
        var rows = await sp.GetRequiredService<IRecordRepository>().ListAllByModuleAsync(module.Id, CancellationToken.None);
        return await sp.GetRequiredService<FrozenRecordsLoader>().LoadAsync(module, rows, CancellationToken.None);
    }

    private Record Row(long id) => Db.Context.Set<Record>().AsNoTracking().Single(x => x.Id == id);

    private decimal Num(long recordId, long field) => Rec(recordId).Values[$"f_{field}"]!.Value.GetDecimal();

    private long BlockOf(long fieldId) => Db.Context.Set<Field>().AsNoTracking().Single(f => f.Id == fieldId).BlockId;

    private Task<ServiceResult> SetFormula(long fieldId, string label, string formula) =>
        Send(new UpdateFieldRequest(fieldId, FieldType.Formula, Text(label),
            new FieldConfig { Formula = formula, ResultType = "number", Scale = 0 }, BlockOf(fieldId)));

    // ───────── repository ─────────

    [Fact]
    public async Task ListPendingByModules_ReturnsOnlyPendingOfGivenModules()
    {
        await Rig();
        var other = await BuildPurchase("pb");
        var flowB = await SeedFlow(other, "pb-a", other.Confirmed, 1, Def(null, Stage("lead", Group(PurchaseLeadGroup))));
        var third = await BuildPurchase("pc");
        var flowC = await SeedFlow(third, "pc-a", third.Confirmed, 1, Def(null, Stage("lead", Group(PurchaseLeadGroup))));
        var a = await SeedInstance(_r, _flow, recordId: 11);
        await SeedInstance(_r, _flow, ApprovalInstanceStatus.Approved, recordId: 12);
        var b = await SeedInstance(other, flowB, recordId: 21);
        await SeedInstance(other, flowB, ApprovalInstanceStatus.Approved, recordId: 22);
        await SeedInstance(third, flowC, recordId: 31);

        using var scope = NewScope();
        var list = await scope.ServiceProvider.GetRequiredService<IApprovalInstanceRepository>()
            .ListPendingByModulesAsync([_r.ModuleId, other.ModuleId], CancellationToken.None);

        list.Select(x => x.InstanceId).Should().BeEquivalentTo([a.Id, b.Id]);
        var first = list.Single(x => x.InstanceId == a.Id);
        first.Should().Be(new PendingInstanceRef(a.Id, 11, _r.ModuleId, _r.Draft, _r.Confirmed, Inst(a.Id).FlowVersionId, "x"));
    }

    // ───────── loader ─────────

    [Fact]
    public async Task Loader_PendingRecord_Frozen()
    {
        await Rig();
        var po = await NewPo(_r);
        await SeedInstance(_r, _flow, recordId: po.Id);
        (await Load(_r.Module)).Contains(Row(po.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task Loader_LockedStateRecord_Frozen()
    {
        await Rig();
        var po = await ApprovedPo();
        (await Load(_r.Module)).Contains(Row(po.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task Loader_DraftRecord_NotFrozen()
    {
        await Rig();
        var po = await NewPo(_r);
        (await Load(_r.Module)).Contains(Row(po.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Loader_DetailRowOfLockedHeader_Frozen()
    {
        await Rig();
        var approved = await ApprovedPo();
        var draft = await NewPo(_r);
        var frozen = await Load(_r.Line);
        LineRows(approved.Id).Should().NotBeEmpty().And.OnlyContain(x => frozen.Contains(x));
        LineRows(draft.Id).Should().NotBeEmpty().And.OnlyContain(x => !frozen.Contains(x));
    }

    [Fact]
    public async Task Loader_DetailRowOfPendingHeader_Frozen()
    {
        await Rig();
        var po = await NewPo(_r);
        await SeedInstance(_r, _flow, recordId: po.Id);
        var frozen = await Load(_r.Line);
        LineRows(po.Id).Should().NotBeEmpty().And.OnlyContain(x => frozen.Contains(x));
    }

    [Fact]
    public async Task Loader_ModuleWithoutApproval_SameAsFactsOnly()
    {
        var r = await BuildPurchase("pl");
        var po = await NewPo(r);
        var frozen = await Load(r.Module);
        frozen.Contains(Row(po.Id)).Should().BeFalse();
        LineRowsOf(r, po.Id).Should().OnlyContain(x => !frozen.Contains(x));
    }

    private List<Record> LineRows(long poId) => LineRowsOf(_r, poId);

    private List<Record> LineRowsOf(PurchaseRig r, long poId)
    {
        var lineModule = Db.Context.Set<Module>().AsNoTracking().Single(m => m.Key == r.Line).Id;
        return Db.Context.Set<Record>().AsNoTracking().Where(x => x.ModuleId == lineModule).AsEnumerable()
            .Where(x => RecordValueProcessor.Parse(x.ValuesJson).TryGetValue($"f_{r.LineHead}", out var h) && h is { } e && e.GetInt64() == poId)
            .ToList();
    }

    // ───────── 整批重算端到端 ─────────

    [Fact]
    public async Task UpdateFormula_NoPending_LockedRecordKeepsOldValue()
    {
        await Rig();
        var approved = await ApprovedPo();
        var draft = await NewPo(_r, extra: V((_r.Discount, 100)));
        Num(approved.Id, _r.NetTotal).Should().Be(900);

        var res = await SetFormula(_r.NetTotal, "折扣後總額", $"{{{_r.LineSum}}}");

        res.IsSuccess.Should().BeTrue(Dump(res));
        Num(approved.Id, _r.NetTotal).Should().Be(900);
        Num(draft.Id, _r.NetTotal).Should().Be(1000);
    }

    [Fact]
    public async Task Locked_FormulaChangedThenPutMemo_LockedTotalUnchanged()
    {
        await Rig();
        var approved = await ApprovedPo();
        (await SetFormula(_r.NetTotal, "折扣後總額", $"{{{_r.LineSum}}}")).IsSuccess.Should().BeTrue();

        var res = await Put(approved.Id, V((_r.Memo, "x")));

        res.IsSuccess.Should().BeTrue(Dump(res));
        Num(approved.Id, _r.NetTotal).Should().Be(900);
    }

    // ───────── 保留筆數 ─────────

    [Fact]
    public async Task UpdateFormula_NoPending_LockedRecordKeepsOldValue_ReportsCount()
    {
        await Rig();
        await ApprovedPo();
        await NewPo(_r, extra: V((_r.Discount, 100)));

        var res = await SetFormula(_r.NetTotal, "折扣後總額", $"{{{_r.LineSum}}}");

        ((FieldResponse)res.Data!).PreservedRecordCount.Should().Be(1);
    }

    [Fact]
    public async Task UpdateDetailFormula_LockedHeader_DetailRowKeepsOldValue()
    {
        await Rig();
        var approved = await ApprovedPo(lines: 3);
        var linesBefore = LineRows(approved.Id);
        linesBefore.Should().HaveCount(3);
        var totalBefore = Num(approved.Id, _r.LineSum);

        var res = await SetFormula(_r.LineSubtotal, "小計", $"{{{_r.LineQty}}} * {{{_r.LinePrice}}} + 1");

        res.IsSuccess.Should().BeTrue(Dump(res));
        ((FieldResponse)res.Data!).PreservedRecordCount.Should().Be(3);
        LineRows(approved.Id).Select(x => x.ValuesJson).Should().BeEquivalentTo(linesBefore.Select(x => x.ValuesJson));
        Num(approved.Id, _r.LineSum).Should().Be(totalBefore);
    }

    [Fact]
    public async Task AddRollupField_LockedRecord_ReportsCount()
    {
        await Rig();
        await ApprovedPo();
        await NewPo(_r);

        var res = await Send(new AddFieldRequest(_r.Module, BlockOf(_r.Memo), FieldType.Rollup, Text("費用數"),
            new FieldConfig { Rollup = new RollupConfig(_r.ExtraRef, RollupFunction.Count, null, null) }));

        res.IsCreated.Should().BeTrue(Dump(res));
        ((FieldResponse)res.Data!).PreservedRecordCount.Should().Be(1);
    }

    [Fact]
    public async Task UpdateLabelOnly_PreservedRecordCountIsNull()
    {
        await Rig();
        await ApprovedPo();

        var config = (await Detail(_r.Module)).Blocks.SelectMany(b => b.Fields).Single(f => f.Id == _r.NetTotal).Config;
        var res = await Send(new UpdateFieldRequest(_r.NetTotal, FieldType.Formula, Text("新標籤"), config, BlockOf(_r.NetTotal)));

        res.IsSuccess.Should().BeTrue(Dump(res));
        ((FieldResponse)res.Data!).PreservedRecordCount.Should().BeNull();
    }
}
