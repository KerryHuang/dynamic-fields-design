namespace DynamicModules.Application.Dynamic.McpService.Tools;

public enum McpRateDenial { Minute, Day }

/// <summary>名額判斷結果：放行時帶 <see cref="Reservation"/>，被擋時帶 <see cref="Denial"/>（每分鐘窗被擋另給到下一窗的秒數）。</summary>
public sealed record McpRateDecision(McpReservation? Reservation, McpRateDenial? Denial = null, int RetryAfterSeconds = 0)
{
    public bool Allowed => Reservation is not null;
}

/// <summary>每日名額的保留憑證：invoker 在稽核寫完之後才釋放；釋放是冪等的，第二次呼叫不做事。</summary>
public sealed class McpReservation
{
    private readonly Action _release;
    private int _released;

    internal McpReservation(Action release) => _release = release;

    public void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0) _release();
    }
}

/// <summary>
/// 每把權杖的速率與每日名額（mcp1a 計畫判斷 13；寫死清單：只在單一實例記憶體）。上限每次呼叫由呼叫端從政策讀來傳入。
/// <list type="bullet">
/// <item>每分鐘：對齊分鐘的固定窗，放行才計一次。</item>
/// <item>每日：已提交數（稽核表，由呼叫端的 <c>countCommitted</c> 查）＋進行中的保留數 &lt; 上限才放行，並保留一個名額直到憑證釋放。</item>
/// </list>
/// 同步契約（第三輪必修 2）：字典的取得與移除、<c>Users</c> 與 <c>Reserved</c> 的增減全在同一個 <c>lock (_sync)</c>（短鎖、不 await）；
/// 每把權杖一個 <c>SemaphoreSlim(1)</c> 把「判斷每分鐘窗、讀已提交數、決定放不放行」排成一個一個來，工具在 semaphore 外執行。
/// 讀取順序（第四輪必修 1）：先在鎖內擷取 <c>Reserved</c>，之後才查已提交數；反過來的順序在「查已提交數 → 別人提交並釋放 → 讀 Reserved」的交錯下會漏算，
/// 先擷取最多把同一次呼叫算兩次，只會偏嚴。重啟後保留歸零，但已提交的稽核列還在，每日上限照樣生效。
/// </summary>
public sealed class McpRateGate(TimeProvider time)
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private readonly object _sync = new();
    private readonly Dictionary<long, TokenGate> _gates = new();
    private DateTimeOffset _lastSweep = DateTimeOffset.MinValue;

    /// <summary>字典裡的權杖數（測試觀察清理用）。</summary>
    public int TrackedTokenCount
    {
        get { lock (_sync) return _gates.Count; }
    }

    private sealed class TokenGate
    {
        public readonly SemaphoreSlim Turn = new(1, 1);
        public DateTimeOffset WindowStart = DateTimeOffset.MinValue;
        public int WindowCount;
        public int Reserved;
        public int Users;
    }

    public async Task<McpRateDecision> TryReserveAsync(long tokenId, int perMinute, int perDay,
        Func<CancellationToken, Task<int>> countCommitted, CancellationToken ct)
    {
        TokenGate gate;
        lock (_sync)
        {
            Sweep(time.GetUtcNow());
            if (!_gates.TryGetValue(tokenId, out gate!)) _gates[tokenId] = gate = new TokenGate();
            gate.Users++;
        }

        try
        {
            await gate.Turn.WaitAsync(ct);
        }
        catch
        {
            lock (_sync) gate.Users--;
            throw;
        }

        var allowed = false;
        try
        {
            var now = time.GetUtcNow();
            var window = new DateTimeOffset(now.UtcTicks - now.UtcTicks % Minute.Ticks, TimeSpan.Zero);
            if (gate.WindowStart != window)
            {
                gate.WindowStart = window;
                gate.WindowCount = 0;
            }
            if (gate.WindowCount >= perMinute)
                return new McpRateDecision(null, McpRateDenial.Minute, Math.Max(1, (int)Math.Ceiling((window + Minute - now).TotalSeconds)));

            int reservedSnapshot;
            lock (_sync) reservedSnapshot = gate.Reserved;
            var committed = await countCommitted(ct);
            if (committed + reservedSnapshot >= perDay) return new McpRateDecision(null, McpRateDenial.Day);

            gate.WindowCount++;
            lock (_sync) gate.Reserved++;
            allowed = true;
            return new McpRateDecision(new McpReservation(() =>
            {
                lock (_sync)
                {
                    gate.Reserved--;
                    gate.Users--;
                }
            }));
        }
        finally
        {
            gate.Turn.Release();
            if (!allowed) lock (_sync) gate.Users--;
        }
    }

    /// <summary>最多每分鐘掃一次；只移除沒有人拿著（含等待者、持有憑證的）、沒有保留、每分鐘窗也已經過去的項目。</summary>
    private void Sweep(DateTimeOffset now)
    {
        if (now - _lastSweep < Minute) return;
        _lastSweep = now;
        foreach (var (id, gate) in _gates.ToList())
            if (gate.Users == 0 && gate.Reserved == 0 && gate.WindowStart + Minute <= now)
                _gates.Remove(id);
    }
}
