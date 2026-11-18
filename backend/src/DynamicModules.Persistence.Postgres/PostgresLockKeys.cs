namespace DynamicModules.Persistence.Postgres;

/// <summary>
/// advisory lock 的鍵（可寫死的技術限制，PostgreSQL provider 設計 §11 第 2 項）。鎖的範圍是單一資料庫（識別含資料庫 OID），
/// 每客戶一套資料庫、每個測試資料庫之間不會互搶；兩把鎖用不同的鍵，啟動初始化裡的寫入交易不會自己等自己。
/// </summary>
public static class PostgresLockKeys
{
    /// <summary>全庫寫入鎖（交易層級，<c>pg_advisory_xact_lock</c>）：ASCII "FWWRITE"。</summary>
    public const long Write = 0x46_57_57_52_49_54_45;

    /// <summary>啟動初始化串行鎖（session 層級，<c>pg_advisory_lock</c>）：ASCII "FWSTART"。</summary>
    public const long Startup = 0x46_57_53_54_41_52_54;
}
