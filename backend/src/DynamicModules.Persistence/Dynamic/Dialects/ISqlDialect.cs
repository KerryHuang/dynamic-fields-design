namespace DynamicModules.Persistence.Dynamic.Dialects;

/// <summary>資料庫方言差異：SQLite 與 PostgreSQL 各一份（PostgreSQL provider 設計 §5.5）。識別字由呼叫端加雙引號，兩邊共用。</summary>
public interface ISqlDialect
{
    /// <summary>回傳加上排序與分頁的 SQL；分頁參數固定叫 @offset、@limit。</summary>
    string Paginate(string selectSql, string orderBy);

    /// <summary>跳脫 LIKE 萬用字元，須搭配 ESCAPE '\'。</summary>
    string EscapeLike(string value);

    /// <summary>布林字面值（SQLite 存成 0／1，PostgreSQL 是 boolean）。</summary>
    string Bool(bool value);

    /// <summary>
    /// 不分大小寫的 LIKE（只對 ASCII 字母），含 ESCAPE '\'；param 是已加上 @ 的參數名，值要先經 <see cref="EscapeLike"/>。
    /// </summary>
    string LikeIgnoreCase(string column, string param);

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

    /// <summary>
    /// 指定索引的提示，接在資料表別名之前（報表 SQL 路徑用）：SQLite 回 <c> INDEXED BY {index}</c>，索引不存在時 prepare 就丟錯；
    /// PostgreSQL 沒有查詢層級的索引提示，回空字串、交給規劃器（PostgreSQL provider 設計 §5.9）。
    /// </summary>
    string IndexHint(string index);

    /// <summary>
    /// 依書寫順序連接的 JOIN 關鍵字，後面接 ON：SQLite 回 <c>CROSS JOIN</c>（規劃器不重排，左邊固定是驅動表）；
    /// PostgreSQL 的 CROSS JOIN 不能帶 ON，回 <c>JOIN</c>，連接順序交給規劃器。
    /// </summary>
    string OrderedJoin();
}
