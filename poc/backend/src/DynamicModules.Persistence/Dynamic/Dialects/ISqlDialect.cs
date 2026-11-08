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
}
