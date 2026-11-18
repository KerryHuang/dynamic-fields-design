using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>任務報表的 API 驗收（看板設計 §7、§12）：資料來源目錄、用 API 跑任務報表、下鑽的任務 Id 點進去伺服器再驗。</summary>
[Trait("Category", "Postgres")]
public class TaskReportsApiTests : IDisposable
{
    private const string Reports = Base + "/reports";

    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private static object Ref(long code) => new { source = "main", fieldId = code, viaFieldId = (long?)null };

    private static Dictionary<string, object?> TaskDef(object[] measures, object[]? groups = null, object[]? columns = null) => new()
    {
        ["version"] = 1, ["dataset"] = "tasks", ["mainModuleKey"] = "", ["child"] = null, ["columns"] = columns ?? [],
        ["groups"] = groups ?? [], ["measures"] = measures, ["filters"] = Array.Empty<object>(), ["primaryDateField"] = null,
        ["dateRange"] = null, ["sort"] = Array.Empty<object>(), ["viz"] = "table"
    };

    /// <summary>依負責人分組：件數、狀態＝待辦的件數。</summary>
    private static Dictionary<string, object?> WorkloadDef() => TaskDef(
        [new { id = "n", kind = "countMain", field = (object?)null, condition = (object?)null },
         new { id = "todo", kind = "countIf", field = (object?)null, condition = new { field = Ref(-2), op = "eq", value = "opt_1" } }],
        [new { field = Ref(-3), bucket = (string?)null }]);

    private static async Task<long> CreateReport(HttpClient c, object definition)
    {
        var res = await c.PostAsJsonAsync(Reports, new { name = "任務負載", definition });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return (await JsonOf(res)).GetProperty("id").GetInt64();
    }

    private static async Task<long> CreateTask(HttpClient c, string path, object body)
    {
        var res = await c.PostAsJsonAsync(path, body);
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return (await JsonOf(res)).GetProperty("id").GetInt64();
    }

    private static async Task<JsonElement> Post(HttpClient c, string path, object body)
    {
        var res = await c.PostAsJsonAsync(path, body);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return await JsonOf(res);
    }

    [Fact]
    public async Task Datasets_ListAndCatalog()
    {
        var admin = _app.AsUser();

        var datasets = await JsonOf(await admin.GetAsync($"{Reports}/datasets?locale=zh-TW"));
        datasets.EnumerateArray().Select(d => (d.GetProperty("dataset").GetString(), d.GetProperty("name").GetString()))
            .Should().Equal(("tasks", "任務"), ("taskEvents", "任務事件（建立或完成）"));

        var tasks = (await JsonOf(await admin.GetAsync($"{Reports}/datasets/tasks/catalog"))).GetProperty("fields").EnumerateArray().ToList();
        tasks.Select(f => f.GetProperty("id").GetInt64()).Should().Equal(Enumerable.Range(1, 15).Select(i => (long)-i));
        tasks[0].GetProperty("canGroup").GetBoolean().Should().BeFalse("標題不能分組（T12）");
        tasks[1].GetProperty("options").EnumerateArray().Select(o => o.GetProperty("label").GetString()).Should().Equal("待辦", "進行中", "已完成", "已取消");

        var events = (await JsonOf(await admin.GetAsync($"{Reports}/datasets/taskEvents/catalog"))).GetProperty("fields").EnumerateArray()
            .Select(f => f.GetProperty("id").GetInt64()).ToList();
        events.Should().Equal(-2, -3, -4, -5, -9, -10, -11, -18, -19);
        events.Should().NotContain(-12).And.NotContain(-1, "標題只在下鑽固定欄出現");

        (await admin.GetAsync($"{Reports}/datasets/foo/catalog")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TaskReport_EndToEnd_Http()
    {
        var admin = _app.AsUser();
        var sales = _app.AsUser("sales01");
        // 業務沒有授權的模組：記錄看不到，但任務指派給業務一，業務一看得到任務
        var module = await CreateModule(admin);
        await Activate(admin, module);
        var title = await TitleKey(admin, module);
        var moduleId = (await JsonOf(await admin.GetAsync($"{Base}/modules/{module}"))).GetProperty("id").GetInt64();
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            db.Set<TaskRule>().Add(new TaskRule
            {
                Key = "api-secret-rule", NameI18n = new([KeyValuePair.Create("zh-TW", "機密規則")]), ModuleId = moduleId,
                CreatedUserId = "admin", CreatedTime = new DateTime(2026, 11, 4, 9, 0, 0)
            });
            await db.SaveChangesAsync();
        }
        var record = await JsonOf(await admin.PostAsJsonAsync($"{Base}/modules/{module}/records",
            new { values = new Dictionary<string, object> { [title] = "機密案件" } }));
        var recordId = record.GetProperty("id").GetInt64();
        var hidden = await CreateTask(admin, $"{Base}/records/{recordId}/tasks", new { title = "看不到記錄的任務", assigneeUserId = "sales01" });
        var mine = await CreateTask(sales, $"{Base}/tasks", new { title = "自己的待辦", assigneeUserId = "sales01" });
        var done = await CreateTask(sales, $"{Base}/tasks", new { title = "做完的", assigneeUserId = "sales01" });
        (await sales.PatchAsync($"{Base}/tasks/{done}", new StringContent("""{"status":"done"}""", Encoding.UTF8, "application/json")))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        await CreateTask(admin, $"{Base}/tasks", new { title = "別人的", assigneeUserId = "sales02" });

        // 目錄選項依觀看者（J14）：業務一沒有該模組的讀取權，看不到模組與其規則；管理員看得到（對照）
        static string Catalog(JsonElement c) => c.GetRawText();
        var salesCatalog = await JsonOf(await sales.GetAsync($"{Reports}/datasets/tasks/catalog"));
        Catalog(salesCatalog).Should().NotContain("測試模組").And.NotContain("機密規則");
        var salesEvents = await JsonOf(await sales.GetAsync($"{Reports}/datasets/taskEvents/catalog"));
        Catalog(salesEvents).Should().NotContain("測試模組").And.NotContain("機密規則");
        var adminCatalog = await JsonOf(await admin.GetAsync($"{Reports}/datasets/tasks/catalog"));
        Catalog(adminCatalog).Should().Contain("測試模組").And.Contain("機密規則");

        var id = await CreateReport(sales, WorkloadDef());
        var run = await Post(sales, $"{Reports}/{id}/run", new { });
        run.GetProperty("state").GetString().Should().Be("ok");
        run.GetProperty("engine").GetString().Should().Be("memory");
        run.GetProperty("total").GetProperty("n").GetDecimal().Should().Be(3);
        run.GetProperty("total").GetProperty("todo").GetDecimal().Should().Be(2);
        run.TryGetProperty("taskIds", out _).Should().BeFalse("沒有明細就沒有任務 Id");

        var preview = await Post(sales, $"{Reports}/preview", WorkloadDef());
        preview.GetProperty("total").GetProperty("n").GetDecimal().Should().Be(3);

        var drill = await Post(sales, $"{Reports}/{id}/drill", new { groupKeys = new[] { "sales01" } });
        var rows = drill.GetProperty("detailRows").EnumerateArray().ToList();
        var taskIds = drill.GetProperty("taskIds").EnumerateArray().Select(x => x.GetInt64()).ToList();
        taskIds.Should().HaveCount(rows.Count).And.BeEquivalentTo([hidden, mine, done]);
        var hiddenRow = rows[taskIds.IndexOf(hidden)];
        hiddenRow[0].GetString().Should().Be("看不到記錄的任務");
        hiddenRow[5].GetString().Should().Be("（無權限）");
        // 畫面顯示的是模組名稱（不是代號），所以兩個都要比；對照斷言確認序列化不跳脫中文，NotContain 才有意義
        drill.GetRawText().Should().Contain("看不到記錄的任務")
            .And.NotContain(module).And.NotContain("測試模組").And.NotContain("機密案件").And.NotContain("機密規則");

        var export = await sales.GetAsync($"{Reports}/{id}/export");
        export.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var wb = new XLWorkbook(new MemoryStream(await export.Content.ReadAsByteArrayAsync())))
            wb.Worksheets.Single().Row(2).Cell(1).GetString().Should().Be("業務一");

        // 點任務伺服器再驗（審查 Y11）：觀看者 200，看不到記錄的那件不帶模組代號與記錄 Id；看不到任務的人 404
        var detail = await sales.GetAsync($"{Base}/tasks/{hidden}");
        detail.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await JsonOf(detail);
        var rec = body.GetProperty("record");
        rec.GetProperty("accessible").GetBoolean().Should().BeFalse();
        (rec.TryGetProperty("moduleKey", out var mk) ? mk.ValueKind : JsonValueKind.Null).Should().Be(JsonValueKind.Null);
        (rec.TryGetProperty("recordId", out var rid) ? rid.ValueKind : JsonValueKind.Null).Should().Be(JsonValueKind.Null);
        body.GetRawText().Should().Contain("看不到記錄的任務")
            .And.NotContain(module).And.NotContain("測試模組").And.NotContain("機密案件");
        (await _app.AsUser("sales02").GetAsync($"{Base}/tasks/{hidden}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        foreach (var t in taskIds) (await sales.GetAsync($"{Base}/tasks/{t}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TaskReport_OtherUser_SeesOwnNumbers_Http()
    {
        var admin = _app.AsUser();
        await CreateTask(admin, $"{Base}/tasks", new { title = "業務一的", assigneeUserId = "sales01" });
        await CreateTask(admin, $"{Base}/tasks", new { title = "業務一的另一件", assigneeUserId = "sales01" });
        await CreateTask(admin, $"{Base}/tasks", new { title = "業務二的", assigneeUserId = "sales02" });
        var id = await CreateReport(admin, WorkloadDef());
        var salesGroup = DashboardTestKit.GroupId(_app, "sales");
        (await DashboardTestKit.ShareReport(admin, id, salesGroup)).StatusCode.Should().Be(HttpStatusCode.OK);

        async Task<decimal> TotalAs(string user) =>
            (await Post(_app.AsUser(user), $"{Reports}/{id}/run", new { })).GetProperty("total").GetProperty("n").GetDecimal();
        (await TotalAs("sales01")).Should().Be(2);
        (await TotalAs("sales02")).Should().Be(1);
        (await TotalAs("admin")).Should().Be(3);

        // 儀表板卡片（審查 G5）：分享給群組的任務報表放在指派給群組的儀表板，兩個觀看者各算各的
        var dashboard = await DashboardTestKit.CreateAssigned(admin, "任務", DashboardTestKit.Layout(DashboardTestKit.Card("c1", id)), salesGroup);
        DashboardTestKit.TotalOf(await DashboardTestKit.RunCard(_app.AsUser("sales01"), dashboard, "c1"), "n").Should().Be(2);
        DashboardTestKit.TotalOf(await DashboardTestKit.RunCard(_app.AsUser("sales02"), dashboard, "c1"), "n").Should().Be(1);
    }

    private async Task<long> Department(HttpClient admin, string code, string name, long? parent)
    {
        var res = await admin.PostAsJsonAsync($"{Base}/departments", new
        {
            code, name = new Dictionary<string, string> { ["zh-TW"] = name }, parentId = parent, managerUserId = (string?)null, sortOrder = 0, active = true
        });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return (await JsonOf(res)).GetProperty("id").GetInt64();
    }

    /// <summary>總部 → 業務部 → 業務一課；業務一在業務一課（2 件）、業務二在業務部（1 件）、未指派 1 件。</summary>
    private async Task<(long Report, long Hq, long Sales, long Sales1)> ArrangeDepartmentsAsync(HttpClient admin)
    {
        var hq = await Department(admin, "hq", "總部", null);
        var sales = await Department(admin, "sales", "業務部", hq);
        var sales1 = await Department(admin, "sales1", "業務一課", sales);
        (await admin.PutAsJsonAsync($"{Base}/admin/users/sales01/department", new { departmentId = sales1 })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.PutAsJsonAsync($"{Base}/admin/users/sales02/department", new { departmentId = sales })).StatusCode.Should().Be(HttpStatusCode.OK);
        await CreateTask(admin, $"{Base}/tasks", new { title = "一課甲", assigneeUserId = "sales01" });
        await CreateTask(admin, $"{Base}/tasks", new { title = "一課乙", assigneeUserId = "sales01" });
        await CreateTask(admin, $"{Base}/tasks", new { title = "業務部", assigneeUserId = "sales02" });
        await CreateTask(admin, $"{Base}/tasks", new { title = "未指派", assigneeUserId = (string?)null });
        var def = WorkloadDef();
        def["primaryDateField"] = Ref(-7);
        return (await CreateReport(admin, def), hq, sales, sales1);
    }

    private static decimal N(JsonElement run) => run.GetProperty("total").GetProperty("n").GetDecimal();

    [Fact]
    public async Task DepartmentFilter_Run_Http()
    {
        var admin = _app.AsUser();
        var (id, hq, sales, sales1) = await ArrangeDepartmentsAsync(admin);

        N(await Post(admin, $"{Reports}/{id}/run", new { })).Should().Be(4, "不選部門就不篩，未指派也算");
        N(await Post(admin, $"{Reports}/{id}/run", new { departmentIds = Array.Empty<long>() })).Should().Be(4);
        N(await Post(admin, $"{Reports}/{id}/run", new { departmentIds = new[] { hq } })).Should().Be(3, "預設包含下層，未指派的排除");
        N(await Post(admin, $"{Reports}/{id}/run", new { departmentIds = new[] { sales } })).Should().Be(3);
        N(await Post(admin, $"{Reports}/{id}/run", new { departmentIds = new[] { sales }, includeSubDepartments = false })).Should().Be(1);
        N(await Post(admin, $"{Reports}/{id}/run", new { departmentIds = new[] { sales1 }, includeSubDepartments = false })).Should().Be(2);
        N(await Post(admin, $"{Reports}/{id}/run", new { departmentIds = new[] { sales1, sales }, includeSubDepartments = false })).Should().Be(3);
    }

    [Fact]
    public async Task DepartmentFilter_IncludesInactiveDepartmentTree_Http()
    {
        var admin = _app.AsUser();
        var (id, hq, sales, _) = await ArrangeDepartmentsAsync(admin);
        var res = await admin.PutAsJsonAsync($"{Base}/departments/{sales}", new
        {
            code = "sales", name = new Dictionary<string, string> { ["zh-TW"] = "業務部" }, parentId = hq, managerUserId = (string?)null, sortOrder = 0, active = false
        });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        N(await Post(admin, $"{Reports}/{id}/run", new { departmentIds = new[] { sales } })).Should().Be(3, "停用部門仍可選、下層照算");
    }

    [Fact]
    public async Task DepartmentFilter_Drill_AndTotals_Http()
    {
        var admin = _app.AsUser();
        var def = TaskDef([new { id = "n", kind = "countMain", field = (object?)null, condition = (object?)null }],
            columns: [Ref(-1), Ref(-3)]);
        var (_, _, sales, sales1) = await ArrangeDepartmentsAsync(admin);
        var id = await CreateReport(admin, def);

        var all = await Post(admin, $"{Reports}/{id}/drill", new { groupKeys = Array.Empty<string>() });
        all.GetProperty("detailRows").GetArrayLength().Should().Be(4);

        var drill = await Post(admin, $"{Reports}/{id}/drill", new { groupKeys = Array.Empty<string>(), departmentIds = new[] { sales1 } });
        drill.GetProperty("detailRows").GetArrayLength().Should().Be(2);
        drill.GetProperty("taskIds").GetArrayLength().Should().Be(2, "taskIds 與明細逐列對齊");
        N(drill).Should().Be(2);

        var onlySales = await Post(admin, $"{Reports}/{id}/drill",
            new { groupKeys = Array.Empty<string>(), departmentIds = new[] { sales }, includeSubDepartments = false });
        onlySales.GetProperty("detailRows").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task DepartmentFilter_TaskEvents_BothRowKinds_Http()
    {
        var admin = _app.AsUser();
        var (_, _, _, sales1) = await ArrangeDepartmentsAsync(admin);
        var done = await CreateTask(admin, $"{Base}/tasks", new { title = "做完", assigneeUserId = "sales01" });
        (await admin.PatchAsync($"{Base}/tasks/{done}", new StringContent("""{"status":"done"}""", Encoding.UTF8, "application/json")))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var def = new Dictionary<string, object?>(TaskDef([new { id = "n", kind = "countMain", field = (object?)null, condition = (object?)null }]))
        {
            ["dataset"] = "taskEvents"
        };
        var id = await CreateReport(admin, def);

        N(await Post(admin, $"{Reports}/{id}/run", new { })).Should().Be(6, "5 個任務建立 5 列＋完成 1 列");
        N(await Post(admin, $"{Reports}/{id}/run", new { departmentIds = new[] { sales1 } })).Should().Be(4, "一課 3 個任務的建立列＋完成列");
    }

    [Fact]
    public async Task Range_Run_AppliesToPrimaryDate_Http()
    {
        var admin = _app.AsUser();
        var (id, _, _, _) = await ArrangeDepartmentsAsync(admin);

        N(await Post(admin, $"{Reports}/{id}/run", new { range = new { kind = "thisMonth" } })).Should().Be(4);
        var last = await Post(admin, $"{Reports}/{id}/run", new { range = new { kind = "lastMonth" } });
        last.GetProperty("state").GetString().Should().Be("empty");
        (await admin.PostAsJsonAsync($"{Reports}/{id}/run", new { range = new { kind = "bogus" } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Export_AppliesCurrentFilters_Http()
    {
        var admin = _app.AsUser();
        var (_, _, sales, sales1) = await ArrangeDepartmentsAsync(admin);
        var def = TaskDef([new { id = "n", kind = "countMain", field = (object?)null, condition = (object?)null }],
            columns: [Ref(-1)]);
        def["primaryDateField"] = Ref(-7);
        var id = await CreateReport(admin, def);

        async Task<int> DataRows(string query)
        {
            var res = await admin.GetAsync($"{Reports}/{id}/export{query}");
            res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
            using var wb = new XLWorkbook(new MemoryStream(await res.Content.ReadAsByteArrayAsync()));
            // 第 1 列是表頭；明細下方有空列與總計，只數標題欄有內容、且是任務標題的列
            return wb.Worksheets.Single().RowsUsed().Skip(1).Count(r => r.Cell(1).GetString() is "一課甲" or "一課乙" or "業務部" or "未指派");
        }

        (await DataRows("")).Should().Be(4);
        (await DataRows($"?departmentIds={sales1}")).Should().Be(2);
        (await DataRows($"?departmentIds={sales}&includeSubDepartments=false")).Should().Be(1);
        (await DataRows($"?departmentIds={sales}&departmentIds={sales1}&includeSubDepartments=false")).Should().Be(3);
        (await DataRows("?rangeKind=thisMonth")).Should().Be(4);
        (await DataRows("?rangeKind=lastMonth")).Should().Be(0);
        (await admin.GetAsync($"{Reports}/{id}/export?rangeKind=bogus")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DepartmentFilter_NonAdmin_NeverExceedsVisibleTasks_Http()
    {
        var admin = _app.AsUser();
        var (_, hq, sales, sales1) = await ArrangeDepartmentsAsync(admin);
        var sales02 = _app.AsUser("sales02");
        var id = await CreateReport(sales02, WorkloadDef());

        N(await Post(sales02, $"{Reports}/{id}/run", new { })).Should().Be(1, "業務二只看得到指派給自己的一件");
        N(await Post(sales02, $"{Reports}/{id}/run", new { departmentIds = new[] { hq } })).Should().Be(1, "選總部（含下層）不會多看到別人的任務");
        N(await Post(sales02, $"{Reports}/{id}/run", new { departmentIds = new[] { sales } })).Should().Be(1);
        (await Post(sales02, $"{Reports}/{id}/run", new { departmentIds = new[] { sales1 } })).GetProperty("state").GetString()
            .Should().Be("empty", "他的任務在業務部，不在業務一課");
        var drill = await Post(sales02, $"{Reports}/{id}/drill", new { groupKeys = new[] { "sales02" }, departmentIds = new[] { hq } });
        drill.GetProperty("taskIds").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Export_BadFilterParameters_Return400_Http()
    {
        var admin = _app.AsUser();
        var (id, _, _, _) = await ArrangeDepartmentsAsync(admin);

        async Task Expect400(string query)
        {
            var res = await admin.GetAsync($"{Reports}/{id}/export{query}");
            res.StatusCode.Should().Be(HttpStatusCode.BadRequest, query + " " + await res.Content.ReadAsStringAsync());
        }
        await Expect400("?rangeKind=lastDays&rangeN=3651");
        await Expect400("?rangeKind=lastDays&rangeN=0");
        await Expect400("?rangeKind=lastDays");
        await Expect400("?departmentIds=abc");
        await Expect400("?rangeN=7");
        (await admin.GetAsync($"{Reports}/{id}/export?rangeKind=lastDays&rangeN=3650")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DepartmentFilter_ModuleReport_IsIgnored_Http()
    {
        var admin = _app.AsUser();
        var module = await CreateModule(admin);
        await Activate(admin, module);
        var title = await TitleKey(admin, module);
        await admin.PostAsJsonAsync($"{Base}/modules/{module}/records", new { values = new Dictionary<string, object> { [title] = "甲" } });
        var def = new Dictionary<string, object?>
        {
            ["version"] = 1, ["mainModuleKey"] = module, ["child"] = null, ["columns"] = Array.Empty<object>(), ["groups"] = Array.Empty<object>(),
            ["measures"] = new object[] { new { id = "n", kind = "countMain", field = (object?)null, condition = (object?)null } },
            ["filters"] = Array.Empty<object>(), ["primaryDateField"] = null, ["dateRange"] = null, ["sort"] = Array.Empty<object>(), ["viz"] = "table"
        };
        var id = await CreateReport(admin, def);

        N(await Post(admin, $"{Reports}/{id}/run", new { departmentIds = new[] { 999L } })).Should().Be(1, "模組資料來源收到部門參數就忽略");
    }

    [Fact]
    public async Task ModuleCatalog_Unchanged_Http()
    {
        var admin = _app.AsUser();
        var module = await CreateModule(admin);
        await Activate(admin, module);

        var list = await JsonOf(await admin.GetAsync($"{Reports}/catalog"));

        list.EnumerateArray().Select(m => m.GetProperty("key").GetString()).Should().Contain(module).And.NotContain("tasks").And.NotContain("taskEvents");
    }
}
