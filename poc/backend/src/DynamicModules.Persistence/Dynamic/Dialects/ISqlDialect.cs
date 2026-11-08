namespace DynamicModules.Persistence.Dynamic.Dialects;

/// <summary>資料庫方言差異；目前只有 SQLite，併入 SQL Server 時再加實作。</summary>
public interface ISqlDialect
{
    /// <summary>回傳加上排序與分頁的 SQL；分頁參數固定叫 @offset、@limit。</summary>
    string Paginate(string selectSql, string orderBy);

    /// <summary>跳脫 LIKE 萬用字元，須搭配 ESCAPE '\'。</summary>
    string EscapeLike(string value);

    /// <summary>區分大小寫的「包含」；與規則條件的記憶體求值（序數比較）一致。param 是已加上 @ 的參數名，不含萬用字元。</summary>
    string ContainsCaseSensitive(string column, string param);

    /// <summary>區分大小寫的「開頭是」。</summary>
    string StartsWithCaseSensitive(string column, string param);

    /// <summary>
    /// 日期欄位的分桶鍵運算式，輸出必須與 <c>ReportDates.BucketKey</c> 逐字相同：day 2026-11-05、week 2026-W45（ISO 週）、
    /// month 2026-11、quarter 2026-Q4、year 2026。bucket 為 null 時回日期部分 yyyy-MM-dd（日期時間欄位取台北牆上日期，索引本來就存牆上時間）。
    /// 欄位為 NULL 時運算式為 NULL，由呼叫端 COALESCE。
    /// </summary>
    string DateBucket(string column, string? bucket);
}
