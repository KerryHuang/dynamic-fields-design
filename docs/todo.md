# 待辦

跨 session、又不屬於任何功能的雜事。一行一項、動詞開頭，冒號後寫脈絡；做完就刪（紀錄留在 git 歷史）。發現其實是功能，就移到 [功能清單](feature.md)。

## 近期可開工

自動駕駛等 CI、部署、審查時的空檔候選：依賴已滿足、範圍小、可隨時暫停。每項寫目標與驗收｜依賴｜基準 SHA｜預計路徑｜資源｜可暫停點｜狀態｜owner；做完刪掉，從下方「其他」挑新的補上。

- 修正略過標記檢查註解引用的設計章節（a5d 複審 N5）：註解 §6.3 改 §6.1，build 過即可｜依賴：無｜基準 3c024feb｜backend/src（略過標記檢查處）｜CPU 輕｜任一 commit 後｜可開工｜未指派
- 持續追蹤 vitest 在 jsdom 拆除後 Quasar 計時器觸發「window is not defined」：本機無法重現，根因未證實；已做局部修正（ApproverSourceEditor spec 每個測試後卸載元件，QSelect 卸載時清 filter／input 計時器），曾試全域包裝 setTimeout 但可能吞掉真實錯誤已撤。CI 再出現時看是哪個 spec、哪個計時器；其他用 QSelect use-input 的 spec 尚未處理
- 檢查 xUnit 測試是否還有依賴牆鐘短上限的寫法：唯讀盤點，結果列回本檔｜依賴：無｜基準 3c024feb｜唯讀 backend/tests｜不吃 CPU｜任何時候｜可開工｜未指派
- SQLite 報表快照的 EF 交易包裝也改成 dispose `UseTransactionAsync` 回傳的 handle（pg1 審查修正只修了 PostgreSQL 版；in-memory 連線常開，目前無害）：驗收 SQLite `FullyQualifiedName~Report` 全過、快照後連線開啟計數平衡｜依賴：無｜基準 92af014a｜backend/src/DynamicModules.Persistence/Dynamic/Query/SqliteReportSqlSnapshot.cs｜CPU 中｜任一 commit 後｜可開工｜未指派
- 登入稽核改用登入專用 API 明確傳入行為者，取代 `AuditOrigin.BeginSignIn` 的呼叫堆疊檢查（o2a 複審建議：目前正確，但依賴呼叫端是 async 狀態機，抽 helper 或換 runtime 時要重核對）：驗收 A04、A11、架構測試全過、BeginSignIn 移除｜依賴：無｜基準 7748ed07｜backend/src/DynamicModules.Application/Common/Audit、Login／DemoLogin handler｜CPU 中｜任一 commit 後｜可開工｜未指派
- 補收件人名稱解析的後端測試（a2d7 N-M3）：政策收件人引用已停用或不存在帳號時的行為，a4a 前端部分已隨收件人 chip 修正補上；驗收新測試兩種資料庫皆過｜依賴：無｜基準 1dda986c｜backend/tests（收件人解析）｜CPU 中｜任一 commit 後｜可開工｜未指派

## 其他

- gt2a 甘特圖畫面小問題與審查建議：時間窗第一天的菱形被左緣切掉一半；zh-TW 日刻度顯示「16日 週一」（Intl 預設帶「日」）；同鍵重抓時沒有「更新中」提示、失敗但保有舊資料時不顯示錯誤與重試；未排程區只有開始日時要帶年份；快取 Intl.DateTimeFormat 並量測 500 列首次呈現與切刻度耗時；TaskGantt.vitest.spec 補延遲回應、快速切刻度、平移失敗、任務修改後重抓案例
- o1b 審查留下的排程器建議：設定驗證 Lease > 2×Renew（目前只在設計補記寫 Renew<Lease，出廠值安全）；補「續租呼叫不理會取消」時排程迴圈卡在 finally 的測試與上限；JobLeaseFence.NowUtc 取送出時刻，註解改成實際保證；JobRunner.RenewRoundCompleted 改 internal
- 查 WebAPI 測試偶發 ObjectDisposedException（SQLitePCL.sqlite3 handle 已釋放）：2026-11-18 run 44234545443 的 TaskCommentAttachmentsApiTests.ClosedModule_StillDownloads 開連線時失敗、rerun 即過；疑與共用 DB 範本（0823c4ac）或 DemoDataApiTests 類別 fixture（7553f9b6）的釋放順序有關，再出現時記 run id
- 建 Playwright 主線 e2e 測試：簽核前端 a4b 完成後做，5–8 條（登入、建記錄、送簽、同意、任務留言），放進 gate 防回歸（2026-11-11 使用者裁決）
- 補事件設計器「轉回草稿前改鎖定欄位」的提示：a2d1 修正後的新問題 1，屬事件設計器，不在簽核設計器範圍
- 加收件人政策 0 項或 20 項以上的提示（a2d7 N-M4）：不影響簽核設計器畫面，之後在政策設定頁處理
- 讓簽核試算的發起人下拉可搜尋（a4a5 審查 S5）：現在列出全部使用者，人多時在手機上要捲很長；照記錄下拉加 use-input 與本地篩選
- 讓營運時區可設定（目前寫死 Asia/Taipei）：tz1 只統一了「無偏移＝台北牆上時間」的解析；BusinessClock.Offset、TaipeiBusinessTime，以及 Normalize、匯入（FieldImportParser.cs:99）、報表過濾（ReportRules.cs:239）補上的 "+08:00" 字面值仍寫死
- 補擋「改關卡代碼或分兩次改來源」清掉候選略過標記的路徑（a5d 複審 N4）：目前只比對上一版同代碼關卡；屬防誤操作，不是安全漏洞
- 改善切換來源類型後才出現的後端錯誤文案（a5d 複審 N6）：使用者看不出是略過標記規則擋下
- 讓簽核臨時讀取時參照欄位能顯示（a4b 既有）：manager01 臨時讀取採購單時「品項」顯示「無權限檢視」
- 查簽核臨時讀取時 schema 先回 403 再重抓成功（a4b 既有）：畫面最終正常，但多一次失敗請求
- 補 MessageText 四語（任務看板 kb1 審查 N1）：a3 合併後把 SupervisorOnlyAssignee（主管只能改負責人的 403 訊息）、UndoInvalid（復原憑證無效）加進 MessageText 表；kb 段不能碰與 a3 共用的 MessageText.cs
- 修 WriteGateTests.Waiters_AreServedInArrivalOrder 的不穩定：已兩次在 CI 偶發失敗
- 查其他端點以 403／404 區別洩漏記錄是否存在：history、upload、update、delete、print、關聯分頁、沖銷、關閉模組（schema 臨時讀取已改一律 403）
- 替「我已處理」查詢加 (ActorUserId, TaskId) 索引：目前缺索引，資料量大時會慢
- 補直接請求被隱藏負責人欄的負面測試（任務看板 kb2 審查 S2）
- 讓 `PATCH /tasks/{id}` 加版本檢查（甘特圖 gt 設計勘察）：`UpdateTaskHandler` 不收版本，任務詳細改任何欄位都是最後寫入者贏；甘特拖拉另開 `schedule` 繞過，任務詳細改日期仍無併發保護
- 查 Demo 已確認的採購單無法改成已取消（demo.md §15 步驟 7）：confirmed→cancelled 只限 manager 群組，必要時改 demo.md 的步驟
- 讓產品啟動時明確設 SQLite WAL：部署時若已有空的 .db 檔，EF Migrate 不會設 WAL，長交易期間其他請求會「database is locked」（demolock 根因）
- 查 AttachmentApiTests 偶發的 SQLite handle 已釋放（ObjectDisposedException）：fg2 各段 CI 前後出現過，與簽核無關，先找出是哪個測試結束後仍有請求在用連線
- 查簽核中心非系統管理員開啟「全部」分頁時 console 出現 403：畫面最終正常，疑似多送了一次無權限的探測請求；確認前端是否該先判斷身分再發請求
- 請母語者審簽核流程圖的泰文、越南文新文案：fg2a2 兩則流程圖定義訊息、fg2b 條件節點無法判斷、fg2d 四句（分支取消、時間軸取消原因）、fg2e 任一到齊註記、fg2f 四則併發訊息；現在只驗佔位符與沒有中文，措辭未經審
- 補 PostgreSQL 的並發真交錯驗證：fg2f、fg2g2 的併發測試標了 `Postgres` trait，但 TestApp 寫死 SQLite；pg0 加上 provider 切換後要實跑，並確認 `TryBumpRowVersionAsync` 在 READ COMMITTED 下等前一筆提交再回 0 列的行為（重試依賴它） ⛔ 等：pg0
- 讓 sequence 定義驗證擋掉夾帶的 `nodes`、`edges`（fg2c 審查 S5）：現在存得進去，`ApprovalDefinitionRefs.Steps` 會把夾帶的簽核節點算進去，夾帶自選節點的 sequence 流程在重新比對後會要求重新送出；正常資料不會發生
- 整理 `CloseLive` 的依賴方向與重複的 reason 字串（fg2c 審查 nit）：不影響行為
- 評估等價性測試的穩定代號（fg2g1 審查建議 3、4）：flowId、fieldId 換成穩定代號、NormalizeData 改有邊界的替換；現在只可能造成假紅，優先度低
- 簽核中心「已處理」分頁的並行名稱改成按模組批次判斷可見性（fg4a 審查建議）：現在每列最多多打 2 次 `ListVisibleIdsAsync`，size 100 時最壞多 200 次查詢；POC 規模可接受
- 請母語者審 fg4a 並行摘要的泰文、越南文（`approval.badge.pendingMany`、`approval.stagesMore`、`approval.addSignOf`）
- 加寬進度圖的邊線標籤欄距（fg3g）：排版參數 fg3b 已備好、設計頁已用；單據頁進度圖要到 fg3g 才傳標籤線
- 讓進度圖長名稱節點 hover 能看全名（可用 Quasar tooltip）：節點拿掉原生 title 以免讀屏重複念，名稱超過兩行會被截斷、畫面上看不到全名
- 在實體 iPad 驗進度圖：平板網址列收合時的視角、820 直向單指在圖上平移與在圖外的時間軸捲頁面、手機縮圖上往下捲頁面（目前只用 DevTools 觸控模擬與合成觸控事件驗過）
- 請母語者審 fg4 新文案的泰文、越南文：`approval.graph.*`（狀態字、匯合到齊方式、縮放按鈕、放大檢視、關閉、細節標題）
- 讓鍵盤 Tab 到畫面外的進度圖節點時視角跟著移動：長流程以 0.8 倍顯示時，焦點會落在看不到的節點上，視角不會自動平移
- fg3e 開工前問使用者：條件節點拉出的第一條線是否自動成為「其他（預設）」線（fg3b 現行實作是會；fg3b 審查 🟡6）
- 設計頁手機首屏聚焦前 3 個節點時左右各被切約 14px（minZoom 0.6 的副作用）：考慮改成 padding 或依寬度算 minZoom
- 設計頁返回鈕回到模組設計頁的預設分頁而非「簽核流程」分頁：分頁要先進網址（`?tab=`）才能用 router.back
- 實機驗唯讀設計者進設計頁（U62）：Demo 沒有只有 designer.read 的帳號，目前只有 spec 覆蓋
- 補 PostgreSQL 的草稿條件更新與 `IsVersionUsedAsync` 競爭（fg3a 審查 🟡4，與既有 PUT 同一種寫法） ⛔ 等：pg0
- 驗 render.yaml 的 buildFilter：第一次只改文件的 push 到 main 後，確認 Render 沒有重新部署
- 前端改動的驗收一律跑完整 `bun run test`（約 2 分鐘）：fg3c 只跑篩選範圍，漏掉 no-hardcoded-colors 守門，CI 才擋下
- 讓 DemoDataApiTests 只做一次完整產生：需要完整產生結果的唯讀案例（輪詢到完成、已產生再產生 409 等）共用一份 fixture；並發與身分隔離案例照舊獨立產生（使用者 2026-11-18 同意，接受案例間連帶紅的代價）。原註：單一類別要 12 分以上且只能序列跑，是 CI 最慢的一份（WebAPITests 1/4 約 14.7 分）
- 啟用 Demo 流程圖範例流程並實際送簽展示（使用者 11-16 裁決）：「採購單（流程圖範例）」改啟用；它目前優先序 0、未稅小計 ≥ 10000，啟用後會攔下 1 萬以上所有採購單，要重排 InventoryScenario 既有送簽情境（大額／一萬以上兩條流程的 Demo 單據），並加幾張走並行分支的送簽單讓進度圖看得到並行
- 實機看設計頁試算時，有警告的線是否保持警告色（只改了 CSS，未實機驗）
- 處理專案設定啟用但沒安裝的 dotnet-test plugin：每次 `claude -p` 開頭都印一行警告；裝上或從 .claude/settings.json 拿掉（lifecycle 3.0.0 試用，2026-11-15）
- 替 .claude/rules/docs.md:9 與 AGENTS.md:49 的清單腳本補退路：沒裝 lifecycle 時沒有 feature_checklist.py，規則又禁手改統計行，只能卡住（lifecycle 3.0.0 試用 S5，2026-11-15）
