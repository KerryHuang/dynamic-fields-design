using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.ReportService;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Domain.Repositories.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>
/// 設計 §7：SQL 聚合與記憶體路徑的結果必須相同。同一份資料用兩種引擎各跑一次，逐項比對（數字用 decimal 數值相等，不比文字）。
/// 記錄直接寫進資料表，所以這裡自己補建索引；欄位全部勾成可篩選，SQL 路徑才有索引可用。
/// </summary>
public class ReportSqlEquivalenceTests : ReportTestBase
{
    private FieldResponse Done = null!;

    private async Task Prepare()
    {
        await Arrange();
        Done = await MustAdd("order", FieldType.Boolean, label: "已結案");
        foreach (var module in new[] { "customer", "order" })
            foreach (var f in (await Detail(module)).Blocks.SelectMany(b => b.Fields))
                (await Update(f, f.Config with { IsFilterable = true })).IsSuccess.Should().BeTrue();
        await Svc<ISystemSettingRepository>().SetAsync(SystemSetting.IndexBackfillVersion, RecordIndexer.BackfillVersion, CancellationToken.None);
        await Db.Context.SaveChangesAsync();
    }

    private async Task<long> Seed(string module, string owner, params (FieldResponse Field, object? Value)[] values)
    {
        var id = await Insert(module, owner, values);
        var m = (await Svc<IModuleRepository>().GetByKeyAsync(module, true, CancellationToken.None))!;
        var record = await Db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().SingleAsync(r => r.Id == id);
        var rows = RecordIndexer.Build(m, RecordValueProcessor.Parse(record.ValuesJson), Svc<FieldTypeRegistry>());
        foreach (var row in rows) row.RecordId = id;
        Db.Context.AddRange(rows);
        await Db.Context.SaveChangesAsync();
        return id;
    }

    /// <summary>約 40 筆，涵蓋空值、多選、兩個擁有者、軟刪除，以及每種欄位型別的邊界值。</summary>
    private async Task SeedOrders()
    {
        var rng = new Random(20261108);
        var days = new[] { "2026-10-30", "2026-11-01", "2026-11-02", "2026-11-04", "2026-11-09", "2026-11-30", "2026-12-31", "2027-01-01", "2026-07-15" };
        var amounts = new[] { 0m, 10m, 50m, 100m, 100.5m, 12.34m, -7.25m, 999999999999.99m, 0.01m };
        for (var i = 0; i < 40; i++)
        {
            var values = new List<(FieldResponse, object?)>();
            if (rng.Next(6) > 0) values.Add((Amount, amounts[rng.Next(amounts.Length)]));
            if (rng.Next(5) > 0) values.Add((Price, amounts[rng.Next(amounts.Length)]));
            if (rng.Next(5) > 0) values.Add((Status, rng.Next(2) == 0 ? OptOpen : OptDone));
            if (rng.Next(4) > 0) values.Add((Tags, rng.Next(3) switch { 0 => new[] { TagA }, 1 => new[] { TagB }, _ => new[] { TagA, TagB } }));
            if (rng.Next(4) > 0) values.Add((Sales, rng.Next(2) == 0 ? "sales01" : "sales02"));
            if (rng.Next(5) > 0) values.Add((Due, days[rng.Next(days.Length)]));
            if (rng.Next(4) > 0) values.Add((At, $"{days[rng.Next(days.Length)]}T{rng.Next(24):00}:{rng.Next(60):00}:00"));
            if (rng.Next(3) > 0) values.Add((Note, new[] { "甲", "乙", "甲乙", "a", "A", "" }[rng.Next(6)]));
            if (rng.Next(3) > 0) values.Add((Done, rng.Next(2) == 0));
            var id = await Seed("order", rng.Next(2) == 0 ? "sales01" : "sales02", values.ToArray());
            if (i % 11 == 10) await SoftDelete(id);
        }
    }

    private async Task<(ReportResult Memory, ReportResult Sql)> Both(ReportDefinition d, ReportDateRange? range = null, ReportEngineMode second = ReportEngineMode.SqlOnly)
    {
        var options = Svc<ReportOptions>();
        options.Engine = ReportEngineMode.MemoryOnly;
        var memory = await Run(d, range);
        options.Engine = second;
        var sql = await Run(d, range);
        return (memory, sql);
    }

    private static void ShouldMatch(ReportResult memory, ReportResult sql, string what, string expectedEngine = "sql")
    {
        memory.Engine.Should().Be("memory", what);
        sql.Engine.Should().Be(expectedEngine, what);
        sql.State.Should().Be(memory.State, what);
        sql.Message.Should().Be(memory.Message, what);
        sql.RowMeaning.Should().Be(memory.RowMeaning, what);
        sql.Columns.Should().BeEquivalentTo(memory.Columns, o => o.WithStrictOrdering(), what);
        sql.DedupedNote.Should().Be(memory.DedupedNote, what);
        sql.AppliedRange.Should().BeEquivalentTo(memory.AppliedRange, what);
        sql.Total.Should().BeEquivalentTo(memory.Total, what);
        (sql.Groups is null).Should().Be(memory.Groups is null, what);
        if (memory.Groups is null) return;
        sql.Groups!.Select(g => string.Join("\u0001", g.GroupKeys)).Should().Equal(memory.Groups.Select(g => string.Join("\u0001", g.GroupKeys)), what);
        sql.Groups!.Select(g => string.Join("\u0001", g.GroupLabels)).Should().Equal(memory.Groups.Select(g => string.Join("\u0001", g.GroupLabels)), what);
        for (var i = 0; i < memory.Groups.Count; i++)
            sql.Groups![i].Values.Should().BeEquivalentTo(memory.Groups[i].Values, what + $" [group {i}]");
    }

    private static readonly string[] AllKinds = ["sum", "avg", "min", "max"];

    private List<ReportMeasure> AmountMeasures() =>
        [M("n", "countMain"), .. AllKinds.Select(k => M(k, k, MainF(Amount))), M("p", "sum", MainF(Price))];

    [Fact]
    public async Task Groups_EveryFieldKind_MatchMemoryPath()
    {
        await Prepare();
        await SeedOrders();
        var cases = new Dictionary<string, ReportDefinition>
        {
            ["enum"] = Def(groups: [G(MainF(Status))], measures: AmountMeasures()),
            ["multi"] = Def(groups: [G(MainF(Tags))], measures: AmountMeasures()),
            ["user"] = Def(groups: [G(MainF(Sales))], measures: AmountMeasures()),
            ["number"] = Def(groups: [G(MainF(Amount))], measures: [M("n", "countMain")]),
            ["currency"] = Def(groups: [G(MainF(Price))], measures: [M("n", "countMain"), M("a", "sum", MainF(Amount))]),
            ["bool"] = Def(groups: [G(MainF(Done))], measures: AmountMeasures()),
            ["text"] = Def(groups: [G(MainF(Note))], measures: AmountMeasures()),
            ["date"] = Def(groups: [G(MainF(Due))], measures: AmountMeasures()),
            ["datetime"] = Def(groups: [G(MainF(At))], measures: [M("n", "countMain")]),
            ["two"] = Def(groups: [G(MainF(Status)), G(MainF(Tags))], measures: AmountMeasures()),
            ["three"] = Def(groups: [G(MainF(Status)), G(MainF(Sales)), G(MainF(Done))], measures: [M("n", "countMain")]),
            ["none"] = Def(measures: AmountMeasures()),
            ["dateMax"] = Def(measures: [M("lo", "min", MainF(Due)), M("hi", "max", MainF(Due))]),
            ["ratio"] = Def(groups: [G(MainF(Sales))], measures: [M("r", "ratio", cond: F(MainF(Status), "eq", OptDone)), M("n", "countMain")]),
        };
        foreach (var bucket in new[] { "day", "week", "month", "quarter", "year" })
        {
            cases[$"due-{bucket}"] = Def(groups: [G(MainF(Due), bucket)], measures: AmountMeasures());
            cases[$"at-{bucket}"] = Def(groups: [G(MainF(At), bucket)], measures: [M("n", "countMain")]);
        }
        foreach (var (name, def) in cases)
        {
            var (memory, sql) = await Both(def);
            ShouldMatch(memory, sql, name);
        }
    }

    [Fact]
    public async Task Filters_EveryOperator_MatchMemoryPath()
    {
        await Prepare();
        await SeedOrders();
        var filters = new List<ReportFilter>
        {
            F(MainF(Amount), "eq", 100), F(MainF(Amount), "ne", 100), F(MainF(Amount), "gt", 50), F(MainF(Amount), "gte", 50),
            F(MainF(Amount), "lt", 50), F(MainF(Amount), "lte", 50), F(MainF(Amount), "between", new[] { 10, 100 }),
            F(MainF(Amount), "in", new[] { 10, 50, 7 }), F(MainF(Amount), "notIn", new[] { 10, 50 }),
            F(MainF(Amount), "eq", 12.345), F(MainF(Amount), "ne", 12.345), F(MainF(Amount), "gt", 12.345), F(MainF(Amount), "lte", 12.345),
            F(MainF(Amount), "in", new[] { 12.345 }), F(MainF(Amount), "isEmpty"), F(MainF(Amount), "isNotEmpty"),
            F(MainF(Price), "gt", 0), F(MainF(Price), "between", new[] { -10, 10 }),
            F(MainF(Status), "eq", OptOpen), F(MainF(Status), "ne", OptOpen), F(MainF(Status), "in", new[] { OptOpen, OptDone }),
            F(MainF(Status), "notIn", new[] { OptOpen }), F(MainF(Status), "isEmpty"), F(MainF(Status), "isNotEmpty"),
            F(MainF(Status), "in", Array.Empty<string>()), F(MainF(Status), "notIn", Array.Empty<string>()),
            F(MainF(Tags), "in", new[] { TagA }), F(MainF(Tags), "notIn", new[] { TagA }), F(MainF(Tags), "eq", TagB),
            F(MainF(Tags), "ne", TagB), F(MainF(Tags), "isEmpty"), F(MainF(Tags), "isNotEmpty"),
            F(MainF(Sales), "eq", "sales01"), F(MainF(Sales), "ne", "sales01"), F(MainF(Sales), "in", new[] { "sales01", "sales09" }),
            F(MainF(Sales), "isEmpty"),
            F(MainF(Note), "eq", "甲"), F(MainF(Note), "ne", "甲"), F(MainF(Note), "in", new[] { "甲", "a" }), F(MainF(Note), "notIn", new[] { "甲", "a" }),
            F(MainF(Note), "isEmpty"), F(MainF(Note), "isNotEmpty"),
            F(MainF(Done), "eq", true), F(MainF(Done), "ne", true), F(MainF(Done), "eq", false), F(MainF(Done), "isEmpty"),
            F(MainF(Due), "eq", "2026-11-04"), F(MainF(Due), "ne", "2026-11-04"), F(MainF(Due), "gt", "2026-11-04"), F(MainF(Due), "gte", "2026-11-04"),
            F(MainF(Due), "lt", "2026-11-04"), F(MainF(Due), "lte", "2026-11-04"), F(MainF(Due), "between", new[] { "2026-11-01", "2026-11-30" }),
            F(MainF(Due), "withinNextDays", 7), F(MainF(Due), "withinLastDays", 7), F(MainF(Due), "beforeToday"),
            F(MainF(Due), "isEmpty"), F(MainF(Due), "isNotEmpty"),
            F(MainF(At), "eq", "2026-11-04"), F(MainF(At), "gt", "2026-11-04"), F(MainF(At), "lte", "2026-11-04T10:00:00"),
            F(MainF(At), "between", new[] { "2026-11-01", "2026-11-05" }), F(MainF(At), "isEmpty"),
        };
        var rejected = 0;
        foreach (var filter in filters)
        {
            var def = Def(groups: [G(MainF(Status))], measures: AmountMeasures(), filters: [filter]);
            var (memory, sql) = await Both(def);
            if (memory.Engine is null)
            {
                // 欄位型別不支援的運算子在授權階段就被擋下，兩種引擎走不到聚合，結果必須一樣
                sql.State.Should().Be(memory.State);
                sql.Message.Should().Be(memory.Message);
                rejected++;
                continue;
            }
            ShouldMatch(memory, sql, $"{filter.Field.FieldId} {filter.Op} {filter.Value}");
        }
        rejected.Should().BeLessThan(filters.Count / 4, "多數條件應實際比對到兩條路徑");
        // 多個條件同時成立
        var combined = Def(groups: [G(MainF(Tags))], measures: AmountMeasures(),
            filters: [F(MainF(Amount), "gte", 10), F(MainF(Status), "ne", OptOpen), F(MainF(Due), "lt", "2027-01-01")]);
        var (m2, s2) = await Both(combined);
        ShouldMatch(m2, s2, "combined");
    }

    [Fact]
    public async Task DateRange_PrimaryDateField_MatchesMemoryPath()
    {
        await Prepare();
        await SeedOrders();
        foreach (var range in new[]
        {
            new ReportDateRange("thisMonth", null), new ReportDateRange("lastMonth", null), new ReportDateRange("thisYear", null),
            new ReportDateRange("lastDays", 30), new ReportDateRange("today", null), new ReportDateRange("thisWeek", null),
        })
        {
            foreach (var primary in new[] { Due, At })
            {
                var def = Def(groups: [G(MainF(Status))], measures: AmountMeasures(), primary: MainF(primary), range: range);
                var (memory, sql) = await Both(def);
                ShouldMatch(memory, sql, $"{primary.Key} {range.Kind}");
            }
        }
    }

    [Fact]
    public async Task OwnScope_OnlyCountsOwnRecords_LikeMemoryPath()
    {
        await Prepare();
        await SeedOrders();
        As("sales01");
        var (memory, sql) = await Both(Def(groups: [G(MainF(Status))], measures: AmountMeasures()));
        ShouldMatch(memory, sql, "own");
        sql.Total!["n"].Should().BeLessThan(40m);
    }

    [Fact]
    public async Task EmptyResult_MatchesMemoryPath()
    {
        await Prepare();
        var (memory, sql) = await Both(Def(groups: [G(MainF(Status))], measures: AmountMeasures()));
        ShouldMatch(memory, sql, "no data");
        sql.State.Should().Be("empty");
    }

    private async Task<FieldConfig> CurrentConfig(string module, long fieldId) =>
        (await Detail(module)).Blocks.SelectMany(b => b.Fields).Single(f => f.Id == fieldId).Config;

    private async Task<List<long?>> ScaledValues(long fieldId) =>
        await Db.Context.Set<RecordIndex>().AsNoTracking().Where(i => i.FieldId == fieldId).OrderBy(i => i.RecordId).Select(i => i.ScaledValue).ToListAsync();

    [Fact]
    public async Task ScaleChange_RebuildsIndexInTheSameCommand_And_ReportsStayEqual()
    {
        await Prepare();
        await Seed("order", "admin", (Amount, 1.23m));
        await Seed("order", "admin", (Amount, 5m));
        (await ScaledValues(Amount.Id)).Should().Equal(123L, 500L);

        // 降到一位小數：1.23 無法精確表示，ScaledValue 必須是 NULL（不是四捨五入的 12），5 變成 50
        (await Update(Amount, (await CurrentConfig("order", Amount.Id)) with { Scale = 1 })).IsSuccess.Should().BeTrue();
        (await ScaledValues(Amount.Id)).Should().Equal(null, 50L);
        var def = Def(measures: [M("s", "sum", MainF(Amount)), M("n", "countMain")]);
        Svc<ReportOptions>().Engine = ReportEngineMode.Auto;
        var lowered = await Run(def);
        lowered.Total!["s"].Should().Be(6.23m, "記憶體路徑加總的是 1.23，不是索引的 12");
        lowered.Engine.Should().Be("memory");
        lowered.FallbackReasons.Should().NotBeEmpty();

        // 升到三位小數：全部精確，回到 SQL 路徑
        (await Update(Amount, (await CurrentConfig("order", Amount.Id)) with { Scale = 3 })).IsSuccess.Should().BeTrue();
        (await ScaledValues(Amount.Id)).Should().Equal(1230L, 5000L);
        var raised = await Run(def);
        raised.Engine.Should().Be("sql");
        raised.Total!["s"].Should().Be(6.23m);
    }

    // ---- 審查反例：SQL 路徑證明不了相同時必須退回記憶體，結果仍與記憶體路徑相同 ----

    private async Task AssertFallsBackWithSameResult(ReportDefinition def, string reasonPrefix)
    {
        var (memory, auto) = await Both(def, second: ReportEngineMode.Auto);
        ShouldMatch(memory, auto, reasonPrefix, expectedEngine: "memory");
        auto.FallbackReasons.Should().Contain(r => r.StartsWith(reasonPrefix));
    }

    [Fact]
    public async Task OverflowingScaledValues_GroupedTogether_FallBack()
    {
        await Prepare();
        await Seed("order", "admin", (Amount, 100000000000000000000m));   // 1e20：× 100 超出 long，ScaledValue 為 NULL
        await Seed("order", "admin", (Amount, 200000000000000000000m));
        await AssertFallsBackWithSameResult(Def(groups: [G(MainF(Amount))], measures: [M("n", "countMain")]), "scaledUnavailable");
    }

    [Fact]
    public async Task NumberConditions_AtPrecisionLimits_MatchMemoryPath()
    {
        await Prepare();
        await Seed("order", "admin", (Amount, 999999999999999m));
        await Seed("order", "admin", (Amount, 999999999999999.01m));
        foreach (var (op, value) in new (string, object)[]
        {
            ("ne", 999999999999999m), ("eq", 999999999999999m), ("eq", 999999999999999.01m), ("gt", 999999999999999m),
            ("gte", 999999999999999.01m), ("lt", 999999999999999.01m), ("lte", 999999999999999m),
            ("between", new[] { 999999999999999m, 999999999999999.005m }),
        })
        {
            var (memory, sql) = await Both(Def(measures: [M("n", "countMain"), M("s", "sum", MainF(Amount))], filters: [F(MainF(Amount), op, value)]));
            ShouldMatch(memory, sql, $"{op} {value}");
        }
    }

    [Fact]
    public async Task TruncatedIndexText_NeAndGroup_FallBack()
    {
        await Prepare();
        var a = new string('a', 440);
        await Seed("order", "admin", (Note, a));
        await Seed("order", "admin", (Note, a + "b"));
        await AssertFallsBackWithSameResult(Def(groups: [G(MainF(Note))], measures: [M("n", "countMain")]), "textTruncated");
        await AssertFallsBackWithSameResult(Def(measures: [M("n", "countMain")], filters: [F(MainF(Note), "ne", a)]), "textTruncated");
    }

    [Fact]
    public async Task TruncatedIndexText_SurrogatePairAtTheLimit_FallsBack()
    {
        await Prepare();
        var withEmoji = new string('a', 439) + "😀";     // 第 440 個 UTF-16 單位是高代理字元，索引只留 439 個
        await Seed("order", "admin", (Note, withEmoji));
        await Seed("order", "admin", (Note, new string('a', 439)));
        await AssertFallsBackWithSameResult(Def(groups: [G(MainF(Note))], measures: [M("n", "countMain")]), "textTruncated");
    }

    [Fact]
    public async Task ChangedFieldConfig_AfterPlanning_IsDetectedInsideTheReadSnapshot()
    {
        await Prepare();
        await Seed("order", "admin", (Amount, 1.23m));
        var query = new ReportSqlQuery(
            new ReportSqlScope((await Db.Context.Set<Module>().SingleAsync(m => m.Key == "order")).Id, null),
            [], [new ReportSqlMeasure("n", "countMain", null, null)], [], [], [new ReportSqlConfigStamp(Amount.Id, "Decimal|Active|9|||||")]);
        var outcome = await Svc<IReportQueryRepository>().AggregateAsync(query, CancellationToken.None);
        outcome.FallbackReason.Should().Be($"configChanged:{Amount.Id}");
    }
}
