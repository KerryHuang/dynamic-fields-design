using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.DeleteRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>
/// 多層明細的查證（設計文件 §5）：工令 → 製程 → 報工。
/// 核心的明細只有一層；這裡用測試固定「哪些做得到、哪些被擋」，以及不用巢狀明細的替代做法。
/// </summary>
public class ThreeLevelDocumentTests : DesignerTestBase
{
    [Fact]
    public async Task DetailOfDetail_IsRejected()
    {
        var wo = await NewModule("wo");
        var step = await NewModule("wo-step");
        var rep = await NewModule("wo-step-rep");
        (await AddField(step, FieldType.Reference, RefTo(wo, ReferenceRelation.Detail))).IsSuccess.Should().BeTrue();
        // 製程已經是工令的明細，不能再有自己的明細
        var r = await AddField(rep, FieldType.Reference, RefTo(step, ReferenceRelation.Detail));
        r.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task DetailHeaderCannotItselfBeADetail()
    {
        var wo = await NewModule("wo");
        var step = await NewModule("wo-step");
        var rep = await NewModule("wo-step-rep");
        (await AddField(rep, FieldType.Reference, RefTo(step, ReferenceRelation.Detail))).IsSuccess.Should().BeTrue();
        var r = await AddField(step, FieldType.Reference, RefTo(wo, ReferenceRelation.Detail));
        r.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task OrdinaryReferenceToADetailModule_IsRejected()
    {
        var wo = await NewModule("wo");
        var step = await NewModule("wo-step");
        var rep = await NewModule("wo-step-rep");
        (await AddField(step, FieldType.Reference, RefTo(wo, ReferenceRelation.Detail))).IsSuccess.Should().BeTrue();
        var r = await AddField(rep, FieldType.Reference, RefTo(step), "製程");
        r.IsSuccess.Should().BeFalse("報工不能用一般參照指到另一個模組的明細列，設計文件的替代做法要寫成報工指向工令");
    }

    // ───── 替代做法 A：製程維持工令的明細（同畫面編輯），報工指向工令並記製程序號 ─────

    private static Dictionary<string, JsonElement?> Vals(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => (JsonElement?)JsonSerializer.SerializeToElement(p.Value));

    private async Task<DynamicRecordResponse> Create(string module, params (string Key, object? Value)[] values)
    {
        var r = await Send(new CreateRecordRequest(module, Vals(values)));
        r.IsCreated.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        return (DynamicRecordResponse)r.Data!;
    }

    private async Task<string> TitleKey(string module) => $"f_{(await Detail(module)).TitleFieldId}";

    private async Task<decimal?> Num(long id, string key)
    {
        var rec = await Db.Context.Set<Record>().AsNoTracking().SingleAsync(r => r.Id == id);
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(rec.ValuesJson)!;
        return values.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;
    }

    [Fact]
    public async Task AlternativeA_StepsAreDetails_ReportsReferenceTheWorkOrder_AndRollUpTheTotal()
    {
        var wo = await NewModule("wo");
        var step = await NewModule("wo-step");
        var rep = await NewModule("wo-rep");
        await AddField(step, FieldType.Reference, RefTo(wo, ReferenceRelation.Detail), "工令");
        var woRef = await MustAdd(rep, FieldType.Reference, RefTo(wo), "工令");
        var qty = await MustAdd(rep, FieldType.Decimal, new FieldConfig(), "報工數量");
        var total = await MustAdd(wo, FieldType.Rollup,
            new FieldConfig { Rollup = new RollupConfig(woRef.Id, RollupFunction.Sum, qty.Id, null) }, "累計報工");
        foreach (var m in new[] { wo, step, rep }) (await Send(new ActivateModuleRequest(m))).IsSuccess.Should().BeTrue();

        var order = await Create(wo, (await TitleKey(wo), "WO-1"));
        var a = await Create(rep, (await TitleKey(rep), "R1"), (woRef.Key, order.Id), (qty.Key, 3));
        await Create(rep, (await TitleKey(rep), "R2"), (woRef.Key, order.Id), (qty.Key, 4));
        (await Num(order.Id, total.Key)).Should().Be(7);

        (await Send(new DeleteRecordRequest(a.Id, a.RowVersion))).IsSuccess.Should().BeTrue();
        (await Num(order.Id, total.Key)).Should().Be(4);
    }

    // ───── 替代做法 B：製程獨立成模組（不再是工令的明細），報工同時指向製程與工令，兩個彙總各自更新 ─────

    [Fact]
    public async Task RollupOfARollup_IsRejected_SoTheWorkOrderTotalMustComeFromTheReportsDirectly()
    {
        var wo = await NewModule("wo");
        var step = await NewModule("wo-step");
        var rep = await NewModule("wo-rep");
        var stepWo = await MustAdd(step, FieldType.Reference, RefTo(wo), "工令");
        var repStep = await MustAdd(rep, FieldType.Reference, RefTo(step), "製程");
        var qty = await MustAdd(rep, FieldType.Decimal, new FieldConfig(), "報工數量");
        var stepTotal = await MustAdd(step, FieldType.Rollup,
            new FieldConfig { Rollup = new RollupConfig(repStep.Id, RollupFunction.Sum, qty.Id, null) }, "製程累計");
        var r = await AddField(wo, FieldType.Rollup,
            new FieldConfig { Rollup = new RollupConfig(stepWo.Id, RollupFunction.Sum, stepTotal.Id, null) }, "工令累計");
        r.IsSuccess.Should().BeFalse("彙總不支援孫記錄：不能把彙總欄位再彙總");
    }

    [Fact]
    public async Task AlternativeB_ReportsReferenceBothStepAndWorkOrder_TotalsStayInSync()
    {
        var wo = await NewModule("wo");
        var step = await NewModule("wo-step");
        var rep = await NewModule("wo-rep");
        var stepWo = await MustAdd(step, FieldType.Reference, RefTo(wo), "工令");
        var repStep = await MustAdd(rep, FieldType.Reference, RefTo(step), "製程");
        var repWo = await MustAdd(rep, FieldType.Reference, RefTo(wo), "工令");
        var qty = await MustAdd(rep, FieldType.Decimal, new FieldConfig(), "報工數量");
        var stepTotal = await MustAdd(step, FieldType.Rollup,
            new FieldConfig { Rollup = new RollupConfig(repStep.Id, RollupFunction.Sum, qty.Id, null) }, "製程累計");
        var woTotal = await MustAdd(wo, FieldType.Rollup,
            new FieldConfig { Rollup = new RollupConfig(repWo.Id, RollupFunction.Sum, qty.Id, null) }, "工令累計");
        foreach (var m in new[] { wo, step, rep }) (await Send(new ActivateModuleRequest(m))).IsSuccess.Should().BeTrue();

        var order = await Create(wo, (await TitleKey(wo), "WO-1"));
        var s1 = await Create(step, (await TitleKey(step), "S1"), (stepWo.Key, order.Id));
        var s2 = await Create(step, (await TitleKey(step), "S2"), (stepWo.Key, order.Id));
        var r1 = await Create(rep, (await TitleKey(rep), "R1"), (repStep.Key, s1.Id), (repWo.Key, order.Id), (qty.Key, 3));
        await Create(rep, (await TitleKey(rep), "R2"), (repStep.Key, s2.Id), (repWo.Key, order.Id), (qty.Key, 4));

        (await Num(s1.Id, stepTotal.Key)).Should().Be(3);
        (await Num(s2.Id, stepTotal.Key)).Should().Be(4);
        (await Num(order.Id, woTotal.Key)).Should().Be(7);

        (await Send(new UpdateRecordRequest(r1.Id, Vals((qty.Key, 5)), r1.RowVersion))).IsSuccess.Should().BeTrue();
        (await Num(s1.Id, stepTotal.Key)).Should().Be(5);
        (await Num(order.Id, woTotal.Key)).Should().Be(9);
    }
}
