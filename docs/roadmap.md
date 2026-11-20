# Formwright ERP 路線與進度

這份文件只放開發順序與工作段表，是進度的單一來源，每段上線後更新狀態。各段的設計理由留在設計文件（`docs/superpowers/specs/`），設計文件不維護段狀態。路線的目的、已定決定、鐵則與階段內容見[路線設計](superpowers/specs/2026-11-06-erp-roadmap-design.md)。

逐項功能的完成狀態見[功能總清單](feature.md)，每段上線時一併打勾。

## 工作段

表格列序是偏好的開發順序：已上線的段在最前，其後依使用者裁決排序。非等不可的前置寫在功能清單項目的 `⛔ 等：`，不靠列序表達。

| 工作段 | 內容 | 狀態 | 設計 |
|---|---|---|---|
| t1a、t1b、t2、t3a、t3b | 任務資料表、權限、API；留言、@、附件；站內通知與到期提醒；前端任務區塊、我的任務、通知鈴與首頁卡片 | ✅ 已上線 | [任務管理](superpowers/specs/2026-11-10-task-management-design.md) |
| t4a1、t4a2、t4b、t4c、t4d、t5 | 任務規則（定義、引擎、手動套用）、套件格式 7、設計器分頁、逐模組開關、Demo 與文件 | ✅ 已上線 | [任務管理](superpowers/specs/2026-11-10-task-management-design.md) |
| a1、a2a、a2b | 組織骨架；簽核定義、版本、約束驗證；送出、同意、退回、撤回（含 a2b2：查詢、簽核中心 API、防少簽補強） | ✅ 已上線 | [簽核流程](superpowers/specs/2026-11-10-approval-workflow-design.md) |
| a2c | 重新比對、預覽、臨時讀取權 | ✅ 已上線（程式與測試已在 main，功能清單已勾） | [簽核流程](superpowers/specs/2026-11-10-approval-workflow-design.md) |
| m1、i1、i2a、i2b、i2c | 多語系：模組選單分區、前端寫死文字、後端語系機制、記錄寫入與簽核訊息 | ✅ 已上線 | — |
| s2c、s3a | 應用程式整合層；報表資料庫端聚合 | ✅ 已上線 | [應用程式整合](superpowers/specs/2026-11-09-app-integration-design.md)、[報表資料庫端聚合](superpowers/specs/2026-11-10-report-db-aggregation-design.md) |
| p1a | 政策後端基礎：政策登記、讀值服務、模組覆寫表與 migration、政策 API；接上 A1、A2 | ✅ 已上線 | [政策設定](superpowers/specs/2026-11-11-policy-settings-design.md) §9 |
| p1b | 政策前端：系統「政策設定」頁、模組設計「政策」分頁；任務規則畫面改讀生效值 | ✅ 已上線 | [政策設定](superpowers/specs/2026-11-11-policy-settings-design.md) §9 |
| p1c | 任務與通知政策：A6、A7、A13、A14 | ✅ 已上線 | [政策設定](superpowers/specs/2026-11-11-policy-settings-design.md) §9 |
| p1d | 附件政策：A20（含部署設定檔當出廠值） | ✅ 已上線 | [政策設定](superpowers/specs/2026-11-11-policy-settings-design.md) §9 |
| a2d1 | 簽核鎖定寫入：鎖定狀態、鎖定欄位與明細的寫入守衛（只驗 SQLite，PostgreSQL 在 pg0 後補驗） | ✅ 已上線（2026-11-12，ef3c00ab） | [簽核流程](superpowers/specs/2026-11-10-approval-workflow-design.md)、[政策設定](superpowers/specs/2026-11-11-policy-settings-design.md) §9 |
| a4b | 前端：送簽畫面與簽核中心 | ✅ 已上線（2026-11-12，6ac7ec80） | [簽核流程](superpowers/specs/2026-11-10-approval-workflow-design.md) |
| a6 | CRM 設計案與簽核 Demo、文件 | ✅ 已上線（2026-11-12，026620fd） | [簽核流程](superpowers/specs/2026-11-10-approval-workflow-design.md) |
| a2d2 | 附件：上傳改走記錄寫入管線、失敗不留檔；簽核中、鎖定、已記帳的單據不能上傳（只驗 SQLite，PostgreSQL 在 pg0 後補驗） | ✅ 已上線（2026-11-12，be0dda9e） | [簽核流程](superpowers/specs/2026-11-10-approval-workflow-design.md) §8.5 |
| a2d3 | 整批重算：固定簽核中、鎖定狀態的單據與其明細列，欄位對話框提示保留筆數（只驗 SQLite，PostgreSQL 在 pg0 後補驗） | ✅ 已上線（2026-11-13，2a596156） | [簽核流程](superpowers/specs/2026-11-10-approval-workflow-design.md) §8.6 |
| a2d4 | 進行中實例守衛：有簽核中的單據時，設計器不能改會改寫記錄值的定義，也不能拆掉實例要走的轉移與狀態（只驗 SQLite，PostgreSQL 在 pg0 後補驗） | ✅ 已上線（2026-11-13，568ba627） | [簽核流程](superpowers/specs/2026-11-10-approval-workflow-design.md) §8.6 |
| a2d5 | 鎖定資料守衛與閉包重檢：有單據停在鎖定狀態時不能整批解鎖、不能刪鎖定欄位的值；改公式、彙總、帶值後重檢可編輯欄位與條件閉包（只驗 SQLite，PostgreSQL 在 pg0 後補驗） | ✅ 已上線（2026-11-13，568ba627） | [簽核流程](superpowers/specs/2026-11-10-approval-workflow-design.md) §8.6、§9.2 |
| a2d6 | 簽核數值與開關政策：關卡數、每關人數、逾期時數、主管層數（可模組覆寫）、意見字數、發起人自簽、管理員略過轉移群組改讀政策設定（只驗 SQLite，PostgreSQL 在 pg0 後補驗） | ✅ 已上線（2026-11-13，22e61ad8） | [簽核流程](superpowers/specs/2026-11-10-approval-workflow-design.md)、[政策設定](superpowers/specs/2026-11-11-policy-settings-design.md) §9 |
| a2d7 | 簽核收件人政策（備援對象、異常通知對象）；總收文件與設計寫回（只驗 SQLite，PostgreSQL 在 pg0 後補驗） | ✅ 已上線（2026-11-13，a651b8e6） | [簽核流程](superpowers/specs/2026-11-10-approval-workflow-design.md)、[政策設定](superpowers/specs/2026-11-11-policy-settings-design.md) §9 |
| a4a | 前端：簽核設計器 | ✅ 已上線（2026-11-13，6821b793） | [簽核流程](superpowers/specs/2026-11-10-approval-workflow-design.md) |
| ci1 | gate 加速：合併前總檢查搬到 GitHub Actions、分路平行；依改動決定範圍（只改文件不跑、只改前端只跑前端、動到後端全跑）；功能分支外洩掃描通過才推（使用者 11-13 裁決） | ✅ 已上線（2026-11-13，c3d12146） | — |
| tz1 | 報表「今天、本月」等日期區間與無時區日期時間改用營運時區（Asia/Taipei），不依主機時區；修好後 CI 拿掉 `TZ` 覆寫。ci1 在 UTC 的 GitHub 機器上抓到：SQL 路徑依主機時區計算，Render 也是 UTC | ✅ 已上線（2026-11-13，b51c169d） | — |
| a5 | 套件格式 8：簽核流程；接手 CRM 設計案流程：套件內建，刪 `DemoApprovals`；採購單金額門檻的 Demo 在這裡驗收 | ✅ 已上線（2026-11-14，ef66fda7） | [簽核流程](superpowers/specs/2026-11-10-approval-workflow-design.md) |
| a3 | 轉交、管理員終止與改派、後加簽、個人代理設定、逾期提醒、臨時讀取的列印預覽（使用者 11-12 裁決） | ✅ 已上線（2026-11-14，a3a–a3d 3f584c8a、6a5dc263 等；a3e、a3f affc0766） | [簽核流程](superpowers/specs/2026-11-10-approval-workflow-design.md) |
| a3g | a3 收尾：Demo 補採購單列印範本、a3 裁決寫回設計文件、手冊總收 | ✅ 已完成（待上線） | [簽核流程](superpowers/specs/2026-11-10-approval-workflow-design.md) |
| fg1 | 簽核流程圖地基（fg1a 資料欄位、只有草稿的流程、執行期守門、分支數政策；fg1b 型別、參照、轉換；fg1c 結構驗證與共用案例；fg1d 警告、Application 驗證、總收）：圖形定義型別、結構驗證、sequence → graph 轉換、migration、分支數政策；API 仍不收 graph（只驗 SQLite，PostgreSQL 在 pg0 後補驗） | ✅ 已上線（2026-11-14，fg1a 301b0da1、fg1b＋fg1c 6a3681cb、fg1d d5ae6594） | [簽核流程圖](superpowers/specs/2026-11-13-approval-flowgraph-design.md) §17 |
| fg2 | 流程圖執行引擎（fg2a1–fg2g2 九段）：並行分支、全部到齊／任一到齊匯合、分支取消、退回與撤回延伸、重新比對、預覽與試算預計路徑、同一單據動作序列化、與 sequence 的等價性；API 開始接受 graph 存檔（沒有畫面，只能從 API 建立）、已存的 graph 流程可送出與簽核；通知節點、匯出仍不收 | ✅ 已上線（2026-11-15，9f3a7cb6）；回退下限 fg2g2 | [簽核流程圖](superpowers/specs/2026-11-13-approval-flowgraph-design.md) §17 |
| fg4 | 單據頁唯讀進度流程圖（fg4a 並行摘要文案、fg4b 後端進度圖資料、fg4c Vue Flow 與寬螢幕進度圖、fg4d 手機縮圖與全螢幕、節點細節；使用者 11-15 裁決提前到 fg3 之前，首次引入 Vue Flow；左到右排版）、並行時簽核摘要列出進行中的關 | ✅ 已上線（2026-11-16，fg4a 5959b8fc、fg4b 756eada5、fg4c ec502385、fg4d c40152d4）；<br>沒有資料變更，回退到任何更早版本都安全 | [簽核流程圖](superpowers/specs/2026-11-13-approval-flowgraph-design.md) §17 |
| fg3 | 拖拉式流程圖設計器（Vue Flow）：草稿與發布、四種寬度（手機只看圖＋改屬性），移除舊關卡編輯 | 進行中：<br>fg3a 後端草稿與發布、fg3b 前端地基已上線（2026-11-16，4444d28a）；<br>fg3c 設計頁（看圖、改屬性、存草稿、發布，只開給流程圖格式與只有草稿的流程）已上線（2026-11-16，c0b4c82d）；<br>fg3d 試算疊圖與 Demo 流程圖範例流程（停用）已上線（2026-11-16）；<br>fg3e 結構編輯、fg3f 切換與移除舊對話框、fg3g 進度圖讀版面未開始。沒有 migration，回退下限見計畫 fg3f 列 | [簽核流程圖](superpowers/specs/2026-11-13-approval-flowgraph-design.md) §17 |
| fg5 | 通知節點：站內通知、收件人解析、範本帶欄位值並依收件人權限遮蔽 | 未開始 | [簽核流程圖](superpowers/specs/2026-11-13-approval-flowgraph-design.md) §17 |
| fg6 | 流程圖收尾：既有流程批次轉換、套件格式升版、手冊與文件 | 未開始 | [簽核流程圖](superpowers/specs/2026-11-13-approval-flowgraph-design.md) §17 |
| kb1、kb2 | 主管看部屬任務與重派；看板資料表、存檔與分享 API、欄查詢與移動 API | ✅ 已上線（2026-11-14，kb1 3d56fc56、kb2 74616b3f） | [任務看板、報表與甘特圖](superpowers/specs/2026-11-13-task-board-reports-gantt-design.md) §20 |
| kb3 | 任務看板畫面：清單／看板切換、拖拉改值、自訂看板與分享 | ✅ 已上線（2026-11-17，a9b9894d）；<br>看板欄查詢回應多帶 `config`，沒有 migration，回退到 kb2 安全 | [任務看板、報表與甘特圖](superpowers/specs/2026-11-13-task-board-reports-gantt-design.md) §20 |
| kr1、kr2、kr3 | 任務報表：任務進報表資料來源、條件量值與中位數、內建任務統計報表 | kr1 已上線（edce4f20：任務與任務事件資料來源、符合條件筆數、中位數、只算符合條件的加總與平均，API 層）；<br>kr2 已上線（a5f9122f：設計器選任務資料來源與新量值、報表頁期間與部門暫時篩選、下鑽點開任務、匯出帶篩選）；<br>kr3 未開始 | [任務看板、報表與甘特圖](superpowers/specs/2026-11-13-task-board-reports-gantt-design.md) §20 |
| st1、st2 | 子任務（一層、父任務顯示進度）；st1 拆 st1a 資料與可見性、st1b 狀態連動、st1c 報表層級（設計 §10.7.6） | ✅ 已上線（2026-11-18；st1a 3b093e25 資料與可見性、st1b f9e58417 狀態連動與 cascade、政策 A40、st1c cbe8b4cb 報表層級 -16、st2 子任務畫面、進度、三選一確認框、看板「顯示子任務」、Demo 行銷活動子任務）；<br>`TaskParent` 只有 SQLite 一份，PostgreSQL 那份隨 pg0 | [任務看板、報表與甘特圖](superpowers/specs/2026-11-13-task-board-reports-gantt-design.md) §20 |
| gt1、gt2 | 甘特圖：任務開始日、甘特檢視與拖拉調整日期；拆 gt1a–gt1c（gt1d 可選）、gt2a–gt2c（設計 §11.5） | ✅ 已上線（gt1d 可選，未做）：<br>gt1a 已上線（2026-11-18，bb9c1cf7；開始日欄位、驗證、到期日改早時開始日跟著移、T21、DTO、任務詳細與新增對話框、看板提示）；<br>gt1b 已上線（2026-11-18，3ed521bc）（甘特設定 `TaskGanttConfig`、`task-views` 收 `kind=gantt`、看板端點拿到甘特 Id 回 404、`POST /tasks/{id}/schedule`、前端型別與 API）；<br>gt1c 已上線（2026-11-18，e850ac4e）（`POST /tasks/gantt`：時間窗、範圍與篩選同看板、未排程、`parents`、`spans`、500／100／366 上限；前端型別、normalize、`getGantt`）；<br>gt2a 已上線（2026-11-18，c44e6cd9）（任務頁「清單／看板／甘特圖」三段切換與 `view=gantt`、預設甘特圖唯讀畫面：日／週／月刻度與平移、三種分組與收合、子任務跨度、今天線、逾期與完成樣式、未排程、500 列提示、四語、Demo 行銷活動開始日；採 §11.5.8 拆分備案：平板與手機暫顯示「甘特圖請用較寬的螢幕」說明，日期對話框與手機週清單移 gt2b）；<br>gt2b 再拆 gt2b1、gt2b2；<br>gt2b1 已上線（2026-11-18，a9c9b8a3）（feature/gt2b1）（平板與手機已可用：平板時間軸、手機依週分組清單、「編輯日期」與「排程」鈕、日期對話框走 `schedule` 與 409 重新整理）；<br>gt2b2 已上線（2026-11-18，62f05471）（feature/gt2b2）（桌機筆電拖拉改日期：整條、兩緣、菱形、未排程拖入、Esc 取消、樂觀更新與 409、請求中不重送、觸控不拖）；<br>gt2c 已上線（2026-11-18）（feature/gt2c）（存檔甘特圖：選擇器、設定、分享沿用看板元件，`gantt=<id>`、上次打開與看板分開、D6、D7、手冊）；<br>`TaskStartDate` 只有 SQLite 一份，PostgreSQL 那份隨 pg0 | [任務看板、報表與甘特圖](superpowers/specs/2026-11-13-task-board-reports-gantt-design.md) §11.5 |
| O1 | 維運地基：結構化 log 與 request id、health live／ready、版本端點、前端錯誤收集、背景排程器（o1a、o1b、o1c）；任務「即將到期」提醒改為到期前 N 天內補一次、同一任務只補一次（使用者 2026-11-12 裁決，排進 o1b） | ✅ 已上線：<br>o1a（觀測地基）已上線（2026-11-12）；<br>o1b（背景排程器、租約表、通知維護工作與提醒新規則，migration `OpsScheduledJob`）已上線（2026-11-18，5658f512）；<br>o1c（前端錯誤回報、錯誤代碼、版本注入、source map 移出 wwwroot）已上線（2026-11-18，6697f019） | [維運與觀測](superpowers/specs/2026-11-11-ops-observability-design.md) §9、§10 |
| un1 | 列表、匯出、篩選的使用者欄位顯示姓名（小段穿插，使用者 11-11 裁決；位置是主控判斷） | ✅ 已完成（2026-11-18 查證為既有行為：清單與明細 `field-type-registry.ts` 的 user 格式化、篩選選項、匯出 `ExportRecordsHandler` 皆顯示姓名，`FieldDisplayTests.User_UsesName_OrRaw` 涵蓋；不另開工） | — |
| pg0a | PostgreSQL provider 抽象：依 `Database:Provider` 選資料庫，SQLite 行為不變、不加 migration | ✅ 已上線（2026-11-18；前半 a0bf0749：設定與 DI、錯誤分類、dialect、WriteGate.ForKey；後半 16266e57：TestDb 改名、SqliteOnly 標記、MigrationConsistency、字串長度與 Kind 守衛） | [PostgreSQL provider](superpowers/specs/2026-11-12-postgres-provider-design.md) |
| pg0 | （pg0b–pg0d）PostgreSQL provider 與 migration＋可在 PostgreSQL 跑的測試環境；驗收要含 O1 的排程器（兩實例搶同一工作）與通知維護（兩條路徑並行不重複）在 PostgreSQL 上通過（WebAPITests 的 `Ops.SchedulerTests`、`Ops.ScheduledJobLeaseTests`、`Ops.NotificationUpkeepJobTests`，不在 ApplicationTests；「無鎖模式」的替換點由 pg0 在 `TestApp` 加）；`OpsScheduledJob` 只有 SQLite 一份，PostgreSQL 那份隨 pg0；pg0a 另列；pg0 做完後另排 Demo 站改接 Neon PostgreSQL（使用者 2026-11-18 改判 U4，產品仍支援 SQLite） | ✅ 已上線（2026-11-19，af820397）：<br>pg0b（2026-11-18，3d3e3c1d；`Persistence.Postgres`、baseline `InitialCreate`、WriteGate＋全庫 advisory lock、啟動串行鎖、報表強制 MemoryOnly、擴充資料衝突只接唯一鍵；本機 postgres:16 走查 §8 第 5、6 項）；<br>pg0c（2026-11-18，a80b8852；Testcontainers 測試環境、`FORMWRIGHT_TEST_DB=postgres` 切換、雙跑集、O1 排程器與通知維護在 PostgreSQL 上通過、CI 加 PostgreSQL job）；<br>pg0d（全套在 PostgreSQL 上 0 失敗：ApplicationTests 3313、WebAPITests 1383；產品程式未改；報表 SQL 路徑 3 類標 PgDeferred 等 pg1） | [PostgreSQL provider](superpowers/specs/2026-11-12-postgres-provider-design.md) |
| a4c | 清單頁系統篩選「簽核中」（記錄清單查詢加簽核實例子查詢）；要在兩種資料庫一次驗證，等 pg0（使用者 11-12 裁決 U2） | ✅ 已上線（2026-11-19，3c5ef44d；`approval=pending |none` 參數、schema `approvalEnabled`、清單頁「全部／簽核中／未在簽核」切換列、匯出帶條件；設計 §13.4.1；兩種資料庫驗證） | [簽核流程](superpowers/specs/2026-11-10-approval-workflow-design.md) §13.4 |
| 權限軸 | 設計與授權分成兩條權限軸（使用者 11-19 裁決排在 O2 之後、p2 之前；授權範圍＝模組權限分頁四塊、共用主檔貢獻欄位開放、開啟套件的群組對應、「不提供給 AI」旗標，簽核人與狀態轉移群組、共用檢視仍歸設計，自授簽核讀取與彙總路徑寫手冊已知限制） | ✅ 完成（2026-11-20；-a 後端 60db37ea、-b 畫面 62e47f43） | [權限軸](superpowers/specs/2026-11-19-permission-axes-design.md) |
| pg1 | 報表資料庫端聚合 SQL 路徑的 PostgreSQL 改寫（pg0 期間 PostgreSQL 上報表強制走記憶體路徑） | ✅ 已上線（2026-11-19，92af014a；識別字引號、索引提示與 `OrderedJoin` 走 dialect、`PostgresDialect.DateBucket`、PostgreSQL 唯讀快照（REPEATABLE READ READ ONLY＋`statement_timeout`，不經寫入閘門）、拿掉強制 MemoryOnly 與 `PgDeferred`；報表 SQL 三類進雙跑集） | [PostgreSQL provider](superpowers/specs/2026-11-12-postgres-provider-design.md) |
| O2 | 事件中心：健康檢查、維運事件、稽核表（與 MCP 共用）、告警通道、監控頁、規則失控自動暫停 | ✅ 完成（2026-11-19）：<br>細部設計 §17 已定稿（2026-11-19，14cec776，兩輪審查）；<br>實作分 o2a–o2e 各自交付；<br>o2a 已上線（2026-11-19，7748ed07；維運九張表與 `OpsEventCenter` migration、`IDatabaseErrors` 唯讀／空間滿／連不上、稽核寫入（73 個請求）與保留清理、稽核查詢 API、Demo 登入每來源總量）；<br>o2b 已上線（2026-11-19，93d70d10；七個健康檢查與看門狗、`ops.monitor`、維運事件記錄器與收件匣、500 與前端錯誤接事件、站內通知、唯讀／空間滿回 503、監控 API）；<br>o2c 已上線（2026-11-19，7fd188b9；SMTP 告警通道設定與加密、MailKit 寄送、測試信、寄送佇列與重試、每小時節流與摘要信、通道失敗事件、資料庫寫不進時看門狗直送與交接）；<br>o2d 已上線（2026-11-19，a858e208）；<br>o2e 已上線（2026-11-19，32ab3355；監控頁、稽核頁、共用寄信設定頁與 `SharedMailSettings` migration、任務規則暫停畫面、Demo 關 diskFree） | [維運與觀測](superpowers/specs/2026-11-11-ops-observability-design.md) §9 |
| p2、p3 | 設計與處理上限；地區與組織政策 | ✅ 已完成：<br>p2（6d647080，2026-11-20）；<br>p3a（ae542b83，2026-11-20：營運時區、部門層數）；<br>p3b（bc8d7203，2026-11-20：預設、啟用、必填語系，沒指定語系用預設語系；剩 11 行靜態路徑見 #66） | [政策設定](superpowers/specs/2026-11-11-policy-settings-design.md) §9、§3.7 |
| i3 | 多語系：名稱欄位多語（儀表板、報表名稱），套件格式隨升版；與 i4f 的套件升版併成一次 | 未開始 | — |
| i4 | 語系包與自訂語系（可新增語系、JSON 語系包匯入匯出、內建語系可覆寫、設計資料 Excel 翻譯），切 i4a–i4f（i4d 分 i4d1、i4d2）；段內順序 i4a → i4b／i4c → i3 → i4d1 → i4d2 → i4e、i4f | 進行中（i4a 已上線 3d0dd59b；i4b 已上線 7843faa9；i4c 已上線 bf6fef7b；i4d1 已上線 0e42b98e） | [語系包與自訂語系](superpowers/specs/2026-11-17-language-packs-design.md) §20 |
| mcp1a | MCP 後端地基：權杖、範圍、工具分級與確認碼、稽核寫入點、速率限制、12 個唯讀工具；權杖與確認碼的 repository 測試在 PostgreSQL 與 SQLite 各跑一次 | 未開始 | [MCP server](superpowers/specs/2026-11-11-mcp-server-design.md) §12 |
| mcp1b | MCP 畫面：個人「AI 存取權杖」頁、管理員「AI 連線」頁、設計器欄位旗標、手冊 | 未開始 | [MCP server](superpowers/specs/2026-11-11-mcp-server-design.md) §12 |
| O3 | 廠商維運中心（Cloudflare Workers＋D1）、外部探測、匿名回報與新版本通知、告警推播 | 未開始 | [維運與觀測](superpowers/specs/2026-11-11-ops-observability-design.md) §9 |
| bk1 | 產品內備份：排程備份、存放位置（雲端版用隔離、只寫不刪的儲存空間）、O2「備份新鮮度」檢查接上真實時間；還原留在階段 5（使用者 11-19 裁決；開工前先寫設計） | 未開始（設計待寫） | [維運與觀測](superpowers/specs/2026-11-11-ops-observability-design.md) |
| pkg1 | 套件更新入口：把新版內建應用程式套到客戶已開啟的應用程式（先寫設計；第一個客戶上線前必須有結論；方向為升級預覽與逐項套用，使用者 11-17 裁決） | 未開始（設計待寫） | — |
| au1 | 帳號安全：帳號信的寄信佇列（不節流、不摘要）、寄信重設密碼、雙因素登入 TOTP（使用者 11-17 裁決，雲端版對外開放前必須有；開工前先寫設計）。SMTP 連線已在 o2e 抽成核心「寄信設定」，與告警信共用連線、佇列分開（使用者 11-19 裁決） | 未開始（設計待寫） | [功能清單](feature.md)「寄信重設密碼與核心寄信能力」「雙因素登入」兩列、[維運與觀測](superpowers/specs/2026-11-11-ops-observability-design.md) §17.2.6 |
| O4、mcpO1 | agent L1 唯讀診斷；產品端維運唯讀工具與維運服務帳號 | 未開始 | [維運與觀測](superpowers/specs/2026-11-11-ops-observability-design.md) §9、[MCP server](superpowers/specs/2026-11-11-mcp-server-design.md) §12 |
| O5 | agent L3 修正 PR、CI gate | 未開始 | [維運與觀測](superpowers/specs/2026-11-11-ops-observability-design.md) §9 |
| O6、mcpO2 | 雲端版 L2 自動處置；產品端維運處置工具 | 未開始 | [維運與觀測](superpowers/specs/2026-11-11-ops-observability-design.md) §9、[MCP server](superpowers/specs/2026-11-11-mcp-server-design.md) §12 |
| mcp2a | MCP 一般寫入工具、冪等鍵 | 未開始 | [MCP server](superpowers/specs/2026-11-11-mcp-server-design.md) §12 |
| mcp2b | MCP 高風險業務工具 | 未開始 | [MCP server](superpowers/specs/2026-11-11-mcp-server-design.md) §12 |
| mcp3 | MCP 設計器工具（開工前另寫設計） | 未開始 | [MCP server](superpowers/specs/2026-11-11-mcp-server-design.md) §12 |
| mcp4 | MCP OAuth 2.1（Claude.ai、ChatGPT 連接器；可視雲端版第一個客戶的需求提前） | 未開始 | [MCP server](superpowers/specs/2026-11-11-mcp-server-design.md) §12 |
| s2d-1g | Demo 公司跨應用程式共用、移除模擬客戶與全部內建模擬實體 | ✅ 已上線（2026-11-14，8719cc5b） | [共用主檔](superpowers/specs/2026-11-10-shared-master-design.md) |
| s2d-1h | 主檔收尾：參照挑選器守衛、CRM 補測試等 | 未開始 | [共用主檔](superpowers/specs/2026-11-10-shared-master-design.md) |
| s2d-2 | 品項共用主檔；併入品項實物／服務類型、預留所屬款式參照、往來對象多送貨地址（使用者 11-17 裁決，開工前先更新共用主檔設計） | 未開始 | [共用主檔](superpowers/specs/2026-11-10-shared-master-design.md) |
| mg1 | MES 改為通用生產管理：應用程式改名、製程與不良原因選項通用化、完工入庫不限單一末道、模具模組改範例、Demo 與手冊同步（使用者 11-17 裁決，排在 s2e 前避免重工；開工前先寫設計） | 未開始（設計待寫） | [ERP 路線](superpowers/specs/2026-11-06-erp-roadmap-design.md) 2026-11-17 一節 |
| s2e-a | 訂單到出貨：追溯與履約核心 | 未開始 | [訂單到出貨](superpowers/specs/2026-11-11-order-workorder-shipment-design.md) |
| s2e-b | 訂單到出貨：應用程式間整合 | 未開始 | [訂單到出貨](superpowers/specs/2026-11-11-order-workorder-shipment-design.md) |
| s2f | 訂單變更單；併入可設定的超交容許量（使用者 11-17 裁決） | 未開始 | [訂單到出貨](superpowers/specs/2026-11-11-order-workorder-shipment-design.md) |
| s2g | 進銷存補強：單位換算、銷貨與採購退回、庫存預留與可用量、批號與有效期（使用者 11-17 裁決，庫存異動結構一次改完；開工前先寫設計） | 未開始（設計待寫） | [ERP 路線](superpowers/specs/2026-11-06-erp-roadmap-design.md) 2026-11-17 一節 |
| s2h | 多幣別完整版：單據幣別、匯率表自動更新、入帳換算、收付款匯差、期末重估（使用者 11-17 裁決；匯差與重估需會計專業確認） | 未開始（設計待寫） | [ERP 路線](superpowers/specs/2026-11-06-erp-roadmap-design.md) 2026-11-17 一節 |
| s3b | 期間鎖定、分組小計、損益表列結構、Top N | 未開始 | [ERP 資料模型](superpowers/specs/2026-11-10-erp-data-model-design.md) |
| s3c | 會計應用程式 | 未開始 | [ERP 資料模型](superpowers/specs/2026-11-10-erp-data-model-design.md) |
| s4p | 生產與採購：依欄位分組拆單（第一個情境：請購單）→ 多層 BOM 與版本 → 安全庫存補貨 → 簡化 MRP（使用者 11-17 裁決，排在 s3c 後、HRM 前；開工前再切段並寫設計） | 未開始（設計待寫） | [ERP 路線](superpowers/specs/2026-11-06-erp-roadmap-design.md) 2026-11-17 一節 |
| ct1 | 核心週期性自動產生單據＋合約與週期開票應用程式（使用者 11-17 裁決；位置為主控判斷，需要 s3c 的發票；開工前先寫設計） | 未開始（設計待寫） | [ERP 路線](superpowers/specs/2026-11-06-erp-roadmap-design.md) 2026-11-17 一節 |
| HRM | 階段 4：員工主檔、請假、出勤、費用報支，含依部門的資料範圍；開工前再切段 | 未開始 | — |
| 階段5 | 檢視與收尾：看板、日曆（含資源時段衝突檢查）、專案與工時、客服工單、應用程式管理畫面補齊（含應用程式匯出畫面）、備份還原、已刪除記錄還原、客戶 Logo 與主色（後兩項使用者 11-17 裁決）；開工前再切段 | 未開始 | — |

HRM（階段 4）與階段 5 先用佔位列排序，開工前再切成正式工作段。

## 階段概況

| 階段 | 狀態 |
|---|---|
| 階段 0：地基 | 已完成 |
| 儀表板 | 已完成 |
| 階段 1：單據流與 CRM | 已完成 |
| 階段 2：事件、帳本、進銷存與 MES | 核心與 s2c 整合層已完成；s2d 共用主檔進行到 s2d-1f |
| 任務管理（插在 s2d-1f 之後） | 已完成 |
| 簽核流程（與任務管理一起做） | 進行中 |
| 階段 3：會計 | s3a 報表資料庫端聚合已完成；s3b、s3c 未開始 |
| 階段 4：HRM | 未開始（組織骨架、站內通知已隨任務與簽核提前） |
| 階段 5：檢視與收尾 | 介面語言切換已完成；其餘未開始 |
| 平台段（2026-11-11 插入）：政策設定、維運、PostgreSQL、MCP | 未開始；政策 p1 開發中 |

## 開發順序（使用者 2026-11-10 確認）

1. **任務管理與簽核**（含 CRM 設計案情境）。簽核後端一條線；任務畫面與規則平行。
2. **多語系**（穿插進行）：模組選單分區多語系化；全面盤點後修補高、中兩級；之後做 i4：語系包與自訂語系（可新增語系、JSON 語系包匯入匯出、內建語系介面文字可覆寫、設計資料 Excel 翻譯）。
3. **共用主檔收尾**：s2d-1g（Demo 公司跨應用程式共用等）、s2d-1h、s2d-2（品項共用、移除模擬實體；併入品項實物／服務、所屬款式、多送貨地址，使用者 11-17 裁決）→ mg1 MES 改通用生產管理（11-17 插入，在 s2e 前）。
4. **訂單到出貨整合**：s2e-a 追溯與履約核心 → s2e-b 應用程式間整合 → s2f 訂單變更單（含超交容許量）→ s2g 進銷存補強 → s2h 多幣別（s2g、s2h 為 11-17 插入的會計前置）。
5. **會計**：s3b 期間鎖定、分組小計、損益表列結構、Top N → s3c 會計應用程式（含銷貨與進貨折讓）→ s4p 生產與採購 → ct1 週期產生與合約（兩段皆 11-17 插入，在 HRM 前）。
6. **HRM**：依部門的資料範圍，以及員工、請假、出勤、費用報支。
7. **階段 5 其餘**：看板與日曆、專案與工時、客服與 IT 工單、應用程式開啟頁面、刪除應用程式、手動群組對應、備份還原。

第一個客戶正式上線前，必須決定套件更新入口（見路線設計「這版不做」；段 pkg1，2026-11-12 排在 O3 之後，先寫設計）。

## 2026-11-11 插入的平台段

設計：[政策設定](superpowers/specs/2026-11-11-policy-settings-design.md)、[維運、觀測與告警派工 agent](superpowers/specs/2026-11-11-ops-observability-design.md)、[MCP server](superpowers/specs/2026-11-11-mcp-server-design.md)。這幾段插在上面第 1 項「任務管理與簽核」的簽核主線中間。

順序：**p1 → o1a → 簽核 a2d1 → a4b → a6 → a2d2–a2d7 → a4a → ci1 → tz1 → a5 → a3 → fg1–fg6 → kb1–kb3、kr1–kr3 → st1、st2 → gt1、gt2 → o1b、o1c → un1 → pg0 → pg1 → O2 → 權限軸 → p2、p3 → i3、i4 → mcp1（mcp1a → mcp1b）→ O3 → bk1 → pkg1 → au1 → O4–O6**，MCP 後續段 mcp2a → mcp2b → mcp3 → mcp4 依進度與 O4–O6 穿插。

- 使用者裁決（2026-11-11）：p1 → O1 → pg0 → a2d–a6 → O2 → mcp1 → O3…；O1 在 p1 之後、a2d 之前；O2 以後視情況穿插；pg0 在 O1 之後，之後的功能兩種資料庫都驗，其餘 PostgreSQL 改寫排在簽核之後；MCP 地基 mcp1 在簽核與 O2 之後，業務寫入、設計器工具、OAuth 後續分段，維運 O4、O6 直接用 MCP 工具；列表的使用者欄位顯示姓名用小段穿插。
- 使用者確認（2026-11-12）：p2、p3 排在 O2 之後、mcp1a 之前；O3 → O4 → O5 → O6 的先後；mcp1 之後是 mcp1a → mcp1b → mcp2a → mcp2b → mcp3 → mcp4，mcpO1 隨 O4、mcpO2 隨 O6，mcp4 可視雲端版第一個客戶的需求提前。
- 使用者裁決（2026-11-19）：權限軸排在 O2 之後、p2 之前；產品內備份（bk1）插在 O3 之後、O4 之前，還原留在階段 5，因為 O4–O6 的 agent 處置以隔離備份為護欄。
- 使用者裁決（2026-11-17）：可自行新增語系與匯入語系包，併入 i4（內建四語介面文字可覆寫；設計見[語系包與自訂語系](superpowers/specs/2026-11-17-language-packs-design.md)）；順序不變。
- 使用者排定（2026-11-12）：翻譯匯出、匯入（i4）排在 p3 之後，與 i3 一起；套件更新入口（pkg1）排在 O3 之後，先寫設計；應用程式匯出畫面併入階段 5。
- 使用者裁決（2026-11-12）：為了 Demo CRM 設計案，簽核主線提前到 o1a 之後、pg0 之前；簽核先只驗 SQLite，pg0 後補驗
- 使用者裁決（2026-11-12）：a2d 拆開，a2d1（鎖定寫入）先做，a2d2–a2d7 排在 a4b、a6 之後、a4a 之前；理由：a4b、a6 不依賴附件與整批重算，先讓簽核畫面與 Demo 上線。
- 使用者裁決（2026-11-13）：a4a 之後、a5 之前插入 ci1（gate 加速），先把總檢查搬到 GitHub Actions，再分析慢測試與不穩定測試。
- 使用者裁決（2026-11-13）：簽核改成拖拉式流程圖（fg1–fg6），順序 a5 → a3 → fg；任務看板、報表、子任務、甘特圖排在流程圖之後，先子任務再甘特圖。
- 主控判斷（2026-11-13，待使用者確認）：tz1 緊接 ci1、排在 a5 前；理由：正式 Demo 跑在 UTC，報表日期區間可能差 8 小時，屬正確性問題。
- 使用者裁決（2026-11-20）：未來各段的設計題（2026-11-19 分流，平台、ERP、應用程式三份）要問使用者的全部照推薦、顧問裁決全部確認；帳務與稅務的專家題不找會計師，照台灣中小企業常見做法先定、做成可設定，設計標「未經會計師確認」。各段寫設計時直接採用，不再重問。
- 主控判斷（位置）：un1 排在 O1 之後、pg0 之前；pkg1 緊接 O3，不等 O4。
- 段號命名：MCP 各段一律用 mcp 開頭（mcp1a、mcp1b、mcp2a、mcp2b、mcp3、mcp4、mcpO1、mcpO2），與已上線的多語系 m1（模組選單分區）區隔。

維運與 MCP 兩份設計的 C1–C7 已由使用者 2026-11-12 全部裁決（都選建議方案），紀錄在維運設計 §16、MCP 設計 §20。
