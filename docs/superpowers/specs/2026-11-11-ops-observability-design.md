# 維運、觀測與告警派工 agent 設計文件

> 2026-11-11｜平台共用能力（非應用程式）｜前置：[政策設定](2026-11-11-policy-settings-design.md)、[任務管理](2026-11-10-task-management-design.md)、[路線](2026-11-06-erp-roadmap-design.md)｜相關：[MCP server 設計](2026-11-11-mcp-server-design.md)（mcp1 地基、mcpO1／mcpO2 維運工具）
> 狀態：草稿，主控已依 builder 審查修訂；C1–C7 已由使用者 2026-11-12 裁決（§16），其餘待使用者核准。內容是總體設計＋O1 細部設計（§10）＋O2 細部設計（§17，2026-11-19 主控撰寫，使用者行為類待確認見 §17.10）；這次只寫設計，不改程式；實作分 O1–O6（§9）。
> 標「（使用者裁決）」的是使用者 2026-11-11 拍板的；標「（主控判斷）」的是主控代為決定、可以推翻的，彙整在 §14。只有使用者能答的 C1–C7 已於 2026-11-12 全部裁決，紀錄在 §16；正文標「依 C? 使用者裁決（2026-11-12）」的段落已是定案。原標主控判斷、後經使用者 2026-11-12 確認的條目標「主控判斷，使用者 2026-11-12 確認」。

## 1. 目的與為什麼

Formwright 要賣成兩種版本：客戶自己裝的**地端安裝版**，和廠商代管的**雲端訂閱版**，比照 vtiger、Odoo（使用者裁決）。兩種版本都要有人知道「系統現在好不好、哪裡壞了、壞了誰去修」。

現況（2026-11-11 讀 `origin/main`）幾乎沒有維運能力：

- log 是 `Microsoft.Extensions.Logging` 預設的純文字 console（`appsettings.json:2-7`），沒有 request logging，回應裡雖然帶 `traceId`（`ServiceResultExtensions.cs:21`、`ConcurrencyExceptionHandler.cs:15`），log 裡卻查不到同一個值。
- `/api/health` 只回 `ok`，不查資料庫（`Program.cs:136`）；沒有版本端點。
- 前端沒有 `app.config.errorHandler`、`window.onerror`、`unhandledrejection`，使用者畫面上的錯誤廠商完全看不到（`services/api/http.ts:44-75` 只處理 API 回應）。
- 沒有背景排程器：任務到期提醒與 90 天通知清理，要等使用者打開鈴鐺才惰性觸發（`NotificationHandlers.cs:47-59`）。使用者不登入，提醒就不產生。
- 稽核只有記錄欄位歷程（`RecordHistory`）；設計、權限、群組、系統設定、登入都沒有稽核。登入限流只在記憶體（`LoginThrottle.cs`）。
- 沒有 email、webhook、監控頁、備份（備份在路線階段 5）、CI。

客戶自訂規則是 Formwright 特有的風險來源：公式、事件、檢查、任務規則都是客戶定義的資料，出錯時要分得清是「客戶設定問題」還是「產品缺陷」，否則客戶的錯會淹沒廠商、廠商的錯會被當成客戶的錯。

這份文件把維運拆成三層（§4），先做產品內建的觀測地基 O1，再一路做到 agent 自動診斷與開修正 PR。

## 2. 目標、不做、反目標

**目標**

- 兩版共用一套產品內建維運：客戶 IT 在系統裡就能看到健康狀態、事件與稽核，不必另裝監控。
- 廠商有一個獨立於產品的維運中心：雲端版全量收事件並每分鐘外部探測；地端版收選擇加入的匿名錯誤特徵。
- 事件依歸屬自動分流給對的人：客戶設定→客戶管理員與設計者；產品缺陷→廠商 agent；基礎設施→雲端版廠商、地端版客戶 IT。
- agent 自動做唯讀診斷，能修的開 PR，由使用者核准合併；可逆的小處置在雲端版可以自動做。
- 所有門檻、間隔、保留天數走政策設定（[政策設定](2026-11-11-policy-settings-design.md) §3、§4），出廠值給在 §11。
- 所有設計都 PostgreSQL 相容（正式版資料庫，使用者說明），並寫出 PostgreSQL 與 SQLite 各自的做法（§8）。

**不做（這一輪）**

- 不做完整 APM、分散式追蹤後台、log 搜尋後台：產品只負責產生結構化資料，要看時可接外部平台（§10.2）。
- 雲端版正式主機與正式告警服務的選型：研發階段只用免費服務，正式主機屆時另議（使用者說明）。
- 不做多租戶：雲端版是每個客戶一套獨立實例（路線「這版不做」）。
- 備份還原的實作仍在路線階段 5；本文件只規定備份的位置與權限（§6），以及健康檢查要看備份新鮮度。
- 業務通知的 email（任務、簽核）仍不做；O2 的告警通道只送維運事件（§14 判斷 U9）。

**反目標**

- 不准用被監控的同一套產品監控自己：維運中心不是 Formwright，兩者同時故障時告警照樣送得出去（使用者裁決）。
- 不准讓 agent 直接動正式資料或正式部署：agent 只能讀、只能開 PR；可逆處置由維運中心照 runbook 呼叫產品 MCP 的維運工具（§6；L2 由誰執行依 C4 使用者裁決（2026-11-12））。
- 不准另做一套維運專用的處置 API：診斷與處置都用產品 MCP server 的工具，權限、權杖、稽核與 MCP 共用（使用者裁決）。
- 不准把客戶業務資料送出客戶環境：地端匿名回報只送錯誤特徵、版本、安裝識別碼（使用者裁決，欄位白名單見 §4.2）。雲端版送到維運中心與 agent 的內容，依 C2 使用者裁決（2026-11-12）：只送正規化後的內容，原始訊息留在產品內（§4.1）。
- 不准自動暫停會造成資料不一致的規則：暫停只用在略過也不會讓帳對不上的規則（§5.4）。
- 不准讓維運功能拖垮業務請求：觀測、回報、排程都有寫死的技術上限（§12）。

## 3. 使用者裁決

2026-11-11 拍板，照寫、不重開：

1. 服務對象兩者都要，先做廠商集中維運（跨客戶事件中心＋agent 派工）；客戶 IT 用系統內建觀測與監控頁。
2. agent 先跑在使用者的 Mac，用訂閱（常駐拉取程式＋`claude -p`）；介面保留日後換 GitHub Actions。
3. 不把 Mac 當資料收集中心：Mac 不存事件與客戶資料。
4. 三層大方向（主控重評、使用者核准）：(A) 產品內建維運，兩版共用，客戶 IT 用；(B) 廠商維運中心獨立於產品，用 Cloudflare Workers＋D1 免費層：雲端版全量事件＋探測，地端版選擇加入的匿名錯誤回報；(C) agent：雲端 L1/L2/L3，地端只 L1＋L3（修正隨版本發佈）。監控不可用同一套產品監控自己。
   - 註：主控提議把 L2 的執行者改成維運中心的 runbook、agent 只能建議（§14 T7）。這項修改已由使用者 2026-11-12 確認（C4 選項 1）。
5. 事件分流：客戶設定問題→該客戶管理員／設計者；產品缺陷→廠商 agent；基礎設施→雲端版廠商、地端版客戶 IT。
6. 分級：L1 自動唯讀診斷；L2 有界可逆自動處置，只在雲端版；L3 agent 修正 PR 先人核准，累積成功紀錄後可在設定把某類告警改成 gate 過就自動合併（分級可設定）；L4 只通知。資料修復、還原、安全事件一律只通知。
   - 註：L2 的處置介面就是產品 MCP 的工具（第 13 條）。初稿這裡另寫「只呼叫產品的處置介面、用 scoped token」並標為裁決，checkpoint 查不到出處，改標主控判斷（§14.3 B31）；主控判斷，使用者 2026-11-12 確認。
7. 地端版匿名錯誤回報：安裝時詢問、預設勾選、可取消；系統設定可隨時關閉，並可檢視最近送出的內容。同一條連線兼做新版本通知。
8. 客戶規則失控（連續失敗、互相觸發迴圈）超過門檻自動暫停，通知設計者與管理員；門檻走政策設定，修好後一鍵恢復。（「設計者」怎麼找、哪些規則能暫停、迴圈偵測的對象，見 §5.4，依 C3 使用者裁決。）
9. 排程：O1 排在政策 p1 之後、簽核 a2d 之前；O2 以後視情況穿插。整體順序 p1 → O1 → pg0 → 簽核 a2d–a6 → O2 → MCP 地基 mcp1 → O3…。
   - 註：初稿另寫的「O3 → O4 → O5 → O6」「O3 可視情況提前」「a2c 的位置」「p2、p3 的位置」都是主控判斷（主控判斷，使用者 2026-11-12 確認），見 §9。
10. 正式版資料庫是 PostgreSQL，SQLite 只是研發；現在設計與實作就要考量 PostgreSQL 相容。馬上立規則：新程式不寫 SQLite 專屬寫法。O1 之後做一段 pg0（provider 抽象＋可在 PostgreSQL 跑的測試環境），之後的功能兩種資料庫都驗；其餘改寫排在簽核之後。
    - 規則已立：`.claude/rules/backend.md` 的「PostgreSQL 相容」條（commit `1c1ea8ae`，本分支已含）。
11. 只用免費服務是研發階段的限制；雲端訂閱版正式主機屆時另議，不受免費限制。
12. 寫總體設計＋O1 細部設計；其餘細節主控照推薦決定、標為主控判斷，送審後給使用者看，並加進功能清單。
13. 產品會做 MCP server，讓 AI agents 接上產品功能（另寫設計文件）。共用地基 mcp1：個人存取權杖（範圍、到期日、可撤銷）、稽核、工具分唯讀／一般寫入／高風險三級，高風險先回預演、拿確認碼再送才執行；權限沿用使用者本身權限。維運 O4（L1 診斷）與 O6（L2 處置）直接用 MCP 工具，不另做處置 API；O2 的稽核表要設計成 MCP 也能共用。

## 4. 三層架構

```
 客戶環境（雲端版：廠商代管一客戶一套；地端版：客戶自管）
 +-------------------------------------------------------------
 | A 層　Formwright 產品內建維運（兩版共用）
 |   結構化 log、request id、health live/ready、版本端點
 |   背景排程器 → 健康檢查 → 維運事件表 → 監控頁、告警通道
 |   規則失控自動暫停、稽核表（與 MCP 共用）
 |   產品 MCP server：mcp1 提供地基與確認碼；維運唯讀工具在 mcpO1（隨 O4）、
 |   維運處置工具在 mcpO2（隨 O6，高風險，只雲端版開放給廠商）
 +-----+-------------------------------------------^-----------
       | 雲端版：全量維運事件（HTTPS 推送）          | 雲端版：MCP 工具呼叫（維運服務帳號的權杖）
       | 地端版：匿名錯誤特徵（選擇加入）            | 兩版：新版本資訊
       v                                           |
 +-------------------------------------------------------------
 | B 層　廠商維運中心（Cloudflare Workers＋D1，不是 Formwright）
 |   收件、去重、歸屬分流、派工佇列、L2 runbook、稽核
 |   MCP 轉送：依分級過濾工具、套冷卻與上限，產品權杖只存在這裡
 |   告警：Email API、廠商手機推播（Telegram）
 |   cron 每分鐘外部探測雲端版 /api/health/ready
 +-----+-------------------------------------------^-----------
       | agent 主動領工（拉，不推）                 | 診斷結果、PR 連結
       | agent 經轉送呼叫 MCP 唯讀工具（L1）        |
       v                                           |
 +-------------------------------------------------------------
 | C 層　agent（使用者 Mac，開機時常駐；日後可換 GitHub Actions）
 |   claude -p（訂閱）、暫存 worktree、做完即刪，不留資料
 +-----+-------------------------------------------------------
       | L3：推分支、開 PR
       v
   公開 repo → CI gate → 使用者核准合併 → 雲端版部署／地端版隨下一版發佈
```

### 4.1 A 層：產品內建維運

兩版完全同一份程式。差別只在設定：雲端版開啟「事件推送到維運中心」，並建立一個給維運中心用的維運服務帳號與 MCP 權杖；地端版開啟「匿名錯誤回報」（安裝時詢問）。

**版別部署設定**（主控判斷，本文件是唯一定義處，MCP 設計引用這裡）：

- 部署設定 `Ops:Edition`＝`Cloud` 或 `OnPrem`，出廠 `OnPrem`。由部署環境決定，不是業務政策，所以不進政策目錄。
- 依版別生效的東西：

| 依版別生效 | `Cloud` | `OnPrem` |
|---|---|---|
| 事件推送到維運中心（O3） | 有 | 無 |
| 匿名錯誤回報、新版本通知（O3） | 無 | 有（政策可關） |
| 維運服務帳號（mcpO1） | 可建立 | 不建立，維運中心不連入 |
| `ops.remoteDiagnosticsEnabled`、`ops.remoteActionsEnabled` | 有效 | 無效（政策頁不顯示） |

- 版別判斷是寫死的安全檢查（§12）：`OnPrem` 時即使資料庫裡有維運服務帳號的列，認證也一律拒絕。

維運用的 MCP 工具（工具清單與分級機制以 MCP 設計文件為準，這裡只列維運需要的）：

- **維運唯讀工具**（mcpO1，隨 O4；L1 用）：讀健康檢查結果、維運事件與其樣本、排程器狀態、版本與執行環境、規則失控與暫停紀錄。只碰維運資料，不讀業務記錄。
  - 回傳內容依 C2 使用者裁決（2026-11-12）：事件與樣本只給正規化後的訊息（與 §4.2 同一套正規化），另加類別、歸屬、moduleKey、ruleId、request id；原始例外訊息、前端 stack 原文只留在產品 log 與產品內的事件樣本，廠商要看就由人登入該實例。
  - 讀稽核的工具也歸 `ops.read` 範圍，但工具內另加「只限系統管理員」的檢查，維運服務帳號不能用（主控判斷 B28）。
- **維運處置工具**（mcpO2，隨 O6；L2 用，歸高風險）：§14 判斷 U2 的五個動作。依 mcp1 的高風險流程先回預演、拿確認碼再送才執行。
- **維運服務帳號**（實作在 mcpO1，隨 O4；形狀見 MCP 設計 §5.6）：只授予維運相關的權限，沒有業務模組的讀寫權；MCP 權限沿用帳號本身權限（使用者裁決），所以 agent 即使被誘導也讀不到客戶業務資料。
  - 與「AI 連線」總開關 `mcp.enabled`、限定群組 `mcp.allowedGroups` 的關係，依 C1 使用者裁決（2026-11-12）：服務帳號不受這兩項約束，改受政策 `ops.remoteDiagnosticsEnabled`（允許廠商遠端維運，唯讀；只雲端版有效，出廠是）與權杖撤銷約束；處置另受 `ops.remoteActionsEnabled` 約束。
  - 權杖怎麼核發與輪替，依 C6 使用者裁決（2026-11-12）：由廠商部署流程注入（MCP 設計 §5.6）。

客戶 IT 與系統管理員在產品裡看到的東西（O2 起）：

- 監控頁：健康檢查目前狀態與趨勢、未結事件、排程器狀態、版本。
- 維運事件：依歸屬標示「需要你處理」或「已回報廠商」。
- 稽核：誰在什麼時候改了設計、權限、群組、系統設定與政策、使用者，以及登入成功與失敗。
- 告警通道：地端版用客戶自己的 SMTP；雲端版由維運中心代寄（§14 判斷 U1）。

### 4.2 B 層：廠商維運中心

獨立於產品的理由（使用者裁決）：監控者和被監控者不能同時故障。若用 Formwright 自己監控 Formwright，資料庫掛了時事件表也寫不進去、告警也送不出去。

研發階段用 Cloudflare 免費層（2026-11-11 研究 subagent 讀官方文件；builder 審查時數字與所知一致，但沒有重讀）：Workers 每日 10 萬請求、每請求與 cron 10ms CPU、每請求 50 個 subrequest、帳號 5 個 cron trigger；D1 5GB、每日讀 500 萬列、寫 10 萬列；超額只停不扣款。

- ⚠️ 未查證：cron 執行算不算進每日請求數。O3 開工前查官方文件或實測。
- 額度是整個 Cloudflare 帳號共用，和使用者其他免費站共用；被灌爆時連探測與告警都停，風險與對策見 §15 第 12 條（主控判斷 B21）。

維運中心的實作方式（主控判斷，O3 細部設計時可調）：

- **程式放哪**（B27）：同 repo 的 `ops-center/`；廠商網域、Telegram chat id 等一律放 wrangler secrets，不進 repo。使用者若要閉源，再搬去私有 repo。
- **產品推事件的認證**（B23）：每個雲端實例一把推送金鑰，放部署 secret；推送請求以 HMAC 簽章，維運中心驗簽與時間戳後才收。
- **匿名收件獨立**（B21）：地端匿名回報與新版本查詢用獨立的 Worker 和獨立的 D1，依來源 IP 與安裝識別碼限流；被灌爆時只停匿名收件，不拖垮雲端版的事件收件、探測與告警。
- **看門狗**（B22）：維運中心每日送一則心跳推播，另用外部免費探測打維運中心的健康端點；否則中心掛了沒人知道。

容量推算（主控判斷，O3 要實測）：

- 每分鐘探測一次一天 1440 次 cron。一次 cron 最多 50 個 subrequest，扣掉寫回與告警，單一 cron 約可探 40 個實例；超過就分批到第二個 cron。
- D1 寫入是瓶頸：每個實例每分鐘寫一列，一天 1440 列，10 萬列約只撐 69 個實例。所以探測結果**只在狀態改變時寫一列**，另外每 5 分鐘寫一列彙總（一天 288 列），約可撐 300 個實例。
- 研發期的客戶數遠低於這個量；正式營運時這一層一起另議。

地端版匿名回報的欄位白名單（寫死，見 §12；使用者裁決只送錯誤特徵、版本、安裝識別碼）：

| 欄位 | 內容 |
|---|---|
| 安裝識別碼 | 安裝時隨機產生的 UUID，不從硬體或網路資訊推導 |
| 產品版本 | 版本號與 commit |
| 執行環境 | 資料庫種類（PostgreSQL／SQLite）、作業系統家族 |
| 錯誤特徵 | 指紋、例外型別、正規化後的訊息、產品程式碼的前幾個呼叫框（不含行號以外的值） |
| 統計 | 期間內次數、首次與最近時間 |

正規化的訊息會把數字、GUID、引號內字串、Email 換成佔位符（§10.2.6），所以記錄值、人名、模組名稱都不會出去。系統設定頁可以看到最近送出的每一則原文（使用者裁決）。

新版本通知走同一條連線：維運中心回應最新版本號與是否含安全修正。關掉錯誤回報後，新版本查詢仍可單獨保留，但只送版本號、不送安裝識別碼（§14 判斷 U6）。

### 4.3 C 層：agent

- Mac 開機登入後由 launchd 啟動一個常駐的拉取程式，定期向維運中心領工（主動拉，不開對外埠）。
- 領到一件工作：在暫存目錄開公開 repo 的 worktree，把事件內容寫成檔案交給 `claude -p` 處理，結果回傳維運中心，然後刪掉暫存目錄。Mac 不留事件與客戶資料（使用者裁決）。
- 隔離方式依 C5 使用者裁決（2026-11-12）：
  - 拉取程式以一個專用的 macOS 標準使用者執行（該使用者的 launchd agent），裡面不放任何個人憑證（ssh 金鑰、雲端憑證、瀏覽器登入）。
  - `claude -p` 開 sandbox；設定目錄（`CLAUDE_CONFIG_DIR`）指到這件工作的暫存目錄，做完連同 session transcript 一起刪。初稿沒算到：`claude -p` 預設把 transcript 存在 `~/.claude/projects/`，刪掉 worktree 也還在，會違反「Mac 不存事件與客戶資料」。
  - L1 的 Bash 只允許 `dotnet test`、`bun run test`、`git` 的指令前綴。
  - ⚠️ 未查證：`CLAUDE_CONFIG_DIR` 能否完整搬走 transcript、sandbox 在 `claude -p` 的行為；O4 計畫的第一步實測。
- 雲端版的 L1 要看即時狀態時，agent 經維運中心的 MCP 轉送點呼叫產品的維運唯讀工具（主控判斷 T7）。agent 只持有一把維運中心發的 agent token；各雲端實例的 MCP 權杖只存在維運中心，由轉送點依這件工作的分級過濾可用工具。
- 轉送點做成 stateless 的 Streamable HTTP 代理（主控判斷 B24）：對 `claude -p` 是 MCP server，對產品是 MCP client；換掉 `Authorization` 標頭、過濾工具清單與呼叫，不用 Durable Objects。agent token 不會轉給產品（MCP 設計 §17 第 5 點）。
- 地端版沒有連入管道，L1 只能依匿名特徵、版本與 repo 程式碼診斷。
- 領工、回報的介面與執行環境無關，日後換成 GitHub Actions 只換「誰來跑 `claude`」（使用者裁決）。
- Mac 關機時工作留在維運中心佇列，不遺失；L4 通知與維運中心的告警不受影響，因為它們不經過 agent。

### 4.4 一個事件走完的流程

1. 產品偵測到異常（例外、健康檢查失敗、規則連續失敗、前端錯誤），算出指紋，寫進維運事件表；同指紋未結時只加次數（§5.5）。
2. 依類別決定歸屬（§5.1），產品內先通知該通知的人。
3. 雲端版把事件推到維運中心；地端版有開回報時，把「產品缺陷」類的匿名特徵批次送出。
4. 維運中心再去重、依類別與分級建立派工：L4 只通知；L2 照 runbook 呼叫 MCP 維運處置工具（預演→比對→確認；依 C4 使用者裁決（2026-11-12））；L1、L3 進 agent 佇列。
5. agent 領工，L1 經轉送點讀 MCP 維運唯讀工具並讀程式，回傳診斷；判斷可修時升 L3，開 PR。
6. CI gate 通過後通知使用者核准；合併後雲端版部署，地端版隨下一版發佈。
7. 產品端同指紋事件不再出現一段時間就自動結案；之後又出現就重開並標「回歸」。

## 5. 事件分類、歸屬與分級

### 5.1 類別與分流（使用者裁決的三類，加上安全）

| 類別 | 例子 | 產品內通知 | 雲端版 | 地端版 |
|---|---|---|---|---|
| 客戶設定 | 公式算不出來、事件串聯超過深度、檢查持續擋存檔、簽核找不到核准人、任務規則失敗、報表查詢超過上限 | 政策 `ops.configIssueRecipients`（出廠：該規則最後修改者＋系統管理員） | 廠商看得到，不派 agent | 不送出 |
| 產品缺陷 | 未處理例外（500）、前端 JS 錯誤、啟動時完整性檢查不符、migration 失敗 | 系統管理員（只告知已回報） | 派 agent | 有開回報才送匿名特徵 |
| 基礎設施 | 資料庫連不上、磁碟將滿、備份過期、外部探測失敗、回應變慢 | 系統管理員 | 廠商處理 | 客戶 IT 處理 |
| 安全 | 大量登入失敗、管理員權限異動、MCP 權杖驗證失敗或越權呼叫 | 系統管理員 | 廠商＋客戶管理員，一律 L4 | 客戶 IT，一律 L4 |

系統沒有「單一模組的設計者」這個角色（政策設定 §3.2），所以初稿寫的「通知該模組設計者」改成政策（鐵則 2；主控判斷 B14）：`ops.configIssueRecipients`，型別比照 A11、A13 的收件人設定，多一個動態對象「該規則的最後修改者」，出廠值「該規則的最後修改者＋全部系統管理員」（§11）。

歸屬判斷（主控判斷 U7）：

- 例外發生時的 log scope 帶有模組與規則識別（§10.2.1），且錯誤屬於規則引擎已知的錯誤碼（例如串聯過深、公式錯誤）→ 客戶設定。
- 其他未預期的例外 → 產品缺陷。判斷不了時一律當產品缺陷：寧可讓廠商多看一件，不讓產品缺陷被當成客戶的錯藏起來。

### 5.2 嚴重度

沿用參考專案的三級 OK／WARNING／CRITICAL 給健康檢查，事件本身用三級：

- **資訊**：只記錄，不通知。
- **警告**：通知，不催。
- **嚴重**：通知，並依 `ops.incidentRenotifyHours` 重複提醒直到結案或有人認領。

### 5.3 自動化分級（使用者裁決）

| 級 | 做什麼 | 誰執行 | 適用 |
|---|---|---|---|
| L1 | 自動唯讀診斷：讀程式、讀事件（雲端版經 MCP 維運唯讀工具）、在暫存 worktree 跑相關測試重現 | agent | 產品缺陷（兩版） |
| L2 | 有界可逆自動處置（初始清單見 §14 判斷 U2） | 維運中心 runbook 呼叫 MCP 維運處置工具（依 C4 使用者裁決（2026-11-12）；原裁決為 agent） | 只雲端版 |
| L3 | 開修正 PR → CI gate → 使用者核准合併；日後可依告警類型放行自動合併 | agent 開 PR，使用者合併 | 產品缺陷（兩版；地端隨版本發佈） |
| L4 | 只通知，人主導 | 人 | 資料修復、還原、安全事件一律；其餘無 runbook 也無法修的 |

每一種事件類型的預設分級登記在維運中心的「告警類型表」，可在維運中心設定調整；但 L4 的三類（資料修復、還原、安全）不能改成其他級（寫死，§12）。

### 5.4 客戶規則失控自動暫停（使用者裁決，O2 實作）

本節的範圍、門檻與迴圈偵測，依 C3 使用者裁決（2026-11-12）。

先釐清現況（初稿寫錯，已更正）：任務規則**不是**非同交易。`TaskRuleStage` 是 `IWriteStage`（Order 12），在存檔交易內、事件之後執行（`TaskRuleStage.cs:20-26`；[任務設計](2026-11-10-task-management-design.md) §5.4：「規則在記錄寫入的同一個交易內執行」）；任務規則失敗時整筆存檔失敗（例如回 Conflict）。所以「任務規則連續失敗」對使用者來說就是「連續存不了檔」。

偵測：

- **連續失敗**：同一條規則在 `ops.rulePauseWindowMinutes` 內連續失敗達 `ops.rulePauseConsecutiveFailures` 次，中間沒有任何一次成功。建議出廠「60 分鐘內連續 5 次」（初稿「10 分鐘內 20 次」對 50 人以下的公司幾乎不會觸發，使用者會一直卡著）。
- **失敗計數寫在業務交易之外**：存檔交易失敗會回滾，交易內寫的計數也會跟著消失；PostgreSQL 的交易失敗後整個交易都不能再用。所以計數在業務交易回滾之後，另開一個獨立的 DI scope 與交易寫入；這筆寫入失敗只記 Error log，不影響回給使用者的錯誤。
- **迴圈偵測目前沒有對象**：任務規則產生的任務不會再觸發規則（任務不是動態記錄，任務設計 §5.4）；事件的循環在設計期擋下（`EventValidator.cs:67`），執行期有串聯深度上限（`EventStage.cs:23`）。唯一可能的來源是外部反覆寫同一筆記錄，例如 MCP mcp2a 上線後的 AI 寫入迴圈、整合。建議迴圈偵測延到 mcp2a 再做，對象改成「同一權杖或同一來源在時間窗內反覆寫同一筆記錄」；O2 不做，`ops.ruleLoopTriggersPerRecord` 也延到 mcp2a 登記。

處理：

- 把該規則標成「自動暫停」（與客戶手動停用分開記，恢復時不會打開客戶自己停用的；比照整合層 `IntegrationSuspendedItems` 的做法，`IntegrationModels.cs:36-49`）。
- 通知 `ops.configIssueRecipients` 的收件人（出廠：該規則的最後修改者＋系統管理員；§5.1），訊息寫出規則、模組、最近一次錯誤、暫停時間。
- 修好後在規則頁按「恢復」，一鍵回到啟用。
- 暫停與恢復都寫稽核。

可以自動暫停的範圍（主控判斷 U5，理由已更正）：

- **可以**：任務規則，以及日後非同交易的規則（排程型、通知型）。任務規則失敗會擋存檔；暫停後存檔照常、只少產生任務，帳不會錯。
- **不可以**：同交易的事件與檢查（`EventStage`、`CheckService`）。它們失敗時整筆存檔回滾，資料本來就不會壞；若自動暫停，存檔會在少了扣庫存、過帳等副作用的情況下成功，帳就對不上了。這類只開「客戶設定」事件並通知，不暫停。

### 5.5 事件去重指紋

指紋規則見 §10.2.6（O1 就實作，供 log 與 O2 共用）。O2 的行為：

- 同指紋有未結事件時，不開新事件，只更新次數、最近時間、最近版本。
- 已結案的同指紋再出現：重開原事件並標「回歸」。雲端版是否提高一級嚴重度是維運中心的設定「回歸時提高一級」，出廠開（主控判斷 B33；初稿寫死、沒列清單，違反鐵則 2）。
- 健康檢查類事件以檢查代碼為指紋，一個檢查同時只有一件未結；嚴重度隨檢查結果升降。
- 連續 `ops.incidentAutoResolveHours` 沒有再出現的缺陷類事件自動結案；健康檢查類在檢查回到 OK 時結案。

### 5.6 稽核表（O2 建，與 MCP 共用）

O2 建一張全平台共用的稽核表，畫面操作、MCP 工具呼叫、排程工作、維運中心的處置都寫同一張（使用者裁決：要讓 MCP 也能共用）。O2 一次把 MCP 需要的欄位建好（MCP 設計 §8.1、§17 第 3 點），mcp1 只加寫入點，不另開表、不另加 migration（主控判斷 B10）。

每列的內容（欄位命名留給 O2 細部設計；兩份文件的列舉值以這張表為準）：

| 內容 | 說明 |
|---|---|
| 時間 | UTC |
| 行為者 | 使用者 id；系統工作時為空 |
| 行為者種類 | 人員、維運服務帳號、系統（獨立欄位；系統身分用保留的種類，不建 AppUser 列，B3） |
| 來源管道 | 畫面、MCP、排程工作、維運中心 |
| 權杖 | MCP 個人存取權杖的 id（不存權杖本身），畫面操作為空；mcp4 之後也可能是 OAuth 授權 id |
| 動作代碼 | 例如 `permission.update`、`policy.set`、`login.failed`、`ops.walCheckpoint`、`mcp.query_records` |
| 風險級別 | 唯讀、一般寫入、高風險（沿用 MCP 的三級；畫面操作也照同一份分級標，B13） |
| 目標 | 目標種類與 id（模組、群組、使用者、政策代碼、工作代碼） |
| 參數雜湊 | MCP 呼叫的參數雜湊（獨立欄位，供篩選與比對） |
| 回傳筆數 | 查詢類的回傳筆數（獨立欄位，供計數） |
| 摘要與差異 | 舊值與新值；密碼、權杖、密鑰一律遮蔽。MCP 其餘項目放這裡的結構化摘要：參數摘要、前 20 個記錄 id、client 自報名稱與版本、耗時、operation id、轉送點的工作編號 |
| 預演關聯 | 高風險動作的確認 id 與結果：預演、已確認、逾時未確認、不符 |
| 結果 | 成功、被拒（含原因代碼）、驗證錯誤、失敗 |
| 關聯 | request id、來源 IP |

- **只能新增**，不能修改或刪除；超過 `ops.auditRetentionDays` 由排程工作清掉。「逾時未確認」另寫一列新紀錄、以確認 id 關聯，不回頭改預演那一列（B11）。
- **寫入介面要能在 `IWriteStage` 裡呼叫**，和業務寫入同一個交易（B12）：
  - 畫面的管理類變更（權限、群組、政策）照同交易寫，稽核寫不進去就整筆失敗。
  - 登入成功與失敗的稽核寫不進去時不擋登入，只記 Error log。
  - MCP 照 MCP 設計 T14 fail closed：寫不進稽核就不回資料。
- **畫面操作的風險級別**（B13）：Application 放一份「動作代碼 → 級別」的靜態對照，和 MCP 的工具登記放在一起，兩邊共用。
- MCP 的唯讀呼叫逐筆記（MCP 設計 U10）。
- 讀稽核本身也是 MCP 唯讀工具之一（歸 `ops.read`），權限比照稽核頁（系統管理員），維運服務帳號不能用（B28）。

## 6. 護欄

業界共識是護欄放在 agent 外面，不靠 prompt 約束（checkpoint 研究：HolmesGPT、incident.io、Copilot coding agent 都是唯讀加開 PR，不自動 merge）。

| 護欄 | 放在哪 | 內容 |
|---|---|---|
| GitHub 身分 | GitHub | agent 用一個免費的 GitHub App 當身分（PR 作者若是使用者本人，就沒辦法自己核准）；用 repository rulesets 限制它只能推 `agent/**` 分支與開 PR，不能推 main、不能改 rulesets 與 branch protection（B26） |
| 產品權杖 | 產品 MCP、維運中心 | 維運中心持有兩把維運服務帳號的權杖：一把只有 `ops.read`（mcpO1 起）、一把是 `ops.read`＋`ops.act`（mcpO2 起）；L1 轉送只用前者，轉送點的過濾寫錯時產品端還擋得住（MCP 設計 §17 第 5 點）。權杖有到期日、可撤銷，只存在維運中心；agent 不持有任何產品權杖 |
| 工具白名單 | agent 拉取程式＋維運中心 MCP 轉送點 | `claude -p` 只開讀檔、搜尋、在 worktree 內跑測試與 git；L1 不開寫檔；不開一般網路工具。轉送點只轉這件工作分級允許的 MCP 工具，L1 只有唯讀工具，agent 永遠拿不到處置工具 |
| Mac 本機隔離 | Mac | 依 C5 使用者裁決（2026-11-12）：專用 macOS 標準使用者、不放個人憑證、`claude -p` 開 sandbox、設定目錄與 transcript 放暫存目錄做完即刪、Bash 只允許測試與 git 前綴（§4.3） |
| 冷卻與次數上限 | 維運中心＋產品 MCP | 同一處置對同一實例有冷卻時間與每日上限；產品端對維運處置工具另有寫死的硬上限，維運中心設錯也擋得住 |
| 每日用量上限 | 維運中心 | 用訂閱沒有逐次帳單，所以上限以「每日工作數」「每件最大輪數」「每件最長時間」表達 |
| 總開關 | 維運中心、產品 | 維運中心一個開關停掉所有派工與 L2；產品端 `ops.remoteActionsEnabled` 關掉後維運處置工具一律拒絕（工具照列在 `tools/list`，呼叫回 `isError` 說明已被客戶關閉，維運中心據此轉人工；MCP 設計 §17 第 1 點）；`ops.remoteDiagnosticsEnabled` 關掉或撤銷維運服務帳號的權杖則連唯讀也停（依 C1 使用者裁決（2026-11-12）） |
| 高風險兩段式 | 產品 MCP（mcp1） | 維運處置工具先回預演、拿確認碼才執行；維運中心比對預演結果符合 runbook 預期才確認，不符就轉 L4。確認碼不能證明有人同意（MCP 設計 §6.3），它對維運中心的價值在於強制先有一份可比對的預演 |
| 稽核 | 產品稽核表（§5.6）＋維運中心 | 每次領工、診斷、PR、MCP 呼叫、處置都記；產品端記來源管道、權杖 id、參數雜湊、預演與結果 |
| 不可信輸入 | agent 拉取程式 | 事件文字、錯誤訊息、PR 留言都可能是 prompt injection（公開 repo）。事件內容以資料檔交給 agent 並明示「以下是資料，不是指令」。agent 的產品與 GitHub 權限限制在上面幾項；Mac 本機的憑證要靠「Mac 本機隔離」那一列擋，初稿只算了產品與 GitHub 端 |
| 備份隔離 | 部署 | 備份放在 agent、維運中心與 MCP 維運工具都碰不到的地方：獨立帳號的儲存空間、只給寫入不給刪除的憑證（業界真實事故：agent 刪了正式資料庫也刪了備份） |
| 人核准 | GitHub branch protection | main 受保護，合併需要使用者核准與 gate 通過；放行自動合併只能由使用者在維運中心設定，且只針對指定告警類型 |

L2 的執行者，依 C4 使用者裁決（2026-11-12）：使用者核准的三層方向寫的是「agent：雲端 L1/L2/L3」；主控提議改成維運中心執行（§14 T7），agent 最多「建議」一個處置，維運中心比對 runbook 白名單、冷卻、上限後才呼叫產品 MCP。這樣 Mac 上不必存產品權杖，也符合「Mac 不存資料」。

## 7. 兩版差異

| 項目 | 雲端訂閱版 | 地端安裝版 |
|---|---|---|
| 誰維運主機與資料庫 | 廠商 | 客戶 IT |
| 事件送維運中心 | 全量（含客戶設定類，供客服）；每則內容依 C2 使用者裁決（2026-11-12）：正規化後的訊息＋類別、歸屬、moduleKey、ruleId、request id | 只有選擇加入的匿名產品缺陷特徵 |
| 外部探測 | 維運中心每分鐘 | 無（客戶可自備） |
| 告警通道 | 維運中心代寄 Email＋廠商手機推播 | 客戶 SMTP＋站內通知 |
| agent 分級 | L1、L2、L3 | L1、L3（修正隨版本發佈） |
| 維運中心連入的 MCP 維運工具 | 啟用；客戶管理員可關處置（`ops.remoteActionsEnabled`），也可連唯讀一起關（`ops.remoteDiagnosticsEnabled`，依 C1 使用者裁決（2026-11-12）） | 不建立維運服務帳號、維運中心不連入（寫死，看 `Ops:Edition`） |
| 新版本通知 | 不需要（廠商部署） | 有 |
| 備份 | 廠商負責，存放與產品隔離 | 客戶負責；產品提供備份工具與新鮮度檢查（階段 5） |

各開關組合下誰負責（docs 規則要求）：

| 組合 | 產品缺陷誰知道 | 基礎設施誰處理 | 客戶設定誰處理 |
|---|---|---|---|
| 雲端版 | 廠商（全量）＋agent | 廠商 | 客戶設計者與管理員，廠商可看 |
| 雲端版、客戶關閉維運處置工具 | 同上，L2 改為通知廠商人工處理 | 廠商 | 同上 |
| 雲端版、客戶關閉遠端維運（唯讀）或撤銷服務帳號權杖（依 C1 使用者裁決（2026-11-12）） | 廠商仍收到推送的事件；L1 只能靠事件與 repo 程式碼，L2 停 | 廠商，需要看即時狀態時由人登入或請客戶重新開啟 | 同上 |
| 雲端版、客戶關閉 AI 連線（`mcp.enabled` 否） | 不受影響：服務帳號不受 `mcp.enabled` 約束（依 C1 使用者裁決（2026-11-12）） | 廠商 | 同上 |
| 地端版、開匿名回報 | 廠商看到特徵；客戶管理員看到事件 | 客戶 IT | 客戶設計者與管理員 |
| 地端版、關匿名回報 | 只有客戶管理員；要回報需自行聯絡廠商並附 request id | 客戶 IT | 客戶設計者與管理員 |
| 任一版、Mac 關機 | 事件照樣進維運中心並告警，派工排隊等 agent 上線 | 不受影響 | 不受影響 |

## 8. 資料庫：PostgreSQL 與 SQLite 的做法

正式版用 PostgreSQL，SQLite 只在研發與單機試用（使用者說明）。新程式不寫 SQLite 專屬寫法（使用者裁決，已寫成 `.claude/rules/backend.md` 的「PostgreSQL 相容」條）。下表的檢查 O2 先做寫入探測、空間、WAL 大小、完整性（`quick_check`、`foreign_key_check` 與產品層檢查），連線與鎖、慢查詢、`amcheck`、每週 `integrity_check` 延到 O4（細目與理由見 §17.2.4、§17.10.2 O2-T20）；PRAGMA 與 `pg_*` 照既有機制隔離：raw SQL 走 Persistence 的 `ISqlDialect`（`Persistence/Dynamic/Dialects/`），非寫不可的資料庫專屬程式放在 provider 專屬類別並在交付說明列出；每個檢查都寫兩種實作。pg0 之後的維運段（O2 起）兩種資料庫都要驗。

| 面向 | PostgreSQL | SQLite |
|---|---|---|
| 健康（ready） | Npgsql 下 `SELECT 1`，短逾時；`pg_is_in_recovery()` 確認不是唯讀副本 | `SELECT 1`；資料庫檔所在目錄可寫 |
| 連線與鎖 | `pg_stat_activity` 連線數對 `max_connections`、執行過久的查詢；`pg_locks` 找被擋的連線；`WriteGate` 等待時間（[pg0 設計](2026-11-12-postgres-provider-design.md) §5.6） | 計數 `SQLITE_BUSY`／`LOCKED`（現有 `DatabaseBusyExceptionHandler`）；`WriteGate` 等待時間 |
| 備份 | `pg_dump -Fc` 邏輯備份；雲端版另用基礎備份＋WAL 歸檔做時間點還原，或託管資料庫的快照 | `VACUUM INTO` 或 Online Backup API，連同 `attachments/`、`keys/` 目錄一起備 |
| 完整性 | `amcheck`（`bt_index_check`）、建庫時開 data checksums；加上產品層檢查（共用主檔 `MasterLifecycle.CheckAsync`） | `PRAGMA quick_check`（每日）、`PRAGMA integrity_check`（每週）、`PRAGMA foreign_key_check`；加上產品層檢查 |
| 空間 | `pg_database_size`、`pg_total_relation_size`、`pg_stat_user_tables.n_dead_tup` 與最近 autovacuum；主機磁碟剩餘 | `page_count * page_size`、`freelist_count`、WAL 檔大小；主機磁碟剩餘 |
| 慢查詢 | `pg_stat_statements` 取最慢與最常的語句；必要時 `auto_explain` | 沒有伺服器端統計：EF Core command interceptor 計時，超過門檻記 log；離線用 `EXPLAIN QUERY PLAN` |
| 例行維護（L2 候選） | 對單表 `ANALYZE` | `PRAGMA wal_checkpoint(TRUNCATE)` |
| 多實例 | 支援；排程器靠資料表租約保證同一工作只跑一份 | 只支援單一實例（檔案資料庫） |

`pg_stat_statements`、`amcheck` 是擴充套件，地端版安裝文件要寫出啟用方式；沒啟用時對應檢查回「未啟用」而不是失敗。同理，備份還沒做（路線階段 5）時，備份新鮮度檢查回「未設定」，不回失敗（主控判斷 B20）。

## 9. 分段 O1–O6

每段可獨立合併、上線後既有行為不變（新政策的出廠值見 §11）。

整體順序：p1 → **O1** → pg0 → 簽核 a2c、a2d–a6 → **O2** → MCP 地基 mcp1 → **O3** → **O4** → **O5** → **O6**。

- 使用者裁決的部分：p1 → O1 → pg0 → 簽核 a2d–a6 → O2 → mcp1 → O3…；O1 在 p1 之後、a2d 之前。
- 主控判斷的部分：
  - **a2c**（B29）：路線文件裡 a2c（重新比對、預覽、臨時讀取權）還沒開始，不在裁決寫的「a2d–a6」裡。排法：O1、pg0 在 a2c 之前，簽核主線照 a2c → a2d → … → a6 走完，O2 在 a6 之後。
  - **p2、p3**：不在裁決的主序裡，也不擋 O1–O6 與 mcp1。使用者 2026-11-12 確認：p2、p3 排在 O2 之後、mcp1a 之前；技術上 mcp1 不依賴 p2、p3；若 p3 尚未合併，沿用既有的 `IBusinessTime` 與目前的預設語系（MCP 設計 §5.1、§6.5）。
  - **O3 → O4 → O5 → O6 的先後**與「O3 的外部探測只需要 O1，可視情況提前」（B32）：初稿標成裁決，實際是主控初步規劃（主控判斷，使用者 2026-11-12 確認）。
- **開工時點**（B4）：o1a、o1c 只需要 `PolicyCatalog`，p1a 合併到 main 後就能開工（p1a 目前只在 `feature/policy-settings` 分支）；o1b 的通知維護要用 A14，排在 p1c 之後。

| 段 | 範圍 | 相依 |
|---|---|---|
| O1 地基 | 結構化 log＋request id、health live／ready、版本端點、前端錯誤收集、背景排程器（任務提醒與通知清理改由排程跑；即將到期提醒改為「到期前 N 天內補一次、同一任務只補一次」，使用者 2026-11-12 裁決，§10.2.9） | 政策 p1 合併（o1a、o1c 等 p1a，o1b 等 p1c）；排在 pg0 與簽核 a2c 之前 |
| O2 事件中心 | 健康檢查規則表＋紀錄表、維運事件表、稽核表（與 MCP 共用、一次含 MCP 欄位，§5.6）、告警通道（站內＋SMTP）、監控頁、規則失控自動暫停（範圍依 C3 使用者裁決，2026-11-12）；細部設計見 §17 | O1、pg0（兩種資料庫都驗）；排在簽核 a6 之後 |
| O3 廠商維運中心 | Workers＋D1、收件與去重、外部探測、匿名回報與新版本通知、Email 與手機推播、推送 HMAC、看門狗 | O1（版本、health）；全量事件推送需 O2；開工前盤點 Cloudflare 帳號現有用量（§15 第 12 條） |
| O4 agent L1 | Mac 拉取程式與隔離環境（C5）、領工協定、維運中心的 MCP 轉送點；產品端：mcpO1（維運服務帳號、`ops.read`、維運唯讀 MCP 工具）；唯讀診斷、結果回傳 | O3、mcp1（權杖、工具分級、稽核寫入點） |
| O5 agent L3 | 修正分支與 PR、CI gate（GitHub Actions）、GitHub App 與 rulesets、核准流程、依告警類型放行自動合併 | O4；repo 目前沒有 CI，O5 要先建 |
| O6 L2 自動處置 | 產品端：mcpO2（`ops.act`、維運處置 MCP 工具，高風險兩段式）；維運中心：runbook 引擎、冷卻與上限 | O2（稽核）、O3（runbook 在維運中心）、mcp1（高風險預演與確認碼）、O4（轉送點、維運服務帳號）；只雲端版 |

不另做維運專用的處置 API（使用者裁決）：O4、O6 的「產品端」工作就是在 MCP server 上登記維運工具（mcpO1、mcpO2），權杖、權限、稽核都用 mcp1 的地基。維運服務帳號從初稿的 O6 搬到 O4，因為 L1 的唯讀工具就要用它。

各段驗收方式：

- **O1**：見 §10.8。
- **O2**（細目見 §17.8 的 M01–M07）：SMTP 測試信寄得出去；本機分別把資料庫設成唯讀、塞滿測試磁碟配額、造一條必定失敗的任務規則（開發環境故障注入），各自在監控頁看到事件、收到告警信；磁碟與規則兩項同時收到站內通知，資料庫唯讀那項的站內通知在恢復可寫後補發（唯讀期間通知寫不進資料庫，§17.2.9）；規則達門檻後自動暫停，修好後恢復會再產生任務、沒修好恢復會再次暫停並標回歸；稽核頁看得到改權限與登入失敗。資料庫唯讀與規則暫停在 SQLite 與 PostgreSQL 各走一次。四種寬度走查監控頁、稽核頁、告警通道設定與任務規則的暫停標示。
- **O3**：停掉 Demo 站，下一次探測後 1 分鐘內廠商手機收到推播、恢復後收到恢復通知（研發期 Demo 每 5 分鐘探測，U12；驗收時可暫時調成 1 分鐘）；地端模式送出一則匿名回報，在系統設定頁看到原文、在維運中心看到同指紋聚合；推送缺 HMAC 簽章被拒；匿名收件超過限流回 429、雲端事件收件不受影響；D1 一日寫入量在估算內。
- **O4、O6 的驗收環境**（主控判斷 B25）：Demo 站開著 `Demo:LoginEnabled`，MCP 整個停用，不能拿來驗。驗收時用 Cloudflare quick tunnel 對外開一個本機實例（`Ops:Edition`＝`Cloud`、關掉 Demo 登入），不多開 Render 服務。
- **O4**：對一個人為植入的缺陷事件，Mac 開機後自動領工，經轉送點讀到該事件的 MCP 資料，維運中心收到診斷（指出檔案與原因），Mac 暫存目錄被清掉；Mac 關機期間事件排隊不遺失；把 prompt injection 字串塞進錯誤訊息，agent 沒有越權動作；agent 經轉送點要求處置工具被拒；產品稽核看得到每次 MCP 呼叫的權杖 id；維運服務帳號查業務記錄被拒、不能登入畫面；Mac 上找不到這件工作的 transcript（C5）。
- **O5**：同一植入缺陷，agent 開出含失敗測試與修正的 PR，CI gate 跑完，使用者核准後合併；agent 的 GitHub App 嘗試推 main 被 rulesets 拒絕。
- **O6**：模擬 WAL 過大，維運中心依 runbook 呼叫 MCP 處置工具，先拿到預演、確認後完成 checkpoint，產品稽核看得到預演與確認兩筆；冷卻期內第二次被拒；預演結果不符 runbook 預期時不確認並轉 L4；產品端關閉 `ops.remoteActionsEnabled` 後一律拒絕。

## 10. O1 細部設計

### 10.1 範圍

O1 做五件事，都不需要新畫面（監控頁在 O2）：

1. request id 與結構化 log。
2. `/api/health/live`、`/api/health/ready`，保留 `/api/health`。
3. `/api/version` 與管理員用的 `/api/v3/dynamic/system/ops/info`。
4. 前端錯誤收集與上傳。
5. 背景排程器，第一個工作是任務提醒與通知清理；同時改任務「即將到期」提醒的規則（使用者 2026-11-12 裁決，§10.2.9）。

建議切成三個可獨立合併的小段（主控判斷 T10）：

| 小段 | 內容 | migration |
|---|---|---|
| o1a | request id、結構化 log、指紋純函式、health、版本端點、ops info | 無 |
| o1b | 排程器、租約表、通知維護工作 | 一個 |
| o1c | 前端錯誤收集、錯誤代碼顯示、版本注入 | 無 |

o1a 與 o1c 可平行；o1b 有 migration，不與其他加 migration 的工作並行（backend 規則）。

### 10.2 後端

#### 10.2.1 request id 與 log scope

- 每個請求的 request id＝目前 `Activity` 的 W3C trace id（32 個十六進位字元）。請求一開始就把 `HttpContext.TraceIdentifier` 設成同一個值，於是現有錯誤回應裡的 `traceId` 不用改，自動和 log 對得上。
- 所有回應加 `X-Request-Id` 標頭。
- 預設不採用外部送來的 `traceparent`，避免客戶端操控 log 裡的識別碼；放在自家反向代理後面時，可由部署設定 `Ops:TrustIncomingTraceParent` 開啟（主控判斷 T3）。
- 每個請求開 log scope：外層 `requestId`（`RequestContextMiddleware`，整條 pipeline 第一個）；內層 `userId`、`route`（路由樣板，例如 `api/v3/dynamic/modules/{key}`）、`moduleKey`（路由有才帶；`RequestScopeMiddleware`，認證之後）。〔o1a 計畫判斷 J5〕路由樣板與 `moduleKey` 在呼叫 `next` 之前就存進 `RequestOpsFeature`：`ExceptionHandlerMiddleware` 會清掉 endpoint，內層 scope 在例外往外傳時也已 Dispose，所以請求結束行與例外 log 不靠 scope，`UserId`、`Route`、`ModuleKey` 直接寫成欄位；只有 handler 內記的 log 帶內層 scope。〔審查 S5，偏離 J5 原位置〕`RequestScopeMiddleware` 放在 `SessionMiddleware` 之後、`IdempotencyKeyMiddleware` 之前：可選登入的 cookie 失效時 `SessionMiddleware` 才把身分降成匿名，scope 要在這之後開，`userId` 才不會留著失效帳號。框架自帶的 `RequestId` 欄位（連線序號格式）會並存，以 `requestId`（＝`TraceId`）為準（J6）。
- 所有 ProblemDetails 與 `SessionMiddleware`（401、403）、`IdempotencyKeyMiddleware`（400）的回應主體，`traceId` 一律等於 `X-Request-Id`（J7）。
- 規則歸屬〔o1a 計畫判斷 J9，取代原「規則 scope」寫法〕：scope 在例外往外傳時已 Dispose，最後一道 handler 看不到，所以改用例外標記。事件串聯、檢查、任務規則三個入口在每條規則外面加 `catch (Exception e) when (RuleFailureTag.Mark(e, kind, ruleId))`（永遠回 false，不吞例外），把 `ruleKind`（event、check、taskRule）與 `ruleId` 寫進 `e.Data`（最內層先寫者為準）；最後一道 handler 讀出來記進 log。`Mark` 不計數，`formwright.rule.failures` 只在最後一道 handler 真的處理到未處理例外且讀得到標記時才 +1，避免把 409、503 等正常流程算成規則失敗（O2 自動暫停會誤停）。代價：規則執行中記的 log 不帶規則識別。
- 背景工作的 scope：`jobKey`、`runId`、`instanceId`。
- mcp1 加入 MCP 後，請求 scope 再多 `channel`（畫面、MCP）與權杖 id；O1 先把 scope 的建立集中在一個 middleware，mcp1 只加欄位。

#### 10.2.2 結構化 log

- 非開發環境改用內建的 JSON console formatter，開 `IncludeScopes`；開發環境維持現在的純文字（主控判斷 T2：不引入 Serilog）。
- 每個請求結束時記一行，欄位：方法、路由樣板、狀態碼、耗時毫秒。
  - 503 記 Warning（資料庫忙碌這類可重試的暫時狀況，記 Error 會在 O2 變成事件；O2 要看忙碌趨勢改查 Warning），其他 5xx 記 Error；4xx 記 Information；耗時超過 `ops.slowRequestMs` 記 Warning（J32）。
  - `ops.slowRequestMs` 用 60 秒的程序內快取讀（主控判斷 B2；實作 `CachedSystemPolicy`，J12）：讀不到資料庫時回出廠值並記 Warning，失敗結果也快取 10 秒、讀值逾時 2 秒，避免資料庫故障時每個請求都再連一次；請求耗時低於政策下限（200ms）時不讀政策。政策讀值只有「同一個 request 內快取」（政策設定 §6），每個請求結束時去讀等於每個請求多一次資料庫讀，違反反目標「不拖垮業務請求」。代價是改值後最多 60 秒才生效。
  - 2xx 且不慢的記 Debug（預設不輸出），避免鈴鐺輪詢灌爆 log。
  - 不記 `/api/health/*` 與靜態檔。
- 新增最後一道 `IExceptionHandler`（`UnhandledExceptionHandler`，註冊在其他三個之後）：記 Error（EventId 5000，含例外型別、指紋、route、userId、moduleKey、規則標記），回 `application/problem+json` 500，`traceId` 同 `X-Request-Id`，另帶 `message`（四語「系統發生錯誤，請稍後再試」，前端 `toApiError` 讀 `message`）（J8）。框架的 `BadHttpRequestException`（主體超限、格式壞掉）保留原狀態碼，只記 Information（EventId 5002，原因記框架的固定訊息），不算指紋、不計 metric（J31）。回應已開始後才失敗的串流（匯出、報表下載中途出錯）框架直接重新丟出，不呼叫我們的 handler，所以沒有 EventId 5000 與指紋，只有框架與 Kestrel 的 log（審查 N10）。`moduleKey` 目前只認路由樣板含 `modules/{key}` 的請求，`modules/{moduleKey}/…` 這組路由不帶（審查 N1）。
- 不准**主動**寫進 log 的內容（我們自己的 log 呼叫；寫死，§12；與 MCP 設計 §9.3 合併成這一份清單）。框架自己的例外 log 帶完整例外訊息，可能含輸入值，所以產品 log 視同敏感資料控管存取（J29，部署說明已寫明）：查詢字串、request／response body、記錄欄位值、密碼、cookie、`Authorization` 標頭、上傳檔名、MCP 個人存取權杖明文（`fwp_` 開頭）、確認碼明文（`fwc_` 開頭）。EF Core 的 sensitive data logging 維持關閉。〔審查 S3，J29 例外〕`appsettings.json` 把 `Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware` 的 log level 設為 `None`：.NET 8 的這個 middleware 在呼叫 `IExceptionHandler` 之前就先記一則 Error（帶完整 stack），連 409、503、413 這類已被處理的狀況也記，會讓 O2 以 Error 層級判斷事件時失真，真正的 500 也會變成兩則。我們的 `UnhandledExceptionHandler` 已帶例外與指紋記 Error，不失去 stack；回應已開始後才失敗的例外仍由 Kestrel（`Microsoft.AspNetCore.Server.Kestrel`）記錄。因此「框架的例外 log 保留」不再成立，上面「框架自己的例外 log 帶完整例外訊息」只剩 Kestrel 與其他框架類別。
- log 落點：stdout。地端版由服務管理器（systemd journal、Docker、Windows 服務的包裝程式）收；要不要內建檔案輪替，留給地端安裝程式設計（§15）。

#### 10.2.3 自訂指標（O1 不裝 OpenTelemetry）

〔o1a 計畫判斷 J30，推翻主控判斷 T1〕理由（簡單優先）：O1 沒有任何匯出端，三個 OpenTelemetry 套件只帶來全域 propagator 互相影響、一條寫法不確定的測試與每請求多一個 Activity 的成本。

- 自訂指標用 .NET 內建的 `System.Diagnostics.Metrics.Meter`（名稱 `Formwright`，`OpsMetrics`）：`formwright.job.runs`（依 job、result）、`formwright.job.duration`（ms）、`formwright.client_errors`、`formwright.rule.failures`（依 ruleKind）。
- 不呼叫 `AddOpenTelemetry`、不設全域 propagator；忽略外來 `traceparent` 只靠 `IgnoreIncomingTraceContext`（`DistributedContextPropagator`）。
- 目前沒有匯出端，要看用 `dotnet-counters monitor --counters Formwright`。接後台的工作段（O2 或 O3）再加 OpenTelemetry 與 OTLP exporter，約十行。
- 地端版不匯出，不違反「只送匿名特徵」。

#### 10.2.4 health

| 端點 | 查什麼 | 回應 |
|---|---|---|
| `/api/health/live` | 不碰資料庫，只代表程序活著 | 200 `{status:"ok"}` |
| `/api/health/ready` | 資料庫在逾時內可連線（`AddDbContextCheck`，預設 `CanConnectAsync`）。不等索引回填 | 200 `{status:"ready"}`／503 `{status:"notReady", checks:{database:"fail"}}` |
| `/api/health` | 維持現狀，等同 live（相容既有外部設定） | 200 `{status:"ok"}` |

- 三個端點都不需登入，也不回錯誤細節或例外訊息（寫死，§12）；失敗原因只寫 log。〔o1a 計畫判斷 J3、J4〕ready 只檢查資料庫、不含 `startup`：`Program.cs` 在 `app.Run()` 之前就跑完 migration、種子與索引回填，伺服器還沒聽埠，`startup` 永遠是 ok，寫了是死碼；日後把初始化改背景時再加回。`CanConnectAsync` 對 SQLite 實際只做 `File.Exists`（不開檔），所以現階段 ready 幾乎不會失敗；換成 PostgreSQL 後會真的開連線，資料庫抖動超過 2 秒就回 503（pg0 前要決定 Render 健康檢查用 live 還是 ready，審查 S9）。O2 要更細的檢查再補。檢查逾時 2 秒，逾時回 503（不是 500）。
- 用內建的 `Microsoft.Extensions.Diagnostics.HealthChecks` 加 EF Core 的 DbContext 檢查，兩種資料庫同一份寫法（主控判斷 T5：不用 Xabaril 套件；O2 要查 PostgreSQL 專屬統計時再評估）。
- 資料庫逾時是寫死的技術限制（§12）：ready 在資料庫不通時也要能回答，而政策值存在資料庫裡，讀不到。
- Demo 資料背景產生不影響 ready（沿用 `Program.cs:117` 的既有行為）。
- 索引回填（`IndexBackfillJob`）不擋 ready（主控判斷 B8）：資料大時回填很久，Render 部署可能逾時判失敗。回填進度改放在 ops info；o1a 在 Demo 部署實測一次。
- `render.yaml` 的 `healthCheckPath` 改成 `/api/health/ready`（主控判斷 T6），部署時資料庫沒準備好就不切流量。

#### 10.2.5 版本與 ops info

- 版本資訊在建置時注入〔o1a 計畫判斷 J10、J11〕：Docker build-arg `APP_VERSION`、`GIT_COMMIT`（退回 `RENDER_GIT_COMMIT`）、`BUILD_TIME` 傳給 `dotnet publish` 的 `-p:AppVersion/GitCommit/BuildTime`，寫成三個 `AssemblyMetadata`（不碰 `InformationalVersion`：沒注入時它預設是 `1.0.0`，分不出「沒注入」）；前端用同一組值注入（o1c）。沒注入時三個欄位都回 `dev`。⚠️ 未驗證 Render 是否把 `RENDER_GIT_COMMIT` 當 build arg 傳入，上線後在 Demo 看 `/api/version` 的 `commit`。
- `GET /api/version`（不需登入）：`{version, commit, builtAt}`，只有這三個欄位（主控判斷 U8：比照 Odoo 公開版本資訊；外部探測與新版本比對都要用）。
- `GET /api/v3/dynamic/system/ops/info`（系統管理員）：版本三欄、`Ops:Edition`、啟動時間、`instanceId`、資料庫種類、最新已套用的 migration、索引回填進度、排程器狀態（每個工作的最近開始、最近結束、結果、連續失敗次數、下次預定時間、租約持有者）。O2 的監控頁讀它。
- 路徑放在 `/api/v3/dynamic` 底下（主控判斷 B1）：`SessionMiddleware` 只套用在這個前綴（`Prefix = "/api/v3/dynamic"`），放在外面時，被停用或被重設密碼的管理員舊 cookie 仍讀得到 ops info。

#### 10.2.6 指紋（Domain 純函式）

O1 就實作，log 與 O2 事件表共用同一套（主控判斷 T4）。指紋＝下列字串的 SHA-256 前 16 個十六進位字元：

| 來源 | 組成 |
|---|---|
| 伺服器例外 | `server`、例外型別全名、正規化訊息、產品程式碼（命名空間前綴 `DynamicModules.`）最上面三個呼叫框的「命名空間.類別.方法」（不含行號）、路由樣板 |
| 前端錯誤 | `client`、種類（vue、window、promise、chunk）、錯誤名稱、正規化訊息、最上面三個呼叫框的函式名與去掉雜湊的檔名、路由樣板 |
| 客戶設定（O2） | `config`、ruleKind、moduleKey、ruleId、錯誤碼（不含訊息） |
| 健康檢查（O2） | `infra`、檢查代碼 |

- 呼叫框還原（主控判斷 B6）：async 方法的呼叫框長得像 `<Handle>d__5.MoveNext`，要還原成外層方法名再取；lambda 與 local function 同樣還原到所屬方法。
- 指紋另存演算法版本：log 欄位帶 `fingerprintVersion`（O1 起為 1），O2 事件表存成欄位；演算法改版時舊事件不重算（T4）。
- 正規化訊息：連續數字→`{n}`、GUID→`{guid}`、引號內字串→`{s}`、Email→`{email}`、八位以上十六進位→`{hex}`，再截斷到 200 字。
- 版本不進指紋：同一個缺陷跨版本仍是同一件，事件另記首次與最近的版本，才看得出回歸。
- 行號不進指紋：小改動不會讓同一個缺陷拆成兩件；代價是同一方法裡兩個不同缺陷會併成一件，靠例外型別與訊息分開。

#### 10.2.7 前端錯誤收件

`POST /api/v3/dynamic/ops/client-errors`：

- 路徑放在 `/api/v3/dynamic` 底下，並在 `SessionMiddleware` 加「可選登入」分支（`IsAnonymousOptional`）：沒帶身分就是匿名；帶 cookie 時照常比對資料庫，失效就降成匿名繼續（不回 401、不登出），有效才把 userId 交給 handler（主控判斷 B1；o1a 計畫判斷 J14）。
- 不需登入（登入頁的錯誤也要收）；已登入時伺服器自行補上 userId，不信任 body 裡的身分。
- body 上限、欄位長度、每來源 IP 與全站每分鐘次數都是寫死的技術限制（§12），超過回 413 或 429，前端不重試。
- 伺服器重新算指紋、正規化訊息後記一行 Warning log（EventId `ClientError`）。O1 不寫資料庫；O2 起寫進維運事件表。
- `ops.clientErrorCollection` 為否時回 204 並丟棄，不記 log。前端登入前讀不到政策，所以一律送，由伺服器決定收不收。伺服器讀這項政策同樣用 60 秒的程序內快取（B2）。
- 來源 IP 判斷沿用登入限流的 `Auth:ClientIpHeader` 設定（抽成 `ClientIpResolver` 共用，J15）。
- 欄位處理〔o1a 計畫判斷 J13〕：`kind` 只接受 `vue`、`window`、`promise`、`chunk`，其他回 400；body 超過 8KB 回 413；欄位超長在伺服器截斷而不回 413（訊息 500、stack 4000、`name`／`moduleKey` 100、`route`／`browser` 200、`version` 50、`locale` 20）；`route` 去掉 `?`、`#` 之後的部分、不是 `/` 開頭就記 null；`lastRequestId` 不符 32 個十六進位字元就記 null。log 帶原始訊息與原始 stack（J29），指紋與正規化訊息另列欄位。

#### 10.2.8 背景排程器

主控判斷 T8：自己寫一個 `BackgroundService`，搭配資料表租約；不用 Hangfire 或 Quartz。

理由：

- 需要的工作很少（通知維護、O2 的健康檢查與清理、O3 的回報批次），間隔都是分鐘級，不需要 cron 表達式、重試佇列、儀表板。
- Hangfire 的 SQLite storage 是社群維護，PostgreSQL storage 也是另一個社群套件；Quartz 的 ADO job store 有兩種資料庫的 schema，但要另外建十幾張表。兩者都比需求重。
- 資料表租約只用條件式 `UPDATE`，SQLite 與 PostgreSQL 同一份 EF 寫法，多實例也成立；不用 PostgreSQL 專屬的 advisory lock 或 `FOR UPDATE SKIP LOCKED`，SQLite 沒有對應。

行為：

- 每個實例啟動時產生 `instanceId`（機器名稱＋程序號＋隨機四碼）。
- 每 30 秒檢查一次登記的工作（寫死，§12）。到期的工作先搶租約：`UPDATE ... SET 租約持有者=我, 租約到期=現在+5 分 WHERE jobKey=? AND (租約到期 < 現在 OR 租約持有者 IS NULL)`，影響列數為 1 才執行。
- 執行中每 1 分鐘續租；結束時寫結果、清租約、算下次預定時間（＝結束時間＋該工作的間隔政策值）。
- 實例當掉時租約自然過期，另一個實例（或重啟後的同一實例）接手。
- 租約時間一律用應用程式的 UTC 時間比較；多實例之間的時鐘誤差由 5 分鐘租約吸收，不另處理，列為已知限制（主控判斷 B9，§12）。
- 工作在自己的 DI scope 裡執行，走既有的 MediatR pipeline 與 `ITransactionalRequest`，所以一樣經過 `WriteGate`：SQLite 上不和使用者寫入搶鎖造成 `SQLITE_BUSY`；PostgreSQL 上同樣在程序內排隊，再取 pg0 的全庫 advisory lock（[pg0 設計](2026-11-12-postgres-provider-design.md) §5.6）。
- 工作的身分（主控判斷 B3）：`ActingScope.Begin(userId)` 一定要給 userId，系統裡沒有「系統使用者」，初稿寫的「沿用 ActingScope 當系統身分」做不到。
  - 本來就逐人處理的工作（例如 `notification.upkeep`）對每位使用者各開一次 `ActingScope`。
  - 不代表任何人的工作，稽核的行為者種類記「系統」（§5.6 的保留種類），不建 AppUser 列；這類工作不呼叫需要 `ICurrentUser` 的 handler。
- 一個工作失敗：記錄結果、累加連續失敗次數、記 Error log；不影響其他工作，下次照間隔再試。O2 起連續失敗會開事件。
- 單次執行有寫死的最長時間（§12），超過就取消並記失敗。
- 部署設定 `Ops:Scheduler:Enabled`（預設開）可整個關掉，給「多實例只讓其中一台跑」或除錯用。這是部署設定，不是業務政策。
- 程式關閉時停止領新工作，等待執行中的工作在 graceful shutdown 時間內結束。
- 〔o1b 實作補記，O1 計畫判斷 J16–J19〕
  - 搶租約的條件另加「已到期」：`WHERE JobKey=? AND NextDueUtc <= 現在 AND (租約持有者 IS NULL OR 租約到期 < 現在)`（偏離上面原寫法）。沒有這一條時，慢一步讀到「到期」的實例會在別人跑完、清掉租約後再搶到一次而重跑。
  - 補列、搶租約、續租、寫結果都是交易型請求（經 `WriteGate`），各開一個短 DI scope；工作本身另開一個 scope。續租在自己的 scope 與交易，不巢狀在工作的交易裡。
  - 續租：不是持有者就中止工作、不寫結果（別人已接手，不能蓋掉新持有者的列），記 `leaseLost`；資料庫暫時失敗只記 Warning、下一週期再試，不判工作失敗。
  - 〔o1b 審查修正〕租約截止：工作的取消來源多一個「租約截止」計時器（走 `TimeProvider`），時間是租約到期前一個續租週期，只有續租成功才往後延；續租失敗或卡住不回都不會延，到了就中止工作、記 `leaseLost`、不寫結果。取代原本「續租失敗後才檢查剩餘時間」（續租卡住不回時永遠檢查不到，租約過期被別人接手時這裡還在跑）。`Renew` 必須小於 `Lease`。
  - 〔o1b 審查修正〕寫入的租約檢查：工作拿到 `JobRunContext`，寫入交易帶 `JobLeaseFence`，交易一開始先用條件式更新確認「持有者是我、租約還沒過期」（PostgreSQL 上順便鎖住工作列到交易結束，接手者的搶租約要等它提交），不成立就回 Conflict（代碼 `jobLeaseLost`）、交易回滾，工作丟 `JobLeaseLostException` 中止，結果記 `leaseLost`。寫結果本來就是 `WHERE 持有者=我`，接手者搶租約會改掉持有者，兩者互斥。
  - 結果：逾時記 `Failed`（`LastError`＝`timeout`）；程式關閉記 `Cancelled`，不累加連續失敗，下次預定時間＝結束時間（下一個 tick 任何實例都可接）；`PartiallyFailed` 也累加連續失敗。
  - 核心 `JobRunner` 在 Application，`SchedulerHostedService` 在 WebAPI；啟動先補工作列，再等一個檢查週期才跑第一次（不和冷啟動的第一位訪客搶資源）。計時器（續租、逾時、檢查週期）都走 `TimeProvider`，測試用手動時鐘快轉，不真的等待。
  - 撞鍵與忙碌的判斷沿用 pg0a 已集中的 `IDatabaseErrors`（`IsUniqueViolation`、`IsBusyOrConflict`），不另建 O1 計畫寫的 `IDbErrorClassifier`。

#### 10.2.9 第一個工作：通知維護（`notification.upkeep`）

- 間隔：`notification.reminderScanMinutes`（出廠 60 分）。
- 內容與現在惰性觸發的 `EnsureNotificationUpkeepHandler` 相同，只是對象從「目前使用者」改成「所有啟用中的使用者」：補即將到期與逾期提醒（`TaskReminderSource`；即將到期改用新規則，見下），再清掉超過保留天數的通知（政策 A14）。
- 每位使用者各自一個交易、各自一個 `ActingScope`（B3）；一位失敗記 log 後繼續下一位，最後整體結果記為「部分失敗」。
- **即將到期提醒的新規則**（使用者 2026-11-12 裁決，取代現行的「到期日是明天才提醒」）：任務在到期前 `N` 天內（N＝政策 A6「到期前幾天提醒」，出廠 1）的任何一天，只要使用者打開通知或排程器跑到，就補一次；同一任務的即將到期提醒只補一次（以 `LastDueSoonNotified` 非空為準，不再是一天一則）。逾期提醒維持現行（到期日早於今天，一天最多一則）。新規則在 o1b 與排程器一起改，惰性路徑與排程路徑共用同一個判斷。任務設計 §6.2 與政策設定 A6 同步改寫。
- 保留惰性觸發（主控判斷 U4）：兩條路徑都冪等，排程器停了提醒也不會完全消失。
- 〔o1b 實作補記〕O1 計畫的使用者裁決（2026-11-12）一併實作：即將到期的範圍含到期當天（D1）；改到期日清掉即將到期戳記、換負責人兩個戳記都清（D2，重新計）；完成或取消後重開、到期日沒改不重新計（B3）；逾期提醒在同一任務還有未讀的逾期通知時只蓋今天的戳記、不新增（B1）。工作呼叫的是同一個 `EnsureNotificationUpkeepRequest`，所以簽核逾期提醒（`ApprovalReminderSource`，本來就是條件式認領）也一起改由排程補。唯讀判斷抽成 `NotificationUpkeepProbe`，兩條路徑共用；〔o1b 審查修正〕啟用名單只在整批開始時讀一次，所以每位使用者的交易內（租約檢查之後）再確認一次帳號仍啟用，途中被停用的跳過（不補、不蓋戳記、不清通知）；全部使用者都失敗時記 `Failed`，部分失敗記 `PartiallyFailed`，`LastError` 是「失敗數／總數 位使用者失敗」。
- 去重要能承受兩條路徑同時跑：現在靠任務列上的 `LastDueSoonNotified`、`LastOverdueNotified` 日期戳。SQLite 經 `WriteGate` 序列化不會重複；PostgreSQL 在 pg0 也有程序內排隊與全庫 advisory lock，但去重不能依賴它們（多實例時程序內排隊不互通，全庫鎖將來也可能拿掉），沒有序列化時兩個交易可能同時讀到舊值而各寫一則。所以 o1b 要把「寫日期戳」改成條件式更新（只在日期戳不是今天時更新，影響列數為 1 才新增通知），兩種資料庫都成立（§10.7）。
  - 〔o1b 審查修正〕認領條件另加「負責人、到期日、狀態和判斷時讀到的一樣」：沒有序列化時，提醒讀到負責人 A 之後，並行的編輯改派給 B 並清掉戳記，只比對戳記的話仍會認領成功、通知 A、卻用掉 B 的唯一提醒；改期移出窗口、完成任務同理。不一樣就不認領、不發，留給下一輪用新的那一版重新判斷；通知只發給認領成功那一版的負責人。
- 使用者看得到的變化：即將到期的提醒在進入到期前 N 天的範圍後一小時內就出現在鈴鐺未讀數，不必先打開鈴鐺；中途才被指派或才建立的任務也會補一次。手冊任務章節要同步（提醒說明改成「到期前 N 天內補一次」）。

### 10.3 前端

- 新增一個錯誤回報模組，在 boot 階段掛上：`app.config.errorHandler`、`window` 的 `error` 與 `unhandledrejection`、router 的 `onError`。
- 動態載入的程式塊載入失敗（部署新版後舊頁面常見）歸類為 `chunk`，除了回報，再提示「系統已更新，請重新整理」。
- 上傳內容：種類、錯誤名稱、訊息（截斷）、stack（截斷）、路由樣板與 `moduleKey`（不含記錄 id 與查詢字串）、前端版本、介面語系、瀏覽器家族、發生時間、最近一次失敗 API 的 request id（30 秒內才帶）。
- 前端先在本機去重：同一指紋在同一分頁 5 分鐘內只送一次；每分頁每分鐘最多送 10 則（寫死，§12）。
- 用 `fetch` 加 `keepalive` 送，不經 axios 攔截器；送失敗就丟掉，不重試、不再回報，避免回報自己的錯誤形成迴圈。
- production build 產生 source map 但不對外提供（`hidden`），保留在建置產物供 O4 agent 還原呼叫框（主控判斷 T9）。
- `http.ts` 從回應讀 `X-Request-Id`；5xx 與網路錯誤的錯誤提示加一行「錯誤代碼：」加 request id 前 8 碼，客戶回報問題時可以附上（主控判斷 U10）。四語文案；四種寬度不橫捲。
- 〔o1c 實作補記〕
  - 錯誤代碼接在訊息同一行尾端「…（錯誤代碼：ab12cd34）」，只在 `toApiError` 一處組好（使用者 2026-11-12 裁決 B2，O1 計畫判斷 J23），所以不是獨立一行。request id 取 `X-Request-Id` 標頭，沒有標頭退回主體的 `traceId`，只認 32 個小寫十六進位字元。5xx 沒有 `message`（代理回的 HTML 502）時先退回四語「系統發生錯誤，請稍後再試」。網路錯誤沒有回應、拿不到 request id，所以不顯示錯誤代碼。少數提示不看 `err.message`、固定顯示自己的文案（例如儀表板「儲存失敗」、外觀「儲存失敗」），這幾處不會出現錯誤代碼。
  - 「最近失敗的 API」指任何帶 request id 的失敗回應（4xx 也算），由 axios 攔截器記下；30 秒內才附進回報（`last-failure.ts` 的 `RECENT_MS`）。
  - 回報的 `version` 是前端版本號，有 commit 時接前 12 碼（`dev+0123456789ab`），Render 沒給 `APP_VERSION` 也分得出是哪一版；沒注入時就是 `dev`。瀏覽器只送家族與主版本（`Chrome 131`），不送完整 User-Agent。
  - 版本注入用 Quasar `build.defineEnv`（程式讀 `import.meta.env.APP_VERSION`）；O1 計畫 J25 寫的 `build.env` 在 app-vite 3 已改成 .env 檔的設定，不能用。
  - API 失敗（`ApiError`）以 `vue`、`promise` 進來時不回報（J24）；`router.onError` 收到的非程式塊錯誤歸 `vue`。程式塊錯誤不管從哪個入口進來都歸 `chunk`，另接 Vite 的 `vite:preloadError`；更新提示整個分頁只顯示一次，按「重新整理」重新載入。
  - 〔o1c 審查修正〕非 Error 的 rejection 物件不序列化內容，只送「非 Error 物件：<建構子名或 typeof>」（字串照送）；其他欄位先套後端上限（`name`／`moduleKey` 100、`route`／`browser` 200、`version` 50、`locale` 20）再量 7680 位元組；本機去重鍵改與伺服器前端指紋同組成（同一套訊息正規化、三個框去行列號與雜湊）；`Unable to preload CSS for` 也算程式塊錯誤，同一個錯誤物件從兩個入口進來只回報一次（WeakSet）。
  - source map：`build.sourcemap = 'hidden'`；兩個 Dockerfile 在前端階段把 `*.map` 移到 `/app/sourcemaps`，`wwwroot` 不留，所以 `UseStaticFiles` 送不出去（J26）。

### 10.4 資料表

O1 只加一張表，一個 migration（o1b）。命名沿用既有 `Dynamic_` 前綴（主控判斷 T11）。

**`Dynamic_ScheduledJob`**（每個登記的工作一列，啟動時補齊缺的列）

| 欄位 | 說明 |
|---|---|
| JobKey | 主鍵，工作代碼，例如 `notification.upkeep` |
| LeaseOwner | 目前持有租約的 instanceId，可空 |
| LeaseUntilUtc | 租約到期時間，可空 |
| LastStartedUtc、LastFinishedUtc | 最近一次開始與結束 |
| LastStatus | Succeeded、PartiallyFailed、Failed、Cancelled |
| LastError | 最近一次錯誤摘要（例外型別＋正規化訊息，截斷 1000 字），不含記錄值 |
| ConsecutiveFailures | 連續失敗次數 |
| NextDueUtc | 下次預定時間 |

- 只存目前狀態，不存每次執行歷程，所以沒有保留天數問題；歷程看 log 與 O2 的事件。
- 啟動補列（主控判斷 B7）：多實例在 PostgreSQL 上同時啟動會撞主鍵。做法是「插入，撞唯一鍵就略過」，撞鍵的判斷經 §10.7 的集中衝突判斷函式，不綁 `SqliteException`。
- 時間欄位一律存 UTC（§10.7）。
- 〔o1b 實作補記〕migration `20261118041712_OpsScheduledJob`（EF 產生、未手改、無部分索引）。`JobKey`、`LeaseOwner` 長度 100，`LastStatus` 存字串（長度 20）；四個時間欄位名稱都以 `Utc` 結尾，讀回時以 value converter 標成 `Kind=Utc`（SQLite 存文字不帶 Kind）。

### 10.5 API

| 方法 | 路徑 | 權限 | 用途 |
|---|---|---|---|
| GET | `/api/health/live` | 無 | 程序存活 |
| GET | `/api/health/ready` | 無 | 可接流量 |
| GET | `/api/health` | 無 | 相容，等同 live |
| GET | `/api/version` | 無 | 版本、commit、建置時間 |
| GET | `/api/v3/dynamic/system/ops/info` | 系統管理員 | 執行環境與排程器狀態 |
| POST | `/api/v3/dynamic/ops/client-errors` | 無（`SessionMiddleware` 匿名白名單；有限流） | 前端錯誤收件 |

（命名為主控判斷，實作可調整。需要登入或可能帶 cookie 的端點一律放在 `/api/v3/dynamic` 底下，才會經過 `SessionMiddleware`；health 與 version 不碰登入狀態，維持原路徑。）

### 10.6 O1 的政策項

照政策設定 §3.3、§4 的格式，登記進 `PolicyCatalog`：

| 代碼 | 說明 | 型別 | 出廠 | 範圍 | 技術上下限 |
|---|---|---|---|---|---|
| `notification.reminderScanMinutes` | 背景補提醒與清理通知的間隔 | 整數（分） | 60 | S | 5–1440 |
| `ops.slowRequestMs` | 請求耗時超過多少毫秒記為慢請求 | 整數（毫秒） | 2000 | S | 200–60000 |
| `ops.clientErrorCollection` | 是否收前端錯誤 | 布林 | 是 | S | — |

- 背景工作讀政策時不帶模組，只取全系統值（政策設定 §3.1）。
- 間隔政策改了之後，從該工作下一次排程開始生效，不中斷執行中的那一次。
- `ops.slowRequestMs`、`ops.clientErrorCollection` 在請求路徑上讀，用 60 秒程序內快取，改值後最多 60 秒生效（B2）；政策頁在這兩列顯示這句提示。
- 這三項要進政策頁的「通知」與新的「維運」分類，四語文案。

### 10.7 PostgreSQL 相容注意

O1 排在 pg0 之前，所以合併當下還不能在 PostgreSQL 上跑；但 O1 的新程式要在 pg0 完成後不改一行就能在兩種資料庫跑（使用者裁決：新程式不寫 SQLite 專屬寫法）。pg0 的驗收要包含 O1 的排程器與通知維護測試在 PostgreSQL 上通過。pg0 目前沒有設計文件，這條驗收已寫進路線文件的 pg0 工作段（主控判斷 B30），pg0 計畫要照抄。

- migration：o1b 的 migration 照現況用 SQLite 產生；pg0 改成每個 provider 一個 migration assembly 時，PostgreSQL 的 InitialCreate 一併含 `Dynamic_ScheduledJob`。o1b 的 migration 不手改、不加 SQLite 專屬的部分索引 filter。

- 不寫任何資料庫專屬 SQL；租約、條件式更新都用 EF 的 `ExecuteUpdate`。
- ops 相關的時間一律 UTC。Npgsql 對應 `timestamptz` 時要求 `DateTime.Kind=Utc`，混用本地時間會在執行期丟例外；業務時間仍照現有 `IBusinessTime`（牆上時間在 pg0 對應 `timestamp without time zone`，與 ops 的 UTC 欄位分開）。
- 例外判斷不綁 `SqliteException`：排程器遇到資料庫忙碌或交易衝突時，透過 pg0 的重試對應判斷（SQLite 的 BUSY／LOCKED、PostgreSQL 的 40001／40P01），O1 先集中在一個判斷函式，pg0 補 PostgreSQL 分支。〔o1b 實作補記〕pg0a 已先做好集中判斷 `IDatabaseErrors`（含 PostgreSQL 的 SQLSTATE），排程器與補列直接用它。
- 計數與次數用整數欄位、不用 `double`。
- 字串主鍵不依賴大小寫不敏感比對（SQLite 預設與 PostgreSQL 不同），工作代碼一律小寫。
- 並行假設以 PostgreSQL 為準：不能靠序列化保證正確，所有「先讀再寫」的去重都改成條件式更新（§10.2.9）。pg0 在 PostgreSQL 上仍啟用 `WriteGate` 做程序內排隊（避免等鎖請求佔滿連線池），另加全庫 advisory lock（[pg0 設計](2026-11-12-postgres-provider-design.md) §5.6、T18）；兩者都不是正確性的依據：多實例時程序內排隊不互通，全庫鎖將來可能拿掉，pg0 用「無鎖模式」測試驗證 O1 本身正確。
- ready 檢查有自己的短逾時，不被連線池耗盡拖住。
- 多實例時，前端錯誤限流與現有登入限流（`LoginThrottle`）都只在單一實例的記憶體裡；O1 接受這個限制並列出（§12），雲端版要多實例時再改成共享。

### 10.8 測試與驗收

先說驗收方式，再動手（AGENTS.md）。

**後端自動測試**（`--filter` 窄範圍）

- Domain：指紋純函式——數字、GUID、引號字串、Email 正規化後指紋相同；不同例外型別指紋不同；版本與行號不影響指紋。
- WebAPI：
  - `/api/health/live` 在資料庫連線字串錯誤時仍回 200；`/ready` 在同情況回 503，body 沒有例外訊息。
  - `/api/health` 回 200，相容既有行為。
  - `/api/version` 只回三個欄位。
  - 任一回應帶 `X-Request-Id`，錯誤回應的 `traceId` 與標頭相同。
  - 測試用 logger 抓到的請求 log 不含查詢字串；超過 `ops.slowRequestMs` 記 Warning。
  - `/api/v3/dynamic/ops/client-errors`：合法 204；超大 413；超過頻率 429；政策關閉時 204 且沒有 log；未登入也收。
  - `/api/v3/dynamic/system/ops/info` 非管理員 403；管理員被停用後，舊 cookie 讀 ops info 被 `SessionMiddleware` 擋下。
  - ready 在索引回填進行中仍回 200。
- Domain：async 方法的呼叫框還原成方法名後，指紋與同步版本一致。
- Application（假時間）：
  - 兩個排程器實例對同一資料庫，同一到期工作只跑一次。
  - 持有者當掉（不續租）後，租約過期由另一實例接手。
  - 工作丟例外：LastStatus＝Failed、ConsecutiveFailures 加一，其他工作照常。
  - 通知維護：有任務在到期前 N 天內（含剛指派、剛建立）的使用者，不呼叫取得通知 API 也收到提醒，且同一任務只收一次；超過保留天數的通知被清；同一天跑兩次不重複；排程與惰性觸發同一天各跑一次也不重複。
  - 〔o1b 實作補記，O1 計畫判斷 J20〕以上四條寫在 WebAPITests（`TestApp(fileDatabase: true)`，每個 scope 一個真的 DbContext）：`Ops.SchedulerTests`、`Ops.ScheduledJobLeaseTests`、`Ops.NotificationUpkeepJobTests`；提醒規則本身在 ApplicationTests 的 `NotificationApiTests`。〔o1b 審查修正〕另補：續租卡住不回超過租期時舊持有者先被中止、別人等租約過期才接手、只完成一次（`SchedulerTests`）；做到一半租約被拿走就不再寫、途中被停用的帳號跳過（`NotificationUpkeepJobTests`）；讀取後才改派、改期、完成時不認領（`NotificationApiTests`、`ReminderClaimRaceTests`，用攔截點在讀取與認領之間插入改派模擬）。

**前端自動測試**（vitest）

- 回報內容的欄位與截斷；同指紋 5 分鐘內只送一次；送出失敗不再觸發回報；不經 axios 攔截器。
- 錯誤提示顯示 request id 前 8 碼；四語鍵一致（`locales.vitest.spec.ts`）。
- `bun run typecheck`。

**本機走查**（`scripts/unattended/local-demo.sh`）

- 在瀏覽器 console 丟一個未捕捉錯誤，後端 log 出現一行 JSON，帶指紋、路由樣板、前端版本。
- 把 `notification.reminderScanMinutes` 設成 5，建一筆明天到期、指派給另一位 Demo 使用者的任務；不以該使用者登入，等 5 分鐘後查 `/api/v3/dynamic/system/ops/info` 看到工作成功，再以該使用者登入，鈴鐺已有未讀提醒。
- 讓一個 API 回 500，錯誤提示有錯誤代碼，log 裡找得到同一個 request id。
- 錯誤提示在 390、820、1366、1920 四種寬度不橫捲。

**PostgreSQL**：O1 合併時還沒有 PostgreSQL provider，相容性只能靠 §10.7 的審查清單與「新程式沒有資料庫專屬 SQL」的檢查，標**未驗證**；pg0 的驗收要在 PostgreSQL 測試環境跑過排程器（兩實例搶同一工作）與通知維護（兩條路徑並行不重複）兩組測試。

**手冊**：任務章節補「到期提醒會自動出現」；錯誤提示的錯誤代碼補進常見問題。

## 11. 維運類政策目錄（全段）

O1 的三項見 §10.6。以下是 O2–O6 的建議登記（代碼與出廠值為主控判斷，開工時可調整）。「範圍」欄：S＝只限全系統。

| 代碼 | 說明 | 出廠 | 範圍 | 技術上下限 | 段 |
|---|---|---|---|---|---|
| `ops.healthResultRetentionDays` | 健康檢查紀錄保留天數 | 30 | S | 7–365 | O2 |
| `ops.incidentRetentionDays` | 已結案維運事件保留天數 | 180 | S | 30–1095 | O2 |
| `ops.auditRetentionDays` | 稽核紀錄保留天數 | 365 | S | 90–3650 | O2 |
| `ops.incidentSampleRetentionDays` | 事件原始樣本（伺服器例外與前端錯誤原文）保留天數（事件本身照事件天數；原名 `ops.clientErrorSampleRetentionDays`，§17.6.1 改名） | 14 | S | 1–90 | O2 |
| `ops.incidentRenotifyHours` | 嚴重事件未處理時重複提醒間隔 | 24 | S | 1–168 | O2 |
| `ops.incidentAutoResolveHours` | 缺陷類事件多久沒再出現就自動結案 | 72 | S | 1–720 | O2 |
| `ops.rulePauseConsecutiveFailures` | 規則連續失敗幾次自動暫停（C3） | 5 | S | 3–1000 | O2 |
| `ops.rulePauseWindowMinutes` | 連續失敗的計算時間窗（C3） | 60 | S | 1–1440 | O2 |
| `ops.ruleLoopTriggersPerRecord` | 同一權杖或來源在時間窗內反覆寫同一筆記錄幾次視為迴圈（C3） | 30 | S | 5–1000 | mcp2a |
| `ops.configIssueRecipients` | 客戶設定類事件與規則自動暫停的收件人（B14） | 該規則的最後修改者＋全部系統管理員 | S | — | O2 |
| `ops.alertRecipients` | 基礎設施、產品缺陷、安全事件的站內收件人（Email 收件信箱改在告警通道設定，§17.10.1 O2-U1） | 全部系統管理員 | S | — | O2 |
| `ops.telemetryEnabled` | 地端版匿名錯誤回報（安裝時詢問，預設勾選） | 是 | S | — | O3 |
| `ops.updateCheckEnabled` | 地端版新版本通知 | 是 | S | — | O3 |
| `ops.telemetrySentLogRetentionDays` | 本機保留「最近送出內容」的天數 | 30 | S | 7–90 | O3 |
| `ops.remoteDiagnosticsEnabled` | 允許廠商遠端維運（唯讀）：維運服務帳號可經 MCP 呼叫維運唯讀工具（只雲端版有效；依 C1 使用者裁決（2026-11-12）） | 是 | S | — | O4（mcpO1） |
| `ops.remoteActionsEnabled` | 允許廠商經 MCP 呼叫維運處置工具（只雲端版有效；唯讀工具不受影響） | 是 | S | — | O6（mcpO2） |

- 三列標 C3 的出廠值與段別依 C3 使用者裁決（2026-11-12）：60 分鐘內連續 5 次、迴圈偵測延到 mcp2a（初稿為 20 次、10 分鐘、迴圈 O2 做）。
- O2 另新增 `ops.monitorScanMinutes`、`ops.upkeepMinutes`、`ops.alertEmailCategories`、`ops.alertEmailMinSeverity`、`ops.alertEmailMaxPerHour`；O2 政策的完整定案（型別、說明、上下限）與健康檢查出廠值以 §17.6 為準。

- 健康檢查的個別門檻與間隔（例如磁碟剩餘警告 20%、嚴重 10%；備份超過 26 小時警告）放在 O2 的「健康檢查規則表」，每列一個檢查、可開關；出廠值與技術上下限一樣只寫在一個登記處，和政策目錄同一套規則（主控判斷 T12）。
- 告警通道（SMTP 主機、帳號、寄件人、收件信箱）是設定資料，不是政策；密碼用 DataProtection 加密存放（§17.2.6）。
- 維運中心的設定（探測間隔、agent 每日工作數、每件最大輪數與時間、L2 冷卻與每日上限、依告警類型放行自動合併、回歸時提高一級嚴重度、匿名收件的限流）存在維運中心，不在產品政策目錄；建議出廠值：探測每 1 分、agent 每日 20 件、每件 40 輪與 30 分鐘、L2 同一處置冷卻 30 分鐘且每實例每日 3 次、自動合併全部關閉、回歸提高一級開、匿名收件每 IP 每分鐘 10 則且每安裝識別碼每日 200 則。

## 12. 可寫死清單

照政策設定 §10 的分類。O2 的增補見 §17.9。

**資料完整性**

- 排程器同一工作同時只跑一份（租約機制本身）。
- 同交易的事件與檢查不自動暫停（§5.4）。
- L4 的三類（資料修復、還原、安全事件）不能改成其他級。
- 自動暫停只記「由系統暫停」的規則，恢復時不打開客戶自己停用的。

**安全**

- health、版本端點不需登入，回應只含固定欄位，不含例外訊息、主機資訊、資料庫種類。
- `/api/v3/dynamic/system/ops/info` 只給系統管理員；需要登入的 ops 端點一律放在 `/api/v3/dynamic` 底下，經過 `SessionMiddleware`。
- 版別 `Ops:Edition` 的判斷（§4.1）：`OnPrem` 時維運服務帳號一律不能認證、不推送事件。
- 雲端版推送到維運中心的請求一律 HMAC 簽章，維運中心不收未簽章的推送。
- 不准寫進 log 的內容（§10.2.2）。
- 匿名回報欄位白名單（§4.2）與訊息正規化規則；安裝識別碼不從硬體推導。
- 預設不採用外部 `traceparent`。
- 前端錯誤收件不信任 body 裡的身分。
- 維運 MCP 工具：地端版不建立維運服務帳號、維運中心不連入；維運服務帳號沒有業務模組權限；維運處置工具一律歸高風險、走預演與確認碼（「不能移出高風險」屬於 MCP 的地板，依 C7 使用者裁決（2026-11-12））；產品端每種處置的每日硬上限（O6 定數字）。
- agent 不持有產品權杖，只經維運中心轉送點呼叫唯讀工具；main 受 rulesets 保護，agent 的 GitHub App 只能推 `agent/**`。
- 稽核表只能新增，不能修改或刪除（保留天數清理除外）；權杖、密碼、密鑰不進稽核。

**技術限制**

- ready 檢查的資料庫逾時 2 秒（讀不到資料庫時也要能回答，不能放在資料庫裡的政策）。
- 排程器檢查週期 30 秒、租約 5 分鐘、續租每 1 分鐘、單次執行最長 10 分鐘。排程器啟動後先等一個檢查週期才跑第一次（O1 計畫判斷 J19，`SchedulerLimits`）。
- 前端錯誤收件：body 8KB、訊息 500 字、stack 4000 字；每來源 IP 每分鐘 30 則、全站每分鐘 600 則（雲端版同一出口 IP 共用額度，錯誤數可能被低估；O2 聚合時把 429 次數也計成 metric，審查 U3）。
- 伺服器端指紋只取呼叫框名稱以 `DynamicModules.`（`ErrorFingerprint.ProductPrefix`）開頭的框；ops info 的機器名截斷 80 字元（`OpsInstance`）。
- 前端錯誤收件的其他欄位上限：`name`／`moduleKey` 100、`route`／`browser` 200、`version` 50、`locale` 20，`lastRequestId` 32 個十六進位字元（O1 計畫判斷 J13）。
- 請求路徑政策讀取失敗時快取 10 秒、讀值逾時 2 秒（O1 計畫判斷 J12）。
- 前端本機：同指紋 5 分鐘一次、每分頁每分鐘 10 則；最近失敗的 request id 30 秒內才帶；回報序列化後上限 7680 位元組（比伺服器 8KB 留餘裕，超過先縮 stack 再縮訊息）；忽略以 `ResizeObserver loop` 開頭的瀏覽器雜訊（〔o1c 實作補記〕O1 計畫 Task 16、J24）。
- 指紋長度 16 個十六進位字元、正規化訊息 200 字、取三個呼叫框。
- `LastError` 截斷 1000 字。
- 前端錯誤限流與登入限流只在單一實例記憶體（多實例時的已知限制）。
- 請求路徑上讀的政策（`ops.slowRequestMs`、`ops.clientErrorCollection`）用 60 秒程序內快取。
- 排程器租約以應用程式 UTC 比較，多實例的時鐘誤差只靠 5 分鐘租約吸收（已知限制）。
- 維運中心：探測間隔下限 1 分鐘（cron 最小單位）。
- 研發期維運中心與使用者其他 Cloudflare 免費站共用帳號額度（已知限制，§15 第 12 條）；正式營運時另議。

## 13. ERP 對照

這是平台能力，不屬於任何應用程式，也不新增主檔或單據；對照的是 ERP 產品本身的維運設計。

- **Odoo**：`ir.cron` 排程動作存在資料庫、多 worker 時以資料列鎖避免重複執行，對應 §10.2.8 的租約；`ir.logging` 存伺服器 log；publisher warranty 讓社群版定期回報安裝資訊並取得通知，對應地端匿名回報＋新版本通知；Odoo.sh 由廠商監控雲端實例，對應雲端版。
- **vtiger**：排程器由 cron 觸發，系統管理有排程工作清單（開關、間隔、最近執行），對應 O1 的排程器與 O2 監控頁的排程器區塊。
- **SAP**：SM37 背景工作總覽、ST22 程式錯誤傾印、SM21 系統 log；EarlyWatch Alert 由廠商遠端分析客戶地端系統、產出報告，對應維運中心＋agent L1。
- 差異：Formwright 多了「客戶設定」這一類（客戶自訂規則造成的錯），要和產品缺陷分流；主流 ERP 的客製多半是程式碼，錯誤直接算客製廠商的。

鐵則檢查：排程器、事件中心、稽核是平台能力，所有應用程式與空系統都用得到（鐵則 3、4）；不依賴任何應用程式存在。

## 14. 主控判斷（待使用者確認；U5、T7、B31、B32 已於 2026-11-12 確認）

每條寫決定、理由、選錯的代價。分兩類：使用者行為類會改變使用者看到或要做的事，請優先確認；技術類可由主控負責。builder 審查（2026-11-11）認為 O1 用到的 U4、U8、U10、U11、T1–T6、T9–T11 可逆、代價小，可照預設做、送審時一起看；審查新增的判斷在 §14.3。改變使用者裁決的 U5、T7 與 MCP 的 U11 已由使用者 2026-11-12 確認（§16 的 C3、C4、C7）。

### 14.1 使用者行為類

**U1 告警通道**

- 決定：地端版用客戶自己的 SMTP（O2 在系統設定填）加站內通知；雲端版由維運中心用免費郵件 API 代寄（候選 Resend 或 Brevo 的免費層，O3 比較每日額度後擇一）；廠商手機用 Telegram bot，ntfy 為備案。
- 理由：地端環境只有客戶自己的郵件主機寄得出去；Render 免費方案擋 SMTP 埠（研究 subagent 的結論，未附來源，⚠️ 未查證；O3 比較郵件 API 時一併查），雲端版從維運中心走 HTTP API 才寄得出去。Telegram 免費、推播可靠、不必自架、Worker 直接呼叫 HTTPS；ntfy 公開主題要靠難猜的名稱保護，自架又要一台主機。推播內容只放事件編號、類別、連結，不放客戶資料。
- 選錯代價：換通道只改維運中心的一個送出模組，產品端不動；Telegram 若被要求改成公司內部工具，O3 要重做一個 adapter，約一天。

**U2 L2 初始動作清單（只雲端版）**

- 決定：第一批只有五個，以產品 MCP 的維運處置工具提供、一律歸高風險（預演＋確認碼），全部冪等或可逆、只碰平台而不碰業務資料：
  1. 重試失敗的背景工作（只限登記為冪等的工作）。
  2. 釋放逾時的排程租約。
  3. 資料庫例行維護：SQLite `wal_checkpoint(TRUNCATE)`、PostgreSQL 對單表 `ANALYZE`。
  4. 取消執行超過上限的報表查詢。
  5. 重跑共用主檔與資料庫完整性檢查（唯讀，結果開事件）。
- 不在清單：重啟主機（不是產品 API）、清資料、改設定、恢復被暫停的客戶規則（要客戶確認修好）。
- 理由：L2 的條件是「有界、可逆、有 runbook」；以上五個做錯最多是多跑一次。
- 選錯代價：清單太保守只是多一些事件變成人工處理；太寬則可能讓自動化改到業務資料，所以從窄開始、依紀錄再加。

**U3 各類資料保留天數**

- 決定：照 §11 的出廠值（健康紀錄 30 天、已結事件 180 天、稽核 365 天、前端錯誤樣本 14 天、送出紀錄 30 天；通知沿用 A14 的 90 天）。維運中心：事件 90 天、探測原始紀錄 7 天、5 分鐘彙總 90 天。
- 理由：健康紀錄量大但只用來看近期趨勢；稽核常被要求留一年；事件要夠看出季度回歸；D1 寫入額度有限，探測原始資料只留一週。
- 選錯代價：都是政策或維運中心設定，改值即可；唯一不可逆的是天數設太短被清掉的資料。

**U4 惰性觸發保留**

- 決定：排程器上線後，打開鈴鐺時補提醒的舊路徑保留。
- 理由：排程器停掉（設定關閉、工作連續失敗）時提醒不會完全消失；兩條路徑都冪等。
- 選錯代價：多一點讀取成本；若日後發現重複，§10.2.9 的條件式更新會擋住。

**U5 自動暫停的範圍**（限縮了使用者裁決；使用者 2026-11-12 確認，C3 選項 1）

- 決定：只對任務規則與日後的非同交易規則自動暫停；同交易的事件與檢查只開事件、不暫停（§5.4）。門檻建議改成「60 分鐘內連續 5 次」；迴圈偵測延到 mcp2a。
- 理由（已更正）：初稿說任務規則是非同交易，錯了。`TaskRuleStage` 是存檔交易內的 `IWriteStage`（Order 12；任務設計 §5.4），任務規則失敗會擋存檔；暫停後存檔照常、只少產生任務，帳不會錯。反之，暫停同交易事件會讓存檔在少了扣庫存、過帳的情況下成功，帳就錯了；它們失敗時本來就整筆回滾，資料不會壞。
- 連帶要求：失敗計數寫在業務交易之外（回滾後另開交易），否則會跟著回滾；PostgreSQL 的交易失敗後不能再用。
- 迴圈偵測目前沒有對象：任務規則產生的任務不會再觸發規則，事件在設計期擋循環、執行期有深度上限；唯一來源是外部反覆寫同一筆（mcp2a 的 AI 寫入、整合）。
- 選錯代價：若使用者其實要事件也能暫停，得另外設計「暫停時一併擋下來源模組的存檔」，否則就是靜默壞帳；若照原文在 O2 做迴圈偵測，會寫出一個量不到東西的偵測器。

**U6 匿名回報與新版本通知分成兩個開關**

- 決定：兩個政策各自開關（`ops.telemetryEnabled`、`ops.updateCheckEnabled`），走同一條連線；只開新版本通知時不送安裝識別碼。
- 理由：使用者裁決要「可隨時關閉回報」，但關掉回報的客戶多半仍想知道有安全更新。
- 選錯代價：若使用者要的是一個總開關，把兩個合併即可，改動很小。

**U7 歸屬判斷不了時算產品缺陷**

- 決定：見 §5.1。
- 理由：讓缺陷被客戶看到、廠商也看到，比讓缺陷被當成客戶設定問題藏起來安全。
- 選錯代價：廠商收到一些其實是客戶設定的事件；O4 的 agent 診斷時會把它們標回客戶設定。

**U8 版本端點公開**

- 決定：`/api/version` 不需登入，只回版本、commit、建置時間。
- 理由：外部探測與新版本比對都要用；Odoo 也公開版本資訊。版本被看到的風險低於「看不到版本、不知道該不該升級」。
- 選錯代價：要收回就改成需要登入，外部探測改帶 token。

**U9 告警通道只送維運事件**

- 決定：O2 的 SMTP 與 Email 只用於維運告警；任務、簽核的 email 通知仍照功能清單「不做」。
- 理由：守住目前的範圍；有了通道之後，業務 email 變成獨立的功能決定。
- 選錯代價：無；之後要開業務 email 時通道可以直接重用。

**U10 錯誤提示顯示錯誤代碼**

- 決定：5xx 與網路錯誤的提示多一行「錯誤代碼：」加 request id 前 8 碼。
- 〔o1c 實作補記〕使用者 2026-11-12 裁決 B2：改成接在同一行尾端「（錯誤代碼：xxxxxxxx）」；網路錯誤沒有回應、沒有 request id，不顯示代碼（§10.3）。
- 理由：客戶打電話或寫信回報時，廠商與客戶 IT 能直接在 log 找到那一次請求；地端關掉回報時這是唯一的對照線索。
- 選錯代價：畫面多一行字；不要的話拿掉即可，request id 仍在回應標頭裡。

**U11 前端錯誤預設收**

- 決定：`ops.clientErrorCollection` 出廠為是；收到的只進自家 log 與事件表（log 帶錯誤原文，可能含畫面上的少量文字，政策說明明寫「log 請視同敏感資料保管」，使用者裁決 U1），不會因此送出客戶環境（地端版送不送由 `ops.telemetryEnabled` 另管）。
- 理由：前端錯誤是現在完全看不到的一塊；路由只記樣板、不記記錄 id 與查詢字串。
- 選錯代價：若客戶在意，管理員可關；出廠值改否只影響新安裝。

**U12 研發期外部探測 Demo 站**

- 決定：O3 把 Demo 站當雲端版探測，但間隔先用 5 分鐘而不是 1 分鐘。
- 理由：Render 免費方案閒置 15 分鐘休眠，探測會讓它常醒；每月 750 小時是整個工作區共用（研究 subagent 的結論，未附來源，⚠️ 未查證），一個服務全月常醒就約 744 小時，會吃光其他免費服務的時數。研發期的 Demo 不是正式雲端版，5 分鐘夠看出掛掉。
- 選錯代價：Demo 掛掉最晚 5 分鐘才知道；正式雲端版照裁決每分鐘。

### 14.2 技術類

**T1 OpenTelemetry**：O1 採用 OTel .NET，預設不匯出；有設 `OTEL_EXPORTER_OTLP_ENDPOINT` 才走 OTLP。理由：.NET 的 `ActivitySource`、`Meter`、`ILogger` 本來就是 OTel 的資料來源，採用成本低，日後要接哪個後台都不用改程式。三個套件已重新點名（§10.2.3）：Hosting、AspNetCore instrumentation、OTLP exporter；EF Core instrumentation 不在 O1（B5）。選錯代價：多三個套件（Apache-2.0）；不要時拿掉匯出即可，內建 API 仍可用。

**T2 不引入 Serilog**：用內建 JSON console formatter。理由：輸出到 stdout 由平台收是雲端與容器的標準做法，內建就夠。選錯代價：地端版若需要檔案輪替，到時候再加檔案 provider，不影響呼叫端。

**T3 不採用外部 traceparent**：預設自己產生 request id，部署設定可開。理由：公開站台的客戶端不該決定 log 識別碼。MCP 2026-07-28 規範在 `_meta.traceparent` 定義了 client 帶追蹤脈絡的方式（builder 讀 changelog 確認）；同一原則適用：`/mcp` 預設不採用，只記成不可信的關聯欄位，mcp1a 細部設計時決定要不要記。選錯代價：接上游追蹤時要記得打開設定。

**T4 指紋規則**：§10.2.6。理由：版本與行號不進指紋，跨版本仍是同一件、小改動不拆件；客戶設定類不含訊息，避免客戶資料進指紋。選錯代價：分太粗會把兩個缺陷併成一件，分太細會洗版；指紋演算法改版要附版本號，舊事件不重算。

**T5 health 用內建套件**：`Microsoft.Extensions.Diagnostics.HealthChecks` 加 EF DbContext 檢查。理由：兩種資料庫同一份寫法，不多依賴。選錯代價：O2 要 PostgreSQL 專屬檢查時，可能還是要加 Xabaril 的 Npgsql 檢查或自己寫。

**T6 Render 健康檢查改 ready**：理由：部署時資料庫沒準備好就不切流量。選錯代價：資料庫短暫不通時 Render 可能判定不健康而重啟；若發生頻繁，改回 live。

**T7 維運中心當 MCP 轉送點；L2 改由維運中心執行（使用者 2026-11-12 確認，C4 選項 1）**：§4.3、§6。

- 轉送點的部分是主控判斷：agent 只拿維運中心的 agent token，經轉送點呼叫產品 MCP；各雲端實例的 MCP 權杖（屬於只有維運權限的服務帳號，兩把，§6）只存在維運中心。
- L2 執行者的部分改動了使用者核准的「agent：雲端 L1/L2/L3」，所以不是主控能定的，經使用者 2026-11-12 確認：L2 由 runbook 引擎直接呼叫處置工具，不經 agent，agent 最多建議。
- 理由：Mac 不存產品權杖與資料；工具過濾、冷卻、上限放在 agent 外；runbook 是確定性的，不需要 LLM 決定要不要做；預演比對可以自動化。
- 選錯代價：維運中心成為持有所有雲端實例權杖的單點，所以權杖範圍只含維運工具、有到期日、可在產品端撤銷。產品端是 stateless、每個 POST 回一次 JSON（MCP 設計 §17 第 5 點；SDK 能否固定不開 SSE 未驗證，mcp1a 確認），轉送點不必維持串流連線；O4 仍要實測 Worker 的 CPU 與 subrequest 用量。
- 初稿的備案「領工時發一把短效唯讀權杖給 agent」已刪：它等於「用權杖建權杖」，違反 MCP 設計 §15；權杖怎麼發見 C6（使用者裁決：部署 secret 注入）。

**T8 排程器自寫**：§10.2.8。理由：需求小，Hangfire／Quartz 的資料庫支援與表結構都比需求重；租約只用條件式 `UPDATE`，兩種資料庫一致、支援多實例。選錯代價：日後真的需要 cron 表達式、重試佇列、工作依賴時，自寫的會長大；屆時換 Quartz，工作本身是 MediatR request，搬移成本不高。

**T9 source map 隱藏保留**：理由：repo 公開，source map 本身不是機密，但對外提供會讓頁面多下載；放在建置產物供 agent 還原呼叫框即可。選錯代價：agent 拿不到對應版本的 source map 時只能看壓縮後的框，診斷變慢。

**T10 O1 切三小段**：§10.1。理由：只有排程器需要 migration，其餘可先合併、平行做。選錯代價：多兩次合併與走查。

**T11 表名沿用 `Dynamic_` 前綴**：理由：照既有慣例（所有表都是 `Dynamic_`），不另開前綴。選錯代價：日後要把維運表分開備份或清理時，要靠表名清單而不是前綴。

**T12 健康檢查門檻放規則表**：每個檢查一列（開關、間隔、警告與嚴重門檻），出廠值與上下限只寫在一個登記處。理由：檢查是一組同形的資料，放政策目錄會變成幾十個零散代碼。選錯代價：政策頁看不到這些門檻，要在監控頁的設定分頁改；若使用者要集中在政策頁，改成政策代碼即可。

### 14.3 依 builder 審查新增的主控判斷

builder 審查（2026-11-11）的 (b) 類，主控照建議答案採用，已寫進正文對應處。編號沿用審查的 B 編號；B15–B19 只涉及 MCP，見 MCP 設計 §16.3。B31–B33 是主控在修訂時另外改標的。每條都可逆、代價小，選錯就改回正文那一處。

| # | 決定 | 寫在 |
|---|---|---|
| B1 | ops info 與前端錯誤收件改到 `/api/v3/dynamic` 底下，後者加進匿名白名單；health、version 維持原路徑 | §10.2.5、§10.2.7、§10.5 |
| B2 | `ops.slowRequestMs`、`ops.clientErrorCollection` 用 60 秒程序內快取 | §10.2.2、§10.2.7、§10.6 |
| B3 | 排程工作沒有系統使用者：逐人工作逐人開 `ActingScope`；稽核用保留的行為者種類「系統」 | §5.6、§10.2.8、§10.2.9 |
| B4 | o1a、o1c 等 p1a 合併；o1b 等 p1c | §9 |
| B5 | O1 只加 ASP.NET Core instrumentation，EF Core 的不加 | §10.2.3、T1 |
| B6 | async 呼叫框還原方法名；產品程式碼以 `DynamicModules.` 判斷；指紋存演算法版本 | §10.2.6 |
| B7 | `Dynamic_ScheduledJob` 啟動補列用「插入，撞唯一鍵就略過」 | §10.4 |
| B8 | ready 不等索引回填，進度放 ops info | §10.2.4 |
| B9 | 租約以應用程式 UTC 比較，時鐘誤差列已知限制 | §10.2.8、§12 |
| B10 | O2 稽核表一次含 MCP 欄位，列舉值兩份文件統一 | §5.6 |
| B11 | 「逾時未確認」另寫新列，不改預演那列 | §5.6 |
| B12 | 稽核寫入介面能在 `IWriteStage` 同交易呼叫；登入稽核失敗不擋登入 | §5.6 |
| B13 | 畫面操作的風險級別用 Application 的靜態對照，與 MCP 工具登記放一起 | §5.6 |
| B14 | 「通知設計者」改成政策 `ops.configIssueRecipients` | §5.1、§5.4、§11 |
| B20 | 還沒有備份時，新鮮度檢查回「未設定」 | §8 |
| B21 | 匿名收件用獨立 Worker 與 D1、依 IP 與安裝識別碼限流；O3 前盤點帳號用量 | §4.2、§15 第 12 條 |
| B22 | 維運中心每日心跳＋外部探測打維運中心 | §4.2 |
| B23 | 每個實例一把推送金鑰，HMAC 簽章 | §4.2、§12 |
| B24 | 轉送點是 stateless 的 Streamable HTTP 代理，不用 Durable Objects | §4.3 |
| B25 | O4、O6 驗收用 Cloudflare quick tunnel 開本機實例 | §9 |
| B26 | agent 的 GitHub 身分用 GitHub App＋rulesets | §6、§9 O5 |
| B27 | 維運中心程式放同 repo 的 `ops-center/`，機敏值放 wrangler secrets | §4.2 |
| B28 | 讀稽核工具歸 `ops.read`，工具內只限系統管理員 | §4.1、§5.6 |
| B29 | O1、pg0 在簽核 a2c 之前；O2 在 a6 之後 | §9 |
| B30 | pg0 驗收含 O1 排程器與 mcp1 repository 的 PostgreSQL 測試，寫進路線文件工作段 | §10.7、路線文件 |
| B31 | L2「用範圍受限的權杖」：初稿標為裁決，checkpoint 查不到出處，改標主控判斷（使用者 2026-11-12 確認） | §3 第 6 條、§6 |
| B32 | O3 → O4 → O5 → O6 的先後、O3 可提前、p2 與 p3 的位置：初稿標為裁決，改標主控判斷（使用者 2026-11-12 確認） | §3 第 9 條、§9 |
| B33 | 回歸時提高一級嚴重度改成維運中心設定（出廠開），不寫死 | §5.5、§11 |

## 15. 風險與未決問題

1. **路線文件要跟上 PostgreSQL 與新排程**：已處理。路線文件「已定決定」改成使用者 2026-11-11 的說明（正式版 PostgreSQL、研發用 SQLite、pg0 在 O1 之後），開發順序加入 O1、pg0、O2、mcp1、O3–O6。O1 的 PostgreSQL 相容在 pg0 完成前都是未驗證。
2. **備份還原排序**：備份在路線階段 5，但護欄要求「備份放在 agent 碰不到的地方」，O2 的備份新鮮度檢查也要有備份可查（沒有時回「未設定」，B20）。建議第一個雲端客戶上線前、最晚 O6 之前先做。
3. **CI**：repo 沒有 `.github` workflow，O5 的 gate 需要先在 GitHub Actions 跑起來（公開 repo 免費）；本機 gate 腳本要能在 CI 執行。
4. **地端版 log 落點**：stdout 之外要不要內建檔案輪替，等地端安裝程式（Windows 服務、Linux、Docker）設計時決定。
5. **地端版的診斷深度**：地端只送匿名特徵，agent L1 拿不到 log 與資料，診斷只能靠指紋、呼叫框與版本；要更深的診斷需要客戶主動上傳診斷包，這件事要不要做未定。
6. **稽核與政策設定的「不做變更歷程」**：政策設定 §12 判斷 8 把設定變更歷程與還原列為 💡；O2 的稽核表會記下政策變更（誰、何時、舊值、新值），等於做了「歷程」的一半。O2 細部設計時要和那條對齊，還原仍不做。
7. **對外 API 與 Webhook 不做**：已處理。功能清單與路線「這版不做」改成「通用 REST API 與 Webhook 仍不做；AI 接入走 MCP，個人存取權杖只限 MCP」（MCP 設計 U16）。維運中心的事件收件是廠商維運專用的內部介面，不開放給客戶整合。
8. **雲端版多實例**：前端錯誤限流與登入限流都在記憶體；雲端版若要多實例，要先改成共享狀態。
9. **匿名回報預設勾選的法遵**：回報內容不含個資，但安裝識別碼是假名化的識別碼；外銷到有更嚴格同意要求的地區時，安裝畫面的說明文字要請法務看過。
10. **訂閱用量與資料條款**：agent 走 `claude -p` 訂閱，訂閱的用量上限會與使用者自己的互動共用；用訂閱跑無人值守 `claude -p` 的用量限制、消費者方案的資料使用條款，兩份文件都還沒查（⚠️ 未查證）。送出內容的範圍依 C2 使用者裁決；工作量大時要不要換 API 金鑰或 GitHub Actions，O4 上線後依實際用量決定。
11. **MCP 設計文件要對齊的點**：已對齊。維運工具在 mcpO1／mcpO2（§4.1）、維運服務帳號歸 O4（mcpO1，§9）、稽核欄位與列舉值統一（§5.6）、唯讀呼叫逐筆稽核、維運中心兩把權杖（§6）、轉送點是 stateless 代理（§4.3）、版別部署設定由本文件定義（§4.1）。兩份文件衝突時以 MCP 設計文件的地基為準，回頭改這份。
12. **Cloudflare 免費額度共用（風險）**：Workers 每日 10 萬請求、5 個 cron trigger、10 個 D1 是整個帳號共用，和使用者其他 Cloudflare 免費站共用。地端匿名收件是公開端點，被灌爆時當天連雲端探測與告警都停，等於「監控和被監控同時故障」。對策（主控判斷 B21）：
    - O3 開工前盤點帳號現有的 cron 與 D1 用量，寫進 O3 計畫。
    - 匿名收件用獨立的 Worker 和獨立的 D1，依來源 IP 與安裝識別碼限流（§4.2、§11），超過回 429。
    - 研發期共用帳號額度列為已知限制（§12）；正式營運時另議。
    - 「cron 執行算不算進每日請求數」未查證，O3 開工前確認。
13. **初稿誤標為使用者裁決的句子**：§3 第 6 條「用 scoped token」、第 9 條「O3 可視情況提前」與 O3–O6 的先後，在 checkpoint 查不到出處，已改標主控判斷（B31、B32），使用者 2026-11-12 確認。

## 16. 使用者裁決紀錄（2026-11-12）

builder 審查找出 7 題只有使用者能答、答案會改變要做什麼的問題。使用者 2026-11-12 全部裁決，七題都選建議方案（選項 1）。每題保留背景、選項與選錯代價供日後查；正文原標「依 C? 裁決」的段落已改為「依 C? 使用者裁決（2026-11-12）」定案。C1、C6、C7 的主體在 MCP 設計（[§20](2026-11-11-mcp-server-design.md#20-使用者裁決紀錄2026-11-12)），這裡寫維運這一側需要知道的部分。

O1 本身不依賴任何一題。

### C1 維運服務帳號要不要受「AI 連線總開關」與「限定群組」管？

- **使用者裁決（2026-11-12）：選項 1（服務帳號不受 `mcp.enabled`、`mcp.allowedGroups` 約束，另設雲端版有效的 `ops.remoteDiagnosticsEnabled`）。**

- **背景**：MCP 設計規定 `mcp.enabled` 為否、或帳號不在 `mcp.allowedGroups` 就一律 403；兩版 `mcp.enabled` 出廠都是否；維運服務帳號又不能加入群組。照字面做，雲端版出廠時廠商的 L1、L2 一律被擋；客戶要讓廠商維運就得替全公司打開 AI 連線；客戶一限定群組，服務帳號永遠被擋。另一面是客戶同意：關掉 AI 的客戶，廠商的 AI 仍在讀它的維運資料。
- **選項**：
  1. （建議）服務帳號不受 `mcp.enabled`、`mcp.allowedGroups` 約束；另設只在雲端版有效的政策 `ops.remoteDiagnosticsEnabled`「允許廠商遠端維運（唯讀）」，出廠為是，與 `ops.remoteActionsEnabled` 並列；客戶關掉它或撤銷權杖就連唯讀也停；「AI 連線」說明頁寫明廠商的診斷也會經 AI 服務商。
  2. 照字面：服務帳號受兩項約束，客戶要開 AI 連線、且不限定群組，廠商才能維運。
  3. 服務帳號不受約束，也不另設開關，只靠撤銷權杖。
- **選錯代價**：選 2，雲端版 O4、O6 在出廠設定下驗收不過，客戶要為了廠商維運打開全公司的 AI 連線。選 3，客戶以為關了 AI 其實沒關，有合約與信任風險。選 1 的成本是多一項政策與一條認證分支。
- **本文件依賴處**：§4.1、§6 總開關、§7 兩張表、§11 `ops.remoteDiagnosticsEnabled`。

### C2 送到 agent（也就是 AI 服務商）的維運資料可以含多少原文？

- **使用者裁決（2026-11-12）：選項 1（只給正規化後的內容，原文留在產品內）。**

- **背景**：
  - 地端匿名回報有欄位白名單，訊息也正規化（§4.2）。
  - 雲端版寫的是「全量事件」（§7），每則事件的內容沒有定義。
  - L1 的唯讀工具回「維運事件與其樣本」（§4.1），樣本含不含原始例外訊息、前端 stack、moduleKey 也沒定義。
  - 這些內容由 `claude -p` 經使用者的**個人訂閱**送到 Anthropic。消費者方案的資料使用條款與雲端版客戶合約，兩份文件都沒提（未查證）。
  - 它決定 O2 事件表存哪些欄位、O3 推送的 payload、mcpO1 唯讀工具的回傳形狀。
- **選項**：
  1. （建議）雲端事件推送與維運唯讀工具一律只給正規化後的內容（與地端白名單同一套正規化），另加類別、歸屬、moduleKey、ruleId、request id；原始訊息只留在產品 log 與產品內的事件樣本，廠商要看就由人登入該實例；訂閱帳號關閉「用於訓練」，並寫進維運中心的作業說明。
  2. 雲端版給原文（含原始例外訊息與 stack），另在雲端版合約與隱私說明寫明。
  3. 雲端版也只給地端白名單的欄位，不含 moduleKey、ruleId。
- **選錯代價**：給太多，客戶業務資料以消費者條款流到第三方，雲端版合約要重寫，而且送出去收不回來。給太少，L1 診斷變弱，但事後放寬只是改回傳欄位。
- **本文件依賴處**：§2 反目標、§4.1、§7 兩版差異表、§15 第 10 條。

### C3 規則失控自動暫停：接受限縮與新門檻嗎？迴圈偵測要不要延後？

- **使用者裁決（2026-11-12）：選項 1（接受 U5 限縮，門檻 60 分鐘內連續 5 次，迴圈偵測延到 mcp2a）。**

- **背景**：
  - 使用者裁決：客戶規則失控（連續失敗、迴圈）超過門檻自動暫停並通知。
  - 主控判斷 U5 限縮成只暫停任務規則與日後的非同交易規則。初稿的理由寫「任務規則是非同交易」，這是錯的：`TaskRuleStage` 是 `IWriteStage`（Order 12），在存檔交易內執行（任務設計 §5.4），任務規則失敗時整筆存檔失敗。
  - 限縮的結論仍成立：暫停任務規則只會少產生任務，帳不會錯。但連帶三件事：
    - 失敗計數必須在業務交易回滾之後另開交易寫，否則會跟著回滾；PostgreSQL 的交易失敗後整個交易都不能用。
    - 「連續失敗」等於使用者連續存不了檔；初稿出廠「10 分鐘內 20 次」對 50 人以下的公司幾乎不會觸發，使用者會一直卡著。
    - 迴圈偵測目前沒有對象：任務規則產生的任務不會再觸發規則；事件在設計期擋循環、執行期有深度上限；唯一的來源是外部反覆寫同一筆，例如 MCP mcp2a 上線後的 AI 寫入迴圈、整合。
- **選項**：
  1. （建議）接受 U5 的限縮，理由照 §5.4 更正；門檻改成「60 分鐘內連續 5 次」；迴圈偵測延到 mcp2a（AI 寫入上線）再做，對象改成「同一權杖或來源在時間窗內反覆寫同一筆記錄」。
  2. 照裁決字面在 O2 做迴圈偵測，門檻維持 20 次／10 分鐘。
  3. 事件與檢查也要能暫停，另設計「暫停時一併擋下來源模組的存檔」。
- **選錯代價**：選 2，會寫出一個量不到東西的迴圈偵測器，門檻也幾乎不會觸發。選 3，若沒擋存檔就是靜默壞帳，擋存檔則要多一段設計。選 1 的代價是 mcp2a 之前「迴圈」這半句裁決沒有實作。
- **本文件依賴處**：§3 第 8 條、§5.4、§11 三項規則政策、§14 U5。

### C4 L2 改由維運中心 runbook 執行、不由 agent 執行（§14 T7），接受嗎？

- **使用者裁決（2026-11-12）：選項 1（接受 T7：L2 由維運中心 runbook 執行，agent 只能建議）。**

- **背景**：使用者核准的三層大方向寫的是「(C) agent：雲端 L1/L2/L3，地端只 L1＋L3」。主控 T7 改成 L2 由維運中心的確定性 runbook 執行，agent 最多建議。初稿 §3 轉述裁決時把「雲端 L1/L2/L3」拿掉了，等於悄悄改了裁決；本版已還原 §3 原文，T7 改寫成提議。兩種做法下 O6 要做的東西不同：照 T7 是 runbook 引擎（白名單、比對預演、冷卻）；照原裁決是 agent 決定處置，維運中心只過濾。
- **選項**：
  1. （建議）接受 T7：L2 執行者改為維運中心的 runbook，agent 只能建議。理由：Mac 不必持有產品權杖；runbook 是確定性的；預演比對可以自動化。
  2. 照原裁決：agent 決定處置並經轉送點呼叫處置工具，維運中心只做白名單、冷卻、上限的過濾。
- **選錯代價**：選 1 而使用者其實要 LLM 判斷處置，O6 的 runbook 引擎要重做成「agent 提案＋中心審核」，約一段工作。選 2 的風險較大：讓 agent 直接處置正是業界事故的來源，而且 L2 工作的轉送點要開放處置工具給 agent。
- **本文件依賴處**：§2 反目標、§3 第 4 條註、§4.4 第 4 步、§5.3 L2 列、§6 最後一段、§14 T7。

### C5 Mac 上的 agent 要隔離到什麼程度？

- **使用者裁決（2026-11-12）：選項 1（專用 macOS 標準使用者＋sandbox＋暫存設定目錄）。**

- **背景**：
  - L1 要「在暫存 worktree 跑相關測試」，等於要開 Bash；Bash 沒辦法限縮成「只跑測試」。
  - 輸入不可信：事件文字可能是 prompt injection。
  - agent 跑在使用者的個人 Mac，同一台有 ssh 金鑰、雲端憑證、瀏覽器登入狀態。初稿 §6 的「被誘導也做不了危險的事」只算了產品端與 GitHub 的權杖，沒算 Mac 本機。
  - `claude -p` 預設把 session transcript 存在 `~/.claude/projects/`，刪掉 worktree 也還在，違反裁決「Mac 不存事件與客戶資料」。
  - 這決定 O4 要不要做專用帳號、容器或 VM，以及使用者要先手動建立什麼環境。
- **選項**：
  1. （建議）開一個專用的 macOS 標準使用者，裡面不放任何個人憑證，拉取程式以該使用者的 launchd agent 執行；`claude -p` 開 sandbox，`CLAUDE_CONFIG_DIR` 指到暫存目錄、做完一起刪；L1 的 Bash 只允許 `dotnet test`、`bun run test`、`git` 的指令前綴。（`CLAUDE_CONFIG_DIR` 與 sandbox 的實際行為未查證，O4 第一步實測。）
  2. 每件工作開一個 VM 或容器。
  3. 不另外隔離，跑在使用者本人的帳號，只靠工具白名單。
- **選錯代價**：選 3，一次 injection 就可能外洩個人憑證。選 2，Mac 資源吃緊，O4 也會延期。選 1 要使用者先手動建帳號，O4 計畫要列出步驟。
- **本文件依賴處**：§4.3、§6「Mac 本機隔離」與「不可信輸入」兩列、§9 O4 驗收。

### C6 維運服務帳號的權杖由誰核發、怎麼輪替？

- **使用者裁決（2026-11-12）：選項 1（部署流程從 secret 注入權杖，MCP §15 明列例外）。**

- **背景**：MCP 設計 §15 寫死「權杖只能在已登入的畫面建立、要重新輸入密碼、不能用權杖建權杖」，但服務帳號沒有密碼、不能登入，權杖初稿寫「由廠商開通程序發」、到期前「由維運中心輪替」；初稿 T7 還有「領工時發短效權杖給 agent」的備案（本版已刪）。這牽涉客戶同意（客戶管理員要不要親手核發廠商的權杖），也決定可寫死清單要不要開例外。
- **選項**：
  1. （建議）雲端版由廠商的部署流程注入：比照 `AdminBootstrap`，啟動時從部署 secret 讀權杖雜湊與到期日，upsert 服務帳號的權杖列；輪替＝維運中心換 secret 後重啟；客戶管理員在「AI 連線」頁看得到、可撤銷，撤銷後要經廠商重新開通才恢復；MCP 設計 §15 加一條明列的例外。
  2. 客戶管理員在畫面親手核發給廠商，到期再核發一次。
  3. 提供只給服務帳號用的輪替入口（用舊權杖換新權杖）。
- **選錯代價**：選 2，每個雲端客戶開通與每次到期都要人工交接權杖。選 3，等於開了「用權杖建權杖」，舊權杖外洩時攻擊者可以自己續命。不開任何例外，服務帳號根本建不出權杖。
- **本文件依賴處**：§4.1 維運服務帳號、§14 T7 最後一點。

### C7 MCP 高風險清單的「地板」接受嗎？

- **使用者裁決（2026-11-12）：選項 1（接受地板，寫死清單列給使用者看）。**

- **背景**：使用者裁決「高風險清單可在系統設定調」。MCP 主控判斷 U11 加了一組寫死、移不出清單的地板（改權限、套用應用程式套件、刪模組或欄位、全部維運處置工具），和「可調」的裁決衝突：管理員不能把這幾項調成一般寫入。
- **選項**：
  1. （建議）接受地板：這些動作影響整個系統或權限，屬於安全檢查，鐵則 2 允許寫死但要列清單給使用者看。
  2. 拿掉地板，全部可調（照裁決字面）。
  3. 更嚴：把刪除記錄也放進地板。
- **選錯代價**：小。拿掉地板只要刪一段驗證；但沒有地板時，管理員誤把「套用套件」移出，AI 就能一次改掉整個系統。對維運的影響：選 2 時，維運處置工具能被調成一般寫入，§6 的「預演比對」護欄就不一定成立，O6 要改成在 runbook 端強制先預演。
- **本文件依賴處**：§12「維運處置工具一律歸高風險」。

## 17. O2 細部設計

> 2026-11-19｜核對基準 `origin/main` `772e7a54`（O1、pg0 已上線）｜狀態：草稿，主控撰寫，待使用者確認 §17.10 的使用者行為類。
> 照 §10 的章節與粒度寫。本節沒寫到的總體規則以 §4–§9、§11、§12 為準；本節與前文不一致處以本節為準，已同步改的前文段落列在 §17.10.4。
> 標「O2-U?」的是使用者行為類主控判斷、「O2-T?」的是技術類主控判斷，彙整在 §17.10。

### 17.1 範圍

O2 做六件事：

1. **稽核表**（§5.6）：全平台共用、一次含 MCP 欄位；畫面的管理與設計變更、登入成功與失敗寫進去；稽核頁。
2. **維運事件**：伺服器 500、前端錯誤、規則失敗、健康檢查、安全事件、通道失敗都變成事件，依 §5.5 指紋去重；狀態機、嚴重度、站內通知。
3. **健康檢查**：規則表＋紀錄表（T12），七個內建檢查（§17.2.4），經 O1 排程器與一個不經租約的看門狗執行。
4. **告警通道**：站內通知＋客戶自己的 SMTP；設定、測試信、寄送佇列、重試、節流、寄送失敗本身的處理。
5. **規則失控自動暫停**（§5.4，依 C3 使用者裁決）：任務規則連續失敗達門檻自動暫停、通知、一鍵恢復。
6. **監控頁**（健康、事件、排程器、版本、告警通道）。

**不做**（留給哪段）：

| 不做 | 留給 |
|---|---|
| 事件推送到維運中心、外部探測、匿名回報、新版本通知；雲端版由維運中心代寄 Email（U1） | O3 |
| 規則的迴圈偵測、`ops.ruleLoopTriggersPerRecord`（C3） | mcp2a |
| PostgreSQL 的連線與鎖（`pg_stat_activity`、`pg_locks`）、慢查詢（`pg_stat_statements`）、`amcheck`；SQLite 的每週 `integrity_check`、EF 慢查詢攔截器（§8 表內其餘項目） | O4（mcpO1 的唯讀工具要讀這些統計時一起做；O2-T20） |
| 維運唯讀與處置的 MCP 工具、維運服務帳號（只預留稽核的行為者種類） | O4（mcpO1）、O6（mcpO2） |
| MCP 呼叫的稽核寫入點（O2 只建表與介面） | mcp1a |
| OpenTelemetry 與 OTLP 匯出（§10.2.3） | O3 |
| 業務 email（任務、簽核；U9）、使用者帳號的 Email 欄位（O2-U1） | 不排；要做時另開功能 |
| 稽核匯出、稽核頁看「讀取」紀錄 | 💡 |
| 備份本身（§15 第 2 條）；O2 只登記 `backup.freshness` 並回「未設定」（B20） | 路線階段 5 |
| 規則暫停期間略過的記錄補產任務（O2-U7） | 💡 |

**子單元切分**（實作者內部的 commit 切法；整段 O2 仍是一次交付、一次合併；主控判斷 O2-T1）：

| 子單元 | 內容 | 相依 | migration |
|---|---|---|---|
| o2a 表與稽核 | 八張表一次建好（§17.4）、`IDatabaseErrors` 加兩個分類、稽核寫入介面與 `AuditBehavior`、全部稽核寫入點、稽核覆蓋的反射測試、稽核 API、`ops.upkeep` 工作的稽核清理 | 無 | **整段唯一一組**（SQLite 與 PostgreSQL 各一個，名稱 `OpsEventCenter`） |
| o2b 健康檢查與事件 | 檢查目錄與規則表、看門狗、`ops.monitor` 工作、事件記錄器與收件匣、500 與前端錯誤接上事件、站內通知、唯讀回 503、監控 API | o2a | 無 |
| o2c 告警通道 | SMTP 設定與加密、MailKit 寄送、測試信、寄送佇列、重試與節流、通道失敗事件、資料庫寫不進時直送 | o2b | 無 |
| o2d 自動暫停 | 失敗計數、暫停與恢復、`ruleEditor` 動態收件人、事件與檢查的已知錯誤碼、開發環境故障注入、規則 API 帶暫停狀態 | o2a、o2b | 無 |
| o2e 前端 | 監控頁、稽核頁、通道設定、任務規則分頁的暫停標示與恢復、鈴鐺新種類、政策編輯器的 `ruleEditor` 選項、四語、四種寬度 | API 形狀定了就能開工（o2b）；走查等 o2c、o2d | 無 |

- 只有 o2a 加 migration，所以後面的子單元**不能再改表**；八張表的欄位要在 o2a 一次定案（§17.4），計畫審查時逐欄確認。
- o2c 與 o2d 都改 DI 註冊、`PolicyCatalog` 與四語檔，依序做，不平行。

### 17.2 後端

#### 17.2.1 稽核

**寫入介面**（Application，`Common/Audit/`）：

- `IAuditLog`（scoped）：`Add(AuditEntry)` 只把列加進 DbContext 的追蹤，跟著呼叫端下一次 `SaveChanges` 寫進**同一個交易**；`WriteDetachedAsync(AuditEntry)` 另開 DI scope 與交易立刻寫，給登入這類非交易請求用，失敗只記 Error log、不往外丟。
- `AuditEntry` 是值物件：動作代碼、目標（種類、id、顯示名快照）、摘要（結構化物件，由 `AuditSummary` 組成）、結果、原因代碼；行為者、行為者種類、來源管道、權杖 id、request id、來源 IP 由 `IAuditLog` 從環境補，呼叫端不填。
- 環境來源：
  - 行為者：`ICurrentUser`（`ActingScope` 內是被代為執行的使用者）；排程工作與沒有身分的寫入記行為者種類「系統」、行為者 null（B3）。
  - 來源管道：新增 AsyncLocal 的 `AuditOrigin.Begin(channel)`，比照 `ActingScope`；`JobRunner` 執行工作時開 `job`，mcp1 在 `/mcp` 開 `mcp`，預設 `ui`（O2-T24）。
  - request id：`Activity.Current` 的 trace id（＝`X-Request-Id`，§10.2.1）；背景工作為 null。
  - 來源 IP：Application 介面 `IRequestOrigin.ClientIp`，WebAPI 用既有的 `ClientIpResolver` 實作；背景工作為 null。
- **只能新增**：repository 只有新增、分頁查詢、依保留天數批次刪除三個方法；沒有修改或單筆刪除的路徑（§12）。不用資料庫 trigger 擋（兩種資料庫寫法不同），靠程式與審查。

**寫入點**（O2-T2）：

- 管理與設計類請求宣告 `IAuditedRequest`：`AuditAction Audit { get; }`（動作代碼與目標）。
- 新增 `AuditBehavior`，註冊在 `TransactionBehavior` 之內、`IdempotencyBehavior` 之外。請求是 `IAuditedRequest` 且結果成功時，組好 `AuditEntry` 呼叫 `IAuditLog.Add`，再 `SaveChanges` 一次（交易還沒提交，所以和業務寫入同交易）。稽核寫不進去就整筆失敗（§5.6）。
- 新舊值由 handler 補：handler 在 scoped 的 `AuditContext` 呼叫 `Describe(before, after)`；沒補的只記請求層的摘要（目標與參數代碼，不含值）。一定要補差異的：權限、群組成員、使用者（顯示名、群組、系統管理員旗標、啟用）、政策（舊值、新值）、健康檢查門檻、告警通道（密碼只記「已變更」）。設計類（模組、欄位、規則、事件、檢查、任務規則、流程、簽核流程、列印範本、轉單、分錄、應用程式套件）只記目標與變更種類，不存整份定義的差異（量大；記錄歷程與套件本身是另一份來源）。
- 不經 `AuditBehavior` 的寫入點：
  - **登入**（`LoginHandler`、`DemoLoginHandler`）：登入成功、失敗都用 `WriteDetachedAsync`，寫不進去不擋登入（§5.6）。失敗時帳號存在就記為目標，不存在只記原因 `unknownAccount`、不記輸入的帳號字串（沿用 `LoginHandler` 的規則）。被 `LoginThrottle` 擋下的嘗試不逐筆記，只累加指標 `formwright.login.throttled`（O2-T3）。
  - **自動暫停**：寫在暫停那一個獨立交易裡（§17.2.7）。
  - **保留天數清理**：每次 `ops.upkeep` 清完寫一列 `ops.retention.purge`，摘要帶各表刪除筆數。
- **只記成功**的畫面操作；被權限擋下（403）、驗證失敗（400）的畫面操作不記，看 log（O2-U9）。結果欄的「被拒」「驗證錯誤」「失敗」給登入失敗與 mcp1 用。
- **覆蓋檢查**（反射測試）：所有實作 `ITransactionalRequest`，且是 `ISystemAdminRequest`、或 `IDesignerRequest` 而 `RequiredDesignerAction` 不是讀的請求，都必須實作 `IAuditedRequest`；例外要列進測試裡的白名單並寫理由（例如 Demo 資料產生器的內部步驟）。新請求忘了宣告就在測試失敗。

**動作目錄**（O2-T2；B13）：

- `AuditActions`（Application 靜態類別）：每個動作代碼一列，含分組（`auth`、`user`、`group`、`permission`、`policy`、`org`、`design`、`app`、`ops`、`mcp`）、風險級別、四語標籤鍵。mcp1 把 `mcp.<工具>` 加進同一份目錄（「和 MCP 的工具登記放在一起」）。
- 代碼一律 `<分組或對象>.<動詞>`、camelCase，例如 `login.failed`、`permission.update`、`policy.set`、`policy.moduleSet`、`user.setActive`、`taskRule.resume`、`ops.channel.test`。完整清單由反射測試列出的請求決定，計畫階段寫成表。
- 風險級別照 MCP 的三級（唯讀、一般寫入、高風險）。畫面操作標高風險的，至少是 C7 地板那幾類：改權限、套用或刪除應用程式套件、刪模組或欄位；另加「授予或拿掉系統管理員」。

**摘要與遮蔽**：

- 摘要由 `AuditSummary` 逐欄組出，**不自動序列化請求物件**，所以請求裡的密碼、權杖不會意外進稽核。組摘要時值以 `fwp_`、`fwc_` 開頭的一律換成 `***`（§10.2.2 清單），鍵名是 `password`、`token`、`secret` 的值一律遮蔽。
- 摘要序列化後上限 16,000 字元（技術限制，§17.9）；超過時差異只留變動的鍵並加 `"truncated": true`。權限這類大矩陣只記變動的格子。

**稽核頁讀取**：

- 只給系統管理員（§4.1；比照 B28）。查詢條件：時間區間（預設最近 7 天、最長 366 天）、行為者、動作或動作分組、來源管道、結果、目標種類與 id。動作分組由伺服器依 `AuditActions` 展開成代碼清單用 `IN` 查，不用 `LIKE` 前綴（O2-T18）。
- 分頁用 Id 遞減的 keyset，每頁最多 100 列。
- 開 Demo 登入（`Demo:LoginEnabled`）時，稽核 API 一律不回來源 IP（公開 Demo 的訪客彼此看得到稽核；§17.9 安全、O2-U10）。

#### 17.2.2 維運事件

**來源與分類**（§5.1、U7）：

| 來源 | 進來的地方 | 類別 | 指紋（§10.2.6） | 出廠嚴重度 |
|---|---|---|---|---|
| `server` | `UnhandledExceptionHandler`（500） | 有已知規則錯誤碼→客戶設定；其他一律產品缺陷 | 伺服器例外那一列 | 警告 |
| `client` | `ReportClientErrorHandler`（§10.2.7） | 產品缺陷 | 前端錯誤那一列 | 資訊（O2-U5） |
| `config` | `RuleFailureBehavior`（§17.2.7）：事件與檢查回傳的已知錯誤碼、任務規則自動暫停 | 客戶設定 | `config`、ruleKind、moduleKey、ruleId、錯誤碼；暫停用錯誤碼 `autoPaused` | 已知錯誤碼：警告；暫停：嚴重 |
| `health` | 健康檢查（§17.2.4） | 檢查目錄登記的類別 | `infra`、檢查代碼 | 隨檢查結果 |
| `security` | 使用者的系統管理員旗標變更（`user.create`、`user.update`） | 安全 | `security`、`admin.changed` | 警告 |
| `channel` | 告警信最後一次重試仍失敗（§17.2.6） | 基礎設施 | `infra`、`channel.smtp` | 警告 |

- `ErrorFingerprint` 加 `ForConfig`、`ForInfra` 兩個純函式（§10.2.6 已定組成）；`security` 與 `channel` 沿用 `ForInfra` 的組法、前綴換成自己的種類字串。
- 任務規則丟例外時會有兩件事件：一件 `server` 來源的產品缺陷（例外本身，給廠商看），暫停時另一件 `config` 來源的客戶設定（給設計者處理）。這是有意的：例外是產品沒防住的錯，暫停是客戶要處理的狀態。
- 「已知規則錯誤碼」（O2-T17）：事件與檢查目前回 409 的失敗沒有代碼，也沒帶規則 id。o2d 在下列回傳處補上代碼，並呼叫 scoped 的 `RuleFailureNote.Add(kind, ruleId, code)`：串聯過深 `event.tooDeep`、串聯迴圈 `event.loop`（`EventStage.cs:67-68`）、檢查條件已不可用 `check.conditionInvalid`（`CheckEvaluator.cs:40`）、效果目標模組未啟用 `effect.targetUnavailable`、找不到明細模組 `effect.missingDetail`、明細列超過上限 `effect.tooManyRows`、找不到外掛處理器 `effect.handlerMissing`、未知效果種類 `effect.unknownKind`（`EffectApplier.cs`）。檢查「正常擋下」（業務規則成立）不算失敗。計畫階段逐處核對行號。

**狀態機**（O2-U4）：

```
            新指紋出現
                │
                v
   ┌────────> 開啟 ──── 有人按「確認」 ───> 已確認
   │            │  ^                          │
   │            │  └── 嚴重度升級（退回開啟）──┘
   │            │                             │
   │   結案條件成立（見下表） ／ 管理員按「結案」 │
   │            v                             v
   │          已結案 <─────────────────────────┘
   │            │
   └── 同指紋再出現：重開，標「回歸」、重開次數＋1
```

| 結案方式 | 適用 | 原因代碼 |
|---|---|---|
| 檢查回到正常 | `health`、`channel`（下一封信寄成功） | `recovered` |
| 連續 `ops.incidentAutoResolveHours` 沒再出現 | `server`、`client`、`config` 的已知錯誤碼、`security` | `expired` |
| 規則按「恢復」或被刪除 | `config` 的暫停事件 | `ruleResumed`、`ruleDeleted` |
| 管理員按「結案」 | `server`、`client`、`config` 的已知錯誤碼、`security` | `manual` |

- `health`、`channel`、暫停事件**不能手動結案**：結了下一輪檢查又會打開，只會製造回歸假象；按鈕在畫面上不出現，API 回 409 `incidentManagedBySource`。
- 「確認」＝有人認領，停止 §5.2 的重複提醒；同指紋再出現只加次數，不再通知。沒有「取消確認」。
- 嚴重度只有健康檢查類會變：升級時（警告→嚴重）退回「開啟」並再通知一次；降級只改欄位、不通知。
- 事件**不能由使用者刪除**，已結案的過了 `ops.incidentRetentionDays` 由 `ops.upkeep` 清掉（連同樣本）。清掉之後同指紋再出現是一件新事件，不是回歸。
- 一個指紋只有一列（§17.4 唯一索引；O2-T4），重開就是改回同一列。

**轉換與通知**（`IncidentRecorder`，Application）：

- 每次訊號進來：同指紋沒有列→新增「開啟」；有列且未結→次數＋1、更新最近時間與版本、嚴重度依規則升降；有列且已結→重開並標回歸。全部用條件式更新（`WHERE Status = 讀到的狀態`），影響 0 列就重讀再判斷一次，最多三次（§17.7）。
- 會通知的轉換：新開（嚴重度為警告以上）、回歸、升級、嚴重事件的重複提醒（`ops.upkeep` 每輪檢查：狀態「開啟」、嚴重、上次通知早於 `ops.incidentRenotifyHours`）。資訊級一律不通知。
- 通知與寄送佇列的列，和事件的轉換寫在**同一個交易**；交易失敗兩者都不會出現，不會有「信寄了、事件沒寫」。
- 處理者標示（監控頁與信件都用）：客戶設定→「需要設計者處理」；基礎設施→地端「需要 IT 處理」、雲端「廠商處理」；安全→「需要管理員確認」；產品缺陷→「產品問題：請附事件編號與錯誤代碼聯絡廠商」。O3 上線事件推送後，產品缺陷改成「已回報廠商」；O2 還沒有任何東西送出，不能寫「已回報」。

**樣本**（C2：原文只留在產品內）：

- `server` 與 `client` 來源每件事件保留最近 20 筆樣本（技術限制）：時間、request id、使用者、版本、路由樣板、原始訊息（500 字）、原始 stack（4000 字）、前端另帶瀏覽器家族與語系。新增一筆時刪掉第 21 筆以後的。
- 樣本另依 `ops.incidentSampleRetentionDays` 清除，事件本身照事件天數。
- 事件列本身只存正規化訊息，不存原文；站內通知與告警信只用正規化訊息（§17.2.5、§17.2.6）。

#### 17.2.3 請求路徑來源的收件匣

伺服器 500 與前端錯誤是請求路徑上的高頻來源，不在請求裡同步寫資料庫（反目標「不拖垮業務請求」；O2-T5）：

- `IIncidentInbox`（Application 介面，singleton 實作）：`Post(IncidentSignal)` 只放進記憶體，同指紋合併成「次數＋最近一筆樣本＋最近版本」，不擋呼叫端。
- WebAPI 的 `IncidentInboxFlusher`（`BackgroundService`）每 5 秒把累積的訊號交給 `IncidentRecorder`，每個指紋一個交易型請求（經 `WriteGate`）。
- 寫不進去（資料庫忙碌、唯讀、斷線）就留在收件匣下一輪再試；收件匣最多 1000 個指紋，滿了丟最舊的並累加指標 `formwright.incident.dropped`（技術限制）。程式關閉時最後再送一次，送不出去就丟（log 裡仍有每一則 Error 或 Warning）。
- 多實例：每個實例各自的收件匣各自寫，計數用 `次數 = 次數 + n` 的條件式更新累加，彼此不會蓋掉。
- 前端錯誤收件的 429 次數另計指標 `formwright.client_errors.throttled`（§12 審查 U3）。

規則失敗、健康檢查、安全、通道失敗的頻率低，直接同步呼叫 `IncidentRecorder`。

#### 17.2.4 健康檢查

**檢查目錄**（Application 靜態 `HealthCheckCatalog`，出廠值與技術上下限只寫在這裡；T12）：

| 代碼 | 查什麼 | 數值與單位 | 出廠門檻（警告／嚴重） | 出廠間隔 | 執行者 | 類別 | 資料庫 |
|---|---|---|---|---|---|---|---|
| `database.writable` | 寫入探測：在交易型請求裡對 `Dynamic_HealthCheck` 自己那一列做條件式更新（`LastProbeUtc`） | 無數值；成功＝正常，唯讀、空間滿、連不上、其他失敗＝嚴重，訊息代碼分四種；忙碌（`IsBusyOrConflict`，例如排隊逾時）不算失敗、沿用上一次狀態，連續 3 次忙碌才記警告 `busy` | — | 1 分 | 看門狗 | 基礎設施 | 兩種 |
| `storage.diskFree` | 資料目錄所在磁碟的剩餘比例，取最小的那一個：SQLite 的資料庫檔目錄、附件根目錄、金鑰目錄 | 剩餘萬分比（2000＝20%），訊息另帶剩餘位元組 | 2000／1000 | 5 分 | 看門狗 | 基礎設施 | 兩種（PostgreSQL 只看本機的附件與金鑰目錄） |
| `scheduler.jobs` | 讀 `Dynamic_ScheduledJob`：最大連續失敗次數；另外任一工作的下次預定時間落後超過三個間隔直接判嚴重（排程器卡住或搶不到租約） | 次數 | 3／10 | 5 分 | 看門狗 | 產品缺陷 | 兩種 |
| `database.walSize` | SQLite 的 `-wal` 檔大小（讀檔案系統，不下 PRAGMA） | 位元組 | 256 MB／1 GB | 15 分 | 排程 | 基礎設施 | 只 SQLite（PostgreSQL 回「不適用」，畫面不列） |
| `database.integrity` | SQLite：`PRAGMA quick_check`、`PRAGMA foreign_key_check`；兩種資料庫：共用主檔的 `MasterLifecycle.CheckAsync` | 問題筆數 | —／1 | 1440 分 | 排程 | 基礎設施 | 兩種（PostgreSQL 只做產品層） |
| `backup.freshness` | 最近一次備份距今時數 | 時數 | 26／50 | 60 分 | 排程 | 基礎設施 | 兩種；備份還沒做時回「未設定」（B20） |
| `security.loginFailures` | 稽核表最近 15 分鐘的 `login.failed` 列數 | 次數 | 20／100 | 5 分 | 排程 | 安全 | 兩種 |

- 每個檢查：可開關、間隔（1–1440 分）、警告與嚴重門檻（各自有技術上下限，見 §17.6.2），存在 `Dynamic_HealthCheck`；沒設的欄位是 null、讀出廠值（政策目錄同一套規則）。門檻的先後要合理（「越大越糟」時警告 ≤ 嚴重，「越小越糟」相反），不合理回 400。
- 結果狀態：`ok`、`warning`、`critical`，另有 `error`（檢查本身丟例外或逾時，事件以警告處理，訊息「檢查本身失敗」）、`notConfigured`、`notApplicable`（都不開事件）。
- 單一檢查最長 60 秒（技術限制），逾時記 `error`。
- 數值一律 `long`（§10.7「計數與次數用整數」；O2-T19）。

**兩個執行者**（O2-T6）：

- **看門狗** `OpsWatchdogHostedService`（WebAPI，每個實例各一個，不經排程器租約）：每 60 秒看一次，跑執行者是看門狗、且到期的檢查。
  - 理由：資料庫唯讀或連不上時，排程器連租約都搶不到（搶租約本身是 `UPDATE`），經排程器跑的檢查永遠不會跑，唯讀就偵測不到。看門狗只依賴「讀」，結果先放記憶體。
  - 檢查設定（開關、門檻、間隔）讀資料庫，失敗時用上一次讀到的值，從沒讀到過就用出廠值。
- **`ops.monitor` 排程工作**（O1 排程器，間隔 `ops.monitorScanMinutes`）：先送寄送佇列（§17.2.6），再跑執行者是排程、且到期的檢查。這些檢查要讀較多資料或比較久，多實例時只跑一份。
- 排程型檢查多實例時只跑一份（租約）；看門狗型每個實例各跑一份（寫入探測各寫各的，量很小），結果列帶 `InstanceId`。

**結果怎麼變成事件**：

1. 檢查跑完，先更新記憶體的 `HealthStatusBoard`（singleton，每個檢查的最新狀態、數值、訊息代碼、時間）。
2. 再寫 `Dynamic_HealthCheckResult` 一列，並把 `Dynamic_HealthCheck` 的最新狀態欄位與下次時間更新；同一交易裡呼叫 `IncidentRecorder`：`warning`／`critical`／`error` 開或更新事件，`ok` 結案。指紋＝`infra`＋檢查代碼（§5.5：一個檢查同時只有一件未結）。
3. 第 2 步寫不進去時，事件轉換放進記憶體的 `PendingIncidentWrites`（每個指紋只留最新一筆，上限 100），下一輪先補寫；狀態板照常更新，監控頁看得到。告警信走直送（§17.2.6）。
4. 手動「立即檢查」：管理員在監控頁按，API 同步跑該檢查（最長 60 秒）並回結果；同一檢查 60 秒內只能按一次（技術限制）。

#### 17.2.5 站內通知

- 新增兩個 `NotificationKinds`：`opsIncident`、`opsRulePaused`。通知只帶標題快照與連結（照既有 `Notification` 的規則，不帶欄位值）。
- 標題：事件的正規化訊息（健康檢查類用「檢查名稱＋狀態」），截斷 200 字；鈴鐺依種類在前面加四語標籤（比照簽核種類）。
- 連結：`opsIncident` 指向監控頁的事件詳細；`opsRulePaused` 指向模組設計頁的任務規則分頁（設計者多半不是系統管理員，看不到監控頁；O2-U3）。
- 收件人：
  - 客戶設定類：`ops.configIssueRecipients`，出廠 `ruleEditor,admins`（該規則的最後修改者＋全部系統管理員；B14）。
  - 其他類：`ops.alertRecipients`，出廠 `admins`。
  - 解析後為空（設定的人全被停用）就改送全部啟用中的系統管理員，記 Warning（寫死的安全網，比照簽核異常通知）。
- `ruleEditor` 是收件人政策的動態項目（O2-T15）：`PolicyDefinition` 加 `DynamicRecipients`（允許的動態項目清單），只有 `ops.configIssueRecipients` 允許 `ruleEditor`；`RecipientResolver` 多收一個情境參數，`ruleEditor` 解析成規則的 `ModifiedUserId`，沒有就 `CreatedUserId`，停用的略過。政策頁的收件人編輯器多一個勾選「該規則的最後修改者」。
- 資料庫寫不進時通知寫不進去，等 `PendingIncidentWrites` 補寫事件時一起補發（§17.2.9）。

#### 17.2.6 SMTP 告警通道

**設定存哪**：

- 一張 `Dynamic_AlertChannel`，一個通道一列，O2 只有 `smtp`（O3 可加其他通道，不改表）。欄位見 §17.4。
- 密碼用 Data Protection 加密（purpose `ops-alert-smtp`，與登入 cookie 同一套金鑰；O2-T10）。金鑰不見時（PostgreSQL 沒設 `KeysDirectory` 而重啟，pg0 §5.8）解不開：GET 回 `passwordUnreadable: true`，畫面要求重填；寄送時當作設定不完整，開通道失敗事件。
- 收件人是通道設定裡的**一組 Email 地址**（1–20 個），不是使用者帳號：`AppUser` 沒有 Email 欄位（O2-U1）。站內通知的收件人照收件人政策，兩者分開設。
- 開 Demo 登入時通道設定唯讀、測試信停用（回 403，比照 `UpdateSiteThemeHandler` 的 Demo 檢查）：公開 Demo 人人是管理員，可以把主機設成任意位址，讓伺服器替他連內網或寄信（§17.9 安全、O2-U10）。

**寄哪些**：

- 類別由 `ops.alertEmailCategories` 決定（出廠：基礎設施、安全、產品缺陷；客戶設定類只站內，給設計者；O2-U2），最低嚴重度 `ops.alertEmailMinSeverity`（出廠「警告」）。
- 觸發的轉換和站內通知相同（新開、回歸、升級、重複提醒）。
- `channel` 來源的事件**永遠不寄信**（通道壞了還寄信會自己觸發自己；寫死，§17.9）。

**寄送**（O2-T9、O2-T11）：

- 事件轉換時在同一個交易寫一列 `Dynamic_AlertOutbox`（待寄）。`ops.monitor` 每輪先送佇列：依建立順序取到期的列，逐封用 MailKit 寄出。
- 重試：失敗後 1、5、15、60 分鐘再試，最多 5 次（技術限制）；第 5 次仍失敗標「失敗」，開或更新 `channel.smtp` 事件（站內通知管理員）。之後任何一封寄成功就把該事件結案（`recovered`）。
- 節流：最近 60 分鐘已寄出數達 `ops.alertEmailMaxPerHour`（出廠 20）就不再逐封寄，把剩下的標成「併入摘要」；下一個小時第一輪寄一封摘要信（「過去一小時另有 N 則告警」＋各事件編號與標題，最多列 50 則）。
- 信件內容：主旨「[Formwright][嚴重] <標題>（#事件編號）」；內文：類別、處理者、次數、首次與最近時間、版本、事件連結。只用正規化訊息，不放原始訊息、stack、記錄值（C2 的精神：信也是送出產品之外）。連結的網址用部署設定 `Ops:PublicUrl`，沒設就不放連結。語系先固定繁中，p3 有系統預設語系後改用它（O2-T22）。
- 通道停用（`Enabled` 為否）或沒設定：佇列照寫、標「略過」，監控頁的通道區塊顯示「未設定，告警只在站內」。

**測試信**：

- `POST .../channels/smtp/test`：不經佇列、立刻用**已儲存**的設定寄一封到清單上的全部收件人（不能臨時填地址，避免被當成寄信工具；O2-U1），30 秒逾時。
- 成功回寄到哪些地址；失敗回 422 與失敗階段（連線、TLS、認證、寄送）和伺服器回應碼，不回例外全文。
- 同一管理員 30 秒內只能寄一次（技術限制）；寫稽核 `ops.channel.test`。

**資料庫寫不進時的直送**（O2-T8）：

- 佇列寫不進去時（看門狗偵測到唯讀、空間滿、連不上），改走記憶體直送佇列：上限 20 封、同指紋同轉換只寄一次、照樣重試與節流。
- 通道設定讀不到時用記憶體裡最後一次成功讀到的設定（密碼仍是加密字串，寄送時才解）；啟動後從沒讀到過就寄不出去，只剩監控頁與 log。
- 資料庫恢復可寫後，`PendingIncidentWrites` 補寫事件時會再產生佇列列；直送已寄過的轉換以「指紋＋轉換＋時間」比對，不重寄（只在同一實例內有效，已知限制）。

#### 17.2.7 規則失控自動暫停

依 §5.4 與 C3 使用者裁決：只暫停任務規則（含手動套用的 manual 規則）與日後的非同交易規則；同交易的事件與檢查只開客戶設定事件、不暫停；門檻 60 分鐘內連續 5 次；迴圈偵測延到 mcp2a。

**狀態存哪**（O2-T13）：

- 新表 `Dynamic_RuleHealth`（RuleKind＋RuleId 為主鍵），只在規則失敗過才有列；不改 `Dynamic_TaskRule`，所以客戶手動停用（`Active`）與系統暫停（`PausedUtc`）分開記，恢復只清後者（§12「自動暫停只記由系統暫停的規則」）。
- 留 RuleKind 是給日後非同交易規則；O2 只有 `taskRule`。

**計數**：

- `TaskRuleStage`（交易內）每次處理一個模組，先一次查出這些規則在 `Dynamic_RuleHealth` 的列（通常是空的）：
  - 已暫停的規則略過不跑，並把 `SkippedWhilePaused` 加一（條件式更新，跟業務寫入同交易）。
  - 有連續失敗紀錄、這次跑成功的規則，把計數歸零（條件式更新 `WHERE ConsecutiveFailures > 0`，同交易；業務交易後來回滾時歸零也一起回滾，下次成功再歸零）。
- `ManualTaskRules`（手動套用）照同樣規則：已暫停的不出現在「套用任務範本」清單；套用失敗也要加上 `RuleFailureTag.Mark`（現在沒有）。
- 失敗計數在新的 `RuleFailureBehavior`：註冊在 `ValidationBehavior` 之後、`ApprovalAlertBehavior` 之前（兩者都在交易之外）。
  - 只處理最外層請求（`ITransactionManager.IsActive` 為真表示巢狀，跳過，比照 `ApprovalAlertBehavior`）。
  - 例外帶有 `taskRule` 標記時：另開 DI scope，送交易型請求 `RecordRuleFailureRequest(kind, ruleId, errorCode, fingerprint, requestId)`，然後**原樣丟出原例外**；這筆寫入失敗只記 Error log，不改變回給使用者的錯誤（§5.4）。
  - 結果是失敗、而 `RuleFailureNote` 有已知錯誤碼時：開或更新客戶設定事件（§17.2.2），不計數、不暫停。
- `RecordRuleFailureRequest` 的寫法（全部條件式更新，兩種資料庫同一份，PostgreSQL 上並行也正確）：
  1. `UPDATE ... SET ConsecutiveFailures = ConsecutiveFailures + 1, LastFailureUtc = 現在, LastErrorCode = ? WHERE 主鍵 AND StreakStartedUtc >= 現在 - 窗口 AND ConsecutiveFailures > 0`；
  2. 影響 0 列（沒有列、或已過窗口、或剛被歸零）→ 重設成 `ConsecutiveFailures = 1, StreakStartedUtc = 現在`；沒有列就新增，撞主鍵（別人剛新增）就回第 1 步再做一次；
  3. `UPDATE ... SET PausedUtc = 現在, PauseCount = PauseCount + 1 WHERE 主鍵 AND PausedUtc IS NULL AND ConsecutiveFailures >= 門檻`；影響 1 列的那個交易就是「這次觸發暫停」的人，在同一交易寫暫停事件、站內通知、寄送佇列、稽核 `taskRule.autoPause`（行為者種類「系統」）。
- 時間窗的語意（O2-T14）：從這一串的第一次失敗起算；超過窗口還沒到門檻，就從下一次失敗重新算 1。所以「60 分鐘內連續 5 次」是「第一次失敗後 60 分鐘內又失敗 4 次、中間沒有成功」。比滑動窗口少算一些邊界情況，換來一列一個時間欄位。

**暫停與恢復的行為**：

- 暫停後：存檔照常成功，只是這條規則不產生、不同步任務；手動套用清單看不到它。已經產生的任務不動。
- 恢復：模組設計頁的任務規則分頁，暫停的規則顯示「已自動暫停」標籤（暫停時間、連續失敗次數、最近錯誤的代碼與正規化訊息、最近一次的錯誤代碼 request id 前 8 碼、暫停期間略過幾次存檔）與「恢復」按鈕。
  - 權限＝能編輯這條規則的人（設計器編輯權＋共用主檔設計權，與 `UpdateTaskRuleRequest` 相同；O2-U6）。
  - 恢復＝清 `PausedUtc`、計數歸零、`SkippedWhilePaused` 歸零（數字留在稽核摘要），暫停事件結案（`ruleResumed`），寫稽核 `taskRule.resume`。
  - 恢復後規則立即對下一次存檔生效（「重新觸發」）。暫停期間存過的記錄**不補產任務**（O2-U7）：`once` 規則對那些記錄要等條件再次由不成立變成立、或手動套用。
  - 恢復後若問題還在，計數從 0 重新累積，達門檻再暫停一次，暫停事件以「回歸」重開。
- 刪除規則：同交易刪掉它的 `Dynamic_RuleHealth` 列，暫停事件結案（`ruleDeleted`）。
- 套件匯出不帶暫停狀態；應用程式關閉再開啟不影響暫停狀態。

**使用者看到什麼**（O2-U7）：

| 誰 | 看到 |
|---|---|
| 存檔的一般使用者 | 失敗那幾次：原本的「系統發生錯誤」加錯誤代碼（O1）。暫停後：存檔成功，沒有任何提示 |
| 規則最後修改者＋系統管理員（出廠收件人） | 暫停當下一則站內通知 `opsRulePaused`；嚴重事件沒人確認時每 `ops.incidentRenotifyHours` 再提醒 |
| 系統管理員 | 監控頁兩件事件：例外本身（產品缺陷）與暫停（客戶設定）；告警信只寄前者（客戶設定類出廠不寄信） |
| 設計者 | 任務規則分頁的標籤與「恢復」按鈕 |

**故障注入**（只給驗收用；O2-T16）：

- 部署設定 `Ops:FaultInjection:TaskRuleKeys`（逗號分隔的規則代碼）只在 `ASPNETCORE_ENVIRONMENT=Development` 時生效：`TaskRuleStage` 評估到這些規則時丟 `InjectedFaultException`，走跟真的例外一樣的路徑。啟用時啟動 log 記 Warning。
- 自動測試不用這個設定，用 DI 換掉同一個 `ITaskRuleFaultInjector` 介面。
- 理由：任務規則目前很難自然失敗（條件解析失敗回 false、指派欄位空就不指派），驗收要「一條必定失敗的規則」只能注入。正式環境的預設實作是空的。

#### 17.2.8 排程工作與保留天數

| 工作 | 間隔政策 | 內容 |
|---|---|---|
| `ops.monitor` | `ops.monitorScanMinutes`（1） | 送寄送佇列；跑到期的排程型健康檢查 |
| `ops.upkeep` | `ops.upkeepMinutes`（60） | 自動結案（`expired`）、嚴重事件重複提醒、各表依保留天數清理、寫 `ops.retention.purge` 稽核 |

- 清理分批：每批最多 5000 列、各自一個交易（技術限制，§17.9），每批交易帶 `JobLeaseFence`；用 `ExecuteDelete` 加 Id 上界，在交易型請求內做，所以 PostgreSQL 上也經過全庫寫入鎖（backend 規則：交易外的 `ExecuteDelete` 不經鎖）。
- 清理對象與天數：健康檢查紀錄與已寄完（寄出、失敗、略過、併入摘要）的佇列列 `ops.healthResultRetentionDays`；已結案事件（從結案時間起算）`ops.incidentRetentionDays`；樣本 `ops.incidentSampleRetentionDays`；稽核 `ops.auditRetentionDays`。
- 健康檢查只靠看門狗與 `ops.monitor`，不另開工作；看門狗不是排程工作，`Ops:Scheduler:Enabled` 關掉時看門狗照跑（排程器停了反而更需要它；`scheduler.jobs` 會報下次預定時間落後）。看門狗另有部署設定 `Ops:Watchdog:Enabled`（預設開），給測試與除錯用。

#### 17.2.9 資料庫唯讀、空間滿、連不上

- `IDatabaseErrors` 加兩個分類（O2-T7；pg0 §5.7 的表多兩列）：

| 分類 | SQLite | PostgreSQL（SQLSTATE） |
|---|---|---|
| 唯讀 | `SQLITE_READONLY` 8 | `25006`（read_only_sql_transaction） |
| 空間滿 | `SQLITE_FULL` 13 | `53100`（disk_full） |

- 一般請求遇到這兩類：新增 `DatabaseUnwritableExceptionHandler`（排在 `DatabaseBusyExceptionHandler` 之後、`UnhandledExceptionHandler` 之前），回 503 加 `Retry-After: 60`，訊息四語「資料庫目前無法寫入，請聯絡系統管理員」（O2-U8）；記 Warning、不算 500、不開產品缺陷事件（同一個原因已由 `database.writable` 開了一件）。
- 這段期間：
  - 監控頁讀得到（讀不受影響），狀態板顯示 `database.writable` 嚴重，頁首提示「以下部分狀態只存在本實例記憶體，尚未寫入資料庫」。
  - 站內通知寫不進去；告警信走直送（§17.2.6）。
  - 登入成功仍可登入，稽核寫不進只記 Error log（§5.6）；管理類變更整筆失敗（503）。
- 恢復可寫後：看門狗下一輪探測成功，先補寫 `PendingIncidentWrites`（事件轉換與站內通知），再把 `database.writable` 結案；收件匣累積的訊號跟著寫入。
- 連不上（不是唯讀）：同上，但監控頁本身也讀不到資料庫，只剩狀態板的內容；`/api/health/ready` 照 O1 回 503。

#### 17.2.10 誰能看、誰能改

| 對象 | 系統管理員 | 設計者（非管理員） | 一般使用者 |
|---|---|---|---|
| 監控頁（健康、事件、樣本、排程器、版本） | 看、立即檢查、確認、結案 | 不能 | 不能 |
| 健康檢查門檻與開關 | 改 | 不能 | 不能 |
| 告警通道設定、測試信 | 改、寄（Demo 站停用） | 不能 | 不能 |
| 稽核頁 | 看（Demo 站不含 IP） | 不能 | 不能 |
| 維運類政策 | 政策頁改（既有規則） | 不能 | 不能 |
| 規則的暫停標示 | 看、恢復 | 能看規則就看得到；能編輯規則就能恢復 | 不能 |
| 站內通知 | 依收件人政策 | 依收件人政策（出廠只在自己是最後修改者時） | 不會收到 |

- 監控、稽核、通道的請求一律實作 `ISystemAdminRequest`（由既有 `SystemAdminBehavior` 把關，fail closed）；路徑放在 `/api/v3/dynamic` 底下經過 `SessionMiddleware`（B1）。
- 維運服務帳號（mcpO1）的行為者種類 O2 先預留，O2 不建帳號。

#### 17.2.11 開關組合下誰負責

| 組合 | 誰知道 | 誰處理 |
|---|---|---|
| SMTP 已設定並啟用 | 站內收件人＋通道收件信箱 | 照處理者標示（§17.2.2） |
| SMTP 未設定或停用 | 只有站內收件人；監控頁通道區塊提示「告警只在站內」 | 同上 |
| SMTP 設定了但寄不出去 | 站內收件人收到 `channel.smtp` 事件 | 系統管理員（地端客戶 IT） |
| 資料庫唯讀或連不上 | 有 SMTP：通道收件信箱（直送）；沒 SMTP：只有打開監控頁的人與 log | 地端客戶 IT；雲端廠商（O3 前要人主動看） |
| 排程器關閉（`Ops:Scheduler:Enabled` 否） | 看門狗照跑，`scheduler.jobs` 報落後；佇列與排程型檢查停 | 部署者 |
| 看門狗關閉 | 寫入探測、磁碟、排程器三項停；排程型檢查照跑 | 部署者（只給測試與除錯） |
| 雲端版（`Ops:Edition`＝`Cloud`） | O2 與地端相同；廠商要等 O3 推送才收得到 | 照 §5.1 |

### 17.3 前端

**入口**：系統管理選單新增「維運監控」（`/system/ops`）與「稽核紀錄」（`/system/audit`），只給系統管理員（`systemLinks` 的管理員區塊）；首頁的系統管理捷徑共用同一份清單。

**監控頁** `/system/ops`，四個分頁（URL query `tab` 記住）：

1. **總覽**：最上面一列狀態卡：整體（取最糟的檢查）、資料庫可寫、未結事件（嚴重／警告數）、告警通道狀態、版本與版別、資料庫種類。下面是「需要處理」清單（未結且嚴重或警告的前 10 件，連到事件詳細）與排程器表（讀 ops info 的 jobs）。狀態板有只在記憶體的內容時顯示頁首提示（§17.2.9）。
2. **事件**：篩選（狀態、類別、嚴重度）＋清單；每列：嚴重度圖示、標題、類別與處理者、回歸標記、次數、最近時間、動作（確認、結案；不能手動結案的不出現按鈕）。點開是詳細：全部欄位、狀態歷程（從稽核讀這件事件的確認與結案）、樣本（原文與 stack 用等寬字型、可捲動，預設摺疊）。
3. **健康檢查**：每個檢查一張卡：狀態、數值（依單位格式化，萬分比顯示成百分比）、上次與下次時間、「立即檢查」；展開看趨勢（最近 24 小時／7 天的數值折線，用既有 echarts，比照 `ReportChart.vue` 的載入方式）與設定（開關、間隔、警告、嚴重；顯示出廠值與允許範圍，留空＝出廠值）。
4. **告警通道**：SMTP 表單（啟用、主機、埠、加密方式、帳號、密碼〔顯示「已設定」，留空不變，另有「清除」〕、寄件地址與名稱、收件信箱清單）＋「寄測試信」＋最近狀態（最近成功、最近失敗與原因、連續失敗次數）。Demo 站整頁唯讀並說明原因。

**稽核頁** `/system/audit`：篩選列（時間區間、行為者、動作分組或動作、來源管道、結果）＋清單（時間、行為者、動作標籤、目標、結果、來源管道）；點開是詳細抽屜：全部欄位與摘要（差異以「欄位：舊值 → 新值」列出，其餘結構化摘要縮排顯示）。無限捲動（keyset）。

**其他畫面**：

- 任務規則分頁（`TaskRulesTab.vue`）：暫停的規則列顯示紅色標籤「已自動暫停」與「恢復」（沒有編輯權時只有標籤）；點標籤展開暫停資訊（§17.2.7）。恢復前確認框寫明「暫停期間略過的 N 次存檔不會補產任務」。
- 通知鈴鐺：`opsIncident`、`opsRulePaused` 兩個種類的四語標籤與圖示。
- 政策頁：新的維運政策出現在「維運」分類；收件人編輯器（`PolicyValueEditor.vue`）在政策允許時多一個「該規則的最後修改者」勾選。
- 文案全部四語，進 `src/i18n/*.json`；檢查名稱、訊息代碼、動作標籤、處理者標示都是鍵，不在前端寫中文常數。前端不留任何門檻或上限的常數，全部從 API 取（鐵則 2）。

**四種寬度的版型重點**（390、820、1366、1920，不得橫捲）：

| 寬度 | 版型 |
|---|---|
| 390（手機） | 分頁列可左右滑；狀態卡一欄；事件與稽核用 `q-table` 的 `grid` 模式（比照 `UsersPage.vue` 的 `$q.screen.lt.sm`）變成卡片，動作收進 `RowActionMenu`；篩選收進可展開區；詳細改成全螢幕對話框；樣本的 stack 區塊自己橫捲（內容捲，不是整頁捲）；趨勢圖寬度 100% |
| 820（平板） | 狀態卡兩欄；清單仍用卡片或精簡欄位（隱藏來源管道、次數等次要欄）；詳細用右側抽屜 |
| 1366（筆電） | 狀態卡三欄；清單用表格全欄位；健康檢查卡兩欄 |
| 1920（桌機） | 狀態卡一列六張；健康檢查卡三欄；稽核詳細抽屜與清單並排 |

- 可捲動容器裡的 `column` 一律加 `no-wrap`（frontend 規則）；所有輸入框有 `label`。

### 17.4 資料表

八張表、一組 migration（`OpsEventCenter`，SQLite 與 PostgreSQL 各一，用 `scripts/db/add-migration.sh` 產；O2-T1）。命名沿用 `Dynamic_`（T11）。時間欄位一律 UTC、名稱以 `Utc` 結尾（PostgreSQL 是 `timestamptz`），讀回用 `ScheduledJobConfiguration` 同一種 value converter 標 `Kind=Utc`。列舉一律 `HasConversion<string>()`、值小寫 camelCase。字串長度用 `HasMaxLength` 宣告（PostgreSQL 仍是 `text`，pg0 T6；測試的長度守衛會抓超長）。

**`Dynamic_AuditLog`**（§5.6 的每一項都有對應）

| 欄位 | 型別 | 說明 |
|---|---|---|
| Id | long，自動編號主鍵 | keyset 分頁用 |
| OccurredUtc | DateTime | 時間 |
| ActorUserId | string(50)，可空 | 行為者；系統為 null |
| ActorKind | string(20) | `user`、`opsService`、`system` |
| Channel | string(20) | `ui`、`mcp`、`job`、`opsCenter` |
| TokenId | long，可空 | MCP 權杖 id（不存權杖本身）；mcp4 的 OAuth 授權 id 屆時另議 |
| Action | string(100) | 動作代碼 |
| RiskLevel | string(20) | `read`、`write`、`highRisk` |
| TargetKind | string(30)，可空 | `module`、`group`、`user`、`policy`、`job`、`healthCheck`、`incident`、`channel`、`taskRule`、`app`… |
| TargetId | string(100)，可空 | 目標 id 或代碼 |
| TargetLabel | string(200)，可空 | 當下的顯示名快照（目標被刪後仍看得懂） |
| ArgumentsHash | string(64)，可空 | MCP 參數的 SHA-256 小寫十六進位 |
| ResultCount | int，可空 | 查詢類回傳筆數 |
| SummaryJson | string（text），可空 | 摘要與差異、MCP 的結構化摘要（參數摘要、前 20 個記錄 id、client 名稱與版本、耗時、operation id、轉送點工作編號）；上限 16,000 字元 |
| ConfirmationId | long，可空 | 高風險動作的確認 id（mcp1 的 `Dynamic_McpConfirmation.Id`） |
| ConfirmationOutcome | string(20)，可空 | `preview`、`confirmed`、`expired`、`mismatch` |
| Result | string(20) | `success`、`denied`、`invalid`、`failed` |
| ReasonCode | string(50)，可空 | 被拒或失敗的原因代碼（`unknownAccount`、`wrongPassword`、`scope`、`rateLimited`…） |
| RequestId | string(32)，可空 | ＝`X-Request-Id` |
| ClientIp | string(45)，可空 | IPv6 最長 45 字 |

- 索引：`OccurredUtc`；`(ActorUserId, OccurredUtc)`；`(Action, OccurredUtc)`；`(TokenId, OccurredUtc)`（MCP 每日上限從稽核計數，MCP 設計 B18）；`(TargetKind, TargetId, OccurredUtc)`。
- 不設外鍵（使用者、權杖被刪後稽核仍要留）。
- 保留：`ops.auditRetentionDays`（出廠 365）。

**`Dynamic_OpsIncident`**

| 欄位 | 型別 | 說明 |
|---|---|---|
| Id | long，自動編號主鍵 | 事件編號 |
| Fingerprint | string(16) | **唯一索引**（一個指紋一列，O2-T4） |
| FingerprintVersion | int | 指紋演算法版本（§10.2.6） |
| Source | string(20) | `server`、`client`、`config`、`health`、`security`、`channel` |
| Category | string(20) | `config`、`defect`、`infra`、`security` |
| Severity | string(20) | `info`、`warning`、`critical` |
| Status | string(20) | `open`、`acknowledged`、`resolved` |
| Code | string(100) | 例外型別全名、檢查代碼或錯誤碼 |
| Title | string(200) | 正規化訊息（健康檢查類是檢查代碼＋狀態，畫面依語系組字） |
| TitleParamsJson | string，可空 | 畫面組字用的參數（數值、門檻），不含記錄值 |
| ModuleKey | string(100)，可空 | |
| RuleKind | string(20)，可空 | |
| RuleId | long，可空 | |
| Route | string(200)，可空 | 路由樣板 |
| OccurrenceCount | long | 次數 |
| FirstSeenUtc、LastSeenUtc | DateTime | |
| FirstVersion、LastVersion | string(50) | 產品版本 |
| LastRequestId | string(32)，可空 | |
| Regression | bool | 是否重開過 |
| ReopenCount | int | |
| AcknowledgedBy | string(50)，可空 | |
| AcknowledgedUtc | DateTime，可空 | |
| ResolvedBy | string(50)，可空 | 手動結案者；自動結案為 null |
| ResolvedUtc | DateTime，可空 | |
| ResolvedReason | string(20)，可空 | `recovered`、`expired`、`ruleResumed`、`ruleDeleted`、`manual` |
| LastNotifiedUtc | DateTime，可空 | 重複提醒用 |

- 索引：`Fingerprint` 唯一；`(Status, Severity, LastSeenUtc)`（監控頁與重複提醒）；`(Status, LastSeenUtc)`（自動結案）；`ResolvedUtc`（清理）。
- 保留：已結案的 `ops.incidentRetentionDays`（出廠 180）。

**`Dynamic_OpsIncidentSample`**

| 欄位 | 型別 | 說明 |
|---|---|---|
| Id | long，自動編號主鍵 | |
| IncidentId | long | 外鍵到事件，刪除串聯 |
| OccurredUtc | DateTime | |
| RequestId | string(32)，可空 | |
| UserId | string(50)，可空 | |
| Version | string(50)，可空 | |
| Route | string(200)，可空 | |
| RawMessage | string(500)，可空 | 原始訊息（C2：只留在產品內） |
| RawStack | string(4000)，可空 | |
| Browser | string(200)，可空 | 前端 |
| Locale | string(20)，可空 | 前端 |

- 索引：`(IncidentId, OccurredUtc)`；`OccurredUtc`（清理）。
- 每件事件最多 20 筆；保留 `ops.incidentSampleRetentionDays`（出廠 14）。

**`Dynamic_HealthCheck`**（規則表，啟動時補列，比照 B7「插入，撞唯一鍵就略過」）

| 欄位 | 型別 | 說明 |
|---|---|---|
| Code | string(50)，主鍵 | 檢查代碼，小寫 |
| Enabled | bool，可空 | null＝出廠值 |
| IntervalMinutes | int，可空 | null＝出廠值 |
| WarnThreshold | long，可空 | null＝出廠值 |
| CriticalThreshold | long，可空 | null＝出廠值 |
| ModifiedUserId | string(50)，可空 | |
| ModifiedUtc | DateTime，可空 | |
| LastProbeUtc | DateTime，可空 | 只有 `database.writable` 用（寫入探測寫這欄） |
| LastCheckedUtc | DateTime，可空 | |
| LastStatus | string(20)，可空 | |
| LastValue | long，可空 | |
| LastMessageCode | string(50)，可空 | |
| NextDueUtc | DateTime | 排程型檢查的下次時間（看門狗型只在記憶體排程，這欄僅供顯示） |

**`Dynamic_HealthCheckResult`**

| 欄位 | 型別 | 說明 |
|---|---|---|
| Id | long，自動編號主鍵 | |
| Code | string(50) | |
| CheckedUtc | DateTime | |
| Status | string(20) | |
| Value | long，可空 | |
| MessageCode | string(50)，可空 | |
| DurationMs | int | |
| InstanceId | string(100) | 哪個實例跑的 |

- 索引：`(Code, CheckedUtc)`（趨勢）；`CheckedUtc`（清理）。
- 量：七個檢查依出廠間隔一天約 2,000 列（`database.writable` 每分鐘佔大半），30 天約 6 萬列。
- 保留：`ops.healthResultRetentionDays`（出廠 30）。

**`Dynamic_RuleHealth`**

| 欄位 | 型別 | 說明 |
|---|---|---|
| RuleKind | string(20) | 主鍵之一；O2 只有 `taskRule` |
| RuleId | long | 主鍵之一；不設外鍵（日後的規則種類在不同表），刪規則時由 handler 一起刪 |
| ConsecutiveFailures | int | |
| StreakStartedUtc | DateTime，可空 | 這一串第一次失敗 |
| LastFailureUtc | DateTime，可空 | |
| LastErrorCode | string(100)，可空 | 例外型別短名或錯誤碼 |
| LastErrorMessage | string(200)，可空 | 正規化訊息 |
| LastRequestId | string(32)，可空 | |
| PausedUtc | DateTime，可空 | 非空＝系統暫停中 |
| PauseCount | int | 累計被暫停幾次 |
| SkippedWhilePaused | int | 這次暫停期間略過幾次 |
| ResumedUtc | DateTime，可空 | |
| ResumedBy | string(50)，可空 | |

**`Dynamic_AlertChannel`**

| 欄位 | 型別 | 說明 |
|---|---|---|
| Kind | string(20)，主鍵 | O2 只有 `smtp` |
| Enabled | bool | |
| Host | string(255)，可空 | |
| Port | int | 出廠 587 |
| Security | string(20) | `none`、`startTls`、`sslOnConnect` |
| UserName | string(255)，可空 | |
| PasswordProtected | string（text），可空 | Data Protection 加密後的字串 |
| FromAddress | string(255)，可空 | |
| FromName | string(100)，可空 | |
| ToAddresses | string(2000)，可空 | 逗號分隔、1–20 個、應用層驗格式 |
| LastSuccessUtc、LastFailureUtc | DateTime，可空 | |
| LastError | string(500)，可空 | 階段＋伺服器回應碼，不含例外全文 |
| ConsecutiveFailures | int | |
| ModifiedUserId | string(50)，可空 | |
| ModifiedUtc | DateTime，可空 | |

**`Dynamic_AlertOutbox`**

| 欄位 | 型別 | 說明 |
|---|---|---|
| Id | long，自動編號主鍵 | |
| Channel | string(20) | `smtp` |
| IncidentId | long，可空 | 摘要信為 null；不設外鍵（事件清掉後佇列列仍可留到自己的保留天數） |
| Transition | string(20) | `opened`、`regressed`、`escalated`、`renotify`、`digest` |
| Subject | string(200) | |
| Body | string(4000) | |
| CreatedUtc | DateTime | |
| NextAttemptUtc | DateTime | |
| Attempts | int | |
| Status | string(20) | `pending`、`sent`、`failed`、`skipped`、`digested` |
| LastError | string(500)，可空 | |
| SentUtc | DateTime，可空 | |

- 索引：`(Status, NextAttemptUtc)`；`SentUtc`（節流計數與清理）；`CreatedUtc`（清理）。
- 保留：`ops.healthResultRetentionDays`（與健康紀錄共用一個天數，說明改成「健康檢查與告警寄送紀錄」）。

**SQLite 與 PostgreSQL 的差異**：只有 EF 依 provider 產生的型別不同（`INTEGER`／`bigint`、`TEXT`／`text`、時間在 SQLite 是 `TEXT`、PostgreSQL 是 `timestamptz`、布林 `INTEGER`／`boolean`、自動編號 `AUTOINCREMENT`／identity）。沒有部分索引、沒有 provider 專屬的 filter 或預設值 SQL，兩份 migration 都不手改。

### 17.5 API

全部在 `/api/v3/dynamic` 底下（經 `SessionMiddleware`；B1）。權限「管理員」＝`ISystemAdminRequest`。

| 方法 | 路徑 | 權限 | 用途 |
|---|---|---|---|
| GET | `system/ops/overview` | 管理員 | 總覽 |
| GET | `system/ops/health-checks` | 管理員 | 檢查清單（含設定的生效值、出廠值、範圍與最新狀態） |
| PUT | `system/ops/health-checks/{code}` | 管理員 | 改開關、間隔、門檻 |
| POST | `system/ops/health-checks/{code}/run` | 管理員 | 立即檢查 |
| GET | `system/ops/health-checks/{code}/results` | 管理員 | 趨勢 |
| GET | `system/ops/incidents` | 管理員 | 事件清單 |
| GET | `system/ops/incidents/{id}` | 管理員 | 事件詳細（含樣本） |
| POST | `system/ops/incidents/{id}/acknowledge` | 管理員 | 確認 |
| POST | `system/ops/incidents/{id}/resolve` | 管理員 | 手動結案 |
| GET | `system/ops/channels/smtp` | 管理員 | 讀 SMTP 設定 |
| PUT | `system/ops/channels/smtp` | 管理員（Demo 站 403） | 存 SMTP 設定 |
| POST | `system/ops/channels/smtp/test` | 管理員（Demo 站 403） | 寄測試信 |
| GET | `system/ops/audit` | 管理員 | 稽核清單 |
| GET | `system/ops/audit/{id}` | 管理員 | 稽核詳細 |
| GET | `system/ops/audit/actions` | 管理員 | 動作目錄（分組、標籤鍵、風險級別），給篩選用 |
| POST | `modules/{key}/task-rules/{ruleKey}/resume` | 設計器編輯＋共用主檔設計（同改規則） | 恢復自動暫停的規則 |
| GET | `modules/{key}/task-rules` | 既有 | 每條規則多帶 `autoPause` |

（路徑命名為主控判斷，實作可調；稽核放在 `system/ops/` 底下是為了和 MCP 的讀稽核工具歸 `ops.read` 同一個命名空間。）

**DTO**（欄位名 camelCase；時間 ISO 8601 UTC）：

```ts
// GET system/ops/overview
OpsOverview {
  overall: 'ok'|'warning'|'critical'|'unknown';
  memoryOnly: boolean;               // 有狀態只在記憶體、尚未寫入資料庫（§17.2.9）
  pendingIncidentWrites: number;
  checks: HealthCheckState[];
  openIncidents: { critical: number; warning: number; info: number;
                   byCategory: Record<'config'|'defect'|'infra'|'security', number> };
  needsAttention: IncidentSummary[]; // 最多 10 件
  jobs: ScheduledJobInfo[];          // 沿用 ops info
  channel: { kind: 'smtp'; configured: boolean; enabled: boolean;
             lastSuccessUtc?: string; lastFailureUtc?: string; consecutiveFailures: number };
  version: string; commit: string; edition: 'Cloud'|'OnPrem'; database: string;
}
HealthCheckState {
  code: string; category: string; runner: 'watchdog'|'scheduler';
  status: 'ok'|'warning'|'critical'|'error'|'notConfigured'|'notApplicable'|'pending';
  value?: number; unit: 'none'|'basisPoints'|'bytes'|'count'|'hours';
  messageCode?: string; checkedUtc?: string; nextDueUtc?: string;
  fromMemory: boolean; incidentId?: number;
}
// GET system/ops/health-checks
HealthCheckConfig {
  code: string; category: string; runner: string; unit: string;
  direction: 'higherIsWorse'|'lowerIsWorse'|'passFail';
  applicable: boolean;               // 這個資料庫種類適不適用
  enabled: Setting<boolean>; intervalMinutes: Setting<number>;
  warn?: Setting<number>; critical?: Setting<number>;   // passFail 沒有門檻
  state: HealthCheckState;
}
Setting<T> { value: T; factory: T; isFactory: boolean; min?: number; max?: number }
// PUT system/ops/health-checks/{code}；null＝回到出廠值，欄位省略＝不變
{ enabled?: boolean|null; intervalMinutes?: number|null; warn?: number|null; critical?: number|null }
// POST .../run → HealthCheckState
// GET .../results?fromUtc&toUtc&limit(≤500，預設 288)
{ items: { checkedUtc: string; status: string; value?: number; durationMs: number }[]; truncated: boolean }
// GET system/ops/incidents?status=open,acknowledged&category&severity&cursor&limit(≤100，預設 50)
{ items: IncidentSummary[]; nextCursor?: string }
IncidentSummary {
  id: number; fingerprint: string; source: string; category: string; severity: string; status: string;
  title: string; titleParams?: Record<string, string|number>; code: string;
  moduleKey?: string; ruleKind?: string; ruleId?: number; route?: string;
  occurrenceCount: number; firstSeenUtc: string; lastSeenUtc: string; firstVersion: string; lastVersion: string;
  regression: boolean; reopenCount: number;
  acknowledgedBy?: string; acknowledgedUtc?: string; resolvedBy?: string; resolvedUtc?: string; resolvedReason?: string;
  handler: 'designer'|'it'|'vendor'|'admin'|'contactVendor';
  manualResolvable: boolean;
}
IncidentDetail extends IncidentSummary {
  samples: { occurredUtc: string; requestId?: string; userId?: string; version?: string; route?: string;
             rawMessage?: string; rawStack?: string; browser?: string; locale?: string }[];
  history: AuditSummary[];           // 這件事件的確認與結案
}
// POST .../acknowledge  { expectedStatus: 'open' }              → IncidentSummary
// POST .../resolve      { expectedStatus: 'open'|'acknowledged'; note?: string /* ≤500，進稽核摘要 */ } → IncidentSummary
// GET system/ops/channels/smtp
SmtpChannel {
  enabled: boolean; host?: string; port: number; security: 'none'|'startTls'|'sslOnConnect';
  userName?: string; hasPassword: boolean; passwordUnreadable: boolean;
  fromAddress?: string; fromName?: string; toAddresses: string[];
  lastSuccessUtc?: string; lastFailureUtc?: string; lastError?: string; consecutiveFailures: number;
  editable: boolean;                 // Demo 站為 false
}
// PUT system/ops/channels/smtp；password：省略＝不變、""＝清除、其他＝新密碼
{ enabled: boolean; host: string; port: number; security: string; userName?: string; password?: string;
  fromAddress: string; fromName?: string; toAddresses: string[] }
// POST system/ops/channels/smtp/test → { sentTo: string[] }
// GET system/ops/audit?fromUtc&toUtc&actorUserId&action&actionGroup&channel&result&targetKind&targetId&cursor&limit(≤100)
{ items: AuditSummary[]; nextCursor?: string }
AuditSummary {
  id: number; occurredUtc: string; actorUserId?: string; actorName?: string; actorKind: string; channel: string;
  tokenId?: number; action: string; riskLevel: string;
  targetKind?: string; targetId?: string; targetLabel?: string;
  result: string; reasonCode?: string; requestId?: string; clientIp?: string;   // Demo 站一律不回 clientIp
}
AuditDetail extends AuditSummary {
  summary?: object; argumentsHash?: string; resultCount?: number;
  confirmationId?: number; confirmationOutcome?: string;
}
// GET system/ops/audit/actions → { code: string; group: string; labelKey: string; riskLevel: string }[]
// GET modules/{key}/task-rules：每條規則多一欄
autoPause?: { pausedUtc: string; consecutiveFailures: number; lastErrorCode?: string; lastErrorMessage?: string;
              lastRequestId?: string; skippedWhilePaused: number; pauseCount: number } | null;
failureStreak?: { consecutiveFailures: number; lastFailureUtc: string } | null;   // 未暫停但有連續失敗
// POST modules/{key}/task-rules/{ruleKey}/resume → 該規則（autoPause 為 null）
```

**錯誤碼**（`ServiceResult` 的 `Code`；訊息照既有 `*Messages.cs` 規則四語）：

| HTTP | 代碼 | 時機 |
|---|---|---|
| 400 | （欄位驗證） | 間隔或門檻超出範圍、門檻先後不合理、Email 格式、收件信箱 0 或超過 20 個、埠不在 1–65535、時間區間超過 366 天、`limit` 超出 |
| 403 | — | 非管理員；Demo 站改通道或寄測試信 |
| 404 | — | 檢查代碼、事件、稽核列、規則不存在 |
| 409 | `incidentStateChanged` | `expectedStatus` 和目前狀態不同（別人先按了） |
| 409 | `incidentManagedBySource` | 對健康檢查類、通道類、暫停類事件按結案 |
| 409 | `healthCheckBusy` | 同一檢查正在跑或 60 秒內剛按過 |
| 409 | `healthCheckNotApplicable` | 對這個資料庫種類不適用的檢查按立即檢查 |
| 409 | `smtpNotConfigured` | 寄測試信但主機、寄件地址或收件信箱沒設，或密碼解不開 |
| 409 | `smtpTestTooSoon` | 30 秒內重複寄測試信 |
| 409 | `ruleNotPaused` | 恢復一條沒有暫停的規則 |
| 422 | `smtpTestFailed` | 測試信寄不出去；`params` 帶 `stage`（`connect`、`tls`、`auth`、`send`）與 `serverCode` |
| 503 | `databaseUnwritable` | 資料庫唯讀或空間滿（§17.2.9），帶 `Retry-After` |

### 17.6 O2 的政策項與健康檢查出廠值

#### 17.6.1 政策（登記進 `PolicyCatalog`，分類 `ops`；全部只限全系統）

| 代碼 | 說明 | 型別 | 出廠 | 技術上下限 |
|---|---|---|---|---|
| `ops.monitorScanMinutes` | 送告警信、跑排程型健康檢查的間隔 | 整數（分） | 1 | 1–60 |
| `ops.upkeepMinutes` | 自動結案、重複提醒、清理的間隔 | 整數（分） | 60 | 5–1440 |
| `ops.healthResultRetentionDays` | 健康檢查與告警寄送紀錄保留天數 | 整數（天） | 30 | 7–365 |
| `ops.incidentRetentionDays` | 已結案維運事件保留天數 | 整數（天） | 180 | 30–1095 |
| `ops.incidentSampleRetentionDays` | 事件原始樣本（伺服器例外與前端錯誤原文）保留天數 | 整數（天） | 14 | 1–90 |
| `ops.auditRetentionDays` | 稽核紀錄保留天數 | 整數（天） | 365 | 90–3650 |
| `ops.incidentRenotifyHours` | 嚴重事件沒人確認時重複提醒間隔 | 整數（時） | 24 | 1–168 |
| `ops.incidentAutoResolveHours` | 缺陷、客戶設定錯誤碼、安全事件多久沒再出現就自動結案 | 整數（時） | 72 | 1–720 |
| `ops.rulePauseConsecutiveFailures` | 規則連續失敗幾次自動暫停（C3） | 整數（次） | 5 | 3–1000 |
| `ops.rulePauseWindowMinutes` | 連續失敗的計算時間窗（C3） | 整數（分） | 60 | 1–1440 |
| `ops.configIssueRecipients` | 客戶設定類事件與規則自動暫停的站內收件人（B14） | 收件人（另允許 `ruleEditor`） | `ruleEditor,admins` | 1–20 項 |
| `ops.alertRecipients` | 基礎設施、產品缺陷、安全事件的站內收件人 | 收件人 | `admins` | 1–20 項 |
| `ops.alertEmailCategories` | 哪些類別的事件寄告警信 | 多選（`infra`、`security`、`defect`、`config`） | `infra,security,defect` | — |
| `ops.alertEmailMinSeverity` | 寄告警信的最低嚴重度 | 單選（`warning`、`critical`） | `warning` | — |
| `ops.alertEmailMaxPerHour` | 每小時最多寄幾封告警信，超過併成摘要 | 整數（封） | 20 | 1–200 |

- 和 §11 初稿的差異（§11 已同步）：`ops.clientErrorSampleRetentionDays` 改名 `ops.incidentSampleRetentionDays`（伺服器例外原文也是樣本，同樣敏感）；`ops.alertRecipients` 只管站內，Email 收件人改在通道設定（O2-U1）；新增 `ops.monitorScanMinutes`、`ops.upkeepMinutes`（`IScheduledJob` 規定每個工作要有間隔政策）與三項 Email 政策。
- `ops.rulePauseConsecutiveFailures`、`ops.rulePauseWindowMinutes` 改值後，從下一次失敗起用新值；已經在計的那一串不重算。
- 間隔類政策照 §10.6：下一次排程開始生效。
- 四語文案；政策頁「維運」分類。

#### 17.6.2 健康檢查出廠值與技術上下限（`HealthCheckCatalog`，T12）

| 代碼 | 開關出廠 | 間隔出廠（分）／範圍 | 警告出廠／範圍 | 嚴重出廠／範圍 | 方向 |
|---|---|---|---|---|---|
| `database.writable` | 開 | 1／1–60 | — | — | 成敗 |
| `storage.diskFree` | 開 | 5／1–1440 | 2000／100–5000（萬分比） | 1000／50–5000 | 越小越糟 |
| `scheduler.jobs` | 開 | 5／1–1440 | 3／1–100 | 10／1–1000 | 越大越糟 |
| `database.walSize` | 開 | 15／5–1440 | 268435456／16 MB–16 GB | 1073741824／16 MB–64 GB | 越大越糟 |
| `database.integrity` | 開 | 1440／60–10080 | — | 1／1–1（任何問題即嚴重） | 越大越糟 |
| `backup.freshness` | 開 | 60／15–1440 | 26／1–720（時） | 50／1–720 | 越大越糟 |
| `security.loginFailures` | 開 | 5／1–60 | 20／1–10000 | 100／1–100000 | 越大越糟 |

- `database.writable` 不能關（寫死：它是唯讀與空間滿唯一的偵測，關掉等於失明；§17.9）；其他可關。
- `security.loginFailures` 的計數窗口固定 15 分鐘（技術限制；門檻可調就夠）。

### 17.7 PostgreSQL 相容注意

O2 在 pg0 之後，合併前兩種資料庫都要驗（使用者裁決 §3 第 10 條）。

- **migration**：一組、兩份、同名，EF 產生不手改；`MigrationConsistencyTests` 把關。沒有部分索引：事件的「一個指紋一列」用全表唯一索引，不用 `WHERE Status <> 'resolved'` 這種兩邊寫法不同的 filter（MCP §13.3 同一原則）。
- **時間**：所有新時間欄位名稱以 `Utc` 結尾、`Kind=Utc`（模型測試與 Kind 守衛會抓）。`Notification.CreatedTime` 仍是牆上時間（`IBusinessTime`），寫通知時照既有寫法，不混用。
- **並行**（以 PostgreSQL 為準，不靠 `WriteGate` 或全庫鎖；§10.7）：
  - 事件的新增或更新：先條件式更新（`WHERE Fingerprint = ? AND Status = ?`），影響 0 列再新增；新增撞唯一鍵（`IDatabaseErrors.IsUniqueViolation`）就重來，最多三次。撞鍵是 `SaveChanges` 的錯誤，有 savepoint 保護，交易還能用（pg0 §4）；`ExecuteUpdate` 的錯誤不能在交易內接住後繼續（backend 規則），所以順序是「先 `ExecuteUpdate`、後 `SaveChanges` 新增」。
  - 計數一律 `SET x = x + n`，不讀出來加完再寫回。
  - 規則失敗計數、暫停的條件式更新見 §17.2.7；兩個並行失敗同時達門檻時只有一個交易影響 1 列，只開一次暫停事件。
  - 確認與結案用 `expectedStatus` 的條件式更新，影響 0 列回 409。
  - 寄送佇列：`ops.monitor` 有租約，同一時間只有一個實例在送；送之前以條件式更新把列標成送出中（`Attempts = Attempts + 1 WHERE Id = ? AND Status = 'pending' AND Attempts = 讀到的值`），避免租約交接的邊界重寄。
- **交易外的 `ExecuteUpdate`／`ExecuteDelete`**：O2 沒有。清理、計數、狀態更新全部在交易型請求內（經全庫寫入鎖）。唯一的例外是看門狗的寫入探測本身：它也是交易型請求，探測失敗時那個交易直接回滾。
- **provider 專屬的地方**（交付說明要列出）：`database.integrity` 的 `PRAGMA quick_check`、`PRAGMA foreign_key_check` 與 `database.walSize` 的 WAL 檔路徑，放在 `Persistence` 的 `SqliteDatabaseHealthProbe`；PostgreSQL 的實作 `PostgresDatabaseHealthProbe` 放在 `Persistence.Postgres`（O2 只回「不適用」與產品層檢查）。Application 只看 `IDatabaseHealthProbe` 介面。
- **錯誤分類**：唯讀、空間滿走 `IDatabaseErrors`（§17.2.9），不 catch `SqliteException`、不比對訊息。
- **字串比對**：動作代碼、檢查代碼、指紋一律區分大小寫的等值比對（`COLLATE "C"`）；動作分組用 `IN` 展開，不用 `LIKE`（SQLite 的 `LIKE` 對 ASCII 不分大小寫，與 PostgreSQL 不同）。
- **大數值**：`long` 在兩邊都是 64 位元整數；萬分比、位元組都放得下。
- **唯讀的重現方式**（測試與驗收）：PostgreSQL 用 `ALTER DATABASE <db> SET default_transaction_read_only = on` 再清連線池，新連線一律 `25006`；SQLite 檔案權限改唯讀對已開啟的連線無效，驗收時連線字串加 `Pooling=False` 再改權限（⚠️ 未實測，o2b 第一步驗證；§17.10.5）。

### 17.8 測試與驗收

先說驗收方式再動手（AGENTS.md）。下表「雙跑」欄標 ✓ 的類別標 `[Trait("Category", "Postgres")]`，gate 另在 PostgreSQL 上跑（pg0 §6.3 的 filter）。自動測試一律用 `TimeProvider` 的手動時鐘（O1 的 `ManualClock`），不真的等待。

**後端自動測試**

| 編號 | 前置 | 操作 | 預期 | 雙跑 |
|---|---|---|---|---|
| A01 | 空資料庫 | 套 migration | 八張表存在；`MigrationConsistencyTests` 兩個 provider 都沒有待產生的變更 | —（一致性測試不需 PostgreSQL 伺服器，照 pg0 §5.3 隨 SQLite 跑；PostgreSQL 的建表由樣板資料庫套 migration 驗證） |
| A02 | 管理員 | 改某模組權限 | 同一交易寫一列 `permission.update`、風險高、摘要只含變動的格子；讓稽核寫入丟例外時，權限沒有被改 | ✓ |
| A03 | 已有帳號 `sales01` | 用錯密碼登入；用不存在的帳號登入 | 各一列 `login.failed`：前者目標是 `sales01`、原因 `wrongPassword`；後者目標空、原因 `unknownAccount`、摘要不含輸入字串 | ✓ |
| A04 | 讓稽核寫入失敗 | 正確密碼登入 | 登入成功，Error log 一則 | — |
| A05 | 反射掃描 Application 組件 | 列出所有交易型、且是管理員或設計器寫入的請求 | 全部實作 `IAuditedRequest`，或在白名單且有理由 | — |
| A06 | 改政策、重設密碼 | 讀稽核摘要 | 政策有舊值與新值；重設密碼的摘要沒有密碼；摘要裡任何 `fwp_`、`fwc_` 開頭的值都是 `***` | — |
| A07 | 400 列稽核、時間分散 | 依動作分組、行為者、時間區間分頁查詢 | 結果與篩選一致、keyset 不重不漏；非管理員 403；Demo 登入開啟時回應沒有 `clientIp` | ✓ |
| A08 | 稽核列超過保留天數 | 跑 `ops.upkeep` | 超過的被刪、未超過的留著、多一列 `ops.retention.purge`；分批時每批一個交易 | ✓ |
| B01 | 兩個實例的收件匣 | 同一指紋各送 3 次、各自 flush | 一列事件、次數 6、狀態開啟；站內通知只發一次 | ✓ |
| B02 | 一件已結案事件 | 同指紋再出現 | 同一列重開、`Regression` 真、`ReopenCount` 1、再通知 | ✓ |
| B03 | 兩個管理員 | 同時確認同一件事件 | 一個成功、一個 409 `incidentStateChanged` | ✓ |
| B04 | 健康檢查類事件 | 呼叫結案 | 409 `incidentManagedBySource` | — |
| B05 | 缺陷事件，最近一次出現早於自動結案時數 | 跑 `ops.upkeep` | 結案、原因 `expired`；嚴重未確認事件超過重複提醒時數時再通知一次 | ✓ |
| B06 | 一個 API 丟未處理例外 | 等收件匣 flush | 事件類別產品缺陷、嚴重度警告、有一筆樣本含原始訊息；前端錯誤同理但嚴重度資訊、不發通知 | — |
| B07 | 每件事件已有 20 筆樣本 | 再來一筆 | 仍是 20 筆、最舊的被刪 | ✓ |
| C01 | 寫入探測換成唯讀資料庫（SQLite：DI 換成 `Mode=ReadOnly` 的連線；PostgreSQL：`default_transaction_read_only`） | 看門狗跑一輪 | 狀態板 `database.writable` 嚴重、訊息代碼 `readOnly`；事件放進待寫；直送佇列有一封 | ✓ |
| C02 | 接著把資料庫改回可寫 | 看門狗再跑一輪 | 待寫的事件與站內通知補寫、`database.writable` 結案原因 `recovered` | ✓ |
| C03 | 資料庫唯讀 | 一般使用者存檔 | 503 `databaseUnwritable` 加 `Retry-After`，沒有新增產品缺陷事件 | ✓ |
| C04 | 磁碟探測換成假的 `IDiskSpace`（剩 15%、5%） | 看門狗兩輪 | 先開警告、再升嚴重（狀態退回開啟、再通知）；回到 30% 時結案 | — |
| C05 | 一個工作連續失敗 3 次；另一個工作的下次預定時間落後四個間隔 | `scheduler.jobs` | 前者警告；後者嚴重 | — |
| C06 | 改 `storage.diskFree` 門檻成警告 500、嚴重 1000 | PUT | 400（越小越糟時警告要大於嚴重）；改成合理值後生效；送 null 回到出廠值；寫稽核 | — |
| C07 | 排程器關閉 | 看門狗 | 寫入探測、磁碟、排程器三項照跑 | — |
| C08 | SQLite | `database.walSize`、`database.integrity` | 有數值與結果；PostgreSQL 上 `walSize` 回不適用、不出現在清單、按立即檢查 409 | ✓ |
| D01 | SMTP 換成假的 `IMailSender` | 開一件基礎設施警告事件 | 佇列一列；`ops.monitor` 送出；主旨與內文只有正規化訊息 | ✓ |
| D02 | 假寄送器一律失敗 | 跑五次重試（快轉時鐘） | 間隔 1、5、15、60 分；第 5 次後標失敗、開 `channel.smtp` 事件、這件事件沒有進佇列；之後一封成功就結案 | ✓ |
| D03 | 每小時上限 3 | 一次開 5 件事件 | 寄 3 封，2 列併入摘要；下一個小時一封摘要信列出那 2 件 | ✓ |
| D04 | 客戶設定類事件 | 出廠政策 | 只有站內通知，不進佇列 | — |
| D05 | 存 SMTP 設定 | 讀資料表與 GET | 密碼欄位是加密字串；GET 只回 `hasPassword`；換一套 Data Protection 金鑰後 GET 回 `passwordUnreadable` | — |
| D06 | Demo 登入開啟 | PUT 通道、寄測試信 | 403 | — |
| D07 | 假寄送器在連線階段失敗 | 寄測試信 | 422 `smtpTestFailed`、`stage` 為 `connect`、回應不含例外全文；30 秒內再寄 409 `smtpTestTooSoon` | — |
| E01 | 一條注入失敗的任務規則、門檻 5 | 存檔 5 次 | 前 4 次 500、計數 1–4；第 5 次後暫停、暫停事件嚴重、出廠收件人（最後修改者＋管理員）各一則 `opsRulePaused`、稽核 `taskRule.autoPause` | ✓ |
| E02 | 接續 E01 | 再存檔 | 成功、沒產生任務、`SkippedWhilePaused` 加一；手動套用清單沒有這條規則 | ✓ |
| E03 | 失敗 3 次後成功 1 次，再失敗 4 次 | — | 不暫停（中間有成功，計數重來） | ✓ |
| E04 | 第一次失敗後 61 分鐘才第 5 次 | — | 不暫停，計數從 1 重算 | — |
| E05 | 兩個請求並行，第 4、第 5 次同時失敗 | — | 只暫停一次、只一件暫停事件、只一組通知 | ✓ |
| E06 | 已暫停、移除故障注入 | 有編輯權的設計者按恢復；再存檔 | 恢復成功、暫停事件結案 `ruleResumed`、稽核 `taskRule.resume`；存檔產生任務 | ✓ |
| E07 | 已暫停、故障仍在 | 恢復後再失敗 5 次 | 再次暫停、暫停事件以回歸重開 | ✓ |
| E08 | 只有讀權的設計者 | 按恢復 | 403；沒暫停的規則按恢復 409 `ruleNotPaused` | — |
| E09 | 失敗計數的寫入丟例外 | 存檔 | 使用者照樣收到原本的 500，Error log 一則 | — |
| E10 | 事件串聯過深 | 存檔 | 409 照舊；開一件客戶設定事件（錯誤碼 `event.tooDeep`），不計數、不暫停 | — |
| E11 | 收件人政策設成只有 `ruleEditor`，而最後修改者已停用 | 觸發暫停 | 改送全部系統管理員、記 Warning | — |
| E12 | 外層請求巢狀呼叫內層、內層丟例外 | — | 只在最外層記一次失敗 | — |
| F01 | `ops.upkeep` 清理 | 健康紀錄、樣本、已結事件、佇列分別超過天數 | 各自被清；未結事件與其樣本不受事件天數影響 | ✓ |

**前端自動測試**（vitest）

- 監控頁：狀態卡依 overview 顯示；`memoryOnly` 時有頁首提示；不能手動結案的事件沒有結案按鈕；409 `incidentStateChanged` 提示後重新整理。
- 健康檢查設定：顯示出廠值與範圍、留空送 null；前端沒有任何門檻常數（grep 測試或審查）。
- 通道設定：密碼留空不送、按清除送空字串；`editable` 為否時整頁唯讀；測試信 422 顯示階段。
- 稽核頁：篩選組出正確 query；詳細顯示差異；沒有 `clientIp` 時不顯示該欄。
- 任務規則分頁：`autoPause` 有值時顯示標籤，有編輯權才有恢復；確認框帶略過次數。
- 鈴鐺：兩個新種類的標籤；四語鍵一致（`locales.vitest.spec.ts`）；`bun run typecheck`。

**手動驗收**（對應 §9 的 O2 驗收；`scripts/unattended/local-demo.sh`，SMTP 用本機的郵件收件測試工具，例如以 Docker 起 Mailpit；SQLite 與 PostgreSQL 各走一次）

| 編號 | §9 驗收項 | 操作 | 預期 |
|---|---|---|---|
| M01 | SMTP 測試信 | 告警通道填本機 SMTP、收件信箱，按寄測試信 | 收件工具收到一封；稽核有 `ops.channel.test` |
| M02 | 資料庫唯讀 | SQLite：連線字串加 `Pooling=False` 啟動，啟動後把資料庫檔與目錄改成唯讀；PostgreSQL：`ALTER DATABASE ... SET default_transaction_read_only = on` 並中斷站台連線 | 1 分鐘內監控頁 `database.writable` 嚴重、頁首提示只在記憶體；收件工具收到告警信；一般使用者存檔看到「資料庫目前無法寫入」；改回可寫後事件寫進事件表並結案、站內通知補發 |
| M03 | 塞滿測試磁碟配額 | 建一個 200 MB 的磁碟映像（macOS `hdiutil`、Linux loop device），附件根目錄指過去，用 `dd` 塞到剩 15%，再塞到剩 5% | 監控頁先警告再嚴重；站內通知與告警信各兩次（開、升級）；清掉檔案後結案 |
| M04 | 必定失敗的任務規則 | 部署設定 `Ops:FaultInjection:TaskRuleKeys` 指定一條規則，存檔 5 次 | 前 4 次錯誤提示帶錯誤代碼；第 5 次後監控頁兩件事件（產品缺陷、暫停）、站內通知、告警信（只有產品缺陷那件）；存檔不再失敗、規則分頁顯示已暫停 |
| M05 | 恢復後重新觸發 | 拿掉故障注入、重啟、按恢復，存一筆新記錄；再把故障注入加回、恢復後存 5 次 | 第一段：新記錄產生任務、暫停事件結案；第二段：再次暫停、事件標回歸 |
| M06 | 稽核：改權限與登入失敗 | 改一個模組的權限；另一個瀏覽器用錯密碼登入 | 稽核頁各一列，詳細看得到權限差異與失敗原因 |
| M07 | 四種寬度 | 390、820、1366、1920 走查監控頁四個分頁、稽核頁、任務規則分頁的暫停標示 | 不橫捲；表格在手機變卡片；詳細在手機是全螢幕 |

**PostgreSQL**：上表雙跑欄 ✓ 的在 gate 的 PostgreSQL 步驟跑；手動驗收 M02、M04、M05 在 PostgreSQL 再走一次（`FORMWRIGHT_TEST_DB` 不適用於本機站台，照 pg0 §8 第 5 項手動起容器）。O2 新增的雙跑類別預估 6–8 個，gate 的 PostgreSQL 步驟增加約 30–60 秒（pg0c 實測一個 WebAPI 類別約 0.4 秒／測試；⚠️ 未實測）。

**手冊**：新增「維運監控」「稽核紀錄」「告警通道」三節；任務規則章節補「連續失敗會自動暫停、怎麼恢復、暫停期間不補產任務」；常見問題補「資料庫目前無法寫入」。

### 17.9 可寫死清單增補（併入 §12）

**資料完整性**

- 稽核只能新增；保留天數清理是唯一的刪除路徑（§12 原有，O2 落實成「repository 沒有修改與單筆刪除」）。
- 一個指紋一列；健康檢查類、通道類、暫停類事件不能手動結案。
- 規則失敗計數寫在業務交易之外；寫不進去不改變回給使用者的錯誤。
- 暫停只清 `PausedUtc`，不動 `Active`。
- 事件轉換、站內通知、寄送佇列在同一個交易。

**安全**

- 監控、稽核、通道 API 只給系統管理員；設計者只看得到自己能看的規則的暫停狀態。
- 稽核摘要不自動序列化請求；`password`、`token`、`secret` 鍵與 `fwp_`、`fwc_` 開頭的值一律遮蔽。
- SMTP 密碼以 Data Protection 加密存放，API 不回明文。
- 測試信只寄到已設定的收件信箱。
- 開 Demo 登入時：通道設定與測試信停用、稽核不回來源 IP。
- 告警信與站內通知只放正規化訊息，不放原始訊息、stack、記錄值。
- 故障注入只在 `Development` 環境生效。
- `database.writable` 不能關。
- 通道失敗事件不寄信（防迴圈）。
- 收件人解析為空時改送全部系統管理員。

**技術限制**

- 稽核摘要 16,000 字元；稽核頁每頁 100 列、查詢區間最長 366 天。
- 事件標題 200 字；每件事件樣本 20 筆、原始訊息 500 字、stack 4000 字；條件式更新重試 3 次。
- 收件匣每 5 秒 flush、最多 1000 個指紋；待寫事件最多 100 個指紋。
- 看門狗每 60 秒；寫入探測連續 3 次忙碌才記警告；單一檢查最長 60 秒；立即檢查同一檢查 60 秒一次；`security.loginFailures` 窗口 15 分鐘。
- 告警信重試 1、5、15、60 分鐘、最多 5 次；測試信逾時 30 秒、同一管理員 30 秒一次；摘要信最多列 50 則；直送佇列最多 20 封。
- 清理每批 5000 列。
- 健康檢查的門檻、間隔的上下限（§17.6.2）。
- 已知限制：狀態板、收件匣、待寫事件、直送佇列、直送去重都只在單一實例記憶體；多實例時監控頁只看得到自己那台的記憶體狀態（雲端版每客戶一套單一實例時沒有影響；§15 第 8 條同一類）。
- 已知限制：資料庫連不上且啟動後從沒讀到通道設定時，告警信寄不出去。

### 17.10 主控判斷（待使用者確認）

#### 17.10.1 使用者行為類

每條：決定、理由、選錯的代價，最後一行是可以不看設計直接回答的題目（推薦答案放第一個）。

**O2-U1 告警信寄到通道設定裡的一組信箱，不是寄給使用者**

- 決定：SMTP 設定頁填 1–20 個收件信箱（例如 IT 共用信箱）；站內通知照收件人政策發給使用者。測試信只寄這組信箱。
- 理由：使用者帳號沒有 Email 欄位；替帳號加 Email 會牽動使用者管理、匯入與個資，還會變成業務 email（U9 不做）的入口。地端 IT 多半用共用信箱收告警。
- 選錯代價：要改寄給個人，要加帳號欄位與一段畫面（約一個小段）；收件信箱清單可以保留當補充。
- 題目：資料庫掛了要寄告警信時，信要寄給誰？(1) 在告警通道設定填一組信箱，例如 `it@客戶.com`、`boss@客戶.com`（推薦）(2) 替每位使用者加 Email 欄位，寄給收件人政策裡的那些人（例如全部系統管理員）。

**O2-U2 出廠只寄基礎設施、安全、產品缺陷三類，客戶設定類只發站內**

- 決定：`ops.alertEmailCategories` 出廠 `infra,security,defect`，最低嚴重度「警告」。
- 理由：客戶設定類（規則失敗、暫停）的處理者是設計者，站內通知已送到最後修改者；寄到 IT 信箱多半沒人能處理。
- 選錯代價：改政策即可。
- 題目：任務規則「出貨後建立追蹤任務」因設定錯誤被自動暫停，除了站內通知最後修改它的設計者，要不要也寄信到 IT 信箱？(1) 不寄，只站內（推薦）(2) 也寄信。

**O2-U3 監控頁、事件、樣本、稽核只給系統管理員；設計者只在規則頁看到暫停狀態**

- 決定：見 §17.2.10。設計者收到的暫停通知連到模組設計頁的任務規則分頁，看得到錯誤代碼與正規化訊息，看不到樣本原文。
- 理由：樣本原文可能含記錄內容（C2 把原文留在產品內，但不等於所有人都能看）；稽核含登入失敗與 IP。
- 選錯代價：之後要開給設計者，要加「只看自己模組的事件」的篩選與權限，約一個小段。
- 題目：設計者小王（不是系統管理員）負責的規則被暫停了，他要能看到什麼？(1) 只在他的規則頁看到「已暫停、錯誤代碼 TaskRuleXyz、最近一次是 10:32」（推薦）(2) 也能打開監控頁看這件事件與錯誤原文。

**O2-U4 事件狀態：開啟→確認→結案；部分事件不能手動結案；不能刪除**

- 決定：見 §17.2.2 狀態機。健康檢查、通道失敗、規則暫停三類只能由系統自己結案；嚴重度升級時已確認的退回「開啟」並再通知；事件不能刪，只隨保留天數清除。
- 理由：檢查還沒恢復就手動結案，下一分鐘會以「回歸」重開，製造假回歸；升級代表情況變糟，認領的人要再被叫一次；事件是稽核的一部分，刪掉就查不到。
- 選錯代價：允許手動結案只是拿掉一個檢查；允許刪除要另加稽核與權限。
- 題目：磁碟剩 15% 開了警告事件，IT 按了「確認」。後來掉到 5%，要怎麼處理？(1) 事件退回「開啟」並再通知一次（推薦）(2) 維持已確認、只更新數字、不再通知。

**O2-U5 前端錯誤只記錄不通知，伺服器 500 通知管理員**

- 決定：前端錯誤事件嚴重度「資訊」，只出現在監控頁；伺服器 500 是「警告」，新開時通知 `ops.alertRecipients` 並寄信。
- 理由：前端錯誤常來自瀏覽器外掛與網路，量大雜訊多；500 幾乎都是真的產品問題。
- 選錯代價：要通知前端錯誤只改一個出廠嚴重度。
- 題目：使用者的瀏覽器跳了一個 JavaScript 錯誤（畫面沒壞），要不要通知管理員？(1) 不通知，記在監控頁的事件清單（推薦）(2) 每個新錯誤都發站內通知並寄信。

**O2-U6 能編輯規則的人就能按「恢復」**

- 決定：恢復權限＝改那條任務規則的權限（設計器編輯權＋共用主檔設計權）；系統管理員本來就有。
- 理由：修規則的人就是要恢復的人；只給管理員會讓設計者修好後還要找人。
- 選錯代價：改成只限管理員只換一個權限宣告。
- 題目：設計者小王修好了被暫停的規則，誰可以按「恢復」？(1) 小王自己（他有這個模組的設計編輯權）（推薦）(2) 只有系統管理員。

**O2-U7 暫停期間不補產任務；存檔的人看不到提示**

- 決定：暫停期間存檔照常成功、不產生任務、存檔的人看不到任何提示；規則頁顯示「暫停期間略過 N 次存檔」，恢復時不補產。
- 理由：補產要重算每筆記錄在當時的條件，`once` 規則還要判斷是否已觸發過，複雜而且可能產出過時的任務；存檔的一般使用者不擁有規則，提示只會造成困擾。
- 選錯代價：要補產時另做「對暫停期間存過的記錄重跑」的動作（💡）；要提示時在存檔回應加一個旗標。
- 題目：規則「新客戶建立後指派業務回訪」被暫停了 2 小時，期間新增了 8 筆客戶。恢復時要怎麼辦？(1) 不補，規則頁告訴設計者「暫停期間略過 8 次存檔」，需要的話由人手動套用（推薦）(2) 恢復時自動替那 8 筆補產回訪任務。

**O2-U8 資料庫唯讀時，存檔看到「資料庫目前無法寫入」而不是「系統發生錯誤」**

- 決定：唯讀與空間滿回 503 加四語訊息「資料庫目前無法寫入，請聯絡系統管理員」，不算產品缺陷。
- 理由：使用者知道不是自己操作錯、也不是隨機錯誤；同一原因不會在事件表灌出一堆 500。
- 選錯代價：訊息文字可改；改回 500 只要拿掉一個例外處理器。
- 題目：資料庫磁碟滿了，業務按存檔時要看到哪個訊息？(1)「資料庫目前無法寫入，請聯絡系統管理員」（推薦）(2) 照現在的「系統發生錯誤，請稍後再試（錯誤代碼：…）」。

**O2-U9 稽核只記成功的管理與設計變更、登入成功與失敗**

- 決定：被權限擋下或驗證失敗的畫面操作不記；畫面的讀取不記（MCP 的讀取由 mcp1 逐筆記，U10）。設計類只記「誰在什麼時候改了哪個模組的什麼」，不存整份定義的前後差異；權限、群組、使用者、政策存前後差異。
- 理由：被擋的操作沒有改到東西，log 已有；畫面讀取量太大；設計定義差異量大，記錄歷程與套件匯出另有來源。
- 選錯代價：要記被擋的操作只改 `AuditBehavior` 的條件；要存設計差異會讓稽核表大幾倍。
- 題目：一般使用者打開權限設定頁想改權限、被系統擋下（他不是管理員），稽核要不要記這一筆？(1) 不記，只記真的改成功的（推薦）(2) 也記一筆「被拒」。

**O2-U10 公開 Demo 站停用告警通道設定、稽核不顯示 IP**

- 決定：開 Demo 登入時，告警通道整頁唯讀、測試信回 403，稽核頁不顯示來源 IP。
- 理由：Demo 站人人是管理員；能設 SMTP 主機就能讓伺服器替他連任意位址或寄信；訪客彼此看得到 IP 是個資外洩。
- 選錯代價：Demo 上看不到告警通道怎麼用，靠手冊截圖。
- 題目：公開 Demo 站上，訪客能不能自己填 SMTP 主機並寄測試信？(1) 不能，Demo 站這頁唯讀（推薦）(2) 可以，方便展示。

**O2-U11 系統管理員權限異動開安全事件並寄信**

- 決定：新增或改使用者時「系統管理員」旗標有變動，開安全事件（警告），通知 `ops.alertRecipients` 並寄信；同一個指紋，多次異動累加次數，樣本記是誰給了誰。
- 理由：§5.1 把「管理員權限異動」列為安全類；帳號被盜後第一件事通常是加管理員。
- 選錯代價：嫌吵可把安全類從 Email 類別政策拿掉，或改成只記稽核。
- 題目：管理員 A 把業務 B 設成系統管理員，要不要另外通知全部系統管理員並寄信到 IT 信箱？(1) 要（推薦）(2) 不用，記在稽核就好。

#### 17.10.2 技術類（主控裁決）

| # | 決定 | 理由與選錯代價 | 寫在 |
|---|---|---|---|
| O2-T1 | 八張表一組 migration，在 o2a 一次建好；子單元只是 commit 切法，整段一次合併 | backend 規則一段最多一組 migration；代價是 o2a 要一次定案所有欄位 | §17.1、§17.4 |
| O2-T2 | 稽核用 `IAuditedRequest`＋`AuditBehavior`（交易內）＋handler 補差異；反射測試保證覆蓋；動作目錄 `AuditActions` 靜態類別 | 宣告式不會漏、差異仍由懂資料的 handler 給；代價是約 40 個請求檔要加宣告 | §17.2.1 |
| O2-T3 | 登入稽核另開 scope 寫、失敗不擋登入；節流擋下的不逐筆記，只計指標 | 匿名者能觸發的寫入要有上限；代價是被節流的嘗試只在 log 與指標 | §17.2.1 |
| O2-T4 | 一個指紋一列、全表唯一索引，結案後重開同一列 | §5.5 本來就要重開原事件；不用部分索引，兩種資料庫一致 | §17.2.2、§17.4 |
| O2-T5 | 500 與前端錯誤走程序內收件匣，每 5 秒聚合寫入 | 請求路徑不多一次寫入；代價是當掉時最多丟 5 秒的計數（log 仍在） | §17.2.3 |
| O2-T6 | 健康檢查分看門狗（每實例、不經租約）與 `ops.monitor`（經租約） | 唯讀時排程器搶不到租約；代價是多一個 hosted service | §17.2.4 |
| O2-T7 | 唯讀與空間滿用寫入探測判斷，`IDatabaseErrors` 加 `IsReadOnly`、`IsStorageFull` | 兩種資料庫一份寫法、也涵蓋空間滿；代價是每分鐘一筆小寫入 | §17.2.4、§17.2.9 |
| O2-T8 | 寫不進資料庫時狀態板、待寫事件、直送佇列留在記憶體 | 資料庫壞時仍要告警；代價是多實例時只在本機（已知限制） | §17.2.4、§17.2.6 |
| O2-T9 | SMTP 用 MailKit（MIT 授權），不用 `System.Net.Mail.SmtpClient` | 官方文件標 `SmtpClient` 不建議新開發、不支援 465 的直接 TLS；代價是多一個套件（版本在計畫鎖定） | §17.2.6 |
| O2-T10 | SMTP 密碼用 Data Protection（purpose `ops-alert-smtp`） | 沿用既有金鑰；代價是金鑰遺失要重填 | §17.2.6 |
| O2-T11 | Email 經寄送佇列表＋`ops.monitor`，重試 1、5、15、60 分、最多 5 次 | 事件與信同交易、可重試、可節流；代價是最快約 1 分 30 秒才寄出 | §17.2.6 |
| O2-T12 | 通道失敗開 `channel.smtp` 事件、只站內 | 防自我觸發迴圈 | §17.2.6 |
| O2-T13 | 規則失敗計數在交易外的 `RuleFailureBehavior`、成功歸零在 `TaskRuleStage` 交易內；狀態放新表 `Dynamic_RuleHealth`，不改 `Dynamic_TaskRule` | 照 §5.4；不讀不寫業務表以外的熱點；代價是每次存檔多一個小查詢（只在模組有任務規則時） | §17.2.7 |
| O2-T14 | 時間窗從這一串第一次失敗起算 | 一個時間欄位就夠；代價是邊界情況比滑動窗口少算 | §17.2.7 |
| O2-T15 | `ruleEditor` 是收件人政策的動態項目，由 `PolicyDefinition.DynamicRecipients` 宣告哪些政策允許 | 其他收件人政策不會誤收這個項目 | §17.2.5 |
| O2-T16 | 故障注入 `Ops:FaultInjection:TaskRuleKeys` 只在 Development 生效 | 任務規則難以自然失敗，驗收需要；代價是正式程式多一個空介面 | §17.2.7 |
| O2-T17 | 事件與檢查的已知錯誤碼在回傳處補代碼並記 `RuleFailureNote` | 客戶設定類事件才有歸屬與指紋；代價是改八處回傳 | §17.2.2 |
| O2-T18 | 稽核動作分組由伺服器展開成代碼清單查詢 | 不靠 `LIKE`，兩種資料庫一致 | §17.2.1、§17.7 |
| O2-T19 | 健康檢查數值與門檻一律 `long`，比例用萬分比 | §10.7 不用浮點數 | §17.2.4 |
| O2-T20 | PostgreSQL 的連線與鎖、慢查詢、`amcheck`，SQLite 的 `integrity_check`、慢查詢攔截器延到 O4 | O2 先做驗收要的；這些是 L1 診斷要讀的，隨 mcpO1 一起做最省；§8 已同步 | §17.1、§8 |
| O2-T21 | OpenTelemetry 不在 O2 | O2 仍沒有匯出端（§10.2.3 的理由不變） | §17.1 |
| O2-T22 | 通知與事件標題存代碼＋參數、畫面依語系組字；告警信先用繁中，p3 後改系統預設語系 | p3 在 O2 之後 | §17.2.5、§17.2.6 |
| O2-T23 | 監控頁的記憶體狀態只代表本實例 | 雲端版單一實例；多實例時另議 | §17.9 |
| O2-T24 | 來源管道用 AsyncLocal 的 `AuditOrigin`，比照 `ActingScope` | 不必每個 handler 傳參數；mcp1 只在 `/mcp` 開一個範圍 | §17.2.1 |

#### 17.10.3 待決衝突

和既有使用者裁決（§3、§16、標「使用者裁決」者）比對後，**沒有發現衝突**。比對過、需要說明的三點：

- §3 第 8 條「連續失敗、互相觸發迴圈」：迴圈依 C3 延到 mcp2a，O2 照做，不是新的限縮。
- C3 寫「只暫停任務規則」：本節把手動套用（manual）的任務規則也算進去，與裁決字面一致；初步曾考慮把 manual 排除，那會是限縮裁決，已放棄。
- C2「原文只留在產品內」：產品內的監控頁讓系統管理員看樣本原文，符合「廠商要看就由人登入該實例」；告警信雖然是產品送出的，也只放正規化訊息（比裁決更保守，不衝突）。

#### 17.10.4 同步修改的前文（不是使用者裁決，都是主控文字）

- §8：「下表的 PRAGMA 與 `pg_*` 檢查是 O2 的東西」改成 O2 先做的項目＋其餘延到 O4（O2-T20）。
- §9：O2 列加細部設計連結；O2 驗收改寫：資料庫唯讀時站內通知寫不進資料庫，改成「恢復可寫後補發」；「恢復後重新觸發」拆成「修好後恢復會再產生任務」與「沒修好恢復會再暫停」兩件；加 SQLite 與 PostgreSQL 各走一次、加稽核頁與通道設定的寬度走查。
- §11：`ops.clientErrorSampleRetentionDays` 改名、`ops.alertRecipients` 只管站內、新增五項；表後指向 §17.6。
- §12：表後加一句指向 §17.9。
- 頁首狀態：加「O2 細部設計（§17）」。

#### 17.10.5 需要使用者先定

無。最接近的是 O2-U1（告警信收件人）：若答案是「寄給個人」，會牽動使用者帳號加 Email 欄位，等於開了業務 email 的入口（U9），但仍可在 O2 內加一個小段完成，不改產品定位，所以照推薦寫進設計。

#### 17.10.6 未驗證

- ⚠️ SQLite 唯讀的重現：`Pooling=False` 加改檔案權限，新連線是否一律 `SQLITE_READONLY`、站台在唯讀資料庫上能否繼續回應讀取（o2b 第一步實測，不成立就改用 DI 換寫入探測的連線做手動驗收）。
- ⚠️ PostgreSQL `default_transaction_read_only` 的測試寫法：設定後清 Npgsql 連線池，新連線是否立即生效、測試結束如何還原樣板資料庫（o2b 實測）。
- ⚠️ MailKit 的版本與 .NET 8 相容、授權仍是 MIT（o2c 計畫時查 NuGet 與官方 repo 並鎖版本）。
- ⚠️ `DriveInfo` 在 macOS 磁碟映像、Linux loop device、Docker overlay 回報的剩餘空間是否就是附件目錄實際可用的空間（o2b 實測）。
- ⚠️ `PRAGMA quick_check` 在大資料庫的耗時（60 秒逾時夠不夠）；Demo 資料量下實測一次。
- ⚠️ gate 的 PostgreSQL 步驟增加的時間（§17.8 粗估 30–60 秒）。
- ⚠️ Render 免費方案擋 SMTP 埠（U1 已列未查證）：對 O2 沒影響，Demo 站本來就停用通道設定。
- 本節的檔案與行號（`EventStage.cs:67-68`、`CheckEvaluator.cs:40`、`EffectApplier.cs`、`TaskRuleStage.cs`、`ManualTaskRules.cs`、`LoginHandler.cs`、`UpdateSiteThemeHandler.cs`）是 2026-11-19 讀 `772e7a54` 的結果，計畫階段再核對一次。
