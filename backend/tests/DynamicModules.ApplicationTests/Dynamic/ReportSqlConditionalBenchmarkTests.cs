using System.Diagnostics;
using System.Text.Json;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.ReportService;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.Repositories.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>
/// 帶條件量值在 SQL 路徑的成本（kr1a，J10）：每個帶條件的 sum／avg 有三個逐列謂詞（EXISTS 子查詢），countIf 一個。
/// 預設不跑，用 <c>dotnet test backend --filter Category=Benchmark</c> 執行；結果寫在測試輸出，與不帶條件的同形狀對照。
/// </summary>
[Trait("Category", "Benchmark")]
public class ReportSqlConditionalBenchmarkTests(ITestOutputHelper output) : ReportTestBase
{
    private const int Count = 10_000;
    private const int Runs = 5;

    protected override bool FieldsFilterable => true;

    [Fact]
    public async Task ConditionalAvg_10k_Orders_SqlPath_Under1s()
    {
        await Arrange();
        foreach (var f in (await Detail("order")).Blocks.SelectMany(b => b.Fields))
            (await Update(f, f.Config with { IsFilterable = true })).IsSuccess.Should().BeTrue();
        await Svc<ISystemSettingRepository>().SetAsync(SystemSetting.IndexBackfillVersion, RecordIndexer.BackfillVersion, CancellationToken.None);
        await Db.Context.SaveChangesAsync();

        var module = (await Svc<IModuleRepository>().GetByKeyAsync("order", true, CancellationToken.None))!;
        var records = Enumerable.Range(0, Count).Select(i =>
        {
            var json = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                [Amount.Key] = (decimal)(i % 500) + 0.25m, [Status.Key] = i % 3 == 0 ? OptDone : OptOpen, [Sales.Key] = i % 2 == 0 ? "sales01" : "sales02",
            });
            var r = new DynamicModules.Domain.Entities.Dynamic.Record { ModuleId = module.Id, Title = "r", OwnerUserId = "admin", ValuesJson = json, RowVersion = "v1" };
            r.StampCreated("admin", new DateTime(2026, 11, 4));
            return r;
        }).ToList();
        Db.Context.AddRange(records);
        await Db.Context.SaveChangesAsync();
        var registry = Svc<FieldTypeRegistry>();
        foreach (var r in records)
        {
            var rows = RecordIndexer.Build(module, RecordValueProcessor.Parse(r.ValuesJson), registry);
            foreach (var row in rows) row.RecordId = r.Id;
            Db.Context.AddRange(rows);
        }
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();

        var done = F(MainF(Status), "eq", OptDone);
        var cases = new (string Name, ReportDefinition Def)[]
        {
            ("不帶條件 avg", Def(groups: [G(MainF(Sales))], measures: [M("a", "avg", MainF(Amount))])),
            ("帶條件 avg", Def(groups: [G(MainF(Sales))], measures: [M("a", "avg", MainF(Amount), done)])),
            ("帶條件 avg＋sum＋countIf", Def(groups: [G(MainF(Sales))], measures: [M("a", "avg", MainF(Amount), done), M("s", "sum", MainF(Amount), done), M("c", "countIf", cond: done)])),
            ("ratio（對照）", Def(groups: [G(MainF(Sales))], measures: [M("r", "ratio", cond: done)])),
        };
        Svc<ReportOptions>().Engine = ReportEngineMode.SqlOnly;
        foreach (var (name, def) in cases)
        {
            var times = new List<double>();
            for (var i = 0; i < Runs; i++)
            {
                var sw = Stopwatch.StartNew();
                var result = await Svc<ReportRunner>().RunAsync(def, new ReportRunOptions(null, null), CancellationToken.None);
                sw.Stop();
                result.State.Should().Be("ok");
                result.Engine.Should().Be("sql");
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            var median = times.Order().ElementAt(Runs / 2);
            output.WriteLine($"{name}：{median:F0} ms（{Runs} 次中位數；各次 {string.Join(", ", times.Select(t => t.ToString("F0")))}）");
            median.Should().BeLessThan(1000, name);
        }
    }
}
