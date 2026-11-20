using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.LocaleService.Packs;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;
using static DynamicModules.WebAPITests.LocaleTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 語系清單、回到上一版、移除覆寫、移除語系（語系包設計 §4.5、§6.4、§6.5；i4d1 計畫 Task 8、判斷 19、20）：
/// 版本 token 每次都換、是／否撞字擋下（Q12）、移除的守衛讀交易內的資料（不讀快照）。
/// </summary>
[Trait("Category", "Postgres")]
public class LocaleManageApiTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private static readonly byte[] JaPartial = Fixture("ja-partial.langpack.json");
    private static readonly byte[] JaB = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(JaPartial).Replace("（日）Sign in\"", "（日）ログイン\""));

    private static T Db<T>(TestApp app, Func<DynamicDbContext, T> read)
    {
        using var scope = app.Services.CreateScope();
        return read(scope.ServiceProvider.GetRequiredService<DynamicDbContext>());
    }

    private static void Write(TestApp app, Action<DynamicDbContext> write)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        write(db);
        db.SaveChanges();
    }

    private static List<LocalePack> Packs(TestApp app, string code) =>
        Db(app, db => db.Set<LocalePack>().AsNoTracking().Where(p => p.Code == code).ToList().OrderBy(p => p.Id).ToList());

    private Task<HttpResponseMessage> Post(string code, string action) => _app.AsUser().PostAsync($"{I18n}/locales/{code}/{action}", null);

    /// <summary>斷言狀態碼與機器碼，回本文（本文只能讀一次）。</summary>
    private static async Task<JsonElement> ShouldBe(HttpResponseMessage res, HttpStatusCode status, string code)
    {
        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(status, text);
        var body = JsonDocument.Parse(text).RootElement.Clone();
        body.GetProperty("code").GetString().Should().Be(code);
        return body;
    }

    private async Task<Dictionary<string, string>> Ui(string code) =>
        (await (await _app.AsUser().GetAsync($"{I18n}/ui/{code}")).Content.ReadFromJsonAsync<Dictionary<string, string>>())!;

    private async Task<JsonElement> List() => await JsonOf(await _app.AsUser().GetAsync($"{I18n}/locales"));

    private static JsonElement Row(JsonElement list, string code) =>
        list.GetProperty("locales").EnumerateArray().Single(l => l.GetProperty("code").GetString() == code);

    private Task Enable(params string[] codes) =>
        _app.AsUser().PutAsJsonAsync($"{Base}/system/policies/locale.enabled", new { value = codes }).ContinueWith(t => t.Result.EnsureSuccessStatusCode());

    private static string Server(string code, string? nativeName, string? yes, string? no)
    {
        var server = new Dictionary<string, string>();
        if (yes is not null) server["report.Yes"] = yes;
        if (no is not null) server["report.No"] = no;
        return Pack(code, nativeName, nativeName is null ? null : "en-US", server: server);
    }

    [Fact]
    public async Task List_Factory_FourBuiltin()
    {
        var list = await List();
        list.GetProperty("importEnabled").GetBoolean().Should().BeTrue();
        list.GetProperty("packMaxBytes").GetInt64().Should().Be(5242880);
        list.GetProperty("maxInstalled").GetInt32().Should().Be(10);
        var rows = list.GetProperty("locales").EnumerateArray().ToList();
        rows.Select(r => r.GetProperty("code").GetString()).Should().Equal("zh-TW", "en-US", "th", "vi");
        foreach (var r in rows)
        {
            r.GetProperty("origin").GetString().Should().Be("builtin");
            r.GetProperty("overrides").GetProperty("ui").GetInt32().Should().Be(0);
            r.GetProperty("overrides").GetProperty("server").GetInt32().Should().Be(0);
            r.TryGetProperty("current", out var current).Should().BeTrue("current 要明示輸出 null");
            current.ValueKind.Should().Be(JsonValueKind.Null);
            r.GetProperty("hasPrevious").GetBoolean().Should().BeFalse();
        }
        Row(list, "zh-TW").GetProperty("isDefault").GetBoolean().Should().BeTrue();
        Row(list, "zh-TW").TryGetProperty("fallbackCode", out var fb).Should().BeTrue();
        fb.ValueKind.Should().Be(JsonValueKind.Null);
        (await _app.AsUser("sales01").GetAsync($"{I18n}/locales")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task List_AfterImport()
    {
        await ApplyPackAsync(_app, JaPartial, "ja-partial.langpack.json");
        var ja = Row(await List(), "ja");
        (ja.GetProperty("origin").GetString(), ja.GetProperty("nativeName").GetString(), ja.GetProperty("fallbackCode").GetString()).Should().Be(("installed", "日本語", "en-US"));
        (ja.GetProperty("enabled").GetBoolean(), ja.GetProperty("isDefault").GetBoolean(), ja.GetProperty("required").GetBoolean()).Should().Be((false, false, false));
        ja.GetProperty("overrides").GetProperty("ui").GetInt32().Should().Be(30);
        ja.GetProperty("overrides").GetProperty("server").GetInt32().Should().Be(5);
        var current = ja.GetProperty("current");
        current.GetProperty("fileName").GetString().Should().Be("ja-partial.langpack.json");
        current.GetProperty("packVersion").GetString().Should().Be("0.1.0");
        current.GetProperty("importedBy").GetString().Should().Be("admin");
        current.GetProperty("importedUtc").GetDateTime().Should().NotBe(default);
        ja.GetProperty("hasPrevious").GetBoolean().Should().BeFalse();
    }

    /// <summary>
    /// 不經測試的等待 helper（審查建議 1）：<see cref="WaitForSnapshotAsync"/> 每輪自己呼叫 <c>Invalidate</c>，正式寫入路徑漏登記提交後失效也會過。
    /// 這裡只發正常請求：假時鐘不前進、快照不會因 TTL 過期，下一個請求看得到結果只可能是提交後失效生效。
    /// </summary>
    [Fact]
    public async Task Writes_NextRequestSeesResult_WithoutTestInvalidate()
    {
        var admin = _app.AsUser();
        async Task<string?> Login()
        {
            var res = await admin.GetAsync($"{I18n}/ui/ja");
            if (res.StatusCode == HttpStatusCode.NotFound) return null;
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            return (await res.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["auth.login"];
        }
        async Task Ok(HttpResponseMessage res) => res.StatusCode.Should().BeOneOf([HttpStatusCode.OK, HttpStatusCode.NoContent], await res.Content.ReadAsStringAsync());

        (await Login()).Should().BeNull();
        await Ok(await ApplyAsync(admin, await PreviewOkAsync(admin, JaPartial)));
        (await Login()).Should().Be("（日）Sign in", "套用");
        await Ok(await ApplyAsync(admin, await PreviewOkAsync(admin, JaB)));
        (await Login()).Should().Be("（日）ログイン", "再套用");
        await Ok(await Post("ja", "rollback"));
        (await Login()).Should().Be("（日）Sign in", "回到上一版");
        await Ok(await Post("ja", "clear"));
        (await Login()).Should().NotBe("（日）Sign in", "移除覆寫");
        await Ok(await admin.DeleteAsync($"{I18n}/locales/ja"));
        (await Login()).Should().BeNull("移除語系");
    }

    [Fact]
    public async Task Rollback_AndBack()
    {
        var a = await ApplyPackAsync(_app, JaPartial);
        var b = await ApplyPackAsync(_app, JaB);
        (await Ui("ja"))["auth.login"].Should().Be("（日）ログイン");

        var r1 = await RollbackAsync(_app, "ja");
        (await Ui("ja"))["auth.login"].Should().Be("（日）Sign in");
        Packs(_app, "ja").Should().HaveCount(2);
        var r2 = await RollbackAsync(_app, "ja");
        (await Ui("ja"))["auth.login"].Should().Be("（日）ログイン", "可以再切回來");
        Packs(_app, "ja").Should().HaveCount(2);
        new[] { a.PackId, b.PackId, r1, r2 }.Should().OnlyHaveUniqueItems("每次寫入的目前版 Id 都不同");
        Row(await List(), "ja").GetProperty("hasPrevious").GetBoolean().Should().BeTrue();

        await ShouldBe(await Post("th", "rollback"), HttpStatusCode.Conflict, "localeNoPrevious");
        await ShouldBe(await Post("xx", "rollback"), HttpStatusCode.NotFound, "localeNotFound");
    }

    [Fact]
    public async Task Clear_BuiltinBackToFactory_AndUndo()
    {
        var factory = await Ui("th");
        await ApplyPackAsync(_app, Fixture("th-fix.langpack.json"));
        (await Ui("th"))["menu.closedApps"].Should().Be("แอปที่ปิดแล้ว (แก้)");

        await ClearAsync(_app, "th");
        (await Ui("th")).Should().Equal(factory);
        Row(await List(), "th").GetProperty("overrides").GetProperty("ui").GetInt32().Should().Be(0);
        await ShouldBe(await Post("th", "clear"), HttpStatusCode.Conflict, "localeNoOverrides");

        await RollbackAsync(_app, "th");
        (await Ui("th"))["menu.closedApps"].Should().Be("แอปที่ปิดแล้ว (แก้)", "移除覆寫可以用回到上一版復原（Q5）");
        await ShouldBe(await Post("vi", "clear"), HttpStatusCode.Conflict, "localeNoOverrides");
    }

    [Fact]
    public async Task Rollback_BooleanCollision_409()
    {
        await ApplyPackAsync(_app, Server("th", null, null, "ไม่"));
        await ApplyPackAsync(_app, Server("th", null, null, "ไม่ใช่"));
        await ApplyPackAsync(_app, Server("ja", "日本語", "ไม่", "いいえ"));
        var before = Packs(_app, "th");

        var message = (await ShouldBe(await Post("th", "rollback"), HttpStatusCode.Conflict, "localeBooleanCollision")).GetProperty("message").GetString();
        message.Should().Contain("ไม่").And.Contain("日本語");
        Packs(_app, "th").Select(p => (p.Id, p.IsCurrent)).Should().Equal(before.Select(p => (p.Id, p.IsCurrent)));
    }

    [Fact]
    public async Task Clear_BooleanCollision_409()
    {
        await ApplyPackAsync(_app, Server("th", null, null, "ไม่"));
        var ja = await PreviewOkAsync(_app.AsUser(), Encoding.UTF8.GetBytes(Server("ja", "日本語", "ไม่ใช่", "いいえ")));
        ja.GetProperty("server").GetProperty("rejected").GetInt32().Should().Be(0, "當時 th 的「否」是 ไม่，不撞");
        (await ApplyAsync(_app.AsUser(), ja)).EnsureSuccessStatusCode();
        await WaitForSnapshotAsync(_app, t => t.Infos.Any(i => i.Code == "ja"));
        var before = Packs(_app, "th");
        var ui = await Ui("th");

        (await ShouldBe(await Post("th", "clear"), HttpStatusCode.Conflict, "localeBooleanCollision")).GetProperty("message").GetString().Should().Contain("ไม่ใช่").And.Contain("日本語");
        var after = Packs(_app, "th");
        after.Select(p => (p.Id, p.IsCurrent, p.ServerJson)).Should().Equal(before.Select(p => (p.Id, p.IsCurrent, p.ServerJson)));
        (await Ui("th")).Should().Equal(ui);
        _app.Services.GetRequiredService<LocaleRegistry>().Texts().Overlay("th")!.PackId.Should().Be(before.Single(p => p.IsCurrent).Id);
    }

    /// <summary>直接寫一列 ja 與三個語系政策，不讓快照換新（registry 的 Codes 沒有 ja，scope 的 IPolicyReader 會把 ja 濾掉）。</summary>
    private async Task JaAheadOfRegistry()
    {
        await _app.Services.GetRequiredService<LocaleRegistry>().EnsureFreshAsync(CancellationToken.None);
        _app.Services.GetRequiredService<LocaleRegistry>().Codes.Should().Equal("zh-TW", "en-US", "th", "vi");
        var now = DateTime.UtcNow;
        Write(_app, db =>
        {
            db.Set<InstalledLocale>().Add(new InstalledLocale { Code = "ja", NativeName = "日本語", FallbackCode = "en-US", Origin = LocaleOrigin.Installed, CreatedUtc = now, ModifiedUtc = now });
            db.Set<SystemSetting>().Add(new SystemSetting { Key = "policy.locale.enabled", Value = "zh-TW,en-US,th,vi,ja" });
            db.Set<SystemSetting>().Add(new SystemSetting { Key = "policy.locale.default", Value = "ja" });
        });
    }

    [Fact]
    public async Task Remove_InUse_PolicyNotFromSnapshot()
    {
        await JaAheadOfRegistry();
        await ShouldBe(await _app.AsUser().DeleteAsync($"{I18n}/locales/ja"), HttpStatusCode.Conflict, "localeInUse");
        Db(_app, db => db.Set<InstalledLocale>().Any(l => l.Code == "ja")).Should().BeTrue();
    }

    [Fact]
    public async Task WorldReader_DbAheadOfRegistry()
    {
        await JaAheadOfRegistry();
        using var scope = _app.Services.CreateScope();
        var world = await scope.ServiceProvider.GetRequiredService<LocaleWorldReader>().ReadAsync(CancellationToken.None);
        world.Installed.Select(i => i.Code).Should().Contain("ja");
        world.DefaultLocale.Should().Be("ja");
        world.Enabled.Should().Contain("ja");
        _app.Services.GetRequiredService<LocaleRegistry>().Codes.Should().NotContain("ja", "資料庫確實領先快照");
    }

    [Fact]
    public async Task Remove_Installed()
    {
        await ApplyPackAsync(_app, JaPartial);
        var admin = _app.AsUser();
        var key = await CreateModule(admin);
        var field = await AddField(admin, key, "text");
        object Rename(string zh) => new { type = "text", label = new Dictionary<string, string> { ["zh-TW"] = zh, ["ja"] = "フィールド" }, config = new { }, blockId = field.BlockId };
        (await admin.PutAsJsonAsync($"{Base}/fields/{field.Id}", Rename("欄位"))).EnsureSuccessStatusCode();
        await PreviewOkAsync(admin, JaB);

        var res = await RemoveLocaleAsync(_app, "ja");
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await res.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
        Db(_app, db => db.Set<InstalledLocale>().Any(l => l.Code == "ja")).Should().BeFalse();
        Db(_app, db => db.Set<LocalePack>().Any(p => p.Code == "ja")).Should().BeFalse();
        Db(_app, db => db.Set<LocalePackPreview>().Any(p => p.Code == "ja")).Should().BeFalse();
        Db(_app, db => db.Set<Field>().AsNoTracking().Single(f => f.Id == field.Id).LabelI18n.Values["ja"]).Should().Be("フィールド", "設計資料裡的文字不動");
        (await admin.PutAsJsonAsync($"{Base}/fields/{field.Id}", Rename("改名"))).StatusCode.Should().Be(HttpStatusCode.OK, "含 ja 的欄位照常改名（M1）");
    }

    [Fact]
    public async Task Remove_Blocked()
    {
        await ShouldBe(await _app.AsUser().DeleteAsync($"{I18n}/locales/th"), HttpStatusCode.Conflict, "localeBuiltinNotRemovable");
        await ShouldBe(await _app.AsUser().DeleteAsync($"{I18n}/locales/xx"), HttpStatusCode.NotFound, "localeNotFound");
        await ApplyPackAsync(_app, JaPartial);
        await Enable("zh-TW", "en-US", "th", "vi", "ja");
        await ShouldBe(await _app.AsUser().DeleteAsync($"{I18n}/locales/ja"), HttpStatusCode.Conflict, "localeInUse");

        await Enable("zh-TW", "en-US", "th", "vi");
        await ApplyPackAsync(_app, Pack("ko", "한국어", "ja"));
        (await ShouldBe(await _app.AsUser().DeleteAsync($"{I18n}/locales/ja"), HttpStatusCode.Conflict, "localeIsFallback")).GetProperty("message").GetString().Should().Be("請先改掉한국어的上層語系");
    }

    [Fact]
    public async Task Remove_FallbackCheck_NotFromSnapshot()
    {
        await ApplyPackAsync(_app, JaPartial);
        var now = DateTime.UtcNow;
        Write(_app, db => db.Set<InstalledLocale>().Add(new InstalledLocale
        {
            Code = "ko", NativeName = "한국어", FallbackCode = "ja", Origin = LocaleOrigin.Installed, CreatedUtc = now, ModifiedUtc = now,
        }));
        _app.Services.GetRequiredService<LocaleRegistry>().Codes.Should().NotContain("ko");
        await ShouldBe(await _app.AsUser().DeleteAsync($"{I18n}/locales/ja"), HttpStatusCode.Conflict, "localeIsFallback");
    }

    [Fact]
    public async Task Remove_AlertChannelLocale_NotBlocked()
    {
        await ApplyPackAsync(_app, JaPartial);
        Write(_app, db =>
        {
            var channel = db.Set<AlertChannel>().FirstOrDefault(c => c.Kind == AlertChannel.Smtp);
            if (channel is null) db.Set<AlertChannel>().Add(new AlertChannel { Kind = AlertChannel.Smtp, MailLocale = "ja" });
            else channel.MailLocale = "ja";
        });
        await RemoveLocaleAsync(_app, "ja");
    }

    [Fact]
    public async Task Reinstall_OldTextReappears()
    {
        await ApplyPackAsync(_app, JaPartial);
        var admin = _app.AsUser();
        var key = await CreateModule(admin);
        var field = await AddField(admin, key, "text");
        (await admin.PutAsJsonAsync($"{Base}/fields/{field.Id}", new
        {
            type = "text", label = new Dictionary<string, string> { ["zh-TW"] = "欄位", ["ja"] = "フィールド" }, config = new { }, blockId = field.BlockId,
        })).EnsureSuccessStatusCode();
        await Activate(admin, key);
        await RemoveLocaleAsync(_app, "ja");

        await ApplyPackAsync(_app, JaPartial);
        await Enable("zh-TW", "en-US", "th", "vi", "ja");
        var ja = _app.AsUser();
        ja.DefaultRequestHeaders.Add("X-Locale", "ja");
        (await (await ja.GetAsync($"{Base}/modules/{key}/export?format=csv")).Content.ReadAsStringAsync()).Should().Contain("フィールド");
    }

    [Fact]
    public async Task DemoOff_403_AllThree()
    {
        using var app = new TestApp();
        app.Settings["Localization:PackImportEnabled"] = "false";
        var admin = app.AsUser();
        await ShouldBe(await admin.PostAsync($"{I18n}/locales/th/rollback", null), HttpStatusCode.Forbidden, "localePackImportDisabled");
        await ShouldBe(await admin.PostAsync($"{I18n}/locales/th/clear", null), HttpStatusCode.Forbidden, "localePackImportDisabled");
        await ShouldBe(await admin.DeleteAsync($"{I18n}/locales/th"), HttpStatusCode.Forbidden, "localePackImportDisabled");
        (await JsonOf(await admin.GetAsync($"{I18n}/locales"))).GetProperty("importEnabled").GetBoolean().Should().BeFalse();
        foreach (var res in new[]
                 {
                     await _app.AsUser("sales01").PostAsync($"{I18n}/locales/th/rollback", null),
                     await _app.AsUser("sales01").PostAsync($"{I18n}/locales/th/clear", null),
                     await _app.AsUser("sales01").DeleteAsync($"{I18n}/locales/th"),
                 })
            res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "一般使用者");
    }

    [Fact]
    public async Task Audit_EachAction()
    {
        await ApplyPackAsync(_app, JaPartial);
        await ApplyPackAsync(_app, JaB);
        await RollbackAsync(_app, "ja");
        await ClearAsync(_app, "ja");
        await RemoveLocaleAsync(_app, "ja");
        var rows = Db(_app, db => db.Set<AuditLogEntry>().AsNoTracking()
            .Where(a => a.Action == "locale.pack.rollback" || a.Action == "locale.pack.clear" || a.Action == "locale.remove").ToList().OrderBy(a => a.Id).ToList());
        rows.Select(r => r.Action).Should().Equal("locale.pack.rollback", "locale.pack.clear", "locale.remove");
        rows.Should().OnlyContain(r => r.TargetKind == "locale" && r.TargetId == "ja" && r.TargetLabel == "日本語");
    }

    // ── i4d2（計畫 Task 2）：完整度、缺最多的分類、覆寫檢視 ──

    private static readonly string[] ReviewFields = ["baseChanged", "paramsChanged", "runtimeFallback", "orphaned"];

    private static IEnumerable<string?> Keys(JsonElement section, string name) => section.GetProperty(name).EnumerateArray().Select(k => k.GetString());

    /// <summary>直接改目前版的原始列（不 Invalidate；清單讀資料庫）。</summary>
    private void EditCurrent(string code, Action<LocalePack> edit) =>
        Write(_app, db => edit(db.Set<LocalePack>().Single(p => p.Code == code && p.IsCurrent)));

    private static string ZhServer(string key) => Application.Common.Localization.BuiltinServerText.TryGet("zh-TW", key, out var zh) ? zh : "";

    private static IReadOnlySet<int> ArgsOf(string template) =>
        System.Text.RegularExpressions.Regex.Matches(template, @"(?<!\{)\{(\d+)\}").Select(m => int.Parse(m.Groups[1].Value)).ToHashSet();

    private static string ServerKey(params int[] args) => Application.Common.Localization.BuiltinServerText.BaseKeys
        .First(k => ArgsOf(ZhServer(k)).SetEquals(args) && !ZhServer(k).Contains("{{"));

    [Fact]
    public async Task List_Factory_Completeness()
    {
        var uiTotal = Application.Common.Localization.BuiltinLocaleResources.BaseKeys.Count;
        var serverTotal = Application.Common.Localization.BuiltinServerText.BaseKeys.Count;
        foreach (var r in (await List()).GetProperty("locales").EnumerateArray())
        {
            var c = r.GetProperty("completeness");
            (c.GetProperty("ui").GetProperty("have").GetInt32(), c.GetProperty("ui").GetProperty("total").GetInt32()).Should().Be((uiTotal, uiTotal));
            (c.GetProperty("server").GetProperty("have").GetInt32(), c.GetProperty("server").GetProperty("total").GetInt32()).Should().Be((serverTotal, serverTotal));
            c.GetProperty("topMissing").GetArrayLength().Should().Be(0);
            foreach (var segment in new[] { "ui", "server" })
                foreach (var f in ReviewFields)
                    r.GetProperty("review").GetProperty(segment).GetProperty(f).GetInt32().Should().Be(0, $"{segment}.{f}");
            r.TryGetProperty("builtFor", out var builtFor).Should().BeTrue("builtFor 要明示輸出 null");
            builtFor.ValueKind.Should().Be(JsonValueKind.Null);
        }
    }

    [Fact]
    public async Task List_NewLocale_Completeness()
    {
        await ApplyPackAsync(_app, JaPartial);
        var ja = Row(await List(), "ja");
        ja.GetProperty("completeness").GetProperty("ui").GetProperty("have").GetInt32().Should().Be(30);
        var top = ja.GetProperty("completeness").GetProperty("topMissing").EnumerateArray().ToList();
        top.Should().HaveCountGreaterThan(0).And.HaveCountLessThanOrEqualTo(5);
        top.Select(t => t.GetProperty("missing").GetInt32()).Should().BeInDescendingOrder();
        ja.GetProperty("builtFor").GetString().Should().Be("2026.11");
    }

    [Fact]
    public async Task List_Review_BaseChangedAndOrphaned()
    {
        await ApplyPackAsync(_app, Fixture("th-fix.langpack.json"));
        EditCurrent("th", p =>
        {
            var hashes = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(p.BaseHashesJson)!;
            foreach (var k in hashes["ui"].Keys.ToList()) hashes["ui"][k] = "0000000000000000";
            p.BaseHashesJson = JsonSerializer.Serialize(hashes);
            var ui = JsonSerializer.Deserialize<Dictionary<string, string>>(p.UiJson)!;
            ui["zz.renamedKey"] = "เก่า";
            p.UiJson = JsonSerializer.Serialize(ui);
        });
        var review = Row(await List(), "th").GetProperty("review").GetProperty("ui");
        review.GetProperty("baseChanged").GetInt32().Should().Be(2);
        Keys(review, "baseChangedKeys").Should().Equal("menu.closedApps", "menu.conversions");
        review.GetProperty("orphaned").GetInt32().Should().Be(1);
        Keys(review, "orphanedKeys").Should().Equal("zz.renamedKey");
    }

    [Fact]
    public async Task List_Review_ParamsMismatch()
    {
        await ApplyPackAsync(_app, Fixture("th-fix.langpack.json"));
        var tooMany = ServerKey(0);
        var tooFew = ServerKey(0, 1);
        EditCurrent("th", p => p.ServerJson = JsonSerializer.Serialize(new Dictionary<string, string> { [tooMany] = "{0} และ {1}", [tooFew] = "เฉพาะ {0}" }));
        var review = Row(await List(), "th").GetProperty("review").GetProperty("server");
        review.GetProperty("paramsChanged").GetInt32().Should().Be(2);
        Keys(review, "paramsChangedKeys").Should().BeEquivalentTo(tooMany, tooFew);
        review.GetProperty("runtimeFallback").GetInt32().Should().Be(1);
        Keys(review, "runtimeFallbackKeys").Should().Equal(tooMany);
        review.GetProperty("baseChanged").GetInt32().Should().Be(0, "沒記雜湊也算參數不符，但不算原文已更新");
    }

    [Fact]
    public async Task List_ReadsDb_NotSnapshot()
    {
        await ApplyPackAsync(_app, JaPartial);
        var before = _app.Services.GetRequiredService<LocaleRegistry>().Texts().Overlay("ja")!.PackId;
        EditCurrent("ja", p => p.UiJson = "{\"menu.home\":\"ホーム\",\"zz.gone\":\"x\"}");
        var ja = Row(await List(), "ja");
        ja.GetProperty("review").GetProperty("ui").GetProperty("orphaned").GetInt32().Should().Be(1);
        ja.GetProperty("completeness").GetProperty("ui").GetProperty("have").GetInt32().Should().Be(1, "完整度也讀資料庫，不讀快照");
        _app.Services.GetRequiredService<LocaleRegistry>().Texts().Overlay("ja")!.Ui.Should().HaveCount(30, "快照仍是舊的");
        before.Should().BePositive();
    }
}
