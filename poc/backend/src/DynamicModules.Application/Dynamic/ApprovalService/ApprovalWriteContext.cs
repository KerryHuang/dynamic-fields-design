namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>
/// 一個請求內的簽核寫入情境（scoped）。Exempt：簽核動作自己寫的記錄與鍵（狀態、可編輯欄位），後盾放行；
/// Touch：這次請求動過哪筆記錄的實例（送出、結束、重來），後盾把它們當成簽核中檢查（設計 §8.5「豁免以記錄為單位」）。
/// </summary>
public sealed class ApprovalWriteContext
{
    private readonly Dictionary<long, HashSet<string>> _exempt = [];
    private readonly HashSet<long> _touched = [];

    public void Exempt(long recordId, long instanceId, IReadOnlySet<string> keys)
    {
        if (!_exempt.TryGetValue(recordId, out var set)) _exempt[recordId] = set = [];
        set.UnionWith(keys);
        _ = instanceId;   // 實例 id 目前只用來表明豁免的來源；a2c 的 content-changed 會用到
    }

    public bool IsExempt(long recordId, string key) => _exempt.TryGetValue(recordId, out var set) && set.Contains(key);

    public void Touch(long recordId) => _touched.Add(recordId);

    public IReadOnlySet<long> TouchedRecordIds => _touched;

    /// <summary>同一個 scope 連續送多個獨立請求時（Demo 產生器）每步開始前呼叫，同寫入日誌的 Clear。</summary>
    public void Clear()
    {
        _exempt.Clear();
        _touched.Clear();
    }
}
