using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests;

/// <summary>儀表板測試共用：一個啟用的模組（標題、金額、狀態、日期）、報表與版面 body 的快速建法。</summary>
internal static class DashboardTestKit
{
    public const string Dashboards = Base + "/dashboards";
    public const string AdminDashboards = Base + "/admin/dashboards";

    internal sealed record World(
        string Main, string TitleKey, long Amount, string AmountKey, long Status, string StatusKey, long Due, string DueKey, string[] Options);

    public static async Task<World> MakeWorld(TestApp app)
    {
        var admin = app.AsUser();
        var main = await CreateModule(admin);
        var amount = await AddField(admin, main, "decimal", new { scale = 0 });
        var status = await AddField(admin, main, "enum", new { });
        var options = new List<string>();
        foreach (var label in new[] { "新單", "處理中", "結案" })
        {
            var res = await admin.PostAsJsonAsync($"{Base}/fields/{status.Id}/options", new { label = new Dictionary<string, string> { ["zh-TW"] = label } });
            res.EnsureSuccessStatusCode();
            options.Add($"opt_{(await JsonOf(res)).GetProperty("id").GetInt64()}");
        }
        var due = await AddField(admin, main, "date", new { });
        await Activate(admin, main);
        app.GrantBusinessGroups(main);
        return new World(main, await TitleKey(admin, main), amount.Id, amount.Key, status.Id, status.Key, due.Id, due.Key, [.. options]);
    }

    public static object Ref(long field) => new { source = "main", fieldId = field, viaFieldId = (long?)null };

    /// <summary>依狀態分組，筆數與金額加總；有主要日期欄位。</summary>
    public static Dictionary<string, object?> ByStatus(World w, string viz = "bar") => new()
    {
        ["version"] = 1, ["mainModuleKey"] = w.Main, ["child"] = null, ["columns"] = Array.Empty<object>(),
        ["groups"] = new object[] { new { field = Ref(w.Status), bucket = (string?)null } },
        ["measures"] = new object[]
        {
            new { id = "n", kind = "countMain", field = (object?)null, condition = (object?)null },
            new { id = "s", kind = "sum", field = Ref(w.Amount), condition = (object?)null }
        },
        ["filters"] = Array.Empty<object>(), ["primaryDateField"] = Ref(w.Due), ["dateRange"] = null,
        ["sort"] = Array.Empty<object>(), ["viz"] = viz
    };

    public static async Task AddRecord(HttpClient c, World w, decimal amount, int status, DateOnly due)
    {
        var values = new Dictionary<string, object>
            { [w.TitleKey] = "t", [w.AmountKey] = amount, [w.StatusKey] = w.Options[status], [w.DueKey] = due.ToString("yyyy-MM-dd") };
        (await c.PostAsJsonAsync($"{Base}/modules/{w.Main}/records", new { values })).EnsureSuccessStatusCode();
    }

    /// <summary>整組換掉模組授權；每個項目是 (群組鍵, 匯出, 範圍)，範圍是 all|own。</summary>
    public static async Task Grant(TestApp app, string module, params (string Group, string Scope)[] groups)
    {
        var res = await app.AsUser().PutAsJsonAsync($"{Base}/modules/{module}/permissions", new
        {
            groups = groups.Select(g => new
            {
                groupId = GroupId(app, g.Group),
                privileges = new { read = true, add = true, edit = true, delete = true, printExport = true, pricing = true, approve = false },
                scope = g.Scope
            }).ToArray(),
            users = Array.Empty<object>(), fields = Array.Empty<object>(), options = Array.Empty<object>()
        });
        res.EnsureSuccessStatusCode();
    }

    public static async Task<JsonElement> RunCard(HttpClient c, long dashboard, string card, object? filterRange = null)
    {
        var res = await c.PostAsJsonAsync($"{Dashboards}/{dashboard}/cards/{card}/run", new { filterRange });
        res.EnsureSuccessStatusCode();
        return await JsonOf(res);
    }

    public static decimal? TotalOf(JsonElement result, string measure) =>
        result.GetProperty("total").TryGetProperty(measure, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;

    public static async Task<long> CreateReport(HttpClient c, object definition, string name = "報表")
    {
        var res = await c.PostAsJsonAsync(Base + "/reports", new { name, definition });
        res.EnsureSuccessStatusCode();
        return (await JsonOf(res)).GetProperty("id").GetInt64();
    }

    public static Task<HttpResponseMessage> ShareReport(HttpClient c, long id, params long[] groupIds) =>
        c.PutAsJsonAsync($"{Base}/reports/{id}/shares", new { groupIds });

    public static long GroupId(TestApp app, string key)
    {
        using var scope = app.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<PermissionGroup>().Single(g => g.Key == key).Id;
    }

    public static object Card(string id, long reportId, int x = 0, int y = 0, string viz = "bar", object? options = null) =>
        new { id, reportId, title = (string?)null, viz, x, y, w = 6, h = 3, options = options ?? new { } };

    public static object Layout(params object[] cards) => new { cards };

    public static async Task<long> CreateAssigned(HttpClient admin, string name, object layout, params long[] groupIds)
    {
        var res = await admin.PostAsJsonAsync(AdminDashboards, new { name, layout });
        res.EnsureSuccessStatusCode();
        var id = (await JsonOf(res)).GetProperty("id").GetInt64();
        (await admin.PutAsJsonAsync($"{AdminDashboards}/{id}/assignments", new { groupIds })).EnsureSuccessStatusCode();
        return id;
    }

    public static async Task<JsonElement> GetDashboard(HttpClient c, long id) => await JsonOf(await c.GetAsync($"{Dashboards}/{id}"));

    public static long[] CardReportIds(JsonElement detail) =>
        detail.GetProperty("layout").GetProperty("cards").EnumerateArray().Select(c => c.GetProperty("reportId").GetInt64()).ToArray();
}
