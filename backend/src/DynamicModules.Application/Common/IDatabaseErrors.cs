namespace DynamicModules.Application.Common;

/// <summary>
/// 資料庫錯誤分類集中在這裡（PostgreSQL provider 設計 §5.7）：依錯誤碼判斷，不比對訊息字串
/// （PostgreSQL 的訊息會依伺服器語系翻譯）。會沿著 InnerException 往內找，可以直接丟 EF 的 DbUpdateException 進來。
/// </summary>
public interface IDatabaseErrors
{
    /// <summary>唯一鍵或主鍵衝突。</summary>
    bool IsUniqueViolation(Exception exception);

    /// <summary>資料庫忙碌、鎖逾時、死結或序列化衝突（API 回 503 加 Retry-After）；已轉成 <see cref="DatabaseBusyException"/> 的也算。</summary>
    bool IsBusyOrConflict(Exception exception);

    /// <summary>數值溢位（例如加總超出 64 位元整數）。</summary>
    bool IsNumericOverflow(Exception exception);

    /// <summary>查詢被中斷（逾時或取消）。</summary>
    bool IsQueryCanceled(Exception exception);

    /// <summary>資料庫唯讀（維運設計 §17.2.9：API 回 503 <c>databaseUnwritable</c>）。</summary>
    bool IsReadOnly(Exception exception);

    /// <summary>資料庫空間滿（同上，<c>databaseUnwritable</c>）。</summary>
    bool IsStorageFull(Exception exception);

    /// <summary>
    /// 連不上資料庫（API 回 503 <c>databaseUnavailable</c>）。取不到連線池的連線已歸 <see cref="IsBusyOrConflict"/>，這裡不重複算。
    /// </summary>
    bool IsUnavailable(Exception exception);
}
