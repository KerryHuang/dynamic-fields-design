using System.Text.Json;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Commands.DeactivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

public class ReportPermissionMatrixTests : ReportTestBase
{
    private static void ShouldBeRestricted(ReportResult r, params string[] hiddenNames)
    {
        r.State.Should().Be("restricted");
        r.Total.Should().BeNull();
        r.Groups.Should().BeNull();
        r.DetailRows.Should().BeNull();
        r.Columns.Should().BeEmpty();
        foreach (var name in hiddenNames) r.Message.Should().NotContain(name);
    }

    public static TheoryData<string> Positions =>
        new() { "column", "group", "measure", "filter", "sort", "via", "primaryDate", "measureCondition" };

    private ReportDefinition HiddenAt(string position) => position switch
    {
        "column" => Def(columns: [MainF(Secret)]),
        "group" => Def(groups: [G(MainF(Secret))], measures: [M("n", "countMain")]),
        "measure" => Def(measures: [M("s", "sum", MainF(SecretNum))]),
        "filter" => Def(measures: [M("n", "countMain")], filters: [F(MainF(Secret), "eq", "機密值")]),
        "sort" => Def(columns: [MainF(Note)], sort: [new ReportSort(ReportCalculator.KeyOf(MainF(Secret)), false)]),
        "via" => Def(columns: [new ReportFieldRef(Main, CustomerSecret.Id, CustomerRef.Id)]),
        "primaryDate" => Def(measures: [M("n", "countMain")], primary: MainF(SecretDate)),
        "measureCondition" => Def(measures: [M("r", "ratio", cond: F(MainF(Secret), "eq", "猜"))]),
        _ => throw new ArgumentException(position),
    };

    [Theory, MemberData(nameof(Positions))]
    public async Task HiddenField_AnyPosition_Restricted_MessageWithoutName(string position)
    {
        await Arrange();
        await Insert("order", "sales01", (Secret, "機密值"), (Note, "n"), (SecretNum, 7m), (SecretDate, "2026-11-04"));
        var d = HiddenAt(position);

        As("admin");
        (await Run(d)).State.Should().Be("ok"); // 對照組：同一定義在看得到的人身上有結果

        As("sales01");
        var r = await Run(d);
        ShouldBeRestricted(r, "機密備註", "機密成本", "機密日期", "客戶機密");
        r.Message.Should().Contain("隱藏欄位");
    }

    [Theory, MemberData(nameof(Positions))]
    public async Task HiddenField_AnyPosition_Drill_Restricted_NoValues(string position)
    {
        await Arrange();
        await Insert("order", "sales01", (Secret, "機密值"), (Note, "n"), (SecretNum, 4242m), (SecretDate, "2026-11-04"));
        As("sales01");
        var r = await Run(HiddenAt(position), drill: position == "group" ? ["機密值"] : []);
        ShouldBeRestricted(r, "機密備註", "機密成本", "機密日期", "客戶機密");
        JsonSerializer.Serialize(r).Should().NotContain("4242").And.NotContain("機密值");
    }

    public static TheoryData<string> UnreadableViaPositions => new() { "filter", "ratio", "primaryDate" };

    /// <summary>條件類位置用到不可讀模組的帶出欄位：回 empty 或 0% 會是假數字，要整張擋下（spec §7、§13.2）。</summary>
    [Theory, MemberData(nameof(UnreadableViaPositions))]
    public async Task UnreadableViaField_InConditionPosition_RestrictsWholeReport(string position)
    {
        await Arrange(salesReadCustomer: false);
        var since = await MustAdd("customer", FieldType.Date, label: "往來起日");
        var viaDate = new ReportFieldRef(Main, since.Id, CustomerRef.Id);
        var c = await Insert("customer", "admin", (Region, "北區"), (since, "2026-11-04"));
        await Insert("order", "sales01", (Amount, 1m), (CustomerRef, c));
        await Insert("order", "sales01", (Amount, 2m));
        var d = position switch
        {
            "filter" => Def(measures: [M("n", "countMain")], filters: [F(RegionVia, "eq", "北區")]),
            "ratio" => Def(measures: [M("r", "ratio", cond: F(RegionVia, "eq", "北區"))]),
            "primaryDate" => Def(measures: [M("n", "countMain")], primary: viaDate, range: new ReportDateRange("today", null)),
            _ => throw new ArgumentException(position),
        };

        As("admin");
        (await Run(d)).State.Should().Be("ok");

        As("sales01");
        ShouldBeRestricted(await Run(d), "地區", "往來起日", "customer");
    }

    [Fact]
    public async Task UnreadableViaField_PrimaryDateWithoutRange_RestrictsToo()
    {
        await Arrange(salesReadCustomer: false);
        var since = await MustAdd("customer", FieldType.Date, label: "往來起日");
        await Insert("order", "sales01", (Amount, 1m));
        As("sales01");
        ShouldBeRestricted(await Run(Def(measures: [M("n", "countMain")], primary: new ReportFieldRef(Main, since.Id, CustomerRef.Id))));
    }

    [Theory]
    [InlineData("sum")]
    [InlineData("avg")]
    [InlineData("min")]
    [InlineData("max")]
    public async Task UnreadableViaField_InMeasure_RestrictsWholeReport(string kind)
    {
        await Arrange(salesReadCustomer: false);
        var score = await MustAdd("customer", FieldType.Decimal, label: "客戶評分");
        var c = await Insert("customer", "admin", (score, 4242m));
        await Insert("order", "sales01", (CustomerRef, c));
        var d = Def(measures: [M("m", kind, new ReportFieldRef(Main, score.Id, CustomerRef.Id))]);

        As("admin");
        (await Run(d)).Total!["m"].Should().Be(4242m);

        As("sales01");
        var r = await Run(d);
        ShouldBeRestricted(r, "客戶評分", "customer");
        JsonSerializer.Serialize(r).Should().NotContain("4242");
    }

    public static TheoryData<string> DirectRefConditionPositions => new() { "filter", "ratio" };

    private ReportDefinition DirectRefAt(string position, long key) => position switch
    {
        "filter" => Def(measures: [M("n", "countMain")], filters: [F(MainF(CustomerRef), "eq", key)]),
        "ratio" => Def(measures: [M("r", "ratio", cond: F(MainF(CustomerRef), "eq", key))]),
        _ => throw new ArgumentException(position),
    };

    [Theory, MemberData(nameof(DirectRefConditionPositions))]
    public async Task DirectReference_TargetUnreadable_InCondition_RestrictsWholeReport(string position)
    {
        await Arrange(salesReadCustomer: false);
        var c = await Insert("customer", "admin", (Region, "北區"));
        await Insert("order", "sales01", (CustomerRef, c));
        var d = DirectRefAt(position, c);

        As("admin");
        (await Run(d)).State.Should().Be("ok");

        As("sales01");
        ShouldBeRestricted(await Run(d), "customer");
    }

    [Theory, MemberData(nameof(DirectRefConditionPositions))]
    public async Task DirectReference_TargetTitleHidden_InCondition_RestrictsWholeReport(string position)
    {
        await Arrange();
        var customer = await Detail("customer");
        await HideForSales(customer.Blocks[0].Fields.Single(f => f.Id == customer.TitleFieldId));
        var c = await Insert("customer", "sales01", (Region, "北區"));
        await Insert("order", "sales01", (CustomerRef, c));
        As("sales01");
        ShouldBeRestricted(await Run(DirectRefAt(position, c)));
    }

    [Fact]
    public async Task DirectReference_TargetModuleDisabled_InCondition_ModuleDisabled()
    {
        await Arrange();
        var c = await Insert("customer", "admin", (Region, "北區"));
        await Insert("order", "admin", (CustomerRef, c));
        (await Send(new DeactivateModuleRequest("customer"))).IsSuccess.Should().BeTrue();
        (await Run(DirectRefAt("filter", c))).State.Should().Be("moduleDisabled");
    }

    [Fact]
    public async Task DirectReference_TargetUnreadable_ColumnAndGroup_StayLabeled()
    {
        await Arrange(salesReadCustomer: false);
        var c = await Insert("customer", "admin", (Region, "北區"));
        await Insert("order", "sales01", (CustomerRef, c), (Amount, 5m));
        As("sales01");
        var r = await Run(Def(groups: [G(MainF(CustomerRef))], measures: [M("s", "sum", MainF(Amount))]));
        r.State.Should().Be("ok");
        GroupOf(r, "（無權限）").Values["s"].Should().Be(5m);
        (await Run(Def(columns: [MainF(CustomerRef)]))).DetailRows![0].Should().Equal("（無權限）");
    }

    [Fact]
    public async Task DirectReference_SingleRecordOutOfScope_InFilter_StaysRowLevel()
    {
        await Arrange();
        var mine = await Insert("customer", "sales01", (Region, "北"));
        var other = await Insert("customer", "sales02", (Region, "南"));
        await Insert("order", "sales01", (CustomerRef, mine));
        await Insert("order", "sales01", (CustomerRef, other));
        As("sales01");
        (await Run(DirectRefAt("filter", mine))).Total!["n"].Should().Be(1m);
        var guess = await Run(DirectRefAt("filter", other)); // 目標模組可讀，只是這筆不在範圍內：逐列不符合
        guess.State.Should().Be("empty");
    }

    [Fact]
    public async Task HiddenViaReferenceField_Restricted()
    {
        await Arrange();
        await HideForSales(CustomerRef);
        await Insert("order", "sales01", (Note, "n"));
        As("sales01");
        ShouldBeRestricted(await Run(Def(columns: [RegionVia])), "客戶");
    }

    [Fact]
    public async Task HiddenChildField_Restricted()
    {
        await Arrange();
        await Insert("order", "sales01", (Note, "n"));
        As("sales01");
        ShouldBeRestricted(await Run(Def(Shipments, columns: [ChildF(ShipSecret)])), "出貨機密");
    }

    [Fact]
    public async Task HiddenLinkField_Restricted()
    {
        await Arrange();
        await HideForSales(LinkField);
        await Insert("order", "sales01", (Note, "n"));
        As("sales01");
        ShouldBeRestricted(await Run(Def(Shipments, measures: [M("n", "countChild")])), "所屬訂單");
    }

    private async Task HideForSales(FieldResponse f)
    {
        Db.Context.Set<FieldPermission>().Add(new FieldPermission { FieldId = f.Id, GroupId = SalesGroupId, Access = FieldAccess.Hidden });
        await Db.Context.SaveChangesAsync();
    }

    [Fact]
    public async Task Scope_Own_vs_All_DifferentNumbers()
    {
        await Arrange();
        await Insert("order", "sales01", (Amount, 100m));
        await Insert("order", "sales02", (Amount, 900m));
        var d = Def(measures: [M("sum", "sum", MainF(Amount))]);

        As("sales01");
        (await Run(d)).Total!["sum"].Should().Be(100m);
        As("sales02");
        (await Run(d)).Total!["sum"].Should().Be(900m);
        As("manager01");
        (await Run(d)).Total!["sum"].Should().Be(1000m);
    }

    [Fact]
    public async Task Scope_Group_CountsGroupMatesOnly()
    {
        await Arrange();
        var orderId = (await Detail("order")).Id;
        var scope = Db.Context.Set<ModuleDataScope>().Single(x => x.ModuleId == orderId && x.GroupId == SalesGroupId);
        scope.Scope = DataScope.Group;
        await Db.Context.SaveChangesAsync();
        await Insert("order", "sales01", (Amount, 100m));
        await Insert("order", "sales02", (Amount, 900m));
        await Insert("order", "manager01", (Amount, 5000m));
        var d = Def(measures: [M("sum", "sum", MainF(Amount))]);

        As("sales01");
        (await Run(d)).Total!["sum"].Should().Be(1000m);
        As("manager01");
        (await Run(d)).Total!["sum"].Should().Be(6000m);
    }

    [Fact]
    public async Task ChildModule_NoRead_Restricted()
    {
        await Arrange();
        await NewModule("visit");
        var link = await MustAdd("visit", FieldType.Reference, RefTo("order"), "訂單");
        (await Send(new ActivateModuleRequest("visit"))).IsSuccess.Should().BeTrue();
        await Insert("order", "sales01", (Amount, 1m));
        As("sales01");
        var r = await Run(Def(new ReportChild("visit", link.Id), measures: [M("n", "countMain")]));
        ShouldBeRestricted(r, "visit");
    }

    [Fact]
    public async Task ChildModule_BackReferenceWithRead_UsesItsOwnScope()
    {
        await Arrange();
        await NewModule("visit");
        var link = await MustAdd("visit", FieldType.Reference, RefTo("order"), "訂單");
        (await Send(new ActivateModuleRequest("visit"))).IsSuccess.Should().BeTrue();
        await Grant(SalesGroupId, "visit", DataScope.Own, read: true, pricing: true);
        var o = await Insert("order", "sales01", (Amount, 1m));
        await Insert("visit", "sales01", (link, o));
        await Insert("visit", "sales02", (link, o));
        As("sales01");
        var r = await Run(Def(new ReportChild("visit", link.Id), measures: [M("kids", "countChild")]));
        r.Total!["kids"].Should().Be(1m);
    }

    [Fact]
    public async Task LinkFieldNotPointingToMain_FieldMissing()
    {
        await Arrange();
        (await Run(Def(new ReportChild("shipment", Qty.Id), measures: [M("n", "countMain")]))).State.Should().Be("fieldMissing");
    }

    [Fact]
    public async Task RollupField_Restricted()
    {
        await Arrange();
        var rollup = await AddRollupToDraft();
        await Insert("order", "admin", (Amount, 1m));
        // 系統管理員也一樣：彙總存全域值，不看讀者範圍
        As("admin");
        foreach (var f in new[] { rollup.Rollup, rollup.Indirect })
            ShouldBeRestricted(await Run(Def(measures: [M("s", "sum", MainF(f))])));
    }

    [Fact]
    public async Task RollupOnViaTarget_RestrictedInEveryPosition()
    {
        await Arrange();
        As("admin");
        // customer 上的彙總：來源是新的子模組（指回 customer 的明細）
        await NewModule("visit");
        var visitLink = await MustAdd("visit", FieldType.Reference, RefTo("customer", ReferenceRelation.Detail), "所屬客戶");
        var hours = await MustAdd("visit", FieldType.Integer, label: "時數");
        (await Send(new DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule.ActivateModuleRequest("visit"))).IsSuccess.Should().BeTrue();
        var rollup = await MustAdd("customer", FieldType.Rollup,
            new FieldConfig { Rollup = new RollupConfig(visitLink.Id, RollupFunction.Sum, hours.Id, null) }, "彙總時數");
        var indirect = await MustAdd("customer", FieldType.Formula,
            new FieldConfig { Formula = $"{{{rollup.Id}}}*2", ResultType = "number" }, "間接公式");
        await Insert("order", "admin", (Amount, 1m));

        foreach (var f in new[] { rollup, indirect })
        {
            var via = new ReportFieldRef(Main, f.Id, CustomerRef.Id);
            ShouldBeRestricted(await Run(Def(columns: [via])));
            ShouldBeRestricted(await Run(Def(groups: [G(via)], measures: [M("n", "countMain")])));
            ShouldBeRestricted(await Run(Def(measures: [M("s", "sum", via)])));
            ShouldBeRestricted(await Run(Def(measures: [M("n", "countMain")], filters: [F(via, "gt", 1)])));
        }
    }

    private async Task<(FieldResponse Rollup, FieldResponse Indirect)> AddRollupToDraft()
    {
        var total = await MustAdd("order", FieldType.Rollup,
            new FieldConfig { Rollup = new RollupConfig(LinkField.Id, RollupFunction.Sum, Qty.Id, null) }, "彙總數量");
        var indirect = await MustAdd("order", FieldType.Formula,
            new FieldConfig { Formula = $"{{{total.Id}}}*2", ResultType = "number" }, "間接公式");
        return (total, indirect);
    }

    [Fact]
    public async Task NoModuleRead_Restricted()
    {
        await Arrange(salesRead: false);
        await Insert("order", "sales01", (Amount, 1m));
        As("sales01");
        ShouldBeRestricted(await Run(Def(measures: [M("n", "countMain")])), "order", "訂單");
    }

    // ---- 金額權限（Pricing） ----

    private FieldResponse Priced(string name) => name switch
    {
        "currency" => Price,
        "formulaCurrency" => FormulaCurrency,
        "formulaFromPrice" => FormulaFromPrice,
        _ => throw new ArgumentException(name),
    };

    private ReportDefinition PricedAt(FieldResponse f, string position) => position switch
    {
        "column" => Def(columns: [MainF(f)]),
        "group" => Def(groups: [G(MainF(f))], measures: [M("n", "countMain")]),
        "sum" => Def(measures: [M("s", "sum", MainF(f))]),
        "filter" => Def(measures: [M("n", "countMain")], filters: [F(MainF(f), "gte", 1000)]),
        "sort" => Def(columns: [MainF(Note)], sort: [new ReportSort(ReportCalculator.KeyOf(MainF(f)), false)]),
        "ratio" => Def(measures: [M("r", "ratio", cond: F(MainF(f), "gte", 1000))]),
        _ => throw new ArgumentException(position),
    };

    public static IEnumerable<object[]> PricedMatrix =>
        from f in new[] { "currency", "formulaCurrency", "formulaFromPrice" }
        from p in new[] { "column", "group", "sum", "filter", "sort", "ratio" }
        select new object[] { f, p };

    [Theory, MemberData(nameof(PricedMatrix))]
    public async Task Pricing_Missing_CurrencyField_Restricted(string fieldName, string position)
    {
        await Arrange(salesPricing: false);
        await Insert("order", "sales01", (Amount, 4242m), (Price, 4242m), (FormulaCurrency, 8484m), (FormulaFromPrice, 8484m), (Note, "n"));
        var f = Priced(fieldName);
        var d = PricedAt(f, position);

        As("manager01"); // 對照組：有 Pricing 且資料範圍涵蓋
        (await Run(d)).State.Should().Be("ok");

        As("sales01");
        var r = await Run(d);
        ShouldBeRestricted(r, "牌價", "金額結果公式", "引用牌價的數字公式");
        var json = JsonSerializer.Serialize(r);
        json.Should().NotContain("4242").And.NotContain("8484");
    }

    [Theory]
    [InlineData("currency")]
    [InlineData("formulaCurrency")]
    [InlineData("formulaFromPrice")]
    public async Task Pricing_Missing_Drill_Restricted_NoValues(string fieldName)
    {
        await Arrange(salesPricing: false);
        await Insert("order", "sales01", (Price, 4242m), (FormulaCurrency, 4242m), (FormulaFromPrice, 4242m));
        var f = Priced(fieldName);
        var d = Def(columns: [MainF(f)], groups: [G(MainF(f))], measures: [M("s", "sum", MainF(f))]);

        As("manager01"); // 對照組：有 Pricing 的人下鑽看得到數值，證明這個斷言抓得到外洩
        var seen = await Run(d, drill: ["4242"]);
        seen.State.Should().Be("ok");
        JsonSerializer.Serialize(seen).Should().Contain("4242");

        As("sales01");
        var r = await Run(d, drill: ["4242"]);
        ShouldBeRestricted(r, "牌價", "金額結果公式", "引用牌價的數字公式");
        JsonSerializer.Serialize(r).Should().NotContain("4242");
    }

    [Fact]
    public async Task Pricing_Missing_FilterGuess_GivesNoSignal()
    {
        await Arrange(salesPricing: false);
        await Insert("order", "sales01", (Price, 4242m));
        As("sales01");
        var hit = await Run(Def(measures: [M("n", "countMain")], filters: [F(MainF(Price), "eq", 4242)]));
        var miss = await Run(Def(measures: [M("n", "countMain")], filters: [F(MainF(Price), "eq", 1)]));
        JsonSerializer.Serialize(hit).Should().Be(JsonSerializer.Serialize(miss));
    }

    [Fact]
    public async Task Pricing_Missing_OtherFieldsStillWork()
    {
        await Arrange(salesPricing: false);
        await Insert("order", "sales01", (Amount, 10m), (Price, 4242m));
        As("sales01");
        var r = await Run(Def(columns: [MainF(Amount)], measures: [M("s", "sum", MainF(Amount))]));
        r.State.Should().Be("ok");
        r.Total!["s"].Should().Be(10m);
        JsonSerializer.Serialize(r).Should().NotContain("4242");
    }

    [Fact]
    public async Task Pricing_Missing_DetailCurrencyFollowsHeader()
    {
        await Arrange(salesPricing: false);
        var o = await Insert("order", "sales01", (Amount, 1m));
        await Insert("shipment", "sales01", (LinkField, o), (ShipPrice, 4242m));
        As("sales01");
        ShouldBeRestricted(await Run(Def(Shipments, measures: [M("s", "sum", ChildF(ShipPrice))])), "出貨單價");
    }

    [Fact]
    public async Task Pricing_Missing_ViaTargetCurrency_Restricted()
    {
        await Arrange(salesPricing: false);
        var c = await Insert("customer", "sales01", (CustomerPrice, 4242m));
        await Insert("order", "sales01", (CustomerRef, c));
        As("sales01");
        ShouldBeRestricted(await Run(Def(measures: [M("s", "sum", new ReportFieldRef(Main, CustomerPrice.Id, CustomerRef.Id))])), "客戶額度");
    }

    [Fact]
    public async Task Pricing_Granted_SeesNumbers()
    {
        await Arrange();
        await Insert("order", "sales01", (Price, 4242m));
        As("sales01");
        (await Run(Def(measures: [M("s", "sum", MainF(Price))]))).Total!["s"].Should().Be(4242m);
    }

    // ---- 參照目標模組的權限不外溢 ----

    [Fact]
    public async Task RestrictedVia_OtherOwnerTitle_NotInDirectReferenceColumn()
    {
        await Arrange();
        var other = await Insert("customer", "sales02", (Region, "南"));
        await Insert("order", "sales01", (CustomerRef, other));
        As("sales01");
        var r = await Run(Def(columns: [MainF(CustomerRef)]));
        r.DetailRows![0].Should().Equal("（無權限）");
    }
}
