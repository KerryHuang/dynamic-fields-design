namespace DynamicModules.Domain.Mcp;

/// <summary>
/// MCP 的技術限制（計畫「寫死清單」；設計 §15）。業務門檻（效期、數量、速率、筆數）一律在 <c>PolicyCatalog</c>，不在這裡。
/// </summary>
public static class McpLimits
{
    /// <summary>單次工具結果：文字內容加結構化內容合計的字元上限（Claude Code 另有 50,000 字元上限）。</summary>
    public const int MaxResultChars = 40_000;
    /// <summary>每把權杖同時未使用的確認碼上限；超過時最舊的作廢。</summary>
    public const int MaxOpenConfirmations = 20;
    /// <summary>權杖名稱字數上限（trim 後至少 1 字）。</summary>
    public const int TokenNameMax = 100;
    /// <summary>最後使用時間最多每幾秒寫一次。</summary>
    public const int LastUsedWriteSeconds = 60;
    /// <summary>稽核參數摘要、預演摘要的字數上限；稽核摘要的記錄 id 數上限。</summary>
    public const int AuditArgsMax = 2000, PreviewSummaryMax = 2000, AuditRecordIdsMax = 20;
    /// <summary>過期、已用、作廢的確認碼，在結束後幾天清除。</summary>
    public const int ConfirmationPurgeDays = 1;
    /// <summary><c>tools/list</c> 的快取秒數（<c>ttlMs</c>）。</summary>
    public const int ListToolsTtlSeconds = 60;
    /// <summary>「我的權杖呼叫紀錄」的筆數。</summary>
    public const int RecentCalls = 50;
}
