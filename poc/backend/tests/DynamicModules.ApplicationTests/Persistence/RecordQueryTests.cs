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
using DynamicModules.Domain.Conditions;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Persistence;
using DynamicModules.Persistence.Seed;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Persistence;

public class RecordQueryTests : IAsyncLifetime
{
    private static readonly DateOnly Today = new(2026, 11, 3);

    private SqliteTestDb _db = null!;
    private ServiceProvider _sp = null!;
    private ModuleDetailResponse _m = null!;
    private string _nameKey = "";

    private sealed class FakeUser : ICurrentUser { public string UserId => "admin"; }

    private sealed class FakeTime : IBusinessTime
    {
        public DateTime Now => new(2026, 11, 3, 9, 0, 0);
        public DateOnly Today => DateOnly.FromDateTime(Now);
    }

    public async Task InitializeAsync()
    {
        _db = await SqliteTestDb.CreateAsync();
        await SeedData.EnsureSeededAsync(_db.Context);
        await DemoPeople.EnsureAsync(_db.Context);
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddPersistenceRepositories();
        services.AddScoped(_ => _db.Context);
        services.AddScoped<ITransactionManager, EfTransactionManager>();
        services.AddSingleton<ICurrentUser, FakeUser>();
        services.AddSingleton<IBusinessTime, FakeTime>();
        _sp = services.BuildServiceProvider();
        await Send(new CreateModuleRequest("order", Text("訂單"), null, null));
        _m = (ModuleDetailResponse)(await Send(new GetModuleRequest("order"))).Data!;
        _nameKey = _m.Blocks[0].Fields.Single(f => f.Id == _m.TitleFieldId).Key;
    }

    public async Task DisposeAsync()
    {
        await _sp.DisposeAsync();
        await _db.DisposeAsync();
    }

    private Task<TRes> Send<TRes>(IRequest<TRes> request) => _sp.GetRequiredService<IMediator>().Send(request);

    private static I18nText Text(string zh = "欄位") => new([KeyValuePair.Create("zh-TW", zh)]);

    private static Dictionary<string, JsonElement?> Vals(params (string Key, object? Value)[] items) =>
        items.ToDictionary(i => i.Key, i => i.Value is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(i.Value));

    private async Task<FieldResponse> AddField(FieldType type, string label, bool sortable = false) =>
        (FieldResponse)(await Send(new AddFieldRequest("order", _m.Blocks[0].Id, type, Text(label),
            new FieldConfig { IsFilterable = true, IsSortable = sortable && FieldTypeCapabilities.SupportsSort(type) }))).Data!;

    private async Task<OptionResponse> AddOption(FieldResponse f, string label) =>
        (OptionResponse)(await Send(new AddOptionRequest(f.Id, Text(label), null))).Data!;

    private async Task<long> Create(string title, params (string Key, object? Value)[] values)
    {
        var r = await Send(new CreateRecordRequest("order", Vals(values.Prepend((_nameKey, title)).ToArray())));
        r.IsCreated.Should().BeTrue(r.Message);
        return ((DynamicRecordResponse)r.Data!).Id;
    }

    private async Task<Module> LoadModule() =>
        (await _sp.GetRequiredService<IModuleRepository>().GetByKeyAsync("order", true, default))!;

    private async Task<ConditionNode> Parse(string json)
    {
        var module = await LoadModule();
        var userIds = await _sp.GetRequiredService<IModuleRepository>().ListUserIdsAsync(default);
        var result = ConditionParser.Parse(JsonDocument.Parse(json).RootElement, module, FieldTypeRegistry.CreateDefault(),
            ConditionUsage.ListFilter, userIds);
        result.Error.Should().BeNull();
        return result.Node!;
    }

    private async Task<List<string>> Titles(ConditionNode? filter = null, IReadOnlyList<RecordSort>? sorts = null,
        string? keyword = null, int page = 1, int size = 100)
    {
        var (items, _) = await _sp.GetRequiredService<IRecordRepository>()
            .QueryAsync(new RecordQuery(_m.Id, filter, sorts ?? [], keyword, Today, page, size), default);
        return items.Select(r => r.Title).ToList();
    }

    private async Task<List<string>> Match(string json) => (await Titles(await Parse(json))).Order().ToList();

    private async Task<RecordSort> Sort(FieldResponse f, bool desc) =>
        new((await LoadModule()).Blocks.SelectMany(b => b.Fields).Single(x => x.Id == f.Id), desc);

    [Fact]
    public async Task Query_EachOperatorPerIndexColumn()
    {
        var text = await AddField(FieldType.Text, "文字");
        var num = await AddField(FieldType.Integer, "數字");
        var date = await AddField(FieldType.Date, "日期");
        var user = await AddField(FieldType.User, "負責人");
        await Send(new ActivateModuleRequest("order"));
        await Create("A", (text.Key, "apple"), (num.Key, 10), (date.Key, "2026-11-01"), (user.Key, "admin"));
        await Create("B", (text.Key, "banana"), (num.Key, 20), (date.Key, "2026-11-03"));
        await Create("C", (text.Key, "cherry"), (num.Key, 30), (date.Key, "2026-11-05"));
        await Create("D");

        string Cond(FieldResponse f, string op, string? value = null) =>
            value is null ? $$"""{"field":{{f.Id}},"op":"{{op}}"}""" : $$"""{"field":{{f.Id}},"op":"{{op}}","value":{{value}}}""";

        // Text
        (await Match(Cond(text, "eq", "\"apple\""))).Should().Equal("A");
        (await Match(Cond(text, "ne", "\"apple\""))).Should().Equal("B", "C", "D");
        (await Match(Cond(text, "in", "[\"apple\",\"cherry\"]"))).Should().Equal("A", "C");
        (await Match(Cond(text, "notIn", "[\"apple\",\"cherry\"]"))).Should().Equal("B", "D");
        (await Match(Cond(text, "contains", "\"AN\""))).Should().Equal("B");
        (await Match(Cond(text, "startsWith", "\"ch\""))).Should().Equal("C");
        (await Match(Cond(text, "isEmpty"))).Should().Equal("D");
        (await Match(Cond(text, "isNotEmpty"))).Should().Equal("A", "B", "C");
        // Number
        (await Match(Cond(num, "eq", "20"))).Should().Equal("B");
        (await Match(Cond(num, "ne", "20"))).Should().Equal("A", "C", "D");
        (await Match(Cond(num, "gt", "20"))).Should().Equal("C");
        (await Match(Cond(num, "gte", "20"))).Should().Equal("B", "C");
        (await Match(Cond(num, "lt", "20"))).Should().Equal("A");
        (await Match(Cond(num, "lte", "20"))).Should().Equal("A", "B");
        (await Match(Cond(num, "between", "[10,20]"))).Should().Equal("A", "B");
        (await Match(Cond(num, "isEmpty"))).Should().Equal("D");
        // Date
        (await Match(Cond(date, "eq", "\"2026-11-03\""))).Should().Equal("B");
        (await Match(Cond(date, "ne", "\"2026-11-03\""))).Should().Equal("A", "C", "D");
        (await Match(Cond(date, "gt", "\"2026-11-03\""))).Should().Equal("C");
        (await Match(Cond(date, "gte", "\"2026-11-03\""))).Should().Equal("B", "C");
        (await Match(Cond(date, "lt", "\"2026-11-03\""))).Should().Equal("A");
        (await Match(Cond(date, "lte", "\"2026-11-03\""))).Should().Equal("A", "B");
        (await Match(Cond(date, "between", "[\"2026-11-01\",\"2026-11-03\"]"))).Should().Equal("A", "B");
        (await Match(Cond(date, "isNotEmpty"))).Should().Equal("A", "B", "C");
        // RefKey
        (await Match(Cond(user, "eq", "\"admin\""))).Should().Equal("A");
        (await Match(Cond(user, "ne", "\"admin\""))).Should().Equal("B", "C", "D");
        (await Match(Cond(user, "in", "[\"admin\"]"))).Should().Equal("A");
        (await Match(Cond(user, "notIn", "[\"admin\"]"))).Should().Equal("B", "C", "D");
        (await Match(Cond(user, "isEmpty"))).Should().Equal("B", "C", "D");
        (await Match(Cond(user, "isNotEmpty"))).Should().Equal("A");
    }

    [Fact]
    public async Task Query_NeAndNotIn_IncludeEmpty()
    {
        var status = await AddField(FieldType.Enum, "狀態");
        var open = await AddOption(status, "開啟");
        var cancel = await AddOption(status, "取消");
        await Send(new ActivateModuleRequest("order"));
        await Create("A", (status.Key, open.Value));
        await Create("B", (status.Key, cancel.Value));
        await Create("C");

        (await Match($$"""{"field":{{status.Id}},"op":"ne","value":"{{cancel.Value}}"}""")).Should().Equal("A", "C");
        (await Match($$"""{"field":{{status.Id}},"op":"notIn","value":["{{cancel.Value}}"]}""")).Should().Equal("A", "C");
        (await Match($$"""{"field":{{status.Id}},"op":"eq","value":"{{cancel.Value}}"}""")).Should().Equal("B");
    }

    [Fact]
    public async Task Query_MultiEnumInAndNotIn()
    {
        var tags = await AddField(FieldType.MultiEnum, "標籤");
        var a = await AddOption(tags, "A");
        var b = await AddOption(tags, "B");
        var c = await AddOption(tags, "C");
        await Send(new ActivateModuleRequest("order"));
        await Create("R1", (tags.Key, new[] { a.Value, b.Value }));
        await Create("R2", (tags.Key, new[] { b.Value }));
        await Create("R3", (tags.Key, new[] { c.Value }));
        await Create("R4");

        (await Match($$"""{"field":{{tags.Id}},"op":"in","value":["{{a.Value}}","{{c.Value}}"]}""")).Should().Equal("R1", "R3");
        (await Match($$"""{"field":{{tags.Id}},"op":"notIn","value":["{{a.Value}}","{{c.Value}}"]}""")).Should().Equal("R2", "R4");
        (await Match($$"""{"field":{{tags.Id}},"op":"isEmpty"}""")).Should().Equal("R4");
    }

    [Fact]
    public async Task Query_NestedAllAny()
    {
        var num = await AddField(FieldType.Integer, "數字");
        var text = await AddField(FieldType.Text, "文字");
        await Send(new ActivateModuleRequest("order"));
        await Create("A", (num.Key, 5), (text.Key, "x"));
        await Create("B", (num.Key, 50), (text.Key, "y"));
        await Create("C", (num.Key, 500), (text.Key, "x"));
        await Create("D", (num.Key, 500), (text.Key, "z"));

        var json = $$"""
            {"all":[{"field":{{text.Id}},"op":"in","value":["x","y"]},
                    {"any":[{"field":{{num.Id}},"op":"lt","value":10},{"field":{{num.Id}},"op":"gt","value":100}]}]}
            """;

        (await Match(json)).Should().Equal("A", "C");
    }

    [Fact]
    public async Task Query_WithinNextDaysUsesToday()
    {
        var date = await AddField(FieldType.Date, "日期");
        var at = await AddField(FieldType.DateTime, "時間");
        await Send(new ActivateModuleRequest("order"));
        await Create("past", (date.Key, "2026-11-02"), (at.Key, "2026-11-02T23:59:00+08:00"));
        await Create("today", (date.Key, "2026-11-03"), (at.Key, "2026-11-03T23:30:00+08:00"));
        await Create("plus7", (date.Key, "2026-11-10"), (at.Key, "2026-11-10T23:59:00+08:00"));
        await Create("plus8", (date.Key, "2026-11-11"), (at.Key, "2026-11-11T00:00:00+08:00"));
        await Create("minus3", (date.Key, "2026-10-31"), (at.Key, "2026-10-31T00:00:00+08:00"));
        await Create("minus4", (date.Key, "2026-10-30"), (at.Key, "2026-10-30T23:59:00+08:00"));

        (await Match($$"""{"field":{{date.Id}},"op":"withinNextDays","value":7}""")).Should().Equal("plus7", "today");
        (await Match($$"""{"field":{{date.Id}},"op":"withinLastDays","value":3}""")).Should().Equal("minus3", "past", "today");
        (await Match($$"""{"field":{{at.Id}},"op":"withinNextDays","value":7}""")).Should().Equal("plus7", "today");
        (await Match($$"""{"field":{{at.Id}},"op":"withinLastDays","value":3}""")).Should().Equal("minus3", "past", "today");
        (await Match($$"""{"field":{{at.Id}},"op":"withinNextDays","value":0}""")).Should().Equal("today");
    }

    [Fact]
    public async Task Query_BeforeTodayIsStrictlyBeforeToday()
    {
        var date = await AddField(FieldType.Date, "日期");
        var at = await AddField(FieldType.DateTime, "時間");
        await Send(new ActivateModuleRequest("order"));
        await Create("past", (date.Key, "2026-11-02"), (at.Key, "2026-11-02T23:59:00+08:00"));
        await Create("today", (date.Key, "2026-11-03"), (at.Key, "2026-11-03T00:00:00+08:00"));
        await Create("future", (date.Key, "2026-11-04"), (at.Key, "2026-11-04T08:00:00+08:00"));
        await Create("old", (date.Key, "2025-01-01"), (at.Key, "2025-01-01T00:00:00+08:00"));
        await Create("empty");

        (await Match($$"""{"field":{{date.Id}},"op":"beforeToday"}""")).Should().Equal("old", "past");
        (await Match($$"""{"field":{{at.Id}},"op":"beforeToday"}""")).Should().Equal("old", "past");
    }

    [Fact]
    public async Task Query_SortWithTiesAndNulls_IsStableAcrossPages()
    {
        var num = await AddField(FieldType.Integer, "數字", sortable: true);
        await Send(new ActivateModuleRequest("order"));
        for (var i = 0; i < 25; i++)
            await Create($"R{i:00}", i % 5 == 4 ? (num.Key, null) : (num.Key, i % 3));
        var sorts = new[] { await Sort(num, desc: false) };

        var all = new List<long>();
        var nullTitles = new List<string>();
        foreach (var page in new[] { 1, 2, 3 })
        {
            var (items, total) = await _sp.GetRequiredService<IRecordRepository>()
                .QueryAsync(new RecordQuery(_m.Id, null, sorts, null, Today, page, 10), default);
            total.Should().Be(25);
            all.AddRange(items.Select(r => r.Id));
        }

        all.Should().OnlyHaveUniqueItems().And.HaveCount(25);
        var byId = _db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().AsNoTracking().ToDictionary(r => r.Id, r => r.Title);
        var order = all.Select(id => int.Parse(byId[id][1..])).ToList();
        var lastNonNull = order.FindLastIndex(i => i % 5 != 4);
        order.Skip(lastNonNull + 1).Should().OnlyContain(i => i % 5 == 4).And.HaveCount(5);
        // 非空值依升冪，同值依 Id DESC
        order.Take(20).Select(i => i % 3).Should().BeInAscendingOrder();

        var desc = await Titles(sorts: [await Sort(num, desc: true)]);
        desc.TakeLast(5).Should().OnlyContain(t => int.Parse(t.Substring(1)) % 5 == 4);
    }

    [Fact]
    public async Task Query_EnumSortsByOptionSequence()
    {
        var status = await AddField(FieldType.Enum, "狀態", sortable: true);
        var first = await AddOption(status, "先");
        var second = await AddOption(status, "後");
        var third = await AddOption(status, "末");
        await Send(new ActivateModuleRequest("order"));
        // 建立順序與 Sequence 無關；標籤與 id 順序也與 Sequence 不同
        await Create("third", (status.Key, third.Value));
        await Create("none");
        await Create("first", (status.Key, first.Value));
        await Create("second", (status.Key, second.Value));
        var db = _db.Context;
        // 把 first 與 third 的 Sequence 對調
        var opts = await db.Set<FieldOption>().Where(o => o.FieldId == status.Id).ToListAsync();
        (opts.Single(o => o.Id == first.Id).Sequence, opts.Single(o => o.Id == third.Id).Sequence) =
            (opts.Single(o => o.Id == third.Id).Sequence, opts.Single(o => o.Id == first.Id).Sequence);
        await db.SaveChangesAsync();

        (await Titles(sorts: [await Sort(status, false)])).Should().Equal("third", "second", "first", "none");
        (await Titles(sorts: [await Sort(status, true)])).Should().Equal("first", "second", "third", "none");
    }

    [Fact]
    public async Task Query_MultiColumnSort()
    {
        var text = await AddField(FieldType.Text, "分類", sortable: true);
        var num = await AddField(FieldType.Integer, "數字", sortable: true);
        await Send(new ActivateModuleRequest("order"));
        await Create("a1", (text.Key, "a"), (num.Key, 1));
        await Create("a2", (text.Key, "a"), (num.Key, 2));
        await Create("b1", (text.Key, "b"), (num.Key, 1));
        await Create("b2", (text.Key, "b"), (num.Key, 2));

        var titles = await Titles(sorts: [await Sort(text, true), await Sort(num, false)]);

        titles.Should().Equal("b1", "b2", "a1", "a2");
    }

    [Fact]
    public async Task Query_ContainsWithWildcardsAndQuotes_MatchesLiterally()
    {
        var text = await AddField(FieldType.Text, "文字");
        await Send(new ActivateModuleRequest("order"));
        await Create("pct", (text.Key, "50% off"));
        await Create("under", (text.Key, "a_b"));
        await Create("quote", (text.Key, "it's"));
        await Create("slash", (text.Key, @"c:\dir"));
        await Create("plain", (text.Key, "50 xoff axb"));

        async Task<List<string>> Contains(string value) =>
            (await Match($$"""{"field":{{text.Id}},"op":"contains","value":{{JsonSerializer.Serialize(value)}}}""")).ToList();

        (await Contains("%")).Should().Equal("pct");
        (await Contains("_")).Should().Equal("under");
        (await Contains("'")).Should().Equal("quote");
        (await Contains(@"\")).Should().Equal("slash");
        (await Contains(@"c:\d")).Should().Equal("slash");
    }

    [Fact]
    public async Task Query_KeywordMatchesTitle()
    {
        await Send(new ActivateModuleRequest("order"));
        await Create("Alpha 100%");
        await Create("Beta");
        await Create("Alpha_x");

        (await Titles(keyword: "alpha")).Should().BeEquivalentTo("Alpha 100%", "Alpha_x");
        (await Titles(keyword: "%")).Should().Equal("Alpha 100%");
        (await Titles(keyword: "_")).Should().Equal("Alpha_x");
    }

    [Fact]
    public async Task Query_ExcludesDeleted()
    {
        await Send(new ActivateModuleRequest("order"));
        var keep = await Create("keep");
        var gone = await Create("gone");
        _db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().Single(r => r.Id == gone).DeleteFlag = true;
        await _db.Context.SaveChangesAsync();

        var (items, total) = await _sp.GetRequiredService<IRecordRepository>()
            .QueryAsync(new RecordQuery(_m.Id, null, [], null, Today, 1, 10), default);

        total.Should().Be(1);
        items.Select(r => r.Id).Should().Equal(keep);
    }

    [Fact]
    public async Task Query_DateParameterMatchesEfStoredFormat()
    {
        var date = await AddField(FieldType.Date, "日期");
        await Send(new ActivateModuleRequest("order"));
        await Create("A", (date.Key, "2026-11-03"));

        var stored = await new Microsoft.Data.Sqlite.SqliteCommand(
            "SELECT DateValue FROM Dynamic_RecordIndex", _db.Connection).ExecuteScalarAsync();

        stored.Should().Be("2026-11-03 00:00:00");
        (await Match($$"""{"field":{{date.Id}},"op":"eq","value":"2026-11-03"}""")).Should().Equal("A");
    }
}
