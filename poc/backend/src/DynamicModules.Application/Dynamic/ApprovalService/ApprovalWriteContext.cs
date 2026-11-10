namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>
/// 一個請求內的簽核寫入情境（scoped）。Exempt：簽核動作自己寫的記錄、鍵與寫入後的值（狀態、可編輯欄位），後盾只在最終值相同時放行；
/// Touch：這次請求動過哪筆記錄的實例（送出、結束、重來），後盾把它們當成簽核中檢查（設計 §8.5「豁免以記錄為單位」）。
/// </summary>
public sealed class ApprovalWriteContext
{
    private readonly Dictionary<long, Dictionary<string, string?>> _exempt = [];
    private readonly HashSet<long> _touched = [];

    /// <summary>expected：鍵 → 寫入後的原始 JSON（null 代表清空）。同一筆記錄多次登記時後者覆蓋。</summary>
    public void Exempt(long recordId, long instanceId, IReadOnlyDictionary<string, string?> expected)
    {
        if (!_exempt.TryGetValue(recordId, out var map)) _exempt[recordId] = map = [];
        foreach (var (k, v) in expected) map[k] = v;
        _ = instanceId;   // 實例 id 目前只用來表明豁免的來源；a2c 的 content-changed 會用到
    }

    /// <summary>這個鍵登記過豁免、而且值與簽核寫入的相同（raw 為寫入後的原始 JSON）。</summary>
    public bool IsExempt(long recordId, string key, string? raw) =>
        _exempt.TryGetValue(recordId, out var map) && map.TryGetValue(key, out var expected) && expected == raw;

    public void Touch(long recordId) => _touched.Add(recordId);

    public IReadOnlySet<long> TouchedRecordIds => _touched;

    /// <summary>同一個 scope 連續送多個獨立請求時（Demo 產生器）每步開始前呼叫，同寫入日誌的 Clear。</summary>
    public void Clear()
    {
        _exempt.Clear();
        _touched.Clear();
    }
}
