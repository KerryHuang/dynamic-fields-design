using DynamicModules.Application.Dynamic.FieldService.Commands.SetFieldStatus;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

public class RollupFieldRulesTests : DesignerTestBase
{
    private const string Key = "Config.Rollup";
    private const string RefMsg = "彙總設定不正確：來源欄位必須是指向這個模組的參照欄位";

    private sealed record Setup(string Parent, string Child, FieldResponse Source, FieldResponse Hours,
        FieldResponse Cost, FieldResponse Day, FieldResponse Status);

    /// <summary>req（申請）被 rec（維修紀錄）參照；rec 有工時 decimal(3)、金額 currency、日期、狀態 text。</summary>
    private async Task<Setup> NewSetup()
    {
        var parent = await NewModule("req");
        var child = await NewModule("rec");
        var source = await MustAdd(child, FieldType.Reference, RefTo(parent), "申請");
        var hours = await MustAdd(child, FieldType.Decimal, new FieldConfig { Scale = 3 }, "工時");
        var cost = await MustAdd(child, FieldType.Currency, new FieldConfig(), "金額");
        var day = await MustAdd(child, FieldType.Date, new FieldConfig(), "日期");
        var status = await MustAdd(child, FieldType.Text, new FieldConfig(), "狀態");
        return new Setup(parent, child, source, hours, cost, day, status);
    }

    private static FieldConfig Roll(long source, RollupFunction fn, long? aggregate = null, string? condition = null) =>
        new() { Rollup = new RollupConfig(source, fn, aggregate, condition is null ? null : J(condition)) };

    private static string Reason(string reason) => $"彙總設定不正確：{reason}";

    [Fact]
    public async Task Rollup_InvalidSource_Rejected()
    {
        var s = await NewSetup();
        var other = await NewModule("other");
        var wrongTarget = await MustAdd(s.Child, FieldType.Reference, RefTo(other), "其他");
        var entityRef = await MustAdd(s.Child, FieldType.Reference,
            new FieldConfig { RefTarget = new ReferenceTarget(ReferenceTarget.EntityKind, "test.entity") }, "模具");
        var inactive = await MustAdd(s.Child, FieldType.Reference, RefTo(s.Parent), "停用的申請");
        (await Deactivate(inactive.Id)).IsSuccess.Should().BeTrue();

        foreach (var id in new[] { s.Hours.Id, wrongTarget.Id, entityRef.Id, inactive.Id, 999999L })
            Err(await AddField(s.Parent, FieldType.Rollup, Roll(id, RollupFunction.Count)), Key).Should().Be(RefMsg);
        Err(await AddField(s.Parent, FieldType.Rollup, new FieldConfig()), Key).Should().Be(RefMsg);
    }

    [Fact]
    public async Task Rollup_AggregateNotNumberOrDate_Rejected()
    {
        var s = await NewSetup();
        foreach (var id in new[] { s.Status.Id, s.Source.Id, 999999L })
            Err(await AddField(s.Parent, FieldType.Rollup, Roll(s.Source.Id, RollupFunction.Max, id)), Key)
                .Should().Be(Reason("彙總欄位必須是來源模組的數字或日期欄位"));
    }

    [Fact]
    public async Task Rollup_CountWithAggregate_Rejected()
    {
        var s = await NewSetup();
        Err(await AddField(s.Parent, FieldType.Rollup, Roll(s.Source.Id, RollupFunction.Count, s.Hours.Id)), Key)
            .Should().Be(Reason("筆數不需要彙總欄位"));
    }

    [Fact]
    public async Task Rollup_MissingAggregate_Rejected()
    {
        var s = await NewSetup();
        foreach (var fn in new[] { RollupFunction.Sum, RollupFunction.Min, RollupFunction.Max })
            Err(await AddField(s.Parent, FieldType.Rollup, Roll(s.Source.Id, fn)), Key).Should().Be(Reason("必須指定彙總欄位"));
    }

    [Fact]
    public async Task Rollup_SumOfDate_Rejected()
    {
        var s = await NewSetup();
        var dateFormula = await MustAdd(s.Child, FieldType.Formula,
            new FieldConfig { Formula = $"DATEADD({{{s.Day.Id}}}, 1)", ResultType = "date" }, "隔日");
        foreach (var id in new[] { s.Day.Id, dateFormula.Id })
            Err(await AddField(s.Parent, FieldType.Rollup, Roll(s.Source.Id, RollupFunction.Sum, id)), Key)
                .Should().Be(Reason("加總不能用在日期欄位"));
    }

    [Fact]
    public async Task Rollup_InvalidCondition_Rejected()
    {
        var s = await NewSetup();
        var r = await AddField(s.Parent, FieldType.Rollup,
            Roll(s.Source.Id, RollupFunction.Sum, s.Hours.Id, """{"field":999999,"op":"eq","value":"x"}"""));
        Err(r, Key).Should().Be(Reason("條件不正確：欄位 999999 不存在或未啟用"));
    }

    [Fact]
    public async Task Rollup_ValidCondition_Accepted()
    {
        var s = await NewSetup();
        var cond = $$"""{"field":{{s.Status.Id}},"op":"eq","value":"done"}""";
        (await AddField(s.Parent, FieldType.Rollup, Roll(s.Source.Id, RollupFunction.Sum, s.Hours.Id, cond))).IsCreated.Should().BeTrue();
    }

    [Fact]
    public async Task Rollup_ResultTypeDerived()
    {
        var s = await NewSetup();
        var currencyFormula = await MustAdd(s.Child, FieldType.Formula,
            new FieldConfig { Formula = $"{{{s.Cost.Id}}}*2", ResultType = "currency", Scale = 4 }, "雙倍金額");

        // 前端送來的 ResultType、Scale 一律被推得的值覆蓋
        var sumCurrency = await MustAdd(s.Parent, FieldType.Rollup, Roll(s.Source.Id, RollupFunction.Sum, s.Cost.Id) with { ResultType = "date", Scale = 6 });
        sumCurrency.Config.ResultType.Should().Be("currency");
        sumCurrency.Config.Scale.Should().Be(2);

        var count = await MustAdd(s.Parent, FieldType.Rollup, Roll(s.Source.Id, RollupFunction.Count) with { ResultType = "date", Scale = 6 });
        count.Config.ResultType.Should().Be("number");
        count.Config.Scale.Should().Be(0);

        var maxDate = await MustAdd(s.Parent, FieldType.Rollup, Roll(s.Source.Id, RollupFunction.Max, s.Day.Id) with { ResultType = "number", Scale = 2 });
        maxDate.Config.ResultType.Should().Be("date");
        maxDate.Config.Scale.Should().BeNull();

        var sumFormula = await MustAdd(s.Parent, FieldType.Rollup, Roll(s.Source.Id, RollupFunction.Sum, currencyFormula.Id));
        sumFormula.Config.ResultType.Should().Be("currency");
        sumFormula.Config.Scale.Should().Be(4);

        var sumHours = await MustAdd(s.Parent, FieldType.Rollup,
            Roll(s.Source.Id, RollupFunction.Sum, s.Hours.Id) with { IsFilterable = true, IsSortable = true });
        sumHours.Config.ResultType.Should().Be("number");
        sumHours.Config.Scale.Should().Be(3);
        sumHours.Config.IsFilterable.Should().BeTrue();

        var qty = await MustAdd(s.Child, FieldType.Integer, new FieldConfig(), "數量");
        var sumInt = await MustAdd(s.Parent, FieldType.Rollup, Roll(s.Source.Id, RollupFunction.Sum, qty.Id));
        sumInt.Config.ResultType.Should().Be("number");
        sumInt.Config.Scale.Should().Be(0);

        // 更新時重新推得
        var updated = await Update(sumHours, Roll(s.Source.Id, RollupFunction.Max, s.Day.Id) with { ResultType = "number" });
        var u = (FieldResponse)updated.Data!;
        u.Config.ResultType.Should().Be("date");
        u.Config.Scale.Should().BeNull();
    }

    [Fact]
    public async Task Rollup_ComputedFlags_Rejected()
    {
        var s = await NewSetup();
        var cfg = Roll(s.Source.Id, RollupFunction.Count);
        foreach (var bad in new[] { cfg with { Required = true }, cfg with { ReadOnly = true }, cfg with { MaxLength = 10 } })
            Err(await AddField(s.Parent, FieldType.Rollup, bad), "Config").Should().Be("系統計算欄位不能設定必填、預設值、唯讀或長度");

        var ok = await MustAdd(s.Parent, FieldType.Rollup, cfg);
        Err(await Update(ok, cfg with { Required = true }), "Config").Should().Be("系統計算欄位不能設定必填、預設值、唯讀或長度");
    }

    /// <summary>eq 自己參照自己（上層設備），hours 為 decimal；r1 = sum(hours) 沿 parentRef。</summary>
    private async Task<(string Module, FieldResponse ParentRef, FieldResponse Hours, FieldResponse R1)> SelfSetup()
    {
        var m = await NewModule("eq");
        var parentRef = await MustAdd(m, FieldType.Reference, RefTo(m), "上層設備");
        var hours = await MustAdd(m, FieldType.Decimal, new FieldConfig(), "工時");
        var r1 = await MustAdd(m, FieldType.Rollup, Roll(parentRef.Id, RollupFunction.Sum, hours.Id), "子設備工時");
        return (m, parentRef, hours, r1);
    }

    [Fact]
    public async Task Rollup_OfRollup_Rejected()
    {
        var (m, parentRef, _, r1) = await SelfSetup();
        Err(await AddField(m, FieldType.Rollup, Roll(parentRef.Id, RollupFunction.Max, r1.Id)), Key)
            .Should().Be(Reason("不能彙總另一個彙總欄位或引用它的公式"));
    }

    [Fact]
    public async Task Rollup_OfFormulaReferencingRollup_Rejected()
    {
        var (m, parentRef, hours, r1) = await SelfSetup();
        var direct = await MustAdd(m, FieldType.Formula, new FieldConfig { Formula = $"{{{r1.Id}}}*2", ResultType = "number" }, "二倍");
        var indirect = await MustAdd(m, FieldType.Formula, new FieldConfig { Formula = $"{{{direct.Id}}}+{{{hours.Id}}}", ResultType = "number" }, "間接");
        foreach (var f in new[] { direct, indirect })
            Err(await AddField(m, FieldType.Rollup, Roll(parentRef.Id, RollupFunction.Sum, f.Id)), Key)
                .Should().Be(Reason("不能彙總另一個彙總欄位或引用它的公式"));
    }

    [Fact]
    public async Task Rollup_ConditionOnRollup_Rejected()
    {
        var (m, parentRef, hours, r1) = await SelfSetup();
        var viaFormula = await MustAdd(m, FieldType.Formula, new FieldConfig { Formula = $"{{{r1.Id}}}*2", ResultType = "number" }, "二倍");
        foreach (var id in new[] { r1.Id, viaFormula.Id })
        {
            var cond = $$"""{"field":{{id}},"op":"gt","value":0}""";
            Err(await AddField(m, FieldType.Rollup, Roll(parentRef.Id, RollupFunction.Sum, hours.Id, cond)), Key)
                .Should().Be(Reason("條件不正確：不能使用彙總欄位或引用它的公式"));
        }
    }

    [Fact]
    public async Task Formula_CanReferenceRollup()
    {
        var (m, _, _, r1) = await SelfSetup();
        var f = await AddField(m, FieldType.Formula, new FieldConfig { Formula = $"{{{r1.Id}}}*2", ResultType = "number" }, "二倍");
        f.IsCreated.Should().BeTrue(f.Message + string.Join(";", f.Errors.Select(e => e.Key + e.Value[0])));
    }

    [Fact]
    public async Task Deactivate_FieldUsedByRollup_Rejected()
    {
        var s = await NewSetup();
        var cond = $$"""{"field":{{s.Status.Id}},"op":"eq","value":"done"}""";
        var rollup = await MustAdd(s.Parent, FieldType.Rollup, Roll(s.Source.Id, RollupFunction.Sum, s.Hours.Id, cond));

        foreach (var used in new[] { s.Source, s.Hours, s.Status })
            Err(await Deactivate(used.Id), "Status").Should().Be("此欄位被彙總欄位引用，不能停用");

        // 沒被引用的欄位可以停用；彙總欄位本身停用後，原本被引用的欄位也解除限制
        (await Deactivate(s.Cost.Id)).IsSuccess.Should().BeTrue();
        (await Deactivate(rollup.Id)).IsSuccess.Should().BeTrue();
        (await Deactivate(s.Hours.Id)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Reactivate_RollupWithInactiveConditionField_Rejected()
    {
        var s = await NewSetup();
        var cond = $$"""{"field":{{s.Status.Id}},"op":"eq","value":"done"}""";
        var rollup = await MustAdd(s.Parent, FieldType.Rollup, Roll(s.Source.Id, RollupFunction.Sum, s.Hours.Id, cond));
        (await Deactivate(rollup.Id)).IsSuccess.Should().BeTrue();
        (await Deactivate(s.Status.Id)).IsSuccess.Should().BeTrue();

        Err(await Send(new SetFieldStatusRequest(rollup.Id, FieldStatus.Active)), Key)
            .Should().Be(Reason($"條件不正確：欄位 {s.Status.Id} 不存在或未啟用"));
    }

    [Fact]
    public async Task Update_FormulaReferencedByRollup_ToDependOnRollup_Rejected()
    {
        var m = await NewModule("eq");
        var parentRef = await MustAdd(m, FieldType.Reference, RefTo(m), "上層設備");
        var hours = await MustAdd(m, FieldType.Decimal, new FieldConfig(), "工時");
        var r1 = await MustAdd(m, FieldType.Rollup, Roll(parentRef.Id, RollupFunction.Sum, hours.Id), "合計");
        var f = await MustAdd(m, FieldType.Formula, new FieldConfig { Formula = $"{{{hours.Id}}}*2", ResultType = "number" }, "二倍");
        await MustAdd(m, FieldType.Rollup, Roll(parentRef.Id, RollupFunction.Sum, f.Id), "二倍合計");

        var bad = await Update(f, f.Config with { Formula = $"{{{r1.Id}}}*2" });
        Err(bad, "Config.Formula").Should().Be("公式不正確：被彙總欄位引用的公式不能引用彙總欄位");

        // 沒被彙總引用的公式可以引用彙總
        var free = await MustAdd(m, FieldType.Formula, new FieldConfig { Formula = $"{{{hours.Id}}}", ResultType = "number" }, "自由");
        (await Update(free, free.Config with { Formula = $"{{{r1.Id}}}*2" })).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Update_ConditionFieldConfig_InvalidatesRollupCondition_Rejected()
    {
        var s = await NewSetup();
        var cond = $$"""{"all":[{"field":{{s.Hours.Id}},"op":"gt","value":1.5},{"field":{{s.Status.Id}},"op":"eq","value":"done"}]}""";
        await MustAdd(s.Parent, FieldType.Rollup, Roll(s.Source.Id, RollupFunction.Sum, s.Cost.Id, cond));

        // 小數位數縮到 0、文字長度縮到比條件常值短：已存的條件會解析失敗，回彙總設定原本的訊息
        Err(await Update(s.Hours, s.Hours.Config with { Scale = 0 }), Key).Should().StartWith(Reason("條件不正確："));
        Err(await Update(s.Status, s.Status.Config with { MaxLength = 2 }), Key).Should().StartWith(Reason("條件不正確："));

        // 不影響條件的修改照常通過
        (await Update(s.Hours, s.Hours.Config with { Scale = 1 })).IsSuccess.Should().BeTrue();
        (await Update(s.Status, s.Status.Config with { MaxLength = 10 })).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Update_AggregatedFormula_ResultTypeRederived()
    {
        var s = await NewSetup();
        var doubled = await MustAdd(s.Child, FieldType.Formula,
            new FieldConfig { Formula = $"{{{s.Hours.Id}}}*2", ResultType = "number", Scale = 1 }, "雙倍工時");
        var sum = await MustAdd(s.Parent, FieldType.Rollup, Roll(s.Source.Id, RollupFunction.Sum, doubled.Id), "雙倍工時合計");
        sum.Config.ResultType.Should().Be("number");

        (await Update(doubled, doubled.Config with { ResultType = "currency", Scale = 3 })).IsSuccess.Should().BeTrue();
        var after = (await Detail(s.Parent)).Blocks.SelectMany(b => b.Fields).Single(f => f.Id == sum.Id);
        after.Config.ResultType.Should().Be("currency");
        after.Config.Scale.Should().Be(3);
    }

    [Fact]
    public async Task Update_AggregatedFormulaToDate_SumRejected()
    {
        var s = await NewSetup();
        var doubled = await MustAdd(s.Child, FieldType.Formula,
            new FieldConfig { Formula = $"{{{s.Hours.Id}}}*2", ResultType = "number" }, "雙倍工時");
        await MustAdd(s.Parent, FieldType.Rollup, Roll(s.Source.Id, RollupFunction.Sum, doubled.Id));

        var r = await Update(doubled, new FieldConfig { Formula = $"DATEADD({{{s.Day.Id}}}, 1)", ResultType = "date" });
        Err(r, Key).Should().Be(Reason("加總不能用在日期欄位"));
    }

    [Fact]
    public async Task Update_FormulaIndirectlyReferencedByRollup_ToDependOnRollup_Rejected()
    {
        var (m, parentRef, hours, r1) = await SelfSetup();
        var inner = await MustAdd(m, FieldType.Formula, new FieldConfig { Formula = $"{{{hours.Id}}}", ResultType = "number" }, "內層");
        var outer = await MustAdd(m, FieldType.Formula, new FieldConfig { Formula = $"{{{inner.Id}}}*2", ResultType = "number" }, "外層");
        await MustAdd(m, FieldType.Rollup, Roll(parentRef.Id, RollupFunction.Sum, outer.Id), "外層合計");

        // 內層只經由外層被彙總引用；改成引用彙總會讓彙總對象間接依賴彙總
        Err(await Update(inner, inner.Config with { Formula = $"{{{r1.Id}}}*2" }), "Config.Formula")
            .Should().Be("公式不正確：被彙總欄位引用的公式不能引用彙總欄位");
    }
}
