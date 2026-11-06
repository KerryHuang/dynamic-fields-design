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

    private readonly Dictionary<string, int> _inFlight = new();

    /// <summary>
    /// 可以嘗試時回傳 true，並保留一個進行中的名額（計入帳號＋IP、IP、帳號三層），呼叫端必須接著呼叫 <see cref="Complete"/> 或 <see cref="Release"/>。
    /// 保留讓檢查與計數是同一個原子步驟，同一 IP 並行送出的請求不會一起超過額度。
    /// </summary>
    public bool TryBegin(string userId, string ip)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            Prune(now);
            if (Used("p|" + userId + "|" + ip, now - ShortWindow) >= PairMax) return false;
            if (Used("i|" + ip, now - ShortWindow) >= IpMax) return false;
            if (Used("a|" + userId, now - AccountWindow) >= AccountMax)
            {
                if (_lastAttempt.TryGetValue(userId, out var last) && now - last < SlowInterval) return false;
                _lastAttempt[userId] = now;
            }
            foreach (var key in Keys(userId, ip)) _inFlight[key] = _inFlight.GetValueOrDefault(key) + 1;
            return true;
        }
    }

    /// <summary>結算 <see cref="TryBegin"/> 保留的名額：失敗記入三層計數；成功只清掉這組帳號＋IP（不讓攻擊者拿自己的帳號洗掉 IP 額度）。</summary>
    public void Complete(string userId, string ip, bool success)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            ReleaseLocked(userId, ip);
            if (success) { _failures.Remove("p|" + userId + "|" + ip); return; }
            foreach (var key in Keys(userId, ip))
            {
                if (!_failures.TryGetValue(key, out var list)) _failures[key] = list = [];
                list.Add(now);
            }
        }
    }

    /// <summary>嘗試沒有結果（例外）時歸還名額，不算失敗。</summary>
    public void Release(string userId, string ip)
    {
        lock (_gate) ReleaseLocked(userId, ip);
    }

    private static string[] Keys(string userId, string ip) => ["p|" + userId + "|" + ip, "i|" + ip, "a|" + userId];

    private void ReleaseLocked(string userId, string ip)
    {
        foreach (var key in Keys(userId, ip))
            if (_inFlight.TryGetValue(key, out var n))
            {
                if (n <= 1) _inFlight.Remove(key);
                else _inFlight[key] = n - 1;
            }
    }

    private int Used(string key, DateTimeOffset since) => Count(key, since) + _inFlight.GetValueOrDefault(key);

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
