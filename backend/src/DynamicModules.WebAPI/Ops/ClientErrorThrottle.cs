namespace DynamicModules.WebAPI.Ops;

/// <summary>
/// 前端錯誤收件的限流（記憶體內，單一實例）：固定一分鐘視窗，每來源 IP 30 則、全站 600 則。
/// 全站額度用完就直接拒絕、不新增 IP 鍵；視窗翻頁時整批清掉，字典最多約 600 個鍵。
/// </summary>
public sealed class ClientErrorThrottle(TimeProvider time)
{
    // 以下皆為技術限制（設計 §12）
    public const int PerIpPerMinute = 30, GlobalPerMinute = 600, MaxBodyBytes = 8192;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly Dictionary<string, int> _perIp = new(StringComparer.Ordinal);
    private DateTimeOffset _windowStart;
    private int _global;

    public int TrackedKeys { get { lock (_gate) return _perIp.Count; } }

    public bool TryAcquire(string ip)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (now - _windowStart >= Window || now < _windowStart)
            {
                _windowStart = now;
                _global = 0;
                _perIp.Clear();
            }
            if (_global >= GlobalPerMinute) return false;
            var used = _perIp.GetValueOrDefault(ip);
            if (used >= PerIpPerMinute) return false;
            _perIp[ip] = used + 1;
            _global++;
            return true;
        }
    }
}
