using System.Net;
using System.Text.Json;
using DynamicModules.Application.Common.Localization;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;
using static DynamicModules.WebAPITests.LocaleTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>JSON 翻譯樣板匯出 API（i4d2 計畫 Task 3、判斷 9）：附件形狀、往返匯入、驗證 400、權限、Demo 可匯出、空系統。</summary>
[Trait("Category", "Postgres")]
public class LocaleTemplateApiTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private const string Both = "scopes=ui&scopes=server";

    private static Task<HttpResponseMessage> Export(HttpClient c, string code, string query = Both, string? locale = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"{I18n}/locales/{code}/template?{query}");
        if (locale is not null) req.Headers.Add("X-Locale", locale);
        return c.SendAsync(req);
    }

    private static async Task<byte[]> ExportOk(HttpClient c, string code, string query = Both)
    {
        var res = await Export(c, code, query);
        var bytes = await res.Content.ReadAsByteArrayAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, System.Text.Encoding.UTF8.GetString(bytes));
        return bytes;
    }

    private static async Task<string> FileError(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, text);
        var body = JsonDocument.Parse(text).RootElement;
        body.GetProperty("code").GetString().Should().Be("localePackInvalid");
        return body.GetProperty("errors").GetProperty("file")[0].GetString()!;
    }

    private static int Int(JsonElement report, string segment, string name) => report.GetProperty(segment).GetProperty(name).GetInt32();

    [Fact]
    public async Task Export_Builtin_Th()
    {
        var res = await Export(_app.AsUser(), "th");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        res.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        res.Content.Headers.ContentDisposition.FileName!.Trim('"').Should().Be("th.langpack.json");
        var root = JsonDocument.Parse(await res.Content.ReadAsByteArrayAsync()).RootElement;
        root.GetProperty("format").GetString().Should().Be("formwright-langpack");
        root.GetProperty("formatVersion").GetInt32().Should().Be(1);
        root.GetProperty("pack").GetProperty("builtFor").GetString().Should().Be("dev");
    }

    [Fact]
    public async Task Export_ThenImport_RoundTrip()
    {
        var admin = _app.AsUser();
        var th = await PreviewOkAsync(admin, await ExportOk(admin, "th"));
        foreach (var segment in new[] { "ui", "server" })
        {
            (Int(th, segment, "rejected"), Int(th, segment, "extra")).Should().Be((0, 0), segment);
            var total = segment == "ui" ? BuiltinLocaleResources.BaseKeys.Count : BuiltinServerText.BaseKeys.Count;
            (Int(th, segment, "unchanged") + Int(th, segment, "sameAsBuiltin") + Int(th, segment, "skipped")).Should().Be(total, segment);
        }

        var id = await PreviewOkAsync(admin, await ExportOk(admin, "id", Both + "&nativeName=" + Uri.EscapeDataString("Bahasa Indonesia") + "&fallback=en-US"));
        id.GetProperty("locale").GetProperty("isNew").GetBoolean().Should().BeTrue();
        id.GetProperty("locale").GetProperty("fallbackCode").GetString().Should().Be("en-US");
        id.GetProperty("locale").GetProperty("nativeName").GetString().Should().Be("Bahasa Indonesia");
        Int(id, "ui", "skipped").Should().Be(BuiltinLocaleResources.BaseKeys.Count);
        Int(id, "server", "skipped").Should().Be(BuiltinServerText.BaseKeys.Count);
    }

    [Fact]
    public async Task Export_Invalid_400()
    {
        var admin = _app.AsUser();
        (await FileError(await Export(admin, "JA_x"))).Should().Be("語系代碼「JA_x」格式不對（例：ja、zh-Hant-HK）");
        (await FileError(await Export(admin, "id"))).Should().Be("新語系要填自稱（locale.nativeName）");
        (await FileError(await Export(admin, "id", Both + "&nativeName=Bahasa&fallback=ko"))).Should().Be("上層語系「ko」還沒安裝");
        (await FileError(await Export(admin, "th", "scopes=ui&scopes=designData"))).Should().Be("匯出範圍只能是 ui、server，至少要選一個");
        (await FileError(await Export(admin, "th", "scopes=ui&scopes=designData", "en-US")))
            .Should().Be("The export scope must be ui and/or server, with at least one selected");
        (await FileError(await Export(admin, "th", Both + "&reference=ko"))).Should().Be("語系 ko 沒有安裝");
    }

    [Fact]
    public async Task Export_NonAdmin_403()
    {
        (await Export(_app.AsUser("sales01"), "th")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Export(_app.AsUser(null), "th")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Export_DemoOff_Allowed()
    {
        using var app = new TestApp();
        app.Settings["Localization:PackImportEnabled"] = "false";
        (await Export(app.AsUser(), "th")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Export_EmptySystem()
    {
        using var app = new TestApp(seedDemoPeople: false);
        var root = JsonDocument.Parse(await ExportOk(app.AsUser(), "th")).RootElement;
        root.GetProperty("ui").EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(BuiltinLocaleResources.BaseKeys);
    }

    [Fact]
    public async Task Export_LongPlatformVersion_ReimportNoWarning()
    {
        using var app = new TestApp();
        LocalePackPreviewApiTests.UseVersion(app, "2026.11.21-build.123456");
        var r = await PreviewOkAsync(app.AsUser(), await ExportOk(app.AsUser(), "th"));
        r.GetProperty("pack").GetProperty("builtFor").GetString().Should().Be("2026.11.21-build.123");
        r.GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("code").GetString()).Should().NotContain("builtForDiffers");
    }

    [Fact]
    public async Task Export_NewCode_FallbackTooDeep_400()
    {
        await InstallAsync(_app, "xa", "XA", "en-US");
        await InstallAsync(_app, "xb", "XB", "xa");
        await InstallAsync(_app, "xc", "XC", "xb");
        (await FileError(await Export(_app.AsUser(), "id", Both + "&nativeName=Bahasa&fallback=xc"))).Should().Be("上層語系最多 3 層");
        await ExportOk(_app.AsUser(), "id", Both + "&nativeName=Bahasa&fallback=xb");
    }

    [Fact]
    public async Task Export_OverlayWithScript_IsData()
    {
        const string script = "<script>alert(1)</script> หน้าแรก";
        await ApplyPackAsync(_app, Pack("th", ui: new Dictionary<string, string> { ["menu.home"] = script }));
        var res = await Export(_app.AsUser(), "th", "scopes=ui");
        res.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        res.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        var root = JsonDocument.Parse(await res.Content.ReadAsByteArrayAsync()).RootElement;
        root.GetProperty("ui").GetProperty("menu.home").GetString().Should().Be(script);
        root.TryGetProperty("server", out _).Should().BeFalse();
    }

    // ── controller 層查詢參數契約（i4d2 後端審查建議 2） ──

    [Fact]
    public async Task Query_NoScopes_400_NotDefaultedToBoth()
    {
        (await FileError(await Export(_app.AsUser(), "th", "onlyMissing=false"))).Should().Be("匯出範圍只能是 ui、server，至少要選一個");
    }

    [Fact]
    public async Task Query_EmptyReference_OmitsReferenceSection_UnsetKeepsDefault()
    {
        var admin = _app.AsUser();
        var none = JsonDocument.Parse(await ExportOk(admin, "th", Both + "&reference=")).RootElement;
        none.TryGetProperty("reference", out _).Should().BeFalse("reference=（空字串）代表不附參考段，不能被 binder 當成未帶");
        none.GetProperty("source").GetProperty("locale").GetString().Should().Be("zh-TW");

        var unset = JsonDocument.Parse(await ExportOk(admin, "th")).RootElement;
        unset.GetProperty("reference").GetProperty("locale").GetString().Should().Be("en-US");
    }

    [Fact]
    public async Task Query_SourceAndReference_NotNormalized_400()
    {
        var admin = _app.AsUser();
        (await FileError(await Export(admin, "th", Both + "&reference=en-us"))).Should().Be("語系 en-us 沒有安裝");
        (await FileError(await Export(admin, "th", Both + "&source=ZH-tw"))).Should().Be("語系 ZH-tw 沒有安裝");
    }

    [Fact]
    public async Task Query_InstalledTarget_IgnoresFallbackAndQuasarLang()
    {
        var root = JsonDocument.Parse(await ExportOk(_app.AsUser(), "th", Both + "&fallback=nope&quasarLang=zz&nativeName=Ignored")).RootElement;
        var locale = root.GetProperty("locale");
        locale.GetProperty("code").GetString().Should().Be("th");
        locale.GetProperty("nativeName").GetString().Should().NotBe("Ignored");
        locale.GetProperty("quasarLang").GetString().Should().NotBe("zz");
        (locale.TryGetProperty("fallback", out var fb) ? fb.ValueKind : JsonValueKind.Null).Should().Be(JsonValueKind.Null);
    }
}
