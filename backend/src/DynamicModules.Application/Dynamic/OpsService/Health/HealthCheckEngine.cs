using System.Collections.Concurrent;
using System.Diagnostics;
using DynamicModules.Application.Common.Observability;
using DynamicModules.Application.Dynamic.OpsService.Alerts;
using DynamicModules.Domain.Entities.Dynamic;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.OpsService.Health;

/// <summary>一次檢查的結果（§17.2.4 第 1 步）：<see cref="TransitionId"/> 之後重試、補寫、直送都用同一個。</summary>
public sealed record HealthObservation(
    string Code, string TransitionId, DateTime CheckedUtc, HealthStatus Status, long? Value, string? MessageCode, int DurationMs, string InstanceId);

/// <summary>待補寫佇列的一筆（只在記憶體）。</summary>
internal sealed class PendingHealthWrite(HealthObservation observation, DateTime? nextDueUtc, HealthStatus? previous)
{
    public HealthObservation Observation { get; set; } = observation;
    public DateTime? NextDueUtc { get; set; } = nextDueUtc;
    /// <summary>本實例上一筆結果的狀態（直送判斷新開或升級用）。</summary>
    public HealthStatus? Previous { get; } = previous;
    /// <summary>嘗試寫過一次的不再合併（它可能其實已提交，§17.2.4 第 3 步）。</summary>
    public bool Attempted { get; set; }
    /// <summary>已交給直送佇列判斷過（寫不進資料庫時只判斷一次）。</summary>
    public bool DirectConsidered { get; set; }
}

/// <summary>
/// 本實例的健康檢查執行器（維運設計 §17.2.4，singleton；看門狗與 <c>ops.monitor</c> 共用）：
/// 跑一個檢查（最長 60 秒）→ 更新記憶體的狀態板 → 放進待補寫佇列 → 依檢查時間先後逐筆走彙總交易。
/// 寫不進去或提交結果不明就停在那一筆，下一輪從那一筆接著補（已提交的會被去重略過）。
/// 佇列同一檢查狀態沒變的連續結果只更新最後一筆，狀態有變才新增；上限 500 筆，滿了丟最舊的。
/// 寫不進去的結果交給直送佇列（§17.2.6：開啟、升級改由看門狗直接寄信）；補寫時帶「同一檢查後面已經結案」給佇列列判斷過時。
/// </summary>
public sealed class HealthCheckEngine(
    IServiceScopeFactory scopes, IEnumerable<IHealthProbe> probes, TimeProvider time, OpsInstance instance, DirectAlertQueue direct,
    ILogger<HealthCheckEngine> logger)
{
    /// <summary>待補寫佇列上限（技術限制，§17.9）。</summary>
    public const int MaxPending = 500;

    private readonly Dictionary<string, IHealthProbe> _probes = probes.ToDictionary(p => p.Code, StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HealthObservation> _board = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _counters = new(StringComparer.Ordinal);
    private readonly List<PendingHealthWrite> _pending = [];
    /// <summary>每個檢查目前在跑的次數（看門狗、<c>ops.monitor</c>、立即檢查共用；鎖 <see cref="_sync"/>）。</summary>
    private readonly Dictionary<string, int> _running = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private readonly SemaphoreSlim _flush = new(1, 1);
    private long _dropped;

    public string InstanceId => instance.InstanceId;

    /// <summary>狀態板：本實例每個檢查的最新結果。</summary>
    public IReadOnlyDictionary<string, HealthObservation> Board => _board;

    public int PendingCount
    {
        get { lock (_sync) return _pending.Count; }
    }

    public long PendingDropped => Interlocked.Read(ref _dropped);

    /// <summary>待補寫佇列目前的內容（依順序，給監控頁與測試）。</summary>
    public IReadOnlyList<HealthObservation> PendingSnapshot()
    {
        lock (_sync) return [.. _pending.Select(p => p.Observation)];
    }

    /// <summary>跑一個檢查並寫入（寫不進去留在佇列）；nextDueUtc 只有排程型的正常執行才給。</summary>
    public async Task<HealthObservation> RunAsync(HealthCheckDefinition def, EffectiveHealthSettings settings, DateTime? nextDueUtc, CancellationToken ct)
    {
        lock (_sync) _running[def.Code] = _running.GetValueOrDefault(def.Code) + 1;
        return await RunEnteredAsync(def, settings, nextDueUtc, ct);
    }

    /// <summary>立即檢查：這個檢查正在跑（背景或另一個立即檢查）就不跑、回 null（呼叫端回 <c>healthCheckBusy</c>）。</summary>
    public async Task<HealthObservation?> TryRunNowAsync(HealthCheckDefinition def, EffectiveHealthSettings settings, CancellationToken ct)
    {
        lock (_sync)
        {
            if (_running.GetValueOrDefault(def.Code) > 0) return null;
            _running[def.Code] = 1;
        }
        return await RunEnteredAsync(def, settings, null, ct);
    }

    private async Task<HealthObservation> RunEnteredAsync(HealthCheckDefinition def, EffectiveHealthSettings settings, DateTime? nextDueUtc, CancellationToken ct)
    {
        try
        {
            var (observation, previous) = await ProbeAsync(def, settings, ct);
            Enqueue(observation, nextDueUtc, previous);
            await FlushAsync(ct);
            return observation;
        }
        finally
        {
            lock (_sync)
                if (--_running[def.Code] == 0) _running.Remove(def.Code);
        }
    }

    private async Task<(HealthObservation Observation, HealthStatus? Previous)> ProbeAsync(HealthCheckDefinition def, EffectiveHealthSettings settings, CancellationToken ct)
    {
        var started = time.GetTimestamp();
        // 截到資料庫的精度：狀態板與寫入後讀回的投影要能比出「同一筆」（OpsMonitorReader 依此判斷記憶體是否比較新）
        var now = Common.StorageTime.ToMicroseconds(time.GetUtcNow().UtcDateTime);
        ProbeResult result;
        try
        {
            if (!_probes.TryGetValue(def.Code, out var probe)) throw new InvalidOperationException($"沒有 {def.Code} 的檢查實作");
            using var timeout = new CancellationTokenSource(HealthCheckCatalog.RunTimeout, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            await using var scope = scopes.CreateAsyncScope();
            var context = new HealthProbeContext(scope.ServiceProvider, def, settings, now, _board.GetValueOrDefault(def.Code), _counters);
            result = await probe.RunAsync(context, linked.Token).WaitAsync(linked.Token);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // 檢查本身丟例外或逾時：記 error，事件以警告處理（§17.2.4）
            logger.LogWarning(e, "健康檢查 {Code} 本身失敗", def.Code);
            result = new ProbeResult(HealthStatus.Error, null, "checkFailed");
        }
        var duration = (int)Math.Min(int.MaxValue, time.GetElapsedTime(started).TotalMilliseconds);
        var observation = new HealthObservation(def.Code, Guid.NewGuid().ToString("N"), now, result.Status, result.Value, result.MessageCode,
            duration, instance.InstanceId);
        var previous = _board.TryGetValue(def.Code, out var before) ? before.Status : (HealthStatus?)null;
        // 重疊的兩次檢查（看門狗與立即檢查）較早開始、較晚完成的不蓋掉較新的結果
        _board.AddOrUpdate(def.Code, observation, (_, current) => current.CheckedUtc > observation.CheckedUtc ? current : observation);
        return (observation, previous);
    }

    private void Enqueue(HealthObservation o, DateTime? nextDueUtc, HealthStatus? previous)
    {
        lock (_sync)
        {
            var last = _pending.LastOrDefault(p => p.Observation.Code == o.Code);
            if (last is { Attempted: false } && last.Observation.Status == o.Status)
            {
                // 狀態沒變：只更新最後一筆的數值與時間（識別碼沿用，它還沒送出過）
                last.Observation = o with { TransitionId = last.Observation.TransitionId };
                last.NextDueUtc = nextDueUtc ?? last.NextDueUtc;
                return;
            }
            _pending.Add(new PendingHealthWrite(o, nextDueUtc, previous));
            if (_pending.Count > MaxPending)
            {
                _pending.RemoveAt(0);
                Interlocked.Increment(ref _dropped);
                OpsMetrics.PendingDropped.Add(1);
            }
        }
    }

    /// <summary>寫不進資料庫的結果依序交給直送佇列判斷（每筆只判斷一次）。</summary>
    private void ConsiderDirect()
    {
        List<PendingHealthWrite> fresh;
        lock (_sync)
        {
            fresh = [.. _pending.Where(p => !p.DirectConsidered)];
            foreach (var p in fresh) p.DirectConsidered = true;
        }
        foreach (var p in fresh) direct.Consider(p.Observation, p.Previous);
    }

    /// <summary>依順序逐筆補寫；回傳這次寫入的筆數。</summary>
    public async Task<int> FlushAsync(CancellationToken ct)
    {
        await _flush.WaitAsync(ct);
        try
        {
            var written = 0;
            while (true)
            {
                PendingHealthWrite? next;
                bool superseded;
                lock (_sync)
                {
                    next = _pending.FirstOrDefault();
                    if (next is not null) next.Attempted = true;
                    // 同一檢查在佇列後面已經回到正常：這筆的開啟、升級信已經過時（§17.2.6）
                    superseded = next is not null && _pending.Skip(1).Any(p => p.Observation.Code == next.Observation.Code && p.Observation.Status == HealthStatus.Ok);
                }
                if (next is null) return written;
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var result = await scope.ServiceProvider.GetRequiredService<ISender>()
                        .Send(new RecordHealthResultRequest(next.Observation, next.NextDueUtc, superseded), ct);
                    if (!result.IsSuccess) throw new InvalidOperationException(result.Message ?? result.Code);
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning(e, "健康檢查結果寫不進資料庫，留在待補寫佇列（{Count} 筆）：{Code} {TransitionId}",
                        PendingCount, next.Observation.Code, next.Observation.TransitionId);
                    ConsiderDirect();
                    return written;
                }
                lock (_sync) _pending.Remove(next);
                written++;
            }
        }
        finally
        {
            _flush.Release();
        }
    }
}
