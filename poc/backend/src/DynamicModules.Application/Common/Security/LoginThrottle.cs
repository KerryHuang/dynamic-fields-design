namespace DynamicModules.Application.Common.Security;

/// <summary>
/// 登入失敗限流（記憶體內，假設單一站台實例）。取代「同帳號輸錯 5 次就鎖帳號」：外人猜錯密碼只會擋到自己的來源 IP，不會讓合法使用者進不來。
/// 三層：帳號＋IP 15 分鐘內失敗 5 次擋該組合；單一 IP 15 分鐘內失敗 20 次擋該 IP（防一個 IP 掃很多帳號）；
/// 單一帳號 1 小時內失敗 50 次（換 IP 猜的情況）則每 10 秒只放行一次嘗試——只變慢，不鎖死。
/// </summary>
public sealed class LoginThrottle(TimeProvider time)
{
    private const int PairMax = 5, IpMax = 20, AccountMax = 50;
    private static readonly TimeSpan ShortWindow = TimeSpan.FromMinutes(15), AccountWindow = TimeSpan.FromHours(1), SlowInterval = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly Dictionary<string, List<DateTimeOffset>> _failures = new();
    private readonly Dictionary<string, DateTimeOffset> _lastAttempt = new();
    private DateTimeOffset _lastPrune;

    /// <summary>可以嘗試時回傳 true，並登記這次嘗試（供帳號層級的 10 秒間隔判斷）。</summary>
    public bool TryBegin(string userId, string ip)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            Prune(now);
            if (Count("p|" + userId + "|" + ip, now - ShortWindow) >= PairMax) return false;
            if (Count("i|" + ip, now - ShortWindow) >= IpMax) return false;
            if (Count("a|" + userId, now - AccountWindow) >= AccountMax)
            {
                if (_lastAttempt.TryGetValue(userId, out var last) && now - last < SlowInterval) return false;
                _lastAttempt[userId] = now;
            }
            return true;
        }
    }

    public void RecordFailure(string userId, string ip)
    {
        var now = time.GetUtcNow();
        lock (_gate)
            foreach (var key in new[] { "p|" + userId + "|" + ip, "i|" + ip, "a|" + userId })
            {
                if (!_failures.TryGetValue(key, out var list)) _failures[key] = list = [];
                list.Add(now);
            }
    }

    /// <summary>登入成功：只清掉這組帳號＋IP，不動 IP 與帳號層級的計數（避免攻擊者拿自己的帳號洗掉 IP 額度）。</summary>
    public void RecordSuccess(string userId, string ip)
    {
        lock (_gate) _failures.Remove("p|" + userId + "|" + ip);
    }

    private int Count(string key, DateTimeOffset since) =>
        _failures.TryGetValue(key, out var list) ? list.Count(t => t > since) : 0;

    private void Prune(DateTimeOffset now)
    {
        if (now - _lastPrune < TimeSpan.FromMinutes(1)) return;
        _lastPrune = now;
        foreach (var key in _failures.Keys.ToList())
        {
            var window = key.StartsWith("a|") ? AccountWindow : ShortWindow;
            _failures[key].RemoveAll(t => t <= now - window);
            if (_failures[key].Count == 0) _failures.Remove(key);
        }
        foreach (var key in _lastAttempt.Where(kv => now - kv.Value >= SlowInterval).Select(kv => kv.Key).ToList())
            _lastAttempt.Remove(key);
    }
}
