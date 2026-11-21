using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>
/// 報表的彙總遮罩回歸（#74、#75）：customer 上以一般參照（訂單的客戶欄）為來源、加總或以「機密成本」（對 sales 隱藏）為條件的彙總。
/// 彙總本來就不能用於報表；sales01 這邊要當成隱藏欄位（訊息不帶彙總名稱），admin 照舊是「不能用於報表」並帶名稱。
/// 這個類別走記憶體引擎，<see cref="ReportRollupMaskSqlTests"/> 走 SQL 聚合。
/// </summary>
[Trait("Category", "Postgres")]
public class ReportRollupMaskTests : ReportTestBase
{
    private async Task<(FieldResponse Sum, FieldResponse Count, FieldResponse Plain)> Rollups()
    {
        await Arrange();
        As("admin");
        var sum = await MustAdd("customer", FieldType.Rollup,
            new FieldConfig { Rollup = new RollupConfig(CustomerRef.Id, RollupFunction.Sum, SecretNum.Id, null) }, "機密成本合計");
        var count = await MustAdd("customer", FieldType.Rollup, new FieldConfig
        {
            Rollup = new RollupConfig(CustomerRef.Id, RollupFunction.Count, null, J($$"""{"field":{{SecretNum.Id}},"op":"gt","value":1}"""))
        }, "高成本筆數");
        var plain = await MustAdd("customer", FieldType.Rollup,
            new FieldConfig { Rollup = new RollupConfig(CustomerRef.Id, RollupFunction.Sum, Amount.Id, null) }, "訂單金額合計");
        await TwoOrders();
        return (sum, count, plain);
    }

    private async Task<ReportResult> As(string user, ReportDefinition d)
    {
        As(user);
        return await Run(d);
    }

    [Theory]
    [InlineData("sum")]
    [InlineData("count")]
    public async Task Rollup_OverHiddenSourceField_HiddenForReader(string which)
    {
        var (sum, count, _) = await Rollups();
        var (f, name) = which == "sum" ? (sum, "機密成本合計") : (count, "高成本筆數");
        var direct = Def(main: "customer", columns: [MainF(f)]);
        var via = Def(columns: [new ReportFieldRef(Main, f.Id, CustomerRef.Id)]);

        foreach (var d in new[] { direct, via })
        {
            var reader = await As("sales01", d);
            reader.State.Should().Be("restricted");
            reader.Message.Should().NotContain(name, "讀者看不到的彙總，訊息不帶它的名稱");
            var admin = await As("admin", d);
            admin.State.Should().Be("restricted", "彙總本來就不能用於報表");
            admin.Message.Should().Contain(name);
        }
    }

    [Fact]
    public async Task Rollup_OverVisibleField_UnchangedForReader()
    {
        var (_, _, plain) = await Rollups();
        var reader = await As("sales01", Def(main: "customer", columns: [MainF(plain)]));
        reader.State.Should().Be("restricted");
        reader.Message.Should().Contain("訂單金額合計", "對照：加總對象看得到時照舊是「不能用於報表」");
        (await As("sales01", Def(main: "customer", columns: [MainF(Region)]))).State.Should().BeOneOf(["ok", "empty"], "模組其他欄位不受影響");
    }
}

/// <summary>同一組案例走 SQL 聚合路徑。</summary>
[Trait("Category", "Postgres")]
public class ReportRollupMaskSqlTests : ReportRollupMaskTests
{
    protected override bool FieldsFilterable => true;
}
