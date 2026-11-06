using System.Diagnostics;
using System.Text.Json;
using DynamicModules.Application;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddField;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Commands.CreateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.ModuleService.Queries.GetModule;
using DynamicModules.Application.Dynamic.ReportService;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Persistence;
using DynamicModules.Persistence.Seed;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>
/// 效能基準（spec §5）：1 萬筆主記錄、每筆 3 列子記錄、含分組與去重，5 次中位數要在 1 秒內。
/// 預設不跑，用 <c>dotnet test poc/backend --filter Category=Benchmark</c> 執行。
/// </summary>
[Trait("Category", "Benchmark")]
public class ReportBenchmarkTests(ITestOutputHelper output)
{
    private const int MainCount = 10_000;
    private const int ChildPerMain = 3;
    private const int Runs = 5;

    private sealed class FakeUser : ICurrentUser { public string UserId => "admin"; }

    private sealed class FakeTime : IBusinessTime
    {
        public DateTime Now => new(2026, 11, 5, 9, 0, 0);
        public DateOnly Today => DateOnly.FromDateTime(Now);
    }

    private static I18nText Text(string zh) => new([KeyValuePair.Create("zh-TW", zh)]);

    [Fact]
    public async Task Run_10k_Main_30k_Child_GroupAndDedupe_Under1s()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        await SeedData.EnsureSeededAsync(db.Context);
        await DemoPeople.EnsureAsync(db.Context);
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddPersistenceRepositories();
        services.AddScoped(_ => db.Context);
        services.AddScoped<ITransactionManager, EfTransactionManager>();
        services.AddSingleton<ICurrentUser, FakeUser>();
        services.AddSingleton<IBusinessTime, FakeTime>();
        services.AddSingleton(new AuthSettings(false));
        await using var sp = services.BuildServiceProvider();
        var mediator = sp.GetRequiredService<IMediator>();

        async Task<ModuleDetailResponse> Module(string key) => (ModuleDetailResponse)(await mediator.Send(new GetModuleRequest(key))).Data!;
        async Task<FieldResponse> Add(string key, FieldType type, FieldConfig? config = null) =>
            (FieldResponse)(await mediator.Send(new AddFieldRequest(key, (await Module(key)).Blocks[0].Id, type, Text(type.ToString()), config ?? new FieldConfig()))).Data!;

        await mediator.Send(new CreateModuleRequest("bench-order", Text("訂單"), null, null));
        var amount = await Add("bench-order", FieldType.Decimal);
        var region = await Add("bench-order", FieldType.Text);
        await mediator.Send(new CreateModuleRequest("bench-ship", Text("出貨"), null, null));
        var link = await Add("bench-ship", FieldType.Reference,
            new FieldConfig { RefTarget = new ReferenceTarget(ReferenceTarget.ModuleKind, "bench-order"), Relation = ReferenceRelation.Detail });
        var qty = await Add("bench-ship", FieldType.Integer);
        var warehouse = await Add("bench-ship", FieldType.Text);
        (await mediator.Send(new ActivateModuleRequest("bench-order"))).IsSuccess.Should().BeTrue();
        (await mediator.Send(new ActivateModuleRequest("bench-ship"))).IsSuccess.Should().BeTrue();

        Record NewRecord(long moduleId, object values)
        {
            var r = new Record { ModuleId = moduleId, Title = "r", OwnerUserId = "admin", ValuesJson = JsonSerializer.Serialize(values), RowVersion = "v1" };
            r.StampCreated("admin", new DateTime(2026, 11, 5));
            return r;
        }

        var orderModule = (await Module("bench-order")).Id;
        var shipModule = (await Module("bench-ship")).Id;
        var rnd = new Random(20261105);
        var seed = Stopwatch.StartNew();
        var mains = Enumerable.Range(0, MainCount)
            .Select(i => NewRecord(orderModule, new Dictionary<string, object> { [amount.Key] = 10m, [region.Key] = $"R{i % 10}" })).ToList();
        db.Context.AddRange(mains);
        await db.Context.SaveChangesAsync();
        var kids = mains.SelectMany(m => Enumerable.Range(0, ChildPerMain).Select(_ => NewRecord(shipModule,
            new Dictionary<string, object> { [link.Key] = m.Id, [qty.Key] = 2, [warehouse.Key] = $"W{rnd.Next(5)}" }))).ToList();
        db.Context.AddRange(kids);
        await db.Context.SaveChangesAsync();
        db.Context.ChangeTracker.Clear();
        seed.Stop();
        output.WriteLine($"寫入 {MainCount} 筆主記錄、{MainCount * ChildPerMain} 筆子記錄：{seed.Elapsed.TotalSeconds:F1} s");

        var main = (FieldResponse f) => new ReportFieldRef("main", f.Id, null);
        var child = (FieldResponse f) => new ReportFieldRef("child", f.Id, null);
        var definition = new ReportDefinition(1, "bench-order", new ReportChild("bench-ship", link.Id), [],
            [new ReportGroup(child(warehouse), null)],
            [new ReportMeasure("sum", "sum", main(amount), null), new ReportMeasure("qty", "sum", child(qty), null), new ReportMeasure("mains", "countMain", null, null)],
            [], null, null, [], "table");

        var runner = sp.GetRequiredService<ReportRunner>();
        var times = new List<double>();
        for (var i = 0; i < Runs; i++)
        {
            var sw = Stopwatch.StartNew();
            var r = await runner.RunAsync(definition, new ReportRunOptions(null, null), CancellationToken.None);
            sw.Stop();
            times.Add(sw.Elapsed.TotalMilliseconds);
            r.State.Should().Be("ok");
            r.Total!["sum"].Should().Be(MainCount * 10m); // 去重：3 列子記錄不會讓金額變 3 倍
            r.Total["qty"].Should().Be(MainCount * ChildPerMain * 2m);
            r.Total["mains"].Should().Be(MainCount);
            r.Groups.Should().HaveCount(5);
        }
        var median = times.Order().ElementAt(Runs / 2);
        output.WriteLine($"各次（ms）：{string.Join(", ", times.Select(t => t.ToString("F0")))}");
        output.WriteLine($"中位數：{median:F0} ms（{Runs} 次）");
        output.WriteLine($".NET {Environment.Version}");
        median.Should().BeLessThan(1000);
    }
}
