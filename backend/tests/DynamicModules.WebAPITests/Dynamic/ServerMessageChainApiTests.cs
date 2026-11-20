using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Localization;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 後端訊息走退回鏈（語系包 i4c 計畫 Task 2–4）：裝了 ja（上層 en-US）、啟用、預設 th 時，回應、驗證錯誤、middleware 與例外處理的訊息
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
