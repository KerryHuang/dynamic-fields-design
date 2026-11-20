namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>
/// 語系包預覽暫存（表 <c>Dynamic_LocalePackPreview</c>，語系包設計 §6.3、§22；i4d1 計畫判斷 8）：存解析後、只含要寫入的句子的計畫，不存原始檔；
/// 存資料庫是為了多台部署時套用打到另一台也找得到。綁定建立者，保留 10 分鐘，全系統最多 5 筆。
/// </summary>
public class LocalePackPreview
{
    /// <summary><c>Guid.NewGuid().ToString("N")</c>。</summary>
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Code { get; set; } = "";
    public string FileHash { get; set; } = "";
    public string FileName { get; set; } = "";
    public string PlanJson { get; set; } = "{}";
    public string ReportJson { get; set; } = "{}";
    /// <summary>預覽當下該語系列的內容指紋；語系沒安裝是 null。</summary>
    public string? LocaleFingerprint { get; set; }
    /// <summary>預覽當下目前版的 <see cref="LocalePack.Id"/>；沒有任何版本是 null。</summary>
    public long? CurrentPackId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
}
