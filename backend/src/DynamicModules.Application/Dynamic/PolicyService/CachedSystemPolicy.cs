using System.Collections.Concurrent;
using System.Globalization;
using DynamicModules.Domain.Policies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.PolicyService;

/// <summary>
/// 請求路徑（middleware）用的全系統政策讀值：singleton、跨請求快取。
/// 讀不到資料庫時回出廠值並記 Warning，不讓請求失敗；失敗結果也快取，避免資料庫故障時每個請求都再連一次。
/// </summary>
public sealed class CachedSystemPolicy(IServiceScopeFactory scopes, TimeProvider time, ILogger<CachedSystemPolicy> logger)
{
    // 以下三個值皆為技術限制（設計 §12；失敗快取與讀值逾時見 O1 計畫判斷 J12）
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan FailureTtl = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<string, (string Value, DateTimeOffset At, bool Failed)> _cache = new(StringComparer.Ordinal);

    public async Task<int> IntAsync(PolicyDefinition def, CancellationToken ct) =>
        int.Parse(await ValueAsync(def, PolicyKind.Integer, ct), NumberStyles.None, CultureInfo.InvariantCulture);

    public async Task<bool> BoolAsync(PolicyDefinition def, CancellationToken ct) =>
        await ValueAsync(def, PolicyKind.Boolean, ct) == "true";

    private async Task<string> ValueAsync(PolicyDefinition def, PolicyKind kind, CancellationToken ct)
    {
        if (def.Scope != PolicyScope.System) throw new InvalidOperationException($"{def.Code} 不是全系統政策");
        if (def.Kind != kind) throw new InvalidOperationException($"{def.Code} 不是 {kind}");
        var now = time.GetUtcNow();
        if (_cache.TryGetValue(def.Code, out var hit) && now - hit.At < (hit.Failed ? FailureTtl : Ttl))
            return hit.Value;
        try
        {
            // 用自己的短 token：呼叫端的 token（如 RequestAborted）不該決定快取的內容
            using var cts = new CancellationTokenSource(ReadTimeout, time);
            using var scope = scopes.CreateScope();
            var effective = (await scope.ServiceProvider.GetRequiredService<IPolicyReader>()
                .ResolveAsync(def, null, cts.Token)).Effective;
            _cache[def.Code] = (effective, now, false);
            return effective;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(e, "讀取政策 {Code} 失敗，暫用出廠值", def.Code);
            _cache[def.Code] = (def.Factory, now, true);
            return def.Factory;
        }
    }
}
