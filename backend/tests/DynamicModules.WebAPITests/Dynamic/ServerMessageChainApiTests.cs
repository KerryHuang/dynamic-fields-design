using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Localization;
using DynamicModules.Application.Dynamic.ReportService.Data;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 後端訊息走退回鏈（語系包 i4c 計畫 Task 2–8）：裝了 ja（上層 en-US）、啟用、預設 th 時，回應、驗證錯誤、middleware 與例外處理的訊息
/// 都沿「ja → 上層 → 預設 → 繁中」取；沒裝新語系時四語與改表前的基準檔逐字相同。
/// </summary>
[Trait("Category", "Postgres")]
public sealed class ServerMessageChainApiTests : IDisposable
{
    private static readonly Dictionary<string, Dictionary<string, string>> Texts = RequestMessagesTests.Baseline("message-report.json");

    private readonly TestApp _app = new();
    private readonly HttpClient _admin;

    public ServerMessageChainApiTests() => _admin = _app.AsUser();

    public void Dispose() => _app.Dispose();

    /// <summary>裝 ja（上層 <paramref name="parent"/>）、啟用 zh-TW／en-US／th／ja、預設 th（同 LocaleChainApiTests.Japanese）。</summary>
    private async Task Japanese(string? parent = "en-US")
    {
        await LocaleTestKit.InstallAsync(_app, "ja", "日本語", fallback: parent);
        (await _admin.PutAsJsonAsync($"{Base}/system/policies/locale.enabled", new { value = new[] { "zh-TW", "en-US", "th", "ja" } })).EnsureSuccessStatusCode();
        (await _admin.PutAsJsonAsync($"{Base}/system/policies/locale.default", new { value = "th" })).EnsureSuccessStatusCode();
    }

    private HttpClient Client(string locale, string? user = "admin")
    {
        var c = _app.AsUser(user);
        c.DefaultRequestHeaders.Add("X-Locale", locale);
        return c;
    }

    private static async Task<string> Message(HttpResponseMessage res, HttpStatusCode status)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(status, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("message").GetString()!;
    }

    private async Task<string> NotFoundMessage(string locale) =>
        await Message(await Client(locale).GetAsync($"{Base}/modules/{NewKey()}"), HttpStatusCode.NotFound);

    [Fact]
    public async Task NotFound_Ja_ParentEnglish()
    {
        await Japanese();
        var ja = await NotFoundMessage("ja");
        ja.Should().Be(await NotFoundMessage("en-US")).And.Be(Texts[MessageText.ModuleNotFound]["en-US"]);
    }

    [Fact]
    public async Task NotFound_JaNoParent_DefaultThai()
    {
        await Japanese(parent: null);
        var ja = await NotFoundMessage("ja");
        ja.Should().Be(await NotFoundMessage("th")).And.Be(Texts[MessageText.ModuleNotFound]["th"]);
    }

    [Fact]
    public async Task Validation_Ja_UsesChain()
    {
        await Japanese();
        async Task<string> Error(string locale)
        {
            var res = await Client(locale).PostAsJsonAsync($"{Base}/auth/change-password", new { oldPassword = "whatever-old", newPassword = "short" });
            var body = await res.Content.ReadAsStringAsync();
            res.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
            return JsonDocument.Parse(body).RootElement.GetProperty("errors").EnumerateObject()
                .Single(p => p.Name.Equals("newPassword", StringComparison.OrdinalIgnoreCase)).Value[0].GetString()!;
        }
        var ja = await Error("ja");
        ja.Should().Be(await Error("en-US"));
        ja.Should().StartWith(Texts[MessageText.NewPasswordTooShort]["en-US"].Split('{')[0]);
    }

    [Fact]
    public async Task SignInRequired_Ja_English()
    {
        await Japanese();
        (await Message(await Client("ja", user: null).GetAsync($"{Base}/menu"), HttpStatusCode.Unauthorized))
            .Should().Be(Texts[MessageText.SignInRequired]["en-US"]);
    }

    private sealed class ThrowingMediator : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default) => throw new DbUpdateConcurrencyException("race");
        public Task Send<TRequest>(TRequest request, CancellationToken ct = default) where TRequest : IRequest => throw new DbUpdateConcurrencyException("race");
        public Task<object?> Send(object request, CancellationToken ct = default) => throw new DbUpdateConcurrencyException("race");
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken ct = default) => throw new NotSupportedException();
        public Task Publish<TNotification>(TNotification notification, CancellationToken ct = default) where TNotification : INotification =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task Stale_Ja_English()
    {
        await Japanese();
        // 另一個主機（同一個資料庫）：語系與政策快照啟動後從資料庫讀，看得到上面裝的 ja
        using var factory = _app.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddScoped<IMediator, ThrowingMediator>()));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User", "admin");
        client.DefaultRequestHeaders.Add("X-Locale", "ja");

        (await Message(await client.GetAsync($"{Base}/modules"), HttpStatusCode.Conflict)).Should().Be(Texts[MessageText.Stale]["en-US"]);
    }

    [Fact]
    public async Task TaskReport_Ja_ParentEnglish()
    {
        await Japanese();
        var ja = Client("ja");
        var datasets = JsonDocument.Parse(await ja.GetStringAsync($"{Base}/reports/datasets")).RootElement;
        datasets.EnumerateArray().Select(d => d.GetProperty("name").GetString()).Should().Equal("Task", "Task event (created or completed)");

        var fields = JsonDocument.Parse(await ja.GetStringAsync($"{Base}/reports/datasets/tasks/catalog")).RootElement.GetProperty("fields").EnumerateArray().ToList();
        var status = fields.Single(f => f.GetProperty("id").GetInt64() == -2);
        status.GetProperty("label").GetString().Should().Be("Status");
        status.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("label").GetString()).Should().Equal("To do", "In progress", "Done", "Cancelled");
    }

    // ---- 記錄寫入路徑（i4c 計畫 Task 5）----

    private static async Task<JsonElement> Errors(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("errors");
    }

    private static string En(string key, params object[] args) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, Texts[key]["en-US"], args);

    [Fact]
    public async Task RecordRequired_Ja_English()
    {
        await Japanese();
        var key = await CreateModule(_admin);
        await Activate(_admin, key);
        var title = await TitleKey(_admin, key);

        var errors = await Errors(await Client("ja").PostAsJsonAsync($"{Base}/modules/{key}/records", new { values = new Dictionary<string, object>() }));
        errors.GetProperty(title)[0].GetString().Should().Be(En(MessageText.Required));
    }

    [Fact]
    public async Task FieldValue_Ja_English()
    {
        await Japanese();
        var key = await CreateModule(_admin);
        var qty = await AddField(_admin, key, "decimal");
        await Activate(_admin, key);
        var title = await TitleKey(_admin, key);

        var errors = await Errors(await Client("ja").PostAsJsonAsync($"{Base}/modules/{key}/records",
            new { values = new Dictionary<string, object> { [title] = "A", [qty.Key] = "abc" } }));
        errors.GetProperty(qty.Key)[0].GetString().Should().Be(En(MessageText.MustBeNumber));
    }

    [Fact]
    public async Task DetailTooMany_Ja_English()
    {
        await Japanese();
        var parent = await CreateModule(_admin);
        var child = await CreateModule(_admin);
        await AddField(_admin, child, "reference", new { refTarget = new { kind = "module", key = parent }, relation = "detail" });
        await Activate(_admin, parent);
        await Activate(_admin, child);
        (await _admin.PutAsJsonAsync($"{Base}/modules/{child}/policies/record.maxDetailRows", new { value = 1 })).EnsureSuccessStatusCode();
        var (parentTitle, childTitle) = (await TitleKey(_admin, parent), await TitleKey(_admin, child));

        object Row(string t) => new { values = new Dictionary<string, object> { [childTitle] = t } };
        var errors = await Errors(await Client("ja").PostAsJsonAsync($"{Base}/modules/{parent}/records", new
        {
            values = new Dictionary<string, object> { [parentTitle] = "H" },
            details = new Dictionary<string, object[]> { [child] = [Row("a"), Row("b")] }
        }));
        errors.GetProperty($"details.{child}")[0].GetString().Should().Be(En(MessageText.TooManyDetailRows, 1));
    }

    [Fact]
    public async Task WorkflowNotAllowed_Ja_English()
    {
        await Japanese();
        var key = await CreateModule(_admin);
        var status = await AddField(_admin, key, "enum");
        async Task<long> Option(string label) => (await JsonOf(await _admin.PostAsJsonAsync($"{Base}/fields/{status.Id}/options",
            new { label = new Dictionary<string, string> { ["zh-TW"] = label }, color = (string?)null }))).GetProperty("id").GetInt64();
        var (draft, done, cancelled) = (await Option("草稿"), await Option("完成"), await Option("作廢"));
        (await _admin.PutAsJsonAsync($"{Base}/modules/{key}/workflow", new
        {
            statusFieldId = status.Id, initialOptionId = draft,
            states = new object[] { new { optionId = draft }, new { optionId = done }, new { optionId = cancelled } },
            transitions = new[] { new { from = draft, to = done, groupIds = Array.Empty<long>() } }
        })).EnsureSuccessStatusCode();
        await Activate(_admin, key);
        var title = await TitleKey(_admin, key);
        var created = await JsonOf(await _admin.PostAsJsonAsync($"{Base}/modules/{key}/records", new { values = new Dictionary<string, object> { [title] = "A" } }));

        var errors = await Errors(await Client("ja").PutAsJsonAsync($"{Base}/records/{created.GetProperty("id").GetInt64()}", new
        {
            values = new Dictionary<string, object> { [status.Key] = $"opt_{cancelled}" },
            rowVersion = created.GetProperty("rowVersion").GetString()
        }));
        errors.GetProperty(status.Key)[0].GetString().Should().Be(En(MessageText.WorkflowNotAllowed));
    }

    [Fact]
    public async Task ConversionInputs_Ja_StillDetectsRequired()
    {
        await Japanese();
        var src = await CreateModule(_admin);
        var dst = await CreateModule(_admin);
        var from = await AddField(_admin, dst, "reference", new { refTarget = new { kind = "module", key = src }, readOnly = true });
        var must = await AddField(_admin, dst, "text", new { required = true });
        await Activate(_admin, src);
        await Activate(_admin, dst);
        var (srcTitle, dstTitle) = (await TitleKey(_admin, src), await TitleKey(_admin, dst));
        var conversion = "cv-" + Guid.NewGuid().ToString("N")[..8];
        var defined = await _admin.PostAsJsonAsync($"{Base}/conversions", new
        {
            key = conversion, name = new Dictionary<string, string> { ["zh-TW"] = "轉換" }, sourceModuleKey = src, targetModuleKey = dst, active = true,
            definition = new
            {
                sourceStates = Array.Empty<long>(),
                header = new { sourceFieldId = from.Id, copy = new[] { new { from = long.Parse(srcTitle[2..]), to = long.Parse(dstTitle[2..]) } } },
                lines = (object?)null
            }
        });
        defined.StatusCode.Should().Be(HttpStatusCode.Created, await defined.Content.ReadAsStringAsync());
        var record = await JsonOf(await _admin.PostAsJsonAsync($"{Base}/modules/{src}/records", new { values = new Dictionary<string, object> { [srcTitle] = "S" } }));
        var url = $"{Base}/records/{record.GetProperty("id").GetInt64()}/conversions/{conversion}/inputs";

        async Task<string[]> Inputs(string locale)
        {
            var res = await Client(locale).GetAsync(url);
            var body = await res.Content.ReadAsStringAsync();
            res.StatusCode.Should().Be(HttpStatusCode.OK, body);
            return JsonDocument.Parse(body).RootElement.EnumerateArray().Select(e => e.GetString()!).ToArray();
        }
        var zh = await Inputs("zh-TW");
        zh.Should().Equal(must.Key);
        (await Inputs("ja")).Should().Equal(zh);
    }

    // ---- 報表、儀表板、欄位值顯示（i4c 計畫 Task 6）----

    /// <summary>主模組（標題、金額、狀態列舉、是否）＋參照它的子模組；兩筆記錄，一筆沒有狀態（分組出現空白）、一筆有兩筆子記錄（去重說明）。</summary>
    private async Task<(string Main, string Child, long Link, long Amount, long Status, string BoolKey, long RecordId)> ReportWorld()
    {
        var main = await CreateModule(_admin);
        var amount = await AddField(_admin, main, "decimal", new { scale = 0 });
        var status = await AddField(_admin, main, "enum");
        var flag = await AddField(_admin, main, "boolean");
        var option = (await JsonOf(await _admin.PostAsJsonAsync($"{Base}/fields/{status.Id}/options",
            new { label = new Dictionary<string, string> { ["zh-TW"] = "新單" } }))).GetProperty("id").GetInt64();
        var child = await CreateModule(_admin);
        var link = await AddField(_admin, child, "reference", new { refTarget = new { kind = "module", key = main } });
        await Activate(_admin, main);
        await Activate(_admin, child);
        var (title, childTitle) = (await TitleKey(_admin, main), await TitleKey(_admin, child));
        async Task<long> Add(string module, Dictionary<string, object> values)
        {
            var res = await _admin.PostAsJsonAsync($"{Base}/modules/{module}/records", new { values });
            res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
            return (await JsonOf(res)).GetProperty("id").GetInt64();
        }
        var id = await Add(main, new() { [title] = "A", [amount.Key] = 100, [status.Key] = $"opt_{option}", [flag.Key] = true });
        await Add(main, new() { [title] = "B", [amount.Key] = 50, [flag.Key] = false });
        foreach (var t in new[] { "c1", "c2" }) await Add(child, new() { [childTitle] = t, [link.Key] = id });
        return (main, child, link.Id, amount.Id, status.Id, flag.Key, id);
    }

    private static object FieldRef(long id) => new { source = "main", fieldId = id, viaFieldId = (long?)null };

    private async Task<long> Report(string main, long status, object[] measures, object? child = null)
    {
        var res = await _admin.PostAsJsonAsync($"{Base}/reports", new
        {
            name = "報表",
            definition = new Dictionary<string, object?>
            {
                ["version"] = 1, ["mainModuleKey"] = main, ["child"] = child, ["columns"] = Array.Empty<object>(),
                ["groups"] = new object[] { new { field = FieldRef(status), bucket = (string?)null } }, ["measures"] = measures,
                ["filters"] = Array.Empty<object>(), ["primaryDateField"] = null, ["dateRange"] = null, ["sort"] = Array.Empty<object>(), ["viz"] = "table"
            }
        });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return (await JsonOf(res)).GetProperty("id").GetInt64();
    }

    private static object CountMeasure => new { id = "n", kind = "countMain", field = (object?)null, condition = (object?)null };

    [Fact]
    public async Task ReportRun_Ja_EnglishLabels()
    {
        await Japanese();
        var w = await ReportWorld();
        var id = await Report(w.Main, w.Status, [CountMeasure]);

        var res = await Client("ja").PostAsJsonAsync($"{Base}/reports/{id}/run", new { range = (object?)null });
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var run = JsonDocument.Parse(body).RootElement;
        run.GetProperty("columns").EnumerateArray().Single(c => c.GetProperty("key").GetString() == "n").GetProperty("label").GetString()
            .Should().Be(En(ReportText.Count, "測試模組"));
        run.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("groupLabels")[0].GetString()).Should().Contain(En(ReportText.Blank));
        run.GetProperty("rowMeaning").GetString().Should().Be(En(ReportText.RowMeaning, "測試模組"));
    }

    [Fact]
    public async Task ReportExport_Ja_TotalRowEnglish()
    {
        await Japanese();
        var w = await ReportWorld();
        var id = await Report(w.Main, w.Status, [new { id = "s", kind = "sum", field = FieldRef(w.Amount), condition = (object?)null }],
            child: new { moduleKey = w.Child, linkFieldId = w.Link });

        var res = await Client("ja").GetAsync($"{Base}/reports/{id}/export");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        using var wb = new XLWorkbook(await res.Content.ReadAsStreamAsync());
        var cells = wb.Worksheet(1).CellsUsed().Select(c => c.GetString()).ToList();
        cells.Should().Contain(En(ReportText.ExportTotal)).And.Contain(En(ReportText.DedupedNote));
    }

    [Fact]
    public async Task DashboardCard_Ja_UnsharedEnglish()
    {
        await Japanese();
        var w = await ReportWorld();
        var report = await Report(w.Main, w.Status, [CountMeasure]);
        var sales = _app.AsUser("sales01");
        _app.GrantBusinessGroups(w.Main);
        (await _admin.PutAsJsonAsync($"{Base}/reports/{report}/shares", new { groupIds = new[] { DashboardTestKit.GroupId(_app, "sales") } })).EnsureSuccessStatusCode();
        var dashboard = (await JsonOf(await sales.PostAsJsonAsync(DashboardTestKit.Dashboards,
            new { name = "我的", layout = DashboardTestKit.Layout(DashboardTestKit.Card("c", report)) }))).GetProperty("id").GetInt64();
        (await _admin.PutAsJsonAsync($"{Base}/reports/{report}/shares", new { groupIds = Array.Empty<long>() })).EnsureSuccessStatusCode();

        var ja = Client("ja", "sales01");
        var result = await DashboardTestKit.RunCard(ja, dashboard, "c");
        result.GetProperty("state").GetString().Should().Be("unshared");
        result.GetProperty("message").GetString().Should().Be(En(ReportText.Unshared));
    }

    [Fact]
    public async Task RecordExport_Ja_BooleanEnglish()
    {
        await Japanese();
        var w = await ReportWorld();
        var res = await Client("ja").GetAsync($"{Base}/modules/{w.Main}/export?format=csv");
        var csv = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, csv);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
        lines.Should().Contain(l => l.Contains("," + En(ReportText.Yes))).And.Contain(l => l.Contains("," + En(ReportText.No)));
        csv.Should().NotContain(Texts[ReportText.Yes]["zh-TW"]);
    }

    [Fact]
    public async Task Print_Ja_BooleanEnglish()
    {
        await Japanese();
        var w = await ReportWorld();
        var title = await TitleKey(_admin, w.Main);
        var put = await _admin.PutAsJsonAsync($"{Base}/records/{w.RecordId}", new
        {
            values = new Dictionary<string, object> { [title] = "<script>x</script>" },
            rowVersion = (await JsonOf(await _admin.GetAsync($"{Base}/records/{w.RecordId}"))).GetProperty("rowVersion").GetString()
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        var template = await _admin.PostAsJsonAsync($"{Base}/modules/{w.Main}/print-templates", new
        {
            key = "pt", name = new Dictionary<string, string> { ["zh-TW"] = "列印" }, body = $"<p>{{{{{w.BoolKey}}}}}</p><p>{{{{{title}}}}}</p>", active = true
        });
        template.IsSuccessStatusCode.Should().BeTrue(await template.Content.ReadAsStringAsync());

        var res = await Client("ja").GetAsync($"{Base}/records/{w.RecordId}/print/pt");
        var html = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, html);
        html.Should().Contain($"<p>{En(ReportText.Yes)}</p>").And.NotContain("<script>x</script>");
    }

    [Fact]
    public async Task Factory_FourLocales_Unchanged()
    {
        foreach (var l in new[] { "zh-TW", "en-US", "th", "vi" })
            (await NotFoundMessage(l)).Should().Be(Texts[MessageText.ModuleNotFound][l], l);
    }

    [Fact]
    public void Catalog_SingletonUnderScopeValidation()
    {
        // WebApplicationFactory 預設 Development：主機建置時已開 ValidateScopes／ValidateOnBuild（從 root 取 scoped 服務會丟例外，證明有開）
        _app.Services.GetRequiredService<IHostEnvironment>().IsDevelopment().Should().BeTrue();
        var scoped = () => _app.Services.GetRequiredService<RequestLocale>();
        scoped.Should().Throw<InvalidOperationException>();

        var root = _app.Services.GetRequiredService<IMessageCatalog>();
        using var a = _app.Services.CreateScope();
        using var b = _app.Services.CreateScope();
        root.Should().BeOfType<MessageCatalog>();
        a.ServiceProvider.GetRequiredService<IMessageCatalog>().Should().BeSameAs(root);
        b.ServiceProvider.GetRequiredService<IMessageCatalog>().Should().BeSameAs(root);
    }
}
