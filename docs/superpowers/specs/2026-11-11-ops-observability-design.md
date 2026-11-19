# 維運、觀測與告警派工 agent 設計文件

> 2026-11-11｜平台共用能力（非應用程式）｜前置：[政策設定](2026-11-11-policy-settings-design.md)、[任務管理](2026-11-10-task-management-design.md)、[路線](2026-11-06-erp-roadmap-design.md)｜相關：[MCP server 設計](2026-11-11-mcp-server-design.md)（mcp1 地基、mcpO1／mcpO2 維運工具）
> 狀態：草稿，主控已依 builder 審查修訂；C1–C7 已由使用者 2026-11-12 裁決（§16），其餘待使用者核准。內容是總體設計＋O1 細部設計（§10）＋O2 細部設計（§17，2026-11-19 主控撰寫、同日依設計審查修訂，使用者行為類待確認見 §17.10）；這次只寫設計，不改程式；實作分 O1–O6（§9）。
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

正式版用 PostgreSQL，SQLite 只在研發與單機試用（使用者說明）。新程式不寫 SQLite 專屬寫法（使用者裁決，已寫成 `.claude/rules/backend.md` 的「PostgreSQL 相容」條）。下表的檢查 O2 先做寫入探測、空間、WAL 大小、完整性（`quick_check`、`foreign_key_check` 與產品層檢查），連線與鎖、慢查詢、`amcheck`、每週 `integrity_check` 延到 O4（細目與理由見 §17.2.4、§17.10.1 O2-U13，待使用者確認）；PRAGMA 與 `pg_*` 照既有機制隔離：raw SQL 走 Persistence 的 `ISqlDialect`（`Persistence/Dynamic/Dialects/`），非寫不可的資料庫專屬程式放在 provider 專屬類別並在交付說明列出；每個檢查都寫兩種實作。pg0 之後的維運段（O2 起）兩種資料庫都要驗。

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
- **O2**（細目見 §17.8 的 M01–M08）：SMTP 測試信寄得出去；本機分別把資料庫設成唯讀、塞滿測試磁碟配額、造一條必定失敗的任務規則（開發環境故障注入），各自在監控頁看到事件、收到告警信（資料庫唯讀時由看門狗直送）；SQLite 資料庫檔所在磁碟塞滿時存檔看到「資料庫目前無法寫入」；磁碟與規則兩項同時收到站內通知，資料庫唯讀那項的站內通知在恢復可寫後補發（唯讀期間通知寫不進資料庫，§17.2.9）；規則達門檻（60 分鐘內連續 5 次，滑動窗口）後自動暫停，修好後恢復，之後新增的記錄會產生任務（暫停期間的記錄不補產）、沒修好恢復會再次暫停並標回歸；稽核頁看得到改權限與登入失敗。資料庫唯讀與規則暫停在 SQLite 與 PostgreSQL 各走一次。四種寬度走查監控頁、稽核頁、告警通道設定與任務規則的暫停標示。
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
- O2 另新增 `ops.monitorScanMinutes`、`ops.upkeepMinutes`、`ops.alertEmailCategories`、`ops.alertEmailMinSeverity`、`ops.alertEmailMaxPerHour`、`ops.loginFailureWindowMinutes`、`ops.schedulerLagIntervals`；O2 政策的完整定案（型別、說明、上下限）與健康檢查出廠值以 §17.6 為準。

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

> 2026-11-19｜核對基準 `origin/main` `772e7a54`（O1、pg0 已上線）｜狀態：草稿，主控撰寫，同日依設計審查的必修 10 條與建議清單修訂，再依第二輪審查的必修 6 條與建議修訂（修改處都見 §17.10.4）；待使用者確認 §17.10 的使用者行為類。
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
| PostgreSQL 的連線與鎖（`pg_stat_activity`、`pg_locks`）、慢查詢（`pg_stat_statements`）、`amcheck`；SQLite 的每週 `integrity_check`、EF 慢查詢攔截器（§8 表內其餘項目） | O4（mcpO1 的唯讀工具要讀這些統計時一起做；O2-U13） |
| 維運唯讀與處置的 MCP 工具、維運服務帳號（只預留稽核的行為者種類） | O4（mcpO1）、O6（mcpO2） |
| MCP 呼叫的稽核寫入點（O2 只建表與介面） | mcp1a |
| OpenTelemetry 與 OTLP 匯出（§10.2.3） | O3 |
| 業務 email（任務、簽核；U9）、使用者帳號的 Email 欄位（O2-U1） | 不排；要做時另開功能 |
| 稽核匯出、稽核頁看「讀取」紀錄 | 💡 |
| 備份本身（§15 第 2 條）；O2 只登記 `backup.freshness` 並回「未設定」（B20） | 路線階段 5 |
| 規則暫停期間略過的記錄補產任務（O2-U7b；選自動補產時改在 o2d 做，不改表） | 💡 |

**子單元切分**（實作者內部的 commit 切法；整段 O2 仍是一次交付、一次合併；主控判斷 O2-T1）：

| 子單元 | 內容 | 相依 | migration |
|---|---|---|---|
| o2a 表與稽核 | 九張表一次建好（§17.4）、`IDatabaseErrors` 加三個分類、稽核寫入介面與 `AuditBehavior`、全部稽核寫入點、稽核覆蓋的反射測試、稽核 API、`ops.upkeep` 工作的稽核清理 | 無 | **整段唯一一組**（SQLite 與 PostgreSQL 各一個，名稱 `OpsEventCenter`） |
| o2b 健康檢查與事件 | 檢查目錄與規則表、看門狗、`ops.monitor` 工作、事件記錄器與收件匣、500 與前端錯誤接上事件、站內通知、唯讀回 503、監控 API | o2a | 無 |
| o2c 告警通道 | SMTP 設定與加密、MailKit 寄送、測試信、寄送佇列、重試與節流、通道失敗事件、資料庫寫不進時直送 | o2b | 無 |
| o2d 自動暫停 | 失敗計數、暫停與恢復、`ruleEditor` 動態收件人、事件與檢查的已知錯誤碼、開發環境故障注入、規則 API 帶暫停狀態 | o2a、o2b | 無 |
| o2e 前端 | 監控頁、稽核頁、通道設定、任務規則分頁的暫停標示與恢復、鈴鐺新種類、政策編輯器的 `ruleEditor` 選項、四語、四種寬度 | API 形狀定了就能開工（o2b）；走查等 o2c、o2d | 無 |

- 只有 o2a 加 migration，所以後面的子單元**不能再改表**；九張表的欄位要在 o2a 一次定案（§17.4），計畫審查時逐欄確認。第二輪審查補的欄位也在 o2a 一起建：健康結果的去重鍵 `Dynamic_HealthCheckResult.TransitionId`（唯一）、檢查列的鎖欄 `Dynamic_HealthCheck.LastAggregatedUtc`、通道列的鎖欄 `Dynamic_AlertChannel.LastClaimUtc` 與信件語系 `MailLocale`（O2-U14 推薦）。
- o2a 開工前必答的只有會改表的 O2-U1、O2-U14（§17.10.5）；O2-U7b 的自動補產有不改表的做法，o2d 開工前答即可。
- o2c 與 o2d 都改 DI 註冊、`PolicyCatalog` 與四語檔，依序做，不平行。

### 17.2 後端

#### 17.2.1 稽核

**寫入介面**（Application，`Common/Audit/`）：

- `IAuditLog`（scoped）有三個寫法，依「寫不進去時要怎樣」分：
  - `Add(AuditEntry)`：只把列加進 DbContext 的追蹤，跟著呼叫端下一次 `SaveChanges` 寫進**同一個交易**；寫不進去整筆失敗。給 `AuditBehavior`、`IWriteStage`、自動暫停用。
  - `WriteNowAsync(AuditEntry)`：另開 DI scope 與交易立刻寫，**寫不進去丟 `AuditWriteException`**，呼叫端看得到失敗。給 MCP 的 fail closed 用（唯讀工具先寫稽核再回資料；寫入工具業務失敗時另寫一筆失敗稽核；MCP 設計 §8.1、T14）。
  - `TryWriteNowAsync(AuditEntry)`：同上，但失敗只記 Error log、回 `false`。給登入用（寫不進不擋登入，§5.6）。
  - 後兩個只能在交易外呼叫：`ITransactionManager.IsActive` 為真時丟 `InvalidOperationException`（PostgreSQL 上同一請求另開寫入交易會等自己外層的閘門與鎖，pg0 §15 第 10 條）。檢查放在方法的**第一步、建立新 DI scope 之前**，讀的是呼叫端 scope 的 `ITransactionManager`；新 scope 裡的交易管理器一定是「沒有交易」，在那裡才檢查永遠不會擋下。`TryWriteNowAsync` 的這個例外也照樣丟出、不吞成 `false`（這是呼叫端的程式錯誤，不是寫不進去）。
- `AuditEntry` 是值物件，呼叫端填的欄位（與 §17.4 `Dynamic_AuditLog` 一一對應）：
  - 必填：動作代碼、結果（`success`、`denied`、`invalid`、`failed`）。
  - 選填：目標（種類、id、顯示名快照）、原因代碼、摘要（結構化物件，由 `AuditSummary` 組成）、參數雜湊、回傳筆數、確認 id 與確認結果（`preview`、`confirmed`、`expired`、`mismatch`）。後四項 O2 不用，mcp1 填。
  - 行為者覆寫 `ActorOverride(userId, kind)`：一般呼叫只有環境裡**沒有身分**時才能給；環境已有身分還給覆寫就丟 `InvalidOperationException`，避免冒用。唯一的例外是登入的稽核身分情境（見下一條）。
  - **登入的稽核身分情境**：登入端點本身不清除既有身分。`SessionMiddleware` 對 `auth/login`、`auth/demo-login` 直接放行、不比對資料庫，`ctx.User` 仍是 cookie 驗證中介層從請求 cookie 解出的身分（可能是另一個帳號、或已停用、已重設密碼而失效的舊 cookie），`ClaimsCurrentUser` 照樣讀得到它；新 cookie 要等 handler 回來、`AuthController.SignInOnSuccess` 才寫。所以「登入時環境是匿名」不成立。做法：新增 AsyncLocal 的 `AuditOrigin.BeginSignIn()`，只有 `LoginHandler`、`DemoLoginHandler` 開（架構測試限定呼叫者只有這兩個）。範圍內 `IAuditLog` **不從 `ICurrentUser` 取行為者**，一律當成沒有身分：成功時必須給 `ActorOverride`（剛登入的帳號），失敗時行為者 null；範圍外的守衛不變。請求帶的舊 cookie 不進稽核的行為者欄，也不另記（舊 cookie 可能是失效的，記成行為者會誤導）。
  - 範圍內允許覆寫，不等於任何程式都能冒用：`BeginSignIn` 只放在兩個登入 handler 裡，覆寫的使用者 id 只能來自 handler 剛驗證通過的帳號；其他寫入點在範圍外，守衛照舊。
  - 行為者、行為者種類、來源管道、權杖 id、request id、來源 IP 其餘一律由 `IAuditLog` 從環境補，呼叫端不填。
- 環境來源：
  - 行為者：`ICurrentUser`（`ActingScope` 內是被代為執行的使用者）；排程工作與沒有身分的寫入記行為者種類「系統」、行為者 null（B3）。
  - 來源管道、權杖 id、行為者種類：新增 AsyncLocal 的 `AuditOrigin.Begin(channel, tokenId?, actorKind?)`，比照 `ActingScope`；`JobRunner` 執行工作時開 `job`，mcp1 在 `/mcp` 驗完權杖後開 `mcp`、帶權杖 id 與行為者種類（`user` 或 `opsService`），預設 `ui`、權杖 null、種類 `user`（O2-T24）。
  - request id：`Activity.Current` 的 trace id（＝`X-Request-Id`，§10.2.1）；背景工作為 null。
  - 來源 IP：Application 介面 `IRequestOrigin.ClientIp`，WebAPI 用既有的 `ClientIpResolver` 實作；背景工作為 null。
- **只能新增**：repository 只有新增、分頁查詢、依保留天數批次刪除三個方法；沒有修改或單筆刪除的路徑（§12）。不用資料庫 trigger 擋（兩種資料庫寫法不同），靠程式與審查。

**寫入點**（O2-T2）：

- 管理與設計類請求宣告 `IAuditedRequest`：`AuditAction Audit { get; }`（動作代碼與目標）。
- 新增 `AuditBehavior`，註冊在 `TransactionBehavior` 之內、`IdempotencyBehavior` 之外。請求是 `IAuditedRequest` 且結果成功時，組好 `AuditEntry` 呼叫 `IAuditLog.Add`，再 `SaveChanges` 一次（交易還沒提交，所以和業務寫入同交易）。稽核寫不進去就整筆失敗（§5.6）。
- 新舊值由 handler 補：handler 在 scoped 的 `AuditContext` 呼叫 `Describe(before, after)`；沒補的只記請求層的摘要（目標與參數代碼，不含值）。一定要補差異的：權限、群組成員、使用者（顯示名、群組、系統管理員旗標、啟用）、政策（舊值、新值）、健康檢查門檻、告警通道（密碼只記「已變更」）。設計類（模組、欄位、規則、事件、檢查、任務規則、流程、簽核流程、列印範本、轉單、分錄、應用程式套件）只記目標與變更種類，不存整份定義的差異（量大；記錄歷程與套件本身是另一份來源）。
- 不經 `AuditBehavior` 的寫入點：
  - **登入**（`LoginHandler`、`DemoLoginHandler`）：登入成功、失敗都用 `TryWriteNowAsync`，寫不進去不擋登入（§5.6）。整個 handler 包在 `AuditOrigin.BeginSignIn()` 裡（見上）。成功時以 `ActorOverride` 記剛登入的使用者（行為者與目標都是他），不論請求帶的是誰的 cookie、有效或失效；失敗時行為者 null（帶著別人的有效 cookie 輸錯密碼也一樣），帳號存在就記為目標，不存在只記原因 `unknownAccount`、不記輸入的帳號字串（沿用 `LoginHandler` 的規則）。被 `LoginThrottle` 擋下的嘗試不逐筆記，只累加指標 `formwright.login.throttled`（O2-T3）。
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
| `security` | 使用者的系統管理員旗標變更（`user.create`、`user.update`）；每次異動都通知（見下） | 安全 | `security`、`admin.changed` | 警告 |
| `channel` | 告警信最後一次重試仍失敗（§17.2.6） | 基礎設施 | `infra`、`channel.smtp` | 警告 |

- `ErrorFingerprint` 加 `ForConfig`、`ForInfra` 兩個純函式（§10.2.6 已定組成）；`security` 與 `channel` 沿用 `ForInfra` 的組法、前綴換成自己的種類字串。
- 任務規則丟例外時會有兩件事件：一件 `server` 來源的產品缺陷（例外本身，給廠商看），暫停時另一件 `config` 來源的客戶設定（給設計者處理）。這是有意的：例外是產品沒防住的錯，暫停是客戶要處理的狀態。
- **安全類每次異動都通知**（O2-U11）：同指紋 `admin.changed` 仍聚合成一件事件、累加次數，但不套用「確認後不再通知」：每次實際異動都發站內通知、寫寄送佇列（信件受 `ops.alertEmailMaxPerHour` 節流，超過併入摘要），不另設安全專用的節流（管理員異動本來就少，大量異動正是要看到的情況）。「誰給了誰」不存在事件列上：handler 在 `AuditContext` 標記 `adminChanged`，`AuditBehavior` 寫完稽核列、取得 Id 後在同一交易呼叫 `IncidentRecorder`，樣本只存那一筆稽核的 `AuditLogId` 與行為者（§17.4）；事件詳細列出最近 20 筆異動、每筆連到稽核看差異，通知的連結也指到那一筆稽核。
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
- 「確認」＝有人認領，停止 §5.2 的重複提醒；同指紋再出現只加次數，不再通知（安全類例外，每次異動都通知，見上）。沒有「取消確認」。
- 嚴重度只有健康檢查類會變：升級時（警告→嚴重）退回「開啟」並再通知一次；降級只改欄位、不通知。
- 事件**不能由使用者刪除**，已結案的過了 `ops.incidentRetentionDays` 由 `ops.upkeep` 清掉（連同樣本）。清掉之後同指紋再出現是一件新事件，不是回歸。
- 一個指紋只有一列（§17.4 唯一索引；O2-T4），重開就是改回同一列。

**轉換與通知**（`IncidentRecorder`，Application）：

- 每次訊號進來：同指紋沒有列→新增「開啟」；有列且未結→次數＋1、更新最近時間與版本、嚴重度依規則升降；有列且已結→重開並標回歸。全部用條件式更新（`WHERE Status = 讀到的狀態`），影響 0 列就重讀再判斷一次，最多三次（§17.7）。
- 會通知的轉換：新開（嚴重度為警告以上）、回歸、升級、嚴重事件的重複提醒（`ops.upkeep` 每輪檢查：狀態「開啟」、嚴重、上次通知早於 `ops.incidentRenotifyHours`）、安全類的每次發生。資訊級一律不通知。
- 通知與寄送佇列的列，和事件的轉換寫在**同一個交易**；交易失敗兩者都不會出現，不會有「信寄了、事件沒寫」。
- 處理者標示（監控頁與信件都用）：客戶設定→「需要設計者處理」；基礎設施→地端「需要 IT 處理」、雲端「廠商處理」；安全→「需要管理員確認」；產品缺陷→「產品問題：請附事件編號與錯誤代碼聯絡廠商」。O3 上線事件推送後，產品缺陷改成「已回報廠商」；O2 還沒有任何東西送出，不能寫「已回報」。

**樣本**（C2：原文只留在產品內）：

- `server` 與 `client` 來源每件事件保留最近 20 筆樣本（技術限制）：時間、request id、使用者、版本、路由樣板、原始訊息（500 字）、原始 stack（4000 字）、前端另帶瀏覽器家族與語系。新增一筆時刪掉第 21 筆以後的。
- 樣本另依 `ops.incidentSampleRetentionDays` 清除，事件本身照事件天數。
- `security` 來源的樣本不存訊息與 stack，只存 `AuditLogId` 與行為者（見上）；`health`、`config`、`channel` 沒有樣本。

**標題與外送內容**（C2；正規化不等於遮蔽）：

- `ErrorFingerprint.NormalizeMessage` 只把引號內字串、Email、GUID、十六進位、數字換成占位符，未加引號的姓名、業務文字、權杖仍會留下，所以**正規化訊息不外送**。
- 事件標題一律存**代碼＋白名單參數**（`TitleKey`＋`TitleParamsJson`），監控頁、鈴鐺、信件依語系組字。組標題的程式只有 Application 的 `IncidentTitles` 一處，各來源只接受下表的參數，參數值只能是代碼、數字或路由樣板（`{id}` 形式，不含實際值），不接受任意字串：

| 來源 | 標題代碼 | 允許的參數 |
|---|---|---|
| `server` | `ops.title.serverError` | 例外型別短名、路由樣板、moduleKey |
| `client` | `ops.title.clientError` | 錯誤名稱（只收 `TypeError` 這類 JavaScript 內建名稱，其他一律換成 `Error`）、路由樣板、moduleKey |
| `config` | `ops.title.config.<錯誤碼>`、`ops.title.rulePaused` | ruleKind、規則代碼、moduleKey、錯誤碼、門檻與次數 |
| `health` | `ops.title.health.<檢查代碼>` | 狀態、數值、門檻、異常實例數 |
| `security` | `ops.title.adminChanged` | 授予或拿掉、次數 |
| `channel` | `ops.title.channelFailed` | 失敗階段、SMTP 回應碼 |

- 正規化訊息另存 `NormalizedMessage`（只有 `server`、`client`），只在監控頁的事件詳細顯示（系統管理員、產品內）。存之前再經 `SensitiveTextMask` 遮蔽：`fwp_`、`fwc_` 開頭的值、`Bearer` 之後的字串、`password=`／`token=`／`secret=`／`key=` 這類鍵值對的值、32 字元以上的 base64 片段，一律換成 `***`。遮蔽只是多一層保險，不保證乾淨，所以仍不外送。原文只在樣本（系統管理員、每件 20 筆、`ops.incidentSampleRetentionDays`）。
- 站內通知、告警信，以及 O3 的事件推送，都只用標題代碼與參數，不用 `NormalizedMessage` 與樣本（O3 細部設計照這條寫，§17.10.3）。

#### 17.2.3 請求路徑來源的收件匣

伺服器 500 與前端錯誤是請求路徑上的高頻來源，不在請求裡同步寫資料庫（反目標「不拖垮業務請求」；O2-T5）：

- `IIncidentInbox`（Application 介面，singleton 實作）：`Post(IncidentSignal)` 只放進記憶體，同指紋合併成「次數＋最近一筆樣本＋最近版本」，不擋呼叫端。
- WebAPI 的 `IncidentInboxFlusher`（`BackgroundService`）每 5 秒把累積的訊號交給 `IncidentRecorder`，每個指紋一個交易型請求（經 `WriteGate`）。
- 寫不進去（資料庫忙碌、唯讀、斷線）就留在收件匣下一輪再試；收件匣最多 1000 個指紋，滿了丟最舊的並累加指標 `formwright.incident.dropped`（技術限制）。程式關閉時最後再送一次，送不出去就丟（log 裡仍有每一則 Error 或 Warning）。
- 多實例：每個實例各自的收件匣各自寫，計數用 `次數 = 次數 + n` 的條件式更新累加，彼此不會蓋掉。
- 提交結果不明（提交後連線才斷、程式收到例外）時，這批訊號留在收件匣重送，次數會多算一次。收件匣的次數本來就是概數（當掉時最多丟 5 秒，O2-T5），新開事件的通知只在「同指紋沒有列」時發、重送不會再發，所以不另做持久化去重；健康檢查的補寫會改變事件狀態與通知，另有去重鍵（§17.2.4）。
- 前端錯誤收件的 429 次數另計指標 `formwright.client_errors.throttled`（§12 審查 U3）。

規則失敗、健康檢查、安全、通道失敗的頻率低，直接同步呼叫 `IncidentRecorder`。

#### 17.2.4 健康檢查

**檢查目錄**（Application 靜態 `HealthCheckCatalog`，出廠值與技術上下限只寫在這裡；T12）：

| 代碼 | 查什麼 | 數值與單位 | 出廠門檻（警告／嚴重） | 出廠間隔 | 執行者 | 類別 | 資料庫 |
|---|---|---|---|---|---|---|---|
| `database.writable` | 寫入探測：在交易型請求裡對 `Dynamic_HealthCheck` 自己那一列做條件式更新（`LastProbeUtc`） | 成敗；成功＝正常，唯讀、空間滿、連不上、其他失敗＝嚴重，訊息代碼分四種；忙碌（`IsBusyOrConflict`，例如排隊逾時）不算失敗、沿用上一次狀態，連續忙碌次數達警告門檻才記警告 `busy` | 連續忙碌 3 次／— | 1 分 | 看門狗 | 基礎設施 | 兩種 |
| `storage.diskFree` | 資料目錄所在磁碟的剩餘比例，取最小的那一個：SQLite 的資料庫檔目錄、附件根目錄、金鑰目錄 | 剩餘萬分比（2000＝20%），訊息另帶剩餘位元組 | 2000／1000 | 5 分 | 看門狗 | 基礎設施 | 兩種（PostgreSQL 只看本機的附件與金鑰目錄） |
| `scheduler.jobs` | 讀 `Dynamic_ScheduledJob`：最大連續失敗次數；另外任一工作的下次預定時間落後超過 `ops.schedulerLagIntervals` 個間隔（出廠 3）直接判嚴重（排程器卡住或搶不到租約） | 次數 | 3／10 | 5 分 | 看門狗 | 產品缺陷 | 兩種 |
| `database.walSize` | SQLite 的 `-wal` 檔大小（讀檔案系統，不下 PRAGMA） | 位元組 | 256 MB／1 GB | 15 分 | 排程 | 基礎設施 | 只 SQLite（PostgreSQL 回「不適用」，畫面不列） |
| `database.integrity` | SQLite：`PRAGMA quick_check`、`PRAGMA foreign_key_check`；兩種資料庫：共用主檔的 `MasterLifecycle.CheckAsync` | 問題筆數 | —／1 | 1440 分 | 排程 | 基礎設施 | 兩種（PostgreSQL 只做產品層） |
| `backup.freshness` | 最近一次備份距今時數 | 時數 | 26／50 | 60 分 | 排程 | 基礎設施 | 兩種；備份還沒做時回「未設定」（B20） |
| `security.loginFailures` | 稽核表最近 `ops.loginFailureWindowMinutes` 分鐘（出廠 15）的 `login.failed` 列數 | 次數 | 20／100 | 5 分 | 排程 | 安全 | 兩種 |

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

**多實例的彙總**（一個檢查仍只有一件未結事件，§5.5）：

- 每個檢查在目錄裡登記**彙總範圍**：看門狗型（`database.writable`、`storage.diskFree`、`scheduler.jobs`）是 `instance`，各實例的連線、磁碟可能不同；排程型是 `global`，同一時間只有一份在跑。
- `instance`：檢查的整體狀態＝**仍在回報的實例**裡最糟的那個。「仍在回報」＝最近三個檢查間隔內有結果列；超過的視為已下線，退出彙總（技術限制，§17.9：只決定下線實例何時退出，在線實例的告警不受影響，檢查間隔本身可調）。所以單一實例回 `ok` 不會結掉別台的異常；全部在線實例都正常才結案。
- `global`：整體狀態＝最新一筆結果。
- 上面的「最新」「在線」都是**截至某個檢查時間**算的（第 2 步的 R），不是寫入當下；補寫歷史結果時用那筆的時間，所以晚到的舊結果算出的是當時的狀態，不會混進之後的結果（見下面第 2 步）。
- 事件標題參數帶異常實例數；事件詳細與監控頁列出各實例最新的狀態、數值與時間（從 `Dynamic_HealthCheckResult` 讀，`(Code, CheckedUtc)` 索引）。

**結果怎麼變成事件**：

1. 檢查跑完，產生這筆結果的穩定識別碼 `TransitionId`（GUID 的 32 字十六進位；之後重試、補寫、直送都用同一個），先更新記憶體的 `HealthStatusBoard`（singleton，本實例每個檢查的最新狀態、數值、訊息代碼、時間）。
2. 再開一個交易（彙總交易），依序：
   - **鎖**：第一句是對 `Dynamic_HealthCheck` 該列的條件式更新 `LastAggregatedUtc = 現在`（只當鎖用，不是狀態），取得該列的列鎖，讓同一檢查的多個實例在 PostgreSQL 上依序彙總、讀得到彼此已提交的結果。
   - **去重**（持久化的處理標記）：查 `Dynamic_HealthCheckResult` 有沒有這個 `TransitionId`（唯一索引，§17.4）。有就表示這筆已經完整處理過：結果列、事件轉換、站內通知、寄送佇列都是同一交易寫的，有結果列就代表其他也都在；這次什麼都不寫、直接提交，並從佇列移除。新增結果列時萬一撞唯一鍵（`IsUniqueViolation`，另一台或上一輪剛寫入）同樣當成已處理。所以「提交成功但回應斷線」後重播，不會多一筆結果、多算次數或多發通知。
   - **歷史結果**：結果列一律新增，不論比現有結果新或舊。
   - **彙總**：以這筆的檢查時間 R 為基準，只看 `CheckedUtc <= R` 的結果：`instance` 範圍取各實例截至 R 的最新一筆，R 往前三個檢查間隔內有結果的實例才算在線；`global` 範圍取截至 R 的最新一筆。算出「R 時的整體狀態」。
   - **目前投影**（`Dynamic_HealthCheck` 的 `LastCheckedUtc`、`LastStatus`、`LastValue`、`LastMessageCode`，總覽與狀態卡讀這些）：只有 R 不早於投影現有的 `LastCheckedUtc`（或還沒有值）時，才以 R 時的整體狀態覆寫、`LastCheckedUtc = R`；R 較早就是歷史，投影不動，所以晚到的舊結果不會讓總覽退回過去的狀態。`NextDueUtc` 只由排程型檢查的正常執行推進（取較晚者），補寫不動它。
   - **事件**：同一交易呼叫 `IncidentRecorder`，以 R 時的整體狀態開、更新或結案（`warning`／`critical`／`error` 開或更新，`ok` 結案），轉換時間用 R。事件已有比 R 更晚的轉換（`LastSeenUtc` 或 `ResolvedUtc` 較晚，例如別台實例已寫入較新的結果）時，只寫結果列、不改事件，避免把歷史當成新的回歸。指紋＝`infra`＋檢查代碼。
3. 第 2 步寫不進去、或提交結果不明（提交時丟例外）時，把這次結果放進記憶體的**待補寫佇列** `PendingIncidentWrites`，狀態板照常更新：
   - 每一筆帶第 1 步的 `TransitionId`、檢查代碼、狀態、數值、訊息代碼、檢查時間、實例 id。
   - 同一檢查**狀態沒變**的連續結果只更新最後一筆的數值與時間，**狀態有變**才新增一筆；所以「警告→嚴重→正常」會留三筆，不會壓成一筆正常。已經嘗試寫過一次的那一筆不再合併（它可能其實已提交，重播會被去重略過，合併進去的新數值會跟著被略過），之後同狀態的結果另開一筆。
   - 上限 500 筆（技術限制），滿了丟最舊的一筆、累加指標 `formwright.incident.pendingDropped`，監控頁的頁首提示帶遺失筆數。
   - 補寫：資料庫恢復可寫後，依檢查時間先後**逐筆**走第 2 步（每筆一個交易；去重、彙總基準、投影與事件的規則都照第 2 步），提交後才從佇列移除；中途失敗或提交結果不明就停在那一筆，下一輪從那一筆接著補（已提交的會被去重略過）。所以結果列、事件的開啟、升級、結案依原本順序補齊，總覽只在補到比現有投影新的那幾筆時才跟著變。
   - 告警信走直送（§17.2.6）；補寫產生寄送佇列列時，以 `TransitionId` 對照直送紀錄，避免重寄。
4. 手動「立即檢查」：管理員在監控頁按，API 同步跑該檢查（最長 60 秒）並回結果；同一檢查 60 秒內只能按一次（技術限制）。

#### 17.2.5 站內通知

- 新增兩個 `NotificationKinds`：`opsIncident`、`opsRulePaused`。通知只帶標題快照與連結（照既有 `Notification` 的規則，不帶欄位值）。
- 標題（O2-T22，與事件同一個契約，§17.2.2）：`DataJson` 存 `{ titleKey, params, incidentId }`，鈴鐺依使用者語系組字，前面加種類的四語標籤（比照簽核種類）；`TitleSnapshot` 存寫入當下以繁中組好的文字（截斷 200 字），只當前端沒有這個種類的組字程式時的退路。不放 `NormalizedMessage`。
- 連結：`opsIncident` 指向監控頁的事件詳細，安全類指向那一筆稽核的詳細；`opsRulePaused` 指向模組設計頁的任務規則分頁（設計者多半不是系統管理員，看不到監控頁；O2-U3）。
- 收件人：
  - 客戶設定類：`ops.configIssueRecipients`，出廠 `ruleEditor,admins`（該規則的最後修改者＋全部系統管理員；B14）。
  - 其他類：`ops.alertRecipients`，出廠 `admins`。
  - 解析後為空（設定的人全被停用）就改送全部啟用中的系統管理員，記 Warning（寫死的安全網，比照簽核異常通知）。
- `ruleEditor` 是收件人政策的動態項目（O2-T15）：`PolicyDefinition` 加 `DynamicRecipients`（允許的動態項目清單），只有 `ops.configIssueRecipients` 允許 `ruleEditor`；`RecipientResolver` 多收一個情境參數，`ruleEditor` 解析成規則的 `ModifiedUserId`，沒有就 `CreatedUserId`，停用的略過。政策頁的收件人編輯器多一個勾選「該規則的最後修改者」。
- 資料庫寫不進時通知寫不進去，等 `PendingIncidentWrites` 補寫事件時照順序一起補發（§17.2.4、§17.2.9）；即使同一檢查在佇列後面已經結案，前面的開啟、升級仍補發，參數帶當時的檢查時間，讓收件人知道發生過什麼。

#### 17.2.6 SMTP 告警通道

**設定存哪**：

- 一張 `Dynamic_AlertChannel`，一個通道一列，O2 只有 `smtp`（O3 可加其他通道，不改表）。欄位見 §17.4。
- 密碼用 Data Protection 加密（purpose `ops-alert-smtp`，與登入 cookie 同一套金鑰；O2-T10）。金鑰不見時（PostgreSQL 沒設 `KeysDirectory` 而重啟，pg0 §5.8）解不開：GET 回 `passwordUnreadable: true`，畫面要求重填；寄送時當作設定不完整，開通道失敗事件。
- 收件人是通道設定裡的**一組 Email 地址**（1–20 個），不是使用者帳號：`AppUser` 沒有 Email 欄位（O2-U1）。站內通知的收件人照收件人政策，兩者分開設。
- 開 Demo 登入時通道設定唯讀、測試信停用（回 403，比照 `UpdateSiteThemeHandler` 的 Demo 檢查）：公開 Demo 人人是管理員，可以把主機設成任意位址，讓伺服器替他連內網或寄信（§17.9 安全、O2-U10）。

**正式環境的安全邊界**（寫死，§17.9）：

- **信任邊界是系統管理員**：只有系統管理員能設主機，系統管理員本來就是全系統最高權限，所以產品**不限制目的位址**，私有位址與內網主機名照樣可用（地端客戶多半用內網 SMTP relay）。雲端版要限制對外連線時由部署層的對外防火牆處理，不在產品內做。
- **DNS**：每次連線時由 MailKit 解析主機名，不快取 IP、不另做位址檢查（上一條已不限位址，做了也沒有意義）。
- **降低被當成探測工具的價值**：測試信與寄送失敗只回階段（`connect`、`tls`、`auth`、`send`）與 SMTP 回應碼，不回伺服器的歡迎訊息或例外全文；連線逾時 30 秒；測試信同一管理員 30 秒一次；每次存設定與寄測試信都寫稽核。
- **TLS**：`startTls` 用 MailKit 的強制模式（伺服器不支援 STARTTLS 就失敗，不退回明文），`sslOnConnect` 用直接 TLS；憑證一律驗證，不提供「略過憑證錯誤」。內網自簽憑證要把簽發的 CA 裝進主機的信任區（安裝文件寫明）。
- **不准明文傳密碼**：`security` 是 `none` 時不能填帳號密碼（PUT 回 400 `smtpPlaintextAuth`）；`none` 只給以來源位址授權、不需登入的內網 relay。

**寄哪些**：

- 類別由 `ops.alertEmailCategories` 決定（出廠：基礎設施、安全、產品缺陷；客戶設定類只站內，給設計者；O2-U2），最低嚴重度 `ops.alertEmailMinSeverity`（出廠「警告」）。
- 觸發的轉換和站內通知相同（新開、回歸、升級、重複提醒、安全類的每次發生）。
- `channel` 來源的事件**永遠不寄信**（通道壞了還寄信會自己觸發自己；寫死，§17.9）。

**寄送**（O2-T9、O2-T11）：

- 事件轉換時在同一個交易寫一列 `Dynamic_AlertOutbox`（`pending`），主旨與內文在這時以通道設定的信件語系 `MailLocale` 組好存成文字快照（O2-U14），之後不再依事件現況、也不依之後改過的語系改寫。
- `ops.monitor` 每輪先送佇列，每封信分三步，**寄 SMTP 的那一步不在任何交易裡**（不准持全庫寫入鎖等網路）：
  1. **領取**（短交易、帶 `JobLeaseFence`），一個交易依序：
     - 第一句 `UPDATE Dynamic_AlertChannel SET LastClaimUtc = 現在 WHERE Kind = 'smtp'`，取得通道列的列鎖：同一通道的領取、額度計算、摘要化（見下）都在這把鎖後面依序執行，PostgreSQL 無鎖模式下也一樣。
     - 額度：同一交易數最近 60 分鐘內 `SentUtc` 的 `sent`，加上目前全部 `sending`（含領取已過期、還沒被重領的）；摘要信不算。
     - 未達 `ops.alertEmailMaxPerHour`，或這列是摘要信、或是要重領的過期 `sending` 列（已算在額度裡）：`UPDATE ... SET Status = 'sending', ClaimedBy = 實例 id＋本輪執行 id, ClaimedUntilUtc = 現在＋2 分鐘, Attempts = Attempts + 1 WHERE Id = ? AND ((Status = 'pending' AND NextAttemptUtc <= 現在) OR (Status = 'sending' AND ClaimedUntilUtc < 現在))`；影響 0 列就是別人領走了，跳過。依建立順序一次領一封。
     - 已達上限：不領取，同一交易做摘要化（見「節流」）。
     - 額度數到的和領取寫下的在同一把列鎖內，兩個寄送者不會都看到「還差一封」而一起領走，超額寄送不會發生。
  2. **寄送**（交易外）：MailKit 寄出，逾時 30 秒。
  3. **記結果**（短交易）：`UPDATE ... WHERE Id = ? AND ClaimedBy = 我`，成功記 `sent`、`SentUtc`；失敗依下一條記 `pending` 與 `NextAttemptUtc`，或 `failed`；同時清 `ClaimedBy`、`ClaimedUntilUtc`。影響 0 列表示領取已過期、被別人接手，只記 Warning（這封可能重複寄出）。
- 程式在第 2、3 步之間當掉，列停在 `sending`，2 分鐘後領取過期，下一輪重領重寄。所以**還在重試中的佇列列**是至少一次交付：SMTP 已收下但結果沒記到時會重複寄一封；列還在佇列、還沒用完重試前不會漏寄（寫死，§17.9）。「至少一次」只限這個範圍，下列情況**不寄或不保證寄到**：
  - 重試耗盡：第 5 次仍失敗標 `failed`，不再寄；改由 `channel.smtp` 事件在站內告知（見下一條）。
  - 略過：通道停用或沒設定標 `skipped`；補寫時已過時的開啟、升級信標 `skipped`（見下面「資料庫寫不進時的直送」最後一條）。
  - 併入摘要：`digested` 的列不單獨寄，只在摘要信列一行或算進「另有 M 則」；摘要信本身照一般信的規則重試。
  - 記憶體遺失：直送佇列與待補寫佇列只在記憶體，程式重啟或佇列滿（直送 20 封、待補寫 500 筆）就丟掉；待補寫的轉換被丟掉時，對應的佇列列也不會產生（監控頁頁首顯示 `pendingDropped`）。
  - 收件端：SMTP 伺服器收下之後的轉寄、退信、垃圾信匣不在產品內。
- 重試：失敗後 1、5、15、60 分鐘再試，最多 5 次（技術限制）；第 5 次仍失敗標「失敗」，開或更新 `channel.smtp` 事件（站內通知管理員）。之後任何一封寄成功就把該事件結案（`recovered`）。
- 節流：領取時數到的額度（見上）達 `ops.alertEmailMaxPerHour`（出廠 20）就不再逐封寄，改做**摘要化**，和額度計算在同一個交易、同一把通道列鎖之後：`UPDATE Dynamic_AlertOutbox SET Status = 'digested' WHERE Channel = 'smtp' AND Status = 'pending' AND ClaimedBy IS NULL AND NextAttemptUtc <= 現在 AND Transition <> 'digest'`。只動**未領取的 `pending`**：別的寄送者已領走（`sending`）的列不會被摘要化，正在寄的信不會同時出現在摘要裡；重試中而還沒到期的列下一輪再判斷。
- 摘要信：
  - 每個整點後的第一輪（同樣先取通道列鎖），若這個整點之後還沒有摘要信、而有 `digested` 且 `DigestOutboxId` 為空的列，就新增一列 `Transition = digest` 的佇列列，並以 `UPDATE ... SET DigestOutboxId = 新列 WHERE Status = 'digested' AND DigestOutboxId IS NULL` 把那些列掛上去，內容依掛上的列組成，三件事同一交易。有列鎖，兩台不會各開一封；一個小時一封，摘要信本身不受每小時上限限制。
  - 內容：「過去一小時另有 N 則告警」，依嚴重度再依時間排列，每則一行：事件編號、類別與標題（代碼＋參數組成）。
  - **長度預算**（`Body` 上限 4000 字）：開頭一段最多 200 字、結尾「另有 M 則，請看監控頁」預留 200 字；每則一行最多 70 字，標題超過就截斷加「…」。依序加入，最多 50 則，下一行會讓內文超過 4000 − 200 字就停；M＝掛上的總數－已列出的則數，M 為 0 時不寫結尾。50 × 70 加頭尾約 3900 字，正常情況列滿 50 則；預算規則是保險（例如語系換成字比較長的翻譯）。主旨固定格式「[Formwright] 過去一小時另有 N 則告警」，不會超過 200 字。
  - 一般信同理：主旨超過 200 字時截斷標題並加「…」；內文的標題最多 500 字，其餘欄位都是短欄位，總長不會超過 4000 字（組字後仍超過就截斷並在結尾加「…（內容過長，請看監控頁）」）。
  - 重試、領取、失敗與一般信相同。
  - 清理：`digested` 的列要等它的摘要信已是 `sent`、`failed` 或 `skipped` 才依保留天數清；摘要信還沒寄完時來源列不清（§17.2.8）。
- 信件內容：主旨「[Formwright][嚴重] <標題>（#事件編號）」；內文：類別、處理者、次數、首次與最近時間、版本、事件連結。標題只用標題代碼與白名單參數（§17.2.2），不放 `NormalizedMessage`、原始訊息、stack、記錄值（C2 的精神：信也是送出產品之外）。連結的網址用部署設定 `Ops:PublicUrl`，沒設就不放連結。語系用通道設定的 `MailLocale`（`zh-TW`、`en-US`、`th`、`vi` 之一）；沒設＝出廠，O2 的出廠是繁中，p3 有系統預設語系後出廠改成跟系統預設語系（O2-U14）。改語系只影響之後寫入佇列的信。
- 通道停用（`Enabled` 為否）或沒設定：佇列照寫、標「略過」，監控頁的通道區塊顯示「未設定，告警只在站內」。

**測試信**：

- `POST .../channels/smtp/test`：不經佇列、立刻用**已儲存**的設定寄一封到清單上的全部收件人（不能臨時填地址，避免被當成寄信工具；O2-U1），30 秒逾時；內容用通道的信件語系，讓管理員確認收件人看得懂。
- 成功回寄到哪些地址；失敗回 422 與失敗階段（連線、TLS、認證、寄送）和伺服器回應碼，不回例外全文。
- 同一管理員 30 秒內只能寄一次（技術限制）；寫稽核 `ops.channel.test`。

**資料庫寫不進時的直送**（O2-T8）：

- 佇列寫不進去時（看門狗偵測到唯讀、空間滿、連不上），改走記憶體直送佇列：上限 20 封（技術限制）、同一個 `TransitionId` 只寄一次、照樣重試（1、5、15、60 分鐘、最多 5 次）與每小時上限（以本實例記憶體計數，超過的不寄、只記 Warning）。
- **執行者是看門狗**，不是 `ops.monitor`：資料庫不可寫時排程器搶不到租約（租約本身是 `UPDATE`），`ops.monitor` 根本不會跑。看門狗除了檢查迴圈，另有一條寄送迴圈（同一個 hosted service 的第二個背景迴圈），每 60 秒送直送佇列裡到期的信，交易外、單封逾時 30 秒，寄送卡住不會拖住檢查。多實例時每個實例各自直送自己偵測到的轉換，同一故障可能每台各寄一封（已知限制，§17.9）。
- 通道設定讀不到時用記憶體裡最後一次成功讀到的設定（密碼仍是加密字串，寄送時才解；信件語系也用那份設定的）；啟動後從沒讀到過就寄不出去，只剩 log 與 `/api/health/ready`。
- 資料庫恢復可寫後，`PendingIncidentWrites` 補寫時會再產生佇列列：直送已寄出的 `TransitionId` 直接記成 `sentDirect`（留紀錄、不再寄）；還沒寄出的照常記 `pending`，直送不再寄；SMTP 正在寄的記成由直送領著的 `sending`，寄完由直送記 `sentDirect`（失敗交回 `pending`）；但同一檢查在補寫佇列後面已經結案的，前面的開啟、升級信已經過時，記 `skipped`（站內通知照補，§17.2.5）。直送紀錄只在本實例記憶體，所以這個比對只對本實例直送的轉換有效（已知限制）。

#### 17.2.7 規則失控自動暫停

依 §5.4 與 C3 使用者裁決：只暫停任務規則（含手動套用的 manual 規則）與日後的非同交易規則；同交易的事件與檢查只開客戶設定事件、不暫停；門檻 60 分鐘內連續 5 次；迴圈偵測延到 mcp2a。

**狀態存哪**（O2-T13）：

- 新表 `Dynamic_RuleHealth`（RuleKind＋RuleId 為主鍵），只在規則失敗過才有列；不改 `Dynamic_TaskRule`，所以客戶手動停用（`Active`）與系統暫停（`PausedUtc`）分開記，恢復只清後者（§12「自動暫停只記由系統暫停的規則」）。
- 另一張 `Dynamic_RuleFailure` 存這一串失敗各自的時間（滑動窗口要用，見下）；只留窗口內、最近一次成功之後的列，每條規則最多約門檻筆數。
- 留 RuleKind 是給日後非同交易規則；O2 只有 `taskRule`。

**計數**：

- `TaskRuleStage`（交易內）每次處理一個模組，先一次查出這些規則在 `Dynamic_RuleHealth` 的列（通常是空的）：
  - 已暫停的規則略過不跑，並把 `SkippedWhilePaused` 加一（條件式更新，跟業務寫入同交易）。
  - 這次跑成功、而且需要留下 reset 的規則（條件見下面「順序與 reset 協定」），記 `StreakResetUtc = 評估時間`（同交易；業務交易後來回滾時一起回滾，下次成功再記）。`Dynamic_RuleFailure` 的舊列不在這裡刪，由下一次失敗時清（見下），成功的路徑最多多一句寫入。
- `ManualTaskRules`（手動套用）照同樣規則：已暫停的不出現在「套用任務範本」清單；套用失敗也要加上 `RuleFailureTag.Mark`（現在沒有）。
- 失敗計數在新的 `RuleFailureBehavior`：註冊在 `ValidationBehavior` 之後、`ApprovalAlertBehavior` 之前（兩者都在交易之外）。
  - 只處理最外層請求（`ITransactionManager.IsActive` 為真表示巢狀，跳過，比照 `ApprovalAlertBehavior`）。
  - 例外帶有 `taskRule` 標記、**而且歸屬於規則本身**時：另開 DI scope，送交易型請求 `RecordRuleFailureRequest(kind, ruleId, errorCode, fingerprint, requestId)`，然後**原樣丟出原例外**；這筆寫入失敗只記 Error log，不改變回給使用者的錯誤（§5.4）。
  - **不歸屬規則的例外不計數**：`RuleFailureTag.Mark` 會標記穿過規則迴圈的**任何**例外，包含取消與資料庫故障，這些不是規則失控，計進去會讓一次資料庫唯讀把所有任務規則都暫停。`RuleFailureClassifier.IsAttributable(e)`（Application，一處）排除：`OperationCanceledException`（含 `TaskCanceledException`）、`DbUpdateConcurrencyException`、`FrozenFieldException`、`IDatabaseErrors` 判為忙碌或衝突、查詢中斷、唯讀、空間滿、連不上、唯一鍵衝突的例外。原則是「只計規則本身的錯」：被 WebAPI 例外處理器鏈（`ConcurrencyExceptionHandler`、`FrozenFieldExceptionHandler`、`DatabaseBusyExceptionHandler`、`DatabaseUnwritableExceptionHandler`）轉成 409、503 的一律不計；少數仍回 500 的基礎設施或並行例外（非用戶端中斷的取消、查詢中斷、唯一鍵衝突）也不計。判準以 §17.8 的 **E13 例外矩陣**為準：每列寫明例外、回應碼與歸屬，測試把每列的真實例外丟進 WebAPI 測試主機的處理器鏈，同時斷言回應碼與 `IsAttributable` 的結果。只比對兩邊的型別清單不夠：處理器內部還有條件（例如 `UnhandledExceptionHandler` 只在 `RequestAborted` 時才把取消當成用戶端中斷、其他取消照樣 500；`DatabaseBusyExceptionHandler` 經 `DatabaseBusy.Is` 委派給 `IsBusyOrConflict`，日後任一邊改寫就可能分歧），矩陣逐列驗才抓得到兩邊判斷不一致。被排除的例外照原路徑回應、記 log，不碰 `Dynamic_RuleHealth`。
  - 結果是失敗、而 `RuleFailureNote` 有已知錯誤碼時：開或更新客戶設定事件（§17.2.2），不計數、不暫停。
- 時間窗的語意（O2-U12，照 C3 字面）：**滑動窗口**。每次失敗時往回看 `ops.rulePauseWindowMinutes`，這段時間內、最近一次成功之後的失敗達 `ops.rulePauseConsecutiveFailures` 次就暫停。例：門檻 5 次、窗口 60 分鐘，在第 0、59、61、62、63、64 分鐘各失敗一次、中間沒有成功，第 64 分鐘那次往回 60 分鐘有 59、61、62、63、64 五次，暫停（初稿的「從第一次失敗起算、過窗口就重來」在這裡不會暫停，等於限縮了 C3，已改掉）。
- **順序與 reset 協定**（失敗在業務交易回滾之後才另開交易補記，中間可能夾著同一規則的成功，所以不能拿補記的時間排順序）：
  - **順序依據＝規則的評估時間**：`TaskRuleStage` 每評估一條規則前取一次 `TimeProvider.GetUtcNow()`（UTC，不是 `IBusinessTime` 的牆上時間）。失敗時 `RuleFailureTag.Mark` 多寫一個 `formwright.ruleEvaluatedUtc` 到例外的 `Data`，`RecordRuleFailureRequest` 用它當 `FailedUtc`，**不用補記當下的時間**；成功時用它當 `StreakResetUtc`。兩者比的是同一種時間，同一時間點的失敗算在 reset 之前（`FailedUtc <= StreakResetUtc` 不算）。
  - **在途失敗**：本實例的 singleton `RuleFailureInFlight` 記「已失敗、還沒補記」的規則與評估時間。`TaskRuleStage` 的 catch（業務交易回滾之前）登記，`RuleFailureBehavior` 補記完成或判定不歸屬時移除（`finally`）；沒被移除的 5 分鐘後過期（技術限制，§17.9）。只在記憶體，不寫資料庫。
  - **成功要不要寫 reset**：(a) 有健康列且 `ConsecutiveFailures > 0`；或 (b) `RuleFailureInFlight` 有這條規則、評估時間早於這次成功的失敗。符合 (a) 或 (b) 才寫，其他成功不碰資料庫，成功的路徑仍然通常是零寫入。
  - **reset 的寫法**：有列時 `UPDATE ... SET StreakResetUtc = 評估時間, ConsecutiveFailures = CASE WHEN LastFailureUtc IS NULL OR LastFailureUtc <= 評估時間 THEN 0 ELSE ConsecutiveFailures END WHERE 主鍵 AND (StreakResetUtc IS NULL OR StreakResetUtc < 評估時間)`，只往後推、不倒退；**還沒有列（首次建列）**而符合 (b) 時新增一列（計數 0、`StreakResetUtc = 評估時間`），撞主鍵就改走上面的 `UPDATE`。所以規則第一次失敗的補記還沒到、中間先來一次成功，那次成功也會留下 reset，晚到的失敗不會算進新的一串。
  - 跨實例：失敗在 A 台、成功在 B 台，B 看不到 A 的在途失敗；只有成功剛好落在 A 的「回滾到補記提交」之間（通常 1 秒內，最長是寫入閘門的排隊上限）而且規則原本沒有連續失敗時，會多算一次失敗。已知限制（§17.9），最多讓暫停提早一次失敗發生。
- `RecordRuleFailureRequest` 的寫法（一個交易，兩種資料庫同一份，PostgreSQL 上並行也正確；下面的 F 是這次失敗的評估時間）：
  1. `UPDATE Dynamic_RuleHealth SET ConsecutiveFailures = ConsecutiveFailures + (CASE WHEN StreakResetUtc IS NULL OR F > StreakResetUtc THEN 1 ELSE 0 END), LastFailureUtc = 較晚者(LastFailureUtc, F), LastErrorCode = ?, LastRequestId = ? WHERE 主鍵`；影響 0 列就新增一列（計數 1、`LastFailureUtc = F`），撞主鍵（別人剛新增）就重做第 1 步。這一句放在最前面，是為了先取得該規則那一列的列鎖：同一規則的並行失敗與成功的 reset 在 PostgreSQL 上會在這裡排隊，後到的交易讀得到先到者已提交的失敗列與 `StreakResetUtc`。`LastErrorCode`、`LastRequestId` 只在 F 是目前最晚的失敗時才換。
  2. F 晚於 `StreakResetUtc`（或它是 null）才新增一列 `Dynamic_RuleFailure(RuleKind, RuleId, FailedUtc = F, RequestId)`；F 不晚於它，表示這次失敗發生在一次已記下的成功之前，只更新第 1 步的欄位、不進窗口計數，到此結束。
  3. 清掉這條規則已經不會再用到的失敗列：`FailedUtc < F − 窗口`，或 `FailedUtc <= StreakResetUtc`（最近一次成功之前）。
  4. 計數：這條規則 `F − 窗口 <= FailedUtc <= F` 且晚於 `StreakResetUtc` 的列數。
  5. 列數達門檻：`UPDATE ... SET PausedUtc = 現在, LastPausedUtc = 現在, PauseCount = PauseCount + 1, SkippedWhilePaused = 0 WHERE 主鍵 AND PausedUtc IS NULL`（暫停時間用補記當下，那是暫停真正生效的時間）；影響 1 列的那個交易就是「這次觸發暫停」的人，在同一交易寫暫停事件、站內通知、寄送佇列、稽核 `taskRule.autoPause`（行為者種類「系統」）。
- `ConsecutiveFailures` 是「最近一次成功之後的失敗總數」（不看窗口），只給畫面顯示；暫停與否只看第 4 步的窗口內列數。

**暫停與恢復的行為**：

- 暫停後：存檔照常成功，只是這條規則不產生、不同步任務；手動套用清單看不到它。已經產生的任務不動。
- 恢復：模組設計頁的任務規則分頁，暫停的規則顯示「已自動暫停」標籤（暫停時間、連續失敗次數、最近錯誤的代碼〔例外型別短名或錯誤碼〕、最近一次的錯誤代碼 request id 前 8 碼、暫停期間略過幾次存檔）與「恢復」按鈕。不顯示錯誤訊息文字：正規化訊息仍可能含業務文字（§17.2.2），設計者不一定是系統管理員。恢復後規則列保留一行「上次暫停：起訖時間、略過 N 次存檔」，直到下次暫停，給設計者篩出要補建的記錄。
  - 權限＝能編輯這條規則的人（設計器編輯權＋共用主檔設計權，與 `UpdateTaskRuleRequest` 相同；O2-U6）。
  - 恢復＝清 `PausedUtc`、記 `ResumedUtc`、計數歸零（`SkippedWhilePaused` 與 `LastPausedUtc` 留到下次暫停，數字也寫進稽核摘要），暫停事件結案（`ruleResumed`），寫稽核 `taskRule.resume`。
  - 恢復也刪掉這條規則的 `Dynamic_RuleFailure` 列，並記 `StreakResetUtc = 現在`，暫停前的失敗不會留到恢復後再算。
  - 恢復後規則立即對下一次存檔生效。暫停期間存過的記錄**不自動補產任務**（O2-U7b），各觸發方式恢復後的效果不同（觸發判斷見 `TaskRuleEvaluator.Triggers`）：

| 觸發方式 | 暫停期間發生的事 | 恢復後 | 要補怎麼補 |
|---|---|---|---|
| `onCreate`（新增時） | 新增的記錄沒產生任務 | 那些記錄**永遠不會再觸發**（只在新增那一次觸發）；之後新增的照常 | 在模組清單用建立時間篩出暫停起訖之間的記錄，逐筆在記錄的任務區「新增任務」手動補建（`CreateRecordTaskRequest`）；手動補建的任務不掛規則 |
| `onUpdateMatch`（存檔後由不符合變符合） | 在暫停期間變成符合的記錄沒產生任務 | 這些記錄已經是「符合」，之後再存檔不算「由不符合變符合」，**不會觸發**；要等條件先變回不符合、再變符合 | 同上，用修改時間篩；或由使用者把條件改回不符合再改回來（會產生規則任務） |
| `onFieldBecomes`（欄位變成指定值） | 欄位在暫停期間變成指定值的記錄沒產生任務 | 同上，要等欄位離開指定值再變回來 | 同上 |
| `manual`（手動套用） | 規則從「套用任務範本」清單消失，套用不到 | 回到清單，照常手動套用 | 不需要補；需要的記錄直接手動套用 |
| 已產生過任務的 `once` 規則 | 暫停期間不同步既有任務（例如負責人、到期日跟著記錄改） | 下一次存檔那筆記錄時照常同步 | 不需要補 |

  - 手動補建的任務不帶規則，日後若那筆記錄又由不符合變符合、規則照常觸發，會再產生一份規則任務（不會被當成已套用）；恢復確認框與手冊寫明這點。
  - 恢復後若問題還在，失敗從 0 重新累積，達門檻再暫停一次，暫停事件以「回歸」重開。
- 刪除規則：同交易刪掉它的 `Dynamic_RuleHealth` 列，暫停事件結案（`ruleDeleted`）。
- 套件匯出不帶暫停狀態；應用程式關閉再開啟不影響暫停狀態。

**使用者看到什麼**（O2-U7a）：

| 誰 | 看到 |
|---|---|
| 存檔的一般使用者 | 失敗那幾次：原本的「系統發生錯誤」加錯誤代碼（O1）。暫停後：存檔成功，沒有任何提示（O2-U7a） |
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
- 清理對象與天數：健康檢查紀錄與已寄完（`sent`、`sentDirect`、`failed`、`skipped`）的佇列列 `ops.healthResultRetentionDays`；`digested` 的列要等它的摘要信已寄完才照同一天數清（§17.2.6），`pending`、`sending` 不清；已結案事件（從結案時間起算）`ops.incidentRetentionDays`；樣本 `ops.incidentSampleRetentionDays`；稽核 `ops.auditRetentionDays`；`Dynamic_RuleFailure` 早於窗口上限（1440 分鐘）的列（規則失敗兩次後再也沒失敗時，殘留的列由這裡清）。
- 健康檢查只靠看門狗與 `ops.monitor`，不另開工作；看門狗不是排程工作，`Ops:Scheduler:Enabled` 關掉時看門狗照跑（排程器停了反而更需要它；`scheduler.jobs` 會報下次預定時間落後）。看門狗另有部署設定 `Ops:Watchdog:Enabled`（預設開），給測試與除錯用。

#### 17.2.9 資料庫唯讀、空間滿、連不上

- `IDatabaseErrors` 加三個分類（O2-T7；pg0 §5.7 的表多三列）：

| 分類 | SQLite | PostgreSQL（SQLSTATE） |
|---|---|---|
| 唯讀 `IsReadOnly` | `SQLITE_READONLY` 8 | `25006`（read_only_sql_transaction） |
| 空間滿 `IsStorageFull` | `SQLITE_FULL` 13 | `53100`（disk_full） |
| 連不上 `IsUnavailable` | `SQLITE_CANTOPEN` 14、`SQLITE_IOERR` 10 | 類別 `08`（connection exception）、`57P01`–`57P03`（伺服器關閉中或還沒能接受連線）；不帶 SQLSTATE 的 `NpgsqlException`（socket 斷線、連不到主機），但連線池耗盡已歸忙碌（pg0 §5.7），不重複算；主機名稱解析失敗由連線邊界包成 `DatabaseUnavailableException`（§17.12） |

- 一般請求遇到這三類：新增 `DatabaseUnwritableExceptionHandler`（排在 `DatabaseBusyExceptionHandler` 之後、`UnhandledExceptionHandler` 之前），回 503 加 `Retry-After: 60`；唯讀與空間滿的代碼是 `databaseUnwritable`、訊息四語「資料庫目前無法寫入，請聯絡系統管理員」，連不上是 `databaseUnavailable`、訊息「資料庫目前無法連線，請聯絡系統管理員」（O2-U8）；記 Warning、不算 500、不開產品缺陷事件（同一個原因已由 `database.writable` 開了一件）。`SessionMiddleware` 讀帳號時丟的同類例外也走這個處理器。
- 這段期間（唯讀或空間滿）：
  - 監控頁讀得到：`SessionMiddleware` 讀帳號、`SystemAdminBehavior` 讀管理員旗標、監控 API 本身都只讀，讀不受影響。（主控文字，o2b 實測後補：SQLite 唯讀檔在 WAL 模式下仍可讀，只有「資料庫檔唯讀、`-wal`／`-shm` 不存在、所在目錄也唯讀」時連 SELECT 都失敗（錯誤碼 8、擴充碼 1544），監控頁跟著 503；見 §17.12。）狀態板顯示 `database.writable` 嚴重，頁首提示「以下部分狀態只存在本實例記憶體，尚未寫入資料庫」。
  - 站內通知寫不進去；告警信走直送（§17.2.6）。
  - 登入成功仍可登入，稽核寫不進只記 Error log（§5.6）；管理類變更整筆失敗（503）。
- 恢復可寫後：看門狗下一輪探測成功，先依序補寫 `PendingIncidentWrites`（結果列、事件轉換、站內通知；§17.2.4 第 3 步），最後一筆就是這次探測成功，`database.writable` 隨之結案；收件匣累積的訊號跟著寫入。
- 連不上（不是唯讀）：**監控頁也打不開**。`SessionMiddleware` 與 `SystemAdminBehavior` 都要先讀資料庫確認身分與管理員旗標，handler 還沒執行就失敗，所有需要登入的 API（含監控頁）回 503 `databaseUnavailable`。維持 fail closed，不另開免驗證或只靠 cookie 的診斷入口：身分與權限讀不到資料庫就無從確認，開了等於繞過權限。這段期間能用的只有：log（每次失敗都有 Error 或 Warning）、直送告警信（有設 SMTP 且啟動後讀過通道設定時）、`/api/health/ready` 回 503（只有固定欄位，§10.2.4）。狀態板的內容在恢復後隨補寫進資料庫，事後在監控頁看得到。

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

- 監控、稽核、通道的請求一律實作 `ISystemAdminRequest`（由既有 `SystemAdminBehavior` 把關，fail closed）；路徑放在 `/api/v3/dynamic` 底下經過 `SessionMiddleware`（B1）。兩者都先讀資料庫，所以資料庫連不上時監控頁也一起不能用（§17.2.9），不為此開例外。
- 維運服務帳號（mcpO1）的行為者種類 O2 先預留，O2 不建帳號。

#### 17.2.11 開關組合下誰負責

| 組合 | 誰知道 | 誰處理 |
|---|---|---|
| SMTP 已設定並啟用 | 站內收件人＋通道收件信箱 | 照處理者標示（§17.2.2） |
| SMTP 未設定或停用 | 只有站內收件人；監控頁通道區塊提示「告警只在站內」 | 同上 |
| SMTP 設定了但寄不出去 | 站內收件人收到 `channel.smtp` 事件 | 系統管理員（地端客戶 IT） |
| 資料庫唯讀或空間滿 | 有 SMTP：通道收件信箱（看門狗直送）；沒 SMTP：只有打開監控頁的人與 log | 地端客戶 IT；雲端廠商（O3 前要人主動看） |
| 資料庫連不上 | 有 SMTP 且啟動後讀過通道設定：通道收件信箱（直送）；否則只有 log 與外部對 `/api/health/ready` 的探測；監控頁打不開 | 同上 |
| 排程器關閉（`Ops:Scheduler:Enabled` 否） | 看門狗照跑，`scheduler.jobs` 報落後；佇列與排程型檢查停 | 部署者 |
| 看門狗關閉 | 寫入探測、磁碟、排程器三項停，直送也停；排程型檢查照跑 | 部署者（只給測試與除錯） |
| 雲端版（`Ops:Edition`＝`Cloud`） | O2 與地端相同；廠商要等 O3 推送才收得到 | 照 §5.1 |

### 17.3 前端

**入口**：系統管理選單新增「維運監控」（`/system/ops`）與「稽核紀錄」（`/system/audit`），只給系統管理員（`systemLinks` 的管理員區塊）；首頁的系統管理捷徑共用同一份清單。

**監控頁** `/system/ops`，四個分頁（URL query `tab` 記住）：

1. **總覽**：最上面一列狀態卡：整體（取最糟的檢查）、資料庫可寫、未結事件（嚴重／警告數）、告警通道狀態、版本與版別、資料庫種類。下面是「需要處理」清單（未結且嚴重或警告的前 10 件，連到事件詳細）與排程器表（讀 ops info 的 jobs）。狀態板有只在記憶體的內容時顯示頁首提示（§17.2.9），有待補寫遺失時一併顯示筆數。API 回 503 `databaseUnavailable` 時整頁顯示「資料庫目前無法連線，監控頁暫時不能用；請看伺服器 log 或告警信」。
2. **事件**：篩選（狀態、類別、嚴重度）＋清單；每列：嚴重度圖示、標題、類別與處理者、回歸標記、次數、最近時間、動作（確認、結案；不能手動結案的不出現按鈕）。點開是詳細：全部欄位、狀態歷程（從稽核讀這件事件的確認與結案）、樣本（原文與 stack 用等寬字型、可捲動，預設摺疊）。
3. **健康檢查**：每個檢查一張卡：狀態、數值（依單位格式化，萬分比顯示成百分比）、上次與下次時間、「立即檢查」；彙總範圍是 `instance` 的另列各實例的最新狀態（只有一個實例時不列）；展開看趨勢（最近 24 小時／7 天的數值折線，用既有 echarts，比照 `ReportChart.vue` 的載入方式）與設定（開關、間隔、警告、嚴重；顯示出廠值與允許範圍，留空＝出廠值）。
4. **告警通道**：SMTP 表單（啟用、主機、埠、加密方式、帳號、密碼〔顯示「已設定」，留空不變，另有「清除」〕、寄件地址與名稱、收件信箱清單、信件語系〔四語下拉，留空＝出廠，顯示出廠是哪一種〕）＋「寄測試信」＋最近狀態（最近成功、最近失敗與原因、連續失敗次數）。Demo 站整頁唯讀並說明原因。

**稽核頁** `/system/audit`：篩選列（時間區間、行為者、動作分組或動作、來源管道、結果）＋清單（時間、行為者、動作標籤、目標、結果、來源管道）；點開是詳細抽屜：全部欄位與摘要（差異以「欄位：舊值 → 新值」列出，其餘結構化摘要縮排顯示）。無限捲動（keyset）。

**其他畫面**：

- 任務規則分頁（`TaskRulesTab.vue`）：暫停的規則列顯示紅色標籤「已自動暫停」與「恢復」（沒有編輯權時只有標籤）；點標籤展開暫停資訊（§17.2.7）。恢復前確認框寫明「暫停期間略過的 N 次存檔不會補產任務」，並依這條規則的觸發方式帶一句補救方式（§17.2.7 的表；`manual` 不需要補）。恢復後規則列顯示「上次暫停」一行。
- 通知鈴鐺：`opsIncident`、`opsRulePaused` 兩個種類的四語標籤與圖示；標題由 `DataJson` 的 `titleKey`＋`params` 依語系組字（§17.2.5）。
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

九張表、一組 migration（`OpsEventCenter`，SQLite 與 PostgreSQL 各一，用 `scripts/db/add-migration.sh` 產；O2-T1）。命名沿用 `Dynamic_`（T11）。時間欄位一律 UTC、名稱以 `Utc` 結尾（PostgreSQL 是 `timestamptz`），讀回用 `ScheduledJobConfiguration` 同一種 value converter 標 `Kind=Utc`。列舉一律 `HasConversion<string>()`、值小寫 camelCase。字串長度用 `HasMaxLength` 宣告（PostgreSQL 仍是 `text`，pg0 T6；測試的長度守衛會抓超長）。

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
| TitleKey | string(100) | 標題代碼（§17.2.2），畫面、鈴鐺、信件依語系組字 |
| TitleParamsJson | string，可空 | 組字用的白名單參數（代碼、數字、路由樣板），不含記錄值；上限 2000 字元 |
| NormalizedMessage | string(200)，可空 | 只有 `server`、`client`：正規化再遮蔽後的訊息，只在監控頁事件詳細顯示，不外送 |
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
| AuditLogId | long，可空 | 只有 `security`：對應的稽核列（誰給了誰看那一列的差異）；不設外鍵（稽核與事件各自依保留天數清） |
| RawMessage | string(500)，可空 | 原始訊息（C2：只留在產品內） |
| RawStack | string(4000)，可空 | |
| Browser | string(200)，可空 | 前端 |
| Locale | string(20)，可空 | 前端 |

- 索引：`(IncidentId, OccurredUtc)`；`OccurredUtc`（清理）。
- 每件事件最多 20 筆；保留 `ops.incidentSampleRetentionDays`（出廠 14）。`security` 的樣本只填 `OccurredUtc`、`UserId`（行為者）、`AuditLogId`、`RequestId`。

**`Dynamic_HealthCheck`**（規則表，啟動時補列，比照 B7「插入，撞唯一鍵就略過」）

| 欄位 | 型別 | 說明 |
|---|---|---|
| Code | string(50)，主鍵 | 檢查代碼，小寫 |
| Enabled | bool，可空 | null＝出廠值 |
| IntervalMinutes | int，可空 | null＝出廠值 |
| WarnThreshold | long，可空 | null＝出廠值；`database.writable` 是連續忙碌次數 |
| CriticalThreshold | long，可空 | null＝出廠值 |
| ModifiedUserId | string(50)，可空 | |
| ModifiedUtc | DateTime，可空 | |
| LastProbeUtc | DateTime，可空 | 只有 `database.writable` 用（寫入探測寫這欄） |
| LastAggregatedUtc | DateTime，可空 | 彙總交易的第一句更新它，取得列鎖（§17.2.4）；只當鎖用，不顯示 |
| LastCheckedUtc | DateTime，可空 | 目前投影的基準時間：投影來自哪個檢查時間的整體狀態；只往後推，補寫較舊的結果不動它（§17.2.4 第 2 步） |
| LastStatus | string(20)，可空 | 目前投影的整體狀態（多實例彙總後，§17.2.4）；`LastValue`、`LastMessageCode` 取最糟那個實例的 |
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
| TransitionId | string(32) | 檢查跑完時產生的穩定識別碼（§17.2.4 第 1 步）；彙總交易的持久化處理標記，有這一列就代表同交易的事件轉換、通知、寄送佇列都已寫入 |

- 索引：`TransitionId` **唯一**（補寫重播的去重；每列都有值，不是可空欄位上的唯一索引）；`(Code, CheckedUtc)`（趨勢與「截至 R 的最新一筆」）；`(Code, InstanceId, CheckedUtc)`（各實例截至 R 的最新一筆）；`CheckedUtc`（清理）。
- 量：七個檢查依出廠間隔一天約 2,000 列（`database.writable` 每分鐘佔大半），30 天約 6 萬列。
- 保留：`ops.healthResultRetentionDays`（出廠 30）。

**`Dynamic_RuleHealth`**

| 欄位 | 型別 | 說明 |
|---|---|---|
| RuleKind | string(20) | 主鍵之一；O2 只有 `taskRule` |
| RuleId | long | 主鍵之一；不設外鍵（日後的規則種類在不同表），刪規則時由 handler 一起刪 |
| ConsecutiveFailures | int | 最近一次成功之後的失敗總數（只顯示用，暫停看 `Dynamic_RuleFailure`） |
| StreakResetUtc | DateTime，可空 | 最近一次成功的**評估時間**或恢復的時間；不晚於它的失敗不算；只往後推（§17.2.7 順序與 reset 協定） |
| LastFailureUtc | DateTime，可空 | 已補記的失敗裡最晚的評估時間 |
| LastErrorCode | string(100)，可空 | 例外型別短名或錯誤碼（不存訊息文字，§17.2.7） |
| LastRequestId | string(32)，可空 | |
| PausedUtc | DateTime，可空 | 非空＝系統暫停中 |
| LastPausedUtc | DateTime，可空 | 最近一次暫停的開始時間，恢復後保留（「上次暫停」顯示用） |
| PauseCount | int | 累計被暫停幾次 |
| SkippedWhilePaused | int | 最近一次暫停期間略過幾次，下次暫停時歸零 |
| ResumedUtc | DateTime，可空 | |
| ResumedBy | string(50)，可空 | |

**`Dynamic_RuleFailure`**（滑動窗口用的失敗時間；O2-U12）

| 欄位 | 型別 | 說明 |
|---|---|---|
| Id | long，自動編號主鍵 | |
| RuleKind | string(20) | |
| RuleId | long | 不設外鍵（同 `Dynamic_RuleHealth`） |
| FailedUtc | DateTime | 失敗的規則**評估時間**，不是補記時間（§17.2.7） |
| RequestId | string(32)，可空 | |

- 索引：`(RuleKind, RuleId, FailedUtc)`（計數與清理）；`FailedUtc`（`ops.upkeep` 清殘留）。
- 量：每條規則最多約門檻筆數（失敗時清掉窗口外與最近一次成功之前的列，暫停後不再失敗）；刪規則與恢復時一起刪。

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
| MailLocale | string(10)，可空 | 告警信、摘要信、測試信的語系：`zh-TW`、`en-US`、`th`、`vi`；null＝出廠（O2 是繁中，p3 後跟系統預設語系；O2-U14） |
| LastClaimUtc | DateTime，可空 | 寄送佇列領取與摘要化交易的第一句更新它，取得通道列鎖（§17.2.6）；只當鎖用 |
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
| Transition | string(20) | `opened`、`regressed`、`escalated`、`renotify`、`occurred`（安全類每次異動）、`digest` |
| TransitionId | string(32)，可空 | 健康檢查轉換的穩定識別碼（＝`Dynamic_HealthCheckResult.TransitionId`，§17.2.4），用來對照直送紀錄；其他來源為 null。去重靠結果表的唯一索引（同交易），這欄不設唯一 |
| Subject | string(200) | 寫入時以通道的信件語系組好的快照（O2-U14） |
| Body | string(4000) | 同上；長度預算與截斷見 §17.2.6 |
| CreatedUtc | DateTime | |
| NextAttemptUtc | DateTime | |
| Attempts | int | 每次領取加一 |
| Status | string(20) | `pending`、`sending`、`sent`、`sentDirect`、`failed`、`skipped`、`digested` |
| ClaimedBy | string(100)，可空 | 領取者（實例 id＋本輪執行 id）；`sending` 時非空 |
| ClaimedUntilUtc | DateTime，可空 | 領取期限，過期可被重領 |
| DigestOutboxId | long，可空 | `digested` 的列掛到哪一封摘要信 |
| LastError | string(500)，可空 | |
| SentUtc | DateTime，可空 | |

- 索引：`(Status, NextAttemptUtc)`（領取）；`SentUtc`（節流計數與清理）；`CreatedUtc`（清理）；`DigestOutboxId`（摘要寄完才清來源列）。
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
  pendingDropped: number;            // 待補寫佇列滿而丟掉的筆數（§17.2.4）
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
  code: string; category: string; runner: 'watchdog'|'scheduler'; scope: 'instance'|'global';
  status: 'ok'|'warning'|'critical'|'error'|'notConfigured'|'notApplicable'|'pending';   // 整體狀態
  value?: number; unit: 'none'|'basisPoints'|'bytes'|'count'|'hours';
  messageCode?: string; checkedUtc?: string; nextDueUtc?: string;
  fromMemory: boolean; incidentId?: number;
  instances?: { instanceId: string; status: string; value?: number; messageCode?: string; checkedUtc: string }[];  // scope=instance 且有兩個以上在線實例時
}
// GET system/ops/health-checks
HealthCheckConfig {
  code: string; category: string; runner: string; unit: string;
  direction: 'higherIsWorse'|'lowerIsWorse'|'passFail';
  applicable: boolean;               // 這個資料庫種類適不適用
  enabled: Setting<boolean>; intervalMinutes: Setting<number>;
  warn?: Setting<number>; critical?: Setting<number>;   // passFail 只有 database.writable 的 warn（連續忙碌次數）
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
  titleKey: string; titleParams?: Record<string, string|number>; code: string;
  moduleKey?: string; ruleKind?: string; ruleId?: number; route?: string;
  occurrenceCount: number; firstSeenUtc: string; lastSeenUtc: string; firstVersion: string; lastVersion: string;
  regression: boolean; reopenCount: number;
  acknowledgedBy?: string; acknowledgedUtc?: string; resolvedBy?: string; resolvedUtc?: string; resolvedReason?: string;
  handler: 'designer'|'it'|'vendor'|'admin'|'contactVendor';
  manualResolvable: boolean;
}
IncidentDetail extends IncidentSummary {
  normalizedMessage?: string;        // 只有 server、client；不外送（§17.2.2）
  samples: { occurredUtc: string; requestId?: string; userId?: string; version?: string; route?: string;
             rawMessage?: string; rawStack?: string; browser?: string; locale?: string;
             auditLogId?: number }[];   // security 的樣本只有行為者與稽核列
  instances?: HealthCheckState['instances'];   // health 來源
  history: AuditSummary[];           // 這件事件的確認與結案，新的在前，最多 100 筆
  historyTruncated: boolean;         // 超過 100 筆（完整的到稽核頁查；§17.12 審查修正）
}
// POST .../acknowledge  { expectedStatus: 'open' }              → IncidentSummary
// POST .../resolve      { expectedStatus: 'open'|'acknowledged'; note?: string /* ≤500，進稽核摘要 */ } → IncidentSummary
// GET system/ops/channels/smtp
SmtpChannel {
  enabled: boolean; host?: string; port: number; security: 'none'|'startTls'|'sslOnConnect';
  userName?: string; hasPassword: boolean; passwordUnreadable: boolean;
  fromAddress?: string; fromName?: string; toAddresses: string[];
  mailLocale: Setting<string>;      // 'zh-TW'|'en-US'|'th'|'vi'；isFactory＝沒設（O2-U14）
  lastSuccessUtc?: string; lastFailureUtc?: string; lastError?: string; consecutiveFailures: number;
  editable: boolean;                 // Demo 站為 false
}
// PUT system/ops/channels/smtp；password：省略或 null＝不變、""＝清除、其他＝新密碼
{ enabled: boolean; host: string; port: number; security: string; userName?: string; password?: string;
  fromAddress: string; fromName?: string; toAddresses: string[]; mailLocale?: string|null /* null＝出廠，省略＝不變 */ }
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
autoPause?: { pausedUtc: string; consecutiveFailures: number; lastErrorCode?: string;
              lastRequestId?: string; skippedWhilePaused: number; pauseCount: number } | null;
lastPause?: { pausedUtc: string; resumedUtc: string; skippedWhilePaused: number } | null;   // 已恢復、還沒再暫停
failureStreak?: { consecutiveFailures: number; lastFailureUtc: string } | null;   // 未暫停但有連續失敗
// POST modules/{key}/task-rules/{ruleKey}/resume → 該規則（autoPause 為 null）
```

**錯誤碼**（`ServiceResult` 的 `Code`；訊息照既有 `*Messages.cs` 規則四語）：

| HTTP | 代碼 | 時機 |
|---|---|---|
| 400 | （欄位驗證） | 間隔或門檻超出範圍、門檻先後不合理、Email 格式、收件信箱 0 或超過 20 個、埠不在 1–65535、時間區間超過 366 天、`limit` 超出、信件語系不是四語之一 |
| 400 | `smtpPlaintextAuth` | 加密方式是 `none` 卻填了帳號或密碼（§17.2.6） |
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
| 503 | `databaseUnavailable` | 資料庫連不上（§17.2.9），帶 `Retry-After`；所有需登入的 API 都可能回 |

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
| `ops.loginFailureWindowMinutes` | `security.loginFailures` 往回數幾分鐘的登入失敗 | 整數（分） | 15 | 5–1440 |
| `ops.schedulerLagIntervals` | 排程工作的下次預定時間落後幾個間隔，`scheduler.jobs` 直接判嚴重 | 整數（個） | 3 | 2–100 |

- 和 §11 初稿的差異（§11 已同步）：`ops.clientErrorSampleRetentionDays` 改名 `ops.incidentSampleRetentionDays`（伺服器例外原文也是樣本，同樣敏感）；`ops.alertRecipients` 只管站內，Email 收件人改在通道設定（O2-U1）；新增 `ops.monitorScanMinutes`、`ops.upkeepMinutes`（`IScheduledJob` 規定每個工作要有間隔政策）、三項 Email 政策，以及依審查從「技術限制」改成政策的 `ops.loginFailureWindowMinutes`、`ops.schedulerLagIntervals`（這兩個數字會改變告警結果，不是技術限制）。
- `ops.rulePauseConsecutiveFailures`、`ops.rulePauseWindowMinutes` 改值後，從下一次失敗起用新值往回數（滑動窗口，§17.2.7）；窗口調大時，之前已依舊窗口清掉的失敗列不會找回。
- 間隔類政策照 §10.6：下一次排程開始生效。
- 四語文案；政策頁「維運」分類。

#### 17.6.2 健康檢查出廠值與技術上下限（`HealthCheckCatalog`，T12）

| 代碼 | 開關出廠 | 間隔出廠（分）／範圍 | 警告出廠／範圍 | 嚴重出廠／範圍 | 方向 |
|---|---|---|---|---|---|
| `database.writable` | 開 | 1／1–60 | 連續忙碌 3 次／1–60 | — | 成敗（警告門檻是連續忙碌次數） |
| `storage.diskFree` | 開 | 5／1–1440 | 2000／100–5000（萬分比） | 1000／50–5000 | 越小越糟 |
| `scheduler.jobs` | 開 | 5／1–1440 | 3／1–100 | 10／1–1000 | 越大越糟 |
| `database.walSize` | 開 | 15／5–1440 | 268435456／16 MB–16 GB | 1073741824／16 MB–64 GB | 越大越糟 |
| `database.integrity` | 開 | 1440／60–10080 | — | 1／1–1（任何問題即嚴重） | 越大越糟 |
| `backup.freshness` | 開 | 60／15–1440 | 26／1–720（時） | 50／1–720 | 越大越糟 |
| `security.loginFailures` | 開 | 5／1–60 | 20／1–10000 | 100／1–100000 | 越大越糟 |

- `database.writable` 不能關（寫死：它是唯讀與空間滿唯一的偵測，關掉等於失明；§17.9）；其他可關。
- `security.loginFailures` 的計數窗口是政策 `ops.loginFailureWindowMinutes`、`scheduler.jobs` 的落後倍數是政策 `ops.schedulerLagIntervals`（§17.6.1）；放在政策目錄而不是檢查規則表，因為規則表每列只有警告與嚴重兩個門檻欄，為一個檢查多加參數欄不划算。初稿把這兩個標成技術限制，會改變告警結果，依鐵則 2 改成可設定。

### 17.7 PostgreSQL 相容注意

O2 在 pg0 之後，合併前兩種資料庫都要驗（使用者裁決 §3 第 10 條）。

- **migration**：一組、兩份、同名，EF 產生不手改；`MigrationConsistencyTests` 把關。沒有部分索引：事件的「一個指紋一列」用全表唯一索引，不用 `WHERE Status <> 'resolved'` 這種兩邊寫法不同的 filter（MCP §13.3 同一原則）。
- **時間**：所有新時間欄位名稱以 `Utc` 結尾、`Kind=Utc`（模型測試與 Kind 守衛會抓）。`Notification.CreatedTime` 仍是牆上時間（`IBusinessTime`），寫通知時照既有寫法，不混用。
- **並行**（以 PostgreSQL 為準，不靠 `WriteGate` 或全庫鎖；§10.7）：
  - 事件的新增或更新：先條件式更新（`WHERE Fingerprint = ? AND Status = ?`），影響 0 列再新增；新增撞唯一鍵（`IDatabaseErrors.IsUniqueViolation`）就重來，最多三次。撞鍵是 `SaveChanges` 的錯誤，有 savepoint 保護，交易還能用（pg0 §4）；`ExecuteUpdate` 的錯誤不能在交易內接住後繼續（backend 規則），所以順序是「先 `ExecuteUpdate`、後 `SaveChanges` 新增」。
  - 計數一律 `SET x = x + n`，不讀出來加完再寫回。
  - 規則失敗計數、暫停的條件式更新見 §17.2.7：第一句先更新 `Dynamic_RuleHealth` 那一列取得列鎖，同一規則的並行失敗在 Read Committed 下依序執行、後到者讀得到先到者的失敗列；暫停的 `WHERE PausedUtc IS NULL` 保證兩個並行失敗同時達門檻時只有一個交易影響 1 列，只開一次暫停事件。
  - 規則失敗與成功的先後以規則的評估時間為準，不以補記時間；成功的 reset 只往後推（`StreakResetUtc < 評估時間` 才更新），首次建列撞主鍵改走更新（§17.2.7 順序與 reset 協定）。
  - 健康檢查的多實例彙總同理：第一句先更新 `Dynamic_HealthCheck` 那一列的 `LastAggregatedUtc`（§17.2.4）。去重先查 `TransitionId`、新增撞唯一鍵也當已處理（撞鍵是 `SaveChanges` 的錯誤，savepoint 保護）；目前投影的更新帶 `WHERE LastCheckedUtc IS NULL OR LastCheckedUtc <= R`，晚到的舊結果不會倒退投影。
  - 確認與結案用 `expectedStatus` 的條件式更新，影響 0 列回 409。
  - 寄送佇列：領取、寄送、記結果三步（§17.2.6）。領取交易的第一句先更新 `Dynamic_AlertChannel` 那一列（`LastClaimUtc`）取得通道列鎖，額度計算、領取、摘要化、摘要信掛列都在這把鎖後面，同一通道的寄送者在 Read Committed 下依序執行，不會一起看到「還差一封」而超額；摘要化只動 `Status = 'pending' AND ClaimedBy IS NULL` 的列，不會把別人正在寄的信併進摘要。領取是條件式更新把 `Status` 改成 `sending` 並寫 `ClaimedBy` 與期限，影響 0 列就是別人領走；記結果以 `ClaimedBy = 我` 為條件。`ops.monitor` 的租約讓同一時間通常只有一個實例在送，但正確性不靠租約：租約交接的邊界、領取過期後的重領，都由這兩個條件式更新擋住重複領取；寄 SMTP 時不在交易裡，不持全庫寫入鎖。
  - 這些並行案例的自動測試除了一般的 PostgreSQL 雙跑，另在 pg0 的無鎖模式（`PostgresWriteSettings.Unserialized`，閘門與全庫 advisory lock 都不做事）再跑一次，避免全庫鎖把並行問題蓋掉（§17.8 標「無鎖」的案例）。
- **交易外的 `ExecuteUpdate`／`ExecuteDelete`**：O2 沒有。清理、計數、狀態更新全部在交易型請求內（經全庫寫入鎖）。唯一的例外是看門狗的寫入探測本身：它也是交易型請求，探測失敗時那個交易直接回滾。
- **provider 專屬的地方**（交付說明要列出）：`database.integrity` 的 `PRAGMA quick_check`、`PRAGMA foreign_key_check` 與 `database.walSize` 的 WAL 檔路徑，放在 `Persistence` 的 `SqliteDatabaseHealthProbe`；PostgreSQL 的實作 `PostgresDatabaseHealthProbe` 放在 `Persistence.Postgres`（O2 只回「不適用」與產品層檢查）。Application 只看 `IDatabaseHealthProbe` 介面。
- **錯誤分類**：唯讀、空間滿走 `IDatabaseErrors`（§17.2.9），不 catch `SqliteException`、不比對訊息。
- **字串比對**：動作代碼、檢查代碼、指紋一律區分大小寫的等值比對（`COLLATE "C"`）；動作分組用 `IN` 展開，不用 `LIKE`（SQLite 的 `LIKE` 對 ASCII 不分大小寫，與 PostgreSQL 不同）。
- **大數值**：`long` 在兩邊都是 64 位元整數；萬分比、位元組都放得下。
- **唯讀的重現方式**（測試與驗收）：PostgreSQL 用 `ALTER DATABASE <db> SET default_transaction_read_only = on` 再清連線池，新連線一律 `25006`；SQLite 檔案權限改唯讀對已開啟的連線無效，驗收時連線字串加 `Pooling=False` 再改權限（⚠️ 未實測，o2b 第一步驗證；§17.10.6）。

### 17.8 測試與驗收

先說驗收方式再動手（AGENTS.md）。下表「雙跑」欄標 ✓ 的類別標 `[Trait("Category", "Postgres")]`，gate 另在 PostgreSQL 上跑（pg0 §6.3 的 filter）；標「✓ 無鎖」的並行案例在 PostgreSQL 上另以無鎖模式（`PostgresWriteSettings.Unserialized`；WebAPI 測試主機已有 `TestApp.WithoutWriteSerialization`，pg0 排程器並行測試用過）再跑一次，確認正確性不靠全庫寫入鎖（§17.7）。自動測試一律用 `TimeProvider` 的手動時鐘（O1 的 `ManualClock`），不真的等待。

**後端自動測試**

| 編號 | 前置 | 操作 | 預期 | 雙跑 |
|---|---|---|---|---|
| A01 | 空資料庫 | 套 migration | 九張表存在；`MigrationConsistencyTests` 兩個 provider 都沒有待產生的變更 | —（一致性測試不需 PostgreSQL 伺服器，照 pg0 §5.3 隨 SQLite 跑；PostgreSQL 的建表由樣板資料庫套 migration 驗證） |
| A02 | 管理員 | 改某模組權限 | 同一交易寫一列 `permission.update`、風險高、摘要只含變動的格子；讓稽核寫入丟例外時，權限沒有被改 | ✓ |
| A03 | 已有帳號 `sales01` | 用錯密碼登入；用不存在的帳號登入 | 各一列 `login.failed`：前者目標是 `sales01`、原因 `wrongPassword`；後者目標空、原因 `unknownAccount`、摘要不含輸入字串 | ✓ |
| A04 | 讓稽核寫入失敗 | 正確密碼登入 | 登入成功，Error log 一則；稽核正常時另驗：成功列的行為者與目標都是剛登入的帳號（`ActorOverride`） | — |
| A05 | 反射掃描 Application 組件 | 列出所有交易型、且是管理員或設計器寫入的請求 | 全部實作 `IAuditedRequest`，或在白名單且有理由 | — |
| A06 | 改政策、重設密碼 | 讀稽核摘要 | 政策有舊值與新值；重設密碼的摘要沒有密碼；摘要裡任何 `fwp_`、`fwc_` 開頭的值都是 `***` | — |
| A07 | 400 列稽核、時間分散 | 依動作分組、行為者、時間區間分頁查詢 | 結果與篩選一致、keyset 不重不漏；非管理員 403；Demo 登入開啟時回應沒有 `clientIp` | ✓ |
| A08 | 稽核列超過保留天數 | 跑 `ops.upkeep` | 超過的被刪、未超過的留著、多一列 `ops.retention.purge`；分批時每批一個交易 | ✓ |
| A09 | 讓稽核寫入失敗 | 呼叫 `WriteNowAsync`；在交易型請求裡呼叫 `WriteNowAsync`、`TryWriteNowAsync` | 前者丟 `AuditWriteException`（MCP fail closed 用得到的失敗）；交易內呼叫一律丟 `InvalidOperationException`（`TryWriteNowAsync` 也丟、不回 `false`），而且沒有建立新的 DI scope（以計數的 scope factory 驗：守衛在建 scope 之前） | ✓ |
| A10 | 已登入的管理員 | 在 `AuditOrigin.BeginSignIn` 範圍外寫一筆帶 `ActorOverride` 的稽核 | 丟 `InvalidOperationException`，沒有寫入；架構測試：`BeginSignIn` 的呼叫者只有 `LoginHandler`、`DemoLoginHandler` | — |
| A11 | 帳號 `sales01`、`admin01`；瀏覽器分別帶：`sales01` 的有效 cookie、`sales01` 已重設密碼而失效的 cookie、已停用帳號的 cookie | 各用 `admin01` 正確密碼登入（切換帳號）；帶 `sales01` 有效 cookie 用 `admin01` 錯密碼登入 | 三次成功列的行為者與目標都是 `admin01`、不是 cookie 裡的帳號，也沒有因環境已有身分而丟例外；失敗列行為者 null、目標 `admin01`、原因 `wrongPassword`；登入回應照常寫入新 cookie | — |
| B01 | 兩個實例的收件匣 | 同一指紋各送 3 次、各自 flush | 一列事件、次數 6、狀態開啟；站內通知只發一次 | ✓ 無鎖 |
| B02 | 一件已結案事件 | 同指紋再出現 | 同一列重開、`Regression` 真、`ReopenCount` 1、再通知 | ✓ |
| B03 | 兩個管理員 | 同時確認同一件事件 | 一個成功、一個 409 `incidentStateChanged` | ✓ 無鎖 |
| B04 | 健康檢查類事件 | 呼叫結案 | 409 `incidentManagedBySource` | — |
| B05 | 缺陷事件，最近一次出現早於自動結案時數 | 跑 `ops.upkeep` | 結案、原因 `expired`；嚴重未確認事件超過重複提醒時數時再通知一次 | ✓ |
| B06 | 一個 API 丟未處理例外 | 等收件匣 flush | 事件類別產品缺陷、嚴重度警告、有一筆樣本含原始訊息；前端錯誤同理但嚴重度資訊、不發通知 | — |
| B07 | 每件事件已有 20 筆樣本 | 再來一筆 | 仍是 20 筆、最舊的被刪 | ✓ |
| B08 | 管理員 A | 把 B 設成系統管理員；按「確認」；再把 C 設成系統管理員 | 一件安全事件、次數 2；兩次各一組站內通知與一列寄送佇列（第二次在確認之後仍通知）；兩筆樣本的 `AuditLogId` 各指到對應的 `user.update` 稽核列，稽核差異看得到給了誰 | ✓ |
| B09 | 一個 API 丟例外，訊息是 `客戶 王小明 token=abc123 fwp_0123456789abcdef 無法出貨` | 等收件匣 flush、開事件並寄信（假寄送器） | 樣本保有原文；事件的 `NormalizedMessage` 沒有 `abc123`、`fwp_0123`；站內通知的 `TitleSnapshot`、`DataJson` 與佇列的 `Subject`、`Body` 完全不含 `王小明`、`abc123`、`fwp_`、`無法出貨` | — |
| C01 | 資料庫唯讀（主控文字，o2b 改：測試用連線攔截器在每次開連線時切換，SQLite `PRAGMA query_only`、PostgreSQL `SET SESSION CHARACTERISTICS AS TRANSACTION READ ONLY`，得到真的錯誤碼 8／`25006`；見 §17.12） | 看門狗跑一輪 | 狀態板 `database.writable` 嚴重、訊息代碼 `readOnly`；事件放進待寫；直送佇列有一封 | ✓ |
| C02 | 接著把資料庫改回可寫 | 看門狗再跑一輪 | 待寫的事件與站內通知補寫、`database.writable` 結案原因 `recovered` | ✓ |
| C09 | 資料庫不可寫期間，磁碟探測依序回 15%、5%、30%（警告→嚴重→正常） | 恢復可寫、看門狗補寫 | 待補寫佇列有三筆；補寫後結果列三筆依時間排列、事件依序開啟→升級→結案；開啟與升級各補一則站內通知；直送已寄的那封佇列列是 `sentDirect`，沒直送的過時信是 `skipped` | ✓ |
| C10 | 兩個實例 id（A、B）的磁碟探測 | A 回 5%、B 回 30%；B 再回 30%；A 回 30%；A 再回 5% 後停止回報三個間隔、B 回 30% | A 異常時事件開啟嚴重，B 的正常不結案；A 正常後結案；A 停止回報後退出彙總，B 正常即結案；兩個實例的彙總交易並行時結果一致 | ✓ 無鎖 |
| C11 | 以建構的例外驗 `IDatabaseErrors` | `SqliteException` 錯誤碼 8、13、14；`PostgresException` SQLSTATE `25006`、`53100`、`08006`、`57P01`；不帶 SQLSTATE、內層是 `SocketException` 的 `NpgsqlException`；連線池耗盡（內層 `TimeoutException`） | 依序是唯讀、空間滿、連不上；池耗盡仍是忙碌、不是連不上 | ✓ |
| C12 | 讓存檔的 `SaveChanges` 丟「空間滿」；讓 `SessionMiddleware` 讀帳號時丟「連不上」 | 一般使用者存檔；管理員開監控頁 | 前者 503 `databaseUnwritable` 加 `Retry-After`；後者 503 `databaseUnavailable`；兩者都沒有新增產品缺陷事件 | — |
| C13 | 排程器關閉（`Ops:Scheduler:Enabled` 否）、SMTP 換成假寄送器、寫入探測回「空間滿」 | 看門狗跑一輪；恢復後補寫 | 不經 `ops.monitor` 就寄出一封直送信、同一 `TransitionId` 只寄一次；補寫後對應的佇列列是 `sentDirect`，沒有第二封 | — |
| C14 | 讓彙總交易在**提交成功之後**丟例外（交易攔截器在 commit 完成後拋），一筆 `storage.diskFree` 的嚴重結果因此進待補寫佇列 | 看門狗下一輪補寫；再讓同一筆在補寫時又提交後丟一次，再補一輪 | 結果列只有一列（`TransitionId` 唯一）、事件次數只加一次、站內通知與寄送佇列各一則；補寫的交易沒有新增任何列就提交並移除佇列項目。SMTP 已設定的變體（o2c 審查修正）：執行器把這筆交給了直送，佇列寄送者與直送都實際跑（誰先跑、或直送時資料庫讀不到），總寄送剛好一次 | ✓ |
| C15 | 兩個實例（A、B）的磁碟檢查；B 已寫入 10:03 的 `ok`（總覽 `ok`、`LastCheckedUtc` 10:03），A 在 10:01 的 `critical` 還在待補寫 | A 補寫 10:01；接著 A 寫入 10:04 的 `critical` | 補寫 10:01 後：結果表多一列 10:01，總覽仍是 `ok`、`LastCheckedUtc` 仍是 10:03，事件不重開（已在 10:03 之後結案）；10:04 寫入後：總覽 `critical`、事件以回歸重開、轉換時間 10:04；兩台的補寫與寫入並行時結果一樣 | ✓ 無鎖 |
| C03 | 資料庫唯讀 | 一般使用者存檔 | 503 `databaseUnwritable` 加 `Retry-After`，沒有新增產品缺陷事件 | ✓ |
| C04 | 磁碟探測換成假的 `IDiskSpace`（剩 15%、5%） | 看門狗兩輪 | 先開警告、再升嚴重（狀態退回開啟、再通知）；回到 30% 時結案 | — |
| C05 | 一個工作連續失敗 3 次；另一個工作的下次預定時間落後四個間隔 | `scheduler.jobs` | 前者警告；後者嚴重 | — |
| C06 | 改 `storage.diskFree` 門檻成警告 500、嚴重 1000 | PUT | 400（越小越糟時警告要大於嚴重）；改成合理值後生效；送 null 回到出廠值；寫稽核 | — |
| C07 | 排程器關閉 | 看門狗 | 寫入探測、磁碟、排程器三項照跑 | — |
| C08 | SQLite | `database.walSize`、`database.integrity` | 有數值與結果；PostgreSQL 上 `walSize` 回不適用、不出現在清單、按立即檢查 409 | ✓ |
| D01 | SMTP 換成假的 `IMailSender` | 開一件基礎設施警告事件 | 佇列一列；`ops.monitor` 送出；主旨與內文由標題代碼與白名單參數組成，沒有 `NormalizedMessage` | ✓ |
| D02 | 假寄送器一律失敗 | 跑五次重試（快轉時鐘） | 間隔 1、5、15、60 分；第 5 次後標失敗、開 `channel.smtp` 事件、這件事件沒有進佇列；之後一封成功就結案 | ✓ |
| D03 | 每小時上限 3 | 一次開 5 件事件 | 寄 3 封，2 列併入摘要；下一個小時一封摘要信列出那 2 件 | ✓ |
| D04 | 客戶設定類事件 | 出廠政策 | 只有站內通知，不進佇列 | — |
| D05 | 存 SMTP 設定 | 讀資料表與 GET；再以省略 `password`、`password: ""` 各存一次 | 密碼欄位是加密字串；GET 只回 `hasPassword`；換一套 Data Protection 金鑰後 GET 回 `passwordUnreadable`；省略（或 null）密碼不變、`""` 清除（§17.5） | — |
| D06 | Demo 登入開啟 | PUT 通道、寄測試信 | 403 | — |
| D07 | 假寄送器在連線階段失敗 | 寄測試信 | 422 `smtpTestFailed`、`stage` 為 `connect`、回應不含例外全文；30 秒內再寄 409 `smtpTestTooSoon` | — |
| D08 | 一列待寄 | 兩個寄送者同時領取；讓領取者在寄出後、記結果前「當掉」，快轉 2 分鐘 | 同時領取只有一個成功；過期後被重領、`Attempts` 加一、再寄一次（至少一次）；原領取者再記結果影響 0 列、只記 Warning | ✓ 無鎖 |
| D09 | 每小時上限 3、一次開 5 件事件 | 跨整點跑 `ops.monitor`；讓摘要信第一次寄送失敗；跑 `ops.upkeep` 並快轉超過保留天數 | 摘要信一列、2 列 `digested` 的 `DigestOutboxId` 指向它；摘要信還在重試時來源列不被清；摘要信寄出後才清 | ✓ |
| D10 | 存 SMTP 設定 | 加密方式 `none` 加帳號密碼；`startTls` 對一台不支援 STARTTLS 的假伺服器寄測試信 | 前者 400 `smtpPlaintextAuth`；後者 422、`stage` 為 `tls`，沒有退回明文 | — |
| D11 | 每小時上限 3、最近一小時已寄 2 封、佇列另有 4 列到期的 `pending` | 兩個寄送者同時跑領取；另一組：寄送者 X 已把 R1 領成 `sending` 時，寄送者 Y 達上限而摘要化 | 第一組只有一封被領走（額度在通道列鎖內數，不會兩個都看到「還差一封」）、其餘 3 列 `digested`；第二組 R1 仍是 `sending` 而不是 `digested`、寄出後是 `sent`，摘要信不列 R1；任何時刻一小時內 `sent`＋`sending` 不超過 3 | ✓ 無鎖 |
| D12 | 一小時內 60 列 `digested`，其中一半的標題參數組出來超過 70 字 | 整點後跑 `ops.monitor` | 一封摘要信：最多 50 行、每行不超過 70 字（超過的以「…」結尾）、`Body` 不超過 4000 字；結尾「另有 M 則」的 M＝60－已列出的行數；60 列的 `DigestOutboxId` 都指向它 | — |
| D13 | 通道 `MailLocale` 設 `en-US` | 開一件基礎設施事件；把語系改成 `vi` 後再開一件；清成 null 再開一件；寄測試信 | 第一封英文、第二封越南文、第三封繁中（O2 出廠），語系改了之後第一封的快照不變；測試信是當時的通道語系 | — |
| E01 | 一條注入失敗的任務規則、門檻 5 | 存檔 5 次 | 前 4 次 500、計數 1–4；第 5 次後暫停、暫停事件嚴重、出廠收件人（最後修改者＋管理員）各一則 `opsRulePaused`、稽核 `taskRule.autoPause` | ✓ |
| E02 | 接續 E01 | 再存檔 | 成功、沒產生任務、`SkippedWhilePaused` 加一；手動套用清單沒有這條規則 | ✓ |
| E03 | 失敗 3 次後成功 1 次，再失敗 4 次 | — | 不暫停（中間有成功，計數重來） | ✓ |
| E04 | 門檻 5、窗口 60 分鐘，第 0、59、61、62、63、64 分鐘各失敗一次、中間沒有成功 | — | 第 64 分鐘那次暫停（往回 60 分鐘有 5 次；滑動窗口，O2-U12） | ✓ |
| E04b | 同上，第 0、10、20、30、70 分鐘各失敗一次 | — | 不暫停（第 70 分鐘往回 60 分鐘只有 10、20、30、70 四次）；第 0 分鐘那列已被清掉 | — |
| E05 | 兩個請求並行，第 4、第 5 次同時失敗 | — | 只暫停一次、只一件暫停事件、只一組通知；不會兩個都沒看到對方而漏掉暫停 | ✓ 無鎖 |
| E06 | 已暫停（`onCreate` 規則）、暫停期間新增記錄 R1、移除故障注入 | 有編輯權的設計者按恢復；再存 R1；新增 R2 | 恢復成功、暫停事件結案 `ruleResumed`、稽核 `taskRule.resume`、規則有 `lastPause`；R1 再存檔仍沒有任務（不補產）；R2 產生任務 | ✓ |
| E06b | 已暫停（`onUpdateMatch` 規則，條件「狀態＝已出貨」），暫停期間記錄 R3 由「未出貨」改成「已出貨」 | 恢復；R3 改備註再存；R3 改回「未出貨」再改「已出貨」 | 改備註不產生任務；改回再改成立後產生任務 | ✓ |
| E06c | 已暫停（`manual` 規則） | 暫停中讀「套用任務範本」清單；恢復後讀清單並套用 | 暫停中清單沒有它、直接套用回 404；恢復後出現在清單、套用成功 | — |
| E07 | 已暫停、故障仍在 | 恢復後再失敗 5 次 | 再次暫停、暫停事件以回歸重開 | ✓ |
| E08 | 只有讀權的設計者 | 按恢復 | 403；沒暫停的規則按恢復 409 `ruleNotPaused` | — |
| E09 | 失敗計數的寫入丟例外 | 存檔 | 使用者照樣收到原本的 500，Error log 一則 | — |
| E10 | 事件串聯過深 | 存檔 | 409 照舊；開一件客戶設定事件（錯誤碼 `event.tooDeep`），不計數、不暫停 | — |
| E11 | 收件人政策設成只有 `ruleEditor`，而最後修改者已停用 | 觸發暫停 | 改送全部系統管理員、記 Warning | — |
| E12 | 外層請求巢狀呼叫內層、內層丟例外 | — | 只在最外層記一次失敗 | — |
| E13 | 下方「E13 例外矩陣」的每一列，由任務規則丟出 | 每列各存檔 5 次 | 每列的回應碼與矩陣一致；標「不計」的不寫 `Dynamic_RuleHealth`、不暫停；標「計」的計數並在第 5 次暫停；同一列另直接呼叫 `RuleFailureClassifier.IsAttributable`，結果與矩陣一致 | ✓ |
| E14 | 門檻 5、已因 5 次失敗暫停 | 恢復後 1 分鐘內失敗 1 次；再失敗 4 次 | 第一次不暫停（恢復時已刪掉暫停前的失敗列、更新 `StreakResetUtc`）；第五次才再暫停、事件標回歸 | — |
| E15 | 門檻 5、已記 4 次失敗；故障注入改成可逐次控制 | 請求 P 的規則評估在 t5 失敗、業務回滾後先暫停在補記之前；請求 Q 在 t6 評估同一規則成功並提交；放行 P 補記；之後再失敗 4 次、第 5 次 | Q 留下 `StreakResetUtc = t6`、`ConsecutiveFailures` 歸 0；P 的 `FailedUtc` 是 t5（不是補記時間），不新增 `Dynamic_RuleFailure`、計數仍 0；之後第 4 次不暫停，第 5 次才暫停 | ✓ 無鎖 |
| E16 | 一條從沒失敗過的規則（沒有健康列） | 同 E15 的時序：P 在 t1 失敗、Q 在 t2 成功並提交、P 才補記；之後再失敗 4 次 | Q 因有在途失敗而新增一列（計數 0、`StreakResetUtc = t2`）；P 補記後計數仍 0；再失敗 4 次不暫停。對照組：沒有在途失敗時，成功不新增健康列 | — |
| F01 | `ops.upkeep` 清理 | 健康紀錄、樣本、已結事件、佇列分別超過天數；一條規則只失敗過 2 次、之後 1 天沒再失敗 | 各自被清；未結事件與其樣本不受事件天數影響；`pending`、`sending` 的佇列列不清；那條規則殘留的 `Dynamic_RuleFailure` 被清 | ✓ |

**E13 例外矩陣**（`RuleFailureClassifier` 與例外處理器鏈的對照；每列用真的例外型別建構，經 WebAPI 測試主機的處理器鏈）：

| 例外 | 回應 | 歸屬 |
|---|---|---|
| SQLite `SQLITE_BUSY` 5、`SQLITE_LOCKED` 6；`DatabaseBusyException`（寫入閘門排隊逾時） | 503 `Retry-After` | 不計 |
| PostgreSQL `40001`、`40P01`、`55P03`；連線池耗盡（無 SQLSTATE、內層 `TimeoutException`） | 503 `Retry-After` | 不計 |
| 唯讀：SQLite 8、PostgreSQL `25006` | 503 `databaseUnwritable` | 不計 |
| 空間滿：SQLite 13、PostgreSQL `53100` | 503 `databaseUnwritable` | 不計 |
| 連不上：SQLite 14、10；PostgreSQL `08006`、`57P01`；無 SQLSTATE、內層 `SocketException` | 503 `databaseUnavailable` | 不計 |
| `DbUpdateConcurrencyException` | 409 | 不計 |
| `FrozenFieldException` | 409 | 不計 |
| `OperationCanceledException`、`TaskCanceledException`，用戶端已中斷 | 不回應（連線已斷） | 不計 |
| 同上，用戶端沒中斷（內部逾時取消） | 500 | 不計 |
| 查詢中斷：SQLite `SQLITE_INTERRUPT` 9、PostgreSQL `57014` | 500 | 不計 |
| 唯一鍵衝突：SQLite 19 的 unique／primary key、PostgreSQL `23505` | 500 | 不計 |
| 上面任一種被包在 `DbUpdateException` 或其他例外的 `InnerException` 裡 | 同內層 | 同內層 |
| `InvalidOperationException`、`NullReferenceException`、`InjectedFaultException` | 500 | 計 |
| 數值溢位（`IsNumericOverflow`） | 500 | 計 |

- 「不計」而回 500 的三列是刻意的：取消與查詢中斷是逾時或關機，唯一鍵衝突是並行寫入撞在一起，都不是規則寫錯；它們照樣開產品缺陷事件（`server` 來源），只是不累加暫停計數。
- 數值溢位歸規則本身（規則算出超界的值）；計畫階段核對既有寫入流程有沒有先轉成 400，有的話這列改成「400、不計」。
- 新增例外處理器或改 `IDatabaseErrors` 的分類時，必須同時在這張矩陣加列，測試才會涵蓋（計畫與審查清單各寫一行）。

**前端自動測試**（vitest）

- 監控頁：狀態卡依 overview 顯示；`memoryOnly` 時有頁首提示（`pendingDropped` 大於 0 時帶筆數）；503 `databaseUnavailable` 時整頁提示；不能手動結案的事件沒有結案按鈕；409 `incidentStateChanged` 提示後重新整理；標題由 `titleKey`＋`titleParams` 組字；`instances` 有兩個以上時列出各實例。
- 健康檢查設定：顯示出廠值與範圍、留空送 null；前端沒有任何門檻常數（grep 測試或審查）。
- 通道設定：密碼留空不送、按清除送空字串；`editable` 為否時整頁唯讀；測試信 422 顯示階段。
- 稽核頁：篩選組出正確 query；詳細顯示差異；沒有 `clientIp` 時不顯示該欄。
- 任務規則分頁：`autoPause` 有值時顯示標籤、不顯示錯誤訊息文字，有編輯權才有恢復；確認框帶略過次數與該觸發方式的補救說明；`lastPause` 有值時顯示「上次暫停」。
- 鈴鐺：兩個新種類的標籤，標題由 `DataJson` 依語系組字、沒有 `DataJson` 時退回 `TitleSnapshot`；四語鍵一致（`locales.vitest.spec.ts`）；`bun run typecheck`。

**手動驗收**（對應 §9 的 O2 驗收；`scripts/unattended/local-demo.sh`，SMTP 用本機的郵件收件測試工具，例如以 Docker 起 Mailpit；SQLite 與 PostgreSQL 各走一次）

| 編號 | §9 驗收項 | 操作 | 預期 |
|---|---|---|---|
| M01 | SMTP 測試信 | 告警通道填本機 SMTP、收件信箱，按寄測試信 | 收件工具收到一封；稽核有 `ops.channel.test` |
| M02 | 資料庫唯讀 | SQLite：連線字串加 `Pooling=False` 啟動，啟動後把資料庫檔與目錄改成唯讀；PostgreSQL：`ALTER DATABASE ... SET default_transaction_read_only = on` 並中斷站台連線 | 1 分鐘內監控頁 `database.writable` 嚴重、頁首提示只在記憶體；收件工具收到告警信；一般使用者存檔看到「資料庫目前無法寫入」；改回可寫後事件寫進事件表並結案、站內通知補發 |
| M03 | 塞滿測試磁碟配額 | 建一個 200 MB 的磁碟映像（macOS `hdiutil`、Linux loop device），附件根目錄指過去，用 `dd` 塞到剩 15%，再塞到剩 5% | 監控頁先警告再嚴重；站內通知與告警信各兩次（開、升級）；清掉檔案後結案 |
| M04 | 必定失敗的任務規則 | 部署設定 `Ops:FaultInjection:TaskRuleKeys` 指定一條規則，存檔 5 次 | 前 4 次錯誤提示帶錯誤代碼；第 5 次後監控頁兩件事件（產品缺陷、暫停）、站內通知、告警信（只有產品缺陷那件）；存檔不再失敗、規則分頁顯示已暫停 |
| M05 | 恢復後重新觸發（`onCreate` 規則） | 拿掉故障注入、重啟、按恢復，新增一筆記錄、再存一次暫停期間新增的記錄；再把故障注入加回、恢復後存 5 次 | 第一段：新記錄產生任務、暫停期間的記錄仍沒有（不補產）、暫停事件結案、規則列顯示「上次暫停」；第二段：再次暫停、事件標回歸 |
| M08 | 資料庫空間滿（只 SQLite；PostgreSQL 的 `53100` 由 C11、C12 自動測試涵蓋） | 把 SQLite 資料庫檔放在 M03 的磁碟映像上（`Pooling=False`），塞到 0 | 存檔看到「資料庫目前無法寫入」；監控頁 `database.writable` 嚴重（訊息代碼 `full`）、收件工具收到直送信；清掉檔案後補寫、結案 |
| M06 | 稽核：改權限與登入失敗 | 改一個模組的權限；另一個瀏覽器用錯密碼登入 | 稽核頁各一列，詳細看得到權限差異與失敗原因 |
| M07 | 四種寬度 | 390、820、1366、1920 走查監控頁四個分頁、稽核頁、任務規則分頁的暫停標示 | 不橫捲；表格在手機變卡片；詳細在手機是全螢幕 |

**PostgreSQL**：上表雙跑欄 ✓ 的在 gate 的 PostgreSQL 步驟跑；手動驗收 M02、M04、M05 在 PostgreSQL 再走一次（`FORMWRIGHT_TEST_DB` 不適用於本機站台，照 pg0 §8 第 5 項手動起容器）。O2 新增的雙跑類別預估 9–12 個，另有 8 個並行案例（B01、B03、C10、C15、D08、D11、E05、E15）在無鎖模式再跑，gate 的 PostgreSQL 步驟增加約 50–100 秒（pg0c 實測一個 WebAPI 類別約 0.4 秒／測試；⚠️ 未實測）。

**手冊**：新增「維運監控」「稽核紀錄」「告警通道」三節；任務規則章節補「連續失敗會自動暫停、怎麼恢復、暫停期間不補產任務」；常見問題補「資料庫目前無法寫入」。

### 17.9 可寫死清單增補（併入 §12）

**資料完整性**

- 稽核只能新增；保留天數清理是唯一的刪除路徑（§12 原有，O2 落實成「repository 沒有修改與單筆刪除」）。
- 一個指紋一列；健康檢查類、通道類、暫停類事件不能手動結案。
- 規則失敗計數寫在業務交易之外；寫不進去不改變回給使用者的錯誤。
- 只計歸屬規則本身的例外：取消、並行衝突、凍結欄位、資料庫忙碌／唯讀／空間滿／連不上不計（`RuleFailureClassifier`，與例外處理器鏈一致）。
- 暫停只清 `PausedUtc`，不動 `Active`。
- 事件轉換、站內通知、寄送佇列在同一個交易。
- 寄 SMTP 不在任何交易裡；還在重試中的佇列列是至少一次交付（可能重複）；重試耗盡、略過、併入摘要、記憶體遺失不在此限（§17.2.6）。
- 健康檢查結果以 `TransitionId` 唯一鍵去重，結果列、事件轉換、通知、寄送佇列同一交易；補寫的舊結果只進歷史，不倒退目前投影。
- 規則失敗與成功以評估時間排序，不以補記時間；成功的 reset 只往後推；有在途失敗時成功一定留下 reset（含首次建列）。
- 寄送額度、領取、摘要化在同一把通道列鎖內依序執行；摘要化只動未領取的 `pending`。
- 多實例的看門狗型檢查以在線實例中最糟者為整體狀態，單一實例正常不能結案。
- 待補寫的健康檢查結果依原本順序逐筆補寫，不合併成最後一筆。

**安全**

- 監控、稽核、通道 API 只給系統管理員；設計者只看得到自己能看的規則的暫停狀態。
- 稽核摘要不自動序列化請求；`password`、`token`、`secret` 鍵與 `fwp_`、`fwc_` 開頭的值一律遮蔽。
- SMTP 密碼以 Data Protection 加密存放，API 不回明文。
- 測試信只寄到已設定的收件信箱。
- 開 Demo 登入時：通道設定與測試信停用、稽核不回來源 IP。
- 告警信、站內通知（以及 O3 的事件推送）只放標題代碼與白名單參數，不放正規化訊息、原始訊息、stack、記錄值；正規化訊息另經遮蔽，只在監控頁顯示。
- SMTP：信任邊界是系統管理員，產品不限制目的位址；憑證一律驗證、不提供略過；STARTTLS 失敗不退回明文；加密方式 `none` 時不准填帳號密碼；錯誤只回階段與 SMTP 回應碼。
- 資料庫連不上時，需要登入的 API（含監控頁）一律 503，不開免驗證的診斷入口。
- 稽核的 `ActorOverride` 只在環境沒有身分時可用；唯一例外是兩個登入 handler 的 `AuditOrigin.BeginSignIn` 範圍，行為者只來自 handler 剛驗證的帳號，不從請求帶的 cookie 取。`BeginSignIn` 執行期驗直接呼叫者，其他呼叫者（含反射）一律丟例外。
- Demo 一鍵登入每來源 15 分鐘 30 次總嘗試量（成功也算），在查資料庫、寫稽核之前；與 `LoginThrottle` 其他門檻同屬登入防濫用守衛（§17.11）。
- `WriteNowAsync`、`TryWriteNowAsync` 在建立新 scope 前檢查呼叫端交易，交易內呼叫一律丟例外。
- 故障注入只在 `Development` 環境生效。
- `database.writable` 不能關。
- 通道失敗事件不寄信（防迴圈）。
- 收件人解析為空時改送全部系統管理員。

**技術限制**

- 稽核摘要 16,000 字元；稽核頁每頁 100 列、查詢區間最長 366 天。
- 事件標題參數 2000 字元、`NormalizedMessage` 200 字、站內通知 `TitleSnapshot` 200 字；每件事件樣本 20 筆、原始訊息 500 字、stack 4000 字；條件式更新重試 3 次。
- 收件匣每 5 秒 flush、最多 1000 個指紋；待補寫佇列最多 500 筆。
- 看門狗每 60 秒（只是輪詢頻率，每個檢查的間隔可調）；單一檢查最長 60 秒；立即檢查同一檢查 60 秒一次。
- 多實例彙總：實例三個檢查間隔沒有回報就視為下線、退出彙總（只決定下線實例何時退出，在線實例的告警不受影響）。
- 告警信重試 1、5、15、60 分鐘、最多 5 次（寄送機制的參數，只決定通道失敗事件最晚約 81 分鐘後開啟，不改變哪些事件要告警）；寄送領取期限 2 分鐘；測試信與單封寄送逾時 30 秒、測試信同一管理員 30 秒一次；摘要信最多列 50 則；直送佇列最多 20 封（記憶體上限，資料庫壞時通常只有一兩個檢查在變）。
- 初稿列在這裡的「寫入探測連續 3 次忙碌」「`security.loginFailures` 窗口 15 分鐘」「排程落後三個間隔」會改變告警結果，已改成可設定（`database.writable` 的警告門檻、`ops.loginFailureWindowMinutes`、`ops.schedulerLagIntervals`，§17.6）。
- 清理每批 5000 列。
- 規則的在途失敗紀錄 5 分鐘過期（只防補記沒跑完時殘留；正常幾秒內就移除）。
- 摘要信每行 70 字、開頭與結尾各預留 200 字、最多 50 則；一般信內文的標題 500 字、主旨 200 字（`Body` 4000 字的預算，§17.2.6）。
- 健康檢查的門檻、間隔的上下限（§17.6.2）。
- 已知限制：狀態板、收件匣、待補寫佇列、直送佇列、直送去重都只在單一實例記憶體；多實例時監控頁只看得到自己那台的記憶體狀態，同一故障每台各直送一封（雲端版每客戶一套單一實例時沒有影響；§15 第 8 條同一類）。
- 已知限制：資料庫連不上且啟動後從沒讀到通道設定時，告警信寄不出去。
- 已知限制：規則的在途失敗只在本實例記憶體；失敗與成功分在兩台、成功剛好落在失敗的補記空檔時，會多算一次失敗（§17.2.7）。
- 已知限制：收件匣 flush 提交結果不明而重送時，事件次數多算一次（不影響通知，§17.2.3）。

### 17.10 主控判斷（待使用者確認）

#### 17.10.1 使用者行為類

每條：決定、理由、選錯的代價，最後一行是可以不看設計直接回答的題目（推薦答案放第一個）。依設計審查：U3、U4、U5、U7、U9、U10、U11 的題目已改寫，U6 補共用主檔權限、U7 與 U11 的決定本身已更正；U12–U14 由技術類 T14、T20、T22 改列（會影響使用者）。依第二輪審查：U4、U7 拆成可獨立作答的小題（U4a–U4c、U7a–U7b）；U12 改成依 C3 裁決的說明、不再表決；U14 改推薦「通道設定加信件語系」並寫進設計；U2、U5、U10、U13 的題目或理由修正。

**O2-U1 告警信寄到通道設定裡的一組信箱，不是寄給使用者**

- 決定：SMTP 設定頁填 1–20 個收件信箱（例如 IT 共用信箱）；站內通知照收件人政策發給使用者。測試信只寄這組信箱。
- 理由：使用者帳號沒有 Email 欄位；替帳號加 Email 會牽動使用者管理、匯入與個資，還會變成業務 email（U9 不做）的入口。地端 IT 多半用共用信箱收告警。
- 選錯代價：要改寄給個人，要加帳號欄位與一段畫面（約一個小段）；收件信箱清單可以保留當補充。
- 題目：資料庫掛了要寄告警信時，信要寄給誰？(1) 在告警通道設定填一組信箱，例如 `it@客戶.com`、`boss@客戶.com`（推薦）(2) 替每位使用者加 Email 欄位，寄給收件人政策裡的那些人（例如全部系統管理員）。

**O2-U2 出廠只寄基礎設施、安全、產品缺陷三類，客戶設定類只發站內**

- 決定：`ops.alertEmailCategories` 出廠 `infra,security,defect`，最低嚴重度「警告」。
- 理由：客戶設定類（規則失敗、暫停）的處理者是設計者，站內通知已送到最後修改者；寄到 IT 信箱多半沒人能處理。站內通知要收件人登入才看得到，不保證及時；任務漏產需要及時發現的客戶，系統管理員隨時可在政策頁把「哪些類別的事件寄告警信」（`ops.alertEmailCategories`）加上「客戶設定」，不必等新版本。
- 選錯代價：改政策即可，當下生效。
- 題目：任務規則「出貨後建立追蹤任務」因連續出錯被自動暫停；暫停期間出貨單照常存檔，但都不會產生追蹤任務，直到有人修好規則按「恢復」。除了站內通知最後修改它的設計者與系統管理員，要不要也寄信到 IT 共用信箱？(1) 出廠不寄，只站內；IT 多半不會改規則。需要及時知道的客戶，系統管理員可在政策頁把「客戶設定」勾進寄信類別，之後這類事件也寄信（推薦）(2) 出廠就寄信，讓不常登入的人也知道任務正在漏產。

**O2-U3 監控頁、事件、樣本、稽核只給系統管理員；設計者只在規則頁看到暫停狀態**

- 決定：見 §17.2.10。監控頁（健康、事件、正規化訊息、樣本原文與 stack）與稽核頁（含登入失敗與來源 IP）只給系統管理員；設計者收到的暫停通知連到模組設計頁的任務規則分頁，只看得到錯誤代碼、暫停時間與略過次數，看不到錯誤訊息文字與樣本原文。
- 理由：樣本原文可能含記錄內容（C2 把原文留在產品內，但不等於所有人都能看）；稽核含登入失敗與 IP。
- 選錯代價：之後要開給設計者，要加「只看自己模組的事件」的篩選與權限，約一個小段。
- 題目：系統裡有兩個維運畫面：「維運監控」列出所有錯誤事件，點開看得到錯誤原文與程式 stack（可能夾帶別人記錄裡的客戶名稱）；「稽核紀錄」列出誰在何時改了權限、誰登入失敗、從哪個 IP。設計者小王（不是系統管理員）負責的任務規則被自動暫停了。這兩個畫面與錯誤原文要開放給誰？(1) 兩個畫面只給系統管理員；小王只在自己的規則頁看到「已暫停、錯誤代碼 NullReferenceException、10:32 暫停、略過 3 次存檔」，看不到錯誤原文（推薦）(2) 小王也能開維運監控，但只看得到自己模組的事件，錯誤原文照樣看得到；稽核仍只給系統管理員 (3) 所有設計者都能看兩個畫面。

**O2-U4 事件狀態：開啟→確認→結案；部分事件不能手動結案；不能刪除（三個小題 U4a–U4c）**

- 決定：見 §17.2.2 狀態機。健康檢查、通道失敗、規則暫停三類只能由系統自己結案；嚴重度升級時已確認的退回「開啟」並再通知；事件不能刪，只隨保留天數清除。
- 理由：檢查還沒恢復就手動結案，下一分鐘會以「回歸」重開，製造假回歸；升級代表情況變糟，認領的人要再被叫一次；事件是稽核的一部分，刪掉就查不到。
- 選錯代價：允許手動結案只是拿掉一個檢查；允許刪除要另加稽核與權限；升級不再通知只改一個條件。
- U4a 題目：維運監控裡有一類事件是系統持續偵測的（磁碟剩餘空間、資料庫能不能寫、告警信寄不出去、任務規則被自動暫停）。以「磁碟剩餘空間不足」為例，系統管理員能不能自己按「結案」？(1) 不能，只能等系統偵測到恢復後自動結案，畫面上不出現結案按鈕（推薦）(2) 能；但下一輪檢查（出廠 5 分鐘一次）若空間仍不足，事件會重新打開並標「回歸」。
- U4b 題目：維運監控裡的事件（例如「伺服器錯誤」「磁碟剩餘空間不足」）能不能刪除？(1) 不能刪；已結案的事件保留 180 天（可在政策頁調整）後自動清掉，這段期間都查得到（推薦）(2) 系統管理員可以刪除事件，刪除這個動作本身記一筆稽核。
- U4c 題目：「磁碟剩餘空間不足」事件已有 IT 按了「確認」（表示有人在處理），之後磁碟從剩 15% 再掉到剩 5%，嚴重度由「警告」升為「嚴重」。要不要再通知一次？(1) 要：事件退回「開啟」並再通知一次，因為情況變糟了（推薦）(2) 不要：只更新數字與嚴重度，維持「已確認」、不再通知。

**O2-U5 前端錯誤只記錄不通知，伺服器 500 通知管理員**

- 決定：前端錯誤事件嚴重度「資訊」，只出現在監控頁；伺服器 500 是「警告」，新開時通知 `ops.alertRecipients` 並寄信。
- 理由：前端錯誤常來自瀏覽器外掛與網路，量大雜訊多；500 幾乎都是真的產品問題。
- 選錯代價：要通知前端錯誤只改一個出廠嚴重度。前端無法可靠分辨哪個錯誤擋住了操作，純前端的錯誤（例如表單打不開）不會有對應的伺服器事件，只能靠監控頁的次數與影響頁面發現；要分辨「擋住操作」得另做前端埋點（💡）。
- 題目：使用者瀏覽器裡發生的 JavaScript 錯誤，有的看不出影響，有的會擋住操作（例如按「存檔」沒反應、表單打不開）；系統分辨不出是哪一種，而且不少來自瀏覽器外掛或網路不穩。若同時有伺服器錯誤（例如存檔時伺服器出錯），那一件會照常通知。這類前端錯誤要不要通知管理員？(1) 不通知也不寄信，只記在維運監控的事件清單，同一個錯誤反覆出現會累加次數與影響的頁面（推薦）(2) 每個新出現的前端錯誤都發站內通知並寄信。

**O2-U6 能編輯規則的人就能按「恢復」**

- 決定：恢復權限＝改那條任務規則的權限（該模組的設計器編輯權；規則掛在共用主檔上時，還要有共用主檔的設計權，與 `UpdateTaskRuleRequest` 相同）；系統管理員本來就有。
- 理由：修規則的人就是要恢復的人；只給管理員會讓設計者修好後還要找人。
- 選錯代價：改成只限管理員只換一個權限宣告。
- 題目：設計者小王修好了被暫停的規則，誰可以按「恢復」？(1) 能修改這條規則的人，例如小王自己（他有這個模組的設計編輯權；規則若掛在「客戶」這類共用主檔上，還要有共用主檔的設計權，跟修改規則的權限一樣）（推薦）(2) 只有系統管理員。

**O2-U7 暫停期間不補產任務；存檔的人看不到提示（兩個小題 U7a、U7b）**

- 決定：暫停期間存檔照常成功、不產生任務、存檔的人看不到任何提示；規則頁顯示暫停起訖與「暫停期間略過 N 次存檔」，恢復時不自動補產。要補時由設計者照觸發方式手動補（§17.2.7 的表）：新增時觸發的規則，用建立時間篩出那段期間的記錄，在記錄的任務區手動新增任務；「條件成立時」與「欄位變成某值」的規則，恢復後那些記錄不會自己再觸發，要手動新增任務、或讓條件先變回不成立再成立；手動套用的規則恢復後直接套用即可。
- 理由：補產要重算每筆記錄在當時的條件，`once` 規則還要判斷是否已觸發過，複雜而且可能產出過時的任務；存檔的一般使用者不擁有規則、也修不了，提示只會造成困擾。
- 選錯代價：要提示存檔者時在存檔回應加一個旗標、前端顯示一行提示（小，不改表）；要自動補產照下面的不改表做法（約一個小段，o2d 與 o2e 各一部分）。兩題都不改表，o2d 開工前答即可。
- **自動補產的不改表做法**（U7b 選 (2) 時照這個做）：
  - 動作：恢復確認框多一個「恢復並補產」；恢復與補產是兩個交易型請求，補產分批（每批 500 筆記錄，技術限制），每批寫一筆稽核 `taskRule.backfill`（補了幾筆、略過幾筆）。
  - 對象：用 `Dynamic_RuleHealth` 已有的暫停起點（`LastPausedUtc`）與恢復時間，換成牆上時間後比記錄的 `CreatedTime`、`ModifiedTime`：`onCreate` 取暫停期間建立的記錄；`onUpdateMatch`、`onFieldBecomes` 取暫停期間修改過的記錄；`manual` 不補（直接套用即可）。
  - 判斷：用**補產當下**的記錄值與修好後的規則條件判斷是否成立，再排除這條規則對這筆記錄已有觸發紀錄（`Dynamic_TaskRuleRun`）的；`everyTime` 規則另排除還有未完成任務的。產生走既有的 `TaskRuleApplier.GenerateAsync`，產出的任務掛規則，和正常觸發的一樣。
  - 為什麼不用改表：對象從記錄既有的時間欄位推得，條件用現況，不需要另存「略過了哪些記錄、當時的條件」。代價是語意是「依現況補」而不是「重播當時」：暫停期間符合、之後又不符合的記錄不會補；`onUpdateMatch`、`onFieldBecomes` 分不出當時是不是「由不符合變符合」，暫停期間只改了備註、但本來就符合且從沒觸發過的記錄也會補。確認框與手冊寫明這兩點。
- U7a 題目：任務規則「新客戶建立後指派業務回訪」因連續出錯被系統自動暫停了 2 小時。暫停期間業務新增客戶，存檔都成功，只是不會產生回訪任務。存檔的業務要不要看到提示？(1) 不提示：存檔照常成功、看不到任何訊息；暫停狀態與「略過 8 次存檔」只顯示在設計者的規則頁（推薦）(2) 提示：暫停期間每次存檔都顯示一行「規則『新客戶回訪』已暫停，這次沒有產生任務」。
- U7b 題目：任務規則「新客戶建立後指派業務回訪」因連續出錯被系統自動暫停了 2 小時，期間業務新增了 8 筆客戶，都沒有產生回訪任務。設計者修好規則按「恢復」之後，這條規則不會再替那 8 筆產生任務（它只在新增那一刻觸發）。那 8 筆怎麼辦？(1) 不自動補：規則頁顯示「暫停 10:00–12:00、略過 8 次存檔」，設計者依建立時間篩出那 8 筆，在記錄的任務區手動新增回訪任務（推薦）(2) 恢復時可以選「恢復並補產」：系統找出暫停期間新增或修改過的記錄，依現在的資料與修好的規則判斷，替還沒有任務的記錄補產；暫停期間符合、現在已不符合的記錄不會補。

**O2-U8 資料庫唯讀時，存檔看到「資料庫目前無法寫入」而不是「系統發生錯誤」**

- 決定：唯讀與空間滿回 503 加四語訊息「資料庫目前無法寫入，請聯絡系統管理員」，不算產品缺陷；資料庫連不上同樣回 503，訊息「資料庫目前無法連線，請聯絡系統管理員」（§17.2.9）。
- 理由：使用者知道不是自己操作錯、也不是隨機錯誤；同一原因不會在事件表灌出一堆 500。
- 選錯代價：訊息文字可改；改回 500 只要拿掉一個例外處理器。
- 題目：資料庫磁碟滿了，業務按存檔時要看到哪個訊息？(1)「資料庫目前無法寫入，請聯絡系統管理員」（推薦）(2) 照現在的「系統發生錯誤，請稍後再試（錯誤代碼：…）」。

**O2-U9 稽核只記成功的管理與設計變更、登入成功與失敗**

- 決定：被權限擋下或驗證失敗的畫面操作不記；畫面的讀取不記（MCP 的讀取由 mcp1 逐筆記，U10）。設計類只記「誰在什麼時候改了哪個模組的什麼」，不存整份定義的前後差異；權限、群組、使用者、政策存前後差異。
- 理由：被擋的操作沒有改到東西，log 已有；畫面讀取量太大；設計定義差異量大，記錄歷程與套件匯出另有來源。
- 選錯代價：要記被擋的操作不只是改 `AuditBehavior` 的條件：權限守衛（`SystemAdminBehavior`、設計器權限檢查）在 `AuditBehavior` 外層，被擋的請求根本走不到它，要在各守衛另寫一筆交易外的「被拒」稽核並加上限，約一個小段；要存設計差異會讓稽核表大幾倍，並要定差異的儲存上限。
- 題目：稽核紀錄要記到多細？兩個例子：(a) 一般使用者想改權限，被系統擋下（他不是管理員）；(b) 設計者把「客戶」模組的「信用額度」欄位從必填改成選填。(1) (a) 不記，只記真的改成功的；(b) 記「小王 10:32 改了客戶模組的欄位設定」，不存改前改後的整份定義；只有權限、群組、使用者、政策的改動存改前改後的值（推薦）(2) 同 (1)，但 (a) 也記一筆「被拒」(3) 同 (1)，但 (b) 也存設計改前改後的完整差異。

**O2-U10 公開 Demo 站停用告警通道設定、稽核不顯示 IP**

- 決定：開 Demo 登入時，告警通道整頁唯讀、測試信回 403，稽核頁與稽核 API 不顯示來源 IP（正式環境照常顯示給系統管理員）。
- 理由：Demo 站人人是管理員；能設 SMTP 主機就能讓伺服器替他連任意位址或寄信；訪客彼此看得到 IP 是個資外洩。
- 選錯代價：Demo 上看不到告警通道怎麼用，靠手冊截圖。
- 題目：公開 Demo 站上每位訪客登入後都是系統管理員，彼此看得到對方的操作紀錄。兩件事：(a) 訪客能不能自己填 SMTP 主機並寄測試信？(b) 稽核紀錄要不要顯示其他訪客的來源 IP？(1) 都不行：Demo 站的告警通道頁唯讀、測試信停用；稽核紀錄不顯示 IP（推薦）(2) 同 (1)，但告警通道頁改成顯示一份示範用的假設定（主機、信箱都是範例，存不了、也不能寄），讓訪客看得到欄位長什麼樣。讓訪客自己填 SMTP 主機不列為選項：那等於讓任何人借伺服器連任意位址或寄信，是安全邊界（§17.9），不是展示取捨。

**O2-U11 系統管理員權限異動開安全事件並寄信**

- 決定：新增或改使用者時「系統管理員」旗標有變動，開或更新一件安全事件（警告），**每次異動**都通知 `ops.alertRecipients` 並寄信（信件受 `ops.alertEmailMaxPerHour` 節流，超過併入摘要），有人按過「確認」也照樣通知；同一個指紋，多次異動累加次數；誰給了誰不存在事件上，每次異動的樣本指到那一筆稽核，通知也連到那一筆稽核（§17.2.2）。
- 理由：§5.1 把「管理員權限異動」列為安全類；帳號被盜後第一件事通常是加管理員。初稿「同一指紋只在新開時通知」會讓第二次以後的異動被靜音，正好漏掉最該看到的情況。
- 選錯代價：嫌吵可把安全類從 Email 類別政策拿掉，或改成只記稽核；改回「只通知第一次」只改一個條件。
- 題目：管理員 A 週一把業務 B 設成系統管理員，IT 收到通知後按了「確認」；週三 A 又把 C 設成系統管理員。每一次異動都要通知全部系統管理員並寄信到 IT 信箱嗎？(1) 每次都通知與寄信（寄信每小時有上限，超過會併成一封摘要），通知點進去看得到是誰把誰設成管理員；監控頁合成一件「系統管理員權限異動」事件、累加次數（推薦）(2) 只有第一次通知，之後只累加次數，直到這件事件結案後再出現才通知 (3) 不通知，只記在稽核。

**O2-U12 規則失控的時間窗用滑動窗口（依 C3 裁決，不需表決；原技術類 O2-T14）**

- 決定：每次失敗時往回看 `ops.rulePauseWindowMinutes`（出廠 60 分鐘），這段時間內、最近一次成功之後的失敗達 `ops.rulePauseConsecutiveFailures`（出廠 5 次）就暫停（§17.2.7）。
- 理由：C3 使用者裁決的字面是「60 分鐘內連續 5 次」，任何 60 分鐘內連續失敗 5 次都要暫停。初稿為了少一個表，改成「從第一次失敗起算、過了窗口就重來」，會漏掉跨過窗口邊界的密集失敗，等於限縮了裁決；審查指出後改回。代價是多一張只存失敗時間的小表（`Dynamic_RuleFailure`）。
- 狀態：依 C3 使用者裁決（2026-11-12）照做，**不是待答題**；固定起點的寫法違反裁決，不列為選項。列在這裡只因為它會影響使用者，給使用者看懂門檻怎麼算。
- 說明例：規則「出貨後建立追蹤任務」的門檻是「60 分鐘內連續失敗 5 次（中間沒有成功）就自動暫停」。它在 10:00 失敗一次，之後 10:59、11:01、11:02、11:03、11:04 各失敗一次，中間都沒成功過。11:04 那一次往回看 60 分鐘（10:04–11:04）已有 5 次失敗，所以暫停。

**O2-U13 O2 先做的資料庫健康檢查，其餘延到 O4（原技術類 O2-T20）**

- 決定：O2 做資料庫可不可寫、磁碟剩餘、排程器卡住、SQLite 的 WAL 檔大小、每日快速完整性檢查（SQLite `quick_check`、`foreign_key_check`，兩種資料庫的共用主檔檢查）、登入失敗次數、備份新鮮度（回「未設定」）；延到 O4 的是 PostgreSQL 的連線數與鎖等待、慢查詢、`amcheck` 索引檢查，與 SQLite 的每週完整檢查、慢查詢計時（§17.1、§8）。
- 理由：O2 先做驗收要的；延後的幾項是 O4 的 L1 診斷要讀的統計，隨 mcpO1 一起做最省；PostgreSQL 的慢查詢與 `amcheck` 要客戶啟用擴充套件，地端安裝文件也要同時寫。
- 選錯代價：O2 期間 PostgreSQL 的連線快用完、鎖等太久、查詢變慢時，監控頁不會提示，只能等使用者反應變慢或 503；要提前做連線數與鎖等待約半個小段（不需擴充套件），全部提前約一個小段。
- 題目：O2 上線後，監控頁會檢查資料庫能不能寫、磁碟剩多少、排程器有沒有卡住、登入失敗次數，以及資料完整性：SQLite 檢查資料庫檔本身有沒有損壞；PostgreSQL 在 O2 只檢查產品層的資料（例如共用主檔的關聯是否一致），不檢查資料庫檔案與索引本身有沒有損壞。另外幾項要等到下一段 O4（廠商遠端診斷）才做：PostgreSQL 連線數快用完（例如上限 100、已用 95）、查詢在等鎖、慢查詢排行、PostgreSQL 的索引損壞檢查。O2 期間發生這些狀況，監控頁不會提示。這樣分可以嗎？(1) 照這樣分，延後的隨 O4 一起做（推薦）(2) 連線數與鎖等待也在 O2 做，慢查詢與索引檢查仍延到 O4 (3) 全部在 O2 做。

**O2-U14 告警信的語系由通道設定決定（原技術類 O2-T22 的信件部分）**

- 決定：告警通道設定加一欄「信件語系」`MailLocale`（四語之一；留空＝出廠，O2 是繁中，p3 有「系統預設語系」後改成跟它）。告警信、摘要信、測試信寫入佇列或寄出時以這個語系組好（§17.2.6、§17.4）。畫面與站內通知照使用者語系組字，不受影響（標題存代碼＋參數，§17.2.5）。
- 理由：信件寄給共用信箱，沒有「收件人語系」可用；p3 在 O2 之後，O2 也沒有系統層的語系。客戶 IT 不一定讀繁中（越南、泰國、英語系客戶），固定繁中等於 p3 前這些客戶的告警看不懂、告警失效。多一欄的成本小（o2a 一欄、通道表單一個下拉）；p3 後留空的自動跟系統預設語系，不必再設。
- 選錯代價：改選固定繁中，o2a 少一欄、非繁中客戶 p3 前收看不懂的信；改選四語並列，信很長、摘要信的長度預算要減半。本推薦已寫進設計，改選其他兩項會拿掉 o2a 的一欄，必須在 o2a 開工前答。
- 題目：資料庫掛了，系統寄告警信到客戶 IT 的共用信箱。目前系統還沒有「系統預設語系」這個設定（排在 O2 之後的 p3 才做），信件不是寄給特定使用者，無從知道收件人用什麼語言。例如客戶 IT 是越南籍、介面用越南文。O2 的告警信用什麼語言？(1) 告警通道設定加一個「信件語系」，由客戶的系統管理員選（例如選越南文）；沒選就先用繁體中文，p3 上線後改跟系統預設語系（推薦）(2) 先固定繁體中文，p3 上線後改用系統預設語系 (3) 四種語言（繁中、英、泰、越）並列在同一封信。

#### 17.10.2 技術類（主控裁決）

| # | 決定 | 理由與選錯代價 | 寫在 |
|---|---|---|---|
| O2-T1 | 九張表一組 migration，在 o2a 一次建好；子單元只是 commit 切法，整段一次合併 | backend 規則一段最多一組 migration；代價是 o2a 要一次定案所有欄位 | §17.1、§17.4 |
| O2-T2 | 稽核用 `IAuditedRequest`＋`AuditBehavior`（交易內）＋handler 補差異；反射測試保證覆蓋；動作目錄 `AuditActions` 靜態類別 | 宣告式不會漏、差異仍由懂資料的 handler 給；代價是約 40 個請求檔要加宣告 | §17.2.1 |
| O2-T3 | 登入稽核另開 scope 寫、失敗不擋登入；節流擋下的不逐筆記，只計指標 | 匿名者能觸發的寫入要有上限；代價是被節流的嘗試只在 log 與指標 | §17.2.1 |
| O2-T4 | 一個指紋一列、全表唯一索引，結案後重開同一列 | §5.5 本來就要重開原事件；不用部分索引，兩種資料庫一致 | §17.2.2、§17.4 |
| O2-T5 | 500 與前端錯誤走程序內收件匣，每 5 秒聚合寫入 | 請求路徑不多一次寫入；代價是當掉時最多丟 5 秒的計數（log 仍在） | §17.2.3 |
| O2-T6 | 健康檢查分看門狗（每實例、不經租約）與 `ops.monitor`（經租約） | 唯讀時排程器搶不到租約；代價是多一個 hosted service | §17.2.4 |
| O2-T7 | 唯讀與空間滿用寫入探測判斷，`IDatabaseErrors` 加 `IsReadOnly`、`IsStorageFull`、`IsUnavailable` | 兩種資料庫一份寫法、也涵蓋空間滿；連不上也歸 503，不被當成產品缺陷；代價是每分鐘一筆小寫入 | §17.2.4、§17.2.9 |
| O2-T8 | 寫不進資料庫時狀態板、待補寫佇列、直送佇列留在記憶體；待補寫只在狀態改變時新增一筆、帶穩定識別碼、恢復後依序逐筆補寫；直送由看門狗的寄送迴圈執行，不經排程器租約 | 資料庫壞時仍要告警，故障歷程不能被壓成最後一筆；`ops.monitor` 在資料庫不可寫時搶不到租約；代價是多實例時只在本機、可能每台各寄一封（已知限制） | §17.2.4、§17.2.6 |
| O2-T9 | SMTP 用 MailKit（MIT 授權），不用 `System.Net.Mail.SmtpClient` | 官方文件標 `SmtpClient` 不建議新開發、不支援 465 的直接 TLS；代價是多一個套件（版本在計畫鎖定） | §17.2.6 |
| O2-T10 | SMTP 密碼用 Data Protection（purpose `ops-alert-smtp`） | 沿用既有金鑰；代價是金鑰遺失要重填 | §17.2.6 |
| O2-T11 | Email 經寄送佇列表＋`ops.monitor`，每封信「短交易領取（`sending`＋領取者＋期限）→ 交易外寄 SMTP → 短交易記結果」，至少一次；重試 1、5、15、60 分、最多 5 次 | 事件與信同交易、可重試、可節流；寄信不持全庫寫入鎖；代價是最快約 1 分 30 秒才寄出，當掉時可能重複寄一封 | §17.2.6、§17.7 |
| O2-T12 | 通道失敗開 `channel.smtp` 事件、只站內 | 防自我觸發迴圈 | §17.2.6 |
| O2-T13 | 規則失敗計數在交易外的 `RuleFailureBehavior`、成功歸零在 `TaskRuleStage` 交易內；狀態放新表 `Dynamic_RuleHealth`、失敗時間放 `Dynamic_RuleFailure`，不改 `Dynamic_TaskRule`；只計 `RuleFailureClassifier` 判為歸屬規則的例外 | 照 §5.4；不讀不寫業務表以外的熱點；一次資料庫故障不會把所有規則暫停；代價是每次存檔多一個小查詢（只在模組有任務規則時） | §17.2.7 |
| O2-T14 | （改列行為類 O2-U12：滑動窗口） | 會影響使用者、且須照 C3 字面 | §17.10.1 |
| O2-T15 | `ruleEditor` 是收件人政策的動態項目，由 `PolicyDefinition.DynamicRecipients` 宣告哪些政策允許 | 其他收件人政策不會誤收這個項目 | §17.2.5 |
| O2-T16 | 故障注入 `Ops:FaultInjection:TaskRuleKeys` 只在 Development 生效 | 任務規則難以自然失敗，驗收需要；代價是正式程式多一個空介面 | §17.2.7 |
| O2-T17 | 事件與檢查的已知錯誤碼在回傳處補代碼並記 `RuleFailureNote` | 客戶設定類事件才有歸屬與指紋；代價是改八處回傳 | §17.2.2 |
| O2-T18 | 稽核動作分組由伺服器展開成代碼清單查詢 | 不靠 `LIKE`，兩種資料庫一致 | §17.2.1、§17.7 |
| O2-T19 | 健康檢查數值與門檻一律 `long`，比例用萬分比 | §10.7 不用浮點數 | §17.2.4 |
| O2-T20 | （改列行為類 O2-U13：哪些健康檢查延到 O4） | 延後期間監控頁看不到這些狀況，會影響使用者 | §17.10.1 |
| O2-T21 | OpenTelemetry 不在 O2 | O2 仍沒有匯出端（§10.2.3 的理由不變） | §17.1 |
| O2-T22 | 事件與站內通知的標題存代碼＋白名單參數（事件 `TitleKey`＋`TitleParamsJson`；通知 `DataJson`，`TitleSnapshot` 只當退路），畫面與鈴鐺依語系組字；告警信寫入佇列時組成文字快照（語系改列行為類 O2-U14） | 一個契約；信件寄出時事件可能已變或被清，快照才穩定 | §17.2.2、§17.2.5、§17.2.6 |
| O2-T23 | 監控頁的記憶體狀態只代表本實例 | 雲端版單一實例；多實例時另議 | §17.9 |
| O2-T24 | 來源管道、權杖 id、行為者種類用 AsyncLocal 的 `AuditOrigin`，比照 `ActingScope` | 不必每個 handler 傳參數；mcp1 只在 `/mcp` 開一個範圍 | §17.2.1 |
| O2-T25 | 稽核寫入分 `Add`（同交易）、`WriteNowAsync`（交易外、失敗丟例外）、`TryWriteNowAsync`（交易外、失敗只記 log）；`AuditEntry` 含參數雜湊、回傳筆數、確認 id 與結果；`ActorOverride` 只在沒有身分時可用 | 三種失敗語意各有呼叫者（業務、MCP fail closed、登入），一個吞錯的方法滿足不了 MCP；登入時請求可能帶著別的帳號或已失效的 cookie，所以登入另開 `AuditOrigin.BeginSignIn` 範圍、只限兩個登入 handler；交易守衛在建 scope 前檢查；代價是多兩個方法、一個範圍與一個守衛 | §17.2.1、MCP §8.1 |
| O2-T26 | 看門狗型檢查依實例彙總：在線實例最糟者為整體狀態，三個間隔沒回報的實例退出彙總；彙總交易先更新檢查列取得列鎖 | 各實例磁碟與連線不同，健康實例的 `ok` 不能結掉別台的故障；不另開表，從結果表讀；代價是每次結果多一次小查詢 | §17.2.4、§17.7 |
| O2-T27 | 外送內容（站內通知、信、O3 推送）只用標題代碼＋白名單參數，正規化訊息另經 `SensitiveTextMask`、只在監控頁 | 正規化不等於遮蔽，未加引號的姓名、業務文字會留下；代價是標題比較制式 | §17.2.2 |
| O2-T28 | SMTP 不限目的位址（信任邊界是系統管理員）、憑證必驗、STARTTLS 不退回明文、無加密時不准帳密 | 內網 SMTP 要能用；限制位址擋不住已是最高權限的管理員；代價是自簽憑證的客戶要裝 CA | §17.2.6 |
| O2-T29 | 健康結果每筆帶 `TransitionId`，結果表唯一索引當持久化處理標記，與事件轉換、通知、佇列同交易；歷史結果與目前投影分開，彙總以該筆檢查時間為基準 | 提交結果不明時重播不重複；晚到的舊結果不倒退總覽；代價是結果表多一欄一個唯一索引、檢查列多一個鎖欄 | §17.2.4、§17.4、§17.7 |
| O2-T30 | 規則失敗與成功以評估時間排序；成功只在有連續失敗或本實例有在途失敗時寫 reset，且只往後推、必要時首次建列 | 失敗在回滾後才補記，用補記時間會把成功前的失敗算進新一串；代價是一個記憶體登記表，跨實例的空檔仍可能多算一次（已知限制） | §17.2.7、§17.4 |
| O2-T31 | 寄送額度、領取、摘要化、摘要信掛列都在通道列鎖（`LastClaimUtc`）之後；摘要化只動未領取的 `pending` | 無鎖模式下也不超額、不把正在寄的信併進摘要；代價是同一通道的領取依序執行（量很小） | §17.2.6、§17.7 |

#### 17.10.3 待決衝突

和既有使用者裁決（§3、§16、標「使用者裁決」者）比對：初稿有一處衝突，已依裁決修正；其餘沒有衝突。

- **已修正**：初稿 O2-T14 的「從第一次失敗起算、過窗口就重來」不符 C3「60 分鐘內連續 5 次」的字面（第 0、59、61、62、63、64 分鐘失敗不會暫停），是以技術判斷限縮裁決。設計審查指出後改成滑動窗口（O2-U12、§17.2.7），不需要使用者重新裁決。

比對過、需要說明的四點：

- §3 第 8 條「連續失敗、互相觸發迴圈」：迴圈依 C3 延到 mcp2a，O2 照做，不是新的限縮。
- C3 寫「只暫停任務規則」：本節把手動套用（manual）的任務規則也算進去，與裁決字面一致；初步曾考慮把 manual 排除，那會是限縮裁決，已放棄。
- C2「原文只留在產品內」：產品內的監控頁讓系統管理員看樣本原文，符合「廠商要看就由人登入該實例」；告警信雖然是產品送出的，也只放標題代碼與白名單參數（比裁決更保守，不衝突）。
- C2 選項 1 寫「只給正規化後的內容」：審查指出現有正規化（`NormalizeMessage`）不會移除未加引號的姓名與業務文字，照字面送正規化訊息仍可能把記錄值送出客戶環境，違背 C2 選它的理由。O2 的外送內容因此只用標題代碼＋白名單參數（§17.2.2）；O3 的事件推送與 mcpO1 的唯讀工具也應照這個契約，比 C2 字面更嚴、方向一致，不另開裁決，O3 細部設計再確認一次。

#### 17.10.4 同步修改的前文（不是使用者裁決，都是主控文字）

- §8：「下表的 PRAGMA 與 `pg_*` 檢查是 O2 的東西」改成 O2 先做的項目＋其餘延到 O4（原 O2-T20，審查後改列 O2-U13）。
- §9：O2 列加細部設計連結；O2 驗收改寫：資料庫唯讀時站內通知寫不進資料庫，改成「恢復可寫後補發」；「恢復後重新觸發」拆成「修好後恢復會再產生任務」與「沒修好恢復會再暫停」兩件；加 SQLite 與 PostgreSQL 各走一次、加稽核頁與通道設定的寬度走查。
- §11：`ops.clientErrorSampleRetentionDays` 改名、`ops.alertRecipients` 只管站內、新增五項；表後指向 §17.6。
- §12：表後加一句指向 §17.9。
- 頁首狀態：加「O2 細部設計（§17）」。
- MCP 設計 §8.1：稽核寫入介面改指向 §17.2.1 的三個寫法與 `AuditEntry` 欄位，唯讀工具的 fail closed 用 `WriteNowAsync`。

**2026-11-19 依設計審查修訂**（必修 10 條與建議清單；逐條位置）：

| # | 問題 | 改在哪 |
|---|---|---|
| 1 | 固定起點窗口不符 C3 | §17.2.7 時間窗與 `RecordRuleFailureRequest`、§17.4 `Dynamic_RuleHealth`／新表 `Dynamic_RuleFailure`、O2-T14 改列 O2-U12、§17.8 E04／E04b／E14、§17.10.3 |
| 2 | 基礎設施故障被算成規則失控 | §17.2.7 `RuleFailureClassifier`、§17.9 資料完整性、§17.8 E13 |
| 3 | 斷線時監控頁打不開 | §17.2.9（新增 `IsUnavailable` 與 503 `databaseUnavailable`、連不上一段改寫）、§17.2.10、§17.2.11、§17.3、§17.5、§17.8 C11／C12 |
| 4 | 故障歷程被覆蓋、直送沒有執行者 | §17.2.4 第 3 步（待補寫佇列）、§17.2.5、§17.2.6 直送（看門狗寄送迴圈）、§17.2.9、O2-T8、§17.8 C09／C13 |
| 5 | 多實例互相錯誤結案 | §17.2.4「多實例的彙總」與第 2 步、§17.4 `Dynamic_HealthCheck`、§17.5 `instances`、§17.7、O2-T26、§17.8 C10 |
| 6 | 寄送佇列沒有占用狀態 | §17.2.6 寄送三步、§17.4 `Dynamic_AlertOutbox`、§17.7、O2-T11、§17.8 D08 |
| 7 | 稽核介面不足以承載 MCP | §17.2.1 寫入介面與登入、MCP §8.1、O2-T24／T25、§17.8 A04／A09／A10 |
| 8 | 正規化不等於遮蔽 | §17.2.2「標題與外送內容」、§17.2.5、§17.2.6、§17.2.7（設計者看不到訊息文字）、§17.4 `Dynamic_OpsIncident`、§17.5、§17.9、O2-T22／T27、§17.8 B09 |
| 9 | 管理員異動被去重靜音 | §17.2.2 安全類每次通知、§17.4 樣本 `AuditLogId`、O2-U11、§17.8 B08 |
| 10 | 恢復後的補救說明不成立 | §17.2.7 各觸發方式的表、§17.3、§17.4 `LastPausedUtc`、§17.5 `lastPause`、O2-U7、§17.8 E06／E06b／E06c、M05、§9 O2 驗收 |
| 建議 | 政策分類 | §17.2.4、§17.6.1 新增兩項政策、§17.6.2、§17.9、§11 |
| 建議 | SMTP 安全邊界 | §17.2.6「正式環境的安全邊界」、§17.5 `smtpPlaintextAuth`、§17.9、O2-T28、§17.8 D10 |
| 建議 | 驗收補強 | §17.8 C11–C13、M08、「無鎖」標示、§17.7 |
| 建議 | 通知標題契約與摘要信 | §17.2.5、§17.2.6 摘要信、§17.2.8 清理、O2-T22、§17.8 D09 |
| 建議 | T14／T20／T22 改列行為類 | O2-U12–U14 |

**2026-11-19 依第二輪設計審查修訂**（必修 6 條與建議；逐條位置）：

| # | 問題 | 改在哪 |
|---|---|---|
| R2-1 | 補寫沒有持久化去重 | §17.2.4 第 1–3 步（`TransitionId` 第 1 步產生、彙總交易的去重、已嘗試的項目不再合併）、§17.2.3（收件匣不另做去重的理由）、§17.4 `Dynamic_HealthCheckResult.TransitionId` 唯一、`Dynamic_AlertOutbox.TransitionId` 說明、§17.7、O2-T29、§17.8 C14、§17.9 |
| R2-2 | 歷史補寫覆蓋健康總覽 | §17.2.4「多實例的彙總」與第 2 步（歷史結果、截至 R 的彙總、目前投影只往後推）、§17.4 `Dynamic_HealthCheck` 的 `LastAggregatedUtc`／`LastCheckedUtc`、索引 `(Code, InstanceId, CheckedUtc)`、§17.7、O2-T29、§17.8 C15、§17.9 |
| R2-3 | 失敗與成功的順序、reset 協定 | §17.2.7 計數（成功的 reset 條件）、「順序與 reset 協定」、`RecordRuleFailureRequest` 第 1–5 步、§17.4 `Dynamic_RuleHealth`／`Dynamic_RuleFailure` 欄位說明、§17.7、O2-T30、§17.8 E15／E16、§17.9（在途失敗過期、跨實例已知限制） |
| R2-4 | 摘要化沒有原子條件 | §17.2.6 領取（通道列鎖、額度與領取同交易）、節流（只動未領取的 `pending`）、摘要信掛列、§17.4 `Dynamic_AlertChannel.LastClaimUtc`、§17.7、O2-T31、§17.8 D11、§17.9 |
| R2-5 | 登入的 `ActorOverride` 假設不成立 | §17.2.1 `ActorOverride`、登入的稽核身分情境（`AuditOrigin.BeginSignIn`）、登入寫入點、O2-T25、§17.8 A10／A11、§17.9 安全 |
| R2-6 | 裁決與開工門檻不一致 | O2-U12 改成依 C3 的說明、§17.10.5 改寫（刪「無」）、O2-U7b 寫出不改表的自動補產做法、§17.1 o2a 範圍（新增欄位）、O2-U14 推薦寫進設計 |
| 守衛 | 稽核交易內呼叫守衛的位置 | §17.2.1（建 scope 前檢查、`TryWriteNowAsync` 也丟）、§17.8 A09、§17.9 |
| U 意見 | U2、U4、U5、U7、U10、U12、U13、U14 | O2-U2（強調政策可開 Email）、U4a–U4c、U5（刪無證據理由）、U7a–U7b、U10（不把任意 SMTP 當選項）、U12、U13（PostgreSQL 只做產品層）、U14（推薦改通道語系；§17.2.6、§17.3、§17.4 `MailLocale`、§17.5、§17.8 D13） |
| 建議 | 摘要信與 `Body` 的長度預算 | §17.2.6 摘要信長度預算與一般信截斷、§17.4 `Body` 說明、§17.8 D12、§17.9 技術限制 |
| 建議 | E13 改例外矩陣 | §17.2.7 `RuleFailureClassifier` 的判準、§17.8 E13 與「E13 例外矩陣」 |
| 建議 | 「至少一次」的範圍 | §17.2.6 寄送（限定為重試中的佇列列，列出重試耗盡、略過、併入摘要、記憶體遺失、收件端）、§17.9 資料完整性 |

#### 17.10.5 需要使用者先定

**o2a 開工前必答**（非推薦選項會改表，o2a 之後不能再改表）：

- O2-U1：選「寄給個人」要替 `AppUser` 加 Email 欄位，牽動使用者管理與匯入，也等於開了業務 email 的入口（U9）；仍可在 O2 內加一個小段完成。
- O2-U14：推薦的「通道設定加信件語系」已寫進設計；改選固定繁中或四語並列要拿掉 `Dynamic_AlertChannel.MailLocale`。

**對應子單元開工前答**（任何選項都不改表）：O2-U2、U3、U4a–U4c、U5、U6、U7a、U7b、U8、U9、U10、U11、U13。O2-U7b 的自動補產有不改表的做法（O2-U7），o2d 開工前答即可。O2-U12 依 C3 裁決，不需回答。

第二輪審查新增的去重與鎖欄位（`Dynamic_HealthCheckResult.TransitionId`、`Dynamic_HealthCheck.LastAggregatedUtc`、`Dynamic_AlertChannel.LastClaimUtc`）是技術類（O2-T29、O2-T31），不需使用者回答，但必須在 o2a 一起建（§17.1）。

#### 17.10.6 未驗證

- ⚠️ SQLite 資料庫檔改成唯讀後，WAL 模式下能不能繼續讀（`-shm` 要可寫；讀不到時唯讀期間監控頁也打不開，§17.2.9 的「唯讀時監控頁可用」要改寫）；o2b 與下一條一起實測。（o2b 已實測，見 §17.12）
- ⚠️ `IsUnavailable` 的對照：SQLite 的 `SQLITE_IOERR` 在磁碟滿時會不會先於 `SQLITE_FULL` 出現；Npgsql 在 socket 斷線、主機拒絕連線時丟的例外是否都不帶 SQLSTATE（o2a 以 C11 的建構例外先寫，o2b 用真的斷線各試一次）。（o2b 已實測，見 §17.12）
- ⚠️ MailKit 強制 STARTTLS 的選項（`SecureSocketOptions.StartTls`）在伺服器不支援時是否一定丟例外、不退回明文（o2c 對 Mailpit 關掉 STARTTLS 實測，D10）。（o2c 已實測，見 §17.13）
- ⚠️ SQLite 唯讀的重現：`Pooling=False` 加改檔案權限，新連線是否一律 `SQLITE_READONLY`、站台在唯讀資料庫上能否繼續回應讀取（o2b 第一步實測，不成立就改用 DI 換寫入探測的連線做手動驗收）。（o2b 已實測，見 §17.12）
- ⚠️ PostgreSQL `default_transaction_read_only` 的測試寫法：設定後清 Npgsql 連線池，新連線是否立即生效、測試結束如何還原樣板資料庫（o2b 實測）。（o2b 已實測，見 §17.12）
- ⚠️ MailKit 的版本與 .NET 8 相容、授權仍是 MIT（o2c 計畫時查 NuGet 與官方 repo 並鎖版本）。（o2c 已實測，見 §17.13）
- ⚠️ `DriveInfo` 在 macOS 磁碟映像、Linux loop device、Docker overlay 回報的剩餘空間是否就是附件目錄實際可用的空間（o2b 實測）。（o2b 已實測，見 §17.12）
- ⚠️ `PRAGMA quick_check` 在大資料庫的耗時（60 秒逾時夠不夠）；Demo 資料量下實測一次。
- ⚠️ gate 的 PostgreSQL 步驟增加的時間（§17.8 粗估 50–100 秒）。
- ⚠️ Render 免費方案擋 SMTP 埠（U1 已列未查證）：對 O2 沒影響，Demo 站本來就停用通道設定。
- ⚠️ EF Core 在 SQLite 與 PostgreSQL 上把 `ExecuteUpdate` 裡的「取較晚者」（`LastFailureUtc`、`StreakResetUtc`）與 `CASE` 翻成可用的 SQL（SQLite 的 `max()`、PostgreSQL 的 `GREATEST`），以及 SQLite 上 `DateTime` 存成 TEXT 時比較大小的結果正確（o2a 寫 `Dynamic_RuleHealth` repository 時先試）。
- ⚠️ 「提交成功後丟例外」的測試注入方式（`DbTransactionInterceptor.TransactionCommitted` 拋例外）在兩種 provider 都會讓呼叫端收到例外、而資料已提交（C14；o2b 先試）。（o2b 已實測，見 §17.12）
- ⚠️ 數值溢位在既有寫入流程是否已轉成 400（E13 矩陣最後一列；o2d 計畫時核對）。
- 本節的檔案與行號（`EventStage.cs:67-68`、`CheckEvaluator.cs:40`、`EffectApplier.cs`、`TaskRuleStage.cs`、`ManualTaskRules.cs`、`LoginHandler.cs`、`UpdateSiteThemeHandler.cs`、`SessionMiddleware.cs`、`ClaimsCurrentUser.cs`、`AuthController.cs`、`DatabaseErrors.cs`、`Errors/*ExceptionHandler.cs`）是 2026-11-19 讀 `772e7a54`（第二輪讀 `fd71171a`）的結果，計畫階段再核對一次。

### 17.11 o2a 實作補記（2026-11-19）

範圍照 §17.1 o2a：九張表、稽核寫入與查詢、`IDatabaseErrors` 三個分類、稽核保留清理。健康檢查邏輯、事件記錄器、SMTP、自動暫停、前端頁面都不在這一段。

**表與 migration**

- 九張表照 §17.4 建（`OpsEventCenter`，兩個 provider 各一份），欄位名、可空、長度由 `OpsSchemaTests` 逐欄比對；列舉一律存 camelCase 文字（`CamelEnum`），`*Utc` 欄位讀回是 UTC（PostgreSQL 27 個 `timestamptz`）。與 §17.4 沒有欄位差異。
- `AlertChannel` 照 O2-U14 加 `MailLocale`；收件人照 O2-U1 放在通道上，`AppUser` 不動。
- ⚠️ `scripts/db/add-migration.sh` 最後一行的 echo 在 zsh／bash 的 `set -u` 下報 `name�: unbound variable`：`$name` 後面緊接全形「」」被當成變數名的一部分。migration 已正常產生，只是結尾訊息壞掉；main 已另行修正（`52599f47`）。

**稽核寫入（§17.2.1）**

- 寫入點：`IAuditedRequest`（73 個請求，宣告動作代碼與目標；`AuditActions` 共 77 個代碼：管理與設計 74、登入 2、清理 1）由 `AuditBehavior` 在交易內、handler 成功後寫一列，與變更同交易提交；稽核寫不進去整筆回滾（A02）。登入（`LoginHandler`、`DemoLoginHandler`）與排程工作不經 behavior，用 `IAuditLog.TryWriteNowAsync`／`Add`。
- **偏離 1：巢狀的稽核請求只記最外層。** 開應用程式、整併主檔、整合重整這類流程會在一個請求裡再送幾十個設計請求；`AuditContext.Depth` 讓只有最外層寫一列（例如只記 `app.open`，不另記內層每個 `field.add`）。Demo 資料產生器本身不是稽核請求，所以它送的每個設計請求各記一列（Demo 資料的稽核時間本來就是模擬的）。
- **偏離 2：權限變更的摘要只放有變的格子，最多 200 格**，另帶 `changeCount`，超過 200 格時加 `truncated: true`（`PermissionAuditCells`）。格子的鍵是 `group:{id}.{動作}`、`group:{id}.scope`、`user:{id}.{動作}`、`field:{欄位}/group:{群組}`、`option:{id}`、`fieldGrant:{id}`。
- **偏離 3：新建類的目標用請求帶的 key 或名稱**（建模組用 `Input.Key`，建群組、儀表板用名稱當 `TargetLabel`）；需要新產生的 id 時由 handler 呼叫 `AuditContext.Target(id)` 補上（建使用者）。兩種匯入（應用程式套件、範本）在驗證通過後補套件或範本的代碼與繁中名稱（複審修正：原本在驗證前補，本文缺名稱時 `Name` 為 null，400 變成 500；範本 `{}`、套件 v1 `{"formatVersion":1}` 兩個負例都回 400、不留稽核）。只在模組內唯一的代碼，目標帶模組：列印範本是 `模組代碼:範本代碼`，模組政策是 `模組代碼:政策代碼`（整合同理是 `應用程式:整合代碼`）；事件、檢查、轉單、任務規則、簽核流程的代碼全域唯一，直接用代碼。
- 登入失敗的 `ReasonCode`：`unknownAccount`、`inactive`、`locked`、`noPassword`、`wrongPassword`；Demo 登入另有 `notDemoAccount`。帳號不存在時目標留空，不記輸入的帳號字串（A03）。被節流擋下的嘗試不寫稽核，只加計數器 `formwright.login.throttled`。Demo 一鍵登入與密碼登入共用同一個 `LoginThrottle`（同一組門檻與計數），公開 Demo 站的匿名者不能靠不存在的帳號無限新增稽核列；門檻是既有的安全守衛常數，沒有進 PolicyCatalog。
- **複審修正：Demo 登入另有每來源總嘗試量。** 只限失敗擋不住「逐次登入公開帳號」——Demo 成功不需要秘密，每次都新增一列 `login.succeeded`。`LoginThrottle.TryDemoAttempt` 在查資料庫、寫稽核之前，對同一來源 15 分鐘內放行的 Demo 登入計數（成功、失敗都算），滿 30 次就回既有的登入失敗訊息、只加 `formwright.login.throttled`、不寫稽核；密碼登入不走這道。門檻寫死、列進 §17.9：它和 `LoginThrottle` 其他門檻一樣是防匿名者灌寫入的守衛，只在公開 Demo 站有作用，不是客戶要依業務調的政策；放進 PolicyCatalog 反而讓守衛能被調鬆。30 次對單一訪客切換示範帳號綽綽有餘；同一 NAT 後面多人同時試用會互相擋（與既有 IP 門檻同一類限制）。
- 登入失敗與沒有身分的寫入記 `ActorKind = system`、`ActorUserId = null`。
- 行為者覆寫（`ActorOverride`）只在 `AuditOrigin.BeginSignIn()` 範圍內允許；範圍內不取環境身分（舊 cookie），範圍外有身分又覆寫就丟 `InvalidOperationException`。`BeginSignIn` 的呼叫者只有兩個登入 handler，由 `AuditArchitectureTests`（WebAPITests）掃五個產品組件的 IL 把關：`call`、`callvirt`、`newobj`、`ldftn`、`ldvirtftn` 都算，方法群組轉委派也抓得到，狀態機與 lambda 類別歸到最外層型別，白名單是型別全名；測試組件裡放了三個故意違規的替身，證明掃描器抓得到（A10）。
- **複審修正：反射繞得過 IL 掃描，改由 `BeginSignIn` 執行期驗呼叫者。** `BeginSignIn` 標 `NoInlining`，用 `new StackFrame(1)` 取直接呼叫者，歸到最外層型別後必須是 Application 組件裡的 `LoginHandler` 或 `DemoLoginHandler`（名單是常值，沒有可被反射改掉的欄位），否則丟 `InvalidOperationException`。正式環境也開：一次登入多一個堆疊框架（微秒級），相對 PBKDF2 可忽略。Debug 與 Release 組態都跑過。
  - 擋得住：`MethodInfo.Invoke`（直接呼叫者是反射框架）、`CreateDelegate` 或方法群組轉成委派再呼叫（呼叫者是轉手的型別）、其他組件裡同名的型別、測試碼直接呼叫。測試替身 `SneakyReflection` 以拼接字串反射呼叫，IL 掃描看不到它，執行期檢查擋下（`AuditArchitectureTests`）。
  - 擋不住：反射直接改 `AuditOrigin` 的私有 `AsyncLocal`／`Frame`（不經 `BeginSignIn`）；兩個 handler 自己呼叫的輔助方法以尾呼叫轉進 `BeginSignIn`（堆疊上看起來是 handler 本身）。兩者都要改到產品程式碼，由審查把關；IL 掃描仍保留，抓一般的直接引用。
  - ApplicationTests 的 `AuditLogTests` 因此開不了登入範圍，改成驗證測試碼呼叫會丟例外；範圍內不取 cookie 身分的行為由 WebAPITests 的 A11 經真的登入驗證。
- `WriteNowAsync`／`TryWriteNowAsync` 在交易內一律丟 `InvalidOperationException`，守衛在建立新 scope 之前（A09，以計數的 scope factory 驗證）。
- 遮蔽：鍵名剛好是 `password`、`token`、`secret`（不分大小寫）的值，與 `fwp_`、`fwc_` 開頭的字串值都換成 `***`。超過 16000 字元時只留有變的鍵並標 `truncated`，仍超過就從最大的項目開始丟。
- 覆蓋率：`AuditCoverageTests` 用反射找出所有交易型請求中屬於 `ISystemAdminRequest`、或 `IDesignerRequest` 且需要的設計權限不是唯讀的，每個都要實作 `IAuditedRequest`，例外清單目前是空的；反向也檢查：實作 `IAuditedRequest` 的（含 struct）都要是交易型、回 `ServiceResult`（A05）。巢狀成功、內層失敗、外層失敗、外層不是稽核請求四種情況由 `AuditBehaviorTests` 驗證；400、403 不留稽核。

**查詢 API（§17.5）**

- `GET system/ops/audit`、`system/ops/audit/{id}`、`system/ops/audit/actions`，只有系統管理員。
- **偏離 4：repository 多一個 `GetAsync(id)`**（詳細頁用），§17.5 只列了分頁查詢與清理。
- **偏離 5：時間範圍兩端都含。** 預設迄時是「現在」，剛寫入的列（同一個時間點）要查得到；§17.5 沒寫端點含不含。日期早於 1970-01-01 回欄位錯誤 400（免得預設起日往前推 7 天溢位）。錯誤訊息登記在 `MessageText`（四語，範圍與筆數上限是參數），依 `X-Locale` 回應。
- 動作與動作分組同時給時取交集；分組展開成動作清單查，不用 LIKE。開 Demo 登入時列表與詳細都不回 `clientIp`。

**清理（§17.6、§17.9）**

- 新工作 `ops.upkeep`，間隔政策 `ops.upkeepMinutes`（出廠 60，5–1440 分鐘），保留天數政策 `ops.auditRetentionDays`（出廠 365，90–3650 天）；兩個政策加進 `PolicyCatalog`、`shared/policy-catalog.json` 與四個語系。截止時間用排程器的時鐘（與租約同一個時間來源）。
- 每批最多 5000 列（`OpsUpkeepOptions`，技術限制），每批一個交易型請求、帶租約檢查；租約丟了就停。
- **偏離 6：`ops.retention.purge` 每批一列、與該批刪除同交易提交**，摘要 `{"deleted":{"auditLog":n}}`；後面的批次失敗、取消或丟了租約，已提交的批次仍有紀錄。沒東西可刪就不寫，避免每小時一列空紀錄。沒有另寫整體摘要列（各批的列加總就是整次的筆數）。

**`IDatabaseErrors`（§17.2.9、§17.2.10）**

- `IsReadOnly`：SQLite 8、PostgreSQL `25006`。`IsStorageFull`：SQLite 13、PostgreSQL `53100`。`IsUnavailable`：SQLite 14、10，PostgreSQL 類別 `08` 與 `57P01`–`57P03`，以及不帶 SQLSTATE、內層是 `SocketException`／`IOException` 的例外。連線池耗盡（內層 `TimeoutException`）仍歸忙碌。C11 用建構的例外驗證一個例外只歸一類；真的斷線時的例外形狀仍照 §17.10.6 待 o2b 實測。

**驗證（本機）**

- 只跑受影響的類別，每種資料庫各一次（全套交 CI）。第一輪（審查前）：ApplicationTests SQLite 103、PostgreSQL 103 過；WebAPITests SQLite 359 過 1 略過（4 分 23 秒）、PostgreSQL 360 過（6 分 53 秒）。
- 審查修正後（第二輪，範圍多了 `AuditBehaviorTests`、`MessageTextTests`、`LoginThrottleTests`、列印範本、範本匯入、語系）：ApplicationTests SQLite 115 過、PostgreSQL 115 過；WebAPITests SQLite 431 過 1 略過（4 分 29 秒）、PostgreSQL 432 過（8 分 2 秒）。
- DomainTests 的政策類別 68 過；前端 `policy-i18n` 與語系檔 41 過，`vue-tsc` 型別檢查過。

### 17.12 o2b 實作補記（2026-11-19）

範圍照 §17.1 o2b：健康檢查目錄與看門狗、`ops.monitor`、事件記錄器與收件匣、500／前端錯誤／系統管理員異動接事件、站內通知、唯讀／空間滿／連不上的 503、監控與事件 API、`ops.upkeep` 的事件與健康紀錄清理。SMTP、寄送佇列與直送（o2c），自動暫停、故障注入、`ruleEditor`（o2d），前端頁面（o2e）不在這一段。沒有新增 migration，九張表照 §17.4 用、沒有不夠的欄位。

**§17.10.6 實測結果**（2026-11-19，macOS、SQLite 3（Microsoft.Data.Sqlite 8）、PostgreSQL 16-alpine 容器）

- SQLite 唯讀重現（`Pooling=False`、WAL、資料庫檔 `chmod 444`）：新連線寫入一律錯誤碼 8（擴充碼 8），`SaveChanges` 包成 `DbUpdateException`；讀照常成功。例外：資料庫檔唯讀、`-wal`／`-shm` 不存在、所在目錄也唯讀（555）時，連 SELECT 都丟 8（擴充碼 1544 `SQLITE_READONLY_DIRECTORY`）。chmod 之前就開著的連線仍寫得進去，所以開連線池時改權限重現不了；`Mode=ReadOnly` 的連線寫入同樣是 8。結論：§17.2.9「唯讀時監控頁讀得到」成立，只有上述目錄也唯讀的組合例外（已在 §17.2.9 加主控文字）。
- PostgreSQL `ALTER DATABASE … SET default_transaction_read_only = on`：已在池裡的連線不受影響；`NpgsqlConnection.ClearAllPools()` 後新連線寫入得到 `25006`。`ExecuteUpdate` 直接丟 `PostgresException`，`SaveChanges` 是 `DbUpdateException` 包著它。還原要連到 `postgres` 資料庫下 `ALTER DATABASE … RESET`（或 SET 與 ALTER 分兩個命令），再清一次池。
- 真斷線的例外形狀（PostgreSQL）：拒絕連線是 `NpgsqlException`（無 SQLSTATE）內層 `SocketException`，經 EF 執行策略再包一層 `InvalidOperationException`（"transient"）；`docker stop` 與 `pg_terminate_backend` 是 `57P01`；`docker kill` 是 `NpgsqlException` 內層 `EndOfStreamException`（`IOException` 的子類）；**主機名稱解析失敗時 Npgsql 不包裝、直接丟裸的 `SocketException`（`HostNotFound`）**——原本的 `IsUnavailable` 抓不到，已補：由 PostgreSQL 連線邊界（EF 連線攔截器）包成 `DatabaseUnavailableException` 再歸連不上，只認 `HostNotFound`、`TryAgain`、`NoData`；分類器不接受裸 `SocketException`（審查修正 1）。密碼錯 `28P01`、資料庫不存在 `3D000` 不歸連不上（是設定錯，照未處理例外開缺陷事件）。
- 真的 SQLite 失敗：資料庫檔或目錄不存在是 14；連線開著時目錄被刪是 10（擴充碼 1802）。都在 `IsUnavailable` 內。
- `DriveInfo(目錄)`（macOS）：回報目錄所在的那顆磁碟（以 `hdiutil` 掛一個磁碟映像、目錄放在裡面，數字與 `df` 一致）；路徑必須存在，否則丟 `DriveNotFoundException`（`DriveInfoDiskSpace` 對不存在的目錄回 null、該目錄不列入）。Linux loop device 與 Docker overlay 沒有實測（⚠️ 未驗證）。
- 提交後丟例外的注入（`DbTransactionInterceptor.TransactionCommitted(Async)` 拋例外）：兩個 provider 都讓呼叫端收到原例外、資料已提交；之後 Dispose 與重用 DbContext 都正常。C14 用這個寫法。

**偏離設計之處**

- **偏離 1：自動測試的唯讀不用「DI 換 `Mode=ReadOnly` 連線」**，改用測試專用的連線攔截器（`ReadOnlySwitch`）：每次開連線時下 SQLite `PRAGMA query_only = 1／0`、PostgreSQL `SET SESSION CHARACTERISTICS AS TRANSACTION READ ONLY／READ WRITE`。理由：換連線字串只影響寫入探測，業務請求、補寫交易仍寫得進去，測不到「整個資料庫唯讀」；攔截器在連線池下每次開啟都切換，不殘留狀態，錯誤碼也是真的（8、`25006`）。C01 前置已改主控文字。手動驗收照上面 chmod（SQLite，需 `Pooling=False` 或重啟站台）與 `ALTER DATABASE`＋清池（PostgreSQL）。
- **偏離 2：`scheduler.jobs` 的工作清單在執行時從 scope 取**，不在建構時注入：`ops.monitor` 本身是工作、又依賴健康檢查執行器，建構注入會形成 DI 循環。
- **偏離 3：「立即檢查」在檢查列還沒補齊時**（站台剛啟動、看門狗第一輪還沒跑完）結果留在待補寫佇列，下一輪補齊列後寫入；回應照樣是本次結果（`fromMemory`）。看門狗啟動時第一輪就補列，正常操作碰不到。
- **偏離 4：事件的 `Code`**：未處理例外是例外型別短名（`InvalidOperationException`），前端錯誤是 `kind:name`（`vue:TypeError`），健康檢查是檢查代碼，安全是 `admin.changed`；§17.4 只寫了「錯誤碼」。
- **偏離 5：系統管理員異動的標題參數只有 `change`（`granted`／`revoked`）**，沒有次數與帳號：次數看事件的 `OccurrenceCount`，誰給了誰看樣本連到的稽核列（§17.2.2「不存在事件列上」）。
- **偏離 6：處理者標示**（`handler`）：客戶設定 `designer`、基礎設施 `it`（Cloud 版別 `vendor`）、安全 `admin`、產品缺陷 `contactVendor`（O2 還沒有回報管道）。
- **偏離 7：事件詳細的狀態歷程**取稽核中目標是這件事件（`incident`）的列，最多 100 筆（超過時 `historyTruncated` 為真），沒有另存歷程表。
- **偏離 8：PUT 健康檢查**以 JSON 是否有該鍵區分「不變」與「回出廠值（null）」；型別不對回 400。門檻先後用套用後的生效值判斷（只改一邊也會檢查另一邊的出廠值）。
- **偏離 9：事件清單的 `status`** 可用逗號給多個（`open,acknowledged`）。
- **偏離 10：`ops.upkeep` 的順序**是自動結案 → 嚴重事件重複提醒 → 稽核、健康紀錄、樣本、已結事件清理；每輪重複提醒最多處理一批（5000 件）。清理摘要的鍵是 `auditLog`、`healthResult`、`incidentSample`、`incident`，o2a 的 `{"deleted":{"auditLog":n}}` 不變。
- **偏離 11：部署設定 `Ops:Watchdog:Enabled`**（預設 true）只停看門狗，收件匣的 flush 不受影響（審查修正 3）；測試主機關掉看門狗、並拿掉收件匣的 hosted service（`TestApp`），測試直接呼叫 `OpsWatchdog.TickAsync`、`IncidentInboxDrain.FlushAsync`。看門狗每 60 秒一輪，收件匣每 5 秒 flush、關機時最後 flush 一次（技術限制）。
- **偏離 12：客戶設定類事件的收件人**在 o2d 前用 `ops.alertRecipients`（`ops.configIssueRecipients` 與 `ruleEditor` 隨 o2d）；總覽的告警通道區塊在 o2c 前一律「未設定」。
- o2c 的部分沒有做：C01、C09 的直送與佇列狀態、C13 整條、B08 的寄送佇列列、B09 的信件主旨與內文。對應測試只驗站內通知與事件那一半。

**已知限制**

- ⚠️ 兩個實例的彙總交易並行、且較晚檢查時間的那筆先提交時（C10 的「A 異常、B 正常同時寫入」），較早那筆提交後目前投影不會被更新（投影只往後推），事件仍正確開啟；投影要等下一筆結果才追上（後續檢查正常執行且寫入成功時，最多落後一個檢查間隔；資料庫寫不進去或檢查停掉時會更久）。C10 對這一步只驗事件狀態。
- `PRAGMA quick_check` 在大資料庫的耗時沒有實測（Demo 資料量下 C08 的完整性檢查在測試內完成，沒有個別計時）。

**驗證（本機）**

- 新增測試類別：`IncidentTests` 13 例、`HealthCheckTests` 14 例（都標 `Category=Postgres`，B01、B03、C10、C15 另含無鎖模式）、`DatabaseUnwritableTests` 1 例；`DatabaseErrorsTests` 補真斷線形狀。
- WebAPITests（`Ops` 命名空間全部＋`AdminUsersApiTests`、`EndpointRoutesTests`、`ConcurrencyExceptionTests`、`SecurityTests`、`HealthTests`）：SQLite 197 過 1 略過（23 秒）、PostgreSQL 198 過（25 秒）。
- ApplicationTests（稽核、`DatabaseErrors`、政策、訊息、架構、DI、通知）：SQLite 276 過（1 分 30 秒）、PostgreSQL 276 過（2 分 3 秒）；DomainTests 1446 過。
- 前端：`locales`、`policy-i18n`、`policy-types` 29 過，`no-hardcoded-colors` 22 過，`vue-tsc` 與 `quasar build` 過。

**審查修正**（2026-11-19，codex 跨家族審查後；每條先寫會失敗的測試看它紅，再修到綠）

- 修正 1：裸 `SocketException`（`HostNotFound`）沿整條內層例外都被當成資料庫連不上，外部服務的 DNS 失敗也回 503、不開缺陷事件。→ 新增 `DatabaseUnavailableException`（Application.Common）與 `PostgresConnectionFailureInterceptor`（EF 開連線失敗且是名稱解析錯誤時包成前者，掛在 `PostgresDatabase.Configure`），分類器改認前者、不再接受裸 `SocketException`。測試：`DatabaseErrorsTests.Postgres_SocketFailureWithoutSqlStateIsUnavailable_PoolExhaustionStaysBusy`（反例）、`Postgres_DatabaseHostNotFound_ThroughEf_IsUnavailable`（真 Npgsql＋EF 連 `.invalid` 主機，拿掉攔截器即紅）、`DatabaseUnwritableTests.NonDatabaseDnsFailure_Is500_OpensDefectIncident`。
- 修正 2：反序提交時 `LastSeenUtc` 保持較新的值，`LastVersion`、`LastRequestId`、標題參數卻被較舊的訊號蓋掉（更新、升級重開、回歸三條路徑）。→ 三個欄位與 `LastSeenUtc` 用同一個時間條件（`LastSeenUtc > 訊號時間` 就保留原值），次數照加。測試：`IncidentTests.B10_OlderSignalCommittedLater_KeepsLatestVersionRequestIdAndTitle`（雙跑）。
- 修正 3：`Ops:Watchdog:Enabled=false` 連帶停掉收件匣 flush，500／前端錯誤堆在記憶體直到滿或關機遺失。→ `IncidentInboxFlusher` 不看這個開關；`TestApp` 改為移除它的 hosted service。測試：`IncidentTests.B11_WatchdogDisabled_InboxStillFlushedPeriodicallyAndOnStop`。
- 修正 4：`DatabaseUnwritableTests` 缺 `Category=Postgres`，CI 的 PostgreSQL job 漏跑 `53100`／Npgsql 分支。→ 補標記，PostgreSQL 實跑 C12 過（503、`Retry-After: 60`、不開缺陷事件）。
- 修正 5：F01 的「保留未結事件」用 200 天前的 server 事件，upkeep 先把它自動結案，斷言只驗 Id 在 → 假綠。→ 改用健康檢查類事件（不自動結案）並斷言仍為 `open`；已結事件清理照舊驗。測試：`IncidentTests.F01_Upkeep_PurgesResolvedIncidentsSamplesAndHealthResults_KeepsOpen`（先加 `open` 斷言於原 server 事件即紅）。
- 修正 6：CI run 44467123758 的 C15（PostgreSQL）時間末位差 1 tick：PostgreSQL `timestamp` 只到微秒。產品程式碼中 SQL 內的先後比較兩邊都由 Npgsql 截到微秒，一致；但記憶體與讀回值比較的兩處會誤判同一筆——`OpsMonitorReader` 拿狀態板的檢查時間比投影（寫入成功後仍判成「記憶體較新」，總覽 `fromMemory` 一直為真），`IncidentRecorder` 拿訊號時間比 `LastSeenUtc`／`ResolvedUtc`。→ 新增 `StorageTime.ToMicroseconds`，執行器產生檢查時間、記錄器收到訊號時先截到微秒；C15 直接送觀測值，測試的時間也先截。測試：`HealthCheckTests.C16_SubMicrosecondCheckTime_PersistedResultNotTreatedAsNewerThanProjection`（PostgreSQL 必紅）、C15 雙模式。
- 建議 1、2：看門狗與立即檢查重疊時較早開始、較晚完成的結果蓋掉狀態板；立即檢查不知道背景正在跑。→ 狀態板依 `CheckedUtc` 只收較新的；執行器記每個檢查的執行中次數，立即檢查（`TryRunNowAsync`）在背景或另一個立即檢查跑同一項時回 `healthCheckBusy`。測試：`HealthCheckTests.C17_OverlappingRuns_RunNowBusy_LaterStartedResultKeptOnBoard`。
- 建議 3：事件詳細的歷程最多 100 筆沒有截斷提示。→ `IncidentDetail` 加 `historyTruncated`（§17.5 已同步，公開契約新增欄位）。測試：`IncidentTests.B12_IncidentDetail_HistoryOverMax_Truncated`。
- 建議 4：已知限制的「最多一個間隔」改為限定在後續檢查正常執行且寫入成功時（上方已知限制已改）。

### 17.13 o2c 實作補記（2026-11-19）

範圍照 §17.1 o2c：SMTP 通道設定與密碼加密、寄送佇列（領取 → 交易外寄 → 記結果）、重試與節流、摘要信、通道失敗事件、資料庫寫不進時的直送、通道 API 與測試信、`ops.upkeep` 的佇列清理。自動暫停、故障注入、`ruleEditor`（o2d），前端頁面（o2e）不在這一段；前端只補了三項新政策的四語名稱。沒有新增 migration，`Dynamic_AlertChannel`、`Dynamic_AlertOutbox` 照 §17.4 用、沒有不夠的欄位。

**§17.10.6 實測結果**（2026-11-19，macOS、MailKit 4.18.2）

- MailKit 強制 STARTTLS（`SecureSocketOptions.StartTls`）對不支援 STARTTLS 的伺服器：`ConnectAsync` 在 EHLO 之後直接丟 `NotSupportedException`，之後沒有送出 `AUTH`、`MAIL`、`RCPT`（伺服器端只收到 `EHLO`），不退回明文；寄送器把連線階段的這個例外歸 `tls`。沒有用 Mailpit，改用測試內的假 SMTP 伺服器（`NoStartTlsSmtpServer`，只聽本機埠、EHLO 不列 STARTTLS），D10 自動跑。另一個測試讓同一台假伺服器當 `none` 的內網 relay，確認 MailKit 真的送出 `MAIL FROM`、兩個 `RCPT TO` 與信件內容、沒有 `AUTH`。
- MailKit 版本與授權：鎖 4.18.2（MIT），有 `net8.0` 目標；相依 MimeKit 4.18.2（MIT）、BouncyCastle.Cryptography 2.7.0（MIT）、System.Formats.Asn1 與 System.Security.Cryptography.Pkcs 10.0.13（MIT，`net8.0` 可用）。5.0.0 太新沒有採用。
- 連線被拒（本機沒人聽的埠）：`ConnectAsync` 丟 `SocketException`，歸 `connect`；422 回應只有 `code`、`message`、`params.stage`／`serverCode`，不含例外訊息與主機位址（D07，真的 MailKit）。

**偏離設計之處**

- **偏離 1：摘要信的時機**：每個整點後的第一輪，若這個整點之後還沒有摘要信、而有「整點前被摘要化且還沒掛上摘要信」的 `digested` 列，就新增一封摘要信並把那些列掛上（同一把通道列鎖、同一交易）。整點後才被摘要化的列（不論何時建立）等下一個整點。摘要化時間記在 `NextAttemptUtc`（`digested` 不再領取，這個欄位不再有其他用途；不加欄位，審查修正）。摘要信不佔每小時額度；額度滿時只領摘要信。
- **偏離 2：寫入後才停用或清掉通道設定**：領取時發現通道不能寄，把還沒寄的 `pending` 全部改成 `skipped`（§17.2.6 只寫了寫入時標 `skipped`）。
- **偏離 3：密碼解不開（金鑰不見）時寄送**：不重試，直接標 `failed`，並以階段 `config` 開或更新通道失敗事件（§17.2.6 的階段只有 `connect`、`tls`、`auth`、`send`；解不開不是 SMTP 的錯，重試也解不開）。測試信在這個情況回 409 `smtpNotConfigured`（照 §17.5）；直送信只記 Error log、丟掉那封。
- **偏離 4：直送信的主旨沒有事件編號**（資料庫寫不進時還沒有事件），內文註明「由看門狗直接寄出」，連結（有設 `Ops:PublicUrl` 時）指向 `/system/ops?tab=health`。
- **偏離 5：直送的判斷**用本實例狀態板上一筆的狀態：從正常（rank 0）變異常是開啟、警告變嚴重是升級；其他轉換（含結案後再發生）不直送，等補寫後由佇列寄。直送佇列用自己快取的通道設定與寄信政策，每輪寄送前重讀、讀不到沿用上一份，啟動時政策是出廠值、通道設定從缺（§17.9 已知限制）。超過每小時上限的直送信丟掉並記 Warning；佇列滿（20 封）丟最舊的；同一檢查回到正常時撤掉它還沒寄出的信。已直送的識別碼保留 500 筆（與待補寫佇列同一個上限）。
- **偏離 6：補寫信是否過時**由執行器在補寫時判斷：同一檢查在待補寫佇列後面已經有「正常」，這筆的開啟或升級列記 `skipped`；直送已寄出的優先記 `sentDirect`。和直送的交接照「審查修正」的協定，不會兩邊都寄。
- **偏離 7：領取與記結果的租約檢查可省略**：`ClaimAlertRequest` 的租約條件可為 null，測試直接模擬兩個寄送者（D08、D11）；正確性靠條件式更新與通道列鎖，不靠租約（§17.7）。`ClaimedBy` 是「實例 id：本輪隨機 8 碼」（最長 100）。同一輪連續 3 次被別人領走就停，下一輪再來。
- **偏離 8：`ops.monitor` 寄送失敗不擋健康檢查**：寄送者丟租約以外的例外時記 Warning、本輪記部分失敗，接著照跑健康檢查。
- **偏離 9：寄送成功時一律嘗試把通道失敗事件結案**（沒有未結的就沒動作），所以每封成功多一次查詢。
- **偏離 10：測試信不是交易型請求**（寄 SMTP 不能在交易裡），稽核 `ops.channel.test` 在交易外直接寫：成功 `success`、失敗 `failed` 並以階段為 `reasonCode`，摘要記收件人數與回應碼；寫不進只記 log。順序：Demo 403 → 設定不完整（含密碼解不開）409 → 30 秒內重複 409 → 寄送 → 失敗 422。30 秒的節流只在本實例記憶體、以管理員為單位。
- **偏離 11：PUT 通道設定的驗證**：主機、寄件地址、收件信箱只在 `enabled` 為真時必填（停用時可以存不完整的設定，讓「先停用再慢慢填」可行）；格式、長度與「最多 20 個」不論啟用與否都驗。§17.5 錯誤表寫「收件信箱 0 個」是 400，實作只在啟用時才算。`none` 加帳號或（新填或保留中的）密碼回 400，回應多一個 `code: "smtpPlaintextAuth"`（`ServiceResult.Invalid` 加了帶代碼的多載，400 回應在有代碼時多一個 `code` 鍵；其他 400 不變）。稽核 `ops.channel.update` 的差異只記 `hasPassword`，換密碼時另記 `passwordChanged`，不記內容。
- **偏離 12：信件時間一律以 UTC 顯示**（`yyyy-MM-dd HH:mm:ss UTC`），不依通道語系換時區。
- **偏離 13：寄送佇列的清理天數**沿用 `ops.healthResultRetentionDays`（出廠 30 天），清理摘要鍵 `alertOutbox`；可清的是 `sent`、`sentDirect`、`failed`、`skipped`，以及已掛上摘要信、而摘要信已經不在 `pending`／`sending` 的 `digested`（D09）。
- **偏離 14：總覽的告警通道區塊**：`configured` 是主機、寄件地址、收件信箱都有（不看啟用），`enabled` 照通道列。
- **偏離 15：安全類事件同一件的後續異動**（B08 第二次給權）在佇列記 `occurred`；事件轉換對應：開啟 → `opened`、結案後再發生 → `regressed`、升級 → `escalated`、upkeep 重複提醒 → `renotify`。

**可寫死清單補充**（§17.9 已列的不再重複）：收件信箱最多 20 個；欄位長度照 §17.4（主機與地址 255、寄件名稱 100、收件信箱合計 2000）；已直送識別碼保留 500 筆；寄送者同一輪連續被別人領走 3 次就停；直送交接的寬限 2 分鐘（＝領取期限）、直送領著佇列列的期限 15 分鐘（審查修正）。

**已知限制**

- ⚠️ Linux 上 STARTTLS 與憑證驗證的行為沒有另外實測（MailKit 跨平台，預期相同）。

**驗證（本機）**

- 新增測試類別 `AlertChannelTests` 19 例（D01–D13、C13、通道停用與未設定、PUT 驗證、MailKit 對假 relay 實寄；標 `Category=Postgres`，D08、D11 另含無鎖模式）；`HealthCheckTests` 的 C01（直送佇列一封）、C09（`sentDirect`／`skipped` 與不重寄）與 `IncidentTests` 的 B08（兩列佇列 `opened`、`occurred`）、B09（佇列與寄出的主旨、內文不含敏感字）補上 o2c 的一半。
- WebAPITests（`Ops` 命名空間全部＋`AdminUsersApiTests`、`EndpointRoutesTests`、`ConcurrencyExceptionTests`、`SecurityTests`、`HealthTests`）：SQLite 222 過 1 略過（19 秒）、PostgreSQL 223 過（24 秒）。
- ApplicationTests（`Common` 全部含稽核覆蓋與訊息、`DatabaseErrorsTests`、`OpsSchemaTests`、政策、通知）：SQLite 231 過（3 秒）、PostgreSQL 231 過（6 秒）；DomainTests（含 `PolicyCatalogFixtureTests`）1446 過。
- 全套 SQLite 跑過一次（ApplicationTests 3630 過 14 略過；WebAPITests 1467 過 2 略過、2 失敗）：失敗的 `MesFlowTests.OverdueWorkOrderReport_…` 與 `AppCombinationTests.ClosingInventory_LeavesMesWritable` 各跑了近 2 分鐘（與 ApplicationTests 並行、機器滿載），單獨重跑 2 例都過（1 秒），與 o2c 無關。
- 前端：`locales`、`policy-i18n`、`policy-types`、`no-hardcoded-colors` 51 過，`vue-tsc` 與 `quasar build` 過。

#### 審查修正（2026-11-19，codex 跨家族審查）

**直送與補寫的交接協定**（必修 1、2 共用）：以 `TransitionId` 為單位，直送佇列每封信有「待寄／寄送中（SMTP 在途）／交接中／已寄」幾種狀態，寄 SMTP 時不持任何資料庫交易。

- 補寫在交易內向直送保留這個轉換（`DirectAlertQueue.Reserve`）：已寄的記 `sentDirect`；待寄的改成交接中、直送不再開始寄，佇列列照寫 `pending`（或過時的 `skipped`）；寄送中的，佇列列寫成由直送領著的 `sending`（`ClaimedBy = direct:<TransitionId>`、期限 15 分鐘），寄完由直送以短交易記 `sentDirect`，失敗交回保留時判定的狀態（`pending` 就由佇列重試）。
- 提交後確認交接（after-commit）；提交結果不明時交接中的信先不寄，等去重返回時確認，或過了 2 分鐘寬限查結果列：有就交出，沒有（交易沒提交）就收回照寄。
- 直送每次開始寄之前先批次查這些 `TransitionId` 的結果列是否已在資料庫（提交成功但回傳失敗時，事件與佇列列也已在同一交易提交），在的就不寄、交給佇列；查不到（資料庫讀不到）就照寄。
- 去重返回（`ResultExistsAsync` 為真）時核對佇列列：直送已寄出的，還在 `pending` 的列記 `sentDirect`；寄送中的，還在 `pending` 的列改由直送領著；待寄的不再寄。
- 每封寄出前在鎖內確認它還在佇列、沒有交接中，快照之後被撤回的不寄；「回到正常」撤回時在途的寄完就丟、失敗不重試。

**修正清單**

- 必修 1：直送從快照寄出已交給佇列或已撤回的信、在途信被佇列再寄一次 → 上述交接協定；測試 `AlertChannelTests.DirectHandoff_BackfillWhileSmtpInFlight_InFlightRowSentDirect_QueuedOneLeftToOutbox_EachSentOnce`、`DirectWithdraw_BackToNormalWhileSendingAnother_WithdrawnNotSent`（可阻塞的假寄送器控制在途時點），回滾保護 `DirectHandoff_BackfillRolledBack_DirectResumesAfterGrace_SentOnce`。
- 必修 2：提交成功但回傳失敗後，直送與佇列各寄一次、去重返回不交接 → 直送寄前查結果列、去重返回核對佇列列；測試 `HealthCheckTests.C14b_CommitThenThrow_SmtpConfigured_BothSendersRun_SentExactlyOnce`（佇列先跑、直送先跑、直送時資料庫讀不到三種順序）。
- 短核對 P2（codex）：交接的 fallback 只在第一次保留時設定——在途時補寫以 `pending` 保留後回滾、同一檢查回到正常，再次補寫判定過時（`skipped`）並提交，SMTP 失敗後佇列列卻被交回 `pending`、寄出過時的開啟信 → 補寫每次保留都以這一次的判定覆寫 fallback；去重核對不帶判定、保留既有交接的狀態（新保留才記 `pending`）；測試 `AlertChannelTests.DirectHandoff_RolledBackThenRecovered_RetryBackfillSuperseded_SmtpFails_RowSkipped_StaleMailNotSent`（直接補寫、提交後回傳失敗走去重核對兩種）。
- 必修 3：`toAddresses` 含 null 元素時 `Trim()` 丟例外回 500 → 正規化前擋下，回 `toAddresses` 的 400 欄位錯誤（`EmailFormat`）；測試 `AlertChannelTests.UpdateValidation_NullRecipient_400ToAddresses`（啟用、停用）。
- 必修 4：摘要信用 `CreatedUtc` 判斷，整點前建立、整點後才摘要化的列下一次領取就寄出 → 摘要化時把時間記進 `NextAttemptUtc`、以它判斷（偏離 1 已改寫；政策說明「下一個整點併成一封摘要信」不變，現在與程式一致）；測試 `AlertChannelTests.D03b_CreatedBeforeHour_DigestedAfterHour_WaitsForNextHour`。
- 不修（主控裁決）：密碼省略或 null＝保留、`""`＝清除，照 §17.5 與 D05（§17.5 補「或 null」、D05 表補這兩步，與 D05 測試一致）。
- 建議（直送停用語意）：直送成功讀到通道停用或設定不完整時，還沒開始寄的信丟掉（同 `pending → skipped`），交接中、在途的照交接處理；讀設定失敗仍沿用快取。測試 `DirectQueue_ChannelDisabledRead_PendingDropped_NotSentAfterReEnable`。
- 建議（安全測試）：收件、寄件地址帶 CRLF 回 400；寄件名稱與主旨帶 CRLF 時真的 MailKit 寄到本機假 relay，標頭沒有注入的 `Bcc:`／`X-Injected:`、`RCPT TO` 只有設定的收件人（MimeKit 編碼）；`sslOnConnect` 連自簽憑證的本機伺服器歸 `tls`（另以接受任何憑證的用戶端對照，確認伺服器確實出示了憑證）。測試 `MailKitSender_CrlfInRecipientFromNameSubject_RejectedOrEncoded_NoHeaderInjection`、`MailKitSender_UntrustedCertificate_422StageTls`。
- 建議（`ClosedPort()` 競態）：D07 改連測試持有埠的本機假伺服器 `LocalTcpServer.Resetting()`（收下連線就以 RST 中斷），仍歸 `connect`；`ClosedPort()` 移除。

**SMTP 密碼與 Data Protection 金鑰**（建議，只寫清楚、沒改部署設定）

- 金鑰位置照 `Program.cs`：有設 `DataProtection:KeysDirectory` 就落地到那個目錄；沒設而資料庫是 SQLite 檔案時放在資料庫檔旁的 `keys`；PostgreSQL 或記憶體資料庫沒設就不落地（每次啟動新金鑰，PostgreSQL 啟動時記 Warning）。沒有呼叫 `SetApplicationName`，應用程式區分用 ASP.NET Core 預設（content root 路徑）；金鑰檔沒有另外加密，靠目錄權限保護。
- 重啟：SQLite 檔案或有設 `KeysDirectory` 時解得開；PostgreSQL 沒設時重啟後解不開。重建（重新部署容器）：`KeysDirectory` 要在持久磁碟上，否則同樣解不開。多實例：要共用同一個 key ring（共享目錄）而且應用程式名稱一致；目前沒設應用程式名稱，各實例的 content root 路徑不同就互相解不開。正式部署需要：持久（多實例時共享）的 `DataProtection__KeysDirectory`；多實例另需固定應用程式名稱（程式還沒做，列為後續）。登入 cookie 用同一套金鑰，症狀相同。Render Demo 沒有持久磁碟，但 Demo 擋下通道設定（403），不受影響。
- 解不開時：GET 回 `passwordUnreadable: true`；佇列寄送標 `failed`、以階段 `config` 開通道失敗事件，標題「告警信寄不出去（設定不完整，請重填密碼）」（四語都有「重填密碼」）；測試信 409 `smtpNotConfigured`「…或密碼需要重填」；直送只記 Error log「SMTP 密碼解不開（金鑰可能不見了），請重填」並丟掉那封。提示已足夠讓管理員知道要重新輸入密碼，沒有改文字。

**已知限制（審查修正後仍在）**

- ⚠️ 提交結果不明、而直送寄前資料庫又讀不到時，直送只能先寄；之後若 `ops.monitor` 比看門狗的去重核對先跑，佇列會再寄一次（需要兩種故障同時發生）。
- ⚠️ 直送領著的佇列列 15 分鐘內沒交出（例如寄完就當機），佇列接手後會再寄一次；與一般領取過期的重寄語意相同。
- 佇列列依 `TransitionId` 查詢沒有索引（不加 migration）；只在補寫去重返回與直送交接時查，佇列列有保留天數，量小。
- ⚠️ 多實例與重建後解密 SMTP 密碼沒有實測（上面的結論來自程式與 ASP.NET Core 的預設行為）。

**驗證（本機，審查修正）**

- 新增與修改的測試類別（`AlertChannelTests`、`HealthCheckTests`，共 47 例）：SQLite 47 過（7 秒）、PostgreSQL 47 過（10 秒）。必修 1–4 的測試在修正前先跑過、全部失敗（9 例：雙寄或寄出撤回的信、500、摘要信提早寄出），修正後通過。
- WebAPITests（`Ops` 命名空間全部＋`AdminUsersApiTests`、`EndpointRoutesTests`、`ConcurrencyExceptionTests`、`SecurityTests`、`HealthTests`）：SQLite 234 過 1 略過（24 秒）、PostgreSQL 235 過（36 秒）。ApplicationTests（`Common`、`DatabaseErrorsTests`、`OpsSchemaTests`）：SQLite、PostgreSQL 各 146 過。完整套件交 CI。

