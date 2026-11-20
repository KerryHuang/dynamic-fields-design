using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Localization;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Persistence.Repositories.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;
using static DynamicModules.WebAPITests.LocaleTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 語系包套用 API（語系包設計 §6.3 第 3 步；i4d1 計畫 Task 7、判斷 16、17）：新安裝、逐句合併、預覽後被改回 409、
/// 交易內重驗（上層語系圖、A39、是／否撞字）、全有或全無、稽核、輸出時的編碼與公式跳脫。
/// </summary>
[Trait("Category", "Postgres")]
public class LocalePackApplyApiTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private static readonly byte[] JaPartial = Fixture("ja-partial.langpack.json");

    private static T Db<T>(TestApp app, Func<DynamicDbContext, T> read)
    {
        using var scope = app.Services.CreateScope();
        return read(scope.ServiceProvider.GetRequiredService<DynamicDbContext>());
    }

    private static HttpClient Admin2(TestApp app)
    {
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            db.Set<AppUser>().Add(new AppUser { UserId = "admin2", DisplayName = "admin2", IsSystemAdmin = true });
            db.SaveChanges();
        }
        return app.AsUser("admin2");
    }

    private static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);

    private static string KoNoIsHai() =>
        Pack("ko", "한국어", "en-US", server: new Dictionary<string, string> { ["report.No"] = "はい" });

    private static async Task<string> CodeOf(HttpResponseMessage res) => (await JsonOf(res)).GetProperty("code").GetString()!;

    private static List<LocalePack> Packs(TestApp app, string code) =>
        Db(app, db => db.Set<LocalePack>().AsNoTracking().Where(p => p.Code == code).ToList().OrderBy(p => p.Id).ToList());

    private static Dictionary<string, string> Layer(string json) => JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;

    private static async Task<Dictionary<string, string>> Ui(HttpClient c, string code)
    {
        var res = await c.GetAsync($"{I18n}/ui/{code}");
        res.StatusCode.Should().Be(HttpStatusCode.OK, code);
        return (await res.Content.ReadFromJsonAsync<Dictionary<string, string>>())!;
    }

    private static IReadOnlyList<string> JaPartialUiKeys() =>
        JsonDocument.Parse(JaPartial).RootElement.GetProperty("ui").EnumerateObject().Select(p => p.Name).ToList();

    [Fact]
    public async Task Apply_NewLocale_Installed_NotEnabled()
    {
        var admin = _app.AsUser();
        var res = await ApplyAsync(admin, await PreviewOkAsync(admin, JaPartial));
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var r = await JsonOf(res);
        r.GetProperty("code").GetString().Should().Be("ja");
        r.GetProperty("isNew").GetBoolean().Should().BeTrue();
        r.GetProperty("noChanges").GetBoolean().Should().BeFalse();
        r.GetProperty("packId").GetInt64().Should().BePositive();
        r.GetProperty("enabled").GetBoolean().Should().BeFalse();

        var row = Db(_app, db => db.Set<InstalledLocale>().AsNoTracking().Single(l => l.Code == "ja"));
        (row.Origin, row.FallbackCode, row.NativeName).Should().Be((LocaleOrigin.Installed, "en-US", "日本語"));
        await WaitForSnapshotAsync(_app, t => t.Overlay("ja")?.PackId == r.GetProperty("packId").GetInt64());

        var effective = await JsonOf(await admin.GetAsync($"{Base}/policies/effective"));
        effective.GetProperty("locales").EnumerateArray().Select(l => l.GetProperty("code").GetString()).Should().Contain("ja");
        (await _app.AsUser(null).GetAsync($"{I18n}/ui/ja")).StatusCode.Should().Be(HttpStatusCode.NotFound, "未啟用的語系匿名取不到");
        var ui = await Ui(admin, "ja");
        var pack = JsonDocument.Parse(JaPartial).RootElement.GetProperty("ui");
        foreach (var p in pack.EnumerateObject()) ui[p.Name].Should().Be(p.Value.GetString(), p.Name);
        ui["menu.home"].Should().Be(BuiltinLocaleResources.Ui["en-US"]["menu.home"], "其他句子沿上層 en-US");
    }

    [Fact]
    public async Task Apply_ServerOverride_ReachesResponses()
    {
        await ApplyPackAsync(_app, JaPartial);
        (await _app.AsUser().PutAsJsonAsync($"{Base}/system/policies/locale.enabled", new { value = new[] { "zh-TW", "en-US", "th", "vi", "ja" } }))
            .EnsureSuccessStatusCode();
        var sales = _app.AsUser("sales01");
        sales.DefaultRequestHeaders.Add("X-Locale", "ja");
        var res = await sales.GetAsync($"{Base}/system/policies");
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await JsonOf(res)).GetProperty("message").GetString().Should().Be("この操作を実行する権限がありません");
    }

    [Fact]
    public async Task Apply_ThaiOverride_U1()
    {
        var admin = _app.AsUser();
        var before = (await admin.GetAsync($"{I18n}/ui/th")).Headers.GetValues("X-UI-Hash").Single();
        await ApplyPackAsync(_app, Fixture("th-fix.langpack.json"));
        var res = await admin.GetAsync($"{I18n}/ui/th");
        res.Headers.GetValues("X-UI-Hash").Single().Should().NotBe(before);
        var ui = (await res.Content.ReadFromJsonAsync<Dictionary<string, string>>())!;
        ui["menu.closedApps"].Should().Be("แอปที่ปิดแล้ว (แก้)");
        ui["menu.conversions"].Should().Be("การแปลงเอกสาร (แก้)");
        Layer(Packs(_app, "th").Single().UiJson).Keys.Should().BeEquivalentTo("menu.closedApps", "menu.conversions");
    }

    [Fact]
    public async Task Merge_30Plus20_U5()
    {
        await ApplyPackAsync(_app, JaPartial);
        var taken = JaPartialUiKeys().ToHashSet();
        var twenty = BuiltinLocaleResources.BaseKeys
            .Where(k => !taken.Contains(k) && BuiltinLocaleResources.UiPlaceholderNames.GetValueOrDefault(k) is not { Count: > 0 }
                        && !BuiltinLocaleResources.Ui["zh-TW"][k].Contains('{'))
            .Take(20).ToDictionary(k => k, k => "（日）" + k);
        await ApplyPackAsync(_app, Pack("ja", ui: twenty));

        var ui = await Ui(_app.AsUser(), "ja");
        foreach (var p in JsonDocument.Parse(JaPartial).RootElement.GetProperty("ui").EnumerateObject()) ui[p.Name].Should().Be(p.Value.GetString());
        foreach (var (k, v) in twenty) ui[k].Should().Be(v);
        var rows = Packs(_app, "ja");
        rows.Should().HaveCount(2);
        Layer(rows.Single(p => !p.IsCurrent).UiJson).Should().HaveCount(30);
        Layer(rows.Single(p => p.IsCurrent).UiJson).Should().HaveCount(50);
    }

    [Fact]
    public async Task Concurrent_SecondGets409()
    {
        var admin = _app.AsUser();
        var other = Admin2(_app);
        var first = await PreviewOkAsync(admin, JaPartial);
        var second = await PreviewOkAsync(other, JaPartial);
        (await ApplyAsync(admin, first)).StatusCode.Should().Be(HttpStatusCode.OK);
        var stale = await ApplyAsync(other, second);
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeOf(stale)).Should().Be("localePackStale");
        (await ApplyAsync(other, await PreviewOkAsync(other, JaPartial))).StatusCode.Should().Be(HttpStatusCode.OK, "重新預覽後可以套用");
    }

    [Fact]
    public async Task Stale_AfterABA_409()
    {
        await ApplyPackAsync(_app, JaPartial);
        var admin = _app.AsUser();
        var mine = await PreviewOkAsync(admin, Bytes(Pack("ja", ui: new Dictionary<string, string> { ["menu.home"] = "（日）ホーム" })));
        await ApplyPackAsync(_app, Bytes(Encoding.UTF8.GetString(JaPartial).Replace("（日）Sign in\"", "（日）ログイン\"")), client: Admin2(_app));
        await RollbackAsync(_app, "ja");
        Layer(Packs(_app, "ja").Single(p => p.IsCurrent).UiJson)["auth.login"].Should().Be("（日）Sign in", "內容回到 A");
        var res = await ApplyAsync(admin, mine);
        res.StatusCode.Should().Be(HttpStatusCode.Conflict, "目前版 Id 換了，不靠修改時間");
        (await CodeOf(res)).Should().Be("localePackStale");
    }

    [Fact]
    public async Task OtherLocale_NotAffected()
    {
        var admin = _app.AsUser();
        var th = await PreviewOkAsync(admin, Fixture("th-fix.langpack.json"));
        await ApplyPackAsync(_app, JaPartial, client: Admin2(_app));
        (await ApplyAsync(admin, th)).StatusCode.Should().Be(HttpStatusCode.OK, "只比該語系的版本");
    }

    [Fact]
    public async Task CrossLocaleBoolean_409_ThenRepreview()
    {
        var admin = _app.AsUser();
        var ja = await PreviewOkAsync(admin, JaPartial);
        ja.GetProperty("server").GetProperty("rejected").GetInt32().Should().Be(0);
        await ApplyPackAsync(_app, KoNoIsHai(), client: Admin2(_app));

        var stale = await ApplyAsync(admin, ja);
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeOf(stale)).Should().Be("localePackStale");
        Db(_app, db => db.Set<InstalledLocale>().Any(l => l.Code == "ja")).Should().BeFalse();

        var again = await PreviewOkAsync(admin, JaPartial);
        again.GetProperty("server").GetProperty("rejectedItems").EnumerateArray()
            .Should().Contain(i => i.GetProperty("key").GetString() == "report.Yes" && i.GetProperty("reason").GetString() == "booleanCollision");
        (await ApplyAsync(admin, again)).StatusCode.Should().Be(HttpStatusCode.OK);
        Layer(Packs(_app, "ja").Single().ServerJson).Should().NotContainKey("report.Yes").And.ContainKey("report.No");
    }

    [Fact]
    public async Task AlreadyRejected_AppliesWithoutStale()
    {
        await ApplyPackAsync(_app, KoNoIsHai());
        var admin = _app.AsUser();
        var ja = await PreviewOkAsync(admin, JaPartial);
        ja.GetProperty("server").GetProperty("rejectedItems").EnumerateArray().Select(i => i.GetProperty("key").GetString()).Should().Contain("report.Yes");
        (await ApplyAsync(admin, ja)).StatusCode.Should().Be(HttpStatusCode.OK);
        Layer(Packs(_app, "ja").Single().ServerJson).Should().NotContainKey("report.Yes");
    }

    [Fact]
    public async Task PreviewIdChecks_404_And409()
    {
        var admin = _app.AsUser();
        var other = Admin2(_app);

        var mine = await PreviewOkAsync(admin, JaPartial);
        var notMine = await ApplyAsync(other, mine);
        notMine.StatusCode.Should().Be(HttpStatusCode.NotFound, "Preview_OtherUser_404");
        (await CodeOf(notMine)).Should().Be("localePackPreviewExpired");

        var wrongHash = await admin.PostAsJsonAsync($"{I18n}/packs/apply", new { previewId = mine.GetProperty("previewId").GetString(), fileHash = new string('0', 64) });
        wrongHash.StatusCode.Should().Be(HttpStatusCode.Conflict, "FileHash_Mismatch_409");
        (await CodeOf(wrongHash)).Should().Be("localePackStale");

        _app.Time.Advance(TimeSpan.FromMinutes(11));
        var expired = await ApplyAsync(admin, mine);
        expired.StatusCode.Should().Be(HttpStatusCode.NotFound, "Preview_Expired_404");
        (await CodeOf(expired)).Should().Be("localePackPreviewExpired");

        var fresh = await PreviewOkAsync(admin, JaPartial);
        (await ApplyAsync(admin, fresh)).StatusCode.Should().Be(HttpStatusCode.OK);
        var consumed = await ApplyAsync(admin, fresh);
        consumed.StatusCode.Should().Be(HttpStatusCode.NotFound, "Preview_Consumed");
        (await CodeOf(consumed)).Should().Be("localePackPreviewExpired");
        Db(_app, db => db.Set<LocalePackPreview>().Count()).Should().Be(0);
    }

    [Fact]
    public async Task MaxInstalled_RecheckedAtApply()
    {
        var admin = _app.AsUser();
        (await admin.PutAsJsonAsync($"{Base}/system/policies/locale.maxInstalled", new { value = 5 })).EnsureSuccessStatusCode();
        var ja = await PreviewOkAsync(admin, JaPartial);
        await InstallAsync(_app, "ko", "한국어", "en-US");
        var res = await ApplyAsync(admin, ja);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await JsonOf(res);
        body.GetProperty("code").GetString().Should().Be("localePackInvalid");
        body.GetProperty("errors").GetProperty("file")[0].GetString().Should().Be("最多只能安裝 5 個語系（含內建）");
        Db(_app, db => db.Set<InstalledLocale>().Any(l => l.Code == "ja")).Should().BeFalse();
    }

    [Fact]
    public async Task FallbackChange_RecheckedAtApply()
    {
        var admin = _app.AsUser();
        await InstallAsync(_app, "ja", "日本語", "en-US");
        await InstallAsync(_app, "ko", "한국어", "en-US");

        var jaToKo = await PreviewOkAsync(admin, Bytes(Pack("ja", fallback: "ko")));
        await ApplyPackAsync(_app, Pack("ko", fallback: "ja"), client: Admin2(_app));
        var cycle = await ApplyAsync(admin, jaToKo);
        cycle.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(cycle)).GetProperty("errors").GetProperty("file")[0].GetString().Should().StartWith("上層語系形成循環");
        Db(_app, db => db.Set<InstalledLocale>().Single(l => l.Code == "ja").FallbackCode).Should().Be("en-US");

        await ApplyPackAsync(_app, Pack("ko", fallback: "en-US"));
        var dangling = await PreviewOkAsync(admin, Bytes(Pack("ja", fallback: "ko")));
        await RemoveAsync(_app, "ko");
        var missing = await ApplyAsync(admin, dangling);
        missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(missing)).GetProperty("errors").GetProperty("file")[0].GetString().Should().Be("上層語系「ko」還沒安裝");
        Db(_app, db => db.Set<InstalledLocale>().Single(l => l.Code == "ja").FallbackCode).Should().Be("en-US");
    }

    [Fact]
    public async Task AllOrNothing_BeforeSave()
    {
        using var app = new TestApp
        {
            ExtraServices = s => s.AddScoped<ILocalePackRepository>(sp => new ThrowingPacks(new LocalePackRepository(sp.GetRequiredService<DynamicDbContext>()))),
        };
        var admin = app.AsUser();
        var preview = await PreviewOkAsync(admin, JaPartial);
        (await ApplyAsync(admin, preview)).StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        AssertNothingWritten(app);
    }

    [Fact]
    public async Task AllOrNothing_AfterBusinessSave()
    {
        using var app = new TestApp
        {
            ExtraServices = s => s.AddScoped<IAuditLog>(sp => new ThrowingAudit(ActivatorUtilities.CreateInstance<AuditLog>(sp))),
        };
        var admin = app.AsUser();
        var preview = await PreviewOkAsync(admin, JaPartial);
        (await ApplyAsync(admin, preview)).StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        AssertNothingWritten(app);
        var registry = app.Services.GetRequiredService<LocaleRegistry>();
        registry.Texts().Overlay("ja").Should().BeNull();
        registry.Codes.Should().NotContain("ja");
    }

    private static void AssertNothingWritten(TestApp app)
    {
        Db(app, db => db.Set<InstalledLocale>().Any(l => l.Code == "ja")).Should().BeFalse();
        Db(app, db => db.Set<LocalePack>().Count()).Should().Be(0);
        Db(app, db => db.Set<LocalePackPreview>().Count()).Should().Be(1, "預覽的消耗也回復");
        Db(app, db => db.Set<AuditLogEntry>().Count(a => a.Action.StartsWith("locale."))).Should().Be(0);
    }

    [Fact]
    public async Task DemoOff_403()
    {
        using var app = new TestApp();
        app.Settings["Localization:PackImportEnabled"] = "false";
        var admin = app.AsUser();
        var report = await PreviewOkAsync(admin, JaPartial);
        var res = await admin.PostAsJsonAsync($"{I18n}/packs/apply", new { previewId = "x", fileHash = report.GetProperty("fileHash").GetString() });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CodeOf(res)).Should().Be("localePackImportDisabled");
        Db(app, db => db.Set<InstalledLocale>().Any(l => l.Code == "ja")).Should().BeFalse();
        (await _app.AsUser("sales01").PostAsJsonAsync($"{I18n}/packs/apply", new { previewId = "x", fileHash = "y" })).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "一般使用者");
    }

    [Fact]
    public async Task Audit_InstallThenImport()
    {
        await ApplyPackAsync(_app, JaPartial, "ja-partial.langpack.json");
        await ApplyPackAsync(_app, Pack("ja", ui: new Dictionary<string, string> { ["menu.home"] = "（日）ホーム" }), "ja-fix.langpack.json");
        var rows = Db(_app, db => db.Set<AuditLogEntry>().AsNoTracking().Where(a => a.Action.StartsWith("locale.")).ToList().OrderBy(a => a.Id).ToList());
        rows.Select(r => r.Action).Should().Equal("locale.install", "locale.pack.import");
        rows.Should().OnlyContain(r => r.TargetId == "ja" && r.TargetLabel == "日本語");
        var first = Regex.Unescape(rows[0].SummaryJson!);
        first.Should().Contain("ja-partial.langpack.json").And.Contain(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JaPartial)).ToLowerInvariant());
        first.Should().Contain("\"added\":30");
        foreach (var row in rows)
        {
            var text = Regex.Unescape(row.SummaryJson!);
            text.Should().NotContain("はい").And.NotContain("（日）").And.NotContain("権限");
        }
    }

    [Fact]
    public async Task XssAndFormula_Escaped()
    {
        await ApplyPackAsync(_app, Pack("th", server: new Dictionary<string, string> { ["report.Yes"] = "<script>x</script>", ["report.No"] = "=1+1" }));
        var admin = _app.AsUser();
        var key = await CreateModule(admin);
        var flag = await AddField(admin, key, "boolean");
        await Activate(admin, key);
        var title = await TitleKey(admin, key);
        long yesId = 0;
        foreach (var (t, v) in new[] { ("A", true), ("B", false) })
        {
            var res = await admin.PostAsJsonAsync($"{Base}/modules/{key}/records", new { values = new Dictionary<string, object> { [title] = t, [flag.Key] = v } });
            res.EnsureSuccessStatusCode();
            if (v) yesId = (await JsonOf(res)).GetProperty("id").GetInt64();
        }
        var thai = _app.AsUser();
        thai.DefaultRequestHeaders.Add("X-Locale", "th");

        (await admin.PostAsJsonAsync($"{Base}/modules/{key}/print-templates", new
        {
            key = "pt", name = new Dictionary<string, string> { ["zh-TW"] = "列印" }, body = $"<p>{{{{{flag.Key}}}}}</p>", active = true
        })).EnsureSuccessStatusCode();
        var html = await (await thai.GetAsync($"{Base}/records/{yesId}/print/pt")).Content.ReadAsStringAsync();
        html.Should().Contain("&lt;script&gt;x&lt;/script&gt;").And.NotContain("<script>x</script>");

        var csv = await (await thai.GetAsync($"{Base}/modules/{key}/export?format=csv")).Content.ReadAsStringAsync();
        csv.Should().Contain(",'=1+1").And.NotContain(",=1+1");

        var xlsx = await thai.GetAsync($"{Base}/modules/{key}/export?format=xlsx");
        xlsx.StatusCode.Should().Be(HttpStatusCode.OK);
        using var wb = new XLWorkbook(await xlsx.Content.ReadAsStreamAsync());
        var cell = wb.Worksheet(1).CellsUsed().Single(c => c.GetString() == "=1+1");
        cell.HasFormula.Should().BeFalse();
    }

    [Fact]
    public async Task NoChanges_NoNewVersion()
    {
        var b = Bytes(Encoding.UTF8.GetString(JaPartial).Replace("（日）Sign in\"", "（日）ログイン\""));
        await ApplyPackAsync(_app, JaPartial);
        var applied = await ApplyPackAsync(_app, b);
        var ids = Packs(_app, "ja").Select(p => p.Id).ToList();
        ids.Should().HaveCount(2);
        applied.PackId.Should().Be(ids[1]);

        var again = await ApplyPackAsync(_app, b);
        (again.NoChanges, again.PackId, again.IsNew).Should().Be((true, ids[1], false));
        Packs(_app, "ja").Select(p => p.Id).Should().Equal(ids);
        Db(_app, db => db.Set<AuditLogEntry>().Count(a => a.Action.StartsWith("locale."))).Should().Be(3);

        using var app = new TestApp();
        await ApplyPackAsync(app, JaPartial);
        var single = Packs(app, "ja").Single().Id;
        (await ApplyPackAsync(app, JaPartial)).NoChanges.Should().BeTrue();
        Packs(app, "ja").Select(p => p.Id).Should().Equal(single);
    }

    /// <summary>在 <see cref="ILocalePackRepository.AddVersion"/> 丟例外（SaveChanges 之前）。</summary>
    private sealed class ThrowingPacks(ILocalePackRepository inner) : ILocalePackRepository
    {
        public Task<List<LocalePack>> ListCurrentAsync(CancellationToken ct) => inner.ListCurrentAsync(ct);
        public Task<LocalePack?> CurrentAsync(string code, CancellationToken ct) => inner.CurrentAsync(code, ct);
        public Task<LocalePack?> PreviousAsync(string code, CancellationToken ct) => inner.PreviousAsync(code, ct);
        public void AddVersion(LocalePack next, LocalePack? current, LocalePack? previous, DateTime now) => throw new InvalidOperationException("測試：寫入版本失敗");
        public Task DeleteForLocaleAsync(string code, CancellationToken ct) => inner.DeleteForLocaleAsync(code, ct);
        public void AddPreview(LocalePackPreview p) => inner.AddPreview(p);
        public Task<LocalePackPreview?> FindPreviewAsync(string id, CancellationToken ct) => inner.FindPreviewAsync(id, ct);
        public void RemovePreview(LocalePackPreview p) => inner.RemovePreview(p);
        public Task PrunePreviewsAsync(DateTime now, int keep, CancellationToken ct) => inner.PrunePreviewsAsync(now, keep, ct);
    }

    /// <summary>寫稽核時丟例外：業務資料已經由 handler 的 CommitAsync 寫進交易（必修 9）。</summary>
    private sealed class ThrowingAudit(IAuditLog inner) : IAuditLog
    {
        public AuditLogEntry Add(AuditEntry entry) => throw new InvalidOperationException("測試：稽核寫不進去");
        public Task WriteNowAsync(AuditEntry entry, CancellationToken ct) => inner.WriteNowAsync(entry, ct);
        public Task<bool> TryWriteNowAsync(AuditEntry entry, CancellationToken ct) => inner.TryWriteNowAsync(entry, ct);
    }
}
