# 產品內備份 設計文件（段 bk1）

> 2026-11-21｜平台共用能力（非應用程式）｜前置：[維運與觀測](2026-11-11-ops-observability-design.md)（§6 護欄「備份隔離」、§8 備份做法、§17.2.4 `backup.freshness`、B20）、[PostgreSQL provider](2026-11-12-postgres-provider-design.md)（§2 正式部署另排）、[政策設定](2026-11-11-policy-settings-design.md)、[MCP server](2026-11-11-mcp-server-design.md)（mcpO1、mcpO2）
> 狀態：主控撰寫（2026-11-21），這次只寫設計、不改程式。依使用者 2026-11-21 裁決：會改變使用者操作的小題照推薦先定，寫成「主控暫定，待使用者確認」；技術類主控直接定。
> 標記：「（使用者裁決）」是使用者已拍板的；「（Q#）」是行為類，主控暫定、待使用者確認，彙整在 §24.1；「（T#）」是技術類，主控已定、可推翻，彙整在 §24.2。

## 1. 目的

Formwright 現在沒有任何備份。監控頁的「備份新鮮度」一律顯示「未設定」（`HealthProbes.cs` `BackupFreshnessProbe`），地端客戶要自己想辦法複製資料庫檔與附件目錄，複製時資料庫還在寫，拿到的檔案不一定能開。

bk1 讓產品自己做備份：

1. 每天在設定的時段自動備份資料庫與附件；系統管理員也可以按「立即備份」。
2. 備份寫進一個和資料分開的位置，經過一個通用的儲存介面；bk1 先做本機目錄（可以是另一顆磁碟或掛載的 NAS），雲端隔離儲存留到上雲再定。
3. 「備份新鮮度」改看真實的最近一次成功備份；備份失敗或過期就照 O2 的事件與告警通道通知。
4. 給維運 O4–O6 一個前提：agent 處置以隔離備份為護欄（使用者 11-19 裁決），雲端隔離儲存要在 O6 開工前補上（§7.3）。

還原的產品畫面留在階段 5（使用者 11-19 裁決）。bk1 只附手動還原步驟並演練一次（Q6），因為沒還原過的備份不能算有備份。

## 2. 範圍

**範圍內**

- 備份執行器：排程（每天一次，時段可設，Q2）、立即備份、同一時間全系統只跑一份、程序中斷後標記與重試。
- 資料庫快照：SQLite 用 `VACUUM INTO`（T3）；PostgreSQL 呼叫 `pg_dump -Fc`（T4）。
- 附件：增量複製到備份位置的附件池，每份備份附一份清單（§5.5）。
- 驗證：寫完重新讀一次備份確認能開、版本對得上（§5.6）。
- 儲存介面 `IBackupStore` 與本機目錄實作；位置由部署設定 `Backup:Root` 決定（Q3）。
- 保留份數政策與清理（只在儲存位置允許刪除時由產品清，§8）。
- `backup.freshness` 接上真實時間，並兼管「上次備份失敗」（Q4）。
- 監控頁新分頁「備份」：狀態、立即備份、歷程（Q5）。
- 稽核、手冊、部署文件（環境變數、手動還原步驟）、Demo 種子。
- 客戶部署用的 `Dockerfile` 裝 PostgreSQL 用戶端工具（T4）。

**範圍外**

- 產品內還原畫面、還原到某個時間點（階段 5，使用者 11-19 裁決）。
- 雲端隔離儲存的實作（S3 相容物件儲存、只寫不刪的憑證、物件鎖定）：要另開雲端帳號，留到上雲再定（domain-advisor 備忘）；bk1 只定介面與它要滿足的契約（§7.2）。
- PostgreSQL 的基礎備份＋WAL 歸檔（時間點還原）與託管資料庫快照：屬於雲端版部署，上雲時由廠商部署決定，產品不管（T4）。
- 從網頁下載備份檔：不做（§19 安全）。備份是全部資料的副本，開一個下載端點等於開一條把整套資料搬走的路。
- 備份檔加密（Q7）。
- 每週、每月輪替（祖父—父—子）：只做「保留最近 N 份」（Q2）。
- 一天多次備份：bk1 只做每天一次（Q2）。
- MCP 備份工具：bk1 不加。mcpO1 之後要讓 agent 看備份狀態時另加唯讀工具；任何 MCP 工具都不能刪備份（§19）。

## 3. 現況盤點（main 84dd2a05）

| 項目 | 現況 | 證據 |
|---|---|---|
| 備份新鮮度檢查 | 已登記，一律回「未設定」 | `Application/Dynamic/OpsService/Health/HealthProbes.cs` `BackupFreshnessProbe`；目錄 `HealthCheckCatalog.cs` `BackupFreshness`：排程型、全系統、間隔 60 分（15–1440）、警告 26 時、嚴重 50 時（1–720）、可關 |
| 資料目錄 | SQLite 檔案所在目錄；PostgreSQL 沒有 | `Persistence/DatabaseProvider.cs` `FileDirectory` |
| 附件目錄 | `Attachments:Root`，沒設就放資料庫旁的 `attachments`；PostgreSQL 沒設時記 Warning | `WebAPI/Program.cs:107-112`、`AttachmentSettingsFactory` |
| 附件檔案 | 檔名是系統產生的 32 碼十六進位 id，兩層目錄打散，`FileMode.CreateNew` 只寫一次、不覆寫 | `Persistence/Files/LocalAttachmentStore.cs` |
| 附件刪檔 | 只有兩處：上傳失敗時刪剛寫的檔、系統管理員手動「清孤兒附件」 | `AttachmentHandlers.cs:153`、`SweepAttachmentsHandler`（`:203-236`，寬限政策 A27）、`AttachmentUploadTracker.cs:23` |
| 金鑰目錄 | `DataProtection:KeysDirectory`，沒設就放資料庫旁的 `keys`；PostgreSQL 沒設時不落地 | `Program.cs:139-147` |
| 金鑰保護的東西 | 登入 cookie、任務復原權杖（`task-undo`）、寄信密碼（`ops-alert-smtp`）；MCP 權杖只存雜湊，不靠金鑰 | `Security/DataProtectionTaskUndoTokens.cs:11`、`Ops/AlertMail.cs:71`、`Domain/Entities/Dynamic/McpToken.cs:18` `TokenHash` |
| 金鑰換過時 | 寄信設定頁已會顯示「密碼解不開（加密金鑰可能換過了），請重新輸入密碼。」 | `docs/manual.md` §23.4 |
| 背景排程器 | 一次只跑一個工作（迴圈裡逐一 `await RunAsync`），單次最長 10 分鐘，租約 5 分、每分續租 | `OpsService/Scheduling/JobRunner.cs`、`SchedulerLimits.cs` |
| 看門狗 | 另一個 hosted service，不經排程器 | `Health/OpsWatchdog.cs`、`Program.cs` `OpsWatchdogHostedService` |
| 磁碟剩餘檢查 | 量資料庫目錄、附件根目錄、金鑰目錄；Demo 站不適用 | `Program.cs` `OpsStoragePaths`、`HealthCheckCatalog` `StorageDiskFree` |
| provider 專屬程式 | SQLite 的 PRAGMA 放在 `SqliteDatabaseHealthProbe`；PostgreSQL 專屬放 `Persistence.Postgres` | `Persistence/SqliteDatabaseHealthProbe.cs`、`Persistence.Postgres/PostgresDatabaseHealthProbe.cs` |
| 寫入閘門 | SQLite 同程序寫入排隊；讀不經閘門 | `Persistence/WriteGate.cs` |
| 政策 | `ops` 分類的政策都在 `PolicyCatalog`；沒有「一天中的時刻」這種型別 | `Domain/Policies/PolicyCatalog.cs:154-232`、`PolicyDefinition.cs:13` `PolicyKind` |
| 稽核 | `ops` 組已有保留清理、健康檢查、事件、通道、寄信設定 | `Application/Common/Audit/AuditActions.cs:118-125` |
| 監控頁 | 四個分頁：總覽、事件、健康檢查、告警通道；只有系統管理員 | `frontend/src/pages/system/OpsPage.vue`、`OpsController.cs`（`ISystemAdminRequest`） |
| 容器 | 客戶用 `Dockerfile`（`aspnet:8.0`，沒有 `pg_dump`）；Demo 用 `Dockerfile.demo`，建置時產生 Demo 資料、刪掉 `keys` | `Dockerfile`、`Dockerfile.demo` |
| Demo 站 | Render 免費方案，沒有永久磁碟，閒置 15 分休眠，喚醒回到映像的資料；每天台北 02:00 部署 | `docs/deploy.md`「Demo 部署」 |
| PostgreSQL 版本 | 測試與本機走查用 16 | `docs/ai/testing.md:35`、`CONTRIBUTING.md:29` |

## 4. 已定決定（本設計照用，不再問）

- 還原留在階段 5；bk1 先做備份，排在 O4–O6 之前，因為 agent 處置以隔離備份為護欄（使用者 11-19 裁決，`docs/roadmap.md` 第 126 行）。
- bk1 → pkg1 → au1 的順序，三段都不依賴 O3（使用者 11-21 裁決）。
- 備份要放在 agent、維運中心與 MCP 維運工具都碰不到的地方；雲端版用獨立帳號、只寫不刪的儲存（維運設計 §6「備份隔離」）。
- 資料修復與還原一律是 L4、只通知（維運設計 §3 第 6 條、§12）。
- 新程式不寫 SQLite 專屬寫法，非寫不可時隔離在 provider 專屬類別（`.claude/rules/backend.md`）。

## 5. 備份內容與格式

### 5.1 一份備份有什麼

| 內容 | SQLite | PostgreSQL |
|---|---|---|
| 資料庫 | `database.sqlite`（`VACUUM INTO` 產生的獨立檔，沒有 `-wal`） | `database.pgdump`（`pg_dump -Fc`） |
| 附件 | 寫進共用的附件池；這份備份用到哪些列在 `attachments.txt` | 同左 |
| 清單 | `manifest.json`，最後寫 | 同左 |
| 金鑰 | 不放（Q1） | 不放（Q1） |

`manifest.json`（格式版本 1）：`formatVersion`、`runKey`、`snapshotUtc`、`appVersion`、`commit`、`edition`、`provider`、`migrationId`（最新一筆已套用的 migration）、`database`（檔名、位元組、SHA-256）、`attachments`（個數、總位元組、清單檔名與它的 SHA-256）、`includesKeys: false`。

`attachments.txt` 每行一個「id、位元組數」，依 id 排序。

### 5.2 儲存位置的版面（T6）

```
<Backup:Root>/
  sets/20261122T020012Z-1f3a9c2e/      一份備份（快照時間＋runKey 前 8 碼）
    database.sqlite | database.pgdump
    attachments.txt
    manifest.json                       最後寫，有它才算完整
  attachments/ab/cd/<id>                附件池，版面和附件根目錄相同
  .staging/<runKey>/                    執行中的暫存，完成後整個搬進 sets/
```

- 本機目錄：先寫 `.staging/<runKey>/`，驗證通過後以同一檔案系統內的改名搬進 `sets/`，所以 `sets/` 下不會有半份。
- 附件池的檔案先寫成 `<id>.partial` 再改名；目標已存在就略過、不覆寫。
- 每次開始先清掉 `.staging/` 裡別的 runKey 的殘留（上一次中斷留下的）。
- 附件池和附件根目錄版面相同，手動還原時整個目錄複製回去即可（§9.3 的步驟）。

### 5.3 SQLite（T3）

- `VACUUM INTO '<staging>/database.sqlite'`：在一個讀取交易裡產生一致的快照檔，WAL 模式下不擋寫入，產出的檔案已經壓縮、沒有 WAL。
- 只放在 Persistence 的 SQLite 專屬類別 `SqliteDatabaseBackup`（交付說明列出），自己開一條連線，不經 `WriteGate`（只讀）。
- 已知影響：備份期間 WAL 無法完整 checkpoint，WAL 檔會長大；備份結束後恢復。大型資料庫在備份時段可能讓 `database.walSize` 一時警告（§16）。
- 記憶體資料庫（測試）照樣能 `VACUUM INTO`；正式部署一定是檔案。

### 5.4 PostgreSQL（T4）

- 呼叫外部程式 `pg_dump --format=custom --no-owner --no-privileges --file=<staging>/database.pgdump`，連線參數從連線字串拆出來；**密碼只經環境變數 `PGPASSWORD` 傳給子程序**，不出現在命令列、log、錯誤訊息（錯誤訊息先遮掉密碼再截斷 1000 字）。
- 程式路徑是部署設定 `Backup:PgDumpPath`，沒設就找 `PATH` 上的 `pg_dump`；找不到回 `pgDumpMissing`。
- 開始前比對版本：`pg_dump --version` 的主版本小於伺服器 `server_version_num` 的主版本時回 `pgDumpVersion`，訊息帶兩個版本。
- `pg_dump` 自己用一致的快照，不擋一般寫入；啟動時的 migration 會被它擋住（DDL 要等它），備份時段不部署即可（§16）。
- 客戶用 `Dockerfile` 加裝 PGDG 的 `postgresql-client-16`（和測試容器同主版本；之後支援更新的伺服器時換成新版用戶端，新版用戶端能備舊版伺服器）。`Dockerfile.demo` 是 SQLite，不裝。
- 程式放 `Persistence.Postgres` 的 `PostgresDatabaseBackup`（交付說明列出）。
- 雲端版另用託管快照或 WAL 歸檔做時間點還原：部署層的事，產品不感知、不管（範圍外）。

### 5.5 附件（T15）

- 快照之後才複製附件，複製來源是附件根目錄現有的檔案中 `LastWriteUtc ≤ snapshotUtc` 的那些。快照裡被引用的附件，一定是在記錄寫入（也就是快照）之前就寫完的（上傳是先寫檔、後寫記錄，`AttachmentHandlers.cs:152`）。
- 最後寫入未滿 2 分鐘的檔案，先等它滿 2 分鐘再複製，並在複製前後比對大小與 `LastWriteUtc`；不一致就這次略過（它不會是快照裡被引用的完整檔案）。
- 附件池已有同 id 的檔案就不複製（附件只寫一次、不覆寫，所以 id 相同內容就相同）。
- **備份執行中，「清孤兒附件」回 409**（Q9）：清孤兒是唯一會刪掉「曾被引用」的附件的地方；不擋的話，快照引用的檔案可能在複製前被刪掉。
- 快照之後才上傳的附件不在這份備份，下一份會有。

### 5.6 驗證（T3、T4）

寫完、搬進 `sets/` 之前：

- SQLite：唯讀開啟備份檔，`PRAGMA quick_check` 要回 `ok`，`__EFMigrationsHistory` 最新一筆要等於 `manifest.migrationId`（provider 專屬類別內）。
- PostgreSQL：`pg_restore --list` 成功，而且列得出資料表。
- 兩者都重新計算資料庫檔與 `attachments.txt` 的 SHA-256，和寫入時算的一致。
- 附件：清單裡每個 id 在附件池都存在、大小相符（只比大小，不逐檔重算雜湊；第一次寫入池時已算過）。
- 驗證失敗：狀態 `failed`、代碼 `verifyFailed`，暫存刪掉，不進 `sets/`。

### 5.7 金鑰（Q1）

金鑰目錄不隨資料備份。理由：金鑰和資料庫放在一起，拿到備份的人可以解開寄信密碼，還能偽造任何使用者的登入 cookie 登入正式站（金鑰沒換之前都有效）。業界慣例是金鑰與資料分開存放。

不帶金鑰還原的代價很小：所有人重新登入、系統管理員重填寄信密碼（畫面已有提示，§3）、未完成的任務復原連結失效；MCP 權杖只存雜湊，不受影響。手冊與還原步驟寫明這三件事。

## 6. 執行：排程、立即備份、多實例（T1）

### 6.1 不走排程器，另開備份執行器

排程器單次最長 10 分鐘，而且一個實例一次只跑一個工作（§3）。大一點的資料庫加上第一次的附件全量複製會超過 10 分鐘，還會讓 `ops.monitor`（告警信、排程型健康檢查）等著。所以備份另開一個 hosted service `BackupWorker`（每個實例一個，和看門狗同一種做法）：

- 每 60 秒醒來一次（技術限制）：先看要不要排一份（§6.2），再領一份排隊中的來跑。
- `Ops:Scheduler:Enabled=false` 的實例也不跑備份執行器（同一個意思：這台不做背景工作）。
- 單次最長 6 小時（技術限制），逾時 `failed`／`timeout`；站台關閉時中止，狀態 `interrupted`。

### 6.2 什麼時候排一份（Q2）

- **排程**：營運時區的現在時刻已過今天的 `ops.backupHour` 整點，而且今天這個時段（`ScheduleSlot`＝營運時區的日期）還沒有排隊中、執行中、成功或失敗的紀錄，就排一份。所以站台在那個時段沒開著（例如休眠），醒來後會補一次，當天不再排。
- **第一次**：位置設好以後，還沒有任何成功或失敗的紀錄時立刻排一份，不等時段。
- **立即備份**：系統管理員按鈕（§11）。
- **中斷重試**：`interrupted` 不算占用時段，下一輪會再排；同一時段最多中斷 3 次（技術限制），之後當天不再自動排，新鮮度照常會警告。
- 失敗（`failed`）不自動重試：同一個原因（空間不足、權限錯）重試也會失敗，交給告警與管理員處理。

改營運時區或備份時段的當天，可能少排一次或多排一次（已知限制，§16）。

### 6.3 同一時間只有一份（T11）

- 排隊中、執行中的紀錄 `ActiveSlot = 1`，其他狀態是 null；`ActiveSlot` 有唯一索引。兩台同時要排時，後插入的撞唯一鍵就略過（`IsUniqueViolation`）。
- 領工：條件式更新 `Status: queued → running`、填 `OwnerInstanceId`、`HeartbeatUtc`，影響一列才算領到。
- 心跳：每 60 秒以 `OwnerInstanceId` 為條件更新 `HeartbeatUtc`；更新落空（被別台判定中斷）就中止。
- 心跳超過 5 分鐘沒更新（技術限制），任一實例的執行器把它條件式改成 `interrupted`、`ActiveSlot = null`，並清掉它的暫存（下一次開始時也會清）。

### 6.4 一次執行的步驟

1. 檢查位置（§7.4）與剩餘空間：預估需要＝資料庫大小＋還不在附件池的附件總大小，乘 1.1；目的地剩餘不足就 `destinationFull`，什麼都不寫。
2. 資料庫快照（§5.3、§5.4），記下 `SnapshotUtc`。
3. 附件（§5.5）。
4. 驗證（§5.6），寫 `manifest.json`，搬進 `sets/`。
5. 狀態 `succeeded`。
6. 保留清理（§8）。清理失敗不改變這份備份的成功，只記 Warning 與 `PruneError`。
7. 不論成敗，立刻重跑一次 `backup.freshness`（不受「立即檢查」60 秒限制），事件馬上開或結案（§9）。

`Stage` 欄記目前在哪一步（`database`、`attachments`、`verify`、`prune`），畫面顯示進度。

## 7. 儲存介面與隔離

### 7.1 介面（T5）

Application 定 `IBackupStore`：

- `Kind`（`local`；之後的雲端實作用自己的代碼）、`DisplayLocation`（給畫面看的位置，雲端實作不含憑證）、`Isolated`（是否為隔離儲存）、`CanDelete`。
- `ExistsAsync(path)`、`PutAsync(path, stream)`（**只寫一次**：目標已存在就丟例外，不覆寫）、`ListAsync(prefix)`、`DeleteAsync(path)`（`CanDelete` 為 false 時一律丟例外）。
- 本機目錄實作 `LocalDirectoryBackupStore`：`Isolated = false`、`CanDelete = true`；非 Windows 時目錄權限 0700、檔案 0600（T7）。
- 資料庫快照寫在本機暫存目錄；本機目錄實作的暫存就在 `<Backup:Root>/.staging/`，之後用改名搬進去；雲端實作改成寫完上傳（`Backup:StagingDirectory`，上雲時再加）。

### 7.2 雲端隔離儲存要滿足的契約（上雲時實作）

- 獨立帳號（不和產品主機、維運中心、agent 共用），產品持有的憑證只能新增物件、列出、讀取存在與否，**不能刪除、不能覆寫**；保留期由儲存端的物件鎖定或生命週期規則負責。
- 憑證只存在產品主機的部署設定，不進資料庫、不進稽核、不給任何 MCP 工具。
- `Isolated = true`、`CanDelete = false`。
- 供應商、物件鎖定模式、保留天數，上雲時另定（domain-advisor 備忘）。

### 7.3 給維運 O6 的前提

維運設計 §6 要求 agent 與維運中心碰不到備份。bk1 期間只有本機目錄（`Isolated = false`），所以：

- `Ops:Edition = Cloud` 而儲存不是隔離的，啟動記 Warning，備份分頁顯示「雲端版的備份應放在隔離儲存（尚未支援），目前存在本機目錄」（T13）。
- **O6（L2 自動處置）開工前，雲端隔離儲存要先上線**；路線文件的 O6 列加這個相依（主控改）。

### 7.4 位置檢查（T7）

`Backup:Root` 沒設：不備份，新鮮度「未設定」。有設時（相對路徑以站台目錄解析）：

- 和資料庫目錄、附件根目錄、金鑰目錄相同、在它們底下、或包住它們，一律不收：啟動記 Warning，備份不跑，新鮮度嚴重 `locationInvalid`。理由：備份和資料在同一棵目錄裡，誤刪資料夾時一起沒；附件池在附件根目錄底下還會被當成附件。
- 目錄不存在就建立；建不起來或寫不進去，該次 `destinationUnwritable`。
- 只比對路徑，不判斷是不是同一顆磁碟（容器裡量不準，同 Demo 站的磁碟檢查）。手冊建議放另一顆磁碟或 NAS。

## 8. 保留與清理（T14）

- 政策 `ops.backupRetentionCount`（出廠 7）：成功的備份依快照時間保留最新 N 份，多的從最舊的刪。
- 最新一份成功的永遠不刪；失敗與中斷的不會進 `sets/`，不算份數。
- 刪完 set 後清附件池：池裡的檔案不在任何保留中 set 的 `attachments.txt`，而且寫入時間早於最新一份的快照時間，才刪。
- 改小保留份數不會立刻刪，下一次成功備份後才照新值刪（照 `.claude/rules/backend.md`「政策調嚴不溯及既有資料」的精神，刪除只在備份時發生）。
- 儲存位置不能刪（`CanDelete = false`，雲端隔離儲存）時產品完全不刪，保留由儲存端負責；政策頁這項的說明寫明「只對可刪除的儲存位置有效」。
- 每刪一份記一筆稽核 `backup.prune`（行為者系統、來源管道排程工作）。
- 備份歷程表只留最新 200 筆（技術限制），由執行器在每次結束時順手清。

## 9. 備份新鮮度接上真實時間（Q4、T8）

### 9.1 判斷

| 情況 | 狀態 | 數值 | 訊息代碼 |
|---|---|---|---|
| `Backup:Root` 沒設 | 未設定 | — | — |
| PostgreSQL、bk1b 還沒合併 | 未設定 | — | `providerUnsupported` |
| 位置不合法（§7.4） | 嚴重 | — | `locationInvalid` |
| 從沒成功過、也沒失敗過（第一份排隊中或執行中） | 等待中（不開事件） | — | `firstBackupPending` |
| 從沒成功過、已失敗過 | 嚴重 | — | `neverSucceeded` |
| 有成功 | 依門檻（出廠 26／50 時） | 距最近一次成功的快照時間，取整數時 | — |
| 有成功，但之後最近一次結束的是失敗 | 上一列與「警告」取較嚴重者 | 同上 | `lastRunFailed` |

- 中斷不算失敗：中斷會自動重試（§6.2），只有時間拖過門檻才警告。
- 門檻、間隔、開關沿用 `HealthCheckCatalog` 的登記與 `Dynamic_HealthCheck` 的設定，不另寫一份。
- 每次備份結束都立刻重跑一次這項檢查（§6.4 第 7 步），所以失敗不必等 60 分鐘才告警，成功後事件也立刻結案。
- 用主機自備的備份、不用產品備份的客戶：不設 `Backup:Root`，狀態就是「未設定」、不開事件；要把這張卡藏起來就關掉這項檢查（既有功能）。

### 9.2 告警

完全走 O2：類別基礎設施、指紋 `infra`＋`backup.freshness`、站內通知 `ops.alertRecipients`、寄信依 `ops.alertEmailCategories` 與最低嚴重度。bk1 不加新通道、不加新收件人政策。

### 9.3 手動還原步驟（Q6，寫進 `docs/deploy.md`）

SQLite：

1. 停站台。
2. 選一份 `sets/<名稱>/`，確認 `manifest.json` 的 `appVersion` 不比要啟動的版本新（舊備份可以用新版啟動，migration 會往前升；新備份不能用舊版開）。
3. 把 `database.sqlite` 複製成資料庫檔（例如 `/app/data/poc.db`），刪掉舊的 `-wal`、`-shm`。
4. 把 `<Backup:Root>/attachments/` 整個複製到附件根目錄（多出來的檔案是別份備份的，之後可用「清孤兒附件」清掉）。
5. 啟動。所有人要重新登入；系統管理員到「寄信設定」重填密碼（Q1）。

PostgreSQL：第 3 步改成建一個空資料庫，`pg_restore --no-owner --dbname=<新資料庫> database.pgdump`，再把連線字串指過去。

bk1 驗收時兩種資料庫各演練一次（§22 D1、D2）。

## 10. 資料模型與 migration（T2）

### 10.1 `Dynamic_BackupRun`

| 欄位 | 型別 | 說明 |
|---|---|---|
| `Id` | long | 主鍵 |
| `RunKey` | string(32) | GUID 十六進位，唯一；set 名稱、暫存目錄用它 |
| `Trigger` | string(16) | `schedule`、`manual` |
| `ScheduleSlot` | string(10)? | 排程的營運時區日期 `yyyy-MM-dd`；立即備份是 null；索引 |
| `Status` | string(16) | `queued`、`running`、`succeeded`、`failed`、`interrupted` |
| `ActiveSlot` | int? | 排隊中、執行中是 1，其他 null；唯一索引（§6.3） |
| `Stage` | string(16)? | `database`、`attachments`、`verify`、`prune` |
| `RequestedUserId` | string? | 立即備份的人 |
| `QueuedUtc`、`StartedUtc`、`HeartbeatUtc`、`SnapshotUtc`、`FinishedUtc` | DateTime（UTC） | 後四個可空 |
| `OwnerInstanceId` | string? | 執行的實例（`OpsInstance`） |
| `StoreKind` | string(32) | `local` |
| `SetName` | string(64)? | 成功才有 |
| `Provider`、`AppVersion`、`MigrationId` | string | 快照當時的值 |
| `DatabaseBytes`、`AttachmentCount`、`NewAttachmentCount`、`NewAttachmentBytes`、`TotalAttachmentBytes` | long? | 統計 |
| `ErrorCode` | string(32)? | §10.2 |
| `ErrorDetail` | string(1000)? | 摘要，已遮密碼、不含記錄值 |
| `PruneError` | string(1000)? | 保留清理失敗的摘要 |
| `PrunedSets` | int? | 這次刪了幾份 |

- 索引：`RunKey` 唯一、`ActiveSlot` 唯一（兩種資料庫的唯一索引都允許多個 NULL，不用部分索引）、`(Status, FinishedUtc)`、`ScheduleSlot`。
- 時間欄位以 `Utc` 結尾、`Kind` 明確（PostgreSQL `timestamptz`）。
- 一個 migration `BackupRuns`，用 `scripts/db/add-migration.sh` 產 SQLite 與 PostgreSQL 各一份；只新增一張表，沒有資料搬移，Down 就是刪表。

### 10.2 錯誤代碼

`destinationUnwritable`、`destinationFull`、`databaseSnapshotFailed`、`pgDumpMissing`、`pgDumpVersion`、`attachmentCopyFailed`、`verifyFailed`、`timeout`、`interrupted`、`locationInvalid`。前端以 `ops.backup.error.<代碼>` 顯示，不認得的代碼原樣顯示。

## 11. API

都在 `/api/v3/dynamic/system/ops/`，全部 `ISystemAdminRequest`。

| 方法 | 路徑 | 說明 |
|---|---|---|
| GET | `backups` | 狀態與最近 20 筆歷程（`cursor` 往下翻）：`configured`、`locationValid`、`storeKind`、`displayLocation`、`isolated`、`edition`、`canDelete`、`provider`、`includesKeys`（false）、`schedule`（`hour`、營運時區偏移、`nextScheduledUtc`）、`retentionCount`、`lastSuccess`、`current`（排隊中或執行中的那筆）、`runs`、`nextCursor`、`manualAllowed`、`manualDisabledReason` |
| GET | `backups/{id}` | 單筆詳細（含錯誤摘要、各項統計、set 名稱） |
| POST | `backups` | 立即備份：成功 202 回那一筆；已有排隊或執行中 409 `backupInProgress`；位置沒設或不合法 409 `backupNotConfigured`；Demo 站 409 `backupManualDisabled`（Q8） |

- 沒有刪除、下載、改位置的 API（§19）。
- 既有的 `POST attachments/sweep`：備份排隊中或執行中回 409 `backupInProgress`（Q9）。
- 新訊息進 `MessageText` 四語表（`backupInProgress`、`backupNotConfigured`、`backupManualDisabled`），超過上限類訊息不適用。

## 12. 權限與稽核

- 看備份狀態、立即備份：系統管理員（`ISystemAdminRequest`），讀不到權限就拒絕（fail closed）。
- 稽核新增（`ops` 組，一般風險 W）：
  - `backup.request`：立即備份（行為者、結果；409 也記「被拒」）。
  - `backup.prune`：保留清理刪了一份（行為者系統、來源管道排程工作、目標是 set 名稱）。
- 排程備份本身不進稽核（歷程表就是它的紀錄）；失敗走事件。
- MCP：bk1 不登記任何備份工具；之後 mcpO1 若加「讀備份狀態」，只能是唯讀；mcpO2 的處置清單不能有刪除或改寫備份的動作（§19）。

## 13. 政策項與部署設定（單一來源）

### 13.1 政策（`PolicyCatalog`，分類 `ops`，只限全系統）

| 代碼 | 說明 | 型別 | 出廠 | 技術上下限 |
|---|---|---|---|---|
| `ops.backupHour` | 每天幾點開始排程備份（營運時區，0–23 整點） | Integer，單位 `hour` | 2 | 0–23 |
| `ops.backupRetentionCount` | 保留最近幾份成功的備份（只對可刪除的儲存位置有效） | Integer，單位 `count` | 7 | 1–90 |

- 前端政策頁從 API 讀定義與上下限，不另寫一份；備份分頁顯示的時段與份數也來自 `GET backups`。
- 新鮮度門檻沿用 `HealthCheckCatalog`（26／50 時），不另設政策。
- 沒有「關掉排程備份」的政策（Q3）：要不要備份由部署設定決定。

### 13.2 部署設定（`appsettings` 或環境變數，不進政策目錄）

| 設定 | 說明 |
|---|---|
| `Backup:Root` | 備份位置（本機目錄或掛載點）；沒設就不備份 |
| `Backup:PgDumpPath` | `pg_dump` 的路徑；沒設就找 `PATH`（`pg_restore` 在同一個目錄找） |

為什麼位置是部署設定、不是畫面設定（Q3）：可以在畫面上填路徑，就等於讓系統管理員把整套資料寫到主機上任何站台程序寫得到的地方；雲端版的客戶管理員也能把廠商負責的備份改走或關掉。

## 14. 畫面

### 14.1 維運監控 → 「備份」分頁（Q5）

`OpsPage.vue` 的分頁加 `backup`（網址 `?tab=backup`），內容在新元件 `presentation/ops/OpsBackupTab.vue`：

- **狀態卡**：備份位置（`displayLocation`）與種類、「隔離儲存：是／否」、排程「每天 02:00（+08:00）」、下次排定、保留「最近 7 份」、上次成功（時間、大小、附件數）、「不含加密金鑰：還原後要重新登入並重填寄信密碼」一行說明（Q1）。
- **位置沒設**：卡片改成說明「還沒有設定備份位置。請由主機管理者設定部署設定 `Backup:Root`（見部署文件）」，沒有立即備份按鈕。
- **位置不合法**：紅色提示「備份位置不能和資料庫、附件或金鑰目錄重疊」。
- **雲端版、非隔離**：黃色提示（§7.3）。
- **立即備份**按鈕：排隊或執行中時停用並顯示目前步驟（「資料庫」「附件 120／340」「驗證」「清理舊備份」）；每 5 秒重新讀取直到結束。Demo 站停用並寫原因（Q8）。
- **歷程**：時間、觸發（排程／立即，立即的帶操作者）、狀態、資料庫大小、附件（新增／總數）、耗時、錯誤；點一列開詳細。
- 時間照營運時區顯示並標時區（同監控頁其他分頁）。

### 14.2 健康檢查卡

`backup.freshness` 的卡片顯示「N 小時前」、訊息代碼的文字（`ops.health.message.lastRunFailed` 等），加一個「看備份」連到 `?tab=backup`。

### 14.3 四種寬度

- 390：分頁列可左右捲（`q-tabs` 已開 `mobile-arrows`）；狀態卡一欄；歷程用卡片列表（`q-table` grid 模式，同稽核頁）；詳細用全螢幕對話框。
- 820：狀態卡兩欄；歷程表隱藏「耗時」「觸發者」欄。
- 1366、1920：全部欄位；詳細用右側滑出。
- 任何寬度不得橫向捲動；長路徑用 `word-break: break-all`。

### 14.4 i18n

`shared/i18n/<code>/ui.json` 四語加 `ops.tabs.backup`、`ops.backup.*`（狀態卡、步驟、錯誤代碼、提示）、`ops.health.message.{lastRunFailed,neverSucceeded,locationInvalid,firstBackupPending,providerUnsupported}`、兩條政策的名稱與說明、兩個稽核動作名稱。

## 15. Demo 種子（Q8）

| 示範 | 怎麼做 |
|---|---|
| 成功路徑 | `Dockerfile.demo` 設 `Backup__Root=/app/backups`；站台每次喚醒時資料回到映像狀態、沒有成功紀錄，執行器立刻跑第一份（§6.2），1 分鐘內就有一筆成功、新鮮度正常 |
| 失敗分支 | Demo 資料產生器寫一筆三天前的排程備份 `failed`／`destinationFull` 歷程（只寫紀錄，沒有檔案），歷程與詳細看得到失敗的樣子 |
| 被擋 | 「立即備份」停用並寫「Demo 站不開放立即備份」（比照寄信設定的測試信，§3）；本機走查另驗備份中按「清孤兒附件」被擋 |
| 未設定 | 公開 Demo 不示範；本機走查不設 `Backup__Root` 驗 |

- Demo 保留份數用政策出廠值 7；Demo 資料庫只有幾 MB，喚醒後只會有一兩份，容器重啟就清空。
- `scripts/unattended/local-demo.sh` 加 `Backup__Root=<worktree>/backend/data/local-e2e-backups`（啟動前清空），走查成功路徑與立即備份。
- 改種子前照 `.claude/rules/backend.md` grep 既有測試對 Demo 維運資料的斷言（`DemoData*Tests`、`OpsMonitor*Tests` 的事件數、稽核筆數）。

## 16. 開關組合下誰負責

| 情況 | 誰備份 | 監控頁 |
|---|---|---|
| 地端 SQLite、設了 `Backup:Root`（另一顆磁碟或 NAS） | 產品每天備份；異地保存（把 NAS 再複製出去）是客戶 IT 的事 | 新鮮度正常；失敗或過期照 O2 告警 |
| 地端、沒設 `Backup:Root` | 沒有產品備份，客戶 IT 自理 | 「未設定」、不開事件；可關掉這項檢查 |
| 地端 PostgreSQL、`pg_dump` 可用 | 產品每天 `pg_dump` | 同第一列 |
| 地端 PostgreSQL、`pg_dump` 不在或版本太舊 | 每次失敗 | 嚴重 `neverSucceeded` 或警告 `lastRunFailed`，錯誤寫明原因 |
| 地端 PostgreSQL、用資料庫自己的備份 | 客戶 IT | 不設 `Backup:Root` → 未設定 |
| 雲端版、bk1 期間（本機目錄） | 產品備份到本機目錄；廠商另做主機快照 | 加「非隔離」提示；**O6 不得上線**（§7.3） |
| 雲端版、隔離儲存上線後 | 產品備份到隔離儲存；保留由儲存端；可再加託管快照 | 正常；保留份數政策不生效 |
| 多台實例 | 只有一台在跑（`ActiveSlot`） | 歷程記執行實例 |
| 某台 `Ops:Scheduler:Enabled=false` | 那台不跑；別台照跑；全部關掉就沒人跑 | 全部關掉時新鮮度會過期告警 |
| 備份時段遇到部署重啟 | 這次中斷，重啟後自動重跑（每時段最多 3 次） | 不告警，除非拖過門檻 |
| 備份中有人寫資料 | 不擋；快照之後的寫入在下一份 | — |
| 備份中有人按「清孤兒附件」 | 被擋 409 | — |
| 大型 SQLite 資料庫備份中 | WAL 一時變大 | `database.walSize` 可能短暫警告，備份完自己恢復 |
| 改營運時區或備份時段的當天 | 可能少一次或多一次 | 少一次時可能過 26 時警告 |
| Demo 站 | 喚醒就跑一份到容器暫存目錄，休眠後消失 | 正常；立即備份停用 |
| 沒開任何應用程式 | 照常備份（空系統的資料庫與附件） | 正常 |

## 17. ERP 對照

以下依主控對主流產品的了解整理，未另行上網查證。

- **開源 ERP**：資料庫管理頁可以把資料庫（`pg_dump`）連同附件檔案庫打包下載，用主密碼保護；代管雲端版每天自動備份，保留多份、存到多個機房，從管理畫面還原。Formwright 相同的是「資料庫＋附件一起備」「每天自動」；簡化的是只保留最近 N 份、沒有週月輪替；刻意不同的是不開網頁下載備份（下載端點是常見的攻擊面，備份是整套資料）。
- **傳統大型 ERP**：備份交給資料庫與基礎架構層（資料庫自己的備份排程、儲存快照），由顧問或 IT 設定，應用程式只看狀態。Formwright 的目標客戶是 50 人以下、沒有專職 DBA，所以產品自己做每日備份，同時保留「用主機自己的備份、不設位置」這條路。
- **檢核 1–7**：不新增模組、主檔、單據，不碰單據鏈、數量契約、開關組合矩陣與會計（ERP 資料模型 §7 不適用）。
- **檢核 9 核心能力**：儲存介面、執行器、新鮮度都是平台機制，任何應用程式組合都一樣用，沒有替特定應用程式開後門。

## 18. 宗旨檢查（formwright-purpose-check）

1. **機制還是政策**：通過。備份流程、格式、驗證、隔離規則是平台機制；備份時段與保留份數是政策（`ops.backupHour`、`ops.backupRetentionCount`，系統管理員在政策頁改）；位置是部署設定（安全理由，§13.2）。技術上限列在 §19。
2. **寫死掃描**：設計階段不適用；新寫死項目列 §19，審查 diff 時照 skill 的 grep 再跑。
3. **單一來源**：通過。兩條政策只在 `PolicyCatalog`；新鮮度門檻只在 `HealthCheckCatalog`；前端從 `GET backups` 與政策 API 取值，不另寫 `2`、`7`、`26`。
4. **兩個應用程式**：通過。備份整套資料，CRM、進銷存、生產管理都一樣用。
5. **空系統**：通過。不開任何應用程式也照常備份；Demo 種子只寫一筆歷程。
6. **套件只用定義組成**：不適用（不碰套件）。
7. **ERP 對照**：通過，見 §17。
8. **權限 fail closed、驗證不只在前端、沒有執行期 DDL**：通過。API 全部系統管理員、後端檢查；位置檢查、同時只一份、409 都在後端；新表走 EF migration。
9. **不限產業**：通過。理由是「小公司沒有 DBA」與「資料不能丟」，服務業、貿易商、批量工廠都成立。

## 19. 可寫死清單

| 項目 | 值 | 類別 |
|---|---|---|
| 執行器醒來間隔 | 60 秒 | 技術限制 |
| 單次備份最長 | 6 小時 | 技術限制 |
| 心跳間隔／判定中斷 | 60 秒／5 分鐘 | 技術限制 |
| 同一時段中斷重試上限 | 3 次 | 技術限制 |
| 附件最後寫入未滿多久先等 | 2 分鐘 | 技術限制（資料完整性） |
| 預估空間的餘裕 | 1.1 倍 | 技術限制 |
| 歷程保留 | 最新 200 筆 | 技術限制 |
| 錯誤摘要長度 | 1000 字 | 技術限制 |
| 同一時間全系統只跑一份備份 | `ActiveSlot` 唯一 | 資料完整性 |
| 最新一份成功的備份不刪 | — | 資料完整性 |
| 備份中不能清孤兒附件 | 409 | 資料完整性 |
| `manifest.json` 最後寫、沒有它不算完整；附件池只寫一次 | — | 資料完整性 |
| 位置不能和資料庫、附件、金鑰目錄重疊 | — | 安全／資料完整性 |
| 位置只由部署設定，畫面不能改、不能關 | — | 安全 |
| 沒有下載、刪除備份的 API；MCP 沒有刪除或改寫備份的工具 | — | 安全 |
| 金鑰不隨資料備份（Q1 照推薦定案時） | — | 安全 |
| `pg_dump` 密碼只走環境變數，不進命令列、log | — | 安全 |
| 備份目錄 0700、檔案 0600（非 Windows） | — | 安全 |
| 還原一律 L4（沿用維運設計 §12） | — | 安全 |

## 20. 與既有設計或程式的矛盾

1. **維運設計 §8 表「SQLite：`VACUUM INTO` 或 Online Backup API，連同 `attachments/`、`keys/` 目錄一起備」**：改成不帶 `keys/`（Q1）；選定 `VACUUM INTO`（T3）。定案後回填 §8。
2. **維運設計 §2「備份還原的實作仍在路線階段 5」、§7 表「產品提供備份工具與新鮮度檢查（階段 5）」、§15 第 2 條**：備份改在 bk1，還原仍在階段 5（使用者 11-19 裁決已改路線，設計文字未跟上）；回填三處。
3. **維運設計 §6「備份隔離」列**：bk1 只有本機目錄，不滿足；改寫成「bk1 本機目錄＋介面，雲端隔離儲存在 O6 之前上線」，並在 §9 的 O6 相依加上它（§7.3）。
4. **B20「備份還沒做時回未設定」**：改成 §9.1 的表，「未設定」只剩沒設位置（與 bk1b 合併前的 PostgreSQL）。`BackupFreshnessProbe` 的註解一起改。
5. **PostgreSQL provider 設計 §2「正式部署：PostgreSQL 版的 Dockerfile、部署設定檔、備份……第一個客戶上線前另排」**：備份（`pg_dump`）與 `Dockerfile` 的用戶端工具由 bk1b 做，其餘照舊另排。
6. **`docs/feature.md` 第 85 行「備份還原（段 階段5）」**：拆成「產品內備份（bk1）」與「備份還原（階段 5）」兩列（主控改）。
7. **`docs/deploy.md`「單一 instance 與 volume」只說要掛 `/app/data`**：補「備份位置要另外掛一個 volume（最好在另一顆磁碟）」，並加 `Backup__Root`、`Backup__PgDumpPath` 兩列環境變數、§9.3 的手動還原步驟。
8. **`Dockerfile.demo` 只刪 `keys`**：加 `Backup__Root`（Q8）；建置時 `--generate-demo-data` 不啟動 hosted service，映像裡不會有備份檔。
9. **手冊 §23**：「四個分頁」改五個，加「23.5 備份」；§23.2 健康檢查補備份新鮮度的幾種訊息；附件段補「備份中不能清孤兒附件」。

## 21. 切段

每段可獨立合併、Demo 不壞。

| 段 | 內容 | migration | 共用檔 | 依賴 |
|---|---|---|---|---|
| bk1a | 後端核心（SQLite）：`Dynamic_BackupRun` 與 migration、`IBackupStore` 與本機目錄實作、位置檢查、`BackupWorker`（排程、第一次、立即備份、心跳、中斷）、`SqliteDatabaseBackup`（快照＋驗證）、附件增量與清單、保留清理、`BackupFreshnessProbe` 真實判斷、API 三支、清孤兒附件 409、兩條政策、稽核兩個動作、`MessageText` 四語、`deploy.md`（環境變數、SQLite 手動還原）；PostgreSQL 上回「未設定 `providerUnsupported`」 | 有（`BackupRuns`，SQLite＋PostgreSQL 各一） | `DynamicDbContext`、新 `BackupRunConfiguration`、兩份 model snapshot、`PolicyCatalog.cs`、`AuditActions.cs`、`MessageText.cs`、Application／Persistence `DependencyInjection.cs`、`Program.cs`、`HealthProbes.cs`、`AttachmentHandlers.cs`、`docs/deploy.md` | 無 |
| bk1b | PostgreSQL：`PostgresDatabaseBackup`（版本比對、`pg_dump`、`pg_restore --list` 驗證、密碼遮罩、逾時砍程序）、`Backup:PgDumpPath`、`Dockerfile` 裝 `postgresql-client-16`、拿掉 `providerUnsupported`、`deploy.md` PostgreSQL 還原步驟 | 無 | `Persistence.Postgres/DependencyInjection.cs`、`Dockerfile`、`docs/deploy.md` | bk1a |
| bk1c | 畫面與 Demo：監控頁「備份」分頁、健康卡連結、四語 `ui.json`、`Dockerfile.demo` 的 `Backup__Root`、Demo 失敗歷程種子、Demo 停用立即備份、`local-demo.sh`、手冊 §23、四種寬度走查 | 無 | `OpsPage.vue`、`HealthCheckCard.vue`、`shared/i18n/*/ui.json`、`ops-types.ts`、`ops-queries.ts`、`dynamic-api.ts`、`Dockerfile.demo`、Demo 種子、`scripts/unattended/local-demo.sh`、`docs/manual.md` | bk1a |

- bk1a 是本段唯一的 migration，不和其他會加 migration 的段平行（後端規則）。
- bk1b、bk1c 都只依賴 bk1a，彼此沒有共用檔，可以平行；`deploy.md` 只有 bk1a、bk1b 動，依序即可。
- bk1a 合併後，地端 SQLite 客戶設好 `Backup:Root` 就有每日備份（還沒有畫面，看得到健康卡與告警）；bk1c 讓 Demo 能示範。
- 路線：O6 列加「相依：雲端隔離儲存」；雲端隔離儲存另開一段（上雲時），不在 bk1（主控改 roadmap）。

## 22. 可驗收案例

### 22.1 執行與排程

| # | Given | When | Then |
|---|---|---|---|
| B1 | SQLite 檔案資料庫、設了 `Backup:Root`、從沒備份過 | 啟動 | 1 分鐘內開始第一份；完成後 `sets/<名稱>/` 有 `database.sqlite`、`attachments.txt`、`manifest.json`；新鮮度正常、0 時 |
| B2 | B1 的備份 | 用 sqlite3 開 `database.sqlite` | `PRAGMA integrity_check` 回 `ok`；`__EFMigrationsHistory` 最新一筆等於 `manifest.migrationId` |
| B3 | 營運時區 +08:00、`ops.backupHour`＝2；昨天 02:00 那份已成功 | 今天 01:59 | 不排 |
| B4 | 同 B3 | 今天 02:00 之後 | 1 分鐘內排一份；當天之後不再排 |
| B5 | 02:00 時站台沒在跑 | 09:30 啟動 | 1 分鐘內補一份；當天不再排 |
| B6 | 兩個實例同時到期 | 兩台各醒一次 | 只有一筆排隊或執行中；另一台略過，不報錯 |
| B7 | 備份執行中 | 系統管理員按立即備份 | 409 `backupInProgress`；稽核 `backup.request` 結果「被拒」 |
| B8 | 備份執行中 | 使用者新增、修改記錄 | 照常成功，不排隊等備份 |
| B9 | 備份執行中 | 程序被停、重啟 | 5 分鐘內那筆變 `interrupted`；重啟後自動再排一份；`sets/` 下沒有半份；`.staging/` 的殘留在下一份開始時清掉 |
| B10 | 同一天已中斷 3 次 | 下一輪 | 不再自動排；立即備份仍可用 |
| B11 | `Ops:Scheduler:Enabled=false`、只有一台 | 到時段 | 不備份；過 26 時後新鮮度警告 |

### 22.2 附件

| # | Given | When | Then |
|---|---|---|---|
| A1 | 120 個附件、附件池是空的 | 備份 | 複製 120 個；`attachments.txt` 120 行 |
| A2 | A1 之後新上傳 3 個 | 再備份 | 只複製 3 個；清單 123 行；統計「新增 3／總數 123」 |
| A3 | 快照之後才上傳的附件 | 這份備份完成 | 不在這份清單；下一份有 |
| A4 | 備份排隊中或執行中 | 系統管理員清孤兒附件 | 409 `backupInProgress`，沒有檔案被刪 |
| A5 | 附件池已有同 id 的檔案 | 備份 | 不覆寫、不重複複製 |

### 22.3 失敗與驗證

| # | Given | When | Then |
|---|---|---|---|
| E1 | 目的地剩餘空間小於預估 | 備份 | `failed`／`destinationFull`；沒有寫出任何檔案；新鮮度至少警告 `lastRunFailed`；照 O2 規則寄告警信 |
| E2 | 目的地沒有寫入權限 | 備份 | `failed`／`destinationUnwritable` |
| E3 | 故障注入：寫完後改壞備份檔 | 驗證 | `failed`／`verifyFailed`；暫存刪掉、`sets/` 沒有這份 |
| E4 | E1 之後修好空間 | 立即備份 | 成功；新鮮度事件結案 |
| E5 | 從沒成功過、第一份失敗 | 檢查 | 嚴重 `neverSucceeded` |
| E6 | `Backup:Root` 設在資料庫目錄底下 | 啟動 | 記 Warning；不備份；新鮮度嚴重 `locationInvalid`；`POST backups` 409 `backupNotConfigured` |
| E7 | 沒設 `Backup:Root` | 檢查 | 未設定、不開事件 |
| E8 | 上次成功 27 小時前 | 檢查 | 警告、數值 27 |
| E9 | 錯誤摘要裡原本有連線字串的密碼 | 看歷程詳細與 log | 看不到密碼 |

### 22.4 保留

| # | Given | When | Then |
|---|---|---|---|
| R1 | 保留 7、已有 7 份成功 | 第 8 份成功 | 刪最舊一份；只被那份引用的附件池檔案刪掉，其他留著；稽核 `backup.prune` 一筆 |
| R2 | 已有 7 份 | 政策改成 3 | 當下不刪；下一份成功後剩 3 份 |
| R3 | 保留 1 | 新的一份成功 | 剩新的這一份 |
| R4 | 測試用的不能刪的儲存（`CanDelete = false`） | 第 8 份成功 | 不刪任何東西、沒有 `backup.prune` |
| R5 | 第 8 份失敗 | — | 不刪任何一份 |

### 22.5 PostgreSQL（bk1b）

| # | Given | When | Then |
|---|---|---|---|
| P1 | PostgreSQL 16、`pg_dump` 16 可用 | 備份 | `database.pgdump`；`pg_restore --list` 成功 |
| P2 | `Backup:PgDumpPath` 指到不存在的檔 | 備份 | `failed`／`pgDumpMissing` |
| P3 | 用戶端主版本小於伺服器 | 備份 | `failed`／`pgDumpVersion`，訊息帶兩個版本 |
| P4 | 備份執行中 | 用 `ps` 看子程序命令列 | 沒有密碼 |
| P5 | bk1a 已合併、bk1b 未合併 | PostgreSQL 上檢查 | 未設定 `providerUnsupported`，不開事件 |

### 22.6 權限、畫面、還原、Demo

| # | Given | When | Then |
|---|---|---|---|
| S1 | 一般使用者 | `GET backups`、`POST backups` | 403 |
| S2 | MCP 已開 | `tools/list` | 沒有任何備份工具 |
| S3 | 備份分頁 | 390、820、1366、1920 | 不橫向捲動；390 歷程是卡片 |
| S4 | `Ops:Edition=Cloud`、本機目錄 | 開備份分頁 | 看到「非隔離」提示；啟動 log 有 Warning |
| S5 | 備份執行中 | 看分頁 | 按鈕停用、顯示目前步驟，結束後自己更新 |
| D1 | SQLite 的一份備份 | 照 `deploy.md` 還原到全新目錄並啟動 | 能登入（要重新登入）；記錄筆數一致；附件下載得到；寄信設定頁提示重填密碼 |
| D2 | PostgreSQL 的一份備份 | 照 `deploy.md` `pg_restore` 到空資料庫並啟動 | 同 D1 |
| G1 | Demo 映像 | 啟動後 1 分鐘 | 歷程有一筆成功（剛跑的）與一筆三天前的失敗；新鮮度正常；立即備份停用並寫原因 |

## 23. 測試重點

- **migration**（`Category=Postgres` 雙跑）：`MigrationConsistencyTests`；`ActiveSlot` 唯一索引在兩種資料庫都允許多個 NULL、拒絕第二個 1。
- **repository**（雙跑）：排隊撞唯一鍵、條件式領工、心跳落空、判定中斷、歷程只留 200 筆；`ScheduleSlot` 查詢。
- **執行器**（Application，假時鐘、假儲存）：B3–B6、B9–B11；營運時區跨日；第一次立即排；中斷重試上限；逾時。
- **SQLite 快照**（WebAPI 或 Persistence 測試，暫存目錄的檔案資料庫）：B1、B2、B8（快照期間另一條連線寫入不被擋）；E3 驗證失敗。
- **附件**：A1–A5；2 分鐘等待用假時鐘；清孤兒附件 409。
- **儲存**：只寫一次（目標已存在丟例外）、`.partial` 改名、`CanDelete = false` 時刪除丟例外；非 Windows 的檔案權限。
- **位置檢查**：E6 的三種重疊（相同、在底下、包住），Windows 不分大小寫。
- **新鮮度**：§9.1 每一列；結束後立刻重跑；事件開與結案走既有 `IncidentRecorder` 測試的做法。
- **保留**：R1–R5，附件池清理不刪仍被引用的檔。
- **PostgreSQL**（bk1b，Testcontainers 的 16）：P1–P4；密碼遮罩單元測試；逾時砍子程序。測試容器裡要有 `pg_dump`：用 Testcontainers 的 `ExecAsync` 在容器內跑，或測試機裝用戶端，計畫時定。
- **權限與稽核**：S1；`backup.request` 成功與被拒、`backup.prune`。
- **前端 vitest**（bk1c）：分頁切換與網址、四種狀態（未設定、不合法、非隔離、正常）、執行中輪詢與停用、錯誤代碼顯示、Demo 停用原因、健康卡連結。
- **種子**：G1；改種子前 grep 既有斷言（§15）。
- **手動演練**：D1、D2 結果寫進交付說明。

## 24. 待確認清單

### 24.1 行為類（主控暫定，待使用者確認）

- **Q1 加密金鑰不和資料一起備份（推薦）。**
  背景：伺服器有一個金鑰目錄（`keys/`），用來加密登入狀態、寄信密碼。原本的維運設計寫「連同 `keys/` 一起備」。
  例子：備份放在公司 NAS，某天 NAS 被偷或帳號外洩。連金鑰一起備份的話，拿到的人可以解開寄信密碼，還能偽造任何人（包括系統管理員）的登入狀態，直接登入正式系統；不帶金鑰，他拿到的是資料副本，但登不進系統。
  - 推薦：不帶金鑰。代價：用備份還原後所有人要重新登入，系統管理員要到「寄信設定」重填 SMTP 密碼（畫面本來就會提示）；AI 存取權杖不受影響。
  - 替代：一起備份。代價：還原後什麼都不用重填；但備份外洩等於正式系統的登入被攻破，要靠換金鑰補救。
  - 替代 2：另設一個獨立的金鑰備份位置（例如不同的 NAS 帳號）。代價：多一個部署設定要管，客戶 IT 容易設成同一個地方，等於沒分開。
  - 選錯的代價：小。改成要帶金鑰只是多複製一個目錄、manifest 改一個欄位。
- **Q2 每天備份一次、時段可設（出廠營運時區凌晨 2 點）、保留最近 7 份、位置設好後立刻跑第一份（推薦）。**
  例子：一家 30 人的貿易商，凌晨 2 點自動備份，NAS 上永遠有最近 7 天每天一份；週三發現週一有人誤刪一批客戶，可以從週一凌晨那份找回來（還原畫面在階段 5，之前照手動步驟）。
  - 推薦：每天一次、只保留最近 N 份。代價：最壞會丟一天的資料；超過 7 天前的狀態找不回來（可調到 90 份）。
  - 替代：每 N 小時一次。代價：要多一條「間隔」政策，和「時段」怎麼搭配要另訂規則；PostgreSQL 客戶真的需要更細時，由主機做時間點還原比較適合。
  - 替代 2：每日＋每週＋每月輪替。代價：規則難懂（「保留 7 天、4 週、3 個月」），保留政策從一條變三條。
  - 選錯的代價：小。多加頻率或輪替只是加政策與排程判斷，資料表不用改。
- **Q3 備份位置只由主機的部署設定決定，畫面上不能改、不能關（推薦）。**
  背景：系統管理員在畫面上看得到位置、按得到立即備份，但改位置要改主機的環境變數 `Backup__Root`。
  例子：雲端版的客戶系統管理員把位置改成空的或關掉，廠商負責的備份就停了；地端的系統管理員在畫面上填 `/etc` 之類的路徑，站台程序就會往那裡寫整套資料。
  - 推薦：只能由部署設定。代價：地端小公司要請管主機的人設定一次；沒設定時監控頁寫明要找誰、設什麼。
  - 替代：畫面上可改位置與開關。代價：上面兩種風險；要另做路徑白名單、雲端版鎖定等規則。
  - 選錯的代價：中。之後若要開放畫面設定，要補路徑白名單與雲端鎖定，資料表要加設定欄。
- **Q4 備份失敗就告警，不等過期（推薦）。**
  背景：原本的「備份新鮮度」只看「距上次成功幾小時」，出廠 26 小時警告、50 小時嚴重。
  例子：今天凌晨的備份因為 NAS 滿了失敗。只看新鮮度的話，要到明天凌晨 4 點（26 小時）才會警告；推薦做法是失敗後立刻警告，下一次成功自動結案。
  - 推薦：上次失敗至少算警告，從沒成功過而且失敗了算嚴重。代價：NAS 短暫斷線導致一次失敗也會發一則警告。
  - 替代：只看新鮮度。代價：失敗要等一天才知道，修好之前已經少了一份。
  - 選錯的代價：極小，只是檢查的判斷式。
- **Q5 畫面放在「維運監控」的新分頁「備份」（推薦）。**
  - 推薦：監控頁加第五個分頁，健康檢查卡「備份新鮮度」有「看備份」連結。代價：分頁變五個，手機寬度要左右捲分頁列。
  - 替代：系統管理選單另開一頁「備份」。代價：選單多一項；備份狀態和告警分在兩處。
  - 選錯的代價：極小，只是元件放哪裡。
- **Q6 bk1 附手動還原步驟，並在驗收時兩種資料庫各演練一次（推薦）。**
  背景：使用者 11-19 裁決還原留在階段 5，指的是產品內的還原畫面。
  例子：bk1 上線後到階段 5 之前，地端客戶的資料庫壞了，手上有備份卻不知道怎麼用；或者備份其實一直都打不開，到要用時才發現。
  - 推薦：`docs/deploy.md` 寫停站、換檔、複製附件、重填密碼的步驟，驗收時照做一次。代價：bk1 多半天工；手動步驟要客戶 IT 操作主機。
  - 替代：bk1 只做備份，還原步驟到階段 5 再寫。代價：階段 5 之前備份能不能用沒有人驗過。
  - 選錯的代價：小，只是文件與一次演練。
- **Q7 bk1 的備份檔不加密（推薦）。**
  例子：地端的備份放在 NAS，看得到 NAS 的人就讀得到備份裡的客戶資料。
  - 推薦：bk1 不加密；地端靠 NAS 或目錄的權限（產品把目錄設成只有站台帳號能讀），雲端靠儲存端的加密；上雲時再評估。代價：NAS 權限沒設好時，備份裡的資料是明文。
  - 替代：用一把客戶自己保管的密碼加密備份。代價：密碼要另外保管，弄丟了所有備份都打不開；還原步驟多一步；對沒有 IT 人員的小公司，弄丟密碼比 NAS 外洩更常見。
  - 選錯的代價：中。之後要加密時，舊備份是明文、新備份是密文，還原要能分辨兩種。
- **Q8 Demo 站開排程備份、停用「立即備份」、種子補一筆失敗紀錄（推薦）。**
  背景：Demo 站任何訪客都能一鍵以系統管理員登入（寄信設定在 Demo 站因此唯讀）；Demo 站沒有永久磁碟，休眠就清空。
  例子：訪客打開「備份」分頁，看到剛才自動跑完的一份成功、三天前一份因空間不足失敗的紀錄，「立即備份」按鈕灰掉並寫「Demo 站不開放立即備份」。
  - 推薦：如上。代價：訪客不能自己按一次立即備份看過程（本機走查會驗）。
  - 替代：Demo 站開放立即備份。代價：任何人可以一直按，雖然同時只跑一份、保留份數有上限，但每次都吃 Demo 站的 CPU 與暫存空間。
  - 替代 2：Demo 站不開備份，只顯示「未設定」。代價：公開 Demo 看不到成功的樣子。
  - 選錯的代價：極小，只是 Demo 的部署設定與一個停用條件。
- **Q9 備份執行中不能「清孤兒附件」（推薦）。**
  例子：系統管理員在凌晨 2 點 5 分（備份正在複製附件）按了「清孤兒附件」，看到「備份進行中，請稍後再清」。
  - 推薦：備份排隊中或執行中時擋下（409）。代價：大型系統第一次全量備份可能跑幾十分鐘，這段時間不能清。
  - 替代：不擋。代價：備份引用的附件可能剛好在複製前被清掉，那份備份還原後少檔案。
  - 選錯的代價：極小。

### 24.2 技術類（主控已定，可推翻）

- **T1** 備份不走排程器，另開 `BackupWorker` hosted service；排程器單次 10 分鐘上限與「一次一個工作」不適合長工作（§6.1）。
- **T2** 新表 `Dynamic_BackupRun`；`ActiveSlot` 可空欄＋唯一索引保證同時只一份，不用部分索引；一個 migration（§10）。
- **T3** SQLite 用 `VACUUM INTO`（不用 Online Backup API：一句 SQL、產出獨立檔、讀取交易不擋寫）；驗證用 `quick_check` 與 migration 比對；放 SQLite 專屬類別（§5.3、§5.6）。
- **T4** PostgreSQL 用 `pg_dump -Fc`，密碼走 `PGPASSWORD`、版本先比、`pg_restore --list` 驗證；客戶 `Dockerfile` 裝 PGDG `postgresql-client-16`；時間點還原與託管快照是雲端部署的事、產品不管（§5.4）。
- **T5** `IBackupStore`：只寫一次、可列出、可查存在、刪除看 `CanDelete`；bk1 只做本機目錄（§7.1）。
- **T6** 位置版面：`sets/` 每份一個目錄、`manifest.json` 最後寫、附件池與附件根目錄同版面、`.staging/` 改名搬入（§5.2）。
- **T7** 位置不能和資料庫、附件、金鑰目錄重疊；目錄 0700、檔案 0600（§7.4、§7.1）。
- **T8** 新鮮度的判斷表（§9.1），結束後立刻重跑檢查。
- **T9** 執行前預估空間（資料庫＋新附件，乘 1.1），不夠就不寫（§6.4）。
- **T10** 技術上限：醒來 60 秒、最長 6 小時、心跳 60 秒、5 分鐘判中斷、中斷重試 3 次、歷程 200 筆、摘要 1000 字（§19）。
- **T11** 多實例：插入撞唯一鍵略過、條件式領工、心跳落空中止（§6.3）。
- **T12** 稽核只加 `backup.request`、`backup.prune`；排程備份本身記在歷程表（§12）。
- **T13** 雲端版配本機目錄只提示、不擋；O6 相依雲端隔離儲存（§7.3）。
- **T14** 保留只在可刪除的儲存由產品執行、最新一份不刪、附件池依清單回收、改政策不立即刪（§8）。
- **T15** 附件只複製 `LastWriteUtc ≤ snapshotUtc` 的檔、未滿 2 分鐘先等、複製前後比對（§5.5）。
- **T16** 排程以營運時區日期為時段，中斷不占時段、失敗占時段（§6.2）。

## 25. 留給實作者

- 四語文案：分頁、狀態卡、步驟、錯誤代碼、健康訊息、兩條政策、兩個稽核動作、三個 409 訊息。
- `Dockerfile` 裝 PGDG 用戶端的確切寫法（金鑰與 apt 來源、映像大小增加多少寫進交付說明）。
- PostgreSQL 測試怎麼取得 `pg_dump`（容器內執行或測試機安裝），計畫時定。
- 進度顯示「附件 120／340」的總數怎麼先算（先列一次清單即可）。
- bk1a 若估出來太大，可把保留清理（§8）拆成 bk1a2，但 migration 只在 bk1a1。
