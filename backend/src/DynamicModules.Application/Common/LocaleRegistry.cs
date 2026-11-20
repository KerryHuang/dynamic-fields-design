using System.Collections.Concurrent;
using System.Text.Json;
using DynamicModules.Application.Common.Localization;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Localization;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Common;

/// <summary>一個已安裝的語系（快照裡的一筆）。</summary>
public sealed record InstalledLocaleInfo(string Code, string NativeName, string? QuasarLang, string? FallbackCode, bool Builtin);

/// <summary>一個語系補齊後的介面文字：內容雜湊（<c>uiHash</c>）、扁平 JSON（UTF-8）、鍵數。</summary>
public sealed record UiBundle(string Hash, byte[] Json, int KeyCount);

/// <summary>一個語系的覆寫層（語系包設計 §6.1；i4d1 計畫判斷 10）：介面文字與後端訊息兩份字典（只含基準鍵），<c>PackId</c> 是目前版的版本 token。</summary>
public sealed record LocaleOverlay(IReadOnlyDictionary<string, string> Ui, IReadOnlyDictionary<string, string> Server, long PackId);

/// <summary>一份快照的不可變檢視：清單、上層關係與覆寫層來自同一次 <see cref="Volatile.Read{T}(ref T)"/>（後端訊息整批產生時用，i4d1 計畫判斷 11）。</summary>
public sealed class LocaleTexts
{
    private readonly IReadOnlyDictionary<string, LocaleOverlay> _overlays;

    internal LocaleTexts(IReadOnlyList<InstalledLocaleInfo> infos, IReadOnlyDictionary<string, LocaleOverlay> overlays)
    {
        Infos = infos;
        _overlays = overlays;
    }

    public IReadOnlyList<InstalledLocaleInfo> Infos { get; }

    /// <summary>每個已安裝語系（含停用）與它的退回鏈，順序同清單。</summary>
    public IReadOnlyList<LocaleContext> ContextsFor(string defaultLocale)
    {
        var pairs = Infos.Select(l => (l.Code, l.FallbackCode)).ToList();
        return Infos.Select(l => new LocaleContext(l.Code, LocaleChain.Fallbacks(l.Code, defaultLocale, pairs))).ToArray();
    }

    /// <summary>該語系覆寫層的後端範本；沒有回 null。</summary>
    public string? ServerOverride(string code, string key) =>
        _overlays.TryGetValue(code, out var o) && o.Server.TryGetValue(key, out var t) ? t : null;

    public LocaleOverlay? Overlay(string code) => _overlays.GetValueOrDefault(code);
}

/// <summary>
/// 已安裝語系的快照（singleton；語系包設計 §4.2，i4a 計畫判斷 2–5）。i4b 的介面文字合併表與雜湊也放這裡。
/// <list type="bullet">
/// <item>內建語系一律取自 <see cref="BuiltinLocales"/>，資料表只貢獻 installed 列：從沒載入成功、或讀不到資料庫時至少有內建四語，
/// 政策選項與退回鏈不會變空。順序固定為內建在前、匯入依安裝時間。</item>
/// <item>換新比照 <see cref="CachedSystemPolicy"/>：請求路徑與啟動時呼叫 <see cref="EnsureFreshAsync"/>，只讓一個呼叫跑、其他沿用舊值；
/// TTL、失敗快取與讀值逾時沿用 <see cref="CachedSystemPolicy.Ttl"/>／<see cref="CachedSystemPolicy.FailureTtl"/>／<see cref="CachedSystemPolicy.ReadTimeout"/>。
/// 過期時先查版本戳記（筆數、最大修改時間），沒變只延長有效期；直接改資料庫又不動修改時間的修補偵測不到（已知限制）。</item>
/// <item><see cref="Generation"/> 只在清單內容改變時遞增；政策快照記下正規化時用的 generation，不一致就重載（判斷 5）。</item>
/// <item><see cref="Invalidate"/> 遞增失效計數並設強制重讀旗標（i4d1 計畫判斷 10：本機寫入之後一定重讀，不賭戳記）；刷新開始時記下計數、
/// 發布時已變就把新快照標成過期並保留旗標，刷新途中的失效通知不會被蓋掉（判斷 4）。</item>
/// <item>介面文字（i4b 計畫判斷 5–7）：<see cref="Ui"/> 沿「要求的語系 → 上層語系 → 系統預設 → 繁中」逐鍵補齊，鏈上每個語系是
/// 「覆寫層 → 內建層」兩層（i4d1 計畫判斷 10），<c>uiHash</c> 是補齊結果的內容雜湊。合併結果依（語系、預設語系）快取在快照上；
/// 清單或覆寫層改變時新快照帶空的快取，內建層整個行程不變，所以不必另外失效。</item>
/// <item>覆寫層（<c>Dynamic_LocalePack</c> 的目前版）和清單一起讀；只留已安裝語系、基準鍵以內的鍵；某一列解析失敗就當成那個語系沒有覆寫層並記 Warning。
/// 覆寫層改變不遞增 <see cref="Generation"/>（政策快照只關心清單）。</item>
/// </list>
/// 整份狀態是不可變的 <see cref="Published"/>，所有寫入都經 <see cref="Publish"/> 的 CAS 迴圈，成敗用 <see cref="object.ReferenceEquals"/> 判斷。
/// </summary>
public sealed class LocaleRegistry(IServiceScopeFactory scopes, TimeProvider time, ILogger<LocaleRegistry> logger) : IInstalledLocales
{
    private static readonly IReadOnlyList<InstalledLocaleInfo> Builtin =
        BuiltinLocales.All.Select(b => new InstalledLocaleInfo(b.Code, b.NativeName, b.QuasarLang, null, true)).ToArray();

    /// <summary>
    /// 一次發布的狀態。<c>Stamp</c> 是上次成功讀到的版本戳記（從沒讀過是 null），<c>Loaded</c> 是否曾成功讀過資料庫。
    /// <c>Ui</c> 是這份清單的介面文字合併快取（語系、預設語系）；只延長有效期的 <c>with</c> 沿用同一份，清單改變時換新的空字典。
    /// </summary>
    private sealed record Published(
        IReadOnlyList<InstalledLocaleInfo> Infos, IReadOnlyList<string> Codes, LocaleVersionStamp? Stamp, long Generation,
        DateTimeOffset LoadedAt, bool Failed, bool Loaded, ConcurrentDictionary<(string Code, string Default), UiBundle> Ui,
        IReadOnlyDictionary<string, LocaleOverlay> Overlays, bool ForceReload);

    private static readonly IReadOnlyDictionary<string, LocaleOverlay> NoOverlays = new Dictionary<string, LocaleOverlay>(StringComparer.Ordinal);
    private static readonly Lazy<HashSet<string>> UiBaseKeys = new(() => BuiltinLocaleResources.BaseKeys.ToHashSet(StringComparer.Ordinal));
    private static readonly Lazy<HashSet<string>> ServerBaseKeys = new(() => BuiltinServerText.BaseKeys.ToHashSet(StringComparer.Ordinal));

    private Published _current = new(Builtin, Builtin.Select(l => l.Code).ToArray(), null, 0, DateTimeOffset.MinValue, Failed: false, Loaded: false, NewUiCache(),
        NoOverlays, ForceReload: false);

    private static ConcurrentDictionary<(string Code, string Default), UiBundle> NewUiCache() => new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _invalidations;

    /// <summary>只給測試：刷新的 CAS 第一次嘗試之前呼叫（模擬「檢查後插入」）。</summary>
    public Action? BeforePublishForTests { get; set; }

    /// <summary>全部已安裝語系：內建在前，再依安裝時間。</summary>
    public IReadOnlyList<InstalledLocaleInfo> Locales => Volatile.Read(ref _current).Infos;

    public IReadOnlyList<string> Codes => Volatile.Read(ref _current).Codes;

    /// <summary>目前快照的內容版本：清單改變才遞增。</summary>
    public long Generation => Volatile.Read(ref _current).Generation;

    public InstalledLocaleInfo? Find(string code) => Locales.FirstOrDefault(l => l.Code == code);

    /// <summary>多語文字的退回鏈（要求的語系之後依序試，<see cref="LocaleChain"/>）。</summary>
    public IReadOnlyList<string> FallbacksFor(string locale, string defaultLocale) => Fallbacks(Locales, locale, defaultLocale);

    private static IReadOnlyList<string> Fallbacks(IReadOnlyList<InstalledLocaleInfo> infos, string locale, string defaultLocale) =>
        LocaleChain.Fallbacks(locale, defaultLocale, infos.Select(l => (l.Code, l.FallbackCode)).ToList());

    /// <summary>
    /// 語系補齊後的介面文字（<c>GET i18n/ui/{code}</c>）；沒安裝回 null。鏈是要求的語系加 <see cref="FallbacksFor"/>，截到繁中為止
    /// （繁中內建層完整，到它一定補齊；鏈尾的「其餘已安裝語系」對介面文字沒有意義）。清單與快取字典都取自開頭讀的同一份快照。
    /// </summary>
    public UiBundle? Ui(string code, string defaultLocale)
    {
        var p = Volatile.Read(ref _current);
        if (!p.Codes.Contains(code)) return null;
        return p.Ui.GetOrAdd((code, defaultLocale), key =>
        {
            var chain = new List<string>();
            foreach (var c in Fallbacks(p.Infos, key.Code, key.Default).Prepend(key.Code).Distinct(StringComparer.Ordinal))
            {
                chain.Add(c);
                if (c == I18nText.FallbackLocale) break;
            }
            var layers = chain.SelectMany(c => new[] { p.Overlays.GetValueOrDefault(c)?.Ui, BuiltinLocaleResources.Ui.GetValueOrDefault(c) })
                .OfType<IReadOnlyDictionary<string, string>>();
            var merged = UiTextMerge.Merge(BuiltinLocaleResources.BaseKeys, layers);
            var (hash, json) = UiTextMerge.Serialize(merged);
            return new UiBundle(hash, json, merged.Count);
        });
    }

    /// <summary>（語系、預設語系）的 <c>uiHash</c>；沒安裝回空字串。</summary>
    public string UiHash(string code, string defaultLocale) => Ui(code, defaultLocale)?.Hash ?? "";

    /// <summary>
    /// 語系與它的退回鏈（寄信、通知等不經請求快取的寫入端用；i4a 計畫判斷 23）：呼叫端自己讀的預設語系（例如交易內經 <c>IPolicyReader</c> 讀的）
    /// 與鏈用同一個來源，不混用政策快照。
    /// </summary>
    public LocaleContext ContextFor(string locale, string defaultLocale) => new(locale, FallbacksFor(locale, defaultLocale));

    /// <summary>
    /// 每個已安裝語系（含停用）與它的退回鏈，順序同 <see cref="Codes"/>（後端訊息 <c>AsI18n</c> 用；i4c 計畫判斷 2）：
    /// 只讀一次快照，清單與上層關係不會在中途換成新的一份。
    /// </summary>
    public IReadOnlyList<LocaleContext> ContextsFor(string defaultLocale) => Texts().ContextsFor(defaultLocale);

    /// <summary>目前快照的不可變檢視（清單與覆寫層同一份）。</summary>
    public LocaleTexts Texts()
    {
        var p = Volatile.Read(ref _current);
        return new LocaleTexts(p.Infos, p.Overlays);
    }

    private static bool IsStale(Published p, DateTimeOffset now) =>
        now - p.LoadedAt >= (p.Failed ? CachedSystemPolicy.FailureTtl : CachedSystemPolicy.Ttl);

    /// <summary>過期就換新；另一個呼叫正在換新時直接返回（沿用舊值、不等待）。呼叫端的 token 取消時丟 <see cref="OperationCanceledException"/>，其他例外不往外丟。</summary>
    public async Task EnsureFreshAsync(CancellationToken ct)
    {
        if (!IsStale(Volatile.Read(ref _current), time.GetUtcNow())) return;
        if (!await _gate.WaitAsync(0, CancellationToken.None)) return;
        try
        {
            var now = time.GetUtcNow();
            var start = Volatile.Read(ref _current);
            if (!IsStale(start, now)) return;
            var invalidationsAtStart = Interlocked.Read(ref _invalidations);
            try
            {
                // 用自己的短 token：呼叫端的 token（如 RequestAborted）不該決定快取的內容
                using var cts = new CancellationTokenSource(CachedSystemPolicy.ReadTimeout, time);
                using var scope = scopes.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<ILocaleRepository>();
                var stamp = await repo.StampAsync(cts.Token);
                IReadOnlyList<InstalledLocaleInfo>? infos = null;
                IReadOnlyDictionary<string, LocaleOverlay>? overlays = null;
                if (!start.Loaded || start.ForceReload || stamp != start.Stamp)
                {
                    infos = Build(await repo.ListAsync(cts.Token));
                    var packs = scope.ServiceProvider.GetRequiredService<ILocalePackRepository>();
                    overlays = OverlaysFrom(await packs.ListCurrentAsync(cts.Token), infos, logger);
                }
                Publish(cur =>
                {
                    // 刷新期間有人失效：照樣發布讀到的內容，但下一個請求一定重讀
                    var invalidated = Interlocked.Read(ref _invalidations) != invalidationsAtStart;
                    var loadedAt = invalidated ? DateTimeOffset.MinValue : now;
                    if (infos is null) return cur with { Stamp = stamp, LoadedAt = loadedAt, Failed = false, ForceReload = invalidated || cur.ForceReload };
                    var changed = !infos.SequenceEqual(cur.Infos);
                    var overlaysChanged = !SameOverlays(overlays!, cur.Overlays);
                    return new Published(changed ? infos : cur.Infos, changed ? infos.Select(l => l.Code).ToArray() : cur.Codes, stamp,
                        changed ? cur.Generation + 1 : cur.Generation, loadedAt, Failed: false, Loaded: true,
                        changed || overlaysChanged ? NewUiCache() : cur.Ui, overlaysChanged ? overlays! : cur.Overlays, ForceReload: invalidated);
                }, BeforePublishForTests);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(e, "讀取已安裝語系失敗，沿用上次的清單");
                // 失敗也不能蓋掉刷新期間收到的失效通知：有新失效就保留「已過期」，資料庫恢復後下一個請求就重讀
                Publish(cur => cur with
                {
                    Failed = true,
                    LoadedAt = Interlocked.Read(ref _invalidations) != invalidationsAtStart ? DateTimeOffset.MinValue : now,
                });
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>語系相關寫入的交易提交後呼叫（i4d1 起）：下一次 <see cref="EnsureFreshAsync"/> 一定重讀。</summary>
    public void Invalidate()
    {
        Interlocked.Increment(ref _invalidations);
        Publish(cur => cur with { LoadedAt = DateTimeOffset.MinValue, ForceReload = true });
    }

    /// <summary>
    /// 內建四列加資料表的 installed 列（快照與 i4d1 的 <c>LocaleWorldReader</c> 共用同一套規則）；Quasar 語言包不在清單內的
    /// <c>QuasarLang</c>（只可能來自資料修補）當成 null。
    /// </summary>
    internal static IReadOnlyList<InstalledLocaleInfo> InfosFrom(IEnumerable<InstalledLocale> rows) =>
        Builtin.Concat(InstalledRows(rows)
                .Select(r => new InstalledLocaleInfo(r.Code, r.NativeName, KnownQuasarLang(r.QuasarLang), r.FallbackCode, false)))
            .ToArray();

    private static IEnumerable<InstalledLocale> InstalledRows(IEnumerable<InstalledLocale> rows) =>
        rows.Where(r => r.Origin == LocaleOrigin.Installed && !BuiltinLocales.Contains(r.Code));

    private static string? KnownQuasarLang(string? lang) => lang is null || BuiltinLocaleResources.QuasarLangs.Contains(lang) ? lang : null;

    /// <summary><see cref="InfosFrom"/> 加上 Quasar 語言包不存在的 Warning。</summary>
    private IReadOnlyList<InstalledLocaleInfo> Build(IReadOnlyList<InstalledLocale> rows)
    {
        foreach (var r in InstalledRows(rows).Where(r => r.QuasarLang is not null && KnownQuasarLang(r.QuasarLang) is null))
            logger.LogWarning("語系 {Code} 的 Quasar 語言包 {QuasarLang} 不存在，改用預設語言包", r.Code, r.QuasarLang);
        return InfosFrom(rows);
    }

    /// <summary>
    /// 解析各語系目前版的覆寫層：只留已安裝語系、基準鍵以內的鍵；解析失敗（只可能是資料修補）就當成那個語系沒有覆寫層並記 Warning。
    /// 快照與 i4d1 的 <c>LocaleWorldReader</c> 共用同一套規則。
    /// </summary>
    internal static IReadOnlyDictionary<string, LocaleOverlay> OverlaysFrom(IReadOnlyList<LocalePack> packs, IReadOnlyList<InstalledLocaleInfo> infos, ILogger logger)
    {
        var installed = infos.Select(i => i.Code).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, LocaleOverlay>(StringComparer.Ordinal);
        foreach (var pack in packs.Where(p => installed.Contains(p.Code)))
        {
            try
            {
                result[pack.Code] = new LocaleOverlay(ParseLayer(pack.UiJson, UiBaseKeys.Value), ParseLayer(pack.ServerJson, ServerBaseKeys.Value), pack.Id);
            }
            catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException)
            {
                logger.LogWarning(e, "語系 {Code} 的覆寫層（版本 {PackId}）無法解析，當成沒有覆寫層", pack.Code, pack.Id);
            }
        }
        return result;
    }

    /// <summary>扁平 JSON 物件（字串 → 字串）；非字串值、根不是物件丟 <see cref="JsonException"/>。基準鍵以外與空字串丟掉。</summary>
    private static IReadOnlyDictionary<string, string> ParseLayer(string json, HashSet<string> baseKeys)
    {
        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? throw new JsonException("覆寫層是 null");
        var layer = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, text) in parsed)
            if (baseKeys.Contains(key) && !string.IsNullOrEmpty(text)) layer[key] = text;
        return layer;
    }

    /// <summary>兩份覆寫層是否同一組版本（比語系與 <see cref="LocaleOverlay.PackId"/>）。</summary>
    private static bool SameOverlays(IReadOnlyDictionary<string, LocaleOverlay> a, IReadOnlyDictionary<string, LocaleOverlay> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var o) && o.PackId == kv.Value.PackId);

    /// <summary>CAS 迴圈：以發布當下的最新物件算出下一份；交換成敗用參考比較，不用 record 的值相等。</summary>
    private void Publish(Func<Published, Published> next, Action? beforeFirstCas = null)
    {
        var first = true;
        while (true)
        {
            var cur = Volatile.Read(ref _current);
            var n = next(cur);
            if (first)
            {
                first = false;
                beforeFirstCas?.Invoke();
            }
            if (ReferenceEquals(Interlocked.CompareExchange(ref _current, n, cur), cur)) return;
        }
    }
}
