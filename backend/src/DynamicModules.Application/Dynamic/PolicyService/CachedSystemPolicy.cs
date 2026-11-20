using System.Collections.Frozen;
using System.Globalization;
using DynamicModules.Domain.Policies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.PolicyService;

/// <summary>
/// 全系統政策的單一快照（singleton、跨請求快取；政策設計 p3 計畫判斷 3、19、20）。一次讀齊 <see cref="PolicyCatalog.All"/> 中全部
/// System 範圍政策，給不能 await 的同步程式用（<see cref="Get"/>、<see cref="BusinessOffset"/>）；讀值入口仍是 <see cref="IPolicyReader"/>。
/// <list type="bullet">
/// <item>過期重載：請求路徑（middleware）與啟動時呼叫 <see cref="EnsureFreshAsync"/>；只讓一個呼叫跑，其他沿用舊值、不等待。</item>
/// <item>系統 PUT 的交易提交後由 <c>AfterCommitActions</c> 呼叫 <see cref="Apply"/> 寫入新值；序號只在比那一項目前的序號新時才套用，
/// 擋掉逆序或被重載涵蓋過的 callback。</item>
/// <item>讀不到資料庫時（例外或逾時）記 Warning、不讓請求失敗：沿用上次成功的值，從沒成功過才用部署出廠值；失敗結果快取
/// <see cref="FailureTtl"/>。與 O1 的差異：O1 讀不到時回程式出廠值；時區或語系在短暫故障時跳回出廠值會用錯偏移寫入索引，所以改成沿用。
/// 已知限制：持續讀不到時沿用沒有時間上限；冷啟動就讀不到時用部署出廠值（設計 §10）。</item>
/// </list>
/// 整份狀態是不可變的 <see cref="Published"/>，所有寫入都經 <see cref="Publish"/> 的 CAS 迴圈。<see cref="Published"/> 是 record，
/// <c>!=</c> 走值相等比較：期間被換成「內容相同的另一個物件」時會誤判交換成功、把新值丟掉，所以交換成敗一律用 <see cref="object.ReferenceEquals"/> 判斷。
/// </summary>
public sealed class CachedSystemPolicy(IServiceScopeFactory scopes, TimeProvider time, PolicyEnvironmentDefaults env, ILogger<CachedSystemPolicy> logger)
{
    // 以下三個值皆為技術限制（設計 §12；失敗快取與讀值逾時見 O1 計畫判斷 J12）
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan FailureTtl = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

    private static readonly PolicyDefinition[] SystemPolicies = PolicyCatalog.All.Where(d => d.Scope == PolicyScope.System).ToArray();

    /// <summary>
    /// 一次發布的狀態。<c>Seq</c> 是每一項最後套用的更新序號，<c>ApplyCount</c> 是 <see cref="Apply"/> 成功的累計次數
    /// （重載期間有 Apply 就整份丟掉），<c>Loaded</c> 是否曾成功讀過資料庫。
    /// </summary>
    public sealed record Published(
        FrozenDictionary<string, string> Values, FrozenDictionary<string, long> Seq, long ApplyCount, TimeSpan BusinessOffset,
        DateTimeOffset LoadedAt, bool Failed, bool Loaded);

    private Published _current = Initial(env);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>已配發的更新序號（可能屬於還沒提交的 PUT）。</summary>
    private long _seq;

    /// <summary>已提交的最大序號：只在 <see cref="Apply"/>（提交後才執行）裡以 max 更新。不變式：大於等於 s 時，序號 s 的 PUT 一定已提交。</summary>
    private long _committedSeq;

    /// <summary>只給測試：重載的 CAS 第一次嘗試之前呼叫（模擬「檢查後插入」）。</summary>
    public Action? BeforePublishForTests { get; set; }

    /// <summary>只給測試：直接換掉目前的狀態（驗證 CAS 用參考比較）。</summary>
    public void ReplaceCurrentForTests(Func<Published, Published> next) => Interlocked.Exchange(ref _current, next(Volatile.Read(ref _current)));

    private static Published Initial(PolicyEnvironmentDefaults env)
    {
        var values = SystemPolicies.ToFrozenDictionary(d => d.Code, env.FactoryOf, StringComparer.Ordinal);
        return new Published(values, FrozenDictionary<string, long>.Empty, 0, OffsetOf(values, env), DateTimeOffset.MinValue, Failed: false, Loaded: false);
    }

    private static TimeSpan OffsetOf(IReadOnlyDictionary<string, string> values, PolicyEnvironmentDefaults env) =>
        PolicyCatalog.UtcOffsetOf(PolicyCatalog.LocaleTimeZone.Normalize(values.GetValueOrDefault(PolicyCatalog.LocaleTimeZone.Code))
                                  ?? env.FactoryOf(PolicyCatalog.LocaleTimeZone));

    /// <summary>全系統政策目前的生效值（標準字串）；只收 System 範圍。沒載入過是部署出廠值。</summary>
    public string Get(PolicyDefinition def)
    {
        if (def.Scope != PolicyScope.System) throw new InvalidOperationException($"{def.Code} 不是全系統政策");
        return Volatile.Read(ref _current).Values.TryGetValue(def.Code, out var v) ? v : env.FactoryOf(def);
    }

    /// <summary>營運時區偏移（政策 locale.timeZone）；唯一來源，<c>IBusinessTime</c> 不帶偏移。</summary>
    public TimeSpan BusinessOffset => Volatile.Read(ref _current).BusinessOffset;

    /// <summary>欄位值顯示用的環境（語系＋目前的營運時區偏移）。</summary>
    public Display.FieldDisplayContext Display(string locale) => new(locale, BusinessOffset);

    public async Task<int> IntAsync(PolicyDefinition def, CancellationToken ct)
    {
        Expect(def, PolicyKind.Integer);
        await EnsureFreshAsync(ct);
        return int.Parse(Get(def), NumberStyles.None, CultureInfo.InvariantCulture);
    }

    public async Task<bool> BoolAsync(PolicyDefinition def, CancellationToken ct)
    {
        Expect(def, PolicyKind.Boolean);
        await EnsureFreshAsync(ct);
        return Get(def) == "true";
    }

    private static void Expect(PolicyDefinition def, PolicyKind kind)
    {
        if (def.Scope != PolicyScope.System) throw new InvalidOperationException($"{def.Code} 不是全系統政策");
        if (def.Kind != kind) throw new InvalidOperationException($"{def.Code} 不是 {kind}");
    }

    /// <summary>只由系統 PUT 在交易內（已持有寫入鎖）呼叫；序號順序等於提交順序。</summary>
    public long NextSequence() => Interlocked.Increment(ref _seq);

    private static bool IsStale(Published p, DateTimeOffset now) => now - p.LoadedAt >= (p.Failed ? FailureTtl : Ttl);

    /// <summary>過期就重載；另一個呼叫正在重載時直接返回（沿用舊值、不等待）。呼叫端的 token 取消時丟 <see cref="OperationCanceledException"/>，其他例外不往外丟。</summary>
    public async Task EnsureFreshAsync(CancellationToken ct)
    {
        if (!IsStale(Volatile.Read(ref _current), time.GetUtcNow())) return;
        if (!await _gate.WaitAsync(0, CancellationToken.None)) return;
        try
        {
            var now = time.GetUtcNow();
            var start = Volatile.Read(ref _current);
            if (!IsStale(start, now)) return;
            // 讀資料庫之前取（不用已配發的 _seq）：序號不大於它的 PUT 都已提交，這次一定讀得到
            var committedAtStart = Volatile.Read(ref _committedSeq);
            try
            {
                // 用自己的短 token：呼叫端的 token（如 RequestAborted）不該決定快取的內容
                using var cts = new CancellationTokenSource(ReadTimeout, time);
                using var scope = scopes.CreateScope();
                var reader = scope.ServiceProvider.GetRequiredService<IPolicyReader>();
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var def in SystemPolicies)
                    values[def.Code] = (await reader.ResolveAsync(def, null, cts.Token)).Effective;
                var frozen = values.ToFrozenDictionary(StringComparer.Ordinal);
                var offset = OffsetOf(frozen, env);
                // 期間有 Apply 就整份丟掉（LoadedAt 不動，下一個請求再重載）
                Publish(cur => cur.ApplyCount != start.ApplyCount
                    ? null
                    : new Published(frozen, StampSeq(cur.Seq, committedAtStart), cur.ApplyCount, offset, now, Failed: false, Loaded: true),
                    BeforePublishForTests);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(e, "讀取全系統政策失敗，沿用上次的值");
                // 以發布當下的物件為基礎：期間 Apply 的值保留
                Publish(cur => cur with { Failed = true, LoadedAt = now });
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 重載時每一項的序號標成 max(原序號, 已提交的最大序號)：被重載涵蓋的 PUT 晚到的 callback 會被擋掉；
    /// 比它大的 PUT（含已配發、還沒提交的）callback 照常套用。兩筆都已提交、callback 都還沒跑時會短暫套用較舊那筆（判斷 20 的已知窗口）。
    /// </summary>
    private static FrozenDictionary<string, long> StampSeq(FrozenDictionary<string, long> seq, long committed) =>
        SystemPolicies.ToFrozenDictionary(d => d.Code, d => Math.Max(seq.GetValueOrDefault(d.Code), committed), StringComparer.Ordinal);

    /// <summary>系統 PUT 提交後寫入新值（<c>AfterCommitActions</c> 的 callback）；序號不比那一項目前的新就不套用，並標成過期讓下一個請求以資料庫為準重載。</summary>
    public void Apply(PolicyDefinition def, string effective, long seq)
    {
        // 第一步：以 max 更新已提交的最大序號（callback 只在提交後執行，不違反不變式；long 是值型別，!= 正確）
        long c;
        do
        {
            c = Volatile.Read(ref _committedSeq);
            if (seq <= c) break;
        } while (Interlocked.CompareExchange(ref _committedSeq, seq, c) != c);

        // 第二步：發布
        var applied = false;
        Publish(cur =>
        {
            applied = seq > cur.Seq.GetValueOrDefault(def.Code);
            if (!applied) return null;
            var values = Replace(cur.Values, def.Code, effective);
            return cur with
            {
                Values = values,
                Seq = Replace(cur.Seq, def.Code, seq),
                ApplyCount = cur.ApplyCount + 1,
                BusinessOffset = def.Code == PolicyCatalog.LocaleTimeZone.Code ? OffsetOf(values, env) : cur.BusinessOffset,
            };
        });
        if (!applied) Publish(cur => cur with { LoadedAt = DateTimeOffset.MinValue });
    }

    private static FrozenDictionary<string, T> Replace<T>(FrozenDictionary<string, T> source, string key, T value) =>
        new Dictionary<string, T>(source, StringComparer.Ordinal) { [key] = value }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>CAS 迴圈：以發布當下的最新物件算出下一份（回 null 表示放棄）；交換成敗用參考比較，不用 record 的值相等。</summary>
    private void Publish(Func<Published, Published?> next, Action? beforeFirstCas = null)
    {
        var first = true;
        while (true)
        {
            var cur = Volatile.Read(ref _current);
            var n = next(cur);
            if (n is null) return;
            if (first)
            {
                first = false;
                beforeFirstCas?.Invoke();
            }
            if (ReferenceEquals(Interlocked.CompareExchange(ref _current, n, cur), cur)) return;
        }
    }
}
