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
