using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using DynamicModules.Application.Dynamic.ReportService;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests;

public class ReportsApiTests
{
    private const string Reports = Base + "/reports";

    /// <summary>主模組：標題、金額（小數）、狀態（列舉，兩個選項）、交期（日期）；可選的子模組是指回主模組的一般參照。</summary>
    private sealed record World(
        string Main, string TitleKey, long Amount, string AmountKey, long Status, string StatusKey, string[] Options,
        long Due, string? Child = null, long ChildLink = 0, long ChildQty = 0, string ChildQtyKey = "", string ChildLinkKey = "");

    private static Task<long> AddOption(HttpClient c, long fieldId, string label) =>
        AddOptionCore(c, fieldId, label);

    private static async Task<long> AddOptionCore(HttpClient c, long fieldId, string label)
    {
        var res = await c.PostAsJsonAsync($"{Base}/fields/{fieldId}/options", new { label = new Dictionary<string, string> { ["zh-TW"] = label } });
        res.EnsureSuccessStatusCode();
        return (await JsonOf(res)).GetProperty("id").GetInt64();
    }

    private static async Task<World> MakeWorld(TestApp app, bool withChild = false)
    {
        var admin = app.AsUser();
        var main = await CreateModule(admin);
        var amount = await AddField(admin, main, "decimal", new { scale = 0 });
        var status = await AddField(admin, main, "enum", new { });
        var o1 = await AddOption(admin, status.Id, "新單");
        var o2 = await AddOption(admin, status.Id, "結案");
        var due = await AddField(admin, main, "date", new { });
        string? child = null;
        (long Id, string Key, long BlockId) link = (0, "", 0), qty = (0, "", 0);
        if (withChild)
        {
            child = await CreateModule(admin);
            link = await AddField(admin, child, "reference", new { refTarget = new { kind = "module", key = main } });
            qty = await AddField(admin, child, "decimal", new { scale = 0 });
            await Activate(admin, child);
            app.GrantBusinessGroups(child);
        }
        await Activate(admin, main);
        app.GrantBusinessGroups(main);
        return new World(main, await TitleKey(admin, main), amount.Id, amount.Key, status.Id, status.Key,
            [$"opt_{o1}", $"opt_{o2}"], due.Id, child, link.Id, qty.Id, qty.Key, link.Key);
    }

    private static async Task<long> AddRecord(HttpClient c, World w, decimal amount, string? status = null)
    {
        var values = new Dictionary<string, object> { [w.TitleKey] = "t", [w.AmountKey] = amount };
        if (status is not null) values[w.StatusKey] = status;
        var res = await c.PostAsJsonAsync($"{Base}/modules/{w.Main}/records", new { values });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return (await JsonOf(res)).GetProperty("id").GetInt64();
    }

    private static object Ref(long field, string source = "main", long? via = null) => new { source, fieldId = field, viaFieldId = via };

    private static object Count(string id = "n") => new { id, kind = "countMain", field = (object?)null, condition = (object?)null };

    private static object Sum(string id, long field) => new { id, kind = "sum", field = Ref(field), condition = (object?)null };

    private static Dictionary<string, object?> Def(
        string main, object[]? columns = null, object[]? groups = null, object[]? measures = null, object[]? filters = null,
        object? primaryDate = null, object? dateRange = null, object[]? sort = null, object? child = null) => new()
    {
        ["version"] = 1, ["mainModuleKey"] = main, ["child"] = child, ["columns"] = columns ?? [], ["groups"] = groups ?? [],
        ["measures"] = measures ?? [], ["filters"] = filters ?? [], ["primaryDateField"] = primaryDate, ["dateRange"] = dateRange,
        ["sort"] = sort ?? [], ["viz"] = "table"
    };

    /// <summary>依狀態分組，筆數與金額加總。</summary>
    private static Dictionary<string, object?> ByStatus(World w) =>
        Def(w.Main, groups: [new { field = Ref(w.Status), bucket = (string?)null }], measures: [Count(), Sum("s", w.Amount)]);

    private static async Task<long> Create(HttpClient c, object definition, string name = "月報")
    {
        var res = await c.PostAsJsonAsync(Reports, new { name, definition });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return (await JsonOf(res)).GetProperty("id").GetInt64();
    }

    private static long GroupId(TestApp app, string key)
    {
        using var scope = app.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<PermissionGroup>().Single(g => g.Key == key).Id;
    }

    private static Task<HttpResponseMessage> Share(HttpClient c, long id, params long[] groupIds) =>
        c.PutAsJsonAsync($"{Reports}/{id}/shares", new { groupIds });

    /// <summary>整組換掉模組授權；每個項目是 (群組鍵, 匯出, 範圍)。</summary>
    private static async Task Grant(TestApp app, string module, object[]? fields = null, params (string Group, bool Print, string Scope)[] groups)
    {
        var res = await app.AsUser().PutAsJsonAsync($"{Base}/modules/{module}/permissions", new
        {
            groups = groups.Select(g => new
            {
                groupId = GroupId(app, g.Group),
                privileges = new { read = true, add = true, edit = true, delete = true, printExport = g.Print, pricing = true, approve = false },
                scope = g.Scope
            }).ToArray(),
            users = Array.Empty<object>(), fields = fields ?? [], options = Array.Empty<object>()
        });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> Run(HttpClient c, long id, object? range = null)
    {
        var res = await c.PostAsJsonAsync($"{Reports}/{id}/run", new { range });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return await JsonOf(res);
    }

    private static decimal TotalOf(JsonElement run, string id) => run.GetProperty("total").GetProperty(id).GetDecimal();

    // ---- 分享與唯讀 ----

    [Fact]
    public async Task Share_OtherGroupMember_CanRunReadOnly_CannotEdit()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var admin = app.AsUser();
        await AddRecord(admin, w, 100, w.Options[0]);
        var id = await Create(admin, ByStatus(w));
        (await Share(admin, id, GroupId(app, "sales"))).StatusCode.Should().Be(HttpStatusCode.OK);
        var sales = app.AsUser("sales01");

        var list = await JsonOf(await sales.GetAsync(Reports));
        var item = list.EnumerateArray().Single(r => r.GetProperty("id").GetInt64() == id);
        item.GetProperty("isOwner").GetBoolean().Should().BeFalse();
        item.GetProperty("ownerName").GetString().Should().NotBeNullOrEmpty();
        item.GetProperty("mainModuleKey").GetString().Should().Be(w.Main);
        item.GetProperty("sharedGroupIds").EnumerateArray().Select(x => x.GetInt64()).Should().Equal(GroupId(app, "sales"));
        var detail = await sales.GetAsync($"{Reports}/{id}");
        detail.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(detail)).GetProperty("definition").GetProperty("mainModuleKey").GetString().Should().Be(w.Main);
        (await Run(sales, id)).GetProperty("state").GetString().Should().Be("ok");

        (await sales.PutAsJsonAsync($"{Reports}/{id}", new { name = "改", definition = ByStatus(w) })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await sales.PutAsJsonAsync($"{Reports}/{id}/shares", new { groupIds = Array.Empty<long>() })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task NotShared_Returns404()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var id = await Create(app.AsUser(), ByStatus(w));
        var sales = app.AsUser("sales01");

        (await JsonOf(await sales.GetAsync(Reports))).GetArrayLength().Should().Be(0);
        (await sales.GetAsync($"{Reports}/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await sales.PutAsJsonAsync($"{Reports}/{id}", new { name = "x", definition = ByStatus(w) })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await sales.DeleteAsync($"{Reports}/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await sales.PostAsync($"{Reports}/{id}/copy", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await sales.PutAsJsonAsync($"{Reports}/{id}/shares", new { groupIds = Array.Empty<long>() })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await sales.PostAsJsonAsync($"{Reports}/{id}/run", new { })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await sales.PostAsJsonAsync($"{Reports}/{id}/drill", new { groupKeys = new[] { "x" } })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await sales.GetAsync($"{Reports}/{id}/export")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await sales.GetAsync($"{Reports}/99999")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Unauthenticated_401()
    {
        using var app = new TestApp();
        var anon = app.AsUser(null);

        (await anon.GetAsync(Reports)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anon.PostAsJsonAsync($"{Reports}/preview", new { })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anon.GetAsync($"{Reports}/1/export")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Copy_NewOwner_NoShares()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var admin = app.AsUser();
        var id = await Create(admin, ByStatus(w));
        await Share(admin, id, GroupId(app, "sales"));
        var sales = app.AsUser("sales01");

        var res = await sales.PostAsync($"{Reports}/{id}/copy", null);

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var copy = await JsonOf(res);
        copy.GetProperty("id").GetInt64().Should().NotBe(id);
        copy.GetProperty("isOwner").GetBoolean().Should().BeTrue();
        copy.GetProperty("name").GetString().Should().Be("月報");
        copy.GetProperty("sharedGroupIds").GetArrayLength().Should().Be(0);
        var original = await JsonOf(await admin.GetAsync($"{Reports}/{id}"));
        original.GetProperty("sharedGroupIds").GetArrayLength().Should().Be(1);
        original.GetProperty("isOwner").GetBoolean().Should().BeTrue();
        // 另存的那份可以改
        (await sales.PutAsJsonAsync($"{Reports}/{copy.GetProperty("id").GetInt64()}", new { name = "我的", definition = ByStatus(w) }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SharedReport_ViewerHasLessPermission_SameDefinition_DifferentNumbers()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        await Grant(app, w.Main, null, ("mis", true, "all"), ("sales", true, "own"));
        var admin = app.AsUser();
        var sales = app.AsUser("sales01");
        await AddRecord(admin, w, 100, w.Options[0]);
        await AddRecord(admin, w, 200, w.Options[0]);
        await AddRecord(sales, w, 5, w.Options[0]);
        var id = await Create(admin, ByStatus(w));
        await Share(admin, id, GroupId(app, "sales"));

        var mine = await Run(admin, id);
        var theirs = await Run(sales, id);

        TotalOf(mine, "s").Should().Be(305);
        TotalOf(mine, "n").Should().Be(3);
        TotalOf(theirs, "s").Should().Be(5);
        TotalOf(theirs, "n").Should().Be(1);
    }

    [Fact]
    public async Task SharedReport_ViewerLacksField_RestrictedState_NotError()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var sales = GroupId(app, "sales");
        await Grant(app, w.Main, [new { fieldId = w.Amount, groupId = sales, access = "hidden" }], ("mis", true, "all"), ("sales", true, "all"));
        var admin = app.AsUser();
        await AddRecord(admin, w, 100, w.Options[0]);
        var id = await Create(admin, ByStatus(w));
        await Share(admin, id, sales);

        var run = await Run(app.AsUser("sales01"), id);

        run.GetProperty("state").GetString().Should().Be("restricted");
        run.TryGetProperty("total", out _).Should().BeFalse();
        run.TryGetProperty("groups", out _).Should().BeFalse();
        (await Run(admin, id)).GetProperty("state").GetString().Should().Be("ok");
    }

    [Fact]
    public async Task SharedReport_ViewerLacksModuleRead_RestrictedState_SharingGivesNoDataAccess()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        // 只有 MIS 與業務有此模組權限，現場沒有
        await Grant(app, w.Main, null, ("mis", true, "all"), ("sales", true, "all"));
        var admin = app.AsUser();
        await AddRecord(admin, w, 100, w.Options[0]);
        var id = await Create(admin, ByStatus(w));
        await Share(admin, id, GroupId(app, "floor"));
        var floor = app.AsUser("floor01");

        (await floor.GetAsync($"{Reports}/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        var run = await Run(floor, id);
        run.GetProperty("state").GetString().Should().Be("restricted");
        run.TryGetProperty("total", out _).Should().BeFalse();
        var drill = await floor.PostAsJsonAsync($"{Reports}/{id}/drill", new { groupKeys = new[] { w.Options[0] } });
        drill.StatusCode.Should().Be(HttpStatusCode.OK);
        var drilled = await JsonOf(drill);
        drilled.GetProperty("state").GetString().Should().Be("restricted");
        drilled.TryGetProperty("detailRows", out _).Should().BeFalse();
    }

    // ---- 定義驗證（存檔與預覽都做）----

    [Fact]
    public async Task Save_WithRollupField_400()
    {
        using var app = new TestApp();
        var admin = app.AsUser();
        var main = await CreateModule(admin);
        var line = await CreateModule(admin);
        var parentRef = await AddField(admin, line, "reference", new { refTarget = new { kind = "module", key = main }, relation = "detail" });
        var qty = await AddField(admin, line, "decimal", new { scale = 0 });
        var rollup = await AddField(admin, main, "rollup", new { rollup = new { sourceFieldId = parentRef.Id, function = "sum", aggregateFieldId = qty.Id } });
        await Activate(admin, main);
        await Activate(admin, line);
        app.GrantBusinessGroups(main);
        var def = Def(main, columns: [Ref(rollup.Id)]);

        (await admin.PostAsJsonAsync(Reports, new { name = "x", definition = def })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync($"{Reports}/preview", def)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // 加總欄位不能是彙總，其他欄位照常能存
        (await admin.PostAsJsonAsync(Reports, new { name = "y", definition = Def(main, measures: [Count()]) })).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Save_FieldViewerCannotSee_400()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        await Grant(app, w.Main, [new { fieldId = w.Amount, groupId = GroupId(app, "sales"), access = "hidden" }], ("mis", true, "all"), ("sales", true, "all"));
        var sales = app.AsUser("sales01");

        var res = await sales.PostAsJsonAsync(Reports, new { name = "x", definition = Def(w.Main, measures: [Sum("s", w.Amount)]) });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await sales.PostAsJsonAsync($"{Reports}/preview", Def(w.Main, measures: [Sum("s", w.Amount)]))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await sales.PostAsJsonAsync(Reports, new { name = "x", definition = Def(w.Main, measures: [Count()]) })).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Save_ChainedVia_400()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app, withChild: true);
        var admin = app.AsUser();
        // 子模組的參照欄位不是主模組的欄位，不能當主模組欄位的 via（等於再往下鏈一層）
        var chained = Def(w.Main, columns: [Ref(w.ChildQty, via: w.ChildLink)]);

        (await admin.PostAsJsonAsync(Reports, new { name = "x", definition = chained })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync($"{Reports}/preview", chained)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // 主模組的非參照欄位不能當 via
        (await admin.PostAsJsonAsync(Reports, new { name = "x", definition = Def(w.Main, columns: [Ref(w.Amount, via: w.Status)]) }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Save_DetailAsMain_400()
    {
        using var app = new TestApp();
        var admin = app.AsUser();
        var main = await CreateModule(admin);
        var line = await CreateModule(admin);
        await AddField(admin, line, "reference", new { refTarget = new { kind = "module", key = main }, relation = "detail" });
        var qty = await AddField(admin, line, "decimal", new { scale = 0 });
        await Activate(admin, main);
        await Activate(admin, line);
        app.GrantBusinessGroups(main);
        app.GrantBusinessGroups(line);
        var def = Def(line, columns: [Ref(qty.Id)]);

        (await admin.PostAsJsonAsync(Reports, new { name = "x", definition = def })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync($"{Reports}/preview", def)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Save_ChildLinkNotReferenceToMain_400_ValidLinkAccepted()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app, withChild: true);
        var admin = app.AsUser();

        var bad = Def(w.Main, columns: [Ref(w.ChildQty, "child")], child: new { moduleKey = w.Child, linkFieldId = w.ChildQty });
        var good = Def(w.Main, columns: [Ref(w.ChildQty, "child")], child: new { moduleKey = w.Child, linkFieldId = w.ChildLink });

        (await admin.PostAsJsonAsync(Reports, new { name = "x", definition = bad })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync(Reports, new { name = "x", definition = good })).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    public static TheoryData<string> InvalidCases => new()
    {
        "name-empty", "name-too-long", "version", "missing-lists", "null-list-item", "duplicate-measure-id", "sum-on-date", "avg-on-date",
        "sum-on-enum", "range-n-zero", "range-n-negative", "range-unknown-kind", "op-not-allowed", "op-unknown", "value-wrong-type",
        "value-missing", "in-empty", "between-one", "bad-enum-option", "bad-date", "two-buckets", "bucket-on-number", "nothing-selected",
        "range-without-primary", "unknown-module", "unknown-field", "child-source-without-child", "bad-viz", "ratio-without-condition"
    };

    private static (string? Name, Dictionary<string, object?> Def) InvalidCase(string name, World w)
    {
        var d = Def(w.Main, columns: [Ref(w.Amount)]);
        string? n = "x";
        object Filter(string op, object? value) => new { field = Ref(w.Amount), op, value };
        switch (name)
        {
            case "name-empty": n = "  "; break;
            case "name-too-long": n = new string('名', 101); break;
            case "version": d["version"] = 2; break;
            case "missing-lists": d.Remove("columns"); d.Remove("groups"); d.Remove("measures"); d.Remove("filters"); d.Remove("sort"); break;
            case "null-list-item": d["columns"] = new object?[] { null }; break;
            case "duplicate-measure-id": d["measures"] = new[] { Count("m"), Sum("m", w.Amount) }; break;
            case "sum-on-date": d["measures"] = new[] { Sum("s", w.Due) }; break;
            case "avg-on-date": d["measures"] = new object[] { new { id = "a", kind = "avg", field = Ref(w.Due), condition = (object?)null } }; break;
            case "sum-on-enum": d["measures"] = new[] { Sum("s", w.Status) }; break;
            case "range-n-zero": d["primaryDateField"] = Ref(w.Due); d["dateRange"] = new { kind = "lastDays", n = 0 }; break;
            case "range-n-negative": d["primaryDateField"] = Ref(w.Due); d["dateRange"] = new { kind = "lastMonths", n = -3 }; break;
            case "range-without-primary": d["dateRange"] = new { kind = "today" }; break;
            case "range-unknown-kind": d["primaryDateField"] = Ref(w.Due); d["dateRange"] = new { kind = "someday" }; break;
            case "op-not-allowed": d["filters"] = new[] { Filter("contains", "1") }; break;
            case "op-unknown": d["filters"] = new[] { Filter("roughly", 1) }; break;
            case "value-wrong-type": d["filters"] = new[] { Filter("gt", "abc") }; break;
            case "value-missing": d["filters"] = new[] { Filter("gt", null) }; break;
            case "in-empty": d["filters"] = new[] { Filter("in", Array.Empty<int>()) }; break;
            case "between-one": d["filters"] = new[] { Filter("between", new[] { 1 }) }; break;
            case "bad-enum-option": d["filters"] = new object[] { new { field = Ref(w.Status), op = "eq", value = "opt_999999" } }; break;
            case "bad-date": d["filters"] = new object[] { new { field = Ref(w.Due), op = "eq", value = "2026/11/05" } }; break;
            case "two-buckets":
                d["groups"] = new object[]
                {
                    new { field = Ref(w.Due), bucket = "month" }, new { field = Ref(w.Due), bucket = "year" }
                };
                break;
            case "bucket-on-number": d["groups"] = new object[] { new { field = Ref(w.Amount), bucket = "month" } }; break;
            case "nothing-selected": d["columns"] = Array.Empty<object>(); break;
            case "unknown-module": d["mainModuleKey"] = "no-such-module"; break;
            case "unknown-field": d["columns"] = new[] { Ref(99999999) }; break;
            case "child-source-without-child": d["columns"] = new[] { Ref(w.Amount, "child") }; break;
            case "bad-viz": d["viz"] = "radar"; break;
            case "ratio-without-condition":
                d["measures"] = new object[] { new { id = "r", kind = "ratio", field = (object?)null, condition = (object?)null } };
                break;
            default: throw new ArgumentException(name);
        }
        return (n, d);
    }

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public async Task InvalidDefinition_400_OnSave_Update_AndPreview(string name)
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var admin = app.AsUser();
        var existing = await Create(admin, Def(w.Main, columns: [Ref(w.Amount)]));
        var (reportName, def) = InvalidCase(name, w);

        var save = await admin.PostAsJsonAsync(Reports, new { name = reportName, definition = def });
        var update = await admin.PutAsJsonAsync($"{Reports}/{existing}", new { name = reportName, definition = def });
        var preview = await admin.PostAsJsonAsync($"{Reports}/preview", def);

        save.StatusCode.Should().Be(HttpStatusCode.BadRequest, name + " save " + await save.Content.ReadAsStringAsync());
        update.StatusCode.Should().Be(HttpStatusCode.BadRequest, name + " update");
        // 預覽不看名稱；只有名稱錯的案例預覽照常可跑
        if (!name.StartsWith("name-"))
            preview.StatusCode.Should().Be(HttpStatusCode.BadRequest, name + " preview " + await preview.Content.ReadAsStringAsync());
        (await JsonOf(await admin.GetAsync($"{Reports}/{existing}"))).GetProperty("name").GetString().Should().Be("月報");
    }

    [Fact]
    public async Task RunWithBadRangeOverride_400()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var admin = app.AsUser();
        var id = await Create(admin, ByStatus(w));

        (await admin.PostAsJsonAsync($"{Reports}/{id}/run", new { range = new { kind = "lastDays", n = 0 } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync($"{Reports}/{id}/run", new { range = new { kind = "nope" } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync($"{Reports}/{id}/drill", new { groupKeys = new[] { "x" }, range = new { kind = "lastDays", n = 0 } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync($"{Reports}/{id}/run", new { range = new { kind = "lastDays", n = 7 } })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Save_ConditionValues_AreNormalized_AndUntimedDateTimeIsTaipeiWallClock()
    {
        using var app = new TestApp();
        var admin = app.AsUser();
        var main = await CreateModule(admin);
        var status = await AddField(admin, main, "enum", new { });
        var opt = await AddOption(admin, status.Id, "新單");
        var at = await AddField(admin, main, "datetime", new { });
        await Activate(admin, main);
        app.GrantBusinessGroups(main);
        var def = Def(main, columns: [Ref(status.Id)], filters:
        [
            new { field = Ref(status.Id), op = "eq", value = $"opt_{opt:00}" },
            new { field = Ref(at.Id), op = "gte", value = "2026-11-05T09:30" },
            new { field = Ref(at.Id), op = "lt", value = "2026-11-06" },
            new { field = Ref(at.Id), op = "gt", value = "2026-11-05T00:00:00Z" }
        ]);

        var id = await Create(admin, def);

        var stored = (await JsonOf(await admin.GetAsync($"{Reports}/{id}"))).GetProperty("definition").GetProperty("filters");
        stored[0].GetProperty("value").GetString().Should().Be($"opt_{opt}");
        stored[1].GetProperty("value").GetString().Should().Be("2026-11-05T09:30:00+08:00");
        stored[2].GetProperty("value").GetString().Should().Be("2026-11-06");
        stored[3].GetProperty("value").GetString().Should().Be("2026-11-05T08:00:00+08:00");
        (await Run(admin, id)).GetProperty("state").GetString().Should().BeOneOf("ok", "empty");
    }

    [Fact]
    public async Task Preview_RunsUnsavedDefinition_UsingViewerPermissions()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        await Grant(app, w.Main, null, ("mis", true, "all"), ("sales", true, "own"));
        var admin = app.AsUser();
        await AddRecord(admin, w, 100, w.Options[0]);
        await AddRecord(app.AsUser("sales01"), w, 7, w.Options[0]);

        var res = await app.AsUser("sales01").PostAsJsonAsync($"{Reports}/preview", ByStatus(w));

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        TotalOf(await JsonOf(res), "s").Should().Be(7);
        (await JsonOf(await admin.GetAsync(Reports))).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Run_EmptyAndDedupedNote_ReturnedAsIs()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app, withChild: true);
        var admin = app.AsUser();
        var def = Def(w.Main, groups: [new { field = Ref(w.Status), bucket = (string?)null }],
            measures: [Sum("s", w.Amount)], child: new { moduleKey = w.Child, linkFieldId = w.ChildLink });
        var id = await Create(admin, def);

        var empty = await Run(admin, id);
        empty.GetProperty("state").GetString().Should().Be("empty");
        empty.GetProperty("dedupedNote").GetBoolean().Should().BeTrue();
    }

    // ---- 分享管理 ----

    [Fact]
    public async Task SetShares_MissingGroupIds_400()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var admin = app.AsUser();
        var id = await Create(admin, ByStatus(w));
        await Share(admin, id, GroupId(app, "sales"));

        (await admin.PutAsJsonAsync($"{Reports}/{id}/shares", new { })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PutAsJsonAsync($"{Reports}/{id}/shares", new { groupIds = new long[] { 99999 } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await JsonOf(await admin.GetAsync($"{Reports}/{id}"))).GetProperty("sharedGroupIds").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task SetShares_ReplacesAndAllowsEmpty_AndRevokesAccess()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var admin = app.AsUser();
        var id = await Create(admin, ByStatus(w));
        await Share(admin, id, GroupId(app, "sales"), GroupId(app, "floor"));
        await Share(admin, id, GroupId(app, "floor"), GroupId(app, "floor"));

        (await JsonOf(await admin.GetAsync($"{Reports}/{id}"))).GetProperty("sharedGroupIds").EnumerateArray().Select(x => x.GetInt64())
            .Should().Equal(GroupId(app, "floor"));
        (await app.AsUser("sales01").GetAsync($"{Reports}/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Share(admin, id)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await app.AsUser("floor01").GetAsync($"{Reports}/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_NonOwner_403()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var admin = app.AsUser();
        var id = await Create(admin, ByStatus(w));
        await Share(admin, id, GroupId(app, "sales"));

        (await app.AsUser("sales01").DeleteAsync($"{Reports}/{id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await admin.GetAsync($"{Reports}/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.DeleteAsync($"{Reports}/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.GetAsync($"{Reports}/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SystemAdmin_CanSeeAndDeleteSharedReport_ButNotUnsharedOnes()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var sales = app.AsUser("sales01");
        var shared = await Create(sales, ByStatus(w), "共用");
        var priv = await Create(sales, ByStatus(w), "私人");
        await Share(sales, shared, GroupId(app, "sales"));
        var admin = app.AsUser();

        var list = await JsonOf(await admin.GetAsync(Reports));
        list.EnumerateArray().Select(r => r.GetProperty("id").GetInt64()).Should().Contain(shared).And.NotContain(priv);
        list.EnumerateArray().Single(r => r.GetProperty("id").GetInt64() == shared).GetProperty("isOwner").GetBoolean().Should().BeFalse();
        (await admin.GetAsync($"{Reports}/{priv}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.DeleteAsync($"{Reports}/{priv}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        // 管理員看得到但不是擁有者，不能改
        (await admin.PutAsJsonAsync($"{Reports}/{shared}", new { name = "x", definition = ByStatus(w) })).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await admin.DeleteAsync($"{Reports}/{shared}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await sales.GetAsync($"{Reports}/{shared}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Deactivated_OwnerReport_StaysShared()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var sales = app.AsUser("sales01");
        var id = await Create(sales, ByStatus(w));
        await Share(sales, id, GroupId(app, "sales"));
        (await app.AsUser().PostAsync($"{Base}/admin/users/sales01/deactivate", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var other = app.AsUser("sales02");

        var list = await JsonOf(await other.GetAsync(Reports));
        var item = list.EnumerateArray().Single(r => r.GetProperty("id").GetInt64() == id);
        item.GetProperty("ownerName").GetString().Should().Be("業務一");
        (await Run(other, id)).GetProperty("state").GetString().Should().BeOneOf("ok", "empty");
    }

    // ---- 下鑽與匯出 ----

    [Fact]
    public async Task Drill_RowsRespectScope_AndCapAt500()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        await Grant(app, w.Main, null, ("mis", true, "all"), ("sales", true, "own"));
        var admin = app.AsUser();
        var sales = app.AsUser("sales01");
        await AddRecord(sales, w, 1, w.Options[0]);
        await AddRecord(sales, w, 2, w.Options[0]);
        for (var i = 0; i < 501; i++) await AddRecord(admin, w, 3, w.Options[0]);
        var def = Def(w.Main, columns: [Ref(w.Amount)], groups: [new { field = Ref(w.Status), bucket = (string?)null }], measures: [Count()]);
        var id = await Create(admin, def);
        await Share(admin, id, GroupId(app, "sales"));

        var mine = await JsonOf(await sales.PostAsJsonAsync($"{Reports}/{id}/drill", new { groupKeys = new[] { w.Options[0] } }));
        var all = await JsonOf(await admin.PostAsJsonAsync($"{Reports}/{id}/drill", new { groupKeys = new[] { w.Options[0] } }));

        mine.GetProperty("detailRows").GetArrayLength().Should().Be(2);
        (mine.TryGetProperty("truncated", out var t1) && t1.GetBoolean()).Should().BeFalse();
        all.GetProperty("detailRows").GetArrayLength().Should().Be(500);
        all.GetProperty("truncated").GetBoolean().Should().BeTrue();
        all.GetProperty("state").GetString().Should().Be("ok");
    }

    [Fact]
    public async Task Export_WithoutPrintExportOnChild_403()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app, withChild: true);
        await Grant(app, w.Main, null, ("mis", true, "all"), ("sales", true, "all"));
        await Grant(app, w.Child!, null, ("mis", true, "all"), ("sales", false, "all"));
        var admin = app.AsUser();
        var def = Def(w.Main, groups: [new { field = Ref(w.Status), bucket = (string?)null }], measures: [Count()],
            child: new { moduleKey = w.Child, linkFieldId = w.ChildLink });
        var id = await Create(admin, def);
        await Share(admin, id, GroupId(app, "sales"));

        (await app.AsUser("sales01").GetAsync($"{Reports}/{id}/export")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.GetAsync($"{Reports}/{id}/export")).StatusCode.Should().Be(HttpStatusCode.OK);
        // 只合併主模組的報表，業務有主模組的匯出權限就能匯出
        var plain = await Create(admin, ByStatus(w), "單模組");
        await Share(admin, plain, GroupId(app, "sales"));
        (await app.AsUser("sales01").GetAsync($"{Reports}/{plain}/export")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- 可分享的群組清單：一般使用者不用設計器權限也能分享 ----

    [Fact]
    public async Task ShareTargets_NonAdminCanRead_OnlyIdAndName_AndCanShare()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var manager = app.AsUser("manager01");

        // 現有的群組清單要設計器權限，業務主管讀不到（維持原樣）
        (await manager.GetAsync($"{Base}/groups")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var res = await manager.GetAsync($"{Reports}/share-targets");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var groups = (await JsonOf(res)).EnumerateArray().ToList();
        groups.Should().NotBeEmpty();
        groups.Should().OnlyContain(g => g.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "id", "name" }));
        groups.Select(g => g.GetProperty("id").GetInt64()).Should().Contain(GroupId(app, "sales"));

        // 業務主管自己建的報表，實際分享成功，對方看得到
        var id = await Create(manager, ByStatus(w));
        (await Share(manager, id, GroupId(app, "sales"))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await app.AsUser("sales01").GetAsync($"{Reports}/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- 匯出旗標：與匯出同一套判斷，前端據此停用按鈕 ----

    [Fact]
    public async Task Detail_CanExport_FollowsPrintExportOnChild()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app, withChild: true);
        await Grant(app, w.Main, null, ("mis", true, "all"), ("sales", true, "all"));
        await Grant(app, w.Child!, null, ("mis", true, "all"), ("sales", false, "all"));
        var admin = app.AsUser();
        var merged = await Create(admin, Def(w.Main, groups: [new { field = Ref(w.Status), bucket = (string?)null }], measures: [Count()],
            child: new { moduleKey = w.Child, linkFieldId = w.ChildLink }));
        var plain = await Create(admin, ByStatus(w), "單模組");
        await Share(admin, merged, GroupId(app, "sales"));
        await Share(admin, plain, GroupId(app, "sales"));
        var sales = app.AsUser("sales01");

        async Task<bool> CanExport(HttpClient c, long id) => (await JsonOf(await c.GetAsync($"{Reports}/{id}"))).GetProperty("canExport").GetBoolean();
        (await CanExport(sales, merged)).Should().BeFalse();
        (await CanExport(sales, plain)).Should().BeTrue();
        (await CanExport(admin, merged)).Should().BeTrue();
    }

    [Fact]
    public async Task Detail_CanExport_FollowsPrintExportOnReferenceTarget()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var k = await AddCustomer(app, w);
        await Grant(app, w.Main, null, ("mis", true, "all"), ("sales", true, "all"));
        await Grant(app, k.Key, null, ("mis", true, "all"), ("sales", false, "all"));
        var admin = app.AsUser();
        var id = await Create(admin, Def(w.Main, columns: [Ref(k.Title, via: k.Cust)]));
        await Share(admin, id, GroupId(app, "sales"));
        var sales = app.AsUser("sales01");

        (await JsonOf(await sales.GetAsync($"{Reports}/{id}"))).GetProperty("canExport").GetBoolean().Should().BeFalse();
        await Grant(app, k.Key, null, ("mis", true, "all"), ("sales", true, "all"));
        (await JsonOf(await sales.GetAsync($"{Reports}/{id}"))).GetProperty("canExport").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Export_NumbersMatchRun()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var admin = app.AsUser();
        await AddRecord(admin, w, 100, w.Options[0]);
        await AddRecord(admin, w, 250, w.Options[0]);
        await AddRecord(admin, w, 40, w.Options[1]);
        var id = await Create(admin, ByStatus(w));
        var run = await Run(admin, id);

        var res = await admin.GetAsync($"{Reports}/{id}/export?locale=zh-TW");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.MediaType.Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        res.Content.Headers.ContentDisposition!.FileName.Should().EndWith(".xlsx");
        using var wb = new XLWorkbook(await res.Content.ReadAsStreamAsync());
        var ws = wb.Worksheet(1);
        var columns = run.GetProperty("columns").EnumerateArray().ToList();
        for (var c = 0; c < columns.Count; c++) ws.Cell(1, c + 1).GetString().Should().Be(columns[c].GetProperty("label").GetString());
        var groups = run.GetProperty("groups").EnumerateArray().ToList();
        for (var r = 0; r < groups.Count; r++)
        {
            ws.Cell(r + 2, 1).GetString().Should().Be(groups[r].GetProperty("groupLabels")[0].GetString());
            ws.Cell(r + 2, 2).GetDouble().Should().Be(groups[r].GetProperty("values").GetProperty("n").GetDouble());
            ws.Cell(r + 2, 3).GetDouble().Should().Be(groups[r].GetProperty("values").GetProperty("s").GetDouble());
        }
        var total = groups.Count + 2;
        ws.Cell(total, 1).GetString().Should().Be("合計");
        ws.Cell(total, 2).GetDouble().Should().Be((double)TotalOf(run, "n"));
        ws.Cell(total, 3).GetDouble().Should().Be((double)TotalOf(run, "s"));
        TotalOf(run, "s").Should().Be(390);
    }

    // ---- 設定 ----

    [Fact]
    public void ReportsMaxRows_IsBoundFromConfiguration()
    {
        using var app = new TestApp();
        app.Services.GetRequiredService<ReportOptions>().MaxRows.Should().Be(50_000);

        using var limited = app.WithWebHostBuilder(b => b.UseSetting("Reports:MaxRows", "123"));

        limited.Services.GetRequiredService<ReportOptions>().MaxRows.Should().Be(123);
    }

    [Fact]
    public async Task List_FreshInstall_Empty_AndCreateThenListShowsOwnerFields()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var admin = app.AsUser();
        (await JsonOf(await admin.GetAsync(Reports))).GetArrayLength().Should().Be(0);

        var id = await Create(admin, ByStatus(w));

        var item = (await JsonOf(await admin.GetAsync(Reports))).EnumerateArray().Single();
        item.GetProperty("id").GetInt64().Should().Be(id);
        item.GetProperty("name").GetString().Should().Be("月報");
        item.GetProperty("isOwner").GetBoolean().Should().BeTrue();
        item.GetProperty("mainModuleKey").GetString().Should().Be(w.Main);
        item.GetProperty("sharedGroupIds").GetArrayLength().Should().Be(0);
    }

    // ---- 審查修正 ----

    private static JsonElement Filters(JsonElement detail) => detail.GetProperty("definition").GetProperty("filters");

    [Fact]
    public async Task Get_MasksConditionValues_ViewerCannotSeeField_OwnerIncluded_ControlStillSeesValues()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var sales = GroupId(app, "sales");
        var admin = app.AsUser();
        var owner = app.AsUser("sales01");
        var def = Def(w.Main, columns: [Ref(w.Status)],
            filters: [new { field = Ref(w.Amount), op = "gt", value = 12345 }, new { field = Ref(w.Status), op = "eq", value = w.Options[0] }],
            measures: [new { id = "r", kind = "ratio", field = (object?)null, condition = (object)new { field = Ref(w.Amount), op = "gt", value = 98765 } }]);
        var id = await Create(owner, def);
        await Share(owner, id, sales, GroupId(app, "floor"));
        // 業務之後看不到金額欄位；現場完全沒有模組權限
        await Grant(app, w.Main, [new { fieldId = w.Amount, groupId = sales, access = "hidden" }], ("mis", true, "all"), ("sales", true, "all"));

        var hiddenForOwner = await owner.GetStringAsync($"{Reports}/{id}");
        var hiddenForFloor = await app.AsUser("floor01").GetStringAsync($"{Reports}/{id}");
        var seen = await admin.GetStringAsync($"{Reports}/{id}");

        foreach (var text in new[] { hiddenForOwner, hiddenForFloor })
            text.Should().NotContain("12345").And.NotContain("98765");
        var masked = JsonDocument.Parse(hiddenForOwner).RootElement;
        Filters(masked)[0].GetProperty("hidden").GetBoolean().Should().BeTrue();
        masked.GetProperty("definition").GetProperty("measures")[0].GetProperty("condition").GetProperty("hidden").GetBoolean().Should().BeTrue();
        // 看得到的欄位照舊
        Filters(masked)[1].GetProperty("value").GetString().Should().Be(w.Options[0]);
        // 完全沒有模組 Read：所有值都遮蔽
        Filters(JsonDocument.Parse(hiddenForFloor).RootElement).EnumerateArray().Should().OnlyContain(f => f.GetProperty("hidden").GetBoolean());
        seen.Should().Contain("12345").And.Contain("98765");
        Filters(JsonDocument.Parse(seen).RootElement)[0].TryGetProperty("hidden", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Copy_ViewerSeesWholeDefinition_Allowed_OtherwiseRejectedWithoutLeakingFields()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var sales = GroupId(app, "sales");
        var admin = app.AsUser();
        var id = await Create(admin, Def(w.Main, columns: [Ref(w.Amount)]));
        await Share(admin, id, sales, GroupId(app, "floor"));
        await Grant(app, w.Main, [new { fieldId = w.Amount, groupId = sales, access = "hidden" }], ("mis", true, "all"), ("sales", true, "all"));
        await Grant(app, w.Main, null, ("mis", true, "all"), ("sales", true, "all"));
        // 重新授權後業務看得到全部
        (await app.AsUser("sales01").PostAsync($"{Reports}/{id}/copy", null)).StatusCode.Should().Be(HttpStatusCode.Created);

        await Grant(app, w.Main, [new { fieldId = w.Amount, groupId = sales, access = "hidden" }], ("mis", true, "all"), ("sales", true, "all"));
        var rejected = await app.AsUser("sales01").PostAsync($"{Reports}/{id}/copy", null);
        var noRead = await app.AsUser("floor01").PostAsync($"{Reports}/{id}/copy", null);

        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await rejected.Content.ReadAsStringAsync()).Should().NotContain($"欄位 {w.Amount}").And.Contain("看不到");
        noRead.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await JsonOf(await app.AsUser("sales01").GetAsync(Reports))).GetArrayLength().Should().Be(2); // 分享來的與先前複製成功的那份
    }

    private sealed record Customer(string Key, long Title, long Back, long Cust, string CustKey);

    /// <summary>客戶模組（有指回主模組的參照）與主模組上指向客戶的參照欄位。</summary>
    private static async Task<Customer> AddCustomer(TestApp app, World w)
    {
        var admin = app.AsUser();
        var k = await CreateModule(admin);
        var back = await AddField(admin, k, "reference", new { refTarget = new { kind = "module", key = w.Main } });
        await Activate(admin, k);
        app.GrantBusinessGroups(k);
        var cust = await AddField(admin, w.Main, "reference", new { refTarget = new { kind = "module", key = k } });
        var title = long.Parse((await TitleKey(admin, k))[2..]);
        return new Customer(k, title, back.Id, cust.Id, cust.Key);
    }

    [Theory]
    [InlineData("direct-column")]
    [InlineData("direct-group")]
    [InlineData("direct-filter")]
    [InlineData("via")]
    public async Task Export_ReferenceTargetNeedsPrintExport(string position)
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var k = await AddCustomer(app, w);
        var def = position switch
        {
            "direct-column" => Def(w.Main, columns: [Ref(k.Cust)]),
            "direct-group" => Def(w.Main, groups: [new { field = Ref(k.Cust), bucket = (string?)null }], measures: [Count()]),
            "direct-filter" => Def(w.Main, columns: [Ref(w.Amount)], filters: [new { field = Ref(k.Cust), op = "isEmpty", value = (object?)null }]),
            _ => Def(w.Main, columns: [Ref(k.Title, via: k.Cust)]),
        };
        await Grant(app, w.Main, null, ("mis", true, "all"), ("sales", true, "all"));
        await Grant(app, k.Key, null, ("mis", true, "all"), ("sales", false, "all"));
        var admin = app.AsUser();
        var id = await Create(admin, def);
        await Share(admin, id, GroupId(app, "sales"));
        var sales = app.AsUser("sales01");

        (await sales.GetAsync($"{Reports}/{id}/export")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await Grant(app, k.Key, null, ("mis", true, "all"), ("sales", true, "all"));
        (await sales.GetAsync($"{Reports}/{id}/export")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Save_ViaTargetFieldThatIsReference_400()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var k = await AddCustomer(app, w);
        var admin = app.AsUser();

        (await admin.PostAsJsonAsync(Reports, new { name = "x", definition = Def(w.Main, columns: [Ref(k.Back, via: k.Cust)]) }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync($"{Reports}/preview", Def(w.Main, columns: [Ref(k.Back, via: k.Cust)]))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync(Reports, new { name = "x", definition = Def(w.Main, columns: [Ref(k.Title, via: k.Cust)]) }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Locale_EnUS_AppliesToRunDrillPreviewAndExport()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var admin = app.AsUser();
        var blockId = (await JsonOf(await admin.GetAsync($"{Base}/modules/{w.Main}"))).GetProperty("blocks")[0].GetProperty("id").GetInt64();
        (await admin.PutAsJsonAsync($"{Base}/fields/{w.Status}", new
        {
            type = "enum", label = new Dictionary<string, string> { ["zh-TW"] = "狀態", ["en-US"] = "Status" }, config = new { }, blockId
        })).EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync($"{Base}/options/{w.Options[0][4..]}", new
        {
            label = new Dictionary<string, string> { ["zh-TW"] = "新單", ["en-US"] = "New" }, color = "#fff", active = true
        })).EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync($"{Base}/fields/{w.Amount}", new
        {
            type = "decimal", label = new Dictionary<string, string> { ["zh-TW"] = "金額", ["en-US"] = "Amount" }, config = new { scale = 0 }, blockId
        })).EnsureSuccessStatusCode();
        await AddRecord(admin, w, 10, w.Options[0]);
        var def = Def(w.Main, columns: [Ref(w.Amount)], groups: [new { field = Ref(w.Status), bucket = (string?)null }], measures: [Count()]);
        var id = await Create(admin, def);

        var run = await JsonOf(await admin.PostAsJsonAsync($"{Reports}/{id}/run?locale=en-US", new { }));
        var preview = await JsonOf(await admin.PostAsJsonAsync($"{Reports}/preview?locale=en-US", def));
        var drill = await JsonOf(await admin.PostAsJsonAsync($"{Reports}/{id}/drill?locale=en-US", new { groupKeys = new[] { w.Options[0] } }));
        var export = await admin.GetAsync($"{Reports}/{id}/export?locale=en-US");

        foreach (var r in new[] { run, preview })
        {
            r.GetProperty("columns")[0].GetProperty("label").GetString().Should().Be("Status");
            r.GetProperty("groups")[0].GetProperty("groupLabels")[0].GetString().Should().Be("New");
        }
        drill.GetProperty("columns")[0].GetProperty("label").GetString().Should().Be("Amount");
        using var wb = new XLWorkbook(await export.Content.ReadAsStreamAsync());
        wb.Worksheet(1).Cell(1, 1).GetString().Should().Be("Status");
        wb.Worksheet(1).Cell(2, 1).GetString().Should().Be("New");
    }

    [Fact]
    public async Task Save_MeasureConditionValue_IsNormalized_AndBadOneRejected()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var admin = app.AsUser();
        var raw = w.Options[0].Replace("opt_", "opt_0");
        object Ratio(object? value) => new { id = "r", kind = "ratio", field = (object?)null, condition = (object)new { field = Ref(w.Status), op = "eq", value } };

        var id = await Create(admin, Def(w.Main, measures: [Ratio(raw)]));

        var stored = (await JsonOf(await admin.GetAsync($"{Reports}/{id}"))).GetProperty("definition").GetProperty("measures")[0];
        stored.GetProperty("condition").GetProperty("value").GetString().Should().Be(w.Options[0]);
        (await admin.PostAsJsonAsync(Reports, new { name = "x", definition = Def(w.Main, measures: [Ratio("opt_999999")]) })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SetShares_RenewsRowVersion()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var admin = app.AsUser();
        var id = await Create(admin, ByStatus(w));
        string Version()
        {
            using var scope = app.Services.CreateScope();
            return scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<Report>().AsNoTracking().Single(r => r.Id == id).RowVersion;
        }
        var before = Version();

        await Share(admin, id, GroupId(app, "sales"));

        Version().Should().NotBe(before);
    }

    [Fact]
    public async Task Export_PrintExportButFieldHidden_NoFile()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var sales = GroupId(app, "sales");
        await Grant(app, w.Main, [new { fieldId = w.Amount, groupId = sales, access = "hidden" }], ("mis", true, "all"), ("sales", true, "all"));
        var admin = app.AsUser();
        var id = await Create(admin, ByStatus(w));
        await Share(admin, id, sales);

        var res = await app.AsUser("sales01").GetAsync($"{Reports}/{id}/export");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        res.Content.Headers.ContentType!.MediaType.Should().NotContain("spreadsheet");
    }

    [Fact]
    public async Task Export_ColumnsWithMeasures_IncludeTotals_AndDedupedNote()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app, withChild: true);
        var admin = app.AsUser();
        await AddRecord(admin, w, 100, w.Options[0]);
        var plain = await Create(admin, Def(w.Main, columns: [Ref(w.Amount)], measures: [Sum("s", w.Amount)]));
        var merged = await Create(admin, Def(w.Main, groups: [new { field = Ref(w.Status), bucket = (string?)null }], measures: [Sum("s", w.Amount)],
            child: new { moduleKey = w.Child, linkFieldId = w.ChildLink }));

        using var totals = new XLWorkbook(await (await admin.GetAsync($"{Reports}/{plain}/export")).Content.ReadAsStreamAsync());
        using var note = new XLWorkbook(await (await admin.GetAsync($"{Reports}/{merged}/export")).Content.ReadAsStreamAsync());

        var label = totals.Worksheet(1).CellsUsed().Single(c => c.GetString().EndsWith("加總"));
        label.CellRight().GetDouble().Should().Be(100);
        note.Worksheet(1).CellsUsed().Should().Contain(c => c.GetString().Contains("只算一次"));
        totals.Worksheet(1).CellsUsed().Should().NotContain(c => c.GetString().Contains("只算一次"));
    }

    [Fact]
    public async Task Run_StoredConditionValueNoLongerMatchesFieldType_NoMatchNot500()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var admin = app.AsUser();
        var blockId = (await JsonOf(await admin.GetAsync($"{Base}/modules/{w.Main}"))).GetProperty("blocks")[0].GetProperty("id").GetInt64();
        var f = await AddField(admin, w.Main, "formula", new { formula = $"{{{w.Amount}}} * 2", resultType = "number", scale = 0 });
        (await admin.PostAsJsonAsync($"{Base}/modules/{w.Main}/records", new
        {
            values = new Dictionary<string, object> { [w.TitleKey] = "t", [w.AmountKey] = 10, [$"f_{w.Due}"] = "2026-11-05" }
        })).EnsureSuccessStatusCode();
        var def = Def(w.Main, columns: [Ref(w.Amount)], filters: [new { field = Ref(f.Id), op = "gt", value = 5 }]);
        var id = await Create(admin, def);
        (await Run(admin, id)).GetProperty("state").GetString().Should().Be("ok");

        (await admin.PutAsJsonAsync($"{Base}/fields/{f.Id}", new
        {
            type = "formula", label = new Dictionary<string, string> { ["zh-TW"] = "公式" }, blockId,
            config = new { formula = $"{{{w.Due}}}", resultType = "date" }
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Run(admin, id)).GetProperty("state").GetString().Should().Be("empty");
    }
}
