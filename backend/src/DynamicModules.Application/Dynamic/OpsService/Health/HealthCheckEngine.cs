using System.Collections.Concurrent;
using System.Diagnostics;
using DynamicModules.Application.Common.Observability;
using DynamicModules.Domain.Entities.Dynamic;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.OpsService.Health;

/// <summary>一次檢查的結果（§17.2.4 第 1 步）：<see cref="TransitionId"/> 之後重試、補寫、直送都用同一個。</summary>
public sealed record HealthObservation(
    string Code, string TransitionId, DateTime CheckedUtc, HealthStatus Status, long? Value, string? MessageCode, int DurationMs, string InstanceId);

/// <summary>待補寫佇列的一筆（只在記憶體）。</summary>
internal sealed class PendingHealthWrite(HealthObservation observation, DateTime? nextDueUtc)
{
    public HealthObservation Observation { get; set; } = observation;
    public DateTime? NextDueUtc { get; set; } = nextDueUtc;
    /// <summary>嘗試寫過一次的不再合併（它可能其實已提交，§17.2.4 第 3 步）。</summary>
    public bool Attempted { get; set; }
}

/// <summary>
/// 本實例的健康檢查執行器（維運設計 §17.2.4，singleton；看門狗與 <c>ops.monitor</c> 共用）：
/// 跑一個檢查（最長 60 秒）→ 更新記憶體的狀態板 → 放進待補寫佇列 → 依檢查時間先後逐筆走彙總交易。
/// 寫不進去或提交結果不明就停在那一筆，下一輪從那一筆接著補（已提交的會被去重略過）。
/// 佇列同一檢查狀態沒變的連續結果只更新最後一筆，狀態有變才新增；上限 500 筆，滿了丟最舊的。
/// </summary>
public sealed class HealthCheckEngine(
    IServiceScopeFactory scopes, IEnumerable<IHealthProbe> probes, TimeProvider time, OpsInstance instance, ILogger<HealthCheckEngine> logger)
{
    /// <summary>待補寫佇列上限（技術限制，§17.9）。</summary>
    public const int MaxPending = 500;

    private readonly Dictionary<string, IHealthProbe> _probes = probes.ToDictionary(p => p.Code, StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HealthObservation> _board = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _counters = new(StringComparer.Ordinal);
    private readonly List<PendingHealthWrite> _pending = [];
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
        var observation = await ProbeAsync(def, settings, ct);
        Enqueue(observation, nextDueUtc);
        await FlushAsync(ct);
        return observation;
    }

    private async Task<HealthObservation> ProbeAsync(HealthCheckDefinition def, EffectiveHealthSettings settings, CancellationToken ct)
    {
        var started = time.GetTimestamp();
        var now = time.GetUtcNow().UtcDateTime;
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
        _board[def.Code] = observation;
        return observation;
    }

    private void Enqueue(HealthObservation o, DateTime? nextDueUtc)
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
            _pending.Add(new PendingHealthWrite(o, nextDueUtc));
            if (_pending.Count > MaxPending)
            {
                _pending.RemoveAt(0);
                Interlocked.Increment(ref _dropped);
                OpsMetrics.PendingDropped.Add(1);
            }
        }
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
                lock (_sync)
                {
                    next = _pending.FirstOrDefault();
                    if (next is not null) next.Attempted = true;
                }
                if (next is null) return written;
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var result = await scope.ServiceProvider.GetRequiredService<ISender>()
                        .Send(new RecordHealthResultRequest(next.Observation, next.NextDueUtc), ct);
                    if (!result.IsSuccess) throw new InvalidOperationException(result.Message ?? result.Code);
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning(e, "健康檢查結果寫不進資料庫，留在待補寫佇列（{Count} 筆）：{Code} {TransitionId}",
                        PendingCount, next.Observation.Code, next.Observation.TransitionId);
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
