using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Application.Common.Localization;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// <c>GET i18n/ui/{code}</c>（語系包設計 §8.2、§19；i4b 計畫判斷 6、8、9）：伺服器沿退回鏈補齊後回扁平 JSON，
/// 依請求的 <c>v</c> 是否等於這台的雜湊設快取標頭；匿名只取得到啟用語系，登入可取已安裝未啟用（試看）。
/// </summary>
[Trait("Category", "Postgres")]
public class I18nUiApiTests
{
    private const string Ui = "/api/v3/dynamic/i18n/ui";

    private static async Task<JsonElement> Json(HttpResponseMessage res) => await res.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<Dictionary<string, string>> Flat(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<Dictionary<string, string>>())!;

    private static string Hash(HttpResponseMessage res) => res.Headers.GetValues("X-UI-Hash").Single();

    private static string CacheControl(HttpResponseMessage res) => res.Headers.CacheControl!.ToString();

    private static async Task<Dictionary<string, string>> HashesOf(HttpClient c, string url) =>
        (await Json(await c.GetAsync(url))).GetProperty("locales").EnumerateArray()
            .ToDictionary(l => l.GetProperty("code").GetString()!, l => l.GetProperty("uiHash").GetString()!);

    private static async Task Put(TestApp app, string code, object value) =>
        (await app.AsUser().PutAsJsonAsync($"/api/v3/dynamic/system/policies/{code}", new { value })).StatusCode.Should().Be(HttpStatusCode.OK);

    [Fact]
    public async Task Options_UiHash_EqualsHeader()
    {
        using var app = new TestApp();
        await LocaleTestKit.InstallAsync(app, "ja", "日本語", fallback: "en-US");
        var anon = app.AsUser(null);
        var user = app.AsUser("sales01");

        var options = await HashesOf(anon, "/api/v3/dynamic/auth/options");
        options.Keys.Should().Equal("zh-TW", "en-US", "th", "vi");
        foreach (var (code, hash) in options) Hash(await anon.GetAsync($"{Ui}/{code}")).Should().Be(hash, code);

        var effective = await HashesOf(user, "/api/v3/dynamic/policies/effective");
        effective.Keys.Should().Contain("ja");
        foreach (var (code, hash) in effective) Hash(await user.GetAsync($"{Ui}/{code}")).Should().Be(hash, code);
    }

    [Fact]
    public async Task Anonymous_Enabled_ReturnsMergedFlat()
    {
        using var app = new TestApp();
        var res = await app.AsUser(null).GetAsync($"{Ui}/th");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        res.Content.Headers.ContentType.CharSet.Should().Be("utf-8");
        var body = await Flat(res);
        body.Should().HaveCount(BuiltinLocaleResources.BaseKeys.Count);
        body.Keys.Should().BeInAscendingOrder(StringComparer.Ordinal);
        body["menu.home"].Should().Be(BuiltinLocaleResources.Ui["th"]["menu.home"]);
        Hash(res).Should().Be((await HashesOf(app.AsUser(null), "/api/v3/dynamic/auth/options"))["th"]);
    }

    [Fact]
    public async Task MatchingV_Immutable()
    {
        using var app = new TestApp();
        var anon = app.AsUser(null);
        var hash = Hash(await anon.GetAsync($"{Ui}/th"));

        var res = await anon.GetAsync($"{Ui}/th?v={hash}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Headers.CacheControl!.Public.Should().BeTrue();
        res.Headers.CacheControl.MaxAge.Should().Be(TimeSpan.FromSeconds(31536000));
        CacheControl(res).Should().Contain("immutable");
    }

    [Fact]
    public async Task MissingOrStaleV_NoStore()
    {
        using var app = new TestApp();
        var anon = app.AsUser(null);
        var current = (await HashesOf(anon, "/api/v3/dynamic/auth/options"))["th"];

        // 多台競態：前端拿到別台的新雜湊、打到這台，回這台目前的內容與雜湊，不可被快取
        foreach (var url in new[] { $"{Ui}/th", $"{Ui}/th?v=0000000000000000" })
        {
            var res = await anon.GetAsync(url);
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            res.Headers.CacheControl!.NoStore.Should().BeTrue(url);
            Hash(res).Should().Be(current);
            (await Flat(res))["menu.home"].Should().Be(BuiltinLocaleResources.Ui["th"]["menu.home"]);
        }
    }

    [Fact]
    public async Task Anonymous_NotEnabled_NotInstalled_InvalidCode_SameNotFound()
    {
        using var app = new TestApp();
        await LocaleTestKit.InstallAsync(app, "ja", "日本語");
        var anon = app.AsUser(null);

        var bodies = new List<string>();
        foreach (var code in new[] { "ja", "ko", "JA", "zh_TW", "..%2Fx" })
        {
            var res = await anon.GetAsync($"{Ui}/{code}");
            res.StatusCode.Should().Be(HttpStatusCode.NotFound, code);
            res.Headers.Contains("X-UI-Hash").Should().BeFalse(code);
            bodies.Add(await res.Content.ReadAsStringAsync());
        }
        bodies.Distinct().Should().ContainSingle("不讓匿名者分辨未啟用、未安裝與格式不合法");
    }

    [Fact]
    public async Task SignedIn_NotEnabled_PrivateNoCache()
    {
        using var app = new TestApp();
        await LocaleTestKit.InstallAsync(app, "ja", "日本語", fallback: "en-US");
        var user = app.AsUser("sales01");
        var hash = Hash(await user.GetAsync($"{Ui}/ja"));

        foreach (var url in new[] { $"{Ui}/ja", $"{Ui}/ja?v={hash}" })
        {
            var res = await user.GetAsync(url);
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            res.Headers.CacheControl!.Private.Should().BeTrue(url);
            res.Headers.CacheControl.NoCache.Should().BeTrue(url);
            CacheControl(res).Should().NotContain("immutable");
        }
    }

    [Fact]
    public async Task SignedIn_Ja_ParentThenDefault()
    {
        using var app = new TestApp();
        await LocaleTestKit.InstallAsync(app, "ja", "日本語", fallback: "en-US");
        var user = app.AsUser("sales01");
        var withParent = await user.GetAsync($"{Ui}/ja");
        (await Flat(withParent)).Should().Equal(BuiltinLocaleResources.Ui["en-US"]);

        await LocaleTestKit.RemoveAsync(app, "ja");
        await LocaleTestKit.InstallAsync(app, "ja", "日本語");
        await Put(app, "locale.default", "th");
        var noParent = await user.GetAsync($"{Ui}/ja");

        (await Flat(noParent)).Should().Equal(BuiltinLocaleResources.Ui["th"]);
        Hash(noParent).Should().NotBe(Hash(withParent));
    }

    [Fact]
    public async Task ExpiredCookie_TreatedAsAnonymous()
    {
        using var app = new TestApp();
        await LocaleTestKit.InstallAsync(app, "ja", "日本語");
        var ghost = app.AsUser("ghost");

        var th = await ghost.GetAsync($"{Ui}/th");
        th.StatusCode.Should().Be(HttpStatusCode.OK);
        th.Headers.Contains("Set-Cookie").Should().BeFalse();
        (await ghost.GetAsync($"{Ui}/ja")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task MustChangePassword_CanFetch()
    {
        using var app = new TestApp();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            db.Set<AppUser>().Single(u => u.UserId == "sales01").MustChangePassword = true;
            db.SaveChanges();
        }
        var user = app.AsUser("sales01");
        (await user.GetAsync("/api/v3/dynamic/menu")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "前提：強制改密碼期間一般端點被擋");

        (await user.GetAsync($"{Ui}/zh-TW")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ZhTw_AlsoServed()
    {
        using var app = new TestApp();
        var res = await app.AsUser(null).GetAsync($"{Ui}/zh-TW");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Flat(res)).Should().Equal(BuiltinLocaleResources.Ui["zh-TW"]);
    }
}
