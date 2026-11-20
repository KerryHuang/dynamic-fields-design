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

/// <summary>
/// 已安裝語系的快照（singleton；語系包設計 §4.2，i4a 計畫判斷 2–5）。i4b 的介面文字合併表與雜湊也放這裡。
/// <list type="bullet">
/// <item>內建語系一律取自 <see cref="BuiltinLocales"/>，資料表只貢獻 installed 列：從沒載入成功、或讀不到資料庫時至少有內建四語，
/// 政策選項與退回鏈不會變空。順序固定為內建在前、匯入依安裝時間。</item>
/// <item>換新比照 <see cref="CachedSystemPolicy"/>：請求路徑與啟動時呼叫 <see cref="EnsureFreshAsync"/>，只讓一個呼叫跑、其他沿用舊值；
/// TTL、失敗快取與讀值逾時沿用 <see cref="CachedSystemPolicy.Ttl"/>／<see cref="CachedSystemPolicy.FailureTtl"/>／<see cref="CachedSystemPolicy.ReadTimeout"/>。
/// 過期時先查版本戳記（筆數、最大修改時間），沒變只延長有效期；直接改資料庫又不動修改時間的修補偵測不到（已知限制）。</item>
/// <item><see cref="Generation"/> 只在清單內容改變時遞增；政策快照記下正規化時用的 generation，不一致就重載（判斷 5）。</item>
/// <item><see cref="Invalidate"/> 遞增失效計數；刷新開始時記下、發布時已變就把新快照標成過期，刷新途中的失效通知不會被蓋掉（判斷 4）。</item>
/// </list>
/// 整份狀態是不可變的 <see cref="Published"/>，所有寫入都經 <see cref="Publish"/> 的 CAS 迴圈，成敗用 <see cref="object.ReferenceEquals"/> 判斷。
/// </summary>
public sealed class LocaleRegistry(IServiceScopeFactory scopes, TimeProvider time, ILogger<LocaleRegistry> logger) : IInstalledLocales
{
    private static readonly IReadOnlyList<InstalledLocaleInfo> Builtin =
        BuiltinLocales.All.Select(b => new InstalledLocaleInfo(b.Code, b.NativeName, b.QuasarLang, null, true)).ToArray();

    /// <summary>一次發布的狀態。<c>Stamp</c> 是上次成功讀到的版本戳記（從沒讀過是 null），<c>Loaded</c> 是否曾成功讀過資料庫。</summary>
    private sealed record Published(
        IReadOnlyList<InstalledLocaleInfo> Infos, IReadOnlyList<string> Codes, LocaleVersionStamp? Stamp, long Generation,
        DateTimeOffset LoadedAt, bool Failed, bool Loaded);

    private Published _current = new(Builtin, Builtin.Select(l => l.Code).ToArray(), null, 0, DateTimeOffset.MinValue, Failed: false, Loaded: false);
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
    public IReadOnlyList<string> FallbacksFor(string locale, string defaultLocale) =>
        LocaleChain.Fallbacks(locale, defaultLocale, Locales.Select(l => (l.Code, l.FallbackCode)).ToList());

    /// <summary>
    /// 語系與它的退回鏈（寄信、通知等不經請求快取的寫入端用；i4a 計畫判斷 23）：呼叫端自己讀的預設語系（例如交易內經 <c>IPolicyReader</c> 讀的）
    /// 與鏈用同一個來源，不混用政策快照。
    /// </summary>
    public LocaleContext ContextFor(string locale, string defaultLocale) => new(locale, FallbacksFor(locale, defaultLocale));

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
                var infos = start.Loaded && stamp == start.Stamp ? null : Build(await repo.ListAsync(cts.Token));
                Publish(cur =>
                {
                    // 刷新期間有人失效：照樣發布讀到的內容，但下一個請求一定重讀
                    var loadedAt = Interlocked.Read(ref _invalidations) != invalidationsAtStart ? DateTimeOffset.MinValue : now;
                    if (infos is null) return cur with { Stamp = stamp, LoadedAt = loadedAt, Failed = false };
                    var changed = !infos.SequenceEqual(cur.Infos);
                    return new Published(changed ? infos : cur.Infos, changed ? infos.Select(l => l.Code).ToArray() : cur.Codes, stamp,
                        changed ? cur.Generation + 1 : cur.Generation, loadedAt, Failed: false, Loaded: true);
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
        Publish(cur => cur with { LoadedAt = DateTimeOffset.MinValue });
    }

    private static IReadOnlyList<InstalledLocaleInfo> Build(IEnumerable<InstalledLocale> rows) =>
        Builtin.Concat(rows.Where(r => r.Origin == LocaleOrigin.Installed && !BuiltinLocales.Contains(r.Code))
                .Select(r => new InstalledLocaleInfo(r.Code, r.NativeName, r.QuasarLang, r.FallbackCode, false)))
            .ToArray();

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
