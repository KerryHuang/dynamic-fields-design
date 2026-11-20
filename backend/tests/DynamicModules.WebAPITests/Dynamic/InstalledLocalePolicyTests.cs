using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>語系類政策的選項跟著已安裝語系（語系包設計 §4.3；i4a 計畫判斷 2、11）。雙跑。</summary>
[Trait("Category", "Postgres")]
public sealed class InstalledLocalePolicyTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private static Task<HttpResponseMessage> PutSystem(HttpClient c, string code, object? value) =>
        c.PutAsJsonAsync($"{Base}/system/policies/{code}", new { value });

    private async Task<string[]> OptionsOf(string code)
    {
        var list = await JsonOf(await _app.AsUser().GetAsync($"{Base}/system/policies"));
        return list.EnumerateArray().Single(e => e.GetProperty("code").GetString() == code)
            .GetProperty("options").EnumerateArray().Select(e => e.GetString()!).ToArray();
    }

    private async Task<JsonElement> AuthOptions() => await JsonOf(await _app.AsUser(null).GetAsync($"{Base}/auth/options"));

    [Fact]
    public async Task Options_FollowInstalledTable()
    {
        await LocaleTestKit.InstallAsync(_app, "ja", "日本語", fallback: "en-US");
        var admin = _app.AsUser();

        (await OptionsOf("locale.enabled")).Should().Equal("zh-TW", "en-US", "th", "vi", "ja");
        (await PutSystem(admin, "locale.default", "zh-TW")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PutSystem(admin, "locale.requiredText", new[] { "zh-TW" })).StatusCode.Should().Be(HttpStatusCode.OK);
        var enabled = await PutSystem(admin, "locale.enabled", new[] { "zh-TW", "ja" });
        enabled.StatusCode.Should().Be(HttpStatusCode.OK, await enabled.Content.ReadAsStringAsync());
        var def = await PutSystem(admin, "locale.default", "ja");
        def.StatusCode.Should().Be(HttpStatusCode.OK, await def.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UninstalledLocale_Rejected()
    {
        var res = await PutSystem(_app.AsUser(), "locale.enabled", new[] { "zh-TW", "ja" });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(res)).GetProperty("errors").GetProperty("value")[0].GetString().Should().Be("只能從以下語系挑選：zh-TW、en-US、th、vi");
    }

    [Fact]
    public async Task RemovedLocale_TreatedAsDisabled()
    {
        await LocaleTestKit.InstallAsync(_app, "ja", "日本語");
        (await PutSystem(_app.AsUser(), "locale.enabled", new[] { "zh-TW", "ja" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await AuthOptions()).GetProperty("enabledLocales").EnumerateArray().Select(e => e.GetString()).Should().Equal("zh-TW", "ja");

        await LocaleTestKit.RemoveAsync(_app, "ja");

        var res = await _app.AsUser(null).GetAsync($"{Base}/auth/options");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(res)).GetProperty("enabledLocales").EnumerateArray().Select(e => e.GetString()).Should().Equal(["zh-TW"]);
        _app.Logs.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("locale.enabled") && e.Message.Contains("ja"));
    }

    [Fact]
    public async Task NoLocalePack_SameAsP3()
    {
        (await OptionsOf("locale.enabled")).Should().Equal("zh-TW", "en-US", "th", "vi");
        (await OptionsOf("locale.default")).Should().Equal("zh-TW", "en-US", "th", "vi");
        var body = await AuthOptions();
        body.GetProperty("defaultLocale").GetString().Should().Be("zh-TW");
        body.GetProperty("enabledLocales").EnumerateArray().Select(e => e.GetString()).Should().Equal("zh-TW", "en-US", "th", "vi");
    }
}
