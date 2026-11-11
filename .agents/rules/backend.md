---
paths:
  - "poc/backend/**"
trigger: always_on
---

# 後端（.NET 8）

- **分層**：`Domain` 只放純函式與值物件，不碰 IO；功能邏輯放 `Application/Dynamic/<功能>Service/` 的 MediatR handler，回傳 `ServiceResult`；`WebAPI` 只轉接，不放業務判斷。
- **寫入**：會寫資料的 request 實作 `ITransactionalRequest`，由 `TransactionBehavior` 包交易；不在 handler 自己開交易。
- **權限 fail closed**：走既有 guard（`DynamicAccessGuard` 等）；讀不到權限輸入時當成沒權限，不放行。
- **migration**：只用 EF 產生，不手改；一個工作段最多一個。會加 migration 的兩項工作不並行（model snapshot 會衝突）。
- **PostgreSQL 相容**：正式版資料庫是 PostgreSQL，SQLite 只用於研發與 Demo。新程式不加 SQLite 專屬寫法：raw SQL 走 `ISqlDialect`、識別字不靠大小寫折疊、不用 `INDEXED BY`／`PRAGMA`／`strftime`、不 catch `SqliteException` 做業務判斷、部分索引 filter 不寫 `= 0／= 1` 當布林；DateTime 一律 `Kind` 明確。非寫不可時隔離在 provider 專屬類別並在交付說明列出。
- **業務政策不寫死**（AGENTS.md 鐵則 2）：後端的寫法是把上限、預設值、天數、通知對象放進可設定的資料，handler 讀設定；不寫成 `const`／`static readonly`。可寫死的寫了要在交付說明列出。
- **使用者看得到的訊息**：集中在同功能的 `*Messages.cs`（例：`TaskService/TaskMessages.cs`），不散寫在 handler。目前後端訊息只有中文、尚未本地化。
- **改種子或共用 fixture 前**，先 grep 既有測試對那筆資料的斷言（例：新增 Demo 使用者會改變群組成員與提及候選的筆數）。
- **測試**：xUnit，`tests/DynamicModules.{Domain,Application,WebAPI}Tests`；開發中只跑 `--filter` 窄範圍，WebAPI 全套留給 gate。測試資料全部虛構。
