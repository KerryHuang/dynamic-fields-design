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
                CreatedUserId = "admin", CreatedTime = DateTime.UtcNow
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
