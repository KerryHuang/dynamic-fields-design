using System.Text.Json;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddOption;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Commands.DeactivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.ReportService;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Persistence.Dynamic.Dialects;
using DynamicModules.Persistence.Repositories.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>
/// 報表測試共用資料：customer（地區）、order（金額等）、shipment（order 的明細，數量與倉庫）。
/// 基準日是 DesignerTestBase 的 2026-11-04。sales 群組範圍 Own，sales-manager 群組範圍 All。
/// </summary>
public abstract class ReportTestBase : DesignerTestBase
{
    protected FieldResponse Region = null!, CustomerSecret = null!, CustomerPrice = null!;
    protected FieldResponse OrderTitle = null!, Amount = null!, Price = null!, CustomerRef = null!, Status = null!, Tags = null!;
    protected FieldResponse Sales = null!, Due = null!, At = null!, Secret = null!, SecretNum = null!, SecretDate = null!, Note = null!;
    protected FieldResponse FormulaCurrency = null!, FormulaFromPrice = null!;
    protected FieldResponse LinkField = null!, Qty = null!, Warehouse = null!, ShipPrice = null!, ShipSecret = null!;
    protected string OptOpen = "", OptDone = "", TagA = "", TagB = "";
    private long _salesGroup, _managerGroup;

    protected const string Main = "main", Child = "child";

    protected static ReportFieldRef MainF(FieldResponse f) => new(Main, f.Id, null);
    protected static ReportFieldRef ChildF(FieldResponse f) => new(Child, f.Id, null);
    protected ReportFieldRef RegionVia => new(Main, Region.Id, CustomerRef.Id);
    protected ReportChild Shipments => new("shipment", LinkField.Id);

    protected static ReportMeasure M(string id, string kind, ReportFieldRef? field = null, ReportFilter? cond = null) => new(id, kind, field, cond);
    protected static ReportFilter F(ReportFieldRef field, string op, object? value = null) =>
        new(field, op, value is null ? null : JsonSerializer.SerializeToElement(value));

    protected static ReportDefinition Def(
        ReportChild? child = null, List<ReportFieldRef>? columns = null, List<ReportGroup>? groups = null,
        List<ReportMeasure>? measures = null, List<ReportFilter>? filters = null, ReportFieldRef? primary = null,
        ReportDateRange? range = null, List<ReportSort>? sort = null, string main = "order") =>
        new(1, main, child, columns ?? [], groups ?? [], measures ?? [], filters ?? [], primary, range, sort ?? [], "table");

    protected static ReportGroup G(ReportFieldRef f, string? bucket = null) => new(f, bucket);

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<ReportQueryRepository>();
        services.AddScoped<IReportQueryRepository>(sp => new ScopeCheckingReportQueries(sp.GetRequiredService<ReportQueryRepository>(), sp.GetRequiredService<ISqlDialect>()));
    }

    /// <summary>
    /// SQL 版本的子類別（<see cref="DesignerTestBase.FieldsFilterable"/>）退回記憶體路徑時，原因只能是報表形狀或資料本身；
    /// 這些原因代表測試環境沒把索引備妥（欄位沒勾可篩選、版本列沒寫），等於沒測到 SQL 路徑。
    /// </summary>
    private static readonly string[] SetupProblems = ["indexBackfillPending", "indexMissing", "indexIncomplete", "configChanged", "fieldNotIndexed", ReportSqlOutcome.TimeoutReason];

    /// <summary>
    /// 一般版本直接跑（引擎為預設的 Auto，欄位沒勾可篩選，所以走記憶體路徑）。
    /// SQL 版本先用 SqlOnly 跑，形狀不能走 SQL 時（SqlOnly 丟例外）改用 Auto 重跑，並斷言走了記憶體路徑、退回原因不是環境問題；
    /// 兩種情形都回傳結果，讓同一組斷言再驗一次（設計 §7 第 1 項）。
    /// </summary>
    protected async Task<ReportResult> Run(ReportDefinition d, ReportDateRange? range = null, IReadOnlyList<string>? drill = null, string? locale = null)
    {
        var runner = Svc<ReportRunner>();
        var o = new ReportRunOptions(range, drill, locale);
        if (!FieldsFilterable) return await runner.RunAsync(d, o, CancellationToken.None);

        var options = Svc<ReportOptions>();
        options.Engine = ReportEngineMode.SqlOnly;
        try
        {
            return await runner.RunAsync(d, o, CancellationToken.None);
        }
        catch (InvalidOperationException e) when (e.Message.StartsWith("SqlOnly 模式"))
        {
            // 形狀不能走 SQL：下面改用 Auto
        }
        finally
        {
            options.Engine = ReportEngineMode.Auto;
        }
        var fallback = await runner.RunAsync(d, o, CancellationToken.None);
        // 記憶體路徑在載入或組列時就失敗（tooMany 等）的結果不帶 Engine
        fallback.Engine.Should().BeOneOf("memory", null);
        (fallback.FallbackReasons ?? []).Should().NotContain(r => SetupProblems.Any(p => r.Contains(p)), "SQL 版本的測試環境要讓索引可用");
        return fallback;
    }

    protected async Task Arrange(bool salesPricing = true, bool salesReadCustomer = true, bool salesRead = true)
    {
        As("admin");
        await NewModule("customer");
        Region = await MustAdd("customer", FieldType.Text, label: "地區");
        CustomerSecret = await MustAdd("customer", FieldType.Text, label: "客戶機密");
        CustomerPrice = await MustAdd("customer", FieldType.Currency, label: "客戶額度");

        await NewModule("order");
        OrderTitle = (await Detail("order")).Blocks[0].Fields[0];
        Amount = await MustAdd("order", FieldType.Decimal, label: "訂單金額");
        Price = await MustAdd("order", FieldType.Currency, label: "牌價");
        CustomerRef = await MustAdd("order", FieldType.Reference, RefTo("customer"), "客戶");
        Status = await MustAdd("order", FieldType.Enum, label: "狀態");
        // 新增順序與 Sequence 相同：先「進行中」再「已完成」
        OptOpen = await AddOption(Status, "進行中", "Open");
        OptDone = await AddOption(Status, "已完成", "Done");
        Tags = await MustAdd("order", FieldType.MultiEnum, label: "標籤");
        TagA = await AddOption(Tags, "甲");
        TagB = await AddOption(Tags, "乙");
        Sales = await MustAdd("order", FieldType.User, label: "負責業務");
        Due = await MustAdd("order", FieldType.Date, label: "交期");
        At = await MustAdd("order", FieldType.DateTime, label: "接單時間");
        Secret = await MustAdd("order", FieldType.Text, label: "機密備註");
        SecretNum = await MustAdd("order", FieldType.Decimal, label: "機密成本");
        SecretDate = await MustAdd("order", FieldType.Date, label: "機密日期");
        Note = await MustAdd("order", FieldType.Text, label: "備註");
        FormulaCurrency = await MustAdd("order", FieldType.Formula,
            new FieldConfig { Formula = $"{{{Amount.Id}}}*2", ResultType = "currency" }, "金額結果公式");
        FormulaFromPrice = await MustAdd("order", FieldType.Formula,
            new FieldConfig { Formula = $"{{{Price.Id}}}*2", ResultType = "number" }, "引用牌價的數字公式");

        await NewModule("shipment");
        LinkField = await MustAdd("shipment", FieldType.Reference, RefTo("order", ReferenceRelation.Detail), "所屬訂單");
        Qty = await MustAdd("shipment", FieldType.Integer, label: "出貨數量");
        Warehouse = await MustAdd("shipment", FieldType.Text, label: "倉庫");
        ShipPrice = await MustAdd("shipment", FieldType.Currency, label: "出貨單價");
        ShipSecret = await MustAdd("shipment", FieldType.Text, label: "出貨機密");

        foreach (var key in new[] { "customer", "order", "shipment" })
            (await Send(new ActivateModuleRequest(key))).IsSuccess.Should().BeTrue();
        // 模組自帶的標題欄位不經 AddField，SQL 版本要另外勾可篩選
        if (FieldsFilterable)
            foreach (var key in new[] { "customer", "order", "shipment" })
            {
                var title = (await Detail(key)).Blocks[0].Fields[0];
                (await Update(title, title.Config with { IsFilterable = true })).IsSuccess.Should().BeTrue();
            }

        var ctx = Db.Context;
        _salesGroup = (await ctx.Set<PermissionGroup>().SingleAsync(g => g.Key == "sales")).Id;
        _managerGroup = (await ctx.Set<PermissionGroup>().SingleAsync(g => g.Key == "sales-manager")).Id;
        foreach (var (gid, scope, pricing) in new[] { (_salesGroup, DataScope.Own, salesPricing), (_managerGroup, DataScope.All, true) })
        {
            await Grant(gid, "order", scope, salesRead || gid == _managerGroup, pricing);
            await Grant(gid, "customer", DataScope.Own, gid == _managerGroup || salesReadCustomer, pricing);
        }
        foreach (var f in new[] { Secret, SecretNum, SecretDate })
            ctx.Set<FieldPermission>().Add(new FieldPermission { FieldId = f.Id, GroupId = _salesGroup, Access = FieldAccess.Hidden });
        foreach (var f in new[] { CustomerSecret, ShipSecret })
            ctx.Set<FieldPermission>().Add(new FieldPermission { FieldId = f.Id, GroupId = _salesGroup, Access = FieldAccess.Hidden });
        await ctx.SaveChangesAsync();
    }

    protected async Task Grant(long groupId, string module, DataScope scope, bool read, bool pricing)
    {
        var m = await Detail(module);
        Db.Context.Set<GroupProgramPrivilege>().Add(new GroupProgramPrivilege
        {
            GroupId = groupId, ProgramCode = m.ProgramCode, CanRead = read, CanAmt = pricing
        });
        Db.Context.Set<ModuleDataScope>().Add(new ModuleDataScope { ModuleId = m.Id, GroupId = groupId, Scope = scope });
        await Db.Context.SaveChangesAsync();
    }

    protected long SalesGroupId => _salesGroup;

    private async Task<string> AddOption(FieldResponse field, string zh, string? en = null)
    {
        var label = Text(zh);
        if (en is not null) label.Set("en-US", en);
        return ((OptionResponse)(await Send(new AddOptionRequest(field.Id, label, null))).Data!).Value;
    }

    /// <summary>直接寫進資料表，可指定擁有者與任意原始值（含 UTC 字串）。</summary>
    protected async Task<long> Insert(string module, string owner, params (FieldResponse Field, object? Value)[] values)
    {
        var m = await Db.Context.Set<Module>().AsNoTracking().SingleAsync(x => x.Key == module);
        var json = JsonSerializer.Serialize(values.ToDictionary(v => v.Field.Key, v => v.Value));
        var row = new DynamicModules.Domain.Entities.Dynamic.Record
        {
            ModuleId = m.Id, Title = "row", OwnerUserId = owner, ValuesJson = json, RowVersion = "v1"
        };
        row.StampCreated(owner, new DateTime(2026, 11, 4));
        Db.Context.Add(row);
        await Db.Context.SaveChangesAsync();
        if (FieldsFilterable)
        {
            // 直接寫表不經 CreateRecordHandler，索引要自己補，SQL 路徑才看得到這筆
            var withFields = (await Svc<IModuleRepository>().GetByKeyAsync(module, true, CancellationToken.None))!;
            var rows = RecordIndexer.Build(withFields, RecordValueProcessor.Parse(json), Svc<FieldTypeRegistry>());
            foreach (var index in rows) index.RecordId = row.Id;
            Db.Context.AddRange(rows);
            await Db.Context.SaveChangesAsync();
        }
        return row.Id;
    }

    protected async Task SoftDelete(long id)
    {
        var row = await Db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().SingleAsync(r => r.Id == id);
        row.DeleteFlag = true;
        await Db.Context.SaveChangesAsync();
    }

    /// <summary>Task 2 的數字：訂單 A 金額 100、3 筆出貨（1,2,3）；訂單 B 金額 50、無出貨。回傳 A、B 的 Id。</summary>
    protected async Task<(long A, long B)> TwoOrders(string owner = "admin")
    {
        var a = await Insert("order", owner, (Amount, 100m));
        var b = await Insert("order", owner, (Amount, 50m));
        await Insert("shipment", owner, (LinkField, a), (Qty, 1), (Warehouse, "X"));
        await Insert("shipment", owner, (LinkField, a), (Qty, 2), (Warehouse, "Y"));
        await Insert("shipment", owner, (LinkField, a), (Qty, 3), (Warehouse, "Y"));
        return (a, b);
    }

    protected static ReportGroupResult GroupOf(ReportResult r, string label) => r.Groups!.Single(g => g.GroupLabels[0] == label);
}

public class ReportRunnerTests : ReportTestBase
{
    [Fact]
    public async Task Run_MergedOrderShipment_DedupesAmount()
    {
        await Arrange();
        await TwoOrders();
        var r = await Run(Def(Shipments, measures:
        [
            M("sum", "sum", MainF(Amount)), M("avg", "avg", MainF(Amount)), M("qty", "sum", ChildF(Qty)),
            M("mains", "countMain"), M("kids", "countChild")
        ]));
        r.State.Should().Be("ok");
        r.Total!["sum"].Should().Be(150m);
        r.Total["avg"].Should().Be(75m);
        r.Total["qty"].Should().Be(6m);
        r.Total["mains"].Should().Be(2m);
        r.Total["kids"].Should().Be(3m);
        r.DedupedNote.Should().BeTrue();
        r.RowMeaning.Should().Be("一筆shipment");
    }

    [Fact]
    public async Task Run_GroupByChildField_ParentInTwoGroups_TotalIsNotSum()
    {
        await Arrange();
        await TwoOrders();
        var r = await Run(Def(Shipments, groups: [G(ChildF(Warehouse))], measures: [M("sum", "sum", MainF(Amount))]));
        GroupOf(r, "X").Values["sum"].Should().Be(100m);
        GroupOf(r, "Y").Values["sum"].Should().Be(100m);
        GroupOf(r, "（空白）").Values["sum"].Should().Be(50m);
        r.Total!["sum"].Should().Be(150m);
    }

    [Fact]
    public async Task Run_SoftDeleted_MainAndChild_NotCounted()
    {
        await Arrange();
        var (a, _) = await TwoOrders();
        var c = await Insert("order", "admin", (Amount, 7m));
        var kid = await Insert("shipment", "admin", (LinkField, a), (Qty, 40));
        await Insert("shipment", "admin", (LinkField, c), (Qty, 500));
        await SoftDelete(c);
        await SoftDelete(kid);
        var r = await Run(Def(Shipments, measures: [M("sum", "sum", MainF(Amount)), M("qty", "sum", ChildF(Qty)), M("kids", "countChild")]));
        r.Total!["sum"].Should().Be(150m);
        r.Total["qty"].Should().Be(6m);
        r.Total["kids"].Should().Be(3m);
    }

    // ---- 帶出欄位 ----

    private async Task<(long North, long Other)> Customers()
    {
        var north = await Insert("customer", "sales01", (Region, "北區"));
        var other = await Insert("customer", "sales02", (Region, "機密地區"));
        return (north, other);
    }

    [Fact]
    public async Task Run_ViaReference_GroupsByCustomerRegion()
    {
        await Arrange();
        var (north, other) = await Customers();
        await Insert("order", "admin", (Amount, 10m), (CustomerRef, north));
        await Insert("order", "admin", (Amount, 20m), (CustomerRef, north));
        await Insert("order", "admin", (Amount, 5m), (CustomerRef, other));
        var r = await Run(Def(groups: [G(RegionVia)], measures: [M("sum", "sum", MainF(Amount))]));
        GroupOf(r, "北區").Values["sum"].Should().Be(30m);
        GroupOf(r, "機密地區").Values["sum"].Should().Be(5m);
        r.Columns[0].Label.Should().Be("客戶．地區");
    }

    [Fact]
    public async Task Run_ViaReference_TargetDeleted_FormsMissingGroup()
    {
        await Arrange();
        var (north, other) = await Customers();
        await Insert("order", "admin", (Amount, 10m), (CustomerRef, north));
        await Insert("order", "admin", (Amount, 5m), (CustomerRef, other));
        await SoftDelete(other);
        var r = await Run(Def(groups: [G(RegionVia)], measures: [M("sum", "sum", MainF(Amount))]));
        var g = GroupOf(r, "（已刪除）");
        g.GroupKeys[0].Should().Be(ReportCalculator.MissingKey);
        g.Values["sum"].Should().Be(5m);
    }

    [Fact]
    public async Task Run_ViaReference_TargetOutOfScope_FormsRestrictedGroup_WithoutValue()
    {
        await Arrange();
        var (north, other) = await Customers();
        await Insert("order", "sales01", (Amount, 10m), (CustomerRef, north));
        await Insert("order", "sales01", (Amount, 5m), (CustomerRef, other));
        As("sales01");
        var r = await Run(Def(columns: [RegionVia], groups: [G(RegionVia)], measures: [M("sum", "sum", MainF(Amount))]));
        var g = GroupOf(r, "（無權限）");
        g.GroupKeys[0].Should().Be(ReportCalculator.RestrictedKey);
        g.Values["sum"].Should().Be(5m);
        JsonSerializer.Serialize(r).Should().NotContain("機密地區");
    }

    [Fact]
    public async Task Run_ViaFilter_RestrictedTarget_NeverMatches_NoOracle()
    {
        await Arrange();
        var (north, other) = await Customers();
        await Insert("order", "sales01", (Amount, 10m), (CustomerRef, north));
        await Insert("order", "sales01", (Amount, 5m), (CustomerRef, other));
        As("sales01");
        // 猜中或猜錯目標的值，結果必須一樣：看不到的目標永遠不符合
        foreach (var guess in new[] { "機密地區", "猜錯" })
        {
            var hit = await Run(Def(measures: [M("n", "countMain")], filters: [F(RegionVia, "eq", guess)]));
            hit.State.Should().Be("empty");
            var miss = await Run(Def(measures: [M("n", "countMain")], filters: [F(RegionVia, "ne", guess)]));
            miss.Total!["n"].Should().Be(1m);
        }
    }

    [Fact]
    public async Task Run_ViaTargetModuleNoRead_AllValuesRestricted_NotWholeReport()
    {
        await Arrange(salesReadCustomer: false);
        var (north, _) = await Customers();
        await Insert("order", "sales01", (Amount, 10m), (CustomerRef, north));
        await Insert("order", "sales01", (Amount, 3m));
        As("sales01");
        var r = await Run(Def(columns: [RegionVia], groups: [G(RegionVia)], measures: [M("sum", "sum", MainF(Amount))]));
        r.State.Should().Be("ok");
        GroupOf(r, "（無權限）").Values["sum"].Should().Be(10m);
        GroupOf(r, "（空白）").Values["sum"].Should().Be(3m);
        r.Columns[0].Label.Should().Be("隱藏欄位");
        JsonSerializer.Serialize(r).Should().NotContain("北區");
    }

    [Fact]
    public async Task Run_ViaEntityTarget_OnlyTitle_GroupsByKey_DeletedIsMissing()
    {
        await Arrange();
        var mold = await MustAdd("order", FieldType.Reference,
            new FieldConfig { RefTarget = new ReferenceTarget(ReferenceTarget.EntityKind, "test.entity") }, "模具");
        await Insert("order", "admin", (mold, "E-0001"), (Amount, 1m));
        await Insert("order", "admin", (mold, "NO-SUCH"), (Amount, 2m));
        var via = new ReportFieldRef(Main, ReportRunner.EntityTitleFieldId, mold.Id);
        var r = await Run(Def(groups: [G(via)], measures: [M("sum", "sum", MainF(Amount))]));
        var g = r.Groups!.Single(x => x.GroupKeys[0] == "E-0001");
        g.GroupLabels[0].Should().StartWith("E-0001 ");
        GroupOf(r, "（已刪除）").Values["sum"].Should().Be(2m);
        r.Columns[0].Label.Should().Be("模具．標題");

        (await Run(Def(columns: [new ReportFieldRef(Main, Region.Id, mold.Id)]))).State.Should().Be("fieldMissing");
    }

    [Fact]
    public async Task Run_ViaTargetModuleDisabled_ReturnsModuleDisabled()
    {
        await Arrange();
        (await Send(new DeactivateModuleRequest("customer"))).IsSuccess.Should().BeTrue();
        (await Run(Def(columns: [RegionVia]))).State.Should().Be("moduleDisabled");
    }

    [Fact]
    public async Task Run_ChildSourceWithoutMerge_ReturnsFieldMissing()
    {
        await Arrange();
        (await Run(Def(columns: [ChildF(Qty)]))).State.Should().Be("fieldMissing");
    }

    // ---- 語系 ----

    [Theory]
    [InlineData("en-US", "(Blank)", "(Deleted)", "(No permission)", " sum", "One ")]
    [InlineData("th", "(ว่าง)", "(ถูกลบแล้ว)", "(ไม่มีสิทธิ์)", "ผลรวม ", " หนึ่งรายการ")]
    [InlineData("vi", "(Trống)", "(Đã xóa)", "(Không có quyền)", "Tổng ", "Một ")]
    [InlineData("zh-TW", "（空白）", "（已刪除）", "（無權限）", " 加總", "一筆")]
    public async Task Run_SystemWordsFollowLocale(string locale, string blank, string deleted, string noPermission, string sumWord, string rowWord)
    {
        await Arrange();
        As("sales01");
        var mine = await Insert("customer", "sales01", (Region, "北區"));
        var others = await Insert("customer", "admin", (Region, "南區"));
        await Insert("order", "sales01", (Amount, 1m));
        await Insert("order", "sales01", (Amount, 1m), (CustomerRef, mine));
        await Insert("order", "sales01", (Amount, 1m), (CustomerRef, others));
        await Insert("order", "sales01", (Amount, 1m), (CustomerRef, 999_999));

        var r = await Run(Def(groups: [G(MainF(CustomerRef))], measures: [M("s", "sum", MainF(Amount))]), locale: locale);

        r.Groups!.Select(g => g.GroupLabels[0]).Should().Contain([blank, deleted, noPermission]);
        r.Columns.Single(c => c.Role == "measure").Label.Should().Contain(sumWord);
        r.RowMeaning.Should().Contain(rowWord);
    }

    [Theory]
    [InlineData("en-US", "Hidden field")]
    [InlineData("th", "ฟิลด์ที่ถูกซ่อน")]
    [InlineData("vi", "Trường bị ẩn")]
    public async Task Run_RestrictedMessage_FollowsLocale(string locale, string hidden)
    {
        await Arrange();
        As("sales01");
        var r = await Run(Def(columns: [MainF(Secret)]), locale: locale);
        r.State.Should().Be("restricted");
        r.Message.Should().Contain(hidden);
        r.Message.Should().NotContain("無權限").And.NotContain("隱藏");
    }

    // ---- 去重說明：列相加或下鑽列數對不上總計時都要說明 ----

    [Fact]
    public async Task Note_MergedCountMain_ShownAndKeptOnDrill()
    {
        await Arrange();
        await TwoOrders();
        var d = Def(Shipments, groups: [G(ChildF(Warehouse))], measures: [M("n", "countMain")]);
        var r = await Run(d);
        r.DedupedNote.Should().BeTrue();
        GroupOf(r, "Y").Values["n"].Should().Be(1m); // 下鑽有 2 列
        var drill = await Run(d, drill: ["Y"]);
        drill.DetailRows!.Should().HaveCount(2);
        drill.DedupedNote.Should().BeTrue();
    }

    [Fact]
    public async Task Note_NotMerged_GroupByMultiSelect_GroupsAddUpToMoreThanTotal()
    {
        await Arrange();
        await Insert("order", "admin", (Amount, 100m), (Tags, new[] { TagA, TagB }));
        var d = Def(groups: [G(MainF(Tags))], measures: [M("s", "sum", MainF(Amount))]);
        var r = await Run(d);
        r.Groups!.Sum(g => g.Values["s"]).Should().Be(200m);
        r.Total!["s"].Should().Be(100m);
        r.DedupedNote.Should().BeTrue();
        (await Run(d, drill: [TagA])).DedupedNote.Should().BeTrue();
    }

    [Fact]
    public async Task Note_GroupByMainMultiSelect_ChildMeasure()
    {
        await Arrange();
        var a = await Insert("order", "admin", (Amount, 100m), (Tags, new[] { TagA, TagB }));
        await Insert("shipment", "admin", (LinkField, a), (Qty, 3));
        var r = await Run(Def(Shipments, groups: [G(MainF(Tags))], measures: [M("q", "sum", ChildF(Qty))]));
        r.Groups!.Sum(g => g.Values["q"]).Should().Be(6m);
        r.Total!["q"].Should().Be(3m);
        r.DedupedNote.Should().BeTrue();
    }

    [Fact]
    public async Task Note_NotShown_WhenRowsAddUpOrMeasureIsNotAdditive()
    {
        await Arrange();
        var a = await Insert("order", "admin", (Amount, 100m), (Status, OptOpen), (Tags, new[] { TagA, TagB }));
        await Insert("shipment", "admin", (LinkField, a), (Qty, 3));
        (await Run(Def(groups: [G(MainF(Status))], measures: [M("s", "sum", MainF(Amount))]))).DedupedNote.Should().BeFalse();
        (await Run(Def(Shipments, groups: [G(ChildF(Warehouse))], measures: [M("q", "sum", ChildF(Qty))]))).DedupedNote.Should().BeFalse();
        (await Run(Def(Shipments, measures: [M("m", "max", MainF(Amount))]))).DedupedNote.Should().BeFalse();
        (await Run(Def(groups: [G(MainF(Tags))], measures: [M("m", "min", MainF(Amount))]))).DedupedNote.Should().BeFalse();
    }

    // ---- 子條件與左合併 ----

    [Fact]
    public async Task Run_ChildFilterRemovesAllChildren_DropsParentRow()
    {
        await Arrange();
        await TwoOrders();
        var r = await Run(Def(Shipments, measures: [M("sum", "sum", MainF(Amount)), M("kids", "countChild")],
            filters: [F(ChildF(Warehouse), "eq", "沒有這個倉")]));
        r.State.Should().Be("empty");

        var some = await Run(Def(Shipments, measures: [M("sum", "sum", MainF(Amount)), M("kids", "countChild")],
            filters: [F(ChildF(Warehouse), "eq", "Y")]));
        some.Total!["sum"].Should().Be(100m); // 訂單 B 沒有子記錄也被子條件排除
        some.Total["kids"].Should().Be(2m);
    }

    [Fact]
    public async Task Run_NoChildren_KeepsParentRow()
    {
        await Arrange();
        await TwoOrders();
        var r = await Run(Def(Shipments, measures: [M("mains", "countMain"), M("kids", "countChild")]));
        r.Total!["mains"].Should().Be(2m);
        r.Total["kids"].Should().Be(3m);
    }

    [Fact]
    public async Task Run_MainFilter_AppliedBeforeChildren()
    {
        await Arrange();
        await TwoOrders();
        var r = await Run(Def(Shipments, measures: [M("sum", "sum", MainF(Amount)), M("kids", "countChild")],
            filters: [F(MainF(Amount), "lt", 100)]));
        r.Total!["sum"].Should().Be(50m);
        r.Total["kids"].Should().Be(0m);
    }

    [Fact]
    public async Task Run_Filters_AreFlatAnd()
    {
        await Arrange();
        await Insert("order", "admin", (Amount, 10m), (Note, "甲"));
        await Insert("order", "admin", (Amount, 20m), (Note, "甲"));
        await Insert("order", "admin", (Amount, 20m), (Note, "乙"));
        var r = await Run(Def(measures: [M("n", "countMain")], filters: [F(MainF(Amount), "gte", 20), F(MainF(Note), "eq", "甲")]));
        r.Total!["n"].Should().Be(1m);
    }

    public static TheoryData<string, object?, int> NumberOps => new()
    {
        { "eq", 20, 1 }, { "ne", 20, 2 }, { "gt", 10, 1 }, { "gte", 10, 2 }, { "lt", 20, 1 }, { "lte", 20, 2 },
        { "between", new[] { 15, 25 }, 1 }, { "isEmpty", null, 1 }, { "isNotEmpty", null, 2 },
    };

    [Theory, MemberData(nameof(NumberOps))]
    public async Task Run_NumberOperators_FollowListFilterSemantics(string op, object? value, int expected)
    {
        await Arrange();
        await Insert("order", "admin", (Amount, 10m));
        await Insert("order", "admin", (Amount, 20m));
        await Insert("order", "admin", (Note, "沒有金額"));
        var r = await Run(Def(measures: [M("n", "countMain")], filters: [F(MainF(Amount), op, value)]));
        r.Total!["n"].Should().Be(expected);
    }

    public static TheoryData<string, object?, int> TextOps => new()
    {
        { "eq", "Alpha", 1 }, { "ne", "Alpha", 2 }, { "in", new[] { "Alpha", "Beta" }, 2 }, { "notIn", new[] { "Alpha" }, 2 },
        { "contains", "lph", 1 }, { "contains", "LPH", 1 }, { "startsWith", "Be", 1 },
    };

    [Theory, MemberData(nameof(TextOps))]
    public async Task Run_TextOperators_FollowListFilterSemantics(string op, object? value, int expected)
    {
        await Arrange();
        await Insert("order", "admin", (Note, "Alpha"));
        await Insert("order", "admin", (Note, "Beta"));
        await Insert("order", "admin", (Amount, 1m));
        var r = await Run(Def(measures: [M("n", "countMain")], filters: [F(MainF(Note), op, value)]));
        r.Total!["n"].Should().Be(expected);
    }

    [Fact]
    public async Task Run_MultiEnumFilter_InAndNotIn()
    {
        await Arrange();
        await Insert("order", "admin", (Tags, new[] { TagA, TagB }));
        await Insert("order", "admin", (Tags, new[] { TagB }));
        (await Run(Def(measures: [M("n", "countMain")], filters: [F(MainF(Tags), "in", new[] { TagA })]))).Total!["n"].Should().Be(1m);
        (await Run(Def(measures: [M("n", "countMain")], filters: [F(MainF(Tags), "notIn", new[] { TagA })]))).Total!["n"].Should().Be(1m);
    }

    [Fact]
    public async Task Run_OperatorNoLongerFitsField_FieldMissing()
    {
        await Arrange();
        await Insert("order", "admin", (Amount, 1m));
        var r = await Run(Def(measures: [M("n", "countMain")], filters: [F(MainF(Amount), "contains", "1")]));
        r.State.Should().Be("fieldMissing");
        r.Message.Should().Be("欄位型別已變更：訂單金額");
    }

    public static TheoryData<string, object?> BadShapes => new()
    {
        { "eq", "abc" }, { "eq", null }, { "gt", new[] { 1, 2 } }, { "between", 5 }, { "between", new[] { 1 } }, { "between", new[] { "a", "b" } },
    };

    [Theory, MemberData(nameof(BadShapes))]
    public async Task Run_ConditionValueShapeNoLongerFits_FieldMissing_NotFalseEmpty(string op, object? value)
    {
        await Arrange();
        await Insert("order", "admin", (Amount, 1m));
        var filter = await Run(Def(measures: [M("n", "countMain")], filters: [F(MainF(Amount), op, value)]));
        var ratio = await Run(Def(measures: [M("r", "ratio", null, F(MainF(Amount), op, value))]));
        foreach (var r in new[] { filter, ratio })
        {
            r.State.Should().Be("fieldMissing");
            r.Message.Should().Be("欄位型別已變更：訂單金額");
        }
    }

    [Fact]
    public async Task Run_DateConditionValueNotADate_FieldMissing()
    {
        await Arrange();
        await Insert("order", "admin", (Due, "2026-11-01"));
        (await Run(Def(measures: [M("n", "countMain")], filters: [F(MainF(Due), "eq", 20261101)]))).State.Should().Be("fieldMissing");
        (await Run(Def(measures: [M("n", "countMain")], filters: [F(MainF(Due), "eq", "不是日期")]))).State.Should().Be("fieldMissing");
        (await Run(Def(measures: [M("n", "countMain")], filters: [F(MainF(Due), "withinLastDays", "七")]))).State.Should().Be("fieldMissing");
        (await Run(Def(measures: [M("n", "countMain")], filters: [F(MainF(Due), "eq", "2026-11-01")]))).Total!["n"].Should().Be(1m);
    }

    [Fact]
    public async Task Run_SumOnFieldThatCannotSum_FieldMissing_MinMaxStillChecked()
    {
        await Arrange();
        await Insert("order", "admin", (Due, "2026-11-01"), (Note, "x"));
        (await Run(Def(measures: [M("s", "sum", MainF(Due))]))).State.Should().Be("fieldMissing");
        (await Run(Def(measures: [M("s", "avg", MainF(Due))]))).State.Should().Be("fieldMissing");
        (await Run(Def(measures: [M("s", "min", MainF(Note))]))).State.Should().Be("fieldMissing");
        (await Run(Def(measures: [M("s", "min", MainF(Due))]))).State.Should().Be("ok");
    }

    [Fact]
    public async Task Run_BucketOnFieldNoLongerDate_FieldMissing()
    {
        await Arrange();
        await Insert("order", "admin", (Due, "2026-11-01"), (Note, "x"));
        (await Run(Def(measures: [M("n", "countMain")], groups: [G(MainF(Note), "month")]))).State.Should().Be("fieldMissing");
        (await Run(Def(measures: [M("n", "countMain")], groups: [G(MainF(Due), "month")]))).State.Should().Be("ok");
    }

    // ---- 狀態 ----

    [Fact]
    public virtual async Task Run_TooManyRows_ReturnsTooMany_NoPartialResult()
    {
        await Arrange();
        Svc<ReportOptions>().MaxRows = 2;
        await Insert("order", "admin", (Amount, 1m));
        await Insert("order", "admin", (Amount, 2m));
        (await Run(Def(measures: [M("sum", "sum", MainF(Amount))]))).State.Should().Be("ok");

        await Insert("order", "admin", (Amount, 3m));
        var r = await Run(Def(measures: [M("sum", "sum", MainF(Amount))]));
        r.State.Should().Be("tooMany");
        r.Total.Should().BeNull();
        r.Groups.Should().BeNull();
        r.DetailRows.Should().BeNull();
    }

    [Fact]
    public async Task Run_SourceOverMaxRows_ConditionNarrowsIt_RunsNormally()
    {
        await Arrange();
        Svc<ReportOptions>().MaxRows = 2;
        foreach (var a in new[] { 1m, 2m, 3m }) await Insert("order", "admin", (Amount, a));

        var r = await Run(Def(measures: [M("sum", "sum", MainF(Amount))], filters: [F(MainF(Amount), "gte", 2)]));

        r.State.Should().Be("ok");
        r.Total!["sum"].Should().Be(5m);
    }

    [Fact]
    public async Task Run_ChildSourceOverMaxRows_ConditionNarrowsIt_RunsNormally()
    {
        await Arrange();
        Svc<ReportOptions>().MaxRows = 2;
        var a = await Insert("order", "admin", (Amount, 1m));
        foreach (var q in new[] { 1, 2, 3 }) await Insert("shipment", "admin", (LinkField, a), (Qty, q));

        var r = await Run(Def(Shipments, measures: [M("qty", "sum", ChildF(Qty))], filters: [F(ChildF(Qty), "gte", 2)]));

        r.State.Should().Be("ok");
        r.Total!["qty"].Should().Be(5m);
    }

    [Fact]
    public virtual async Task Run_OverMaxRowsAfterConditions_MessageSaysNarrowConditions()
    {
        await Arrange();
        Svc<ReportOptions>().MaxRows = 1;
        foreach (var a in new[] { 1m, 2m }) await Insert("order", "admin", (Amount, a));

        var r = await Run(Def(measures: [M("sum", "sum", MainF(Amount))]));

        r.State.Should().Be("tooMany");
        r.Message.Should().Be("資料太多，請縮小條件");
    }

    [Fact]
    public virtual async Task Run_OverMaxLoadRows_TooManyEvenWithNarrowCondition_MessageDoesNotSayNarrow()
    {
        await Arrange();
        Svc<ReportOptions>().MaxLoadRows = 2;
        foreach (var a in new[] { 1m, 2m, 3m }) await Insert("order", "admin", (Amount, a));

        var main = await Run(Def(measures: [M("sum", "sum", MainF(Amount))], filters: [F(MainF(Amount), "gte", 3)]));

        main.State.Should().Be("tooMany");
        main.Total.Should().BeNull();
        main.Message.Should().Contain("載入上限").And.NotContain("請縮小條件");

        Svc<ReportOptions>().MaxLoadRows = 3;
        foreach (var q in new[] { 1, 2, 3, 4 }) await Insert("shipment", "admin", (Qty, q));
        var child = await Run(Def(Shipments, measures: [M("n", "countChild")]));
        child.State.Should().Be("tooMany");
        child.Message.Should().Contain("載入上限");
    }

    [Fact]
    public async Task Run_MergedRowsOverLimit_ReturnsTooMany()
    {
        await Arrange();
        Svc<ReportOptions>().MaxRows = 3;
        var a = await Insert("order", "admin", (Amount, 1m));
        await Insert("order", "admin", (Amount, 2m));
        foreach (var q in new[] { 1, 2, 3 }) await Insert("shipment", "admin", (LinkField, a), (Qty, q));
        var r = await Run(Def(Shipments, measures: [M("n", "countChild")]));
        r.State.Should().Be("tooMany"); // 3 列子記錄＋沒有子記錄的主記錄 1 列 = 4 列
        r.Total.Should().BeNull();
    }

    [Fact]
    public async Task Run_FieldDeleted_ReturnsFieldMissing()
    {
        await Arrange();
        (await Run(Def(columns: [new ReportFieldRef(Main, 999_999, null)]))).State.Should().Be("fieldMissing");

        (await Deactivate(Note.Id)).IsSuccess.Should().BeTrue();
        var r = await Run(Def(columns: [MainF(Note)]));
        r.State.Should().Be("fieldMissing");
        r.Message.Should().Contain("備註");
    }

    [Fact]
    public async Task Run_HiddenAndDeactivatedField_DoesNotLeakItsName()
    {
        await Arrange();
        (await Deactivate(Secret.Id)).IsSuccess.Should().BeTrue();
        As("sales01");
        var r = await Run(Def(columns: [MainF(Secret)]));
        r.State.Should().Be("fieldMissing");
        r.Message.Should().NotContain("機密備註");
    }

    [Fact]
    public async Task Run_ModuleDisabled_ReturnsModuleDisabled()
    {
        await Arrange();
        (await Send(new DeactivateModuleRequest("order"))).IsSuccess.Should().BeTrue();
        (await Run(Def(columns: [MainF(Note)]))).State.Should().Be("moduleDisabled");
        (await Run(Def(columns: [MainF(Note)], main: "no-such-module"))).State.Should().Be("moduleDisabled");
    }

    [Fact]
    public async Task Run_ChildModuleDisabled_ReturnsModuleDisabled()
    {
        await Arrange();
        (await Send(new DeactivateModuleRequest("shipment"))).IsSuccess.Should().BeTrue();
        (await Run(Def(Shipments, measures: [M("n", "countMain")]))).State.Should().Be("moduleDisabled");
    }

    [Fact]
    public async Task Run_DetailModuleAsMain_ReturnsModuleDisabled()
    {
        await Arrange();
        var r = await Run(Def(columns: [MainF(Qty)], main: "shipment"));
        r.State.Should().Be("moduleDisabled");
        r.Message.Should().Be("明細模組不能當主模組");
    }

    [Fact]
    public async Task Run_NoData_ReturnsEmpty()
    {
        await Arrange();
        var r = await Run(Def(measures: [M("n", "countMain")]));
        r.State.Should().Be("empty");
        r.BasisDate.Should().Be(new DateOnly(2026, 11, 4));
    }

    [Fact]
    public async Task BasisDate_IsBusinessToday()
    {
        await Arrange();
        (await Svc<ReportRunner>().BasisDateAsync(CancellationToken.None)).Should().Be(new DateOnly(2026, 11, 4));
    }

    // ---- 範圍 ----

    [Fact]
    public async Task Run_DetailMerge_InheritsHeaderScope()
    {
        await Arrange();
        var mine = await Insert("order", "sales01", (Amount, 100m));
        var theirs = await Insert("order", "sales02", (Amount, 900m));
        await Insert("shipment", "sales01", (LinkField, mine), (Qty, 1));
        await Insert("shipment", "sales02", (LinkField, theirs), (Qty, 9));
        // 明細自己的程式沒授權給 sales，靠表頭的範圍 Own
        As("sales01");
        var r = await Run(Def(Shipments, measures: [M("sum", "sum", MainF(Amount)), M("qty", "sum", ChildF(Qty)), M("kids", "countChild")]));
        r.State.Should().Be("ok");
        r.Total!["sum"].Should().Be(100m);
        r.Total["qty"].Should().Be(1m);
        r.Total["kids"].Should().Be(1m);
    }

    [Fact]
    public async Task Run_ChildOutOfScope_NotCounted_MainStillCounted()
    {
        await Arrange();
        var mine = await Insert("order", "sales01", (Amount, 100m));
        await Insert("shipment", "sales02", (LinkField, mine), (Qty, 9));
        As("sales01");
        var r = await Run(Def(Shipments, measures: [M("sum", "sum", MainF(Amount)), M("kids", "countChild")]));
        r.Total!["sum"].Should().Be(100m);
        r.Total["kids"].Should().Be(0m);
    }

    // ---- 分組標籤 ----

    [Fact]
    public async Task Labels_OptionAndUserAndReference_UseDisplayNames_ColumnsUseFieldNames()
    {
        await Arrange();
        var c = await Insert("customer", "admin", (Region, "北區"));
        await Insert("order", "admin", (Status, OptDone), (Sales, "sales01"), (CustomerRef, c), (Amount, 1m));

        var byStatus = await Run(Def(groups: [G(MainF(Status))], measures: [M("n", "countMain")]), locale: "en-US");
        byStatus.Groups![0].GroupKeys[0].Should().Be(OptDone);
        byStatus.Groups![0].GroupLabels[0].Should().Be("Done");
        byStatus.Columns.Select(x => x.Label).Should().Contain("狀態");
        byStatus.Columns.Should().Contain(x => x.Role == "measure" && x.Key == "n");

        var zh = await Run(Def(groups: [G(MainF(Status))], measures: [M("n", "countMain")]));
        zh.Groups![0].GroupLabels[0].Should().Be("已完成");

        var byUser = await Run(Def(groups: [G(MainF(Sales))], measures: [M("n", "countMain")]));
        byUser.Groups![0].GroupKeys[0].Should().Be("sales01");
        byUser.Groups![0].GroupLabels[0].Should().Be("業務一");

        var byRef = await Run(Def(groups: [G(MainF(CustomerRef))], measures: [M("n", "countMain")]));
        byRef.Groups![0].GroupKeys[0].Should().Be(c.ToString());
        byRef.Groups![0].GroupLabels[0].Should().Be("row");
    }

    [Fact]
    public async Task Labels_Boolean_ShowsYesNo_AndFilters()
    {
        await Arrange();
        var urgent = await MustAdd("order", FieldType.Boolean, label: "急件");
        await Insert("order", "admin", (urgent, true));
        await Insert("order", "admin", (urgent, false));
        var r = await Run(Def(groups: [G(MainF(urgent))], measures: [M("n", "countMain")], filters: [F(MainF(urgent), "eq", true)]));
        r.Groups!.Single().GroupLabels[0].Should().Be("是");
    }

    [Fact]
    public async Task Labels_DirectReference_DeletedAndRestricted_FormOwnGroups()
    {
        await Arrange();
        var mine = await Insert("customer", "sales01", (Region, "北"));
        var gone = await Insert("customer", "sales01", (Region, "西"));
        var other = await Insert("customer", "sales02", (Region, "南"));
        await SoftDelete(gone);
        foreach (var c in new[] { mine, gone, other }) await Insert("order", "sales01", (CustomerRef, c));
        As("sales01");
        var r = await Run(Def(groups: [G(MainF(CustomerRef))], measures: [M("n", "countMain")]));
        r.Groups!.Select(g => g.GroupLabels[0]).Should().BeEquivalentTo("row", "（已刪除）", "（無權限）");
    }

    [Fact]
    public async Task Labels_TextEqualToReservedKey_IsNotLabeledAsDeleted()
    {
        await Arrange();
        await Insert("order", "admin", (Note, ReportCalculator.MissingKey));
        var r = await Run(Def(groups: [G(MainF(Note))], measures: [M("n", "countMain")]));
        r.Groups![0].GroupLabels[0].Should().Be(ReportCalculator.MissingKey);
    }

    [Fact]
    public async Task Groups_OptionsSortedBySequence_MultiSelectEachOption()
    {
        await Arrange();
        await Insert("order", "admin", (Status, OptDone), (Tags, new[] { TagB, TagA }), (Amount, 10m));
        await Insert("order", "admin", (Status, OptOpen), (Tags, new[] { TagB }), (Amount, 5m));
        await Insert("order", "admin", (Amount, 1m));

        var byStatus = await Run(Def(groups: [G(MainF(Status))], measures: [M("sum", "sum", MainF(Amount))]));
        byStatus.Groups!.Select(g => g.GroupLabels[0]).Should().Equal("進行中", "已完成", "（空白）");

        var byTag = await Run(Def(groups: [G(MainF(Tags))], measures: [M("sum", "sum", MainF(Amount))]));
        byTag.Groups!.Select(g => g.GroupLabels[0]).Should().Equal("甲", "乙", "（空白）");
        GroupOf(byTag, "乙").Values["sum"].Should().Be(15m);
        byTag.Total!["sum"].Should().Be(16m);
    }

    [Fact]
    public async Task Groups_SortByMeasure_NullsLast()
    {
        await Arrange();
        await Insert("order", "admin", (Note, "甲"), (Amount, 5m));
        await Insert("order", "admin", (Note, "乙"), (Amount, 50m));
        await Insert("order", "admin", (Note, "丙"));
        var r = await Run(Def(groups: [G(MainF(Note))], measures: [M("sum", "sum", MainF(Amount))], sort: [new ReportSort("sum", true)]));
        r.Groups!.Select(g => g.GroupLabels[0]).Should().Equal("乙", "甲", "丙");
    }

    // ---- 日期 ----

    [Fact]
    public async Task Bucket_KeyGoesToText_DateStaysForRangeAndMinMax()
    {
        await Arrange();
        await Insert("order", "admin", (Due, "2026-10-31"), (Amount, 1m));
        await Insert("order", "admin", (Due, "2026-11-01"), (Amount, 2m));
        await Insert("order", "admin", (Due, "2026-11-03"), (Amount, 4m));
        var r = await Run(Def(groups: [G(MainF(Due), "month")], measures: [M("sum", "sum", MainF(Amount)), M("max", "max", MainF(Due))],
            primary: MainF(Due), range: new ReportDateRange("thisMonth", null)));
        var g = r.Groups!.Should().ContainSingle().Subject;
        g.GroupKeys[0].Should().Be("2026-11");
        g.GroupLabels[0].Should().Be("2026-11");
        g.Values["sum"].Should().Be(6m);
        g.Values["max"].Should().Be(new DateOnly(2026, 11, 3).DayNumber);
        r.Columns.Single(c => c.Key == "max").ValueType.Should().Be("dayNumber");
        r.AppliedRange.Should().Be(new ReportAppliedRange(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), "thisMonth", null));
    }

    [Fact]
    public async Task DateTime_UtcValue_ConvertsToTaipeiBeforeRange()
    {
        await Arrange();
        // 台北 11/5 00:00（UTC 11/4 16:00）不屬於 11/4；台北 11/4 23:59:59（UTC 11/4 15:59:59）屬於
        await Insert("order", "admin", (At, "2026-11-04T16:00:00Z"), (Amount, 1m));
        await Insert("order", "admin", (At, "2026-11-04T15:59:59Z"), (Amount, 2m));
        var r = await Run(Def(measures: [M("sum", "sum", MainF(Amount))], primary: MainF(At), range: new ReportDateRange("today", null)));
        r.Total!["sum"].Should().Be(2m);
        r.AppliedRange!.From.Should().Be(new DateOnly(2026, 11, 4));
    }

    [Fact]
    public async Task DateTime_UtcValue_BucketsByTaipeiDay()
    {
        await Arrange();
        await Insert("order", "admin", (At, "2026-11-04T16:00:00Z"));
        var r = await Run(Def(groups: [G(MainF(At), "day")], measures: [M("n", "countMain")]));
        r.Groups![0].GroupKeys[0].Should().Be("2026-11-05");
    }

    [Fact]
    public async Task DateTime_BetweenWithDates_UpperBoundIsBeforeNextDayMidnight()
    {
        await Arrange();
        await Insert("order", "admin", (At, "2026-11-04T23:59:59+08:00"));
        await Insert("order", "admin", (At, "2026-11-05T00:00:00+08:00"));
        var r = await Run(Def(measures: [M("n", "countMain")], filters: [F(MainF(At), "between", new[] { "2026-11-01", "2026-11-04" })]));
        r.Total!["n"].Should().Be(1m);
    }

    [Fact]
    public async Task DateTime_ComparesTimeOfDay_WhenValueHasTime()
    {
        await Arrange();
        await Insert("order", "admin", (At, "2026-11-04T09:00:00+08:00"));
        await Insert("order", "admin", (At, "2026-11-04T15:00:00+08:00"));
        var r = await Run(Def(measures: [M("n", "countMain")], filters: [F(MainF(At), "gte", "2026-11-04T12:00:00+08:00")]));
        r.Total!["n"].Should().Be(1m);
    }

    [Fact]
    public async Task DateTime_FilterValueWithoutOffset_IsTaipeiWallClock()
    {
        await Arrange();
        await Insert("order", "admin", (At, "2026-11-04T15:00:00+08:00"), (Amount, 1m));
        await Insert("order", "admin", (At, "2026-11-04T09:00:00+08:00"), (Amount, 2m));
        var r = await Run(Def(measures: [M("sum", "sum", MainF(Amount))], filters: [F(MainF(At), "gte", "2026-11-04T12:00:00")]));
        r.Total!["sum"].Should().Be(1m);
    }

    [Fact]
    public async Task DateTime_StoredWithoutOffset_IsTaipeiWallClock()
    {
        await Arrange();
        await Insert("order", "admin", (At, "2026-11-04T23:00:00"));
        var r = await Run(Def(measures: [M("n", "countMain")], primary: MainF(At), range: new ReportDateRange("today", null)));
        r.Total!["n"].Should().Be(1m);
    }

    public static TheoryData<string, object, int> TimeOps => new()
    {
        { "gt", "09:00", 2 }, { "gte", "09:00", 3 }, { "lt", "17:30", 2 }, { "lte", "17:30", 3 },
        { "between", new[] { "09:30", "17:30" }, 2 }, { "eq", "09:00", 1 }, { "ne", "09:00", 2 },
    };

    [Theory, MemberData(nameof(TimeOps))]
    public async Task Run_TimeOperators_CompareAsTimeOfDay(string op, object value, int expected)
    {
        await Arrange();
        var time = await MustAdd("order", FieldType.Time, label: "到貨時間");
        foreach (var t in new[] { "09:00", "12:00", "17:30" }) await Insert("order", "admin", (time, t));
        var r = await Run(Def(measures: [M("n", "countMain")], filters: [F(MainF(time), op, value)]));
        r.Total!["n"].Should().Be(expected);
    }

    [Fact]
    public async Task WithinLastDays_UsesBasisDate()
    {
        await Arrange();
        await Insert("order", "admin", (Due, "2026-11-04"));
        await Insert("order", "admin", (Due, "2026-11-01"));
        await Insert("order", "admin", (Due, "2026-10-31"));
        var r = await Run(Def(measures: [M("n", "countMain")], filters: [F(MainF(Due), "withinLastDays", 3)]));
        r.Total!["n"].Should().Be(2m);
    }

    [Fact]
    public async Task BeforeToday_IsStrictlyBeforeBasisDate_EmptyExcluded()
    {
        await Arrange();
        await Insert("order", "admin", (Due, "2026-11-03"), (At, "2026-11-03T23:59:00+08:00"), (Amount, 1m));
        await Insert("order", "admin", (Due, "2026-11-04"), (At, "2026-11-04T00:00:00+08:00"), (Amount, 10m));
        await Insert("order", "admin", (Due, "2026-11-05"), (At, "2026-11-05T09:00:00+08:00"), (Amount, 100m));
        await Insert("order", "admin", (Amount, 1000m));

        (await Run(Def(measures: [M("sum", "sum", MainF(Amount))], filters: [F(MainF(Due), "beforeToday")]))).Total!["sum"].Should().Be(1m);
        (await Run(Def(measures: [M("sum", "sum", MainF(Amount))], filters: [F(MainF(At), "beforeToday")]))).Total!["sum"].Should().Be(1m);
    }

    [Fact]
    public async Task BeforeToday_AsRatioCondition_CountsOnlyRowsBeforeBasisDate()
    {
        await Arrange();
        await Insert("order", "admin", (Due, "2026-11-01"));
        await Insert("order", "admin", (Due, "2026-11-03"));
        await Insert("order", "admin", (Due, "2026-11-04"));
        await Insert("order", "admin", (Amount, 1m));
        var r = await Run(Def(measures: [M("late", "ratio", cond: F(MainF(Due), "beforeToday"))]));
        r.Total!["late"].Should().Be(0.5m);
    }

    [Fact]
    public async Task RangeOverride_WinsOverReportRange()
    {
        await Arrange();
        await Insert("order", "admin", (Due, "2026-11-04"));
        await Insert("order", "admin", (Due, "2026-10-04"));
        var r = await Run(Def(measures: [M("n", "countMain")], primary: MainF(Due), range: new ReportDateRange("thisMonth", null)),
            new ReportDateRange("lastMonth", null));
        r.Total!["n"].Should().Be(1m);
        r.AppliedRange!.Kind.Should().Be("lastMonth");
    }

    [Fact]
    public async Task NoPrimaryDate_RangeNotApplied()
    {
        await Arrange();
        await Insert("order", "admin", (Due, "2020-01-01"));
        var r = await Run(Def(measures: [M("n", "countMain")]), new ReportDateRange("today", null));
        r.Total!["n"].Should().Be(1m);
        r.AppliedRange.Should().BeNull();
    }

    [Fact]
    public async Task ChildPrimaryDate_FiltersChildRecords()
    {
        await Arrange();
        var dueOnShipment = await MustAdd("shipment", FieldType.Date, label: "出貨日");
        var a = await Insert("order", "admin", (Amount, 100m));
        await Insert("shipment", "admin", (LinkField, a), (dueOnShipment, "2026-11-04"));
        await Insert("shipment", "admin", (LinkField, a), (dueOnShipment, "2026-01-01"));
        await Insert("order", "admin", (Amount, 50m));
        var r = await Run(Def(Shipments, measures: [M("sum", "sum", MainF(Amount)), M("kids", "countChild")],
            primary: ChildF(dueOnShipment), range: new ReportDateRange("today", null)));
        r.Total!["sum"].Should().Be(100m);
        r.Total["kids"].Should().Be(1m);
    }

    // ---- 比率：缺值也要算在分母 ----

    [Theory]
    [InlineData("eq", 1)]
    [InlineData("ne", 2)]
    [InlineData("isEmpty", 1)]
    public async Task Ratio_RecordWithoutValue_CountsInDenominator(string op, int hits)
    {
        await Arrange();
        await Insert("order", "admin", (Note, "甲"));
        await Insert("order", "admin", (Note, "乙"));
        await Insert("order", "admin", (Amount, 1m));
        var r = await Run(Def(measures: [M("r", "ratio", cond: F(MainF(Note), op, op == "isEmpty" ? null : "甲"))]));
        r.Total!["r"].Should().Be((decimal)hits / 3);
        r.Columns.Single(c => c.Key == "r").ValueType.Should().Be("ratio");
    }

    [Fact]
    public async Task Ratio_ChildCondition_LeftJoinRowNotInDenominator()
    {
        await Arrange();
        await TwoOrders();
        var r = await Run(Def(Shipments, measures: [M("r", "ratio", cond: F(ChildF(Warehouse), "eq", "Y"))]));
        r.Total!["r"].Should().Be(2m / 3);
    }

    [Fact]
    public async Task Ratio_Merged_DedupesByMain_InTotalAndInGroup()
    {
        await Arrange();
        await TwoOrders();
        var r = await Run(Def(Shipments, groups: [G(ChildF(Warehouse))],
            measures: [M("r", "ratio", cond: F(MainF(Amount), "gte", 100))]));
        r.Total!["r"].Should().Be(0.5m);
        GroupOf(r, "X").Values["r"].Should().Be(1m);
        GroupOf(r, "Y").Values["r"].Should().Be(1m);
        GroupOf(r, "（空白）").Values["r"].Should().Be(0m);
    }

    [Fact]
    public async Task Group_WithChildFilter_OnlySurvivingChildrenFormGroups_MainStillDeduped()
    {
        await Arrange();
        await TwoOrders();
        var r = await Run(Def(Shipments, groups: [G(ChildF(Warehouse))],
            measures: [M("sum", "sum", MainF(Amount)), M("kids", "countChild"), M("r", "ratio", cond: F(MainF(Amount), "gte", 100))],
            filters: [F(ChildF(Qty), "gte", 2)]));
        var g = r.Groups!.Should().ContainSingle().Subject; // X 的數量 1 被篩掉；B 沒有子記錄也被子條件排除
        g.GroupLabels[0].Should().Be("Y");
        g.Values["sum"].Should().Be(100m);
        g.Values["kids"].Should().Be(2m);
        g.Values["r"].Should().Be(1m);
        r.Total!["sum"].Should().Be(100m);
    }

    // ---- 明細與下鑽 ----

    [Fact]
    public async Task DetailReport_RowsAlignWithFieldColumns_AndSort()
    {
        await Arrange();
        var c = await Insert("customer", "admin", (Region, "北區"));
        await Insert("order", "admin", (Amount, 10m), (Status, OptDone), (Sales, "sales01"), (CustomerRef, c), (Due, "2026-11-01"),
            (At, "2026-11-04T16:30:00Z"), (Tags, new[] { TagB, TagA }));
        await Insert("order", "admin", (Amount, 20m));
        var r = await Run(Def(columns: [MainF(Amount), MainF(Status), MainF(Sales), RegionVia, MainF(Due), MainF(At), MainF(Tags)],
            sort: [new ReportSort(ReportCalculator.KeyOf(MainF(Amount)), true)]));
        r.State.Should().Be("ok");
        r.Columns.Should().OnlyContain(x => x.Role == "field");
        r.Columns[3].Label.Should().Be("客戶．地區");
        r.DetailRows![0].Should().Equal(20m, null, null, null, null, null, null);
        r.DetailRows![1].Should().Equal(10m, "已完成", "業務一", "北區", "2026-11-01", "2026-11-05 00:30", "甲; 乙");
    }

    [Fact]
    public async Task Drill_ReturnsOnlyRowsOfGroup_SameScope()
    {
        await Arrange();
        await Insert("order", "sales01", (Note, "甲"), (Amount, 1m));
        await Insert("order", "sales01", (Note, "乙"), (Amount, 2m));
        await Insert("order", "sales02", (Note, "甲"), (Amount, 4m));
        As("sales01");
        var d = Def(columns: [MainF(Amount)], groups: [G(MainF(Note))], measures: [M("sum", "sum", MainF(Amount))]);
        var r = await Run(d, drill: ["甲"]);
        r.State.Should().Be("ok");
        r.DetailRows.Should().HaveCount(1);
        r.DetailRows![0].Should().Equal(1m);
        r.Total!["sum"].Should().Be(1m);
        r.Groups.Should().BeNull();
    }

    [Fact]
    public async Task Drill_WithoutColumns_ShowsGroupAndMeasureFields()
    {
        await Arrange();
        await Insert("order", "admin", (Status, OptOpen), (Amount, 1m));
        var r = await Run(Def(groups: [G(MainF(Status))], measures: [M("sum", "sum", MainF(Amount))]), drill: [OptOpen]);
        r.Columns.Select(c => c.Label).Should().Equal("狀態", "訂單金額");
        r.DetailRows![0].Should().Equal("進行中", 1m);
    }

    [Fact]
    public async Task Drill_KeyCountMismatch_ReturnsEmpty()
    {
        await Arrange();
        await Insert("order", "admin", (Note, "甲"));
        var r = await Run(Def(groups: [G(MainF(Note))], measures: [M("n", "countMain")]), drill: ["甲", "多的"]);
        r.State.Should().Be("empty");
    }

    [Fact]
    public async Task MeasuresWithoutGroups_NoDetailRows()
    {
        await Arrange();
        await Insert("order", "admin", (Amount, 1m));
        var r = await Run(Def(measures: [M("n", "countMain")]));
        r.Groups.Should().BeEmpty();
        r.DetailRows.Should().BeNull();
    }
}

/// <summary>
/// 報表資料庫端聚合設計 §7 第 1 項：同一組斷言改走 SQL 聚合路徑再跑一次。欄位一律勾可篩選、索引隨記錄建立；
/// 能走 SQL 的形狀用 SqlOnly 跑，不能走的（合併子模組、帶出、明細、下鑽等）改用 Auto 並斷言走了記憶體路徑（見 <see cref="ReportTestBase.Run"/>）。
/// </summary>
public class ReportRunnerSqlTests : ReportRunnerTests
{
    protected override bool FieldsFilterable => true;

    // ---- 記憶體路徑的上限：SQL 聚合刻意不受 MaxRows、MaxLoadRows 限制（設計 §10 第 1 項），分組數另以 MaxRows 為上限 ----

    [Fact]
    public override async Task Run_TooManyRows_ReturnsTooMany_NoPartialResult()
    {
        await Arrange();
        Svc<ReportOptions>().MaxRows = 2;
        foreach (var a in new[] { 1m, 2m, 3m }) await Insert("order", "admin", (Amount, a));
        var r = await Run(Def(measures: [M("sum", "sum", MainF(Amount))]));
        r.Engine.Should().Be("sql");
        r.State.Should().Be("ok", "SQL 聚合不受來源筆數限制");
        r.Total!["sum"].Should().Be(6m);

        var grouped = await Run(Def(groups: [G(MainF(Amount))], measures: [M("sum", "sum", MainF(Amount))]));
        grouped.State.Should().Be("tooMany", "三個分組超過上限 2");
        grouped.Total.Should().BeNull();
        grouped.Groups.Should().BeNull();
        grouped.DetailRows.Should().BeNull();
    }

    [Fact]
    public override async Task Run_OverMaxRowsAfterConditions_MessageSaysNarrowConditions()
    {
        await Arrange();
        Svc<ReportOptions>().MaxRows = 1;
        foreach (var a in new[] { 1m, 2m }) await Insert("order", "admin", (Amount, a));
        (await Run(Def(measures: [M("sum", "sum", MainF(Amount))]))).State.Should().Be("ok");

        var r = await Run(Def(groups: [G(MainF(Amount))], measures: [M("sum", "sum", MainF(Amount))]));
        r.State.Should().Be("tooMany");
        r.Message.Should().Be("資料太多，請縮小條件");
    }

    [Fact]
    public override async Task Run_OverMaxLoadRows_TooManyEvenWithNarrowCondition_MessageDoesNotSayNarrow()
    {
        await Arrange();
        Svc<ReportOptions>().MaxLoadRows = 2;
        foreach (var a in new[] { 1m, 2m, 3m }) await Insert("order", "admin", (Amount, a));
        var main = await Run(Def(measures: [M("sum", "sum", MainF(Amount))], filters: [F(MainF(Amount), "gte", 3)]));
        main.Engine.Should().Be("sql");
        main.State.Should().Be("ok", "SQL 聚合不載入記錄");
        main.Total!["sum"].Should().Be(3m);

        // 合併子模組仍走記憶體路徑；沒有主模組條件可縮小，照舊被載入上限擋下
        Svc<ReportOptions>().MaxLoadRows = 3;
        foreach (var q in new[] { 1, 2, 3, 4 }) await Insert("shipment", "admin", (Qty, q));
        var child = await Run(Def(Shipments, measures: [M("n", "countChild")]));
        child.State.Should().Be("tooMany");
        child.Message.Should().Contain("載入上限");
    }
}
