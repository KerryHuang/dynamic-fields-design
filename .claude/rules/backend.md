---
paths:
  - "backend/**"
---

# 後端（.NET 8）

- **分層**：`Domain` 只放純函式與值物件，不碰 IO；功能邏輯放 `Application/Dynamic/<功能>Service/` 的 MediatR handler，回傳 `ServiceResult`；`WebAPI` 只轉接，不放業務判斷。
- **寫入**：會寫資料的 request 實作 `ITransactionalRequest`，由 `TransactionBehavior` 包交易；不在 handler 自己開交易。
- **權限 fail closed**：走既有 guard（`DynamicAccessGuard` 等）；讀不到權限輸入時當成沒權限，不放行。
- **migration**：只用 EF 產生，不手改；一個工作段最多一組（SQLite 與 PostgreSQL 各一個、同名，用 `scripts/db/add-migration.sh <名稱>` 一次產兩份，`MigrationConsistencyTests` 把關）。會加 migration 的兩項工作不並行（兩邊的 model snapshot 都會衝突）。
- **PostgreSQL 相容**：正式版資料庫是 PostgreSQL，SQLite 只用於研發與 Demo。新程式不加 SQLite 專屬寫法：raw SQL 走 `ISqlDialect`（識別字加雙引號、布林用 `Bool`、索引提示用 `IndexHint`、日期分桶用 `DateBucket`）、識別字不靠大小寫折疊、不直接寫 `INDEXED BY`／`PRAGMA`／`strftime`、不 catch `SqliteException` 做業務判斷、部分索引 filter 不寫 `= 0／= 1` 當布林；DateTime 一律 `Kind` 明確，存 UTC 的屬性名稱以 `Utc` 結尾（PostgreSQL 是 `timestamptz`，其餘是牆上時間 `timestamp`）；Dapper 的 `DateTime` 參數指定 `DbType.DateTime2`。交易內接住非 `SaveChanges` 的資料庫錯誤（Dapper、raw SQL、`ExecuteUpdate`）後只能回失敗結果，不能再發 SQL（PostgreSQL 會把整個交易標成失效）；要繼續就自己包 savepoint。LINQ 依可空欄位排序要明寫 NULL 的位置（SQLite 遞增時 NULL 在前、PostgreSQL 在後，例如先 `OrderBy(x => x.Col == null ? 0 : 1)`）；不依賴沒有 `ORDER BY` 時的回傳順序（測試也一樣）；列舉查詢結果時不在迴圈內再發查詢（Npgsql 同一條連線不能同時開兩個讀取器，"A command is already in progress"），要先 `ToListAsync`。交易外的 `ExecuteUpdate`／`ExecuteDelete` 不經 PostgreSQL 的全庫寫入鎖，新增時在交付說明列出。非寫不可時隔離在 provider 專屬類別並在交付說明列出。新增或修改 repository、並行、raw SQL、migration 相關的測試要加進 PostgreSQL 雙跑集（類別標 `Category=Postgres`，跑法見 `docs/ai/testing.md`）。
- **業務政策不寫死**（AGENTS.md 鐵則 2）：後端的寫法是把上限、預設值、天數、通知對象放進可設定的資料，handler 讀設定；不寫成 `const`／`static readonly`。可寫死的寫了要在交付說明列出。
- **使用者看得到的訊息**：集中在同功能的 `*Messages.cs`（例：`TaskService/TaskMessages.cs`），不散寫在 handler。WebAPI 依 `X-Locale` 回傳語系，四語字串表在 `Application/Common/MessageText.cs`；字面值是表內某句的繁中原文才會被翻譯，其餘照中文回傳（還沒搬完）。
- **改種子或共用 fixture 前**，先 grep 既有測試對那筆資料的斷言（例：新增 Demo 使用者會改變群組成員與提及候選的筆數）。
- **測試**：xUnit，`tests/DynamicModules.{Domain,Application,WebAPI}Tests`；開發中只跑 `--filter` 窄範圍，WebAPI 全套留給 gate。測試資料全部虛構。
- **上限與政策的通則**（使用者 2026-11-20 裁決，從個案提升為規範）：
  - 政策調嚴不溯及既有資料：上限只在新增、或重新存檔時檢查；既有超標的資料照常能讀、能執行，不會突然存不了。
  - 超過上限的錯誤訊息要帶出上限數字（例「每個流程最多 {max} 條轉換」），不和其他錯誤共用一句籠統的訊息。
  - 授予或收回高權限（系統管理員、系統權限）的稽核標 HighRisk，並依實際持有者變化發安全事件（含經群組成員異動間接取得）；一般模組權限存檔只記稽核、不發通知。
- **語系退回**（使用者 2026-11-20 裁決）：請求沒指定語系時用系統「預設語系」政策，不寫死 `"zh-TW"`；退回鏈是「要求的語系 → 系統預設語系 → 繁中 → 第一個有值的」。
