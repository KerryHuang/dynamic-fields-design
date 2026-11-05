using System.Diagnostics;
using System.Text.Json;
using DynamicModules.Application;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddField;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddOption;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Commands.CreateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.ModuleService.Queries.GetModule;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetPagedRecords;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Persistence;
using DynamicModules.Persistence.Seed;
using FluentAssertions;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>
/// 效能基準（spec §15 風險 9）：1 萬筆記錄、30 個可篩選可排序欄位。預設不跑，
/// 用 <c>dotnet test poc/backend --filter Category=Benchmark</c> 執行。只斷言筆數，不斷言時間。
/// </summary>
[Trait("Category", "Benchmark")]
public class ListBenchmarkTests(ITestOutputHelper output)
{
    private const int RecordCount = 10_000;
    private const int Runs = 5;
    private const int PageSize = 20;

    private sealed class FakeUser : ICurrentUser { public string UserId => "admin"; }

    private sealed class FakeTime : IBusinessTime
    {
        public DateTime Now => new(2026, 11, 3, 9, 0, 0);
        public DateOnly Today => DateOnly.FromDateTime(Now);
    }

    private static I18nText Text(string zh) => new([KeyValuePair.Create("zh-TW", zh)]);

    private static async Task<double> MedianMs(Func<Task> action)
    {
        var times = new List<double>();
        for (var i = 0; i < Runs; i++)
        {
            var sw = Stopwatch.StartNew();
            await action();
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        return times.Order().ElementAt(Runs / 2);
    }

    [Fact]
    public async Task List_10k_Records_30_IndexedFields()
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
        await using var sp = services.BuildServiceProvider();
        var mediator = sp.GetRequiredService<IMediator>();

        await using (var cmd = db.Connection.CreateCommand())
        {
            cmd.CommandText = "select sqlite_version()";
            output.WriteLine($"SQLite {await cmd.ExecuteScalarAsync()}");
        }
        output.WriteLine($".NET {Environment.Version}");

        // 模組：8 個整數、8 個文字、7 個日期、7 個 enum（各 5 個選項），共 30 個欄位，全部可篩選可排序
        (await mediator.Send(new CreateModuleRequest("bench", Text("基準"), null, null))).IsCreated.Should().BeTrue();
        var module = (ModuleDetailResponse)(await mediator.Send(new GetModuleRequest("bench"))).Data!;
        var blockId = module.Blocks[0].Id;
        var titleKey = module.Blocks[0].Fields.Single(f => f.Id == module.TitleFieldId).Key;
        var config = new FieldConfig { IsFilterable = true, IsSortable = true };

        async Task<List<FieldResponse>> AddFields(FieldType type, int n)
        {
            var list = new List<FieldResponse>();
            for (var i = 0; i < n; i++)
                list.Add((FieldResponse)(await mediator.Send(new AddFieldRequest("bench", blockId, type, Text($"{type}{i}"), config))).Data!);
            return list;
        }

        var ints = await AddFields(FieldType.Integer, 8);
        var texts = await AddFields(FieldType.Text, 8);
        var dates = await AddFields(FieldType.Date, 7);
        var enums = await AddFields(FieldType.Enum, 7);
        var optionValues = new List<List<string>>();
        foreach (var e in enums)
        {
            var values = new List<string>();
            for (var o = 0; o < 5; o++)
                values.Add(((OptionResponse)(await mediator.Send(new AddOptionRequest(e.Id, Text($"選項{o}"), null))).Data!).Value);
            optionValues.Add(values);
        }
        (await mediator.Send(new ActivateModuleRequest("bench"))).IsSuccess.Should().BeTrue();

        // 1 萬筆：走 CreateRecordHandler（正式索引路徑）。用固定種子的亂數，同時在記憶體留一份用來算預期筆數
        var rnd = new Random(20261103);
        var firstDate = new DateOnly(2026, 1, 1);
        var model = new List<(int Int0, string Text0, DateOnly Date0)>(RecordCount);
        var create = Stopwatch.StartNew();
        for (var r = 0; r < RecordCount; r++)
        {
            var values = new Dictionary<string, JsonElement?> { [titleKey] = JsonSerializer.SerializeToElement($"R{r:00000}") };
            var int0 = 0;
            var text0 = "";
            var date0 = default(DateOnly);
            for (var i = 0; i < ints.Count; i++)
            {
                var v = rnd.Next(1000);
                if (i == 0) int0 = v;
                values[ints[i].Key] = JsonSerializer.SerializeToElement(v);
            }
            for (var i = 0; i < texts.Count; i++)
            {
                var v = "t" + rnd.Next(100);
                if (i == 0) text0 = v;
                values[texts[i].Key] = JsonSerializer.SerializeToElement(v);
            }
            for (var i = 0; i < dates.Count; i++)
            {
                var v = firstDate.AddDays(rnd.Next(365));
                if (i == 0) date0 = v;
                values[dates[i].Key] = JsonSerializer.SerializeToElement(v.ToString("yyyy-MM-dd"));
            }
            for (var i = 0; i < enums.Count; i++)
                values[enums[i].Key] = JsonSerializer.SerializeToElement(optionValues[i][rnd.Next(5)]);

            var res = await mediator.Send(new CreateRecordRequest("bench", values));
            res.IsCreated.Should().BeTrue(res.Message);
            model.Add((int0, text0, date0));
            db.Context.ChangeTracker.Clear();
        }
        create.Stop();
        output.WriteLine($"建立 {RecordCount} 筆：總計 {create.Elapsed.TotalSeconds:F1} s，每筆平均 {create.Elapsed.TotalMilliseconds / RecordCount:F2} ms");

        async Task<PagedResponse<DynamicRecordResponse>> Query(int page, string? filter, params (string Key, string Order)[] sorts)
        {
            var r = await mediator.Send(new GetPagedRecordsRequest("bench", page, PageSize, filter,
                sorts.Select(s => s.Key).ToList(), sorts.Select(s => s.Order).ToList(), null));
            r.IsSuccess.Should().BeTrue(r.Message);
            return (PagedResponse<DynamicRecordResponse>)r.Data!;
        }

        async Task Measure(string name, int expectedTotal, int expectedItems, int page, string? filter, params (string, string)[] sorts)
        {
            var ms = await MedianMs(async () =>
            {
                var res = await Query(page, filter, sorts);
                res.TotalCount.Should().Be(expectedTotal);
                res.Items.Count.Should().Be(expectedItems);
            });
            output.WriteLine($"{name}：中位數 {ms:F1} ms（{Runs} 次）");
        }

        string One(long fieldId, string op, object value) =>
            JsonSerializer.Serialize(new { all = new[] { new { field = fieldId, op, value } } });

        // 單一條件 + 單欄排序
        var gte500 = model.Count(m => m.Int0 >= 500);
        await Measure("單一條件 + 單欄排序，第 1 頁", gte500, PageSize, 1, One(ints[0].Id, "gte", 500), (ints[1].Key, "desc"));
        await Measure("單一條件 + 單欄排序，第 100 頁", gte500, PageSize, 100, One(ints[0].Id, "gte", 500), (ints[1].Key, "desc"));

        // 3 個條件 + 3 欄排序
        var from = new DateOnly(2026, 3, 1);
        var to = new DateOnly(2026, 9, 30);
        var triple = model.Count(m => m.Int0 >= 250 && m.Text0.StartsWith("t1", StringComparison.Ordinal) && m.Date0 >= from && m.Date0 <= to);
        var tripleFilter = JsonSerializer.Serialize(new
        {
            all = new object[]
            {
                new { field = ints[0].Id, op = "gte", value = 250 },
                new { field = texts[0].Id, op = "startsWith", value = "t1" },
                new { field = dates[0].Id, op = "between", value = new[] { from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd") } },
            }
        });
        await Measure("3 條件 + 3 欄排序，第 1 頁", triple, PageSize, 1, tripleFilter,
            (ints[1].Key, "desc"), (texts[1].Key, "asc"), (enums[0].Key, "asc"));

        // 不篩選只算 count（每頁 1 筆）
        var countMs = await MedianMs(async () =>
        {
            var r = await mediator.Send(new GetPagedRecordsRequest("bench", 1, 1, null, [], [], null));
            ((PagedResponse<DynamicRecordResponse>)r.Data!).TotalCount.Should().Be(RecordCount);
        });
        output.WriteLine($"不篩選只算 count：中位數 {countMs:F1} ms（{Runs} 次）");
    }
}
