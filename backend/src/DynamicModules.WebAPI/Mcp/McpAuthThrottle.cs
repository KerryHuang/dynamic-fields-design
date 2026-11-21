namespace DynamicModules.WebAPI.Mcp;

/// <summary>
/// <c>/mcp</c> 認證失敗的 IP 降速（mcp1a 計畫判斷 11；寫死清單：安全，數字對齊 <c>LoginThrottle.IpMax</c>）：15 分鐘內失敗 20 次就擋，
/// 直到最舊的失敗滑出窗口。照 <c>LoginThrottle.TryBegin</c>：檢查與「進行中名額」保留是同一個原子步驟，已失敗數＋進行中數達上限就不放行，
/// 同一 IP 並行送一批壞權杖不會一起穿過。只在單一實例記憶體裡。
/// </summary>
public sealed class McpAuthThrottle(TimeProvider time)
{
    public const int IpMax = 20;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly object _gate = new();
    private readonly Dictionary<string, List<DateTimeOffset>> _failures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _inFlight = new(StringComparer.Ordinal);
    private DateTimeOffset _lastPrune;

    /// <summary>可以嘗試時保留一個進行中名額並回 true，呼叫端之後必須呼叫 <see cref="Complete"/> 或 <see cref="Release"/>；不放行時給建議的等待秒數。</summary>
    public bool TryBegin(string ip, out int retryAfterSeconds)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            Prune(now);
            var recent = _failures.TryGetValue(ip, out var list) ? list.Where(t => t > now - Window).ToList() : [];
            if (recent.Count + _inFlight.GetValueOrDefault(ip) >= IpMax)
            {
                // 最舊的失敗滑出窗口的時間；額度全被進行中的嘗試佔住時，請一秒後再試
                retryAfterSeconds = recent.Count >= IpMax
                    ? Math.Max(1, (int)Math.Ceiling((recent[recent.Count - IpMax] + Window - now).TotalSeconds))
                    : 1;
                return false;
            }
            _inFlight[ip] = _inFlight.GetValueOrDefault(ip) + 1;
            retryAfterSeconds = 0;
            return true;
        }
    }

    /// <summary>結算保留的名額：失敗記一次；成功不清掉既有失敗（不讓攻擊者拿一把有效權杖洗掉 IP 額度）。</summary>
    public void Complete(string ip, bool success)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            ReleaseLocked(ip);
            if (success) return;
            if (!_failures.TryGetValue(ip, out var list)) _failures[ip] = list = [];
            list.Add(now);
        }
    }

    /// <summary>嘗試沒有結果（例外）時歸還名額，不算失敗。</summary>
    public void Release(string ip)
    {
        lock (_gate) ReleaseLocked(ip);
    }

    private void ReleaseLocked(string ip)
    {
        if (!_inFlight.TryGetValue(ip, out var n)) return;
        if (n <= 1) _inFlight.Remove(ip);
        else _inFlight[ip] = n - 1;
    }

    private void Prune(DateTimeOffset now)
    {
        if (now - _lastPrune < TimeSpan.FromMinutes(1)) return;
        _lastPrune = now;
        foreach (var key in _failures.Keys.ToList())
        {
            _failures[key].RemoveAll(t => t <= now - Window);
            if (_failures[key].Count == 0) _failures.Remove(key);
        }
    }
}
