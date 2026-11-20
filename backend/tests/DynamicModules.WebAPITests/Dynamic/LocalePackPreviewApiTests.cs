using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Application.Dynamic.OpsService;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.WebAPI.Controllers.v3.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;
using static DynamicModules.WebAPITests.LocaleTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>語系包預覽 API（語系包設計 §6.3；i4d1 計畫 Task 6、判斷 12、13、15）：權限、報告、暫存（10 分鐘、最多 5 筆）、大小上限、Demo 不暫存。</summary>
[Trait("Category", "Postgres")]
public class LocalePackPreviewApiTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(RepoRoot(), "backend", "tests", "Fixtures", "langpacks", name));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "shared", "locale-chain-cases.json"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static Task<HttpResponseMessage> Preview(HttpClient c, byte[] body, string fileName = "t.langpack.json", string? locale = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/i18n/packs/preview?fileName={Uri.EscapeDataString(fileName)}") { Content = new ByteArrayContent(body) };
        if (locale is not null) req.Headers.Add("X-Locale", locale);
        return c.SendAsync(req);
    }

    private static T Db<T>(TestApp app, Func<DynamicDbContext, T> read)
    {
        using var scope = app.Services.CreateScope();
        return read(scope.ServiceProvider.GetRequiredService<DynamicDbContext>());
    }

    private static async Task<string> FileError(HttpResponseMessage res)
    {
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await JsonOf(res);
        body.GetProperty("code").GetString().Should().Be("localePackInvalid");
        return body.GetProperty("errors").GetProperty("file")[0].GetString()!;
    }

    [Fact]
    public async Task NonAdmin_403()
    {
        (await Preview(_app.AsUser("sales01"), Fixture("ja-partial.langpack.json"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Preview(_app.AsUser(null), Fixture("ja-partial.langpack.json"))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        Db(_app, db => db.Set<LocalePackPreview>().Count()).Should().Be(0);
    }

    /// <summary>一般使用者在讀主體前就被擋（審查建議 2）：伺服器沒讀走任何位元組，不為非管理員配置上傳緩衝。</summary>
    [Fact]
    public async Task NonAdmin_403_BodyNotRead()
    {
        _app.ResetBodyCount();
        var res = await Preview(_app.AsUser("sales01"), Fixture("ja-partial.langpack.json"));
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await JsonOf(res)).GetProperty("message").GetString().Should().NotBeNullOrEmpty();
        _app.BodyBytesRead.Should().Be(0);
        Db(_app, db => db.Set<LocalePackPreview>().Count()).Should().Be(0);
    }

    [Fact]
    public async Task Preview_NewLocale_ReportAndStored()
    {
        var localesBefore = Db(_app, db => db.Set<InstalledLocale>().Count());
        var res = await Preview(_app.AsUser(), Fixture("ja-partial.langpack.json"), "C:\\fakepath\\ja-partial.langpack.json");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var r = await JsonOf(res);
        var previewId = r.GetProperty("previewId").GetString();
        previewId.Should().NotBeNullOrEmpty();
        r.GetProperty("expiresUtc").GetDateTime().ToUniversalTime().Should().BeCloseTo(_app.Time.GetUtcNow().UtcDateTime.AddMinutes(10), TimeSpan.FromSeconds(1));
        r.GetProperty("importEnabled").GetBoolean().Should().BeTrue();
        r.GetProperty("fileName").GetString().Should().Be("ja-partial.langpack.json");
        r.GetProperty("fileHash").GetString().Should().HaveLength(64);
        r.GetProperty("locale").GetProperty("isNew").GetBoolean().Should().BeTrue();
        r.GetProperty("locale").GetProperty("quasarLang").ValueKind.Should().Be(JsonValueKind.Null);
        r.GetProperty("ui").GetProperty("added").GetInt32().Should().Be(30);
        r.GetProperty("completeness").GetProperty("ui").GetProperty("have").GetInt32().Should().Be(30);

        // 另一個 scope：真的存進資料庫，不只在 tracking 裡
        var row = Db(_app, db => db.Set<LocalePackPreview>().AsNoTracking().Single(p => p.Id == previewId));
        row.UserId.Should().Be("admin");
        row.Code.Should().Be("ja");
        row.LocaleFingerprint.Should().BeNull();
        row.CurrentPackId.Should().BeNull();
        row.PlanJson.Should().Contain("\"code\":\"ja\"");
        Db(_app, db => db.Set<InstalledLocale>().Count()).Should().Be(localesBefore);
        Db(_app, db => db.Set<LocalePack>().Count()).Should().Be(0);

        // 預覽真的存了檔：用回傳的 previewId 套用成功（Task 7 補上）
        var apply = await _app.AsUser().PostAsJsonAsync($"{Base}/i18n/packs/apply", new { previewId, fileHash = r.GetProperty("fileHash").GetString() });
        apply.StatusCode.Should().Be(HttpStatusCode.OK, await apply.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Preview_Bad_400()
    {
        var admin = _app.AsUser();
        (await FileError(await Preview(admin, "not json"u8.ToArray()))).Should().Be("檔案不是語系包（.langpack.json）");
        (await FileError(await Preview(admin, [0x50, 0x4B, 0x03, 0x04, 0, 0]))).Should().Be("本版只收 .langpack.json，Excel 翻譯樣板之後支援");
        (await FileError(await Preview(admin, Fixture("rtl.langpack.json")))).Should().Be("本版不支援由右至左的語言");
        var v2 = "{\"format\":\"formwright-langpack\",\"formatVersion\":2,\"locale\":{\"code\":\"ja\"}}"u8.ToArray();
        (await FileError(await Preview(admin, v2))).Should().Be("這份語系包的格式版本 2 比系統支援的 1 新");
        (await FileError(await Preview(admin, v2, locale: "en-US"))).Should().Be("This language pack uses format version 2, which is newer than the supported version 1");
        Db(_app, db => db.Set<LocalePackPreview>().Count()).Should().Be(0);
    }

    /// <summary>既有語系帶 "ui":null、"server":null 不能當成沒變更（審查必修 3）：回 400、不暫存。</summary>
    [Fact]
    public async Task Preview_NullSection_400()
    {
        var admin = _app.AsUser();
        foreach (var section in new[] { "ui", "server" })
        {
            var body = System.Text.Encoding.UTF8.GetBytes(
                "{\"format\":\"formwright-langpack\",\"formatVersion\":1,\"locale\":{\"code\":\"en-US\"},\"" + section + "\":null}");
            (await FileError(await Preview(admin, body))).Should().Be($"{section} 段必須是 JSON 物件");
        }
        Db(_app, db => db.Set<LocalePackPreview>().Count()).Should().Be(0);
    }

    [Fact]
    public async Task Preview_TooLarge()
    {
        var admin = _app.AsUser();
        (await admin.PutAsJsonAsync($"{Base}/system/policies/locale.packMaxBytes", new { value = 2048 })).StatusCode.Should().Be(HttpStatusCode.OK);
        var big = new byte[3 * 1024];
        Array.Fill(big, (byte)' ');
        "{}"u8.CopyTo(big);
        (await FileError(await Preview(admin, big))).Should().Be("語系包不能超過 0.002 MB");

        var limit = 2 * 2048 + UploadGuard.MultipartOverhead;
        _app.ResetBodyCount();
        (await Preview(admin, new byte[limit + 1])).StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        _app.BodyBytesRead.Should().Be(0, "Content-Length 超過就不讀主體");

        _app.ResetBodyCount();
        var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/i18n/packs/preview")
        {
            Content = new StreamContent(new UnknownLengthStream(limit * 4)),
        };
        (await admin.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        _app.BodyBytesRead.Should().BeLessThan(limit * 2, "沒有 Content-Length 的串流讀到上限就停");
        Db(_app, db => db.Set<LocalePackPreview>().Count()).Should().Be(0);
    }

    [Fact]
    public async Task Preview_DemoOff_NotStored()
    {
        foreach (var settings in new[] { ("Localization:PackImportEnabled", "false"), ("Demo:LoginEnabled", "true") })
        {
            using var app = new TestApp();
            app.Settings[settings.Item1] = settings.Item2;
            var res = await Preview(app.AsUser(), Fixture("ja-partial.langpack.json"));
            res.StatusCode.Should().Be(HttpStatusCode.OK, settings.Item1);
            var r = await JsonOf(res);
            r.TryGetProperty("previewId", out var id).Should().BeTrue("previewId 要明示輸出 null");
            id.ValueKind.Should().Be(JsonValueKind.Null);
            r.TryGetProperty("expiresUtc", out var exp).Should().BeTrue();
            exp.ValueKind.Should().Be(JsonValueKind.Null);
            r.GetProperty("importEnabled").GetBoolean().Should().BeFalse();
            r.GetProperty("ui").GetProperty("added").GetInt32().Should().Be(30);
            Db(app, db => db.Set<LocalePackPreview>().Count()).Should().Be(0);
        }
    }

    [Fact]
    public async Task Preview_Cap5_OldestDropped()
    {
        var admin = _app.AsUser();
        var ids = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            _app.Time.Advance(TimeSpan.FromSeconds(1));
            ids.Add((await JsonOf(await Preview(admin, Fixture("th-fix.langpack.json")))).GetProperty("previewId").GetString()!);
        }
        var stored = Db(_app, db => db.Set<LocalePackPreview>().Select(p => p.Id).ToList());
        stored.Should().HaveCount(5).And.NotContain(ids[0]).And.Contain(ids[5]);
    }

    [Fact]
    public async Task Preview_Expired_Pruned()
    {
        var admin = _app.AsUser();
        var first = (await JsonOf(await Preview(admin, Fixture("th-fix.langpack.json")))).GetProperty("previewId").GetString();
        _app.Time.Advance(TimeSpan.FromMinutes(11));
        var second = (await JsonOf(await Preview(admin, Fixture("th-fix.langpack.json")))).GetProperty("previewId").GetString();
        Db(_app, db => db.Set<LocalePackPreview>().Select(p => p.Id).ToList()).Should().Equal(second);
        first.Should().NotBe(second);
    }

    [Fact]
    public async Task Preview_ExistingBuiltin_FingerprintAndOverridesCounted()
    {
        var r = await JsonOf(await Preview(_app.AsUser(), Fixture("th-fix.langpack.json")));
        r.GetProperty("locale").GetProperty("builtin").GetBoolean().Should().BeTrue();
        r.GetProperty("ui").GetProperty("sameAsBuiltin").GetInt32().Should().Be(1);
        var row = Db(_app, db => db.Set<LocalePackPreview>().Single());
        row.LocaleFingerprint.Should().HaveLength(64);
    }

    [Fact]
    public async Task Preview_EmptySystem_Works()
    {
        using var app = new TestApp(seedDemoPeople: false);
        (await Preview(app.AsUser(), Fixture("ja-partial.langpack.json"))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── i4d2（計畫 Task 1）──

    /// <summary>回固定版本的假 <see cref="IOpsEnvironment"/>。</summary>
    internal sealed record FakeOps(string Version) : IOpsEnvironment
    {
        public string Commit => "test";
        public string BuiltAt => "test";
        public string Edition => "OnPrem";
    }

    internal static void UseVersion(TestApp app, string version) => app.ExtraServices = s =>
    {
        s.RemoveAll<IOpsEnvironment>();
        s.AddSingleton<IOpsEnvironment>(new FakeOps(version));
    };

    /// <summary>直接在目前版的 <c>UiJson</c> 加一個非基準鍵（模擬平台改了鍵名）。</summary>
    internal static void AddOrphan(TestApp app, string code, string key, string text)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        var row = db.Set<LocalePack>().Single(p => p.Code == code && p.IsCurrent);
        var layer = JsonSerializer.Deserialize<SortedDictionary<string, string>>(row.UiJson)!;
        layer[key] = text;
        row.UiJson = JsonSerializer.Serialize(layer);
        db.SaveChanges();
    }

    private static int Dropped(JsonElement report, string segment) => report.GetProperty("orphanedDropped").GetProperty(segment).GetInt32();

    [Fact]
    public async Task Preview_OrphanedDropped()
    {
        var admin = _app.AsUser();
        await ApplyPackAsync(_app, Fixture("ja-partial.langpack.json"));
        AddOrphan(_app, "ja", "zz.renamedKey", "古い訳");

        var changed = await PreviewOkAsync(admin, System.Text.Encoding.UTF8.GetBytes(Pack("ja", ui: new Dictionary<string, string> { ["menu.home"] = "（新）ホーム" })));
        (Dropped(changed, "ui"), Dropped(changed, "server")).Should().Be((1, 0));

        var same = await PreviewOkAsync(admin, Fixture("ja-partial.langpack.json"));
        Dropped(same, "ui").Should().Be(0, "沒有變動就不寫新版本，失效的鍵這次不會被清");

        var nameOnly = await PreviewOkAsync(admin, System.Text.Encoding.UTF8.GetBytes(Pack("ja", nativeName: "日本語（改）")));
        nameOnly.GetProperty("locale").GetProperty("changes").EnumerateArray().Select(c => c.GetString()).Should().Contain("nativeName");
        Dropped(nameOnly, "ui").Should().Be(0, "只改語系欄位不寫新版本");
    }

    private static byte[] WithBuiltFor(string builtFor) => System.Text.Encoding.UTF8.GetBytes(
        "{\"format\":\"formwright-langpack\",\"formatVersion\":1,\"locale\":{\"code\":\"ja\",\"nativeName\":\"日本語\"},\"pack\":{\"builtFor\":\"" + builtFor
        + "\"},\"ui\":{\"menu.home\":\"ホーム\"}}");

    private static IEnumerable<string?> WarningCodes(JsonElement r) => r.GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("code").GetString());

    [Fact]
    public async Task Preview_BuiltFor_PlatformVersion()
    {
        using var app = new TestApp();
        UseVersion(app, "2026.11");
        var r = await PreviewOkAsync(app.AsUser(), WithBuiltFor("2026.10"));
        var w = r.GetProperty("warnings").EnumerateArray().Single(x => x.GetProperty("code").GetString() == "builtForDiffers");
        w.GetProperty("args").EnumerateArray().Select(a => a.GetString()).Take(2).Should().Equal("2026.10", "2026.11");

        WarningCodes(await PreviewOkAsync(_app.AsUser(), WithBuiltFor("2026.10"))).Should().NotContain("builtForDiffers", "預設主機是 dev");
    }

    [Fact]
    public async Task Preview_Report_TopMissing()
    {
        var r = await PreviewOkAsync(_app.AsUser(), Fixture("ja-partial.langpack.json"));
        var top = r.GetProperty("completeness").GetProperty("topMissing").EnumerateArray().ToList();
        top.Should().HaveCountGreaterThan(0).And.HaveCountLessThanOrEqualTo(5);
        top.Select(t => t.GetProperty("missing").GetInt32()).Should().BeInDescendingOrder();
        top[0].GetProperty("segment").GetString().Should().BeOneOf("ui", "server");
    }

    /// <summary>不回報長度的串流（HttpClient 以 chunked 送出，伺服器拿不到 Content-Length）。</summary>
    private sealed class UnknownLengthStream(long total) : Stream
    {
        private long _left = total;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, _left);
            Array.Fill(buffer, (byte)' ', offset, n);
            _left -= n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
