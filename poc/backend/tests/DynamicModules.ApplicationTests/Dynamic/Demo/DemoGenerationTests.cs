using System.Globalization;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.DemoService;
using DynamicModules.Application.Dynamic.DemoService.Commands.GenerateDemoData;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.ApplicationTests.Dynamic.Demo;

/// <summary>測試輔助：直接以 AsNoTracking 讀 DbContext。</summary>
internal static class DemoDb
{
    public static async Task<List<string>> Snapshot(DynamicDbContext db)
    {
        var modules = await db.Set<Module>().AsNoTracking().ToDictionaryAsync(m => m.Id, m => m.Key);
        var fields = await db.Set<Field>().AsNoTracking().ToDictionaryAsync(f => f.Id, f => f.Key);
        var records = (await db.Set<Record>().AsNoTracking().ToListAsync())
            .OrderBy(r => modules[r.ModuleId], StringComparer.Ordinal).ThenBy(r => r.Id)
            .Select(r => $"{modules[r.ModuleId]}|{r.Title}|{r.ValuesJson}|{r.OwnerUserId}|{r.CreatedUserId}|{r.CreatedTime:O}|{r.ModifiedUserId}|{r.ModifiedTime:O}");
        var history = (await db.Set<RecordHistory>().AsNoTracking().ToListAsync()).OrderBy(h => h.Id)
            .Select(h => $"{h.RecordId}|{h.Operation}|{(h.FieldId is { } id ? fields[id] : null)}|{h.OldValue}|{h.NewValue}|{h.ChangedUserId}|{h.ChangedTime:O}");
        return [.. records, .. history];
    }

    public static Task<List<Record>> Records(DynamicDbContext db, string moduleKey) =>
        db.Set<Record>().AsNoTracking()
            .Where(r => !r.DeleteFlag && db.Set<Module>().Any(m => m.Id == r.ModuleId && m.Key == moduleKey))
            .OrderBy(r => r.Id).ToListAsync();

    public static async Task<string> FieldKey(DynamicDbContext db, string moduleKey, string label) =>
        (await db.Set<Field>().AsNoTracking()
            .Where(f => db.Set<Module>().Any(m => m.Id == f.ModuleId && m.Key == moduleKey)).ToListAsync())
        .Single(f => f.LabelI18n.Resolve("zh-TW") == label).Key;

    public static string? Text(Record r, string key) =>
        JsonDocument.Parse(r.ValuesJson).RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    public static DateOnly Date(string? s) => DateOnly.ParseExact(s!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>各自建新庫產生（確定性、跨年、回滾）。</summary>
public class DemoGenerationTests
{
    private static async Task<DemoTestHost> Generated(DateOnly today)
    {
        var host = await DemoTestHost.CreateAsync(today);
        var r = await host.Send(new GenerateDemoDataRequest(today));
        r.IsSuccess.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => $"{e.Key}:{string.Join(",", e.Value)}")));
        return host;
    }

    [Fact]
    public async Task Generate_SameToday_TwiceOnFreshDbs_IdenticalSnapshot()
    {
        await using var a = await Generated(DemoTestHost.Today);
        await using var b = await Generated(DemoTestHost.Today);

        var snapshot = await DemoDb.Snapshot(a.Db.Context);
        snapshot.Should().HaveCountGreaterThan(300);
        snapshot.Should().Equal(await DemoDb.Snapshot(b.Db.Context));
    }

    [Fact]
    public async Task Generate_OnNewYearsDay_SucceedsWithDistributions()
    {
        var today = new DateOnly(2027, 1, 1);
        await using var host = await Generated(today);
        var db = host.Db.Context;

        var status = await DemoDb.FieldKey(db, "order", "狀態");
        var due = await DemoDb.FieldKey(db, "order", "交期");
        var orderDate = await DemoDb.FieldKey(db, "order", "訂單日期");
        var open = (await db.Set<FieldOption>().AsNoTracking().ToListAsync())
            .Where(o => o.LabelI18n.Resolve("zh-TW") is "已確認" or "出貨中").Select(o => $"opt_{o.Id}").ToHashSet();
        var orders = await DemoDb.Records(db, "order");
        orders.Count(o => DemoDb.Date(DemoDb.Text(o, due)) < today && open.Contains(DemoDb.Text(o, status)!))
            .Should().BeGreaterThanOrEqualTo(4);
        var december = orders.Where(o => DemoDb.Date(DemoDb.Text(o, orderDate)).Month == 12).ToList();
        december.Should().NotBeEmpty().And.OnlyContain(o => o.Title.StartsWith("SO-202612-"));

        var mold = await DemoDb.FieldKey(db, "mold-care", "模具");
        var careDate = await DemoDb.FieldKey(db, "mold-care", "保養日期");
        var next = await DemoDb.FieldKey(db, "mold-care", "下次保養日");
        (await DemoDb.Records(db, "mold-care")).GroupBy(c => DemoDb.Text(c, mold))
            .Select(g => DemoDb.Date(DemoDb.Text(g.MaxBy(c => DemoDb.Date(DemoDb.Text(c, careDate)))!, next)))
            .Count(d => d >= today && d <= today.AddDays(6)).Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Generate_FailsMidway_RollsBackEverything()
    {
        await using var host = await DemoTestHost.CreateAsync(configure: DemoPeopleTests.WithoutMaintenanceUser);
        var db = host.Db.Context;
        var modules = await db.Set<Module>().AsNoTracking().CountAsync();
        var records = await db.Set<Record>().AsNoTracking().CountAsync();
        var sequences = await db.Set<NumberSequence>().AsNoTracking().CountAsync();
        var first = DemoTimeline.Plan(DemoTestHost.Today, DemoCatalog.Scenarios).OrderBy(s => s.At).First(s => s.UserId == "maint01");

        var r = await host.Send(new GenerateDemoDataRequest(DemoTestHost.Today));

        r.IsSuccess.Should().BeFalse();
        r.IsConflict.Should().BeFalse();
        r.IsForbidden.Should().BeFalse();
        r.IsNotFound.Should().BeFalse();
        r.Errors["demo"].Single().Should().StartWith($"{first.Label}：");
        (await db.Set<Module>().AsNoTracking().CountAsync()).Should().Be(modules);
        (await db.Set<Record>().AsNoTracking().CountAsync()).Should().Be(records);
        (await db.Set<NumberSequence>().AsNoTracking().CountAsync()).Should().Be(sequences);
    }
}

/// <summary>用共用的產生結果（Today = 2026-11-05）就能驗的部分。</summary>
[Collection(DemoGeneratedCollection.Name)]
public class DemoGenerationSharedTests(DemoGeneratedFixture fx)
{
    /// <summary>附件的值只能經上傳端點產生（產生器不寫檔案），舊的八個 Demo 模組也沒有附件欄位。</summary>
    private static readonly FieldType[] RecordValueTypes = [.. Enum.GetValues<FieldType>().Where(t => t != FieldType.Attachment)];

    [Fact]
    public async Task Generate_CoversAllRecordValueFieldTypes()
    {
        fx.Generated();
        var db = fx.Host.Db.Context;
        var moduleIds = await db.Set<Module>().AsNoTracking()
            .Where(m => DemoCatalog.ActivationOrder.Contains(m.Key)).Select(m => m.Id).ToListAsync();
        moduleIds.Should().HaveCount(8);
        var fields = await db.Set<Field>().AsNoTracking()
            .Where(f => moduleIds.Contains(f.ModuleId) && f.Status == FieldStatus.Active).ToListAsync();
        fields.Select(f => f.Type).Distinct().Should().BeEquivalentTo(RecordValueTypes);

        var filled = (await db.Set<Record>().AsNoTracking().Where(r => moduleIds.Contains(r.ModuleId)).ToListAsync())
            .SelectMany(r => JsonDocument.Parse(r.ValuesJson).RootElement.EnumerateObject())
            .Where(p => p.Value.ValueKind != JsonValueKind.Null).Select(p => p.Name).ToHashSet();
        foreach (var type in RecordValueTypes)
            fields.Should().Contain(f => f.Type == type && filled.Contains(f.Key), $"{type} 要有欄位在某筆記錄有值");
    }

    [Fact]
    public async Task Generate_ShiftedToday_SameRelativeDates()
    {
        fx.Generated();
        var shifted = DemoTestHost.Today.AddDays(10);
        await using var host = await DemoTestHost.CreateAsync(shifted);
        (await host.Send(new GenerateDemoDataRequest(shifted))).IsSuccess.Should().BeTrue();

        async Task<List<DateOnly>> OrderDates(DynamicDbContext db)
        {
            var key = await DemoDb.FieldKey(db, "order", "訂單日期");
            return (await DemoDb.Records(db, "order")).Select(o => DemoDb.Date(DemoDb.Text(o, key))).ToList();
        }
        var before = await OrderDates(fx.Host.Db.Context);
        var after = await OrderDates(host.Db.Context);
        after.Should().HaveCount(before.Count).And.Equal(before.Select(d => d.AddDays(10)));
    }

    [Fact]
    public void Generate_ReportsProgress()
    {
        fx.Generated();
        var steps = ((DemoGenerateResult)fx.Result.Data!).Steps;

        fx.Progress.Should().HaveCount(steps);
        fx.Progress[^1].Should().Be((steps, steps));
        fx.Progress.Should().OnlyContain(p => p.Total == steps);
    }
}
