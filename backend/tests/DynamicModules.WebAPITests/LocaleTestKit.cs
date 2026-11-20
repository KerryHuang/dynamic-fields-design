using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.LocaleService.Commands.ApplyLocalePack;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.WebAPITests;

/// <summary>
/// 測試用的「安裝／移除一個語系」（i4a 計畫判斷 21）：直接寫 <c>Dynamic_Locale</c> 一列，再讓語系快照立刻換新
/// （政策快照看到 generation 不同，下一個請求就重載）。i4a 沒有安裝 API，這是唯一的造資料方式；i4d1 有 API 後可改走 API。
/// </summary>
internal static class LocaleTestKit
{
    public static async Task InstallAsync(TestApp app, string code, string nativeName, string? fallback = null)
    {
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            var now = DateTime.UtcNow;
            db.Set<InstalledLocale>().Add(new InstalledLocale
            {
                Code = code, NativeName = nativeName, FallbackCode = fallback, Origin = LocaleOrigin.Installed, CreatedUtc = now, ModifiedUtc = now,
            });
            await db.SaveChangesAsync();
        }
        await RefreshAsync(app, codes => codes.Contains(code));
    }

    public static async Task RemoveAsync(TestApp app, string code)
    {
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<InstalledLocale>().Where(l => l.Code == code).ExecuteDeleteAsync();
        await RefreshAsync(app, codes => !codes.Contains(code));
    }

    /// <summary>
    /// 等到快照真的反映這次寫入。另一個請求正在換新時 <see cref="LocaleRegistry.EnsureFreshAsync"/> 直接返回（設計如此），
    /// 那次換新可能在寫入前就讀完，所以要重試到看見結果；不重試的話下一個請求會拿到舊快照（CI 偶發）。
    /// </summary>
    private static Task RefreshAsync(TestApp app, Func<IReadOnlyList<string>, bool> done) =>
        WaitForSnapshotAsync(app, texts => done(texts.Infos.Select(i => i.Code).ToList()));

    /// <summary>
    /// 共用的等待（i4d1 計畫判斷 21）：<c>Invalidate</c> → <c>EnsureFreshAsync</c> → 判斷，20 ms 重試、30 秒逾時。
    /// CI 的 PostgreSQL 偶發 10 秒逾時、根因未證實，先放寬並在逾時訊息附上 <paramref name="describe"/> 對最後一次快照的描述、
    /// 快照 generation、嘗試次數與總耗時（CI 偶發診斷）。
    /// </summary>
    public static async Task WaitForSnapshotAsync(TestApp app, Func<LocaleTexts, bool> done, Func<LocaleTexts, string>? describe = null)
    {
        var registry = app.Services.GetRequiredService<LocaleRegistry>();
        var started = DateTime.UtcNow;
        var deadline = started.AddSeconds(30);
        var attempts = 0;
        while (true)
        {
            attempts++;
            registry.Invalidate();
            await registry.EnsureFreshAsync(CancellationToken.None);
            var texts = registry.Texts();
            if (done(texts)) return;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"語系快照 30 秒內沒有反映寫入；{(describe is null ? "" : describe(texts) + "；")}"
                    + $"generation={registry.Generation}、嘗試 {attempts} 次、耗時 {(DateTime.UtcNow - started).TotalMilliseconds:F0} ms");
            await Task.Delay(20);
        }
    }

    private static string PackIdState(LocaleTexts t, string code, long? expected) =>
        $"預期 {code} 的 packId={expected?.ToString() ?? "null"}、實際 Overlay({code})?.PackId={t.Overlay(code)?.PackId.ToString() ?? "null"}、清單含 {code}={t.Infos.Any(i => i.Code == code)}";

    // ── 語系包 API（i4d1 計畫判斷 21）：寫入後都等快照反映「預期的結果」 ──

    public const string I18n = "/api/v3/dynamic/i18n";

    /// <summary><c>backend/tests/Fixtures/langpacks/</c> 的測試檔。</summary>
    public static byte[] Fixture(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "shared", "locale-chain-cases.json"))) dir = dir.Parent;
        return File.ReadAllBytes(Path.Combine(dir!.FullName, "backend", "tests", "Fixtures", "langpacks", name));
    }

    /// <summary>組一份語系包 JSON；<paramref name="ui"/>、<paramref name="server"/> 是 null 就不帶那一段。</summary>
    public static string Pack(string code, string? nativeName = null, string? fallback = null,
        IReadOnlyDictionary<string, string>? ui = null, IReadOnlyDictionary<string, string>? server = null, string? version = null)
    {
        var locale = new JsonObject { ["code"] = code };
        if (nativeName is not null) locale["nativeName"] = nativeName;
        if (fallback is not null) locale["fallback"] = fallback;
        var root = new JsonObject { ["format"] = "formwright-langpack", ["formatVersion"] = 1, ["locale"] = locale };
        if (version is not null) root["pack"] = new JsonObject { ["version"] = version };
        if (ui is not null) root["ui"] = new JsonObject(ui.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));
        if (server is not null) root["server"] = new JsonObject(server.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));
        return root.ToJsonString();
    }

    public static Task<HttpResponseMessage> PreviewAsync(HttpClient c, byte[] body, string fileName = "t.langpack.json") =>
        c.PostAsync($"{I18n}/packs/preview?fileName={Uri.EscapeDataString(fileName)}", new ByteArrayContent(body));

    /// <summary>預覽並回報告（要成功）。</summary>
    public static async Task<JsonElement> PreviewOkAsync(HttpClient c, byte[] body, string fileName = "t.langpack.json")
    {
        var res = await PreviewAsync(c, body, fileName);
        var text = await res.Content.ReadAsStringAsync();
        if (res.StatusCode != HttpStatusCode.OK) throw new HttpRequestException($"預覽失敗 {(int)res.StatusCode}：{text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public static Task<HttpResponseMessage> ApplyAsync(HttpClient c, JsonElement report) =>
        c.PostAsJsonAsync($"{I18n}/packs/apply", new { previewId = report.GetProperty("previewId").GetString(), fileHash = report.GetProperty("fileHash").GetString() });

    public static Task<ApplyLocalePackResponse> ApplyPackAsync(TestApp app, string json, string fileName = "t.langpack.json", HttpClient? client = null) =>
        ApplyPackAsync(app, Encoding.UTF8.GetBytes(json), fileName, client);

    /// <summary>預覽＋套用；等到快照的清單含這個語系、目前版是回應的 <c>packId</c>（<c>noChanges</c> 時即原版本仍在）。</summary>
    public static async Task<ApplyLocalePackResponse> ApplyPackAsync(TestApp app, byte[] body, string fileName = "t.langpack.json", HttpClient? client = null)
    {
        var c = client ?? app.AsUser();
        var res = await ApplyAsync(c, await PreviewOkAsync(c, body, fileName));
        var text = await res.Content.ReadAsStringAsync();
        if (res.StatusCode != HttpStatusCode.OK) throw new HttpRequestException($"套用失敗 {(int)res.StatusCode}：{text}");
        var r = JsonSerializer.Deserialize<ApplyLocalePackResponse>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        await WaitForSnapshotAsync(app, t => t.Infos.Any(i => i.Code == r.Code) && t.Overlay(r.Code)?.PackId == r.PackId,
            t => PackIdState(t, r.Code, r.PackId));
        return r;
    }

    /// <summary>回到上一版（要成功），等到目前版是回應的 <c>packId</c>。</summary>
    public static Task<long> RollbackAsync(TestApp app, string code) => SwitchAsync(app, code, "rollback");

    /// <summary>移除覆寫（要成功），等到目前版是回應的 <c>packId</c>。</summary>
    public static Task<long> ClearAsync(TestApp app, string code) => SwitchAsync(app, code, "clear");

    private static async Task<long> SwitchAsync(TestApp app, string code, string action)
    {
        var res = await app.AsUser().PostAsync($"{I18n}/locales/{code}/{action}", null);
        var text = await res.Content.ReadAsStringAsync();
        if (res.StatusCode != HttpStatusCode.OK) throw new HttpRequestException($"{action} 失敗 {(int)res.StatusCode}：{text}");
        var packId = JsonDocument.Parse(text).RootElement.GetProperty("packId").GetInt64();
        await WaitForSnapshotAsync(app, t => t.Overlay(code)?.PackId == packId, t => $"{action}：" + PackIdState(t, code, packId));
        return packId;
    }

    /// <summary>移除語系（要 204），等到清單不含它、也沒有覆寫層。</summary>
    public static async Task<HttpResponseMessage> RemoveLocaleAsync(TestApp app, string code)
    {
        var res = await app.AsUser().DeleteAsync($"{I18n}/locales/{code}");
        if (res.StatusCode != HttpStatusCode.NoContent)
            throw new HttpRequestException($"移除失敗 {(int)res.StatusCode}：{await res.Content.ReadAsStringAsync()}");
        await WaitForSnapshotAsync(app, t => t.Infos.All(i => i.Code != code) && t.Overlay(code) is null);
        return res;
    }
}
