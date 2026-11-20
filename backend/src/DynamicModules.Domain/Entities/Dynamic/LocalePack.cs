namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>
/// 語系的覆寫層版本（表 <c>Dynamic_LocalePack</c>，語系包設計 §6.4、§22；i4d1 計畫判斷 7）：每語系最多兩列，目前版（<see cref="IsCurrent"/>）與上一版。
/// 每一次覆寫層寫入（匯入、回到上一版、移除覆寫）都新增一列，<see cref="Id"/> 自增不重用，所以目前版的 <see cref="Id"/> 就是持久化的版本 token。
/// <see cref="UiJson"/>／<see cref="ServerJson"/> 是整份覆寫層（扁平 JSON 物件，鍵 Ordinal 排序）；<see cref="BaseHashesJson"/> 是每個覆寫鍵匯入當時
/// 內建 zh-TW 原文的雜湊（SHA-256 前 16 個十六進位字元）。
/// </summary>
public class LocalePack
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public bool IsCurrent { get; set; }
    public string UiJson { get; set; } = "{}";
    public string ServerJson { get; set; } = "{}";
    public string BaseHashesJson { get; set; } = "{}";
    /// <summary>語系包檔案的 SHA-256（十六進位全長）；移除覆寫產生的空版本是 null。</summary>
    public string? FileHash { get; set; }
    public string? FileName { get; set; }
    public string? PackVersion { get; set; }
    public string? BuiltFor { get; set; }
    public string? BaseLocale { get; set; }
    public string? Note { get; set; }
    /// <summary>套用當下的報告摘要（各段筆數與完整度，不含拒收清單）。</summary>
    public string? ReportJson { get; set; }
    public string? ImportedUserId { get; set; }
    public DateTime ImportedUtc { get; set; }
    public DateTime ModifiedUtc { get; set; }
}
