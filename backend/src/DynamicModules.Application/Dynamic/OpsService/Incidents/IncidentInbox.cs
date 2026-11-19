using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Observability;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.OpsService.Incidents;

/// <summary>
/// 請求路徑來源（伺服器 500、前端錯誤）的收件匣（維運設計 §17.2.3、O2-T5）：<see cref="Post"/> 只放進記憶體，
/// 同指紋合併成「次數＋最近一筆樣本＋最近版本」，不擋呼叫端，不在請求裡寫資料庫。
/// </summary>
public interface IIncidentInbox
{
    void Post(IncidentSignal signal);
}

public sealed class IncidentInbox : IIncidentInbox
{
    /// <summary>最多幾個指紋（技術限制，§17.9），滿了丟最舊的。</summary>
    public const int MaxFingerprints = 1000;

    private readonly object _sync = new();
    private readonly Dictionary<string, IncidentSignal> _byFingerprint = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _order = new();

    public int Count
    {
        get { lock (_sync) return _byFingerprint.Count; }
    }

    public void Post(IncidentSignal signal)
    {
        lock (_sync)
        {
            if (_byFingerprint.TryGetValue(signal.Fingerprint, out var existing))
            {
                // 次數累加；樣本、版本、時間取較新的那一筆
                var newer = signal.OccurredUtc >= existing.OccurredUtc ? signal : existing;
                _byFingerprint[signal.Fingerprint] = newer with { Count = existing.Count + signal.Count, Sample = newer.Sample ?? existing.Sample };
                return;
            }
            if (_byFingerprint.Count >= MaxFingerprints)
            {
                var oldest = _order.First!.Value;
                _order.RemoveFirst();
                _byFingerprint.Remove(oldest);
                OpsMetrics.IncidentsDropped.Add(1);
            }
            _byFingerprint[signal.Fingerprint] = signal;
            _order.AddLast(signal.Fingerprint);
        }
    }

    /// <summary>取走目前累積的全部訊號（依進來的順序）。</summary>
    public List<IncidentSignal> Drain()
    {
        lock (_sync)
        {
            var all = _order.Select(f => _byFingerprint[f]).ToList();
            _order.Clear();
            _byFingerprint.Clear();
            return all;
        }
    }
}

/// <summary>
/// 把收件匣累積的訊號交給 <see cref="IncidentRecorder"/>：每個指紋一個交易型請求（經寫入閘門）。寫不進去（忙碌、唯讀、斷線）
/// 或提交結果不明時放回收件匣下一輪再試（次數可能多算一次，§17.2.3）。WebAPI 的 IncidentInboxFlusher 每 5 秒呼叫一次。
/// </summary>
public sealed class IncidentInboxDrain(IncidentInbox inbox, IServiceScopeFactory scopes, ILogger<IncidentInboxDrain> logger)
{
    private readonly SemaphoreSlim _single = new(1, 1);

    public async Task<int> FlushAsync(CancellationToken ct)
    {
        await _single.WaitAsync(ct);
        try
        {
            var written = 0;
            var batch = inbox.Drain();
            for (var i = 0; i < batch.Count; i++)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new RecordIncidentSignalRequest(batch[i]), ct);
                    if (!result.IsSuccess) throw new InvalidOperationException(result.Message ?? result.Code ?? "寫入失敗");
                    written++;
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "維運事件寫不進資料庫，留在收件匣下一輪再試：{Fingerprint}", batch[i].Fingerprint);
                    // 這一筆與後面還沒送的都放回去
                    foreach (var rest in batch.Skip(i)) inbox.Post(rest);
                    if (e is OperationCanceledException && ct.IsCancellationRequested) throw;
                    break;
                }
            }
            return written;
        }
        finally
        {
            _single.Release();
        }
    }
}

/// <summary>寫一個事件訊號（交易內呼叫 <see cref="IncidentRecorder"/>）。</summary>
public sealed record RecordIncidentSignalRequest(IncidentSignal Signal) : IRequest<ServiceResult>, ITransactionalRequest;

public sealed record IncidentRecordResult(IncidentTransition Transition, long? IncidentId);

public sealed class RecordIncidentSignalHandler(IncidentRecorder recorder) : IRequestHandler<RecordIncidentSignalRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(RecordIncidentSignalRequest request, CancellationToken ct)
    {
        var (transition, id) = await recorder.RecordAsync(request.Signal, ct);
        return ServiceResult.Success(new IncidentRecordResult(transition, id));
    }
}
