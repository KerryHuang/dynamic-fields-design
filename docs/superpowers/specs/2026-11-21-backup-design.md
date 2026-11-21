# 產品內備份 設計文件（段 bk1）

> 2026-11-21｜平台共用能力（非應用程式）｜前置：[維運與觀測](2026-11-11-ops-observability-design.md)（§6 護欄「備份隔離」、§8 備份做法、§17.2.4 `backup.freshness`、B20）、[PostgreSQL provider](2026-11-12-postgres-provider-design.md)（§2 正式部署另排）、[政策設定](2026-11-11-policy-settings-design.md)、[MCP server](2026-11-11-mcp-server-design.md)（mcpO1、mcpO2）
> 狀態：主控撰寫（2026-11-21），同日依同家審查修訂（M1–M9 與 13 條建議，見 §24 的 Q10–Q12、T17–T30）；Q1 已由使用者裁決。這次只寫設計、不改程式。依使用者 2026-11-21 裁決：會改變使用者操作的小題照推薦先定，寫成「主控暫定，待使用者確認」；技術類主控直接定。
> 標記：「（使用者裁決）」是使用者已拍板的；「（Q#）」是行為類，主控暫定、待使用者確認，彙整在 §24.1；「（T#）」是技術類，主控已定、可推翻，彙整在 §24.2。

## 1. 目的

Formwright 現在沒有任何備份。監控頁的「備份新鮮度」一律顯示「未設定」（`HealthProbes.cs` `BackupFreshnessProbe`），地端客戶要自己想辦法複製資料庫檔與附件目錄，複製時資料庫還在寫，拿到的檔案不一定能開。

bk1 讓產品自己做備份：

1. 每天在設定的時段自動備份資料庫與附件；系統管理員也可以按「立即備份」。
2. 備份寫進一個和資料分開的位置，經過一個通用的儲存介面；bk1 先做本機目錄（可以是另一顆磁碟或掛載的 NAS），雲端隔離儲存另成一段、排在 O4 之前（Q11）。
3. 「備份新鮮度」改看真實的最近一次成功備份；備份失敗或過期就照 O2 的事件與告警通道通知。
4. 給維運 O4–O6 一個前提：agent 處置以隔離備份為護欄（使用者 11-19 裁決）。bk1 做介面，雲端隔離儲存另成一段，要在 O4 開工前補上（§7.3、Q11）。

還原的產品畫面留在階段 5（使用者 11-19 裁決）。bk1 只附手動還原步驟並演練一次（Q6），因為沒還原過的備份不能算有備份。

## 2. 範圍

**範圍內**

- 備份執行器：排程（每天一次，時段可設，Q2）、立即備份、同一時間全系統只跑一份、程序中斷後標記與重試。
- 資料庫快照：SQLite 用 `VACUUM INTO`（T3）；PostgreSQL 呼叫 `pg_dump -Fc`（T4）。
- 附件：增量複製到備份位置的附件池，每份備份附一份清單（§5.5）。
- 驗證：寫完重新讀一次備份確認能開、版本對得上（§5.6）。
- 儲存介面 `IBackupStore` 與本機目錄實作；位置由部署設定 `Backup:Root` 決定（Q3）；位置標記檔與多實例一致性檢查（§7.4）。
- 備份與「清孤兒附件」互斥（§5.5）。
- 保留份數政策與清理（只在儲存位置允許刪除時由產品清，§8）。
- `backup.freshness` 接上真實時間，並兼管「上次備份失敗」（Q4）。
- 監控頁新分頁「備份」：狀態、立即備份、歷程（Q5）。
- 稽核、手冊、部署文件（環境變數、手動還原步驟）、Demo 種子。
- 客戶部署用的 `Dockerfile` 裝 PostgreSQL 用戶端工具（T4）。

**範圍外**

- 產品內還原畫面、還原到某個時間點（階段 5，使用者 11-19 裁決）。
- 雲端隔離儲存的實作（S3 相容物件儲存、只寫不刪的憑證、物件鎖定）：要另開雲端帳號，另成一段、排在 bk1 之後、O4 之前（Q11；domain-advisor 備忘）；bk1 只定介面與它要滿足的契約（§7.2）。
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

`manifest.json`（格式版本 1）：`formatVersion`、`runKey`、`trigger`（`schedule`／`manual`，保留清理依它分開算，§8）、`locationId`、`snapshotUtc`、`appVersion`、`commit`、`edition`、`provider`、`migrationId`（最新一筆已套用的 migration）、`database`（檔名、位元組、SHA-256）、`attachments`（個數、總位元組、清單檔名與它的 SHA-256）、`includesKeys: false`。

`attachments.txt` 每行一個「id、位元組數」，依 id 排序。

### 5.2 儲存位置的版面（T6）

```
<Backup:Root>/
  .formwright-backup-location           位置標記檔（§7.4），內容是位置識別碼
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

- 呼叫外部程式 `pg_dump --format=custom --no-owner --no-privileges --file=<staging>/database.pgdump`。用 `ProcessStartInfo.ArgumentList` 逐項傳參數，不經 shell。
- 連線參數一律走環境變數：`PGHOST`、`PGPORT`、`PGDATABASE`、`PGUSER`、`PGSSLMODE`，命令列不帶任何連線資訊。
- **密碼寫進 0600 的暫存 `PGPASSFILE`**，子程序結束（含逾時、被砍）就刪。不用 `PGPASSWORD`：PostgreSQL 官方文件不建議，部分系統的 `ps` 看得到環境變數。密碼不出現在命令列、環境變數、log、錯誤訊息；錯誤訊息先遮掉密碼，再截斷到 1000 字。
- Npgsql `SSL Mode` 對到 libpq `sslmode`：`Disable`、`Allow`、`Prefer`、`Require`、`VerifyCA`、`VerifyFull` 用同名值。`Trust Server Certificate=true` 只能搭配 `Require`，對到 libpq 的 `require` 並且不給根憑證，所以不驗憑證；其他組合對不上時回 `pgDumpConfig`，訊息寫出是哪個設定。雲端託管資料庫常用這種組合，所以要明寫。
- 程式路徑是部署設定 `Backup:PgDumpPath`，沒設就找 `PATH` 上的 `pg_dump`；找不到回 `pgDumpMissing`。
- 站台關閉與逾時時，用 `Kill(entireProcessTree: true)` 結束子程序。理由：殘留的 `pg_dump` 會一直持有鎖，下一次啟動的 migration 會卡在 `lock_timeout`，站台起不來。
- 開始前比對版本：`pg_dump --version` 的主版本小於伺服器 `server_version_num` 的主版本時回 `pgDumpVersion`，訊息帶兩個版本。
- `pg_dump` 自己用一致的快照，不擋一般寫入，但會擋住啟動時的 migration（DDL 要等它）。
  - 排程備份：避開備份時段部署即可。
  - 立即備份：任何時刻都可能發生。部署時若有立即備份在跑，新版要等它結束才啟動得了。
  - `deploy.md` 寫明：部署前先看監控頁「備份」分頁有沒有執行中的備份（§16）。
- 客戶用 `Dockerfile` 加裝 PGDG 的 `postgresql-client-16`（和測試容器同主版本；之後支援更新的伺服器時換成新版用戶端，新版用戶端能備舊版伺服器）。`Dockerfile.demo` 是 SQLite，不裝。
- 程式放 `Persistence.Postgres` 的 `PostgresDatabaseBackup`（交付說明列出）。
- 雲端版另用託管快照或 WAL 歸檔做時間點還原：部署層的事，產品不感知、不管（範圍外）。

### 5.5 附件（T15）

**`SnapshotUtc` 的定義**：快照指令完成之後才取時間。SQLite 是 `VACUUM INTO` 返回之後，PostgreSQL 是 `pg_dump` 結束之後。

- 理由：上傳是先寫檔、後提交記錄（`AttachmentHandlers.cs:133-147`），所以快照裡引用到的附件，檔案一定在快照的一致點之前就寫完了。
- 時間取晚一點，「距上次成功」會略為偏樂觀（差的是快照本身的耗時），可以忽略。

**複製哪些檔**：

- 在 `SnapshotUtc` 之後列一次附件根目錄，清單裡的檔全部複製，**不用 `LastWriteUtc` 做上限過濾**。多備一個快照之後才上傳的檔沒有壞處，少備才有。附件根目錄在 NAS 上時，檔案時間是 NAS 的時鐘，拿它和應用程式的時鐘比上限會漏檔。
- 「最後寫入距現在未滿 2 分鐘」的檔（含時間落在未來的，即 NAS 時鐘偏差），先等它滿 2 分鐘，最多等 5 分鐘。複製前後比對大小與 `LastWriteUtc`，不一致就略過，記進統計 `SkippedAttachments`。
  - 略過的檔不會是快照引用的檔。快照引用的檔在快照前就寫完，而附件只寫一次、之後不再變動。會變動的只有還在上傳中的檔，它的記錄還沒提交，不在快照裡。
- 附件池已有同 id 的檔案就不複製（附件只寫一次、不覆寫，所以 id 相同，內容就相同）。

**和「清孤兒附件」雙向互斥**（T30；審查 M4）：

清孤兒是唯一會刪掉「曾被引用」的附件的地方。它先讀完全部記錄，再逐檔刪除（`AttachmentHandlers.cs:203-236`）。寬限時間只看檔案自己的寫入時間，不看引用是什麼時候消失的。所以只擋一邊不夠：清孤兒先開始、備份後排進來，一樣會在複製前刪掉快照引用的檔。

- 兩邊搶同一把全系統的維運鎖 `attachments`（`Dynamic_OpsLock`，§10.3，條件式取得，做法同排程器租約）。
- 清孤兒：開始時搶鎖，搶不到（備份持有）就回 409 `backupInProgress`；拿到之後每 60 秒續鎖，結束或失敗時放鎖。鎖最長 30 分鐘（技術限制），請求異常中止時最多卡 30 分鐘。
- 備份：領工後、快照前搶鎖，持有到附件複製與驗證結束，由心跳續鎖（§6.3）。搶不到（清孤兒持有）就停在 `Stage = waiting`，每輪再試。等待時間算進單次 6 小時的上限。
- 快照之後才上傳的附件，可能在這份備份裡，也可能不在（看列目錄的時點）；下一份一定有。

### 5.6 驗證（T3、T4）

寫完、搬進 `sets/` 之前：

- SQLite：唯讀開啟備份檔，`PRAGMA quick_check` 要回 `ok`，`__EFMigrationsHistory` 最新一筆要等於 `manifest.migrationId`（provider 專屬類別內）。
- PostgreSQL：`pg_restore --list` 成功，而且列得出資料表。
- 兩者都重新計算資料庫檔與 `attachments.txt` 的 SHA-256，和寫入時算的一致。
- 附件：清單裡每個 id 在附件池都存在、大小相符（只比大小，不逐檔重算雜湊；第一次寫入池時已算過）。
- 驗證失敗：狀態 `failed`、代碼 `verifyFailed`，暫存刪掉，不進 `sets/`。

### 5.7 金鑰（Q1，使用者 2026-11-21 裁決：不帶）

金鑰目錄不隨資料備份。理由：金鑰和資料庫放在一起，拿到備份的人可以解開寄信密碼，還能偽造任何使用者的登入 cookie 登入正式站（金鑰沒換之前都有效）。業界慣例是金鑰與資料分開存放。

不帶金鑰還原（換主機、金鑰目錄也沒了）的代價：

- 所有人要重新登入。
- 系統管理員要重填寄信密碼（畫面已有提示，§3）。
- 未完成的任務復原連結失效。
- MCP 權杖只存雜湊，不受影響。
- **au1 上線後，所有人的雙因素（TOTP）密鑰都會失效。** 理由：TOTP 密鑰用 Data Protection 加密（au1 已定決定 22），新金鑰解不開。
  - 一般使用者用復原碼登入，再重新綁定，或請系統管理員在使用者管理「重設雙因素」（au1-8）。
  - 唯一的系統管理員手上沒有復原碼時，走部署端救援 `Auth__ResetAdminPassword`，它會一併清掉雙因素（au1-漏2）。
  - 前提：復原碼存的是雜湊、不靠金鑰。au1 設計時要確認這一點，否則復原碼也會一起失效。

原本的金鑰目錄還在時（同一台主機只有資料庫壞了），保留它就沒有以上代價。手冊與還原步驟（§9.3）寫明這些事。

## 6. 執行：排程、立即備份、多實例（T1）

### 6.1 不走排程器，另開備份執行器

排程器單次最長 10 分鐘，而且一個實例一次只跑一個工作（§3）。大一點的資料庫加上第一次的附件全量複製會超過 10 分鐘，還會讓 `ops.monitor`（告警信、排程型健康檢查）等著。所以備份另開一個 hosted service `BackupWorker`（每個實例一個，和看門狗同一種做法）：

- 每 60 秒醒來一次（技術限制）：先看要不要排一份（§6.2），再領一份排隊中的來跑。
- `Ops:Scheduler:Enabled=false` 的實例也不跑備份執行器（同一個意思：這台不做背景工作）。
- 單次最長 6 小時（技術限制），逾時 `failed`／`timeout`；站台關閉時中止，狀態 `interrupted`。

### 6.2 什麼時候排一份（Q2）

- **排程**：營運時區的現在時刻已過今天的 `ops.backupHour` 整點，而且今天這個時段（`ScheduleSlot`＝營運時區的日期）還沒有排隊中、執行中、成功或失敗的紀錄，就排一份。所以站台在那個時段沒開著（例如休眠），醒來後會補一次，當天不再排。
- **第一次**：目前位置（§7.4 的位置識別碼）上還沒有任何成功或失敗的排程紀錄時，立刻排一份，不等時段。這一份就是今天這個時段的排程備份：`Trigger = schedule`、`ScheduleSlot = 今天`，和一般排程一樣受「同時段最多中斷 3 次」約束，所以不會變成重啟循環。這一份失敗的話，今天的時段就算用掉，不會在 02:00 再排一次。
- **立即備份**：系統管理員按鈕（§11），`Trigger = manual`、`ScheduleSlot = null`。立即備份不算「第一次」，也不占時段。
- **中斷重試**：`interrupted` 不算占用時段，下一輪會再排；同一時段最多中斷 3 次（技術限制），之後當天不再自動排。新鮮度會依 §9.1 告警，從沒成功過的情況也包括在內。
- 失敗（`failed`）不自動重試：同一個原因（空間不足、權限錯）重試也會失敗，交給告警與管理員處理。
- 上面所有判斷都只看目前位置識別碼的紀錄（§7.4）。

改營運時區或備份時段的當天，可能少排一次或多排一次（已知限制，§16）。

### 6.3 同一時間只有一份（T11）

- 排隊中、執行中的紀錄 `ActiveSlot = 1`，其他狀態是 null；`ActiveSlot` 有唯一索引。兩台同時要排時，後插入的撞唯一鍵就略過（`IsUniqueViolation`）。
- 領工：條件式更新 `Status: queued → running`、填 `OwnerInstanceId`、`HeartbeatUtc`，影響一列才算領到。
- 心跳：每 60 秒以 `OwnerInstanceId` 為條件更新 `HeartbeatUtc`，順便續 `attachments` 鎖（§5.5）；更新落空（被別台判定中斷）就中止。
  - **心跳是獨立的計時工作**，和備份步驟並行，不夾在步驟之間。`VACUUM INTO` 和 `pg_dump` 都是一次長時間的呼叫，大型資料庫可能跑幾十分鐘，心跳放在步驟之間會被誤判成中斷。
- 心跳超過 5 分鐘沒更新（技術限制），任一實例的執行器在下一輪把它條件式改成 `interrupted`、`ActiveSlot = null`，並清掉它的暫存（下一次開始時也會清）。
- 領工、心跳、判定中斷、放鎖、歷程修剪都是交易外的 `ExecuteUpdate`／`ExecuteDelete`，不經 PostgreSQL 的全庫寫入鎖，寫法參考 `ScheduledJobRepository.cs:17-31` 的租約，交付說明要列出（`.claude/rules/backend.md`）。

### 6.4 一次執行的步驟

1. 檢查位置與標記檔（§7.4）與剩餘空間：預估需要＝資料庫大小＋還不在附件池的附件總大小，乘 1.1；目的地剩餘不足就 `destinationFull`，什麼都不寫。接著搶 `attachments` 鎖（§5.5），搶不到就 `Stage = waiting`。
2. 資料庫快照（§5.3、§5.4）；指令完成後記下 `SnapshotUtc`、`AppVersion`、`MigrationId`（§5.5）。
3. 附件（§5.5）。
4. 驗證（§5.6），寫 `manifest.json`，搬進 `sets/`。
5. 狀態 `succeeded`。
6. 保留清理（§8）。清理失敗不改變這份備份的成功，只記 Warning 與 `PruneError`。
7. 不論成敗，立刻重跑一次 `backup.freshness`（不受「立即檢查」60 秒限制），事件馬上開或結案（§9）。
   - `HealthCheckEngine.TryRunNowAsync` 在同一檢查正在跑時回 null（`HealthCheckEngine.cs:78-79`）。這時每 5 秒再試，最多試 60 秒；還是不行就交給下一輪 `ops.monitor`，最多晚一個檢查間隔。

放鎖在第 4 步之後（附件與驗證都結束）。`Stage` 欄記目前在哪一步（`waiting`、`database`、`attachments`、`verify`、`prune`），畫面依它顯示進度。

## 7. 儲存介面與隔離

### 7.1 介面（T5）

Application 定 `IBackupStore`：

- `Kind`（`local`；之後的雲端實作用自己的代碼）、`DisplayLocation`（給畫面看的位置，雲端實作不含憑證）、`Isolated`（是否為隔離儲存）、`CanDelete`。
- `ExistsAsync(path)`、`PutAsync(path, stream)`（**只寫一次**：目標已存在就丟例外，不覆寫）、`ListAsync(prefix)`、`DeleteAsync(path)`（`CanDelete` 為 false 時一律丟例外）。
- 本機目錄實作 `LocalDirectoryBackupStore`：`Isolated = false`、`CanDelete = true`。
  - 非 Windows 時，目錄設 0700、檔案設 0600（T7）。這是盡力而為：CIFS／NFS 掛載點上 chmod 可能失敗或不生效，失敗只記一次 Warning，不讓備份失敗。手冊建議在 NAS 端設權限。
- 資料庫快照寫在本機暫存目錄；本機目錄實作的暫存就在 `<Backup:Root>/.staging/`，之後用改名搬進去；雲端實作改成寫完上傳（`Backup:StagingDirectory`，上雲時再加）。

### 7.2 雲端隔離儲存要滿足的契約（上雲時實作）

- 獨立帳號（不和產品主機、維運中心、agent 共用），產品持有的憑證只能新增物件、列出、讀取存在與否，**不能刪除、不能覆寫**；保留期由儲存端的物件鎖定或生命週期規則負責。
- 憑證只存在產品主機的部署設定，不進資料庫、不進稽核、不給任何 MCP 工具。
- `Isolated = true`、`CanDelete = false`。
- 供應商、物件鎖定模式、保留天數，上雲時另定（domain-advisor 備忘）。

### 7.3 給維運 O4–O6 的前提（Q11）

維運設計 §6 要求 agent 與維運中心碰不到備份；使用者 11-19 裁決「O4–O6 的 agent 處置以隔離備份為護欄」。bk1 期間只有本機目錄（`Isolated = false`），所以：

- `Ops:Edition = Cloud` 而儲存不是隔離的，啟動記 Warning，備份分頁顯示「雲端版的備份應放在隔離儲存（尚未支援），目前存在本機目錄」（T13）。
- **O4 開工前，雲端隔離儲存要先上線**（照 11-19 裁決的字面；Q11 推薦）。雲端隔離儲存是 bk1 之後、O4 之前的獨立一段，路線文件的 O4 列加這個相依（主控改）。

### 7.4 位置檢查（T7）

`Backup:Root` 沒設：不備份，新鮮度「未設定」。有設時（相對路徑以站台目錄解析）：

- 以下位置一律不收：啟動時記 Warning，備份不跑，新鮮度判嚴重 `locationInvalid`。
  - 和資料庫目錄、附件根目錄、金鑰目錄相同、在它們底下，或包住它們。理由：備份和資料在同一棵目錄裡，誤刪資料夾時會一起沒；附件池放在附件根目錄底下，還會被當成附件。
  - 在站台目錄的 `wwwroot` 底下，或包住 `wwwroot`。`wwwroot` 存在時站台會開 `UseStaticFiles`（`Program.cs:231-237`），`manifest.json`、`attachments.txt` 是已知的 MIME 類型，會被公開提供。相對路徑以站台目錄解析，很容易誤設到這裡。
- 路徑比對一律不分大小寫：Windows 和 macOS 的預設檔案系統都不分大小寫，寧可多拒。
- 目錄不存在就建立；建不起來或寫不進去，該次判 `destinationUnwritable`。
- 只比對路徑，不判斷是不是同一顆磁碟（容器裡量不準，同 Demo 站的磁碟檢查）。手冊建議放另一顆磁碟或 NAS。

**位置標記檔與多實例**（審查 M5、M7）：

- 位置根目錄放一個標記檔 `.formwright-backup-location`，內容是位置識別碼（GUID）。資料庫單列表 `Dynamic_BackupState` 記「目前採用的位置識別碼」（§10.2）。
- 執行器每輪開始時讀標記檔：
  - **沒有標記檔**：建一個新的，並把它設為目前採用的位置（後寫的覆蓋先寫的），記 Warning「備份位置換新」。換到一個新的空目錄就屬於這種，之後會照 §6.2 的「第一次」立刻排一份。
  - **有標記檔，和目前採用的相同**：照常。
  - **有標記檔，但和目前採用的不同**：這台不跑備份，新鮮度判嚴重，訊息代碼 `locationMismatch`。
- 多實例時，`Backup:Root` 必須是所有實例共用的掛載點，或只讓一台開背景工作（`Ops:Scheduler:Enabled=false`，`Program.cs:91` 已經是「多實例只讓一台跑」的用法），兩者擇一，寫進 `deploy.md` 與 §16。
  - 兩台各自指向本機目錄時：後建標記的那台成為目前採用的位置，另一台看到不一致就停，監控頁顯示 `locationMismatch`。不會兩台輪流覆蓋，因為已有標記檔的那台不會再覆蓋。
- 換回以前用過的位置（裡面留著舊標記檔）會判不一致：要刪掉舊的標記檔，`deploy.md` 寫明。
- 新鮮度、`lastSuccess`、「第一次」、保留清理都只看目前位置識別碼的紀錄。舊位置的紀錄留在歷程裡，標「舊位置」。

## 8. 保留與清理（T14）

**以儲存位置為準，資料表只當歷程**（審查 M5）：

保留清理用 `IBackupStore.ListAsync("sets/")` 列出各 set，並讀每份的 `manifest.json`（`trigger`、`snapshotUtc`、`locationId`），不看 `Dynamic_BackupRun`。

- 理由：歷程表只留 200 筆，保留份數最多 90；兩者錯開時，看資料表會漏掉 set，留下永遠清不掉的孤兒 set。
- 沒有 `manifest.json` 的目錄不是完整的 set（§5.2），清理不碰它；`.staging/` 照 §5.2 清。

**排程備份和立即備份分開算**（Q10；審查 M6）：

- 政策 `ops.backupRetentionCount`（出廠 7）只算排程備份：依快照時間保留最新 N 份，多的從最舊的刪。
- 立即備份不占這 N 份。比它新的排程備份成功之後，它就可以刪。
- 不論哪一種，最新一份成功的永遠不刪。失敗與中斷的不會進 `sets/`，不算份數。

**其他規則**：

- 刪完 set 後清附件池：池裡的檔案不在任何保留中 set 的 `attachments.txt` 裡，而且寫入時間早於最新一份的快照時間，才刪。
- 改小保留份數不會立刻刪，下一次成功備份後才照新值刪。這是照 `.claude/rules/backend.md`「政策調嚴不溯及既有資料」的精神：刪除只在備份時發生。
- 儲存位置不能刪（`CanDelete = false`，雲端隔離儲存）時產品完全不刪，保留由儲存端負責；政策頁這一項的說明寫明「只對可刪除的儲存位置有效」。
- 每刪一份記一筆稽核 `backup.prune`（行為者是系統、來源管道是排程工作）。
- 歷程表修剪：保留最新 200 筆（技術限制），再加上 `SetName` 仍在保留中的列，由執行器在每次結束時順手清。所以畫面上每份還在的備份都查得到它的歷程。

## 9. 備份新鮮度接上真實時間（Q4、T8）

### 9.1 判斷

| 情況 | 狀態 | 數值 | 訊息代碼 |
|---|---|---|---|
| `Backup:Root` 沒設 | 未設定 | — | — |
| PostgreSQL、bk1b 還沒合併 | 未設定 | — | `providerUnsupported` |
| 位置不合法（§7.4） | 嚴重 | — | `locationInvalid` |
| 標記檔和目前採用的位置不同（§7.4） | 嚴重 | — | `locationMismatch` |
| 從沒成功過、也沒失敗過，等待未滿警告門檻 | 等待中（不開事件） | 從目前位置第一筆 `QueuedUtc` 起算的時數 | `firstBackupPending` |
| 從沒成功過、也沒失敗過，等待已達警告門檻 | 依門檻（警告或嚴重） | 同上 | `firstBackupPending` |
| 從沒成功過、已失敗過 | 嚴重 | — | `neverSucceeded` |
| 從沒成功過、同一時段已中斷達上限（3 次） | 嚴重 | — | `neverSucceeded` |
| 有成功 | 依門檻（出廠 26／50 時） | 距最近一次成功的快照時間，取整數時 | — |
| 有成功，但之後最近一次結束的是失敗 | 上一列與「警告」取較嚴重者 | 同上 | `lastRunFailed` |

- 「從沒成功過」「上次成功」都只看目前位置識別碼的紀錄（§7.4）。換到新位置後，不會沿用舊位置的成功。
- 中斷不算失敗：中斷會自動重試（§6.2），時間拖過門檻才警告；從沒成功過而中斷達上限，直接判嚴重。
- 門檻、間隔、開關沿用 `HealthCheckCatalog` 的登記與 `Dynamic_HealthCheck` 的設定，不另寫一份。
- 每次備份結束都立刻重跑一次這項檢查（§6.4 第 7 步），所以失敗不必等 60 分鐘才告警，成功後事件也立刻結案。
- 用主機自備的備份、不用產品備份的客戶：不設 `Backup:Root`，狀態就是「未設定」、不開事件；要把這張卡藏起來就關掉這項檢查（既有功能）。

### 9.2 告警

完全走 O2：類別基礎設施、指紋 `infra`＋`backup.freshness`、站內通知 `ops.alertRecipients`、寄信依 `ops.alertEmailCategories` 與最低嚴重度。bk1 不加新通道、不加新收件人政策。

### 9.3 手動還原步驟（Q6，寫進 `docs/deploy.md`）

SQLite：

1. 停站台。
2. 選一份 `sets/<名稱>/`，確認它不比要啟動的版本新：舊備份可以用新版啟動，migration 會往前升；新備份不能用舊版開。
   - 比對 `manifest.json` 的 `appVersion`。
   - 遇到 `dev`（本機與 Demo 的版本一律是 `dev`，`IOpsEnvironment.DevVersion`）時改比 `migrationId`：備份的 `migrationId` 必須在要啟動的版本的 migration 清單裡。
3. 把 `database.sqlite` 複製成資料庫檔（例如 `/app/data/poc.db`），刪掉舊的 `-wal`、`-shm`。
4. 把 `<Backup:Root>/attachments/` 整個複製到附件根目錄。多出來的檔案是別份備份的，之後可以用「清孤兒附件」清掉。
5. 啟動。
   - 原本的金鑰目錄還在（只是資料庫壞了、同一台主機還原）：保留金鑰目錄，就不必重新登入，也不必重填密碼。
   - 金鑰目錄也沒了（換主機、整顆磁碟壞）：所有人要重新登入，系統管理員要到「寄信設定」重填密碼（Q1）。
   - au1 上線後，同一種情況下雙因素也全部失效（§5.7）：
     - 使用者用復原碼登入後重新綁定，或由系統管理員重設雙因素。
     - 唯一的系統管理員沒有復原碼時，設 `Auth__ResetAdminPassword` 重啟，登入後重新綁定，完成後移除這個設定。
     - au1 合併時，這一步要補進 `deploy.md` 的還原步驟與手冊。

PostgreSQL：第 3 步改成建一個空資料庫，`pg_restore --no-owner --dbname=<新資料庫> database.pgdump`，再把連線字串指過去。

bk1 驗收時兩種資料庫各演練一次（§22 D1、D2）。

## 10. 資料模型與 migration（T2）

### 10.1 `Dynamic_BackupRun`

| 欄位 | 型別 | 說明 |
|---|---|---|
| `Id` | long | 主鍵 |
| `RunKey` | string(32) | GUID 十六進位，唯一；set 名稱、暫存目錄用它 |
| `Trigger` | string(16) | `schedule`（含「第一次」）、`manual` |
| `ScheduleSlot` | string(10)? | 排程的營運時區日期 `yyyy-MM-dd`；「第一次」是當天；立即備份是 null；索引 |
| `LocationId` | string(32) | 排隊時的目前位置識別碼（§7.4）；索引 |
| `Status` | string(16) | `queued`、`running`、`succeeded`、`failed`、`interrupted` |
| `ActiveSlot` | int? | 排隊中、執行中是 1，其他 null；唯一索引（§6.3） |
| `Stage` | string(16)? | `waiting`、`database`、`attachments`、`verify`、`prune` |
| `RequestedUserId` | string(100)? | 立即備份的人 |
| `QueuedUtc`、`StartedUtc`、`HeartbeatUtc`、`SnapshotUtc`、`FinishedUtc` | DateTime（UTC） | 後四個可空 |
| `OwnerInstanceId` | string(100)? | 執行的實例（`OpsInstance` 的 id 最長約 93 字，和排程器 `LeaseOwner` 的 100 一致） |
| `StoreKind` | string(32) | `local` |
| `SetName` | string(64)? | 成功才有 |
| `Provider` | string(16) | 排隊時填 |
| `AppVersion`、`MigrationId` | string(64)?、string(150)? | 快照完成時才填（排隊中的列可能被部署後的新版實例領走） |
| `DatabaseBytes`、`AttachmentCount`、`NewAttachmentCount`、`NewAttachmentBytes`、`TotalAttachmentBytes`、`SkippedAttachments` | long? | 統計 |
| `ErrorCode` | string(32)? | §10.2 |
| `ErrorDetail` | string(1000)? | 摘要，已遮密碼、不含記錄值 |
| `PruneError` | string(1000)? | 保留清理失敗的摘要 |
| `PrunedSets` | int? | 這次刪了幾份 |

- 索引：`RunKey` 唯一、`ActiveSlot` 唯一（兩種資料庫的唯一索引都允許多個 NULL，不用部分索引）、`(LocationId, Status, FinishedUtc)`、`ScheduleSlot`。
- 時間欄位以 `Utc` 結尾、`Kind` 明確（PostgreSQL `timestamptz`）。

### 10.2 `Dynamic_BackupState`（單列）

主鍵是字串 `Kind`，固定為 `default`，寫法沿用 `MailSettings`。欄位：

- `LocationId` string(32)?：目前採用的位置識別碼。
- `AdoptedUtc`。
- `AdoptedByInstanceId` string(100)?。

### 10.3 `Dynamic_OpsLock`

全系統的維運鎖，bk1 只用 `attachments` 這一把（§5.5）。欄位：

- `Key` string(32)：主鍵。
- `Owner` string(100)?：持有者。
- `UntilUtc`：到期時間。

取得、續鎖、放鎖都是條件式更新，做法同排程器租約。啟動時補 `attachments` 這一列（插入，撞唯一鍵就略過，同 `Dynamic_HealthCheck`）。

### 10.4 migration

一個 migration `BackupRuns`，三張新表一起，用 `scripts/db/add-migration.sh` 產 SQLite 與 PostgreSQL 各一份，由 `MigrationConsistencyTests` 把關。

- 都是新表，沒有既有資料，不需要預設值或回填。
- Down 就是刪表。

### 10.5 錯誤代碼

`destinationUnwritable`、`destinationFull`、`databaseSnapshotFailed`、`pgDumpMissing`、`pgDumpVersion`、`pgDumpConfig`、`attachmentCopyFailed`、`verifyFailed`、`timeout`、`interrupted`、`locationInvalid`、`locationMismatch`。前端以 `ops.backup.error.<代碼>` 顯示，不認得的代碼原樣顯示。

## 11. API

都在 `/api/v3/dynamic/system/ops/`，全部 `ISystemAdminRequest`。

| 方法 | 路徑 | 說明 |
|---|---|---|
| GET | `backups` | 狀態與最近 20 筆歷程（`cursor` 往下翻）：`configured`、`locationValid`、`storeKind`、`displayLocation`、`isolated`、`edition`、`canDelete`、`provider`、`includesKeys`（false）、`schedule`（`hour`、營運時區偏移、`nextScheduledUtc`）、`retentionCount`、`lastSuccess`、`current`（排隊中或執行中的那筆）、`runs`、`nextCursor`、`manualAllowed`、`manualDisabledReason` |
| GET | `backups/{id}` | 單筆詳細（含錯誤摘要、各項統計、set 名稱） |
| POST | `backups` | 立即備份：成功 202 回那一筆；已有排隊或執行中 409 `backupInProgress`；位置沒設、不合法或不一致 409 `backupNotConfigured`；Demo 站 403（Q8） |

- Demo 站的判斷依據是 `AuthSettings.DemoLoginEnabled`，回 `ServiceResult.Forbidden()`，和寄信設定、測試信同一種寫法（`AlertChannelHandlers.cs:59`、`:131`）。`GET backups` 的 `manualAllowed: false`、`manualDisabledReason: "demoSite"` 讓前端先把按鈕停用。
- `displayLocation`：雲端版（`Ops:Edition = Cloud`）只回儲存種類，不回路徑，避免客戶管理員看到廠商主機的路徑（Q12）；地端版回路徑。
- 沒有刪除、下載、改位置的 API（§19）。
- 既有的 `POST attachments/sweep`：備份持有 `attachments` 鎖時回 409 `backupInProgress`（T30、§5.5）。
- 新訊息進 `MessageText` 四語表（`backupInProgress`、`backupNotConfigured`）；超過上限類的訊息不適用。

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
| `ops.backupHour` | 每天幾點開始排程備份（營運時區，0–23 整點） | Integer，單位 `hourOfDay`（新單位） | 2 | 0–23 |
| `ops.backupRetentionCount` | 保留最近幾份成功的備份（只對可刪除的儲存位置有效） | Integer，單位 `count` | 7 | 1–90 |

- 前端政策頁從 API 讀定義與上下限，不另寫一份；備份分頁顯示的時段與份數也來自 `GET backups`。
- `hourOfDay` 是新單位：既有的 `ops.unit` 只有 `hours`、`count`，沿用 `hours` 會顯示成「2 小時」。政策頁要新增這個單位的顯示，值 2 顯示「02:00」（§14.4）。
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

- **狀態卡**：備份位置（`displayLocation`；雲端版只有種類，Q12）、「隔離儲存：是／否」、排程「每天 02:00（+08:00）」、下次排定、保留「最近 7 份」、上次成功（時間、大小、附件數）、「不含加密金鑰：換主機還原後要重新登入並重填寄信密碼」一行說明（Q1）；au1 上線後這行補「雙因素要用復原碼重綁」。
- **位置沒設**：卡片改成說明「還沒有設定備份位置。請由主機管理者設定部署設定 `Backup:Root`（見部署文件）」，沒有立即備份按鈕。
- **位置不合法**：紅色提示「備份位置不能和資料庫、附件、金鑰或網站檔案目錄重疊」。
- **位置不一致**：紅色提示「這台伺服器的備份位置和其他伺服器不同（或換回了舊位置），這台不會備份」，並連到部署文件。
- **雲端版、非隔離**：黃色提示（§7.3）。
- **立即備份**按鈕：排隊或執行中時停用並顯示目前步驟（「等待清孤兒附件結束」「資料庫」「附件 120／340」「驗證」「清理舊備份」）；每 5 秒重新讀取直到結束。Demo 站停用並寫原因（Q8）。
- **歷程**：時間、觸發（排程／立即，立即的帶操作者）、狀態、資料庫大小、附件（新增／總數）、耗時、錯誤；舊位置的列標「舊位置」；點一列開詳細。
- 時間照營運時區顯示並標時區（同監控頁其他分頁）。

### 14.2 健康檢查卡

`backup.freshness` 的卡片顯示「N 小時前」、訊息代碼的文字（`ops.health.message.lastRunFailed` 等），加一個「看備份」連到 `?tab=backup`。

### 14.3 四種寬度

- 390：分頁列可左右捲（`q-tabs` 已開 `mobile-arrows`）；狀態卡一欄；歷程用卡片列表（`q-table` grid 模式，同稽核頁）；詳細用全螢幕對話框。
- 820：狀態卡兩欄；歷程表隱藏「耗時」「觸發者」欄。
- 1366、1920：全部欄位；詳細用右側滑出。
- 任何寬度不得橫向捲動；長路徑用 `word-break: break-all`。

### 14.4 i18n

`shared/i18n/<code>/ui.json` 四語要加的鍵分兩段：

- **bk1a 加**：
  - `ops.health.message.{lastRunFailed,neverSucceeded,locationInvalid,locationMismatch,firstBackupPending,providerUnsupported}`。理由：bk1a 合併後健康卡就會出現這些代碼，`HealthCheckCard.vue:22` 的 `labelOr` 找不到鍵時會顯示原始代碼。
  - 兩條政策的名稱與說明。
  - 單位 `ops.unit.hourOfDay`（§13.1）與政策頁的時刻顯示。
  - 兩個稽核動作名稱。
- **bk1c 加**：`ops.tabs.backup`，以及 `ops.backup.*`（狀態卡、步驟、錯誤代碼、提示）。

`ui.json` 由 bk1a、bk1c 依序修改，不衝突。

## 15. Demo 種子（Q8）

| 示範 | 怎麼做 |
|---|---|
| 成功路徑 | `Dockerfile.demo` 設 `Backup__Root=/app/backups`；站台每次喚醒時資料回到映像狀態、沒有任何備份紀錄，不論喚醒時刻，執行器都立刻跑「第一次」（§6.2），1 分鐘內就有一筆成功 |
| 被擋 | 「立即備份」停用並寫「Demo 站不開放立即備份」，API 回 403（比照寄信設定的測試信，§3） |
| 失敗分支 | 公開 Demo 不示範；本機走查用故障注入示範：把 `Backup__Root` 設成唯讀目錄 → `destinationUnwritable`、監控頁嚴重 `neverSucceeded`；改回可寫後按立即備份 → 成功、事件結案 |
| 清孤兒附件被擋 | 本機走查：備份執行中按「清孤兒附件」→ 409 |
| 未設定 | 公開 Demo 不示範；本機走查不設 `Backup__Root` |

- **不種備份歷程**（審查 M1）。原因有兩個：
  - 種一筆失敗紀錄，「第一次」就不成立。喚醒時刻在台北 00:00–02:00 時要等到 02:00 才備份，這段期間還會判成 `neverSucceeded`，每次喚醒都開一個嚴重事件。
  - Demo 資料產生器在客戶站台也能從「系統管理 → Demo 資料」按（`docs/manual.md` §10），種歷程等於在客戶的維運紀錄裡插一筆捏造的失敗。
- Demo 資料產生器不寫任何備份資料。
- Demo 保留份數用政策出廠值 7；Demo 資料庫只有幾 MB，喚醒後只會有一兩份，容器重啟就清空。
- `scripts/unattended/local-demo.sh` 加 `Backup__Root=<worktree>/backend/data/local-e2e-backups`（啟動前清空），用來走查成功路徑、立即備份與上面的故障注入。
- 種子不動，所以不必 grep 既有的種子斷言。

## 16. 開關組合下誰負責

| 情況 | 誰備份 | 監控頁 |
|---|---|---|
| 地端 SQLite、設了 `Backup:Root`（另一顆磁碟或 NAS） | 產品每天備份；異地保存（把 NAS 再複製出去）是客戶 IT 的事 | 新鮮度正常；失敗或過期照 O2 告警 |
| 地端、沒設 `Backup:Root` | 沒有產品備份，客戶 IT 自理 | 「未設定」、不開事件；可關掉這項檢查 |
| 地端 PostgreSQL、`pg_dump` 可用 | 產品每天 `pg_dump` | 同第一列 |
| 地端 PostgreSQL、`pg_dump` 不在或版本太舊 | 每次失敗 | 嚴重 `neverSucceeded` 或警告 `lastRunFailed`，錯誤寫明原因 |
| 地端 PostgreSQL、用資料庫自己的備份 | 客戶 IT | 不設 `Backup:Root` → 未設定 |
| 雲端版、bk1 期間（本機目錄） | 產品備份到本機目錄；廠商另做主機快照 | 加「非隔離」提示；**O4–O6 不得開工**（§7.3） |
| 雲端版、隔離儲存上線後 | 產品備份到隔離儲存；保留由儲存端；可再加託管快照 | 正常；保留份數政策不生效 |
| 多台實例、`Backup:Root` 是共用掛載點 | 只有一台在跑（`ActiveSlot`） | 歷程記執行實例 |
| 多台實例、只有一台開背景工作 | 那台在跑 | 同上 |
| 多台實例、各自指向本機目錄（設錯） | 只有後建標記的那台在跑，其他台停 | 嚴重 `locationMismatch` |
| 部署者把 `Backup:Root` 換到新的空目錄 | 新位置立刻排第一份；舊位置的備份產品不再管 | 不沿用舊位置的成功 |
| 備份位置所在的磁碟（含 NAS）快滿 | 照常備份，直到 `destinationFull` | `storage.diskFree` 也量備份位置，提早警告 |
| 部署時有立即備份在跑（PostgreSQL） | 新版的 migration 要等 `pg_dump` 結束 | 先看「備份」分頁再部署（`deploy.md`） |
| 某台 `Ops:Scheduler:Enabled=false` | 那台不跑；別台照跑；全部關掉就沒人跑 | 全部關掉時新鮮度會過期告警 |
| 備份時段遇到部署重啟 | 這次中斷，重啟後自動重跑（每時段最多 3 次） | 不告警，除非拖過門檻 |
| 備份中有人寫資料 | 不擋；快照之後的寫入在下一份 | — |
| 備份中有人按「清孤兒附件」 | 被擋 409 | — |
| 清孤兒附件進行中排到備份 | 備份等它結束才快照 | 步驟顯示「等待清孤兒附件結束」 |
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
| 附件最後寫入未滿多久先等／最多等多久 | 2 分鐘／5 分鐘 | 技術限制（資料完整性） |
| `attachments` 維運鎖最長 | 30 分鐘（持有者每 60 秒續） | 技術限制 |
| 備份結束後重跑新鮮度的重試 | 每 5 秒、最多 60 秒 | 技術限制 |
| 預估空間的餘裕 | 1.1 倍 | 技術限制 |
| 歷程保留 | 最新 200 筆 | 技術限制 |
| 錯誤摘要長度 | 1000 字 | 技術限制 |
| 同一時間全系統只跑一份備份 | `ActiveSlot` 唯一 | 資料完整性 |
| 最新一份成功的備份不刪 | — | 資料完整性 |
| 備份和清孤兒附件互斥（同一把鎖） | 409／等待 | 資料完整性 |
| `manifest.json` 最後寫、沒有它不算完整；附件池只寫一次 | — | 資料完整性 |
| 保留清理以儲存位置的 manifest 為準 | — | 資料完整性 |
| 位置標記檔不一致時那台不備份 | — | 資料完整性 |
| 位置不能和資料庫、附件、金鑰、`wwwroot` 目錄重疊；路徑比對不分大小寫 | — | 安全／資料完整性 |
| 位置只由部署設定，畫面不能改、不能關 | — | 安全 |
| 沒有下載、刪除備份的 API；MCP 沒有刪除或改寫備份的工具 | — | 安全 |
| 金鑰不隨資料備份（Q1，使用者 11-21 裁決） | — | 安全 |
| `pg_dump` 密碼只走 0600 暫存 `PGPASSFILE`，不進命令列、環境變數、log；參數用 `ArgumentList` 不經 shell | — | 安全 |
| 備份目錄 0700、檔案 0600（非 Windows，盡力而為） | — | 安全 |
| Demo 站不開放立即備份 | 403 | 安全（同寄信測試信） |
| 還原一律 L4（沿用維運設計 §12） | — | 安全 |

## 20. 與既有設計或程式的矛盾

1. **維運設計 §8 表「SQLite：`VACUUM INTO` 或 Online Backup API，連同 `attachments/`、`keys/` 目錄一起備」**：改成不帶 `keys/`（Q1，使用者 11-21 裁決）；選定 `VACUUM INTO`（T3）。回填 §8。au1 設計要補一句「不帶金鑰還原後 TOTP 全部失效，靠復原碼與救援」（§5.7）。
2. **維運設計 §2「備份還原的實作仍在路線階段 5」、§7 表「產品提供備份工具與新鮮度檢查（階段 5）」、§15 第 2 條**：備份改在 bk1，還原仍在階段 5（使用者 11-19 裁決已改路線，設計文字未跟上）；回填三處。
3. **維運設計 §6「備份隔離」列**：bk1 只有本機目錄，不滿足；改寫成「bk1 本機目錄＋介面，雲端隔離儲存在 O4 之前上線」，並在 §9 的 O4 相依加上它（§7.3、Q11）。
4. **B20「備份還沒做時回未設定」**：改成 §9.1 的表，「未設定」只剩沒設位置（與 bk1b 合併前的 PostgreSQL）。`BackupFreshnessProbe` 的註解一起改。
5. **PostgreSQL provider 設計 §2「正式部署：PostgreSQL 版的 Dockerfile、部署設定檔、備份……第一個客戶上線前另排」**：備份（`pg_dump`）與 `Dockerfile` 的用戶端工具由 bk1b 做，其餘照舊另排。
6. **`docs/feature.md` 第 85 行「備份還原（段 階段5）」**：拆成「產品內備份（bk1）」與「備份還原（階段 5）」兩列（主控改）。
7. **`docs/deploy.md`「單一 instance 與 volume」只說要掛 `/app/data`**，要補下列內容：
   - 備份位置要另外掛一個 volume，最好在另一顆磁碟。
   - 多實例時備份位置要共用，或只讓一台開背景工作（§7.4）。
   - 換回舊位置要先刪標記檔。
   - PostgreSQL 部署前先看有沒有執行中的備份（§5.4）。
   - `Backup__Root`、`Backup__PgDumpPath` 兩列環境變數。
   - §9.3 的手動還原步驟。
8. **`Dockerfile.demo` 只刪 `keys`**：加 `Backup__Root`（Q8）；建置時 `--generate-demo-data` 不啟動 hosted service，映像裡不會有備份檔。
9. **手冊 §23**：「四個分頁」改五個，加「23.5 備份」；§23.2 健康檢查補備份新鮮度的幾種訊息；附件段補「備份中不能清孤兒附件」；23.5 寫不帶金鑰還原的代價（重新登入、重填寄信密碼，au1 上線後雙因素要用復原碼重綁、唯一系統管理員走 `Auth__ResetAdminPassword`，§5.7）。
10. **路線文件 bk1 範圍寫「存放位置（雲端版用隔離、只寫不刪的儲存空間）」**（`docs/roadmap.md:62`）。第 126 行把 bk1 排在 O4 之前的理由是「O4–O6 的 agent 處置以隔離備份為護欄」。本設計把雲端隔離儲存移出 bk1，改成「bk1 做介面，雲端隔離儲存另成一段，掛在 O4 之前」。
    - 這改變了使用者排序裁決依據的前提（bk1 本身不再帶隔離儲存），所以開成 Q11 待確認，不是技術題。
    - 推薦照 11-19 裁決的字面，把隔離儲存掛在 O4 之前（不是只掛 O6）。
    - roadmap 的 bk1 列範圍與 O4 相依，由主控照 Q11 的結果改。
11. **`storage.diskFree` 量的目錄**（`Program.cs:149-150` 的 `OpsStoragePaths`）：加進 `Backup:Root`，備份位置將滿時能提早警告；手冊 §23.2 的說明一併補。
12. **Demo 站的擋法**：立即備份用 403，和寄信設定一致（`AlertChannelHandlers.cs:59`、`:131`）。初稿寫的 409 `backupManualDisabled` 已改掉。

## 21. 切段

每段可獨立合併、Demo 不壞。

| 段 | 內容 | migration | 共用檔 | 依賴 |
|---|---|---|---|---|
| bk1a | 後端核心（SQLite），見表下說明 | 有（`BackupRuns`，三張表，SQLite＋PostgreSQL 各一） | 見表下說明 | 無 |
| bk1b | PostgreSQL：`PostgresDatabaseBackup`（版本比對、`pg_dump`、`PGPASSFILE`、SSL 對應、`pg_restore --list` 驗證、密碼遮罩、逾時砍整個程序樹）、`Backup:PgDumpPath`、`Dockerfile` 裝 `postgresql-client-16`、拿掉 `providerUnsupported`、`deploy.md` PostgreSQL 還原步驟與部署前檢查 | 無 | `Persistence.Postgres/DependencyInjection.cs`、`Dockerfile`、`docs/deploy.md` | bk1a |
| bk1c | 畫面與 Demo：監控頁「備份」分頁、健康卡連結、`ui.json` 的 `ops.tabs.backup`、`ops.backup.*`、`Dockerfile.demo` 的 `Backup__Root`、Demo 站停用立即備份（前端）、`local-demo.sh`、手冊 §23、四種寬度走查 | 無 | `OpsPage.vue`、`HealthCheckCard.vue`、`shared/i18n/*/ui.json`、`ops-types.ts`、`ops-queries.ts`、`dynamic-api.ts`、`Dockerfile.demo`、`scripts/unattended/local-demo.sh`、`docs/manual.md` | bk1a |

**bk1a 的內容**：

- 資料表：`Dynamic_BackupRun`、`Dynamic_BackupState`、`Dynamic_OpsLock` 與 migration。
- 儲存與位置：`IBackupStore` 與本機目錄實作、位置檢查與標記檔。
- 執行：`BackupWorker`（排程、第一次、立即備份、獨立心跳、中斷）、`SqliteDatabaseBackup`（快照與驗證）、附件增量與清單。
- 互斥與清理：清孤兒附件改搶 `attachments` 鎖、保留清理（以 manifest 為準、排程與立即分開算）。
- 狀態：`BackupFreshnessProbe` 改成真實判斷、`storage.diskFree` 加量備份位置。
- 介面與設定：API 三支（Demo 站 403）、兩條政策、`hourOfDay` 單位、稽核兩個動作。
- 文字：`MessageText` 四語、`ui.json` 的健康訊息、政策、單位與稽核鍵（§14.4）。
- 文件：`deploy.md`（環境變數、多實例、SQLite 手動還原）。
- PostgreSQL 上先回「未設定 `providerUnsupported`」。

**bk1a 的共用檔**：

- 資料存取：`DynamicDbContext`、三份新的 Configuration、兩份 model snapshot。
- 登記處：`PolicyCatalog.cs`、`AuditActions.cs`、`MessageText.cs`。
- 註冊：Application 與 Persistence 的 `DependencyInjection.cs`、`Program.cs`。
- 既有功能：`HealthProbes.cs`、`AttachmentHandlers.cs`。
- 文字與文件：`shared/i18n/*/ui.json`、前端政策單位顯示、`docs/deploy.md`。

- bk1a 是本段唯一的 migration，不和其他會加 migration 的段平行（後端規則）。
- bk1b、bk1c 都只依賴 bk1a，彼此沒有共用檔，可以平行；`deploy.md` 只有 bk1a、bk1b 動，`ui.json` 只有 bk1a、bk1c 動，各自依序即可。
- bk1a 合併後，地端 SQLite 客戶設好 `Backup:Root` 就有每日備份（還沒有畫面，看得到健康卡與告警）；bk1c 讓 Demo 能示範。
- 路線：照 Q11 的結果改。推薦做法定案時，雲端隔離儲存另開一段（排在 bk1 之後、O4 之前），O4 列加「相依：雲端隔離儲存」（主控改 roadmap）。

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
| B9 | 備份執行中 | 程序被停、重啟 | 心跳逾時（5 分鐘）之後的下一輪，那筆變 `interrupted`；重啟後自動再排一份；`sets/` 下沒有半份；`.staging/` 的殘留在下一份開始時清掉 |
| B10 | 同一天已中斷 3 次（已有過成功） | 下一輪 | 不再自動排；立即備份仍可用 |
| B11 | `Ops:Scheduler:Enabled=false`、只有一台 | 到時段 | 不備份；過 26 時後新鮮度警告 |
| B12 | 從沒成功過，第一份連續中斷 3 次 | 下一輪 | 不再自動排；新鮮度嚴重 `neverSucceeded` |
| B13 | 第一份一直排隊中、沒被領走（例如所有實例都關了背景工作） | 從第一筆 `QueuedUtc` 起 26 時 | 新鮮度警告 `firstBackupPending` |
| B14 | 備份執行中、`VACUUM INTO` 單一呼叫跑了 20 分鐘 | — | 心跳照常，沒有被判中斷 |
| B15 | 第一份在 10:00 失敗 | 當天之後的每一輪 | 不再自動排；隔天時段照常 |

### 22.2 附件

| # | Given | When | Then |
|---|---|---|---|
| A1 | 120 個附件、附件池是空的 | 備份 | 複製 120 個；`attachments.txt` 120 行 |
| A2 | A1 之後新上傳 3 個 | 再備份 | 只複製 3 個；清單 123 行；統計「新增 3／總數 123」 |
| A3 | 快照裡引用的附件，提交時間緊貼著快照開始 | 這份備份完成 | 在這份清單裡（`SnapshotUtc` 取在快照完成之後） |
| A4 | 備份持有 `attachments` 鎖 | 系統管理員清孤兒附件 | 409 `backupInProgress`，沒有檔案被刪 |
| A4b | 清孤兒附件進行中 | 排到一份備份 | 備份停在「等待」；清孤兒結束放鎖後才開始快照 |
| A5 | 附件池已有同 id 的檔案 | 備份 | 不覆寫、不重複複製 |
| A6 | 附件根目錄在 NAS、NAS 時鐘快 10 分鐘 | 備份 | 檔案沒有因時間被濾掉；等滿最多 5 分鐘後照常複製 |

### 22.3 失敗與驗證

| # | Given | When | Then |
|---|---|---|---|
| E1 | 目的地剩餘空間小於預估 | 備份 | `failed`／`destinationFull`；沒有寫出任何檔案；新鮮度至少警告 `lastRunFailed`；照 O2 規則寄告警信 |
| E2 | 目的地沒有寫入權限 | 備份 | `failed`／`destinationUnwritable` |
| E3 | 故障注入：寫完後改壞備份檔 | 驗證 | `failed`／`verifyFailed`；暫存刪掉、`sets/` 沒有這份 |
| E4 | E1 之後修好空間 | 立即備份 | 成功；新鮮度事件結案 |
| E5 | 從沒成功過、第一份失敗 | 檢查 | 嚴重 `neverSucceeded` |
| E6 | `Backup:Root` 設在資料庫目錄底下 | 啟動 | 記 Warning；不備份；新鮮度嚴重 `locationInvalid`；`POST backups` 409 `backupNotConfigured` |
| E6b | `Backup:Root` 設在 `wwwroot/backups`，或大小寫不同的資料庫目錄 | 啟動 | 同 E6 |
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
| R6 | 保留 7、已有 7 份排程備份；同一天按 7 次立即備份 | 7 次都成功 | 7 份排程備份一份都沒刪；隔天排程備份成功後，那 7 份立即備份刪掉 |
| R7 | 歷程表已超過 200 筆、保留 90 份 | 下一份成功 | 保留中的 90 份都還查得到歷程；清理依 manifest 找到每一份 |
| R8 | 位置上有一個沒有 `manifest.json` 的 set 目錄 | 保留清理 | 不碰它 |

### 22.4b 位置

| # | Given | When | Then |
|---|---|---|---|
| L1 | 舊位置已有 5 份成功 | 部署者把 `Backup:Root` 換到新的空目錄並重啟 | 建新標記檔；1 分鐘內排第一份；第一份完成前新鮮度不沿用舊位置的成功；歷程的舊列標「舊位置」 |
| L2 | 兩台實例各自指向本機目錄 | 兩台各醒一輪 | 後建標記的那台照常；另一台不備份，新鮮度嚴重 `locationMismatch` |
| L3 | 換回以前用過的目錄（留著舊標記檔） | 重啟 | `locationMismatch`；刪掉標記檔後下一輪照 L1 |
| L4 | NAS 掛載點不支援 chmod | 備份 | 成功；記一次 Warning |

### 22.5 PostgreSQL（bk1b）

| # | Given | When | Then |
|---|---|---|---|
| P1 | PostgreSQL 16、`pg_dump` 16 可用 | 備份 | `database.pgdump`；`pg_restore --list` 成功 |
| P2 | `Backup:PgDumpPath` 指到不存在的檔 | 備份 | `failed`／`pgDumpMissing` |
| P3 | 用戶端主版本小於伺服器 | 備份 | `failed`／`pgDumpVersion`，訊息帶兩個版本 |
| P4 | 備份執行中 | 看子程序的命令列與環境變數（`ps`、`/proc/<pid>/environ`） | 都沒有密碼；結束後暫存 `PGPASSFILE` 已刪 |
| P6 | 連線字串 `SSL Mode=Require;Trust Server Certificate=true` | 備份 | `PGSSLMODE=require`、不驗憑證，成功 |
| P7 | 連線字串 `SSL Mode=VerifyFull;Trust Server Certificate=true` | 備份 | `failed`／`pgDumpConfig` |
| P8 | `pg_dump` 執行中 | 站台關閉或逾時 | 整個子程序樹被結束；重啟後 migration 不被卡住 |
| P5 | bk1a 已合併、bk1b 未合併 | PostgreSQL 上檢查 | 未設定 `providerUnsupported`，不開事件 |

### 22.6 權限、畫面、還原、Demo

| # | Given | When | Then |
|---|---|---|---|
| S1 | 一般使用者 | `GET backups`、`POST backups` | 403 |
| S2 | MCP 已開 | `tools/list` | 沒有任何備份工具 |
| S3 | 備份分頁 | 390、820、1366、1920 | 不橫向捲動；390 歷程是卡片 |
| S4 | `Ops:Edition=Cloud`、本機目錄 | 開備份分頁 | 看到「非隔離」提示；位置只顯示種類、沒有路徑；啟動 log 有 Warning |
| S6 | Demo 站 | `POST backups` | 403；畫面按鈕停用並寫原因 |
| S7 | 政策頁 | 看 `ops.backupHour` | 顯示「02:00」，不是「2 小時」 |
| S5 | 備份執行中 | 看分頁 | 按鈕停用、顯示目前步驟，結束後自己更新 |
| D1 | SQLite 的一份備份，金鑰目錄不帶 | 照 `deploy.md` 還原到全新目錄並啟動 | 能登入（要重新登入）；記錄筆數一致；附件下載得到；寄信設定頁提示重填密碼 |
| D1b | 同 D1，但保留原本的金鑰目錄 | 還原並啟動 | 不必重新登入；寄信密碼照常解得開 |
| D2 | PostgreSQL 的一份備份 | 照 `deploy.md` `pg_restore` 到空資料庫並啟動 | 同 D1 |
| G1 | Demo 映像，在任一時刻（含台北 00:00–02:00）啟動 | 啟動後 1 分鐘 | 歷程有一筆剛跑的成功、沒有其他列；新鮮度正常；沒有開過備份事件；立即備份停用並寫原因 |

## 23. 測試重點

- **migration**（`Category=Postgres` 雙跑）：`MigrationConsistencyTests`；`ActiveSlot` 唯一索引在兩種資料庫都允許多個 NULL、拒絕第二個 1。
- **repository**（雙跑）：排隊撞唯一鍵、條件式領工、心跳落空、判定中斷、歷程修剪（200 筆加保留中的 set）；`ScheduleSlot`、`LocationId` 查詢；`Dynamic_OpsLock` 搶鎖、續鎖、過期接手。
- **執行器**（Application，假時鐘、假儲存）：B3–B6、B9–B15；營運時區跨日；第一次立即排並占時段；中斷重試上限；逾時；心跳在長呼叫期間照跑（B14）。
- **位置**：L1–L4；標記檔三種情況。
- **互斥**：A4、A4b；清孤兒附件異常中止後鎖 30 分鐘到期。
- **SQLite 快照**（WebAPI 或 Persistence 測試，暫存目錄的檔案資料庫）：B1、B2、B8（快照期間另一條連線寫入不被擋）；E3 驗證失敗。
- **附件**：A1–A6；2 分鐘等待與 5 分鐘上限用假時鐘。
- **儲存**：只寫一次（目標已存在丟例外）、`.partial` 改名、`CanDelete = false` 時刪除丟例外；非 Windows 的檔案權限。
- **位置檢查**：E6、E6b 的三種重疊（相同、在底下、包住），含 `wwwroot`；一律不分大小寫。
- **新鮮度**：§9.1 每一列；結束後立刻重跑，含檢查正在跑時的重試退路。
  - 事件的開與結案照既有 `IncidentRecorder` 測試的做法。
- **保留**：R1–R8；附件池清理不刪仍被引用的檔。
- **PostgreSQL**（bk1b，Testcontainers 的 16）：P1–P8；SSL 對應表與密碼遮罩各寫單元測試；逾時時結束整個程序樹。測試容器裡要有 `pg_dump`：用 Testcontainers 的 `ExecAsync` 在容器內跑，或測試機裝用戶端，計畫時定。
- **權限與稽核**：S1、S6；`backup.request` 的成功與被拒、`backup.prune`。
- **前端 vitest**：
  - bk1a：政策頁的 `hourOfDay` 顯示（S7）。
  - bk1c：分頁切換與網址；五種狀態（未設定、不合法、不一致、非隔離、正常）；執行中輪詢與停用；錯誤代碼顯示；Demo 停用原因；健康卡連結；雲端版不顯示路徑。
- **Demo**：G1（種子不動）。
- **手動演練**：D1、D2 結果寫進交付說明。

## 24. 待確認清單

### 24.1 行為類

狀態（2026-11-21）：

- Q1：使用者已裁決。
- Q2–Q6、Q8：領域顧問拍板，待使用者確認。
- Q7：維持主控推薦。
- Q9：改歸技術類，見 §24.2 T30。
- Q10–Q12：本輪依審查新增，主控暫定，交分流。

- **Q1 加密金鑰不和資料一起備份。**（使用者 2026-11-21 裁決：不帶；代價依 §5.7 補 au1 上線後 TOTP 失效）
  背景：伺服器有一個金鑰目錄（`keys/`），用來加密登入狀態、寄信密碼。原本的維運設計寫「連同 `keys/` 一起備」。
  例子：備份放在公司 NAS，某天 NAS 被偷或帳號外洩。連金鑰一起備份的話，拿到的人可以解開寄信密碼，還能偽造任何人（包括系統管理員）的登入狀態，直接登入正式系統；不帶金鑰，他拿到的是資料副本，但登不進系統。
  - 推薦：不帶金鑰。代價：換主機還原後，所有人要重新登入，系統管理員要到「寄信設定」重填 SMTP 密碼（畫面本來就會提示）；au1 上線後，所有人的雙因素也會失效，要用復原碼登入重綁，唯一的系統管理員走部署端救援；AI 存取權杖不受影響。
  - 替代：一起備份。代價：還原後什麼都不用重填；但備份外洩等於正式系統的登入被攻破，要靠換金鑰補救。
  - 替代 2：另設一個獨立的金鑰備份位置（例如不同的 NAS 帳號）。代價：多一個部署設定要管，客戶 IT 容易設成同一個地方，等於沒分開。
  - 選錯的代價：小。改成要帶金鑰只是多複製一個目錄、manifest 改一個欄位。
- **Q2 每天備份一次、時段可設（出廠營運時區凌晨 2 點）、保留最近 7 份、位置設好後立刻跑第一份（推薦；顧問拍板，待使用者確認）。**
  例子：一家 30 人的貿易商，凌晨 2 點自動備份，NAS 上永遠有最近 7 天每天一份；週三發現週一有人誤刪一批客戶，可以從週一凌晨那份找回來（還原畫面在階段 5，之前照手動步驟）。
  - 推薦：每天一次、只保留最近 N 份。代價：最壞會丟一天的資料；超過 7 天前的狀態找不回來（可調到 90 份）。
  - 替代：每 N 小時一次。代價：要多一條「間隔」政策，和「時段」怎麼搭配要另訂規則；PostgreSQL 客戶真的需要更細時，由主機做時間點還原比較適合。
  - 替代 2：每日＋每週＋每月輪替。代價：規則難懂（「保留 7 天、4 週、3 個月」），保留政策從一條變三條。
  - 選錯的代價：小。多加頻率或輪替只是加政策與排程判斷，資料表不用改。
- **Q3 備份位置只由主機的部署設定決定，畫面上不能改、不能關（推薦；顧問拍板，待使用者確認）。**
  背景：系統管理員在畫面上看得到位置、按得到立即備份，但改位置要改主機的環境變數 `Backup__Root`。
  例子：雲端版的客戶系統管理員把位置改成空的或關掉，廠商負責的備份就停了；地端的系統管理員在畫面上填 `/etc` 之類的路徑，站台程序就會往那裡寫整套資料。
  - 推薦：只能由部署設定。代價：地端小公司要請管主機的人設定一次；沒設定時監控頁寫明要找誰、設什麼。
  - 替代：畫面上可改位置與開關。代價：上面兩種風險；要另做路徑白名單、雲端版鎖定等規則。
  - 選錯的代價：中。之後若要開放畫面設定，要補路徑白名單與雲端鎖定，資料表要加設定欄。
- **Q4 備份失敗就告警，不等過期（推薦；顧問拍板，待使用者確認）。**
  背景：原本的「備份新鮮度」只看「距上次成功幾小時」，出廠 26 小時警告、50 小時嚴重。
  例子：今天凌晨的備份因為 NAS 滿了失敗。只看新鮮度的話，要到明天凌晨 4 點（26 小時）才會警告；推薦做法是失敗後立刻警告，下一次成功自動結案。
  - 推薦：上次失敗至少算警告，從沒成功過而且失敗了算嚴重。代價：NAS 短暫斷線導致一次失敗也會發一則警告。
  - 替代：只看新鮮度。代價：失敗要等一天才知道，修好之前已經少了一份。
  - 選錯的代價：極小，只是檢查的判斷式。
- **Q5 畫面放在「維運監控」的新分頁「備份」（推薦；顧問拍板，待使用者確認）。**
  - 推薦：監控頁加第五個分頁，健康檢查卡「備份新鮮度」有「看備份」連結。代價：分頁變五個，手機寬度要左右捲分頁列。
  - 替代：系統管理選單另開一頁「備份」。代價：選單多一項；備份狀態和告警分在兩處。
  - 選錯的代價：極小，只是元件放哪裡。
- **Q6 bk1 附手動還原步驟，並在驗收時兩種資料庫各演練一次（推薦；顧問拍板，待使用者確認）。**
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
- **Q8 Demo 站開排程備份、停用「立即備份」（推薦；顧問拍板，待使用者確認）。**
  （審查 M1 修正：初稿另寫「種子補一筆失敗紀錄」，會讓第一份不立刻跑、每次喚醒開嚴重事件，還會寫進客戶站台的維運紀錄，已拿掉；失敗分支改在本機走查用故障注入示範，§15。推薦本身不變。）
  背景：Demo 站任何訪客都能一鍵以系統管理員登入（寄信設定在 Demo 站因此唯讀）；Demo 站沒有永久磁碟，休眠就清空。
  例子：訪客打開「備份」分頁，看到剛才自動跑完的一份成功，「立即備份」按鈕灰掉並寫「Demo 站不開放立即備份」。
  - 推薦：如上。代價：訪客不能自己按一次立即備份看過程（本機走查會驗）。
  - 替代：Demo 站開放立即備份。代價：任何人可以一直按，雖然同時只跑一份、保留份數有上限，但每次都吃 Demo 站的 CPU 與暫存空間。
  - 替代 2：Demo 站不開備份，只顯示「未設定」。代價：公開 Demo 看不到成功的樣子。
  - 選錯的代價：極小，只是 Demo 的部署設定與一個停用條件。
- **Q10 立即備份另外算，不占每日備份的保留份數（推薦）。**（審查 M6）
  背景：保留份數原本是「所有成功的備份一起算最新 7 份」。
  例子：系統管理員週四下午測試，連按 7 次立即備份。照原本的寫法，週一到週三凌晨的排程備份全被刪掉，週三發現週一有人誤刪客戶資料，已經沒有週一那份可以找回。這和 Q2 承諾的「永遠有最近 7 天每天一份」矛盾。
  - 推薦：保留份數只算排程備份；立即備份在下一份排程備份成功後就刪（最新一份仍不刪）。代價：立即備份只能撐到隔天凌晨；想長期保存某個時間點的狀態（例如年度結帳前），要自己把那份 set 複製到別處。
  - 替代：立即備份另設保留天數（例如 7 天），和排程分開算。代價：多一條政策；短時間連按很多次時，磁碟空間會比推薦做法多用。
  - 替代 2：維持一起算。代價：上面的例子會發生，丟的是還原點。
  - 選錯的代價：小。只影響清理規則；manifest 已記 `trigger`，事後改規則不必改資料。
- **Q11 雲端隔離儲存移出 bk1，另成一段，掛在 O4 之前（推薦）。**（審查 M9）
  背景：路線文件的 bk1 範圍寫了「雲端版用隔離、只寫不刪的儲存空間」。使用者 11-19 把 bk1 排在 O4 之前，理由是「O4–O6 的 agent 處置以隔離備份為護欄」。做隔離儲存要另開一個雲端帳號，目前還沒有雲端客戶。
  例子：bk1 上線後，地端客戶馬上有每日備份，不必等雲端帳號。O4（agent 透過維運中心讀正式站的維運資料）開工前，雲端隔離儲存那一段要先完成，所以 agent 從第一天起就碰不到備份。
  - 推薦：bk1 做通用儲存介面與本機目錄；雲端隔離儲存另開一段，排在 bk1 之後、O4 之前，照 11-19 裁決的字面當成 O4–O6 共同的前置。
    - 代價：O4 要等雲端帳號開好、隔離儲存做完才能開工，O3 之後可能多空一段。
    - 代價：O4、O5 本身只有唯讀與開 PR，嚴格說還碰不到正式資料，提早做隔離儲存是保守做法。
  - 替代：只掛在 O6 之前（O4、O5 不等）。代價：和 11-19 裁決的字面不符；O4 起 agent 就連到正式站的 MCP，萬一轉送點或權限設錯，那段期間沒有隔離備份當最後防線。
  - 替代 2：bk1 就做雲端隔離儲存。代價：bk1 要先等雲端帳號、選供應商、設物件鎖定；bk1 延後，pkg1、au1 也跟著延後。
  - 選錯的代價：中。掛得太晚，agent 上線時就沒有護欄；掛得太早，O4 會多等。不論選哪個，路線文件都要把相依寫死在對應的列上。
- **Q12 雲端版的備份分頁只顯示儲存種類，不顯示路徑（推薦）。**（審查建議 11）
  例子：雲端版客戶的系統管理員打開「備份」分頁，看到的是「本機目錄（廠商代管）」，而不是 `/mnt/backup/tenant-07` 這種廠商主機的路徑。
  - 推薦：雲端版只顯示種類；地端版顯示完整路徑，方便客戶 IT 找檔。代價：雲端客戶問「備份放在哪」時，要由廠商回答。
  - 替代：兩版都顯示路徑。代價：會露出廠商主機的目錄結構與其他客戶的命名方式。
  - 選錯的代價：極小，只改 API 回應的一個欄位。

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
- **T13** 雲端版配本機目錄只提示、不擋；O4–O6 相依雲端隔離儲存（§7.3、Q11）。
- **T14** 保留只在可刪除的儲存由產品執行、最新一份不刪、附件池依清單回收、改政策不立即刪（§8）。
- **T15** 附件只複製 `LastWriteUtc ≤ snapshotUtc` 的檔、未滿 2 分鐘先等、複製前後比對（§5.5）。
- **T16** 排程以營運時區日期為時段，中斷不占時段、失敗占時段；「第一次」是當天時段的排程備份，受中斷上限約束（§6.2）。
- **T17** `SnapshotUtc` 取在快照指令完成之後；附件不用時間做上限過濾，靠「未滿 2 分鐘先等、前後比對」避開上傳中的檔（§5.5）。
- **T18** 清孤兒附件與備份共用 `Dynamic_OpsLock` 的 `attachments` 鎖，雙向互斥（§5.5）。
- **T19** 保留清理以儲存位置的 manifest 為準；歷程修剪保留仍在保留中的 set 的列（§8）。
- **T20** 位置標記檔加 `Dynamic_BackupState` 記目前位置；不一致時那台不跑；多實例要共用位置，或只開一台背景工作（§7.4）。
- **T21** `pg_dump` 用 `ArgumentList`、連線走 `PG*` 環境變數、密碼走 0600 暫存 `PGPASSFILE`、SSL 對應表、結束整個程序樹（§5.4）。
- **T22** 心跳是獨立的計時工作；交易外的條件式更新列進交付說明（§6.3）。
- **T23** 位置檢查加 `wwwroot`、一律不分大小寫；chmod 盡力而為（§7.4、§7.1）。
- **T24** Demo 站立即備份回 403，和寄信設定一致（§11）。
- **T25** `ops.backupHour` 用新單位 `hourOfDay` 顯示成時刻；健康訊息、政策、單位與稽核的四語鍵放 bk1a（§13.1、§14.4）。
- **T26** 備份結束後重跑新鮮度，遇到檢查正在跑就每 5 秒重試、最多 60 秒，再不行交給下一輪 `ops.monitor`（§6.4）。
- **T27** `storage.diskFree` 加量 `Backup:Root`（§20 第 11 點）。
- **T28** 還原步驟：原本的金鑰目錄還在就保留；版本是 `dev` 時改比 `migrationId`（§9.3）。
- **T29** 資料表欄位：`Provider` 在排隊時填；`AppVersion`、`MigrationId` 可空、快照時才填；實例 id 長度 100（§10.1）。
- **T30** 備份與「清孤兒附件」雙向互斥（原 Q9，2026-11-21 改歸技術類）：備份持有附件鎖時清孤兒回 409「備份進行中，請稍後再清」，清孤兒進行中時備份先等。理由：不擋的話，快照引用的附件可能在複製前被刪，那份備份還原後少檔。代價：大型系統第一次全量備份可能跑幾十分鐘，這段時間不能清（§5.5、T18）。

## 25. 留給實作者

- 四語文案：分頁、狀態卡、步驟、錯誤代碼、健康訊息、兩條政策、兩個稽核動作、三個 409 訊息。
- `Dockerfile` 裝 PGDG 用戶端的確切寫法（金鑰與 apt 來源、映像大小增加多少寫進交付說明）。
- PostgreSQL 測試怎麼取得 `pg_dump`（容器內執行或測試機安裝），計畫時定。
- 進度顯示「附件 120／340」的總數怎麼先算（先列一次清單即可）。
- bk1a 若估出來太大，可把保留清理（§8）拆成 bk1a2，但 migration 只在 bk1a1。
