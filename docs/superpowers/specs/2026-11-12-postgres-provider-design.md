# PostgreSQL provider 與測試環境設計文件（段 pg0）

> 2026-11-12｜平台共用能力（非應用程式）｜前置：[維運與觀測](2026-11-11-ops-observability-design.md)（O1 排程器與通知維護、§8、§10.7）、[MCP server](2026-11-11-mcp-server-design.md) §13.7、[事件與帳本](2026-11-08-events-ledger-design.md) §7、[儲存負載報告](2026-11-06-storage-load-report.md)（`WriteGate`）｜進度：[路線](../../roadmap.md) 的 pg0 列
> 狀態：草稿，待使用者核准。這次只寫設計，不改程式；實作分 pg0a–pg0d（§9）。
> 標「（使用者裁決）」的是使用者已拍板的；標「（主控判斷）」的是主控代為決定、可以推翻的，彙整在 §14；只有使用者能答的列在 §13。現況核對基準是 `origin/main` `cef82aa0`。

## 1. 目的與為什麼

正式版資料庫是 PostgreSQL，SQLite 只用於研發與 Demo（使用者 2026-11-11 說明）。現在整個後端只跑得了 SQLite：連線字串、EF provider、migration、raw SQL、交易與鎖、例外判斷、測試基礎都綁 SQLite。之後每個功能都要在兩種資料庫驗過（使用者裁決），所以要先有兩件東西：

1. **provider 抽象**：同一份程式依設定跑 SQLite 或 PostgreSQL，SQLite 的行為不變。
2. **可在 PostgreSQL 跑的測試環境**：開發與 gate 都能在 PostgreSQL 上跑測試，時間在預算內。

2026-11-11 的 SQLite 綁定盤點（Explore subagent，基準 `9e614a49`）結論是資料層邊界不錯：raw SQL 集中在 5 個檔、已有 `ISqlDialect`、EF 設定集中、動態值攤平在 `Dynamic_RecordIndex`、`RowVersion` 是應用層 Guid 字串。不是重寫，卡關在 migration、raw SQL 識別字、並行模型、測試基礎四塊。§4 是這份盤點依 `cef82aa0` 核對、補漏之後的版本。

## 2. 目標、不做、反目標

**目標**

- `Database:Provider` 設成 `Postgres` 時，站台能在 PostgreSQL 上啟動、套 migration、登入、新增修改刪除記錄、列表篩選排序、跨記錄檢查、自動編號、任務、簽核、通知、報表（記憶體路徑）。
- SQLite 的行為、Demo 站、現有 migration 鏈完全不變。
- 測試能用一個環境變數切到 PostgreSQL；gate 多跑一組「雙跑集」在 PostgreSQL 上，增加的時間有上限。
- O1 的排程器（兩實例搶同一工作）與通知維護（兩條路徑並行不重複）在 PostgreSQL 上通過（使用者裁決）。
- 之後的新表與查詢有一套可照做的 PostgreSQL 相容檢查方式：migration 兩份、測試加進雙跑集。

**不做（pg0 範圍外）**

- 報表 SQL 路徑的 PostgreSQL 改寫（識別字、`INDEXED BY`、日期分桶、讀取快照與逾時）：移到簽核之後的 pg1（§5.9）。pg0 在 PostgreSQL 上報表一律走記憶體路徑，結果正確、資料量大時較慢。
- 正式部署：PostgreSQL 版的 Dockerfile、部署設定檔、備份、連線池調校、金鑰與附件目錄的強制檢查。第一個客戶上線前另排。
- CI：repo 目前沒有 CI（沒有 `.github/workflows`），PostgreSQL 測試只在本機跑；CI gate 是 O5 的範圍。
- Demo 站改用 PostgreSQL（§13 U4）。
- 拿掉 PostgreSQL 的全庫寫入鎖、改成細粒度鎖（§5.6 列出到時要補的清單）。

**反目標**

- 不為了 PostgreSQL 改變 SQLite 的 SQL 或執行計畫，除非兩邊共用同一份字串時無法避免（只有識別字加引號，見 §5.5）。
- 不靠識別字大小寫折疊（`.claude/rules/backend.md`「PostgreSQL 相容」條）：不用小寫命名慣例去遷就未加引號的 SQL。
- 不比對錯誤訊息字串判斷資料庫錯誤（PostgreSQL 的訊息會依伺服器 `lc_messages` 翻譯）。

## 3. 已定決定（使用者裁決，不重問）

1. 正式版資料庫是 PostgreSQL，SQLite 只用於研發與 Demo（2026-11-11）。
2. 新程式不寫 SQLite 專屬寫法，規則已寫成 `.claude/rules/backend.md`「PostgreSQL 相容」條（2026-11-11）。
3. 段位：pg0 在 O1、un1 之後，簽核 a2d 之前；之後的功能兩種資料庫都驗；其餘 PostgreSQL 改寫排在簽核之後（2026-11-11）。
4. pg0 的驗收要含 O1 的排程器（兩實例搶同一工作）與通知維護（兩條路徑並行不重複）在 PostgreSQL 上通過（路線 pg0 列、維運設計 §10.7）。
5. 只用免費託管與服務。
6. 維運設計已定：`WriteGate` 在 PostgreSQL 停用；排程租約與去重用條件式 `UPDATE`，不用 advisory lock 或 `FOR UPDATE SKIP LOCKED`；資料庫忙碌與交易衝突的判斷集中在一個函式，pg0 補 PostgreSQL 分支（維運設計 §10.2.8、§10.7）。

## 4. 現況盤點（核對 `cef82aa0`）

| # | 項目 | 位置 | 處理段 |
|---|---|---|---|
| 1 | `UseSqlite` 與 `SqliteDialect` 寫死 | `Persistence/DependencyInjection.cs:25,37`、`DesignTimeDbContextFactory.cs:12` | pg0a／pg0b |
| 2 | 啟動時用 `SqliteConnectionStringBuilder` 解析連線字串 | `WebAPI/Program.cs:30,83,155`、`WebAPI/Attachments/AttachmentSettingsFactory.cs:20` | pg0a |
| 3 | 34 個 migration 全是 SQLite 型別與 `Sqlite:Autoincrement`（37 處） | `Persistence/Migrations/` | pg0b |
| 4 | 部分索引 filter 在 PostgreSQL 無效 | `RecordConfiguration.cs:20`、`RecordIndexConfiguration.cs:21-22` | pg0b |
| 5 | DateTime 沒有 Kind 對應策略 | 29 個 `DateTime` 屬性、1 個 `DateTimeOffset` | pg0b |
| 6 | 文字排序與比較的 collation | 全部字串欄位 | pg0b |
| 7 | raw SQL 未加引號的 PascalCase 識別字、布林寫成 `= 0` | `Dynamic/Query/DynamicQueryBuilder.cs`（列表、跨記錄聚合） | pg0a |
| 8 | `LIKE` 大小寫語意不同 | `DynamicQueryBuilder.cs` 關鍵字與 contains／startsWith | pg0a／pg0b |
| 9 | 聚合結果的 Dapper 型別對應 | `RecordRepository.cs:182-203` | pg0a |
| 10 | catch `SqliteException` 判斷整數溢位 | `RecordRepository.cs:195` | pg0a |
| 11 | 以訊息字串比對唯一鍵衝突 | `EventService/EffectApplier.cs:186`、`TaskRuleService/TaskRuleStage.cs:109` | pg0a |
| 12 | 忙碌判斷只認 SQLite 錯誤碼 | `EfTransactionManager.cs:70-77`（`DatabaseBusy`）、`UnitOfWork.cs:32` | pg0a／pg0b |
| 13 | 寫入序列化靠 `WriteGate`＋`BEGIN IMMEDIATE` | `WriteGate.cs`、`EfTransactionManager.cs` | pg0b |
| 14 | 自動編號讀後寫 | `NumberSequenceRepository.cs:10-22` | pg0b（由 #13 涵蓋） |
| 15 | `PRAGMA mmap_size` 攔截器 | `SqliteMmapInterceptor.cs` | pg0a |
| 16 | 多實例同時啟動跑 migration | `Program.cs:153-180` | pg0b |
| 17 | LINQ 依可空欄位排序時 NULL 的位置不同 | 各 repository 的 `OrderBy` | pg0d |
| 18 | 報表 SQL 路徑整套 | `ReportQueryBuilder.cs`、`ReportSqlSnapshot.cs`、`ReportQueryRepository.cs`、`SqliteDialect.DateBucket` | pg1 |
| 19 | 測試全綁 SQLite | `SqliteTestDb.cs`、`TestApp.cs`、`sqlite_master`／`PRAGMA` 測試 | pg0a／pg0c |
| 20 | 部署：Render、`Dockerfile.demo` | `render.yaml` | 不動 |

各項說明：

- **#2** 盤點漏了 `AttachmentSettingsFactory.cs:20`。PostgreSQL 的連線字串丟進 `SqliteConnectionStringBuilder` 會因為不認得的關鍵字直接丟例外，所以現在連啟動都過不了。
- **#4** 盤點列了兩個，實際是三個：`"HostKey" IS NOT NULL AND "DeleteFlag" = 0`（布林欄位和整數比較，PostgreSQL 沒有這個運算子）、`TextTruncated = 1`（未加引號會折成小寫找不到欄位，且同樣是布林比整數）、`ScaledValue IS NULL AND NumberValue IS NOT NULL`（未加引號）。其他 filter（`"Status" = 'pending'`、`"AppId" IS [NOT] NULL`、`"HostEntityKey" IS NOT NULL`）在 PostgreSQL 有效。
- **#9** 盤點沒列。PostgreSQL 的 `SUM(bigint)` 回 `numeric`、整數字面值 `0` 是 `int4`，而 `AggregateRow(long, long, long)` 靠建構子對應，型別不符就失敗（⚠️ Dapper 建構子對應的嚴格程度以實測為準）。
- **#11** 盤點沒列。`Message.Contains("UNIQUE")` 在英文的 PostgreSQL 訊息恰好也成立（"duplicate key value violates unique constraint"），但伺服器訊息語系不是英文時就失效。
- **#14** 兩個交易同時讀到 `NextValue = 5` 會各自發出 5。SQLite 上被寫入鎖序列化所以沒事。
- **#17** 盤點沒列。SQLite 遞增排序時 NULL 在前，PostgreSQL 在後；EF Core 不會幫忙統一。`DynamicQueryBuilder` 的排序已明寫 `CASE WHEN … IS NULL`，不受影響；LINQ 的 `OrderBy` 要逐處看。
- 事件與帳本設計 §7 提到的 `ICheckSerializer` 在程式裡不存在（grep 無結果），跨記錄檢查的正確性目前完全靠 SQLite 的全庫寫入鎖。§5.6 的做法讓它在 PostgreSQL 上維持同樣的前提。
- `EF.Functions.Like(r.ValuesJson, …)`（`DeletionRepository.cs:113`）在 PostgreSQL 區分大小寫；它比對的是精確大小寫的欄位代碼，結果不變，不改。
- 一般的 `catch (DbUpdateException)`（`CreateRecordHandler.cs:53`、`ReverseRecord.cs:76`、`ApprovalHandlers.cs:73`）在 PostgreSQL 同樣成立，這次不改。EF Core 在使用者交易內的 `SaveChanges` 失敗時會自動退回它建的 savepoint，所以 PostgreSQL「交易出錯後整個不能用」的限制不會讓這些路徑卡住；它們之後都回失敗結果，由 `TransactionBehavior` 回滾。

## 5. Provider 抽象

### 5.1 設定與選擇

- 新增部署設定 `Database:Provider`，值為 `Sqlite`（預設）或 `Postgres`；其他值拒絕啟動。連線字串沿用 `ConnectionStrings:Dynamic`。不從連線字串的格式猜 provider（主控判斷 T1）。
- SQLite 專屬設定 `Sqlite:MmapSizeMb` 只在 `Sqlite` 時讀；`Postgres` 時設了就記一行 Warning。
- 新增 PostgreSQL 專屬部署設定 `Database:LockTimeoutSeconds`（預設 30，對齊 SQLite 連線字串 `Default Timeout` 的預設值），用在 §5.6 的寫入鎖。這是部署設定，不是業務政策。
- `Database:Provider=Postgres` 且 `Reports:Engine=SqlOnly` 時拒絕啟動（§5.9）。
- O1 的 ops info「資料庫種類」從同一個設定讀。

### 5.2 專案結構與 DI

- **`DynamicModules.Persistence`**（既有）：repository、`UnitOfWork`、`ISqlDialect` 與兩個 dialect、錯誤分類、SQLite 專屬類別（`WriteGate`、`EfTransactionManager`、`SqliteMmapInterceptor`、`SqliteConnectionStrings`）、SQLite migration。SQLite migration 留在原處不搬（主控判斷 T2）。
- **`DynamicModules.Persistence.Postgres`**（新）：參考 `Npgsql.EntityFrameworkCore.PostgreSQL`（版本對齊 EF Core 8.0.11）、PostgreSQL 的 DbContext 選項設定、模型調整（§5.4）、寫入鎖（§5.6）、啟動鎖（§5.8）、PostgreSQL migration 與它的 design-time factory。Npgsql 的相依只出現在這個專案。
- `AddPersistence` 拆成兩層：provider 無關的 `AddPersistenceRepositories`（既有，拿掉寫死的 `SqliteDialect` 註冊）；provider 專屬的 `AddSqliteDatabase(configuration)` 與 `AddPostgresDatabase(configuration)`，各自註冊 DbContext 選項、`ISqlDialect`、`ITransactionManager`、啟動初始化。`Program.cs` 依 `Database:Provider` 二選一。
- 測試專案（ApplicationTests、WebAPITests）參考 `Persistence.Postgres`，切換方式見 §6.3。

### 5.3 Migration 策略

- **每個 provider 一個 migration assembly**（盤點建議，主控判斷 T2）：SQLite 用既有的 34 個 migration，原封不動；PostgreSQL 在 `Persistence.Postgres/Migrations/` 從當時的模型產一個 baseline `InitialCreate`。
- baseline 要在 O1（o1b 的 `Dynamic_ScheduledJob` migration）合併之後產生，才會包含 O1 的表（維運設計 §10.7）。pg0b 開工時先確認 `origin/main` 已含 o1b 的 migration。
- 兩個 migration 鏈各有自己的 model snapshot 與 `__EFMigrationsHistory`。PostgreSQL 鏈從 baseline 起算，不重演 SQLite 的歷史；既有的 SQLite 資料庫不遷移到 PostgreSQL（目前沒有 PostgreSQL 客戶，不需要資料搬移工具）。
- **之後每次改 schema 產兩個同名 migration**：新增 `scripts/db/add-migration.sh <名稱>`（Windows 附同功能的 `.cmd` 或寫明兩行指令），依序對 `Persistence`（SQLite design-time factory）與 `Persistence.Postgres`（PostgreSQL design-time factory）跑 `dotnet ef migrations add`。
- **一致性測試把關**：`MigrationConsistencyTests` 對兩個 provider 各建一個 design-time context，斷言 `Database.HasPendingModelChanges()` 為 false（EF Core 8 起提供）。它不需要 PostgreSQL 伺服器，所以跟一般 SQLite 測試一起跑，忘了產任一邊都會在 gate 失敗。
- PostgreSQL 的 design-time factory 與執行期共用同一個選項設定函式（含 §5.4 的模型調整），保證 migration 看到的模型和執行時一樣。
- `.claude/rules/backend.md` 的 migration 條改成「一個工作段最多一組 migration（兩個 provider 各一個、同名）」；「會加 migration 的兩項工作不並行」維持，理由多一個：兩邊的 snapshot 都會衝突。
- SQLite 的「逐步套 migration 驗證既有資料升級」測試（`SqliteTestDb.CreateAsync(migrate: false)`）只在 SQLite 跑：PostgreSQL 鏈只有 baseline，沒有升級歷史可驗。

### 5.4 模型在 PostgreSQL 上的對應

這些調整放在 `Persistence.Postgres` 的模型調整器（替換 EF 的 `IModelCustomizer`，先套用既有的全部 `IEntityTypeConfiguration`，再改 PostgreSQL 需要的部分）。`Domain/Data/Configurations/` 不動，SQLite 的模型與 migration 不受影響（主控判斷 T8）。

**型別**

- `long` 主鍵用 Npgsql 預設的 identity（`GENERATED BY DEFAULT AS IDENTITY`），對應 SQLite 的 autoincrement。種子與 migration 都沒有寫明確 Id（核對過：沒有 `HasData`、沒有 `InsertData`），不會有 identity 序號落後的問題。
- `bool` → `boolean`；`double` → `double precision`；`DateOnly` → `date`；JSON 欄位維持 `text`（不改 `jsonb`，現在沒有在資料庫裡查 JSON 的需求）。
- **字串一律 `text`，不轉成 `varchar(n)`**（主控判斷 T6）。SQLite 不檢查 `HasMaxLength`，150 個宣告了長度的屬性目前沒有逐一確認寫入路徑會截斷或驗證；PostgreSQL 若照長度建 `varchar(n)`，超長值會變成只在 PostgreSQL 出現的 500。pg0 先讓兩邊行為一致，另在測試裡加長度守衛：測試主機註冊一個 `SaveChanges` 攔截器，寫入的字串超過模型宣告的長度就讓測試失敗（兩種資料庫都跑）。守衛清乾淨之後要不要改成 `varchar(n)`，另案決定。

**DateTime**（盤點列為高風險；主控判斷 T5）

Npgsql 6 之後，`timestamp with time zone` 只收 `Kind=Utc` 的值，`timestamp without time zone` 不收 `Kind=Utc`，寫錯在執行期丟例外。現有 29 個 `DateTime` 屬性存的都是業務牆上時間（`IBusinessTime.Now`，台北時間，`Kind` 不是 Utc），O1 新增的 ops 欄位一律 UTC、名稱以 `Utc` 結尾（維運設計 §10.4、§10.7）。規則：

- `DateTime`／`DateTime?` 預設 → `timestamp without time zone`（與 SQLite 存無時區文字的語意相同）。
- 屬性名稱以 `Utc` 結尾 → `timestamp with time zone`。
- `DateTimeOffset`（目前只有 `AppUser.LockedUntil`，寫入值來自 `TimeProvider.GetUtcNow()`，offset 為 0）→ `timestamp with time zone`。
- 模型測試（兩個 provider 都跑）：每個名稱以 `Utc` 結尾的 `DateTime` 屬性在 PostgreSQL 模型是 `timestamptz`、其餘是 `timestamp`；新程式要存 UTC 就得照這個命名，否則測試失敗。
- PostgreSQL 時間精度是微秒，.NET 是 100 奈秒；寫入再讀回的值可能少最後一位。比較「讀回值等於寫入值」的測試在 PostgreSQL 會失敗，pg0d 逐一處理（測試改比較到秒或毫秒，不改產品）。

**Collation**

- 字串欄位在 PostgreSQL 一律 `COLLATE "C"`（位元組序），等同 SQLite 預設的 BINARY：`=` 區分大小寫、排序依碼位、與報表記憶體路徑的序數比較一致（盤點；`ReportSqlEquivalenceTests` 是 pg1 的回歸）。使用者看得到的影響是中文排序依 Unicode 碼位，與現在 SQLite 相同（§13 U2）。
- 欄位層級指定，不依賴資料庫建立時的預設 collation：客戶或託管商建好的資料庫預設通常是 `en_US.utf8`，欄位層級才保證一致。

**部分索引 filter**

PostgreSQL 依索引名覆寫三個 filter（§4 #4），SQLite 原樣保留：

| 索引 | PostgreSQL 的 filter |
|---|---|
| `Dynamic_Record` 的 (ModuleId, HostKey) 唯一索引 | `"HostKey" IS NOT NULL AND NOT "DeleteFlag"` |
| `IX_Dynamic_RecordIndex_Truncated` | `"TextTruncated"` |
| `IX_Dynamic_RecordIndex_NoScaled` | `"ScaledValue" IS NULL AND "NumberValue" IS NOT NULL` |

SQLite 不改，是因為 SQLite 只在查詢條件能推出索引條件時才用部分索引，改了 filter 文字要連報表探測的 SQL 一起改，還要多一個 SQLite migration 重建索引，風險與收益不成比例。filter 寫錯的話 PostgreSQL baseline migration 在測試建樣板資料庫時就會失敗，不必另寫檢查。

### 5.5 Raw SQL 與 dialect（pg0 只處理 `DynamicQueryBuilder`）

- **識別字一律加雙引號**，兩種資料庫共用同一份字串（`"Dynamic_Record"`、`r."ModuleId"`、`i."TextValue"`）。SQLite 對加引號的識別字行為相同、執行計畫不變（主控判斷 T9）。`DynamicQueryBuilderTests` 裡比對 SQL 片段的斷言（例如 `r.OwnerUserId IN (@o0, @o1)`）跟著改。
- **布林字面值走 dialect**：`ISqlDialect` 加 `Bool(bool)`，SQLite 回 `0`／`1`（SQL 與現在逐字相同），PostgreSQL 回 `FALSE`／`TRUE`。`r.DeleteFlag = 0` 改成 `r."DeleteFlag" = {Bool(false)}`。
- **不分大小寫的 LIKE 走 dialect**：加 `LikeIgnoreCase(column, param)`。SQLite 回 `{column} LIKE {param} ESCAPE '\'`（與現在相同，只對 ASCII 不分大小寫）；PostgreSQL 回 `ILIKE`。差異：PostgreSQL 的 `ILIKE` 對非 ASCII 字母（全形英文、希臘字母等）也不分大小寫，SQLite 不會；中文沒有大小寫，不受影響。接受這個差異，列在已知限制。
- **新增 `PostgresDialect`**（放在既有的 `Persistence/Dynamic/Dialects/`）：
  - `Paginate`：`LIMIT @limit OFFSET @offset`，與 SQLite 相同。
  - `EscapeLike`：與 SQLite 相同（PostgreSQL 預設跳脫字元也是反斜線，`standard_conforming_strings` 開啟時 `ESCAPE '\'` 合法）。
  - `ContainsCaseSensitive`：`strpos({column}, {param}) > 0`；`StartsWithCaseSensitive`：`left({column}, length({param})) = {param}`。搭配欄位的 `"C"` collation，結果與 SQLite 的 `instr`／`substr` 一致。
  - `DateBucket`：pg1 才用到；pg0 先丟 `NotSupportedException`，避免誤用。
- **聚合結果的型別**：`BuildAggregate` 的 `SUM(...)` 與常數 `0` 包成 `CAST(... AS BIGINT)`，兩種資料庫都回 64 位元整數。PostgreSQL 的 `SUM` 超出 `bigint` 時丟 SQLSTATE `22003`，對應到既有的 `AggregateOverflowException`（§5.7），取代現在比對 `"integer overflow"` 訊息的寫法。SQLite 的 `CAST(SUM(x) AS BIGINT)` 溢位行為不變（`SUM` 本身先丟例外）。
- Dapper 的參數：日期篩選的參數是 `DateOnly.ToDateTime(...)`，`Kind=Unspecified`，Npgsql 推成 `timestamp`，與欄位型別一致，不必改。

### 5.6 寫入序列化與交易（`WriteGate` 的替代）

**現況的正確性前提**：SQLite 的寫入交易以 `BEGIN IMMEDIATE` 開始，一開始就持有全庫寫入鎖；`WriteGate` 只負責同行程內先進先出與逾時。事件與帳本設計 §7 明說跨記錄檢查（例如庫存不得為負）的正確性來自這把全庫寫入鎖；自動編號的讀後寫（§4 #14）、各處「先查再寫」也都默默靠它。PostgreSQL 預設的 Read Committed 沒有這把鎖，兩個交易可以各自通過「餘額夠扣」的檢查（寫入偏斜）。

**做法**（主控判斷 T3）：

- SQLite：完全不變（`WriteGate`＋`BEGIN IMMEDIATE`）。
- PostgreSQL：`WriteGate` 停用（不建立、不排隊，符合維運設計的已定決定）。改成**每個 EF 開始的寫入交易，第一個動作取同一把交易層級的 advisory lock**：先 `SET LOCAL lock_timeout`（`Database:LockTimeoutSeconds`），再 `SELECT pg_advisory_xact_lock(<固定鍵>)`，交易結束自動釋放。
  - 涵蓋範圍：用 EF 的交易攔截器在「交易已開始」時執行，所以 `ITransactionalRequest` 的交易與 `SaveChanges` 自己開的隱含交易都會取鎖，等同 SQLite「每個寫入交易都先拿寫入鎖」。
  - 不取鎖的：沒有交易的單一陳述式（`ExecuteUpdate`／`ExecuteDelete` 在自動提交模式下）與唯讀查詢。O1 的租約搶奪是單一條件式 `UPDATE`，本來就不需要外層鎖，也就不會被這把鎖序列化。
  - 隔離層級維持 Read Committed。持有全庫鎖之後，交易內每個陳述式看到的都是之前所有寫入者提交後的狀態，效果與 SQLite 的序列化寫入相同。
  - 取鎖逾時（SQLSTATE `55P03`）轉成既有的 `DatabaseBusyException`，API 回 503 加 `Retry-After`，與 SQLite 排隊逾時同一個契約。
  - 跨實例有效：鎖在資料庫裡，不像 `WriteGate` 只在單一行程內。
- 為什麼不選別的：
  - **只用 Read Committed＋逐處補鎖**：要先稽核所有讀後寫與跨記錄檢查（76 個 `ITransactionalRequest` 檔案、事件效果、帳本檢查），漏一處就是 PostgreSQL 上才有的資料錯誤，而且平常測不出來。pg0 的目的是「PostgreSQL 跑起來與 SQLite 一樣正確」，不是調吞吐。
  - **全部用 Serializable＋衝突重試**：可以抓到寫入偏斜，但 `Dynamic_RecordIndex` 上的聚合檢查會讓 SSI 的述詞鎖升級到整張表，同時寫入時誤判衝突多，要嘛使用者常看到 503，要嘛在 `TransactionBehavior` 重跑整個 handler（要清 change tracker、處理交易外的副作用），都比全庫鎖複雜。
- 代價：PostgreSQL 的寫入吞吐上限和 SQLite 一樣是「1 除以持鎖時間」；長交易（儲存檢查回填、Demo 產生器）會擋住所有寫入，這也和現在一樣。讀取不受影響（MVCC）。對 50 人以下、每客戶一套資料庫的目標夠用。
- **O1 的並行測試在 PostgreSQL 跑兩種模式**（主控判斷 T4）：一般模式（有全庫鎖），以及測試主機把寫入鎖換成不做事的版本。第二種模式證明條件式更新本身就能防重複，不是被全庫鎖蓋掉（維運設計 §10.7「並行假設以 PostgreSQL 為準，不能靠序列化保證正確」）。關鎖只能在測試裡用 DI 替換，沒有部署設定可以關。

**將來拿掉全庫鎖之前要補的清單**（不在 pg0 做，留給日後量到吞吐不夠時）：

1. 自動編號：`NumberSequenceRepository` 改成單一 `UPDATE … SET NextValue = NextValue + 1` 後讀回，或 `SELECT … FOR UPDATE`；第一筆插入撞唯一鍵時重試。
2. 跨記錄檢查：實作事件與帳本設計 §7 的 `ICheckSerializer`，求值前對同一組檢查取鎖列。
3. 稽核所有 `ITransactionalRequest` 的讀後寫，改成條件式更新或樂觀並行（`RowVersion`）。
4. 唯一性靠應用層先查再寫的地方，確認都有唯一索引兜底。
5. 所有既有的並行測試在「無鎖模式」重跑。

### 5.7 錯誤分類集中

- Application 層新增介面（暫名 `IDatabaseErrors`）：`IsUniqueViolation`、`IsBusyOrConflict`、`IsNumericOverflow`、`IsQueryCanceled`。實作放在 `Persistence`，用 SQLite 的 `SqliteErrorCode` 與 .NET 標準的 `DbException.SqlState` 判斷，不引用 Npgsql 型別，所以一份實作兩種資料庫都適用（主控判斷 T11）。
- 若 O1 已建立集中的衝突判斷函式（維運設計 §10.7），pg0 擴充它，不另建第二個；名稱以 O1 實作為準。

| 分類 | SQLite | PostgreSQL（SQLSTATE） |
|---|---|---|
| 忙碌或衝突（回 503） | `SQLITE_BUSY` 5、`SQLITE_LOCKED` 6 | `40001`、`40P01`、`55P03` |
| 唯一鍵衝突 | 19（`SQLITE_CONSTRAINT`，擴充碼 2067、1555） | `23505` |
| 數值溢位 | 訊息含 `integer overflow`（SQLite 沒有專屬錯誤碼） | `22003` |
| 查詢被中斷 | `SQLITE_INTERRUPT` 9 | `57014` |

- 改用它的地方：`DatabaseBusy.Is`（`DatabaseBusyExceptionHandler`、`UnitOfWork` 都經過它）、`RecordRepository.cs:195`、`EffectApplier.cs:186`、`TaskRuleStage.cs:109`。SQLite 的溢位仍是比對訊息，但只剩這一處、集中在分類實作裡。

### 5.8 啟動、檔案與金鑰

- **連線字串解析**：`SqliteConnectionStrings.ResolveDataSource` 只在 SQLite 時呼叫。「資料庫檔案所在目錄」改成一個 provider 感知的小函式：SQLite 回檔案目錄（記憶體資料庫回 null），PostgreSQL 一律回 null。`Program.cs` 建目錄、DataProtection 金鑰目錄與 `AttachmentSettingsFactory` 的預設附件目錄都經過它。
- **DataProtection 金鑰**：新增選填部署設定 `DataProtection:KeysDirectory`，有設就用（兩種 provider 都適用）；沒設時沿用現行規則（SQLite 檔案旁的 `keys`），PostgreSQL 則不落地並記 Warning，行為與現在的記憶體資料庫相同（主控判斷 T16）。
- **附件目錄**：沿用 `Attachments:Root`；PostgreSQL 沒設時用暫存目錄並記 Warning，與記憶體資料庫相同。
- 正式部署要強制設定這兩個目錄（沒設就拒絕啟動），留給部署段；pg0 只用在本機與測試。
- **啟動初始化串行**：PostgreSQL 上 `InitializeDatabaseAsync`（migrate、種子、admin 引導、索引回填、主檔完整性檢查）整段包在 session 層級的 advisory lock 裡（與 §5.6 不同的鍵），多個實例同時啟動時依序執行；EF Core 8 的 `Migrate` 本身沒有跨實例鎖（主控判斷 T15）。SQLite 不變。
- `--generate-demo-data`（`Dockerfile.demo` 用）只在 SQLite 跑，不改。

### 5.9 報表 SQL 路徑：pg0 在 PostgreSQL 上走記憶體路徑

- `Database:Provider=Postgres` 時，報表引擎強制為 `MemoryOnly`（既有選項，`ReportOptions.Engine`）；設定成 `SqlOnly` 則拒絕啟動。記憶體路徑只用 EF 的 LINQ 讀資料，兩種資料庫通用；縮小載入（`LoadNarrowed`）在 `MemoryOnly` 時本來就不走（`ReportRunner.cs:207`）。
- 影響：PostgreSQL 上報表與儀表板卡片結果正確，資料量大時比 SQLite 的資料庫端聚合慢，`Reports:MaxLoadRows`（預設 20 萬筆）照樣把關。
- **pg1（建議排在簽核 a6 之後、會計 s3b 之前）**做完整改寫：`ReportQueryBuilder` 識別字加引號與布林走 dialect；`INDEXED BY` 改成 dialect 的索引提示（SQLite 保留、PostgreSQL 回空字串）；`PostgresDialect.DateBucket`（`to_char` 搭配 ISO 週 `IYYY`／`IW`）；讀取快照改成 provider 專屬實作（PostgreSQL 用 `REPEATABLE READ READ ONLY` 交易加 `SET LOCAL statement_timeout`，逾時是 SQLSTATE `57014`，`CancellationToken` 可直接中斷）；`ReportQueryRepository` 的 `SqliteException` 判斷改用 §5.7；`ReportSqlEquivalenceTests` 加進雙跑集。pg1 的段位要使用者排（§13 的說明）。

## 6. 測試環境

### 6.1 PostgreSQL 從哪來

- **Testcontainers**（`Testcontainers.PostgreSql`，計畫時鎖定當時支援 .NET 8 的最新穩定版）啟動 `postgres:16-alpine`，每次 `dotnet test` 一個容器，跑完由 Testcontainers 的資源回收容器清掉（主控判斷 T12）。容器啟動參數提高 `max_connections`（例如 300），應付 xUnit 平行跑時的多個連線池。
- **外部伺服器覆寫**：設了環境變數 `FORMWRIGHT_TEST_PG`（一個可以 `CREATE DATABASE` 的連線字串）就直接用它，不啟動容器。給沒有 Docker、或想用 Homebrew 裝的本機 PostgreSQL 的人。
- 本機需求：Docker Desktop（個人與小型企業免費）、OrbStack、colima 擇一。寫這份設計時本機已裝 Docker Desktop，但 daemon 沒有在跑。
- 容器啟動失敗時，測試基礎丟出明確訊息：「PostgreSQL 測試環境無法啟動：Docker 沒有在跑，或設定 FORMWRIGHT_TEST_PG」，不默默跳過（§13 U1）。

### 6.2 測試資料隔離：樣板資料庫

現在每個測試（或每個測試類別）用一個全新的 SQLite 記憶體資料庫並套 migration。PostgreSQL 上每次都套 migration 太慢，改用樣板資料庫：

1. 第一次用到時建 `fw_template` 並套 PostgreSQL migration，然後關掉所有連線（Npgsql 要清連線池，否則樣板被占用無法複製）。
2. 每個測試資料庫 `CREATE DATABASE fw_<guid> TEMPLATE fw_template STRATEGY FILE_COPY`（小資料庫用 FILE_COPY 比預設的 WAL_LOG 快）。
3. 測試結束 `DROP DATABASE … WITH (FORCE)`（PostgreSQL 13 起），順便斷掉還開著的池連線。
4. 樣板只含 schema，不含種子；種子照現在的流程由測試主機啟動時寫入。

「重新啟動」類測試（`TestApp` 的 `sharedConnectionString`）與 O1 的「兩個排程器實例對同一資料庫」：同一個資料庫名稱開第二個 DbContext／第二個測試主機即可，PostgreSQL 上兩邊是真的不同連線，並行是真的。

### 6.3 切換方式與分類

- 環境變數 `FORMWRIGHT_TEST_DB=postgres` 讓共用的測試基礎改用 PostgreSQL；沒設就是 SQLite，與現在相同（主控判斷 T13）。Windows 用 `set`／`$env:` 設同一個變數。
- `SqliteTestDb` 改名為 provider 無關的 `TestDb`（對外介面不變：`CreateAsync`、`Context`；`Connection` 型別放寬為 `DbConnection`，目前只有 4 處用到）。`TestApp` 依同一個變數決定連線字串與 `Database:Provider`。共用的 provider 切換與樣板資料庫程式放在 `tests/Fixtures/`，用 `Compile Include` 連到兩個測試專案（照 `TestEntityProvider.cs` 的既有做法）。
- 用 xUnit trait 分三類：
  - `Category=Postgres`：**雙跑集**，gate 在 SQLite 全套之外，另在 PostgreSQL 上跑這一組。
  - `Category=SqliteOnly`：只在 SQLite 有意義的測試，在 PostgreSQL 跑全套時排除。
  - `Category=PgDeferred`：已知要等 pg1 才會在 PostgreSQL 過的測試（報表 SQL 路徑），必須寫原因；pg1 完成後全部拿掉。
- 既有的 `Category!=Benchmark` 預設排除維持；命令列 filter 會覆蓋 csproj 的預設，所以 PostgreSQL 的 filter 要自己再排除 Benchmark。

### 6.4 哪些測試兩種都跑、哪些只跑一種

**雙跑集（`Category=Postgres`，pg0c 起）**

- ApplicationTests：`Persistence/` 下的 `DynamicQueryBuilderTests`、`RecordQueryTests`、`NestedTransactionTests`、`PermissionRepositoryTests`、`SeedDataTests`、`SharedMasterSchemaTests`；`RecordIndexTests`；新的 `PostgresWriteLockTests`、`DatabaseErrorsTests`、DateTime 對應模型測試；O1 的排程器與通知維護測試（兩種模式，§7）。
- WebAPITests：`RecordsApiTests`、`ViewsApiTests`、`AutoNumberApiTests`、`ConcurrentWriteApiTests`（PostgreSQL 版，見下）、`InventoryConcurrencyTests`、`TaskRuleConcurrencyTests`、`EffectsRuntimeApiTests`、`ApprovalApiTests`、`TasksApiTests`、`NotificationsApiTests`、`AuthApiTests`、`IdempotencyApiTests`、`SavepointFeasibilityTests`、`HealthTests`。
- 規則（寫進 `docs/ai/testing.md` 與 `.claude/rules/backend.md`）：之後每段新增或修改的 repository、並行、raw SQL、migration 相關測試都加進雙跑集；mcp1a 的權杖與確認碼 repository 測試照 MCP 設計 §13.7 加入。

**只跑 SQLite（`Category=SqliteOnly`）**

- `WriteGateTests`、`MmapSizeSettingTests`、`EfTransactionManagerTests`（驗 `BEGIN IMMEDIATE`；PostgreSQL 的對應是 `PostgresWriteLockTests`）、`SchemaTests` 裡查 `sqlite_master`／`PRAGMA` 的案例、逐步套 migration 的升級測試、`ConcurrentWriteApiTests` 的檔案型資料庫外部占鎖案例、Benchmark。

**PostgreSQL 版的忙碌測試**：`ConcurrentWriteApiTests` 的「鎖被外部連線占住時回 503 加 `Retry-After`、放開後可成功」改成外部連線先取同一把 advisory lock、`Database:LockTimeoutSeconds` 設 1 秒。

**其餘測試**（設計器、規則、套件、領域邏輯等）平常只跑 SQLite；§6.6 的全套 PostgreSQL 試跑會涵蓋它們。

### 6.5 gate 與時間預算

- `scripts/unattended/gate.sh` 在三個後端專案之後、前端之前，加兩步：

  ```bash
  FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.ApplicationTests --filter "Category=Postgres&Category!=Benchmark"
  FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.WebAPITests --filter "Category=Postgres&Category!=Benchmark"
  ```

  每步一樣印 `exit=` 與秒數；gate 照舊依序跑，不和其他測試並行。
- gate 開頭先檢查 Docker（或 `FORMWRIGHT_TEST_PG`）：macOS 上 Docker 沒在跑就試 `open -a Docker` 並等最多 60 秒，還是不行就依 §13 U1 的裁決處理。
- **時間預算**：PostgreSQL 兩步合計不超過 10 分鐘（gate 從 30–45 分鐘變成 40–55 分鐘以內，主控判斷 T14）。粗估：容器與樣板約 10–20 秒；ApplicationTests 雙跑集約 150 個測試，每個複製資料庫約 0.1–0.3 秒；WebAPITests 雙跑集約 15 個類別、每類別一個測試主機。pg0c 合併前實測並把數字寫回這一節；超過 10 分鐘就先縮 WebAPITests 的清單（保留並行與 O1 相關的類別），不放寬預算。
- `docs/ai/testing.md` 補 PostgreSQL 的跑法、窄範圍指令範例與異常判讀（容器起不來、連線數用完、樣板被占用）。

### 6.6 全套 PostgreSQL 試跑（不進 gate）

```bash
FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.ApplicationTests --filter "Category!=SqliteOnly&Category!=PgDeferred&Category!=Benchmark"
FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.WebAPITests --filter "Category!=SqliteOnly&Category!=PgDeferred&Category!=Benchmark"
```

- pg0d 至少完整跑過一次並全數通過；之後在動到 Persistence、migration、交易或上線前手動跑。
- 預估時間是 SQLite 全套的 1.2–2 倍（每個測試多一次資料庫複製），所以不進 gate。

## 7. O1 與 mcp1a 在 PostgreSQL 上的驗證

O1 合併時 PostgreSQL 相容標為未驗證（維運設計 §10.8）。pg0c 把以下 O1 測試（ApplicationTests，假時間）加進雙跑集，並各在「有全庫鎖」與「無鎖」兩種模式跑：

1. **兩個排程器實例搶同一工作**：兩個實例各用自己的 DbContext 與連線，對同一個到期工作同時搶租約，結果只有一個 `LeaseOwner`、工作只執行一次。PostgreSQL 上第二個 `UPDATE` 會等第一個的列鎖，提交後重新評估 `WHERE`，影響列數為 0。
2. **持有者不續租、租約過期由另一實例接手**。
3. **通知維護兩條路徑並行不重複**：惰性觸發（使用者打開通知）與排程器對同一位使用者同時跑，同一任務的即將到期提醒只有一則、逾期提醒同一天只有一則；靠條件式更新任務列上的 `LastDueSoonNotified`、`LastOverdueNotified`（維運設計 §10.2.9）。
4. **啟動補列撞鍵略過**：兩個實例同時補 `Dynamic_ScheduledJob` 的列，不丟例外、最後每個工作一列；撞鍵判斷經 §5.7（維運設計 §10.4 B7）。

O1 若因為 PostgreSQL 的行為需要改程式（違反「pg0 完成後不改一行就能跑」），在 pg0 修並在交付說明列出原因。

維運設計 B30 寫「pg0 驗收含 mcp1 repository 的 PostgreSQL 測試」：mcp1a 排在 pg0 之後，pg0 驗收時那些測試還不存在；改成由 mcp1a 在它自己的驗收裡用 pg0 的環境跑（路線 mcp1a 列已這樣寫）。

## 8. 驗收

先說驗收方式再動手（AGENTS.md）。各段的驗收在 §9；pg0 整體合併前要全部成立：

| # | 指令或操作 | 預期 |
|---|---|---|
| 1 | `bash scripts/unattended/gate.sh <worktree>` | 每步 `exit=0`，含兩個 PostgreSQL 步驟；PostgreSQL 兩步合計 ≤ 10 分鐘 |
| 2 | `dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~MigrationConsistencyTests"` | 通過：兩個 provider 都沒有待產生的 migration |
| 3 | `FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~Scheduler\|FullyQualifiedName~NotificationUpkeep"`（類名以 O1 實作為準） | 通過，含兩種鎖模式的案例（§7） |
| 4 | §6.6 的兩行全套指令 | 0 失敗；`PgDeferred` 只含報表 SQL 路徑的測試，每個有原因 |
| 5 | 本機用 Docker 起 `postgres:16`，以 `Database__Provider=Postgres`、`ConnectionStrings__Dynamic=<連線字串>` 啟動 WebAPI | 首次啟動自動建表與種子；`/api/health/ready` 回 200；登入、建模組、新增記錄、列表篩選與排序、匯出、報表（記憶體路徑）、送簽與同意都可用 |
| 6 | 同一個資料庫同時啟動兩個 WebAPI 實例 | 兩個都啟動成功，`__EFMigrationsHistory` 只有一列 baseline，種子不重複 |
| 7 | `bash scripts/unattended/local-demo.sh` 與 Demo 站冒煙（SQLite） | 與 pg0 前相同 |
| 8 | 外洩掃描 | 通過 |

第 5、6 項是本機走查，沒有畫面改動，不需要四種寬度檢查。

## 9. 切段（每段可獨立合併）

**pg0a：provider 抽象，SQLite 行為不變**（約 M）

- `Database:Provider` 設定與驗證、DI 拆層、`Program.cs`／`AttachmentSettingsFactory` 改用 provider 感知的目錄函式、`DataProtection:KeysDirectory`。
- 錯誤分類介面與實作（含 PostgreSQL 的 SQLSTATE 分支，用純單元測試驗）；改用它的四處。
- `ISqlDialect` 加 `Bool`、`LikeIgnoreCase`；`DynamicQueryBuilder` 識別字加引號、布林與 LIKE 走 dialect、聚合 `CAST` 成 `BIGINT`；更新比對 SQL 片段的測試。
- 測試基礎：`SqliteTestDb` → `TestDb`、`Category=SqliteOnly` 標記、`MigrationConsistencyTests`（先只有 SQLite）、字串長度守衛攔截器。
- 驗收：gate 全過（只有 SQLite），沒有新的 migration，Demo 冒煙照舊。
- 不依賴 O1，可以先做。

**pg0b：PostgreSQL provider 與 migration**（約 M–L）

- 新專案 `Persistence.Postgres`、Npgsql、`AddPostgresDatabase`、模型調整（型別、DateTime、collation、三個 filter）、baseline `InitialCreate`（O1 合併後產）、design-time factory、`scripts/db/add-migration.sh`。
- `PostgresDialect`（`DateBucket` 先不支援）、全庫寫入鎖攔截器、`Database:LockTimeoutSeconds`、啟動串行鎖、報表強制 `MemoryOnly`。
- 文件與規則：`.claude/rules/backend.md` 的 migration 條、AGENTS.md 架構地圖（Persistence 兩種 provider）、`CONTRIBUTING.md` 的設定說明。改 `.claude/` 後重產 `.agents/` 副本一起 commit。
- 驗收：`MigrationConsistencyTests` 兩個 provider 都過；§8 第 5、6 項本機走查（手動起 PostgreSQL 容器）。
- 前置：O1 的 o1b migration 已合併；pg0b 開發期間不和其他會加 migration 的工作並行。

**pg0c：PostgreSQL 測試環境與雙跑集**（約 M）

- Testcontainers 與 `FORMWRIGHT_TEST_PG`、樣板資料庫、`TestDb`／`TestApp` 的 provider 切換、測試替換寫入鎖的掛勾。
- 雙跑集標記（§6.4）、`PostgresWriteLockTests`、PostgreSQL 版忙碌測試、O1 的兩種模式案例（§7）。
- `gate.sh` 加 PostgreSQL 步驟與 Docker 檢查、`docs/ai/testing.md`。
- 驗收：§8 第 1、3 項；實測時間寫回 §6.5。

**pg0d：全套 PostgreSQL 試跑與修正**（範圍要跑了才知道）

- 跑 §6.6，逐一分類失敗：產品在 PostgreSQL 上的錯（修）、測試依賴 SQLite 的隱含行為（改測試：隱含排序、時間精度、NULL 排序位置）、報表 SQL 路徑（標 `PgDeferred`）、真的只有 SQLite 有意義（標 `SqliteOnly`）。
- 產品側預期會遇到的：LINQ 依可空欄位排序（§4 #17，逐處明寫 NULL 放哪），以及長度守衛抓到的超長寫入。
- `.claude/rules/backend.md`「PostgreSQL 相容」條補兩句：可空欄位排序要明寫 NULL 的位置；不依賴沒有 `ORDER BY` 時的回傳順序。
- 驗收：§8 第 4 項。
- 停損：第一次試跑後，非報表、非測試寫法造成的產品失敗若超過 50 個，先回報失敗分類與估計，由使用者決定是否切成更多段，不在 pg0d 裡硬做完。

pg0a 與 pg0b 改同一批 Persistence 檔，依序做；pg0c 依賴 pg0b；pg0d 依賴 pg0c。只有 pg0b 加 migration（只有 PostgreSQL 的 baseline，SQLite 沒有新 migration）。

## 10. 開關組合與負責者

| Provider | 寫入序列化 | 報表引擎 | 金鑰目錄 |
|---|---|---|---|
| Sqlite（預設） | `WriteGate`＋`BEGIN IMMEDIATE` | 照 `Reports:Engine`（預設 Auto） | `KeysDirectory`，否則資料庫檔旁 `keys`；記憶體資料庫不落地 |
| Postgres | 全庫 advisory lock，逾時 `Database:LockTimeoutSeconds` | 強制 MemoryOnly（pg1 前）；設 SqlOnly 拒絕啟動 | `KeysDirectory`，否則不落地＋Warning |

- 選 provider、填連線字串與目錄是部署者（地端是客戶 IT，雲端是廠商）的事；都不是系統管理員在畫面上能改的業務政策。
- `Sqlite:MmapSizeMb` 只在 Sqlite 生效；`Database:LockTimeoutSeconds` 只在 Postgres 生效；設在另一種 provider 上記 Warning。

## 11. 可寫死清單（技術限制）

依 AGENTS.md 鐵則 2，以下是可以寫死、但要讓使用者看過的技術限制：

1. `Database:Provider` 只接受 `Sqlite`、`Postgres`。
2. 全庫寫入鎖與啟動鎖的 advisory lock 鍵是程式常數。
3. 錯誤分類的錯誤碼對照（§5.7）。
4. DateTime 的欄位型別規則：名稱以 `Utc` 結尾才是 `timestamptz`（§5.4）。
5. PostgreSQL 字串欄位一律 `text` 與 `COLLATE "C"`（§5.4；collation 依 §13 U2）。
6. 三個 PostgreSQL 部分索引 filter 的文字（§5.4）。
7. PostgreSQL 上報表強制 `MemoryOnly`，直到 pg1。
8. 測試用的 PostgreSQL 映像版本（`postgres:16-alpine`）與容器 `max_connections`。
9. gate 的 PostgreSQL 時間預算 10 分鐘（開發流程規則，不是產品行為）。

`Database:LockTimeoutSeconds`（預設 30）、`DataProtection:KeysDirectory` 是部署設定，可由部署者調整。

## 12. ERP 對照

這是平台底層，不新增主檔或單據，對使用者的業務流程沒有改變。和 ERP 慣例有關的兩點：

- 單據自動編號在並行時不得重號、跨記錄檢查（庫存不得為負、額度不得超過）不得被同時寫入繞過，是 ERP 的基本要求。§5.6 讓 PostgreSQL 上的保證與 SQLite 相同，並列出將來改成細粒度鎖時要補的項目。
- 主流 ERP 的正式環境都跑在資料庫伺服器上（Odoo 只支援 PostgreSQL）；Formwright 以 PostgreSQL 為正式版、SQLite 為研發與單機試用，與這個慣例一致。

## 13. 使用者決定（2026-11-12 已裁決）

以下會改變使用者看到的結果或要做的事，由使用者裁決。

**U1 gate 跑不起 PostgreSQL 時怎麼辦**

- **使用者裁決（2026-11-12）：gate 失敗。**

- 推薦：**gate 失敗**。gate 先試著啟動 Docker，60 秒內起不來就整個 gate 判失敗，不能合併。
- 另一個選擇：跳過 PostgreSQL 步驟，回報標「PostgreSQL 未驗證」，照樣可以合併。
- 理由：「之後每個功能兩種資料庫都驗」是使用者裁決；能跳過的話，PostgreSQL 很容易長期沒被驗。
- 選錯代價：選失敗，無人值守夜跑時若 Docker 起不來，當晚所有合併都會停住，要等使用者早上處理；選跳過，可能累積多段沒驗過 PostgreSQL 的程式，等到第一個客戶上線前才一次爆出來。

**U2 PostgreSQL 上文字的排序規則**

- **使用者裁決（2026-11-12）：位元組序（`COLLATE "C"`）。**

- 推薦：**位元組序（`COLLATE "C"`）**，與現在 SQLite 相同：英文大寫排在小寫前，中文依 Unicode 碼位排，不是依筆劃或注音。
- 另一個選擇：中文語系排序（PostgreSQL 的 ICU collation，例如依筆劃）。
- 理由：與 SQLite、報表記憶體路徑的結果一致，報表在兩條路徑、兩種資料庫上的排序都一樣；語系排序要另外處理記憶體路徑，否則同一張報表換路徑就換順序。
- 選錯代價：選位元組序，日後客戶要「依筆劃排序」時要做成一個能力（欄位或列表層級的排序規則），兩種資料庫與記憶體路徑都要實作；選語系排序，pg0 要多改記憶體路徑與等價測試，而且 SQLite 做不到相同結果，兩種資料庫的列表順序會不同。

**U3 正式版支援的 PostgreSQL 最低版本**

- **使用者裁決（2026-11-12）：PostgreSQL 16 以上。**

- 推薦：**PostgreSQL 16 以上**，測試環境用 16。
- 另一個選擇：14 以上（涵蓋較舊的客戶主機），或直接 17。
- 理由：16 是目前主流託管與 Linux 發行版都提供的版本；設計用到的功能（`DROP DATABASE … WITH (FORCE)` 要 13、`CREATE DATABASE … STRATEGY` 要 15）都在 16 內。版本越低，要驗的組合越多。
- 選錯代價：定太高，客戶既有的舊主機要先升級才能裝；定太低，要在測試裡多跑一個版本，否則就是對外承諾了沒驗過的版本。

**U4 Demo 站要不要改用 PostgreSQL**

- **使用者裁決（2026-11-12）：維持 SQLite。**

- 推薦：**維持 SQLite**，pg0 不動 Demo 站。
- 另一個選擇：Demo 改接免費的 PostgreSQL 託管（Render、Neon、Supabase 的免費層）。
- 理由：Demo 目前每次部署都重建資料，SQLite 檔案就夠；免費 PostgreSQL 託管有容量上限或到期刪除（⚠️ 各家的額度與到期規則未查證），多一個外部相依和失效點。
- 選錯代價：維持 SQLite，Demo 不能代表正式版的資料庫行為，PostgreSQL 的問題只能靠本機測試抓；改 PostgreSQL，要處理免費層到期或額度用完時 Demo 掛掉。

另外要使用者排的段位：**pg1（報表 SQL 路徑的 PostgreSQL 改寫）**。主控建議排在簽核 a6 之後、O2 之前，最晚在會計 s3b 之前（s3b 的分組小計、損益表要在 PostgreSQL 上用資料庫端聚合）。這符合使用者「其餘 PostgreSQL 改寫排在簽核之後」的裁決。**使用者裁決（2026-11-12）：pg1 排在簽核 a6 之後、O2 之前。**

## 14. 主控判斷

每條寫決定、理由、選錯代價。都是技術類，可以推翻。

**T1 用設定選 provider**：`Database:Provider`，不從連線字串猜。理由：明確、錯了就拒絕啟動，O1 的 ops info 也能直接讀。代價：部署多一個設定；設錯只是啟動失敗。

**T2 每個 provider 一個 migration assembly，SQLite 留在原處**：理由：EF Core 官方建議的多 provider 做法，兩邊型別不同無法共用一份 migration；SQLite 鏈不搬，現有資料庫與 Demo 零風險。代價：以後每次改 schema 產兩個 migration（腳本與一致性測試把關）；結構不對稱（SQLite 在主 Persistence、PostgreSQL 在獨立專案），日後要加第三種資料庫時可能再把 SQLite 拆出去。

**T3 PostgreSQL 用全庫交易 advisory lock 取代 `WriteGate`，隔離層級維持 Read Committed**：理由見 §5.6，保住跨記錄檢查與自動編號的正確性前提，不必在 pg0 稽核所有讀後寫。代價：寫入吞吐上限與 SQLite 相同；將來要拿掉時得補 §5.6 的清單。

**T4 O1 並行測試在 PostgreSQL 跑「有鎖」「無鎖」兩種模式**：理由：證明 O1 的條件式更新本身正確，不被全庫鎖蓋掉。代價：多一組測試與一個只在測試用的 DI 替換點。

**T5 DateTime 依名稱決定欄位型別**：預設 `timestamp`，名稱以 `Utc` 結尾為 `timestamptz`；`DateTimeOffset` 為 `timestamptz`。理由：既有 29 個欄位都是牆上時間，O1 已用 `Utc` 結尾命名 UTC 欄位；模型測試強制。代價：命名成為契約，有人把 UTC 存進不叫 `…Utc` 的欄位，Npgsql 會在執行期丟例外（測試會先抓到）。

**T6 PostgreSQL 字串欄位一律 `text`，加測試期長度守衛**：理由：避免只在 PostgreSQL 出現的超長值 500，同時讓兩種資料庫的測試都抓得到超長寫入。代價：資料庫層不檢查長度（與 SQLite 現況相同）；之後要改 `varchar(n)` 需要一個 migration。

**T7 collation `"C"` 在欄位層級指定**：依 U2 的推薦。理由見 §5.4。代價：同 U2。

**T8 PostgreSQL 專屬的模型差異放在模型調整器**：`Domain/Data/Configurations/` 不動，三個 filter 依索引名覆寫。理由：SQLite 的模型、migration、執行計畫完全不變。代價：filter 有兩份，改 SQLite 那份時要記得看 PostgreSQL 的覆寫（索引名寫在同一個常數清單，調整器找不到該索引就丟例外，不會默默失效）。

**T9 raw SQL 識別字兩邊都加雙引號**：理由：一份 SQL、不靠大小寫折疊（backend 規則）；SQLite 加引號不影響計畫。代價：`DynamicQueryBuilderTests` 的字串斷言要改；SQL 字串可讀性略降。

**T10 報表 SQL 路徑延到 pg1，pg0 在 PostgreSQL 上強制 MemoryOnly**：理由：報表 SQL 路徑是盤點裡最大的一塊（識別字、索引提示、日期分桶、快照與逾時），且路線裁決「其餘改寫排在簽核之後」；記憶體路徑結果正確。代價：pg1 之前 PostgreSQL 上的大報表比 SQLite 慢；在 PostgreSQL 驗報表的新功能（若有）只驗得到記憶體路徑。

**T11 錯誤分類用 `DbException.SqlState`，不引用 Npgsql 型別**：理由：一份實作兩邊適用，Persistence 不必相依 Npgsql，Application 只看介面。代價：SQLite 的溢位仍要比對訊息（SQLite 沒有專屬錯誤碼），已集中在一處。

**T12 Testcontainers＋外部伺服器覆寫**：理由：開發者不必手動裝 PostgreSQL，版本固定；沒有 Docker 的人可以接自己的伺服器。代價：gate 依賴 Docker；第一次要下載映像（約 100MB）。

**T13 環境變數切換＋三種 trait**：理由：不必複製測試類別，同一個測試兩邊都能跑；雙跑集範圍明確。代價：開發者要記得把新的資料層測試標進雙跑集（寫進規則與 testing.md）。

**T14 PostgreSQL 步驟時間預算 10 分鐘**：理由：使用者要求 gate 不能翻倍太多；10 分鐘約增加 25–30%。代價：雙跑集涵蓋有限，其餘靠 §6.6 的手動全套試跑。

**T15 PostgreSQL 啟動初始化整段串行**：用 session advisory lock。理由：EF Core 8 的 `Migrate` 沒有跨實例鎖，種子與 admin 引導也是先查再寫。代價：多實例同時啟動時後到的要等前一個做完，正常只有數秒。

**T16 金鑰與附件目錄在 PostgreSQL 上沿用「記憶體資料庫」的退路**：沒設就不落地或用暫存並記 Warning；強制檢查留給部署段。理由：pg0 只用在本機與測試，不擴大範圍。代價：有人在 pg0 之後、部署段之前就拿 PostgreSQL 版上線，會遇到重啟後登入失效、附件放在暫存目錄（部署段要在第一個客戶前做完）。

**T17 不在 pg0 做 CI**：理由：repo 沒有 CI，CI gate 是 O5；本機 gate 已涵蓋。代價：PostgreSQL 的驗證完全靠本機紀律（U1 決定 gate 擋不擋）。

## 15. 風險

1. **pg0d 的工作量要跑了才知道**：全套 PostgreSQL 試跑可能冒出很多失敗。對策：先分類、設停損（§9 pg0d），超過就回報使用者切段。
2. **DateTime Kind 例外只在執行期出現**：Npgsql 寫入時才檢查。對策：模型測試鎖住欄位型別；雙跑集與全套試跑涵蓋主要寫入路徑；名稱規則寫進 backend 規則。
3. **隱含順序與 NULL 排序**：PostgreSQL 沒有 `ORDER BY` 時的順序會隨更新改變，測試可能時好時壞。對策：pg0d 把依賴隱含順序的測試與產品查詢都改成明寫排序；規則補一句。
4. **全庫鎖掩蓋並行缺陷**：PostgreSQL 上除了 O1，其他功能仍在全庫鎖下，「無鎖也正確」只驗了 O1。對策：§5.6 的清單在拿掉鎖之前必做；新功能照 O1 的寫法（條件式更新），不新增依賴序列化的讀後寫。
5. **兩份 migration 漂移**：忘了產 PostgreSQL 那份，或兩份手改不一致。對策：一致性測試在 gate 擋；腳本一次產兩份；規則仍是「只用 EF 產生，不手改」。
6. **gate 變慢與依賴 Docker**：對策：時間預算與縮清單規則（§6.5）；U1 決定 Docker 起不來時的處理。
7. **模型調整器與 EF 升級**：替換 `IModelCustomizer` 依賴 EF 的擴充點，EF 大版本升級時要重驗。對策：一致性測試與模型測試會先失敗。
8. **pg0b 的 baseline 時點**：若 O1 之後、pg0b 之前又有別段加了 SQLite migration，baseline 也要涵蓋；pg0b 開工時以當下 `origin/main` 的模型產生即可，一致性測試會確認。
9. **Npgsql 版本**：要選與 EF Core 8.0.11 相容的 8.0.x；日後升 EF 時兩個 provider 一起升。

## 16. 要同步的文件與規則（實作時）

- `.claude/rules/backend.md`：migration 條（兩份同名）、「PostgreSQL 相容」條補 NULL 排序與隱含順序、新的資料層測試要進雙跑集、UTC 欄位命名以 `Utc` 結尾。改完重產 `.agents/` 副本。
- `AGENTS.md` 架構地圖：Persistence 改寫成「EF Core，SQLite 與 PostgreSQL 兩種 provider」，加 `Persistence.Postgres`。
- `docs/ai/testing.md`：PostgreSQL 跑法、雙跑集、異常判讀。
- `CONTRIBUTING.md`：`Database:Provider`、`Database:LockTimeoutSeconds`、`DataProtection:KeysDirectory` 與本機起 PostgreSQL 的方法。
- 維運設計 §10.7、§10.8：pg0 完成後把「PostgreSQL 未驗證」改成已驗證，並註明 B30 的 mcp1 部分移到 mcp1a。
- 事件與帳本設計 §7 第 6 點：註明 PostgreSQL 目前以全庫鎖維持前提，`ICheckSerializer` 尚未實作，列在本設計 §5.6 的清單。
- 手冊：沒有使用者看得到的行為改變，不需要更新。
