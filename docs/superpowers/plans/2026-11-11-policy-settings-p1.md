# 政策設定 p1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 建好政策登記、讀值服務、模組覆寫表與 API、兩個設定畫面，並把 A1、A2、A6、A7、A13、A14、A20 從程式常數改成讀政策；出廠值等於現值，部署後沒人改設定時行為不變。

**Architecture:** Domain 的 `PolicyCatalog` 是唯一寫出廠值與技術上下限的地方；Application 的 `IPolicyReader`（scoped，同一 request 快取）依「模組覆寫 → 全系統值（`Dynamic_SystemSetting` 鍵 `policy.<代碼>`）→ 部署出廠值 → 程式出廠值」查值，壞值退回上一層並記 Warning。各處常數改成呼叫讀值服務；Domain 純函式需要的值由呼叫端傳入。前端只從 API 取得生效值，不另存常數。

**Tech Stack:** .NET 8、EF Core SQLite、MediatR、xUnit＋FluentAssertions；Vue 3、Quasar 2、TanStack Query、vue-i18n、vitest、bun。不新增相依套件。

**Spec:** `docs/superpowers/specs/2026-11-11-policy-settings-design.md`（已核准，約束力最高）。repo 規範：`AGENTS.md`、`.claude/rules/backend.md`、`.claude/rules/frontend.md`、`docs/ai/testing.md`。

**路徑慣例：** 後端相對 `poc/backend/`，前端相對 `poc/frontend/`，文件相對 repo 根目錄。

---

## 子段切法

p1 一次合併太大，切成四個可獨立合併的子段，依序做。每段上線後行為不變、gate 能過。**每段只把「這段接上的政策」加進 `PolicyCatalog`**，畫面依登記產生，所以畫面上永遠只出現已經接上的政策，不會出現「設定存在、程式還讀自己的常數」（設計 §2 反目標）。

| 子段 | 內容 | Task | 估計行數（含測試，不含 EF 產生的 migration） |
|---|---|---|---|
| p1a | 後端基礎：登記、讀值服務、資料表與唯一的 migration、§7 API；接上 A1、A2（後端） | 1–6 | 約 1,100 |
| p1b | 前端：系統「政策設定」頁、模組設計「政策」分頁；任務規則畫面改讀生效值 | 7–11 | 約 1,000 |
| p1c | 任務與通知：A6、A7、A13、A14 | 12–16 | 約 700 |
| p1d | 附件：A20（含部署設定檔當出廠值） | 17–20 | 約 750 |

p1a 合併後到 p1b 合併前，A1 的上限只有 API 能改、前端仍顯示 20：沒人呼叫 API 時行為不變，可以接受；p1b 之後前端改讀 API。

---

## 計畫期間的判斷

設計沒講清楚、計畫自己決定的地方。每條：判斷／理由／選錯代價。

1. **切四個子段，政策登記跟著接線分段加入。** 理由：每段能獨立審、獨立合併，畫面不會出現沒接上的政策。代價：p1a–p1b 之間有一段只有 API 能改 A1；若想一次上線，可把 p1a、p1b 合併後再推。
2. **API 沿用 repo 既有前綴 `api/v3/dynamic`**：`GET/PUT system/policies[/{code}]`、`GET/PUT modules/{key}/policies[/{code}]`、`GET policies/effective?module={key}`。理由：設計 §7 說命名可調，既有端點全部在這個前綴下。代價：低，只是路徑字串。
3. **模組覆寫的寫入同時是 `IMasterDesignRequest`**：共用主檔模組的覆寫要設計權 Edit 再加系統管理員。理由：主檔被多個應用程式共用，改它的行為比照既有設計器請求（`CreateTaskRuleRequest` 等都這樣）。代價：若使用者認為主檔覆寫只要設計權，拿掉介面一行即可。
4. **生效值 API 只要求登入**；不存在的模組回 404，不檢查模組讀取權。理由：任務留言者看得到任務不代表有記錄所屬模組的權限（`DownloadTaskAttachmentHandler` 的既有規則），卡讀取權會讓留言框拿不到上限；生效值不含誰設的，不是敏感資料。代價：任何登入者能探知某個模組 key 是否存在。
5. **「系統設定 → 政策」做成獨立頁 `/system/policies`，選單名「政策設定」，排在「外觀預設」之後。** 理由：repo 沒有一個叫「系統設定」的頁面，系統管理是一組獨立頁。代價：之後若要合併成多分頁的「系統設定」，只搬頁面不動 API。
6. **政策代碼、分類、i18n 落點**：代碼用 `<領域>.<名稱>`（見 Global Constraints），分類只用 p1 需要的 `task`、`notification`、`attachment`、`locale`；文案放 i18n 頂層鍵 `policies`（名稱 `policies.items.<代碼>.name/help`）。新增共用 fixture `poc/shared/policy-catalog.json`，後端測試驗它等於 `PolicyCatalog`，前端測試驗四語文案齊全。理由：沿用 `poc/shared/filter-operators.json` 的做法，避免後端加了政策、前端沒文案。代價：加政策要多改一個 JSON 檔。
7. **技術上下限的數值**（設計 §3.3 交給主控）：見 Global Constraints 的表。理由：上限取「保護伺服器」與「現值有餘裕」之間；附件單檔 50MB × 留言 10 檔仍在單一請求可承受範圍。代價：若某個已部署環境的設定檔超過上限（例如單檔 100MB），上線後會被夾到 50MB 並記 Warning，該環境行為改變。
8. **A6「到期前幾天提醒」＝到期日正好是「今天＋N 天」那天提醒一次**，N 範圍 1–30；N=1 就是現況的「明天到期」。「提醒逾期」關掉就不補逾期提醒。理由：沿用現行「同一種提醒一天最多一則、那天沒開通知就錯過」的語意，不改 `LastDueSoonNotified` 的意義。代價：N>1 時若使用者那天沒登入就收不到「即將到期」（現況 N=1 也一樣）。
9. **A7 同時套到報表與儀表板的「本週、上週、近 N 週」區間**，不只「我的任務」的本週篩選。理由：反目標「同一政策只有一個讀值入口」——報表的 `ReportDates.WeekStart` 也寫死週一，只改任務會讓兩處的「本週」不一致。ISO 週分桶鍵（`2026-W45`）是格式，不跟著變，列進設計 §10。代價：報表的本週結果會跟著設定變；p1c 的手冊要寫明。
10. **A13 照設計 §3.4.1 做：只開放留言、完成、取消三種事件的收件角色**；指派、提到、到期提醒的收件人固定（§4 表格已於 2026-11-11 改成與 §3.4.1 一致；到期提醒收件人設計沒提，計畫判斷固定為負責人）。代價：使用者若真要設定到期提醒收件人，要另加一個政策。
11. **A14 縮短保留天數後，下次讀通知時就清掉超過新天數的舊通知。** 理由：保留天數本身就是清理規則，不是「新增數量上限」，§3.5 的「只管之後」不適用；畫面說明要寫清楚。代價：管理員誤設很小的值會清掉通知（無法還原）。
12. **A2 是獨立的全系統政策 `task.maxRulesPerPackage`（出廠 50，技術範圍 1–500），在匯入（`ImportAppPackageHandler`）與開啟（`AppOpenPlanner`）時檢查**，不放在靜態的 `AppPackageValidator.Validate` 裡。理由：`Validate` 是靜態方法，`AppCatalog` 啟動時也呼叫它，那時不能讀資料庫；現行會擋 50 條以上套件的兩個時間點正是匯入與開啟。上限 500 是因為套件本身還受 1MB 限制（B13）。代價：內建套件在啟動時不檢查總數（內建套件都在 50 條內）；管理員把上限調低後，條數超過的內建套件會在開啟時被擋。
23. **套件裡「每條規則的任務數」改用 A1 `task.maxTasksPerRule` 的全系統值檢查**（同樣在匯入與開啟時，與 A2 同一個方法），結構層只留「至少 1 個任務」。理由：不能在套件驗證裡留 `20` 這個常數，套件匯入時還不知道規則會落在哪個模組。出廠值 20 等於現況，所以上線行為不變。代價：某模組的覆寫比全系統寬時，套件仍以全系統值擋；開啟時再由 `CreateTaskRuleRequest` 依模組生效值檢查一次。
13. **附件單檔大小以 byte 為單位存、畫面也以 byte 編輯，旁邊附 MB 換算提示。** 理由：部署設定檔 `Attachments:MaxBytes` 本來就是 byte，既有測試用 10 byte、1000 byte 的設定；改用 MB 會讓那些設定被夾值。代價：管理員輸入大數字較不直覺；之後改成 MB 編輯只動前端。
14. **副檔名白名單另有寫死的危險副檔名黑名單（主控複核改判，安全檢查，列進設計 §10）**：`PolicyCatalog` 的 `attachment.allowedExtensions` 帶一份 `Forbidden` 清單 `html,htm,xhtml,svg,js,mjs,exe,msi,bat,cmd,com,scr,ps1,sh,jar,vbs,dll,app,dmg,php,asp,aspx,jsp`。`Normalize` 遇到黑名單項目回 null（PUT 回 400，訊息列出被拒的副檔名）；`Clamp`（部署設定檔）丟掉黑名單項目並記 Warning；`UploadAttachmentHandler`、`AddTaskCommentHandler` 上傳時也再擋一次（防資料庫被手改）。理由：白名單從「只有維運能改的設定檔」移到「系統管理員能在畫面改」，可改的人變多，出廠白名單本來就刻意排除 svg、html 與可執行檔。代價：真的需要收這類檔案的客戶要改程式；p1d 的測試要加 `Normalize_ForbiddenExtension_Rejected`、`Clamp_ConfigForbiddenExtension_DroppedWithWarning`、`Upload_ForbiddenExtensionInStoredList_Rejected`。
15. **`AttachmentSettings` 縮成部署設定（Root、UploadEnabled、DisabledReason）**；三個上限改由政策提供。`GET /attachments/settings` 回應形狀不變，`maxBytes`、`maxFilesPerField`、`allowedExtensions` 改成讀值服務的全系統值；需要模組值的留言輸入框改讀生效值 API。理由：前端既有元件與測試依賴這個回應形狀。代價：`maxBytes` 欄位不代表模組覆寫值，前端新程式不該再用它（Task 19 有說明註解）。
16. **上傳 filter 的順序改成「上傳開關 → 資格檢查（回傳該模組上限）→ 主體大小」**。理由：主體上限現在要看模組，必須先知道記錄或任務屬於哪個模組；資格檢查不讀主體，未授權的大請求仍在讀主體前被擋。代價：Content-Length 超大但同時沒權限的請求，回 403／404 而不是 413。
17. **系統政策的 PUT 不比照外觀預設擋 Demo 站。** 理由：其他系統管理寫入（使用者、群組）在 Demo 站都可以用，Demo 資料會重產；外觀擋 Demo 是因為外觀只存瀏覽器。代價：Demo 訪客能改全站政策，直到 Demo 重建。
18. **寫入後呼叫 `IPolicyReader.Invalidate()`。** 理由：讀值服務在 scope 內快取；ApplicationTests 預設整個測試共用一個 scope，同一 request 內寫後讀也要看到新值。代價：無。
19. **讀值服務以 `ModuleId`（`long?`）為鍵**，覆寫表外鍵指向模組並 cascade 刪除。理由：handler 手上多半是模組實體或 `TaskItem.ModuleId`；cascade 自然滿足「模組刪除時連同覆寫一起刪」。代價：無。
20. **模組設計頁的「政策」分頁對明細模組隱藏。** 理由：p1 的模組政策（任務規則、附件、提醒）對明細模組都無效（明細模組不能有任務規則、不能直接上傳附件）。代價：p2 的 A23 明細列上限是給明細模組的，那時要把條件改掉。
21. **模組政策回應不帶最後修改者與時間**，只存在表上（設計 §5、§12 判斷 8）。理由：設計沒要求顯示。代價：要顯示時回應加兩個欄位即可。
22. **前端在生效值載入前，受上限控制的「新增」按鈕一律停用**，不留前端預設值。理由：鐵則 2 不准前端另存一份常數。代價：生效值 API 失敗時新增鈕停用（顯示載入失敗訊息）。

---

## Global Constraints

- 出廠值＝現值，部署後沒人改設定時行為不變；既有測試的預期值不改（只改呼叫簽章）。
- 兩層：全系統預設＋模組覆寫；覆寫可比全系統寬或嚴，只受技術上下限。調全系統值不影響已存在的覆寫。
- 系統管理員改全系統（`ISystemAdminRequest`）；設計權 Edit 改模組覆寫（`IDesignerRequest`，讀取用 Read）。
- 改嚴只管之後的新增；編輯既有資料只在「這次增加數量」時檢查。
- 全系統值存 `Dynamic_SystemSetting`，鍵 `policy.<代碼>`；沒這筆就用出廠值。模組覆寫存新表 `Dynamic_ModulePolicy`。**整個 p1 只有 p1a 的一個 migration（`ModulePolicy`）**。
- 值一律存字串，由登記項型別解析；解析失敗或超出範圍視為沒設，退回上一層並記 Warning。
- 附件：部署設定檔 `Attachments` 節有寫的值當該環境出廠值，超出技術上下限啟動時記 Warning 並夾到上下限。
- 設定 `system` 範圍的政策做模組覆寫，一律回 400。寫入都走 `ITransactionalRequest`。
- 使用者看得到的後端訊息放 `PolicyMessages.cs`（中文）；前端新字串四語（`zh-TW`、`en-US`、`th`、`vi`）鍵集合一致、單行 JSON、新鍵放在頂層 `policies` 底下；含 `{`、`}`、`@`、`|` 的訊息用字面量寫法。
- 畫面在 390、820、1366、1920 四種寬度不得橫向捲動；輸入框要有 `label` 或 `aria-label`。
- 不寫 `const`／`static readonly` 的業務上限；技術上下限只寫在 `PolicyCatalog`。
- 測試只跑窄範圍（每個 Task 寫了指令）；後端與前端測試、gate 不並行；前端 vitest 一律 `--maxWorkers=2`。
- git：逐檔 `git add`；commit 前 `git status` 確認暫存區；不用 `--no-verify`、不 force push、不 merge commit。公開 repo：不寫公司字串、個人絕對路徑、內網 IP。
- 每個子段收尾跑 `bash scripts/unattended/gate.sh <worktree 絕對路徑>`，並用 `formwright-purpose-check` skill 逐項對照鐵則。

**政策登記（p1 全部；各子段只加自己那幾列）**

| 代碼 | 段 | 分類 | 型別 | 範圍 | 出廠 | 技術範圍 | 單位 |
|---|---|---|---|---|---|---|---|
| `task.maxRulesPerModule` | p1a | task | Integer | Module | `20` | 1–100 | count |
| `task.maxRulesPerPackage` | p1a | task | Integer | System | `50` | 1–500 | count |
| `task.maxTasksPerRule` | p1a | task | Integer | Module | `20` | 1–50 | count |
| `task.reminderLeadDays` | p1c | task | Integer | Module | `1` | 1–30 | days |
| `task.remindOverdue` | p1c | task | Boolean | Module | `true` | — | — |
| `task.notify.commented` | p1c | notification | RoleSet | Module | `assignee,creator` | 選項 assignee、creator | — |
| `task.notify.completed` | p1c | notification | RoleSet | Module | `creator` | 同上 | — |
| `task.notify.cancelled` | p1c | notification | RoleSet | Module | `creator` | 同上 | — |
| `notification.retentionDays` | p1c | notification | Integer | System | `90` | 1–3650 | days |
| `calendar.weekStart` | p1c | locale | Choice | System | `monday` | monday…sunday | — |
| `attachment.maxFileBytes` | p1d | attachment | Integer | Module | `10485760` | 1–52428800 | bytes |
| `attachment.maxFilesPerField` | p1d | attachment | Integer | Module | `20` | 1–100 | count |
| `attachment.allowedExtensions` | p1d | attachment | CodeList | System | `pdf,png,jpg,jpeg,webp,txt,csv,docx,xlsx,pptx` | 1–40 個，每個 `^[a-z0-9]{1,10}$` | — |
| `attachment.maxFilesPerComment` | p1d | attachment | Integer | Module | `5` | 1–10 | count |

標準字串格式：Integer 是不帶符號的十進位（`InvariantCulture`）；Boolean 是 `true`／`false`；Choice 是選項原字；RoleSet 依選項順序、逗號分隔、可為空字串；CodeList 小寫、去前導點、去重保留首次順序、逗號分隔。

## Review Focus

1. **壞值或範圍外的值已在資料庫裡**（手改資料庫、或未來縮小技術範圍）：讀值要退回上一層並記 Warning，不能讓存檔或頁面 500。→ Task 3 `BadStoredValues_FallBackOneLayer_AndWarn`。
2. **限制調得比既有資料還低**：已有 25 個任務的規則在上限 20 時，改標題要能存、加第 26 個要擋；已有 3 條規則時上限改 1，既有規則可改、不能新增；已滿的附件欄位不能再上傳。→ Task 5 `TasksPerRule_LoweredLimit_EditWithoutIncreaseAllowed`、`RulesLimitLowered_ExistingRulesStillEditable`；Task 18 `Upload_FieldAlreadyOverLoweredLimit_Rejected`。
3. **JSON null 被 `WhenWritingNull` 吃掉**：沒設的 `value`／`override` 在回應裡是「沒有這個欄位」，前端要當成 null；PUT body `{}` 與 `{"value":null}` 都是「回到上一層」。→ Task 4 `System_Put_EmptyBody_SameAsNull`、`System_Get_UnsetValueIsAbsent`；Task 7 `normalizes missing value to undefined`。
4. **上傳 filter 改順序後**：沒權限的大請求仍在讀主體前被擋；主體上限用的是該模組的覆寫值，不是全系統值。→ Task 18 `Upload_ModuleOverrideMaxBytes_413WithoutReadingTheBody`；既有 `Upload_WithoutEditPermission_RejectedBeforeTheBodyIsRead` 照常過。
5. **改全系統值時，已有覆寫的模組不受影響；把覆寫取消後才跟回全系統值**。→ Task 3 `ChangingSystem_KeepsExistingOverride`、Task 9 `沿用全系統送 null`。

---

# p1a 後端基礎＋A1、A2

### Task 1: Domain 政策登記與值的正規化

**Files:**
- Create: `src/DynamicModules.Domain/Policies/PolicyDefinition.cs`
- Create: `src/DynamicModules.Domain/Policies/PolicyCatalog.cs`
- Create: `poc/shared/policy-catalog.json`
- Test: `tests/DynamicModules.DomainTests/Policies/PolicyDefinitionTests.cs`、`tests/DynamicModules.DomainTests/Policies/PolicyCatalogFixtureTests.cs`

**Interfaces:**
- Produces（`namespace DynamicModules.Domain.Policies`）:
  - `public enum PolicyScope { System, Module }`
  - `public enum PolicyKind { Integer, Boolean, Choice, RoleSet, CodeList }`
  - `public sealed record PolicyDefinition(string Code, string Category, PolicyKind Kind, PolicyScope Scope, string Factory, int Min = 0, int Max = 0, IReadOnlyList<string>? Options = null, string? Unit = null)`
    - `public string SettingKey => $"policy.{Code}";`
    - `public string? Normalize(string? raw)`：合法回標準字串，否則 null（規則見 Global Constraints 的標準字串格式；Integer 與 CodeList 用 Min／Max，CodeList 的 Min／Max 是項目數）。
    - `public string? Clamp(string? raw)`：給部署設定檔用。Integer 能解析就夾到 Min–Max；CodeList 丟掉不合格項目、截到 Max 項，剩不到 Min 項回 null；其他型別等於 `Normalize`。
  - `public static class PolicyCatalog`：`TaskMaxRulesPerModule`、`TaskMaxTasksPerRule`、`TaskMaxRulesPerPackage` 三個 `public static readonly PolicyDefinition`（值照 Global Constraints 表）；`public static IReadOnlyList<PolicyDefinition> All`；`public static PolicyDefinition? Find(string code)`（Ordinal）。
  - 共用 fixture `poc/shared/policy-catalog.json`：`{ "codes": [...], "categories": [...], "choices": [...] }`，p1a 為 `codes: ["task.maxRulesPerModule","task.maxTasksPerRule","task.maxRulesPerPackage"]`、`categories: ["task"]`、`choices: []`。

- [ ] **Step 1: 寫失敗的測試**

`PolicyDefinitionTests`（用測試內自建的 definition，涵蓋五種型別，後面子段不必再寫型別測試）：

```csharp
static PolicyDefinition Int() => new("t.int", "task", PolicyKind.Integer, PolicyScope.Module, "20", 1, 100);
static PolicyDefinition Roles() => new("t.roles", "task", PolicyKind.RoleSet, PolicyScope.Module, "assignee,creator", Options: ["assignee", "creator"]);
static PolicyDefinition Exts() => new("t.ext", "attachment", PolicyKind.CodeList, PolicyScope.System, "pdf", 1, 40);

[Theory] [InlineData("20", "20")] [InlineData("1", "1")] [InlineData("100", "100")]
[InlineData("0", null)] [InlineData("101", null)] [InlineData("abc", null)] [InlineData("2.5", null)] [InlineData(null, null)]
public void Integer_Normalize(string? raw, string? expected) => Int().Normalize(raw).Should().Be(expected);

// Boolean: "TRUE" → "true"、"no" → null；Choice（Options monday,sunday）: "monday" → "monday"、"Monday" → null
// RoleSet: "creator,assignee" → "assignee,creator"；"" → ""；"creator,creator" → "creator"；"owner" → null
// CodeList: ".PDF, png,pdf" → "pdf,png"；"" → null；"p-d" → null；41 個合法項 → null
// Clamp: Int "0" → "1"、"999" → "100"、"x" → null；Exts "pdf,bad-ext" → "pdf"；41 個合法項 → 前 40 個
```

`PolicyCatalogFixtureTests`：
- `Codes_AreUnique_AndFactoriesAreNormal`：`All` 的 `Code` 不重複；每個 `d.Normalize(d.Factory).Should().Be(d.Factory)`；`d.SettingKey.Length.Should().BeLessOrEqualTo(100)`（`Dynamic_SystemSetting.Key` 上限）。
- `Factories_EqualCurrentBehavior`：`TaskMaxRulesPerModule.Factory == "20"`、`TaskMaxTasksPerRule.Factory == "20"`、`TaskMaxRulesPerPackage.Factory == "50"` 且 `Scope == PolicyScope.System`。
- `Catalog_MatchesSharedFixture`：沿用 `IndexAndOperatorTests.FixturePath()` 往上找 `poc/shared/policy-catalog.json` 的寫法；`codes` 與 `All` 的代碼集合相等、`categories` 與分類集合相等、`choices` 與所有 Options 聯集相等。

- [ ] **Step 2: 跑測試確認失敗**

Run: `cd poc/backend && dotnet test tests/DynamicModules.DomainTests --filter "FullyQualifiedName~Policies"`
Expected: 編譯失敗（`PolicyDefinition` 不存在）

- [ ] **Step 3: 實作 `PolicyDefinition`、`PolicyCatalog`，建立 fixture**

`Normalize` 用 `int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, …)`（不收正負號、小數、空白）；CodeList 項目 regex `^[a-z0-9]{1,10}$`。

- [ ] **Step 4: 跑測試確認通過**（同 Step 2 指令，Expected: PASS）

- [ ] **Step 5: Commit**

```bash
git add poc/backend/src/DynamicModules.Domain/Policies/PolicyDefinition.cs poc/backend/src/DynamicModules.Domain/Policies/PolicyCatalog.cs poc/shared/policy-catalog.json poc/backend/tests/DynamicModules.DomainTests/Policies/PolicyDefinitionTests.cs poc/backend/tests/DynamicModules.DomainTests/Policies/PolicyCatalogFixtureTests.cs
git commit -m "feat(policy): 政策登記與值的正規化"
```

### Task 2: 模組覆寫表、儲存庫與唯一的 migration

**Files:**
- Create: `src/DynamicModules.Domain/Entities/Dynamic/ModulePolicy.cs`
- Create: `src/DynamicModules.Domain/Data/Configurations/ModulePolicyConfiguration.cs`
- Create: `src/DynamicModules.Domain/Repositories/Dynamic/IModulePolicyRepository.cs`
- Create: `src/DynamicModules.Persistence/Repositories/Dynamic/ModulePolicyRepository.cs`
- Create: migration `ModulePolicy`（`src/DynamicModules.Persistence/Migrations/`，EF 產生，不手改）
- Modify: `src/DynamicModules.Domain/Repositories/Dynamic/ISystemSettingRepository.cs`、`src/DynamicModules.Persistence/Repositories/Dynamic/SystemSettingRepository.cs`、`src/DynamicModules.Persistence/DependencyInjection.cs`（`AddPersistenceRepositories` 加一行）
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/Policies/ModulePolicyStoreTests.cs`

**Interfaces:**
- Produces:
  - `public class ModulePolicy { long ModuleId; string Code = ""; string Value = ""; string ModifiedUserId = ""; DateTime ModifiedTime; }`（一般屬性，不繼承 `AuditableEntity`）
  - Configuration：表 `Dynamic_ModulePolicy`，主鍵 `(ModuleId, Code)`；`Code` 100、`Value` 500 必填、`ModifiedUserId` 50 必填；`HasOne<Module>().WithMany().HasForeignKey(x => x.ModuleId).OnDelete(DeleteBehavior.Cascade)`。
  - `IModulePolicyRepository`：`Task<List<ModulePolicy>> ListByModuleAsync(long moduleId, CancellationToken ct)`（不追蹤）、`Task<ModulePolicy?> GetTrackedAsync(long moduleId, string code, CancellationToken ct)`、`void Add(ModulePolicy p)`、`void Remove(ModulePolicy p)`。
  - `ISystemSettingRepository` 新增：`Task<IReadOnlyDictionary<string, string>> ListByPrefixAsync(string prefix, CancellationToken ct)`（不追蹤，Ordinal `StartsWith`）、`Task RemoveAsync(string key, CancellationToken ct)`（沒有這筆就什麼都不做，由呼叫端存檔）。

- [ ] **Step 1: 寫失敗的測試**（`ModulePolicyStoreTests : DesignerTestBase`）
  - `SystemSettings_ListByPrefix_AndRemove`：寫入 `policy.a`、`policy.b`、`theme.defaultMode`，`ListByPrefixAsync("policy.")` 只回兩筆；`RemoveAsync("policy.a")`＋存檔後剩一筆；移除不存在的鍵不丟例外。
  - `DeleteModule_RemovesItsOverrides`：`NewModule("pm")`，直接 `Add(new ModulePolicy{ ModuleId=…, Code="task.maxRulesPerModule", Value="3", ModifiedUserId="admin" })` 存檔；`Send(new DeleteModuleRequest("pm", 0))` 成功後 `Db.Context.Set<ModulePolicy>().Count()` 為 0。

- [ ] **Step 2: 跑測試確認失敗**

Run: `cd poc/backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~ModulePolicyStoreTests"`
Expected: 編譯失敗

- [ ] **Step 3: 實作實體、configuration、儲存庫、註冊**

- [ ] **Step 4: 產生 migration**

Run: `cd poc/backend && dotnet tool restore && dotnet ef migrations add ModulePolicy --project src/DynamicModules.Persistence --startup-project src/DynamicModules.Persistence`
Expected: 新增 `*_ModulePolicy.cs`、`.Designer.cs`，`DynamicDbContextModelSnapshot.cs` 只多 `ModulePolicy` 一段。打開確認只有建表與外鍵，沒有動到其他表。

- [ ] **Step 5: 跑測試確認通過**（同 Step 2，另跑 `--filter "FullyQualifiedName~SeedDataTests"` 確認種子與 migration 正常）

- [ ] **Step 6: Commit**（逐檔 add 上列檔案與三個 migration 檔；訊息 `feat(policy): 模組政策覆寫表與 migration`）

### Task 3: 讀值服務

**Files:**
- Create: `src/DynamicModules.Application/Dynamic/PolicyService/PolicyReader.cs`（`IPolicyReader`、`PolicyResolution`、`PolicyReader`）
- Create: `src/DynamicModules.Application/Dynamic/PolicyService/PolicyEnvironmentDefaults.cs`
- Modify: `src/DynamicModules.Application/DependencyInjection.cs`
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/Policies/PolicyReaderTests.cs`

**Interfaces:**
- Consumes: Task 1 `PolicyDefinition`、`PolicyCatalog`；Task 2 兩個儲存庫。
- Produces（`namespace DynamicModules.Application.Dynamic.PolicyService`）:
  - `public sealed record PolicyResolution(string Factory, string? System, string? Override, string Effective);`
  - `public interface IPolicyReader`
    - `Task<PolicyResolution> ResolveAsync(PolicyDefinition def, long? moduleId, CancellationToken ct);`
    - `Task<int> IntAsync(PolicyDefinition def, long? moduleId, CancellationToken ct);`
    - `Task<bool> BoolAsync(PolicyDefinition def, long? moduleId, CancellationToken ct);`
    - `Task<string> ChoiceAsync(PolicyDefinition def, long? moduleId, CancellationToken ct);`
    - `Task<IReadOnlyList<string>> ListAsync(PolicyDefinition def, long? moduleId, CancellationToken ct);`（RoleSet 與 CodeList；空字串回空清單）
    - `void Invalidate();`
  - `public sealed class PolicyEnvironmentDefaults(IReadOnlyDictionary<string, string> values)`：`public static readonly PolicyEnvironmentDefaults Empty`；`public string FactoryOf(PolicyDefinition def)`（有就回 values 的值，否則 `def.Factory`）。p1d 再加 `Build`。
  - DI：`services.AddScoped<IPolicyReader, PolicyReader>();`、`services.TryAddSingleton(PolicyEnvironmentDefaults.Empty);`（WebAPI 之後註冊的實例會蓋過它）。

查值規則：`Factory = env.FactoryOf(def)`；`System = def.Normalize(全系統列)`；`Override = def.Scope == Module && moduleId is not null ? def.Normalize(覆寫列) : null`；`Effective = Override ?? System ?? Factory`。存在但 Normalize 為 null 的值記 `LogWarning("政策 {Code} 的{Layer}值「{Value}」無法使用，改用上一層", …)`。全系統列用 `ListByPrefixAsync("policy.")` 每個 scope 讀一次；覆寫依模組各讀一次；`Invalidate()` 清掉兩種快取。型別不符（例如對 Boolean 呼叫 `IntAsync`）丟 `InvalidOperationException`。

- [ ] **Step 1: 寫失敗的測試**（`PolicyReaderTests : DesignerTestBase`，用 `ConfigureServices` 注入抓 log 的 `ILogger<PolicyReader>`，寫法比照 `TaskFollowUpTests.CaptureLogger`；資料直接寫 `Db.Context` 後呼叫 `Invalidate()`）

```csharp
var def = PolicyCatalog.TaskMaxRulesPerModule;
// Factory_WhenNothingSet: IntAsync(def, null) == 20；IntAsync(def, moduleA) == 20
// SystemValue_OverridesFactory: policy.task.maxRulesPerModule = "5" → 兩個模組都是 5
// ModuleOverride_WinsForThatModuleOnly: 系統 5、模組 A 覆寫 50 → A 是 50（比全系統寬也行）、B 是 5
// ChangingSystem_KeepsExistingOverride: 再把系統改 7 → A 仍 50、B 變 7；刪掉 A 的覆寫 → A 變 7
// BadStoredValues_FallBackOneLayer_AndWarn: 系統 "abc" → 20；A 覆寫 "999"（超出 1–100）且系統 "5" → 5；log 有兩則 Warning 且含代碼
// SystemScope_IgnoresOverrideRows: new PolicyDefinition("test.sys","task",Integer,System,"3",1,9)，覆寫表有 A 的 "test.sys"="8" → IntAsync(def, A) == 3
// EnvironmentDefault_ReplacesFactory: ConfigureServices 註冊 new PolicyEnvironmentDefaults({ ["task.maxRulesPerModule"] = "9" }) → 沒設時 9，ResolveAsync(...).Factory == "9"
// KindMismatch_Throws: BoolAsync(def, null) 丟 InvalidOperationException
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `cd poc/backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~PolicyReaderTests"`
Expected: 編譯失敗

- [ ] **Step 3: 實作 `PolicyReader`、`PolicyEnvironmentDefaults`、DI 註冊**

- [ ] **Step 4: 跑測試確認通過**（同 Step 2）

- [ ] **Step 5: Commit**（`feat(policy): 政策讀值服務`）

### Task 4: §7 API

**Files:**
- Create: `src/DynamicModules.Application/Dynamic/PolicyService/PolicyRequests.cs`
- Create: `src/DynamicModules.Application/Dynamic/PolicyService/PolicyHandlers.cs`
- Create: `src/DynamicModules.Application/Dynamic/PolicyService/PolicyModels.cs`（回應型別＋`PolicyJson`）
- Create: `src/DynamicModules.Application/Dynamic/PolicyService/PolicyMessages.cs`
- Create: `src/DynamicModules.WebAPI/Controllers/v3/Dynamic/PoliciesController.cs`
- Test: `tests/DynamicModules.WebAPITests/Dynamic/PolicyApiTests.cs`；`tests/DynamicModules.WebAPITests/Dynamic/SharedMasterDesignerTests.cs`（加一個 Fact）

**Interfaces:**
- Consumes: Task 3 `IPolicyReader`；Task 2 儲存庫。
- Produces:
  - Requests：
    - `GetSystemPoliciesRequest() : IRequest<ServiceResult>, ISystemAdminRequest`
    - `UpdateSystemPolicyRequest(string Code, JsonElement? Value) : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest`
    - `GetModulePoliciesRequest(string ModuleKey) : IRequest<ServiceResult>, IDesignerRequest`（Read）
    - `UpdateModulePolicyRequest(string ModuleKey, string Code, JsonElement? Value) : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest`（Edit；`MasterTargets => [new(ModuleKey: ModuleKey)]`）
    - `GetEffectivePoliciesRequest(string? ModuleKey) : IRequest<ServiceResult>`
  - Responses（`PolicyModels.cs`；`Kind`、`Scope` 用 enum，全域 camelCase 轉成 `integer`、`roleSet`、`system`…）：
    - `SystemPolicyResponse(string Code, string Category, PolicyKind Kind, PolicyScope Scope, string? Unit, int? Min, int? Max, IReadOnlyList<string>? Options, JsonElement Factory, JsonElement? Value, JsonElement Effective)`
    - `ModulePolicyResponse(string Code, string Category, PolicyKind Kind, string? Unit, int? Min, int? Max, IReadOnlyList<string>? Options, JsonElement SystemValue, JsonElement? Override, JsonElement Effective)`（`SystemValue` 是全系統生效值＝System ?? Factory）
    - `EffectivePoliciesResponse(string? ModuleKey, IReadOnlyDictionary<string, JsonElement> Values)`（全部政策，含 system 範圍）
    - `Min`／`Max` 只在 Integer、CodeList 有值，`Options` 只在 Choice、RoleSet 有值。
  - `public static class PolicyJson`：`JsonElement ToJson(PolicyDefinition def, string canonical)`（Integer→number、Boolean→bool、Choice→string、RoleSet／CodeList→string array）；`string? FromJson(PolicyDefinition def, JsonElement value)`（型別不對回 null，其餘交給 `def.Normalize`）。
  - `PolicyMessages`：`NotFound = "找不到政策"`、`ModuleNotFound = "找不到模組"`、`SystemOnly = "這個政策只能在系統設定改，不能由模組覆寫"`、`static string Invalid(PolicyDefinition d)`：Integer `$"必須是 {d.Min} 到 {d.Max} 的整數"`；Boolean `"必須是 true 或 false"`；Choice `$"必須是以下其中一個：{string.Join("、", d.Options!)}"`；RoleSet `$"只能從以下角色挑選：{string.Join("、", d.Options!)}"`；CodeList `$"要有 {d.Min} 到 {d.Max} 個，每個只能是 1 到 10 個小寫英文或數字"`。
  - Controller（`[Route("api/v3/dynamic")]`）：`GET system/policies`、`PUT system/policies/{code}`、`GET modules/{key}/policies`、`PUT modules/{key}/policies/{code}`、`GET policies/effective`（`[FromQuery(Name = "module")] string? module`）；PUT body `public record PolicyValueBody(JsonElement? Value);`。GET 清單直接回陣列，依 `PolicyCatalog.All` 順序；模組清單只列 Module 範圍。

Handler 檢查順序：
- 系統 PUT：代碼 404 → 值 null 時 `settings.RemoveAsync(def.SettingKey)`；否則 `FromJson` 為 null 回 `ServiceResult.Invalid({"value": [PolicyMessages.Invalid(def)]})`，合法則 `SetAsync` → `uow.CommitAsync` → `reader.Invalidate()` → 回該項 `SystemPolicyResponse`。
- 模組 PUT：模組 404 → 代碼 404 → `def.Scope == System` 回 400（鍵 `code`，`SystemOnly`）→ 值 null 時刪除覆寫（沒有也成功）；否則驗值、upsert（`ModifiedUserId = ICurrentUser.UserId`、`ModifiedTime = IBusinessTime.Now`）→ commit → `Invalidate()` → 回 `ModulePolicyResponse`。
- 生效值：`ModuleKey` 為 null 時 moduleId 為 null；有給但找不到回 404。

- [ ] **Step 1: 寫失敗的測試**（`PolicyApiTests`，`TestApp`＋`AsUser`；設計權使用者比照 `SharedMasterDesignerTests` 給 `planner01` 加 `UserProgramPrivilege{ ProgramCode = "DYN00000", … }`）
  - `System_Get_AdminOnly`：`sales01` 403；`admin` 200，`task.maxRulesPerModule` 那項 `factory` 20、`effective` 20、`kind` `"integer"`、`scope` `"module"`、`min` 1、`max` 100。
  - `System_Get_UnsetValueIsAbsent`：該項 JSON 沒有 `value` 屬性（`TryGetProperty("value", out _)` 為 false）。
  - `System_Put_OutOfRange_400WithRange`：`{ value: 0 }` → 400，`errors.value[0] == "必須是 1 到 100 的整數"`；`{ value: "20" }`（字串）也 400。
  - `System_Put_ThenNull_BackToFactory`：`{ value: 10 }` → 200、`effective` 10、資料庫 `policy.task.maxRulesPerModule` 為 `"10"`；`{ value: null }` → 列被刪、`effective` 20。
  - `System_Put_EmptyBody_SameAsNull`：先設 10，再 PUT `{}` → `effective` 20。
  - `System_Put_UnknownCode_404`。
  - `Module_Get_NeedsDesignerRead`：沒有 DYN00000 權限的 `sales01` 403；`planner01`（Read）200，`systemValue` 20、沒有 `override`。
  - `Module_Put_NeedsDesignerEdit`：只有 Read 的 `planner01` 403；給 Edit 後 `{ value: 3 }` 200、`override` 3、`effective` 3；`{ value: null }` 後沒有 `override`。
  - `Module_Put_UnknownModuleOrCode_404`。
  - `Effective_AnyLoggedInUser`：`sales01` 取 `?module=<key>` 200，`values["task.maxRulesPerModule"]` 等於覆寫值；不帶 module 是全系統值；不存在的模組 404；未登入 401。
  - `SharedMasterDesignerTests.ModulePolicyOverride_OnMaster_NeedsSystemAdmin`：沿用 `OpenCrmAndStockAsync` 與 `planner01` 全權限，PUT `modules/{Partner}/policies/task.maxRulesPerModule` → 403；admin → 200。
  - `Module_Put_SystemScope_400`：PUT `modules/<key>/policies/task.maxRulesPerPackage` `{ value: 10 }` → 400，`errors.code[0] == "這個政策只能在系統設定改，不能由模組覆寫"`；`GET modules/<key>/policies` 不列這一項。

- [ ] **Step 2: 跑測試確認失敗**

Run: `cd poc/backend && dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~PolicyApiTests|FullyQualifiedName~ModulePolicyOverride_OnMaster"`
Expected: 編譯失敗或 404

- [ ] **Step 3: 實作 requests、handlers、models、messages、controller**

- [ ] **Step 4: 跑測試確認通過**（同 Step 2）

- [ ] **Step 5: Commit**（`feat(policy): 政策設定 API`）

### Task 5: A1 任務規則數接上政策

**Files:**
- Modify: `src/DynamicModules.Application/Dynamic/TaskRuleService/TaskRuleValidator.cs:16-71`
- Modify: `src/DynamicModules.Application/Dynamic/TaskRuleService/TaskRuleModels.cs:37,43`
- Modify: `src/DynamicModules.Application/Dynamic/TaskRuleService/TaskRuleHandlers.cs:83,104`
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/Tasks/TaskRuleDefinitionTests.cs`

**Interfaces:**
- Consumes: `IPolicyReader.IntAsync`、`PolicyCatalog.TaskMaxRulesPerModule`、`TaskMaxTasksPerRule`。
- Produces:
  - `TaskRuleValidator(IModuleRepository modules, ITaskRuleRepository rules, FieldTypeRegistry registry, IPolicyReader policies)`；常數只留 `public const int MaxOffsetDays = 3650;`（技術限制，不在本段範圍）。
  - `public async Task<ServiceResult?> ValidateAsync(Module module, TaskRuleInput input, TaskRule? existing, CancellationToken ct)`：`existing` 是更新中的規則（`GetByKeyAsync` 已含 Tasks），新增時為 null。
  - `TaskRuleMessages.TasksCount(int max) => $"任務至少 1 個、至多 {max} 個"`、`TaskRuleMessages.TooManyRules(int max) => $"每個模組至多 {max} 條任務規則"`（由 const 改成 static 方法）；新增 `TasksRequired = "任務至少 1 個"`（給套件結構層，Task 6 用）。

規則：任務數 0 一律擋；`count > maxTasks && count > (existing?.Tasks.Count ?? 0)` 才擋（§3.5 只檢查增加）。規則數只在 `existing is null` 時檢查 `模組既有規則數 >= maxRules`。

- [ ] **Step 1: 寫失敗的測試**（加在 `TaskRuleDefinitionTests`；覆寫用 `Send(new UpdateModulePolicyRequest(r.Key, "task.maxRulesPerModule", J("2")))`）
  - `RulesPerModule_UsesModuleOverride`：覆寫 2 → 建兩條成功，第三條 400，`Errors["moduleKey"][0] == "每個模組至多 2 條任務規則"`。
  - `RulesLimitLowered_ExistingRulesStillEditable`：建 3 條後覆寫 1 → `UpdateTaskRuleRequest` 改 rule-0 名稱成功；新增 400。
  - `TasksPerRule_LoweredLimit_EditWithoutIncreaseAllowed`：建一條 3 個任務的規則；覆寫 `task.maxTasksPerRule` 2 → 同樣 3 個任務改標題成功；改成 4 個 400 且 `Errors["tasks"][0] == "任務至少 1 個、至多 2 個"`；新增一條 3 個任務的規則 400。
  - 既有 `TasksCount_And_RulesPerModule_Limits` 不改、照常過（出廠 20）。

- [ ] **Step 2: 跑測試確認失敗**

Run: `cd poc/backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~TaskRuleDefinitionTests"`
Expected: 新測試 FAIL（上限仍是 20）

- [ ] **Step 3: 改 validator、訊息、兩個 handler 的呼叫**

- [ ] **Step 4: 跑測試確認通過**（同 Step 2，另跑 `--filter "FullyQualifiedName~TaskRule"` 全部任務規則測試）

- [ ] **Step 5: Commit**（`feat(policy): 任務規則數改讀政策（A1）`）

### Task 6: A2 套件內任務規則總數；p1a 文件與 gate

**Files:**
- Modify: `src/DynamicModules.Application/Dynamic/AppService/AppPackageValidator.V7.cs:13,16,32,52`
- Modify: `src/DynamicModules.Application/Dynamic/AppService/Commands/ImportAppPackage.cs:25-41`（建構子加 `IPolicyReader`；`Validate` 之後加總數檢查）
- Modify: `src/DynamicModules.Application/Dynamic/AppService/AppOpenPlanner.cs:53-58`（同上，錯誤併進 `errors`，在 `errors.Count > 0` 判斷之前）
- Modify（既有測試）: `tests/DynamicModules.ApplicationTests/Dynamic/AppTaskRulePackageTests.cs:161-166`
- Test: `AppTaskRulePackageTests.cs`；`tests/DynamicModules.WebAPITests/Dynamic/AppFormat7Tests.cs`（加三個 Fact）
- Modify（文件）: `docs/superpowers/specs/2026-11-11-policy-settings-design.md`（§10 技術限制補 p1a 三項的範圍；§12 後面加「實作期間的判斷」引用本計畫判斷 2、3、4、12、23）、`docs/superpowers/specs/2026-11-06-erp-roadmap-design.md`（新增「政策設定的工作段」表：p1a–p1d、a2d 併入說明，p1a 標狀態）、`docs/feature-checklist.md`（p1 改 🚧，跑清單腳本重算）

**Interfaces:**
- Consumes: Task 5 `TaskRuleMessages.TasksRequired`、`TasksCount(int)`；`PolicyCatalog.TaskMaxRulesPerPackage`、`TaskMaxTasksPerRule`；`IPolicyReader.IntAsync(def, null, ct)`。
- Produces:
  - `AppPackageValidator.MaxTaskRules` 刪除；`Format7Structure` 不再檢查總數，每條規則只檢查 `r.Tasks.Count == 0` → `$"{at}.tasks: {TaskRuleMessages.TasksRequired}"`。
  - `public static IReadOnlyList<string> TaskRuleLimitErrors(AppPackageData p, int maxRules, int maxTasksPerRule)`（放在 `AppPackageValidator.V7.cs`）：`p.TaskRules.Count > maxRules` → `$"taskRules: 最多 {maxRules} 個"`（文字與現行相同）；每條規則 `r.Tasks.Count > maxTasksPerRule` → `$"{TaskRuleAt(r, i)}.tasks: {TaskRuleMessages.TasksCount(maxTasksPerRule)}"`；`null` 項略過（結構層已報錯）。
  - `ImportAppPackageHandler`、`AppOpenPlanner` 用全系統值（moduleId 為 null）呼叫它：`maxRules = TaskMaxRulesPerPackage`、`maxTasksPerRule = TaskMaxTasksPerRule`。匯入時錯誤走既有 `Invalid(...)`；開啟時錯誤併進預覽的 `Errors`。
  - 檔頭註解改成「套件內規則總數（政策 A2）與每條任務數（A1 全系統值）在匯入與開啟時由 `TaskRuleLimitErrors` 檢查；每模組規則數由 CreateTaskRuleRequest 依模組生效值檢查」。

- [ ] **Step 1: 改既有測試、寫失敗的測試**
  - `AppTaskRulePackageTests.TooManyTaskRules_Rejected` 改成兩段：51 條時 `Errors(Package(rules))`（靜態驗證）不再含 `"taskRules: 最多 50 個"`；同一份套件以同檔 `Errors` 的方式解析成 `AppPackageData` 後 `AppPackageValidator.TaskRuleLimitErrors(data, 50, 20).Should().Contain("taskRules: 最多 50 個")`；50 條時不含。
  - `AppTaskRulePackageTests.TasksPerRule_CheckedWithGivenLimit`：一條規則有 2 個任務（用 `With` 複製任務並改代碼），`TaskRuleLimitErrors(data, 50, 1)` 含 `".tasks: 任務至少 1 個、至多 1 個"`；`(data, 50, 2)` 為空。
  - `AppFormat7Tests.Import_OverPackageRuleLimit_400`：admin PUT `system/policies/task.maxRulesPerPackage` `{ value: 1 }` 後匯入 `Crm7()`（含 `LeadRule`、`PartnerRule` 兩條）→ 400、回應含 `"taskRules: 最多 1 個"`；改回 null 後匯入成功（出廠 50 時行為不變）。
  - `AppFormat7Tests.Open_PackageRuleLimitLoweredAfterImport_Rejected`：先以出廠值匯入 `Crm7()`，再把上限設 1，`OpenAsync("crmx")` 回 4xx 且含 `"taskRules: 最多 1 個"`，`App`、`LeadKey` 模組、`LeadRule` 都不存在。
  - `AppFormat7Tests.OpenApp_OverModuleRuleLimit_RollsBack`：匯入主檔與 `Crm7()` 後，在主檔 partner 模組手動建一條代碼不同於 `PartnerRule` 的規則（寫法同 `CreateManualRuleAsync`），admin PUT `modules/<partner key>/policies/task.maxRulesPerModule` `{ value: 1 }`；`OpenAsync("crmx")` 回 4xx、回應含「每個模組至多 1 條任務規則」，`App`、`LeadKey` 模組、`LeadRule` 都不存在（同 `OpenApp_RuleFails_RollsBackEverything` 的斷言）。

- [ ] **Step 2: 跑測試確認失敗**

Run: `cd poc/backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~AppTaskRulePackageTests"`，再跑 `dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~AppFormat7Tests"`
Expected: 編譯失敗（`TaskRuleLimitErrors` 不存在）

- [ ] **Step 3: 改 `AppPackageValidator.V7.cs`、`ImportAppPackage.cs`、`AppOpenPlanner.cs`**

- [ ] **Step 4: 跑測試確認通過**（同 Step 2，另跑 `--filter "FullyQualifiedName~AppCatalogChecks|FullyQualifiedName~AppFormat6Tests|FullyQualifiedName~AppPackageFormatTests"` 確認內建套件與其他格式照常）

- [ ] **Step 5: 更新文件**（設計、路線、功能清單；清單腳本：`python3 <lifecycle plugin>/scripts/feature_checklist.py docs/feature-checklist.md --write`）

- [ ] **Step 6: gate**

Run: `bash scripts/unattended/gate.sh <worktree 絕對路徑>`
Expected: 每步 `exit=0`

- [ ] **Step 7: Commit**（程式與測試一個 commit `feat(policy): 套件內任務規則總數改讀政策（A2）`；文件一個 commit `docs: 政策設定 p1a 進度與判斷`）

---

# p1b 前端兩個設定畫面＋任務規則改讀生效值

### Task 7: 前端型別、API、查詢、文案骨架

**Files:**
- Create: `src/domain/dynamic/policy-types.ts`
- Modify: `src/services/api/dynamic-api.ts`（新增五個方法）
- Create: `src/application/dynamic/queries/policies-queries.ts`
- Modify: `src/i18n/{zh-TW,en-US,th,vi}.json`（新增頂層 `policies`）
- Test: `src/domain/dynamic/policy-types.vitest.spec.ts`、`src/domain/dynamic/policy-i18n.vitest.spec.ts`

**Interfaces:**
- Produces:
  - `policy-types.ts`：
    - `export type PolicyKind = 'integer' | 'boolean' | 'choice' | 'roleSet' | 'codeList';`
    - `export type PolicyValue = number | boolean | string | string[];`
    - `export interface PolicyMeta { code: string; category: string; kind: PolicyKind; unit?: string; min?: number; max?: number; options?: string[] }`
    - `export interface SystemPolicy extends PolicyMeta { scope: 'system' | 'module'; factory: PolicyValue; value?: PolicyValue; effective: PolicyValue }`
    - `export interface ModulePolicy extends PolicyMeta { systemValue: PolicyValue; override?: PolicyValue; effective: PolicyValue }`
    - `export interface EffectivePolicies { moduleKey?: string; values: Record<string, PolicyValue> }`
    - `export function samePolicyValue(a?: PolicyValue, b?: PolicyValue): boolean`（陣列比內容與順序）
    - `export function policyValueText(meta: PolicyMeta, v: PolicyValue, t: Translate): string`（Boolean→`policies.on/off`；Choice、RoleSet→`policies.choices.<選項>` 用頓號串，空的顯示 `policies.none`；CodeList 用頓號串；Integer 數字，unit `bytes` 時附 `policies.bytesHint`）
    - `export function policyRangeText(meta: PolicyMeta, t: Translate): string | null`（Integer `policies.rangeInt`、CodeList `policies.rangeList`，其餘 null）
    - `export function groupByCategory<T extends PolicyMeta>(items: T[]): { category: string; items: T[] }[]`（保持 API 回傳順序）
    - `type Translate = (key: string, params?: Record<string, unknown>) => string`
  - `dynamicApi`：`getSystemPolicies(): Promise<SystemPolicy[]>`（GET `/system/policies`）、`saveSystemPolicy(code: string, value: PolicyValue | null): Promise<SystemPolicy>`（PUT，body `{ value }`）、`getModulePolicies(moduleKey: string): Promise<ModulePolicy[]>`、`saveModulePolicy(moduleKey: string, code: string, value: PolicyValue | null): Promise<ModulePolicy>`、`getEffectivePolicies(moduleKey: string | null): Promise<EffectivePolicies>`（GET `/policies/effective`，有 key 才帶 `params: { module }`）；路徑片段一律 `encodeURIComponent`。
  - `policies-queries.ts`：`useSystemPolicies()`（key `['system-policies']`）、`useSaveSystemPolicy()`（成功後 invalidate `['system-policies']`、`['module-policies']`、`['effective-policies']`）、`useModulePolicies(moduleKey: MaybeRefOrGetter<string>)`（key `['module-policies', key]`）、`useSaveModulePolicy(moduleKey)`（invalidate `['module-policies', key]`、`['effective-policies']`）、`useEffectivePolicies(moduleKey: MaybeRefOrGetter<string | null>)`（key `['effective-policies', key ?? '']`，`staleTime: 60_000`）。
  - i18n `policies`（zh-TW；en-US 給對應英文，th、vi 由實作者翻譯，不留中文）：`menu` 政策設定、`title` 政策設定、`hint` 調整全系統的預設值。標示「可模組覆寫」的項目，模組設計者可以在模組設計頁另外設定。改嚴只影響之後的新增，既有資料不動。、`current` 目前值、`factory` 出廠值、`range` 允許範圍、`rangeInt` {min} 到 {max}、`rangeList` {min} 到 {max} 個、`moduleOverridable` 可模組覆寫、`save` 儲存、`reset` 恢復出廠值、`saved` 已儲存、`saveFailed` 儲存失敗、`loadFailed` 政策載入失敗、`moduleTab` 政策、`moduleHint` 這裡的值只套用在這個模組；選「沿用全系統」會跟著系統設定變動。、`inherit` 沿用全系統（{value}）、`override` 覆寫為、`systemValue` 全系統值、`on` 開、`off` 關、`none` （無）、`bytesHint` 約 {size}、`categories.task` 任務、`categories.notification` 通知、`categories.attachment` 附件、`categories.locale` 地區與語系、`items.task.maxRulesPerModule.name` 每個模組的任務規則數上限、`.help` 超過時不能再新增規則；既有規則照常可改。、`items.task.maxTasksPerRule.name` 每條規則產生的任務數上限、`.help` 編輯既有規則時，只有增加任務數才檢查。、`items.task.maxRulesPerPackage.name` 一個套件內的任務規則總數上限、`.help` 匯入或開啟應用程式套件時檢查；已開啟的應用程式不受影響。、`choices` 先建空物件。

- [ ] **Step 1: 寫失敗的測試**
  - `policy-types.vitest.spec.ts`：`samePolicyValue(['a','b'], ['a','b'])` 為 true、`(['a'], ['b'])` false、`(undefined, undefined)` true；`policyRangeText({kind:'integer',min:1,max:100})` 為 `1 到 100`；`policyValueText` 對 `true` 回 `開`、對 RoleSet `[]` 回 `（無）`、對 unit `bytes` 的 `10485760` 含 `10.0 MB`；`groupByCategory` 保持順序；`normalizes missing value to undefined`：`{ ...item }` 沒有 `value` 時 `samePolicyValue(item.value, undefined)` 為 true（Review Focus 3）。
  - `policy-i18n.vitest.spec.ts`：import `../../../../shared/policy-catalog.json`，四語每個 `codes` 都有 `policies.items.<code>.name` 與 `.help`（非空字串），每個 `categories` 有 `policies.categories.<c>`，每個 `choices` 有 `policies.choices.<c>`。

- [ ] **Step 2: 跑測試確認失敗**

Run: `cd poc/frontend && bunx vitest run src/domain/dynamic/policy-types.vitest.spec.ts src/domain/dynamic/policy-i18n.vitest.spec.ts --maxWorkers=2`
Expected: FAIL（模組不存在）

- [ ] **Step 3: 實作型別、API、查詢，補四語文案**

- [ ] **Step 4: 跑測試確認通過**（同 Step 2，另跑 `bunx vitest run src/i18n --maxWorkers=2`（`locales.vitest.spec.ts` 鍵集合一致）與 `bun run typecheck`）

- [ ] **Step 5: Commit**（`feat(policy): 前端政策型別、API 與文案`）

### Task 8: 政策值編輯元件與「政策設定」頁

**Files:**
- Create: `src/presentation/policies/PolicyValueEditor.vue`
- Create: `src/pages/system/PoliciesPage.vue`
- Modify: `src/router/routes.ts`（`system/policies`）、`src/domain/dynamic/menu-nav.ts`（系統管理員連結，排在外觀預設之後：`{ to: '/system/policies', label: 'policies.menu', icon: 'tune', testid: 'policies-menu' }`）、`src/presentation/theme/breadcrumb.ts`（`SYSTEM_PAGES` 加 `['policies', 'policies.menu']`）
- Modify（既有測試）: `src/domain/dynamic/menu-nav.vitest.spec.ts:48-49`、`src/pages/HomePage.vitest.spec.ts:126`（清單在 `/system/appearance` 後加 `/system/policies`）、`src/presentation/theme/breadcrumb.vitest.spec.ts`（加 `['/system/policies', ['系統管理', '政策設定']]`）、`src/router/routes.vitest.spec.ts`（加 `/system/policies` 開啟 `PoliciesPage`）
- Test: `src/presentation/policies/PolicyValueEditor.vitest.spec.ts`、`src/pages/system/PoliciesPage.vitest.spec.ts`

**Interfaces:**
- Consumes: Task 7 全部。
- Produces:
  - `PolicyValueEditor.vue`：props `{ meta: PolicyMeta; modelValue: PolicyValue; label: string; disable?: boolean }`，emit `update:modelValue`。Integer：`q-input type="number"`，帶 `min`／`max`，送出前轉 number，unit `bytes` 時 hint 顯示換算；Boolean：`q-toggle`；Choice：`q-select`，選項文字 `policies.choices.<選項>`；RoleSet：`q-option-group type="checkbox"`；CodeList：`q-select multiple use-chips use-input new-value-mode="add-unique"`。每種都把 `label` 當可讀名稱（`label` 或 `aria-label`）。
  - `PoliciesPage.vue`：非系統管理員顯示 `ForbiddenNotice`（同 `AppearancePage`）；依分類分組，每列 `data-testid="policy-row-<code>"`：名稱、說明、`PolicyValueEditor`（草稿＝`value ?? factory`）、出廠值、允許範圍、範圍是 module 時的「可模組覆寫」標籤；草稿與 `effective` 不同時 `policy-save-<code>` 可按；`value` 有值時顯示 `policy-reset-<code>`（送 null）。存檔失敗把伺服器 `errors.value` 或 `message` 顯示在 `policy-error-<code>`，草稿保留；成功 Notify `policies.saved`。版面用 `row q-col-gutter-md` 加 `col-12 col-md-*`，四種寬度不橫捲。

- [ ] **Step 1: 寫失敗的測試**
  - `PolicyValueEditor.vitest.spec.ts`：五種型別各一個，掛載後操作會 emit 正確型別（Integer 輸入 `"7"` emit `7`；RoleSet 勾選 emit `['assignee']`；CodeList 輸入 `pdf` emit `['pdf']`），每個輸入元素都有可讀名稱。
  - `PoliciesPage.vitest.spec.ts`（`vi.mock` dynamicApi 的 `getMe`、`getSystemPolicies`、`saveSystemPolicy`，寫法同 `AppearancePage.vitest.spec.ts`）：
    - `非系統管理員看到禁止頁、不呼叫 getSystemPolicies`。
    - `依分類分組並顯示出廠值與範圍`：`task.maxRulesPerModule` 那列含 `每個模組的任務規則數上限`、`20`、`1 到 100`、`可模組覆寫`。
    - `改值後按儲存送出 number`：輸入 30 → `saveSystemPolicy('task.maxRulesPerModule', 30)`。
    - `沒改過時不顯示恢復出廠值；改過的按下送 null`：`value` 缺席時沒有 `policy-reset-…`；`value: 30` 時有，點擊送 `null`。
    - `儲存失敗顯示伺服器訊息、保留草稿`：reject `{ errors: { value: ['必須是 1 到 100 的整數'] } }` → `policy-error-…` 含該訊息，輸入框仍是 0。

- [ ] **Step 2: 跑測試確認失敗**

Run: `cd poc/frontend && bunx vitest run src/presentation/policies src/pages/system/PoliciesPage.vitest.spec.ts src/domain/dynamic/menu-nav.vitest.spec.ts src/pages/HomePage.vitest.spec.ts src/presentation/theme/breadcrumb.vitest.spec.ts src/router/routes.vitest.spec.ts --maxWorkers=2`
Expected: FAIL

- [ ] **Step 3: 實作元件、頁面、路由、選單、麵包屑**

- [ ] **Step 4: 跑測試確認通過**（同 Step 2，加 `bun run typecheck`）

- [ ] **Step 5: Commit**（`feat(policy): 系統政策設定頁`）

### Task 9: 模組設計頁「政策」分頁

**Files:**
- Create: `src/presentation/policies/ModulePoliciesTab.vue`
- Modify: `src/pages/system/dynamic-modules/ModuleEditorPage.vue:279,358`（在任務規則分頁後加 `<q-tab v-if="!mod?.detailOf" name="policies" :label="t('policies.moduleTab')" data-testid="tab-policies" />` 與對應 panel）
- Modify（既有測試）: `src/pages/system/dynamic-modules/ModuleEditorPage.vitest.spec.ts:15`（`vi.mock` 清單加 `getModulePolicies`、`saveModulePolicy`）
- Test: `src/presentation/policies/ModulePoliciesTab.vitest.spec.ts`；`ModuleEditorPage.vitest.spec.ts` 加一個 it

**Interfaces:**
- Consumes: Task 7 `useModulePolicies`、`useSaveModulePolicy`、`useMe`；Task 8 `PolicyValueEditor`。
- Produces: `ModulePoliciesTab.vue` props `{ module: ModuleDetail }`。每列 `policy-row-<code>`：名稱、說明、全系統值文字、`q-radio` 兩個選項 `policy-inherit-<code>`（`policies.inherit`，帶全系統值文字）與 `policy-override-<code>`（`policies.override`＋`PolicyValueEditor`，初值 `override ?? systemValue`）；`policy-save-<code>` 在選擇或草稿與現況不同時可按：選沿用送 null、選覆寫送草稿。`me.designer.edit` 為 false 時全部唯讀、沒有儲存鈕。錯誤顯示同 Task 8。

- [ ] **Step 1: 寫失敗的測試**（`mockHttp`，寫法同 `TaskRulesTab.vitest.spec.ts`）
  - `沒有覆寫時選沿用全系統並顯示全系統值`：`systemValue: 20`、沒有 `override` → inherit radio 已選、文字含 `沿用全系統（20）`。
  - `選覆寫填 5 存檔送 PUT body { value: 5 }`：URL `/modules/m1/policies/task.maxRulesPerModule`。
  - `沿用全系統送 null`（Review Focus 5）：有 `override: 5` 時切到沿用、存檔 → body `{ value: null }`。
  - `非設計器編輯者唯讀`：`designer.edit: false` → 沒有 `policy-save-…`，radio 停用。
  - `ModuleEditorPage.vitest.spec.ts`：`明細模組沒有政策分頁`：一般模組有 `tab-policies`、`detailOf` 有值時沒有。

- [ ] **Step 2: 跑測試確認失敗**

Run: `cd poc/frontend && bunx vitest run src/presentation/policies/ModulePoliciesTab.vitest.spec.ts src/pages/system/dynamic-modules/ModuleEditorPage.vitest.spec.ts --maxWorkers=2`
Expected: FAIL

- [ ] **Step 3: 實作分頁並接進 ModuleEditorPage**

- [ ] **Step 4: 跑測試確認通過**（同 Step 2，加 `bun run typecheck`）

- [ ] **Step 5: Commit**（`feat(policy): 模組設計政策分頁`）

### Task 10: 任務規則畫面改讀生效值

**Files:**
- Modify: `src/domain/dynamic/task-rule-draft.ts:48-49,113,131`（刪 `MAX_TASKS`、`MAX_RULES`）
- Modify: `src/presentation/dynamic/tasks/TaskRulesTab.vue:8,64-65`、`src/presentation/dynamic/tasks/TaskRuleEditorDialog.vue:9,78,176`
- Modify: `src/i18n/{zh-TW,en-US,th,vi}.json`：`taskRules.limit` 改「每個模組最多 {max} 條任務規則」、`taskRules.errors.tasksCount` 改「任務至少 1 個、至多 {max} 個」（四語都改成帶 `{max}`）
- Modify（既有測試）: `src/domain/dynamic/task-rule-draft.vitest.spec.ts`（所有 `validateDraft(d, fields)` 改成 `validateDraft(d, fields, LIMITS)`，`const LIMITS = { maxTasks: 20, originalTaskCount: 0 }`）、`src/presentation/dynamic/tasks/TaskRulesTab.vitest.spec.ts:40-45`（mock 加 `/policies/effective` 回 `{ moduleKey: 'm1', values: { 'task.maxRulesPerModule': o.maxRules ?? 20, 'task.maxTasksPerRule': o.maxTasks ?? 20 } }`）

**Interfaces:**
- Consumes: Task 7 `useEffectivePolicies`。
- Produces:
  - `export function validateDraft(draft: TaskRuleDraft, fields: SchemaField[], limits: { maxTasks: number; originalTaskCount: number }): string[]`：0 個任務一律 `tasksCount`；`length > maxTasks && length > originalTaskCount` 才 `tasksCount`。
  - `TaskRuleEditorDialog` 新 prop `maxTasks: number`；`originalTaskCount` 取自編輯中規則的任務數（新增為 0）；錯誤文字 `t(key, { max: maxTasks })`；新增任務鈕在 `draft.tasks.length >= maxTasks` 時停用。
  - `TaskRulesTab`：`useEffectivePolicies(key)`；新增鈕在生效值未載入或 `rules.length >= maxRules` 時停用；提示 `t('taskRules.limit', { max: maxRules })`；生效值載入失敗顯示 `policies.loadFailed`。

- [ ] **Step 1: 改既有測試、寫失敗的測試**
  - `task-rule-draft.vitest.spec.ts` 加：`既有規則超過新上限時，不增加就不報錯`：3 個任務、`{ maxTasks: 2, originalTaskCount: 3 }` → 沒有 `tasksCount`；4 個任務 → 有。
  - `TaskRulesTab.vitest.spec.ts` 加：`上限依模組生效值`：`maxRules: 2`、已有 2 條 → 新增鈕停用、提示含 `每個模組最多 2 條任務規則`；`生效值還沒回來時新增鈕停用`。既有 `已有 20 條規則時…`、`19 條規則時…` 不改斷言。

- [ ] **Step 2: 跑測試確認失敗**

Run: `cd poc/frontend && bunx vitest run src/domain/dynamic/task-rule-draft.vitest.spec.ts src/presentation/dynamic/tasks/TaskRulesTab.vitest.spec.ts --maxWorkers=2`
Expected: FAIL

- [ ] **Step 3: 改 draft、兩個元件、四語文案**

- [ ] **Step 4: 跑測試確認通過**（同 Step 2，加 `bunx vitest run src/i18n --maxWorkers=2`、`bun run typecheck`；`grep -rn "MAX_TASKS\|MAX_RULES" src/domain src/presentation/dynamic/tasks` 應無結果）

- [ ] **Step 5: Commit**（`feat(policy): 任務規則畫面改讀生效值`）

### Task 11: p1b 手冊、實機與 gate

**Files:**
- Modify: `docs/manual.md`：新增「系統管理：政策設定」一節（放在「12. 系統管理：群組」之後，章節號依現況順延或用不編號小節）與 §17 任務規則補「上限依政策設定，出廠 20」；文案以畫面實際文字為準
- Modify: `docs/superpowers/specs/2026-11-06-erp-roadmap-design.md`（p1b 狀態）、`docs/feature-checklist.md`

- [ ] **Step 1: 實機驗證**：用 `formwright-demo-verify` skill（或 chrome-devtools）在 390、820、1366、1920 四種寬度開 `/system/policies` 與任一模組的「政策」分頁：不得橫向捲動；改值、恢復出廠值、覆寫、沿用各走一次；截圖留在 `.tmp/`。
- [ ] **Step 2: gate**：`bash scripts/unattended/gate.sh <worktree 絕對路徑>`，每步 `exit=0`
- [ ] **Step 3: Commit**（`docs: 政策設定畫面手冊與 p1b 進度`）

---

# p1c 任務與通知：A6、A7、A13、A14

### Task 12: 登記 p1c 政策與文案

**Files:**
- Modify: `src/DynamicModules.Domain/Policies/PolicyCatalog.cs`（加 7 項，見 Global Constraints 表；另加 `public static DayOfWeek WeekStartOf(string choice)`，`Enum.Parse<DayOfWeek>(choice, ignoreCase: true)`）
- Modify: `poc/shared/policy-catalog.json`（codes 加 7 個；categories 加 `notification`、`locale`；choices 加 `monday`…`sunday`、`assignee`、`creator`）
- Modify: `poc/frontend/src/i18n/{zh-TW,en-US,th,vi}.json`
- Test: `tests/DynamicModules.DomainTests/Policies/PolicyCatalogFixtureTests.cs`（`Factories_EqualCurrentBehavior` 加斷言）

文案（zh-TW）：
- `task.reminderLeadDays`：到期前幾天提醒／到期日前這麼多天，負責人打開通知時會收到一則「即將到期」。
- `task.remindOverdue`：提醒逾期／開啟時，逾期未完成的任務每天提醒負責人一次。
- `task.notify.commented`：新留言通知誰／被 @ 的人另收「提到你」，不重複收；留言者本人不通知。
- `task.notify.completed`：任務完成通知誰／完成的人本人不通知；系統建立的任務沒有建立者。
- `task.notify.cancelled`：任務取消通知誰／取消的人本人不通知；系統建立的任務沒有建立者。
- `notification.retentionDays`：通知保留天數／縮短後，下次打開通知時會清掉超過天數的舊通知。
- `calendar.weekStart`：一週從星期幾開始／影響「本週到期」篩選，以及報表、儀表板的本週、上週、近 N 週。
- `choices`：monday 星期一 … sunday 星期日、assignee 負責人、creator 建立者。

- [ ] **Step 1: 寫失敗的測試**：`Factories_EqualCurrentBehavior` 加 `task.reminderLeadDays` `"1"`、`task.remindOverdue` `"true"`、`task.notify.commented` `"assignee,creator"`、completed／cancelled `"creator"`、`notification.retentionDays` `"90"`、`calendar.weekStart` `"monday"`；`WeekStartOf("sunday") == DayOfWeek.Sunday`。
- [ ] **Step 2: 跑測試確認失敗**：`cd poc/backend && dotnet test tests/DynamicModules.DomainTests --filter "FullyQualifiedName~Policies"`，Expected FAIL
- [ ] **Step 3: 加登記、fixture、四語文案**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；`cd poc/frontend && bunx vitest run src/domain/dynamic/policy-i18n.vitest.spec.ts src/i18n --maxWorkers=2`
- [ ] **Step 5: Commit**（`feat(policy): 登記任務與通知政策`）

### Task 13: A7「本週」起始日

**Files:**
- Modify: `src/DynamicModules.Application/Dynamic/TaskService/TaskHandlers.cs:125,148,157-159`
- Modify: `src/DynamicModules.Domain/Reports/ReportDates.cs:5,9,46,48`、`src/DynamicModules.Domain/Reports/ReportCardMath.cs:10-12`
- Modify: `src/DynamicModules.Application/Dynamic/ReportService/ReportRunner.cs:27,180`、`src/DynamicModules.Application/Dynamic/ReportService/ReportRunner.Card.cs:51`
- Modify（既有測試）: `tests/DynamicModules.DomainTests/Reports/ReportDatesTests.cs:11`（helper 傳 `DayOfWeek.Monday`）、`tests/DynamicModules.DomainTests/Reports/CardOptionsTests.cs:13,21,29,37,39,41`（`PreviousWindows` 加 `DayOfWeek.Monday`）
- Test: 上兩個檔加案例；`tests/DynamicModules.ApplicationTests/Dynamic/Tasks/TaskQueryTests.cs`；報表整合測試加在 grep `"thisWeek"` 找到的既有報表測試類別

**Interfaces:**
- Consumes: `PolicyCatalog.CalendarWeekStart`、`WeekStartOf`、`IPolicyReader.ChoiceAsync(def, null, ct)`。
- Produces:
  - `ReportDates.Resolve(ReportDateRange r, DateOnly basis, DayOfWeek weekStart)`；`WeekStart(DateOnly d, DayOfWeek weekStart) => d.AddDays(-(((int)d.DayOfWeek - (int)weekStart + 7) % 7))`；類別註解改成「一週起始日由呼叫端傳入（政策 calendar.weekStart）；週別分桶仍採 ISO 8601」。
  - `ReportCardMath.PreviousWindows(ReportDateRange range, DateOnly basis, int count, DayOfWeek weekStart)`。
  - `GetMyTasksHandler` 注入 `IPolicyReader`；`internal static DateOnly WeekEnd(DateOnly today, DayOfWeek weekStart)`＝本週起始日＋6 天。
  - `ReportRunner` 注入 `IPolicyReader`，在解析區間前讀一次 weekStart，傳給 `Resolve` 與 `PreviousWindows`。

- [ ] **Step 1: 寫失敗的測試**
  - `ReportDatesTests.WeekStartsOnSunday`：basis 2026-11-05（週四）、Sunday → thisWeek `(2026-11-01, 2026-11-07)`、lastWeek `(2026-10-25, 2026-10-31)`、lastWeeks n=2 `(2026-10-25, 2026-11-07)`；basis 2026-11-01（週日）Monday → thisWeek `(2026-10-26, 2026-11-01)`、Sunday → `(2026-11-01, 2026-11-07)`。
  - `CardOptionsTests.PreviousWindows_FollowWeekStart`：thisWeek、basis 2026-11-05、1 期、Sunday → `[(2026-10-25, 2026-10-31)]`。
  - `TaskQueryTests.MyTasks_ThisWeek_FollowsWeekStartPolicy`：沿用 `MyTasks_TabsAndFilters` 的資料（今天 2026-11-04 週三），admin `UpdateSystemPolicyRequest("calendar.weekStart", J("\"sunday\""))` 後 `Mine("sales01","assigned","thisWeek")` 只剩 `D(11, 4)`（週六 11-07 結束）；原測試的 `D(11,4), D(11,8)` 不改。
  - 報表整合：一份有主要日期欄位、區間 `thisWeek` 的報表，資料在 2026-11-01 與 2026-11-08；週一起始時含 11-08 不含 11-01，改 sunday 後相反。
- [ ] **Step 2: 跑測試確認失敗**：`cd poc/backend && dotnet test tests/DynamicModules.DomainTests --filter "FullyQualifiedName~ReportDatesTests|FullyQualifiedName~CardOptionsTests"`，Expected 編譯失敗
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；`dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~TaskQueryTests|FullyQualifiedName~<報表測試類別>|FullyQualifiedName~ReportSqlEquivalence"`
- [ ] **Step 5: Commit**（`feat(policy): 本週起始日改讀政策（A7）`）

### Task 14: A6 任務提醒

**Files:**
- Modify: `src/DynamicModules.Domain/Repositories/Dynamic/ITaskRepositories.cs:34-41`
- Modify: `src/DynamicModules.Persistence/Repositories/Dynamic/TaskRepositories.cs:102-128`
- Modify: `src/DynamicModules.Application/Dynamic/NotificationService/TaskReminderSource.cs`
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/Tasks/NotificationApiTests.cs`（`SeedTask` 加選填 `long? moduleId` 參數）

**Interfaces:**
- Consumes: `PolicyCatalog.TaskReminderLeadDays`、`TaskRemindOverdue`、`IPolicyReader`。
- Produces:
  - 刪除 `AnyReminderDueAsync`、`ListReminderDueAsync`，改為 `Task<List<TaskItem>> ListReminderCandidatesAsync(string userId, DateOnly today, int maxLeadDays, bool track, CancellationToken ct)`：負責人、未完成、DueDate 不為 null、所屬模組沒關閉，而且 `(today <= DueDate <= today+maxLeadDays 且 LastDueSoonNotified != today)` 或 `(DueDate < today 且 LastOverdueNotified != today)`；依 Id 排序。
  - `TaskReminderSource(ITaskRepository tasks, INotificationRepository notifications, IBusinessTime time, IPolicyReader policies)`：候選用 `maxLeadDays = PolicyCatalog.TaskReminderLeadDays.Max`；逐筆以 `task.ModuleId` 讀 leadDays 與 remindOverdue；`DueDate == today.AddDays(leadDays)` 補 TaskDueSoon，`remindOverdue && DueDate < today` 補 TaskOverdue。`AnyDueAsync` 用 `track: false` 跑同一判斷，`AddDueAsync` 用 `track: true`。
  - 類別註解把「明天到期」改成「到期日正好是今天＋提醒天數（政策 task.reminderLeadDays）」。

- [ ] **Step 1: 寫失敗的測試**
  - `LeadDays_ModuleOverride_RemindsThatManyDaysBefore`：模組覆寫 3；該模組任務到期 11-07 → 一則 TaskDueSoon；同模組到期 11-05 → 沒有；沒有模組的任務到期 11-05 → 有（全系統值 1）。
  - `RemindOverdue_Off_NoOverdueReminder`：全系統 `task.remindOverdue` false → 逾期任務不補 TaskOverdue；`AnyDueAsync` 只因逾期任務時回 false（不觸發 upkeep）。
  - 既有 `Reminders_AtMostOncePerDay`、`Overdue_NextDay_RemindsAgain`、`Reminder_UsesBusinessDate` 不改斷言。
- [ ] **Step 2: 跑測試確認失敗**：`cd poc/backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~NotificationApiTests|FullyQualifiedName~NotificationReminderSourceTests"`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**（同 Step 2，加 `--filter "FullyQualifiedName~TaskFollowUpTests"`）
- [ ] **Step 5: Commit**（`feat(policy): 任務提醒天數與逾期提醒改讀政策（A6）`）

### Task 15: A13 通知收件角色、A14 保留天數

**Files:**
- Modify: `src/DynamicModules.Application/Dynamic/NotificationService/TaskNotificationWriter.cs`
- Modify: `src/DynamicModules.Application/Dynamic/NotificationService/NotificationModels.cs:17-21`（刪 `RetentionDays`，`MaxTake` 是技術限制留著）
- Modify: `src/DynamicModules.Application/Dynamic/NotificationService/NotificationHandlers.cs:11-58`
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/Tasks/TaskNotificationTests.cs`、`NotificationApiTests.cs`

**Interfaces:**
- Consumes: `PolicyCatalog.TaskNotifyCommented`／`Completed`／`Cancelled`、`NotificationRetentionDays`、`IPolicyReader.ListAsync`、`IntAsync`。
- Produces:
  - `TaskNotificationWriter(INotificationRepository notifications, IBusinessTime time, IPolicyReader policies)`；`NotifyAsync` 改真正 async。Assigned、Mentioned 照舊；Commented、Completed、Cancelled 依該任務 `ModuleId` 的角色組合取 `{ assignee → Task.AssigneeUserId, creator → Task.CreatedBy }`，空值略過；Commented 照舊扣掉這則留言已 @ 到的人；一律排除 `ActorUserId`、去重。類別註解改成「收件角色由政策 task.notify.* 決定」。
  - `internal static async Task<DateTime> CutoffAsync(IBusinessTime time, IPolicyReader policies, CancellationToken ct)`＝`time.Now.AddDays(-retentionDays)`；`GetNotificationsHandler`、`EnsureNotificationUpkeepHandler` 注入 `IPolicyReader` 改用它。

- [ ] **Step 1: 寫失敗的測試**
  - `TaskNotificationTests.Comment_RecipientsFollowModulePolicy`：模組覆寫 `task.notify.commented` `["creator"]` → 第三人留言後只有建立者收到 TaskCommented，負責人沒有。
  - `TaskNotificationTests.Comment_EmptyRoles_NobodyGetsCommented_MentionsStillSent`：覆寫 `[]`，留言 @ 負責人 → 負責人只收到 TaskMentioned。
  - `TaskNotificationTests.Complete_NotifiesAssigneeToo_WhenPolicySaysSo`：覆寫 `task.notify.completed` `["assignee","creator"]`，admin 完成別人建立、別人負責的任務 → 兩人都收到 TaskCompleted。
  - `NotificationApiTests.Purge_FollowsRetentionPolicy`：全系統 `notification.retentionDays` 30 → 31 天前的通知被清、29 天前的保留；既有 `Purge_Older90Days` 不改。
- [ ] **Step 2: 跑測試確認失敗**：`cd poc/backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~TaskNotificationTests|FullyQualifiedName~NotificationApiTests"`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**（同 Step 2，加 `--filter "FullyQualifiedName~TaskCommentTests|FullyQualifiedName~TaskFollowUpTests"`）
- [ ] **Step 5: Commit**（`feat(policy): 任務通知收件人與通知保留天數改讀政策（A13、A14）`）

### Task 16: p1c 文件、實機與 gate

**Files:**
- Modify: `docs/manual.md:732`（報表「本週從週一開始」改成「依政策設定，出廠週一」）、`:886`（本週到期同上）、§16 站內通知（保留天數、即將到期天數、逾期提醒開關、收件人表格改寫成「出廠值，可在政策設定調整」）
- Modify: `docs/superpowers/specs/2026-11-11-policy-settings-design.md`（§10 技術限制補 p1c 範圍；可寫死清單補「ISO 週分桶格式」；A13 只開放三種事件的說明）、路線文件、功能清單

- [ ] **Step 1: 實機**：四種寬度開 `/system/policies`，Boolean、Choice、RoleSet 三種編輯器各存一次、恢復出廠值一次；模組政策分頁覆寫一個 RoleSet。
- [ ] **Step 2: gate**（同 Task 6 Step 6）
- [ ] **Step 3: Commit**（`docs: 任務與通知政策手冊與 p1c 進度`）

---

# p1d 附件：A20

### Task 17: 部署設定檔當出廠值、縮小 AttachmentSettings

**Files:**
- Modify: `src/DynamicModules.Domain/Policies/PolicyCatalog.cs`（加 4 項附件政策）、`poc/shared/policy-catalog.json`（codes 加 4 個、categories 加 `attachment`）
- Modify: `src/DynamicModules.Application/Dynamic/PolicyService/PolicyEnvironmentDefaults.cs`（加 `Build`）
- Modify: `src/DynamicModules.Application/Dynamic/AttachmentService/AttachmentSettings.cs`（record 縮成 `AttachmentSettings(string Root, bool UploadEnabled, string? DisabledReason)`；刪 `DefaultMaxBytes`、`DefaultMaxFilesPerField`、`DefaultExtensions`、`IsAllowed`；`AttachmentSettingsResponse` 形狀不變、刪 `From`）
- Modify: `src/DynamicModules.Application/Dynamic/AttachmentService/AttachmentHandlers.cs:16-19`（`GetAttachmentSettingsHandler` 從讀值服務取全系統值組回應）
- Modify: `src/DynamicModules.WebAPI/Attachments/AttachmentSettingsFactory.cs`、`src/DynamicModules.WebAPI/Program.cs:51-54`
- Modify: `poc/frontend/src/i18n/{zh-TW,en-US,th,vi}.json`
- Modify（既有測試）: `tests/DynamicModules.ApplicationTests/Dynamic/DesignerTestBase.cs:74-75`、`tests/DynamicModules.ApplicationTests/Dynamic/Demo/DemoTestHost.cs:55-56`（新建構子）、`tests/DynamicModules.ApplicationTests/Dynamic/Tasks/TaskAttachmentFileTests.cs:46-47`（另註冊 `new PolicyEnvironmentDefaults(new Dictionary<string,string>{ ["attachment.maxFileBytes"] = "1024" })`）、`tests/DynamicModules.ApplicationTests/Dynamic/AttachmentRulesTests.cs:52`（改斷言 `PolicyCatalog.AttachmentAllowedExtensions.Factory.Split(',')`）
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/Policies/PolicyEnvironmentDefaultsTests.cs`、`tests/DynamicModules.WebAPITests/Dynamic/AttachmentApiTests.cs`（加兩個 Fact）

**Interfaces:**
- Produces:
  - `public static PolicyEnvironmentDefaults Build(IEnumerable<(PolicyDefinition Def, string Raw)> configured, List<string> warnings)`：`Normalize` 成功直接用；否則 `Clamp`，成功記 `"部署設定 {code} 的值「{raw}」超出範圍，改用 {clamped}"`，失敗記 `"部署設定 {code} 的值「{raw}」無法使用，改用程式出廠值"` 並略過。
  - `AttachmentSettingsFactory.Create(IConfiguration config, string dynamicConnection)` 改回傳 `(AttachmentSettings Settings, PolicyEnvironmentDefaults Defaults, IReadOnlyList<string> Warnings)`；只有設定檔真的有寫的鍵才放進 configured：`Attachments:MaxBytes` → `attachment.maxFileBytes`、`Attachments:MaxFilesPerField` → `attachment.maxFilesPerField`、`Attachments:AllowedExtensions`（陣列以逗號串起）→ `attachment.allowedExtensions`。
  - Program：註冊 `Settings` 與 `Defaults` 兩個 singleton；`builder.Build()` 之後對每則 warning `app.Logger.LogWarning("{Warning}", w)`。
  - 四語文案（zh-TW）：`attachment.maxFileBytes` 單檔大小上限（位元組）／10485760 位元組是 10 MB；超過大小的檔案不能上傳。`attachment.maxFilesPerField` 每個附件欄位的檔案數上限／已超過上限的記錄照常可看、可刪檔，只是不能再上傳。`attachment.allowedExtensions` 允許的副檔名／沒列出的一律拒絕上傳；縮小清單不影響已上傳的檔案。`attachment.maxFilesPerComment` 每則留言的附件數上限／一則留言一次最多附幾個檔案。

- [ ] **Step 1: 寫失敗的測試**
  - `PolicyEnvironmentDefaultsTests`：`("attachment.maxFileBytes","0")` → 值 `"1"`、一則 warning；`"abc"` → 用程式出廠值、一則 warning；`".PDF,png"` → `"pdf,png"`、沒有 warning；`"10"` → `"10"`（既有測試的 10 byte 設定照常可用）。
  - `AttachmentApiTests.Settings_ConfigValueIsEnvironmentFactory`：`_app.Settings["Attachments:MaxFilesPerField"] = "2"` → admin `GET system/policies` 中 `attachment.maxFilesPerField` 的 `factory` 為 2；`Settings_ConfigOutOfRange_Clamped`：`Attachments:MaxBytes = "999999999"` → `factory` 為 52428800。
  - `Factories_EqualCurrentBehavior` 加四項出廠值。
- [ ] **Step 2: 跑測試確認失敗**：`cd poc/backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~PolicyEnvironmentDefaultsTests|FullyQualifiedName~AttachmentRulesTests"`
- [ ] **Step 3: 實作**（此時 handler 還在用舊欄位的地方先改讀讀值服務，細節在 Task 18；本 Task 結束時要能編譯，可把 Task 17、18 一起提交）
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~AttachmentApiTests"`；`cd poc/frontend && bunx vitest run src/domain/dynamic/policy-i18n.vitest.spec.ts src/i18n --maxWorkers=2`
- [ ] **Step 5: Commit**（與 Task 18 合併提交，見 Task 18 Step 5）

### Task 18: 上傳與留言改讀政策、filter 改順序

**Files:**
- Modify: `src/DynamicModules.Application/Dynamic/AttachmentService/AttachmentHandlers.cs:28-78,84-104`（`AttachmentUploadGate`、`CheckAttachmentUploadHandler`、`UploadAttachmentHandler`）
- Modify: `src/DynamicModules.Application/Dynamic/TaskService/TaskCommentHandlers.cs:30-80,183-187`（刪 `TaskCommentRules.MaxFiles`，`BodyMax`、`ContentTypeMax` 是技術限制留著）
- Modify: `src/DynamicModules.Application/Dynamic/TaskService/TaskMessages.cs`（`TooManyFiles` 改成 `TooManyFiles(int max)`）
- Modify: `src/DynamicModules.WebAPI/Controllers/v3/Dynamic/UploadGuard.cs`、`src/DynamicModules.WebAPI/Controllers/v3/Dynamic/TaskCommentUploadGuardFilter.cs`
- Modify（既有測試）: `tests/DynamicModules.WebAPITests/Dynamic/AttachmentApiTests.cs:427-431`（`ApplyBodyLimit(http, 1000L)`）
- Test: `AttachmentApiTests.cs`、`TaskCommentAttachmentsApiTests.cs`、`tests/DynamicModules.ApplicationTests/Dynamic/Tasks/TaskAttachmentFileTests.cs`

**Interfaces:**
- Consumes: Task 17 的四個政策、`IPolicyReader`。
- Produces:
  - `public sealed record UploadBodyLimit(long MaxBytes, int Files);`（`AttachmentService/AttachmentRequests.cs`）
  - `CheckAttachmentUploadHandler` 通過時回 `ServiceResult.Success(new UploadBodyLimit(<模組 maxFileBytes>, 1))`；`CheckTaskCommentUploadHandler` 通過時回 `Success(new UploadBodyLimit(<任務模組 maxFileBytes>, <任務模組 maxFilesPerComment>))`（`task.ModuleId` 為 null 用全系統值）。
  - `UploadGuard.ApplyBodyLimit(HttpContext http, long maxBytes, int files = 1)`。
  - 兩個 filter 順序：上傳關閉 409 → mediator 檢查（失敗照舊回應）→ 從 `check.Data` 取 `UploadBodyLimit` 呼叫 `ApplyBodyLimit`（413 照舊）→ `next()`。路由值解析不出來時用 `PolicyCatalog.AttachmentMaxFileBytes.Max` 與 `AttachmentMaxFilesPerComment.Max` 當上限。
  - `UploadAttachmentHandler`：副檔名用 `ListAsync(AllowedExtensions, null)`（大小寫不分）；數量 `existing.Count >= maxFilesPerField(module)` 擋（既有檔案超標時仍擋新增，不擋刪除）；大小 `store.SaveAsync(..., maxFileBytes(module), ...)`。
  - `AddTaskCommentHandler`：檔數 `> maxFilesPerComment(task.ModuleId)` 回 `TooManyFiles(max)`（`"一則留言最多 {max} 個附件"`，維持現有文字形狀）；副檔名、大小同上。

- [ ] **Step 1: 寫失敗的測試**
  - `AttachmentApiTests.Upload_ModuleOverrideMaxFiles_UsesOverride`：模組覆寫 `attachment.maxFilesPerField` 1 → 第二次上傳 400。
  - `AttachmentApiTests.Upload_FieldAlreadyOverLoweredLimit_Rejected`（Review Focus 2）：先傳 3 個，覆寫改 2 → 第 4 個 400；記錄照常可讀、刪掉兩個檔（存記錄）後可再傳一個。
  - `AttachmentApiTests.Upload_ModuleOverrideMaxBytes_413WithoutReadingTheBody`（Review Focus 4）：全系統 `maxFileBytes` 不動、模組覆寫 1000 → `UploadProbe` 回 413 且 `BodyBytesRead < ProbeSize / 4`。
  - `AttachmentApiTests.Upload_ExtensionRemovedFromWhitelist_ExistingDownloadStillWorks`：先傳 `a.txt`，系統白名單改 `["pdf"]` → 再傳 txt 400，下載 `a.txt` 200。
  - `TaskCommentAttachmentsApiTests.Comment_ModuleOverrideMaxFiles`：任務所屬模組覆寫 `attachment.maxFilesPerComment` 2 → 3 個檔 400、`errors.files[0]` 含「最多 2 個」；主體上限變成 `1024*2 + 64*1024`。
  - 既有 `Upload_OverMaxBytes_400AndPartialFileRemoved`、`Upload_OverMaxFilesPerField_400`、`Upload_ContentLengthOverTheLimit_413WithoutReadingTheBody`、`Upload_WithoutEditPermission_RejectedBeforeTheBodyIsRead`、`Comment_BodyOverLimit_413`、`Upload_AccessCheckedBeforeBody` 不改斷言。
- [ ] **Step 2: 跑測試確認失敗**：`cd poc/backend && dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~AttachmentApiTests|FullyQualifiedName~TaskCommentAttachmentsApiTests"`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；`dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~Attachment|FullyQualifiedName~TaskComment|FullyQualifiedName~Demo"`；`grep -rn "MaxFilesPerField\|DefaultMaxBytes\|TaskCommentRules.MaxFiles" src` 只剩政策相關的命名
- [ ] **Step 5: Commit**（Task 17＋18 一起：`feat(policy): 附件上限改讀政策，部署設定檔當出廠值（A20）`）

### Task 19: 留言輸入框改讀生效值

**Files:**
- Modify: `poc/frontend/src/presentation/dynamic/tasks/MentionInput.vue:9-13,57,71-72,115,122`（刪 `MAX_FILES`；新 prop `moduleKey: string | null`）
- Modify: `poc/frontend/src/presentation/dynamic/tasks/TaskDialog.vue:137`（傳 `:module-key="data.record?.moduleKey ?? null"`）
- Modify: `poc/frontend/src/domain/dynamic/types.ts:28-36`（`AttachmentSettings` 的 `maxBytes`、`maxFilesPerField` 加註解：全系統值，模組值看生效值 API）
- Modify（既有測試）: `poc/frontend/src/presentation/dynamic/tasks/TaskDialog.vitest.spec.ts:30-36`（mock 加 `/policies/effective` 回 `{ values: { 'attachment.maxFilesPerComment': o.maxFiles ?? 5, 'attachment.maxFileBytes': o.maxBytes ?? 1048576 } }`）

**Interfaces:**
- Consumes: `useEffectivePolicies(moduleKey)`。
- Produces: `MentionInput` 的檔數上限＝`values['attachment.maxFilesPerComment']`、單檔上限＝`values['attachment.maxFileBytes']`；生效值未載入時附件鈕停用；上傳開關、副檔名仍用 `useAttachmentSettings()`。

- [ ] **Step 1: 寫失敗的測試**：`TaskDialog.vitest.spec.ts` 加 `留言附件上限依任務模組生效值`：`maxFiles: 2` 時選 3 個檔只留 2 個、提示含「最多 2 個檔案」，且 `/policies/effective` 的 `params.module` 是任務的 moduleKey；既有 `選 6 個檔案時只保留 5 個並提示上限` 不改斷言。
- [ ] **Step 2: 跑測試確認失敗**：`cd poc/frontend && bunx vitest run src/presentation/dynamic/tasks/TaskDialog.vitest.spec.ts --maxWorkers=2`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2，加 `bunx vitest run src/pages/tasks/MyTasksPage.vitest.spec.ts src/components/framework/AttachmentInput.vitest.spec.ts --maxWorkers=2`、`bun run typecheck`
- [ ] **Step 5: Commit**（`feat(policy): 留言附件上限改讀生效值`）

### Task 20: p1d 文件、實機與 gate

**Files:**
- Modify: `docs/manual.md:876` 與附件欄位相關段落（上限改為「依政策設定，出廠 …」；說明部署設定檔 `Attachments` 節的值會當出廠值）、`docs/deploy.md`（目前沒有 `Attachments` 節的說明，新增一小段：`Attachments:MaxBytes`、`MaxFilesPerField`、`AllowedExtensions` 是該環境的出廠值，可在政策設定覆蓋；超出技術範圍啟動時記 Warning 並夾值）
- Modify: 設計文件 §10 安全類補「附件危險副檔名黑名單」與附件技術範圍、路線文件 p1d 完成、功能清單 p1 打勾（跑清單腳本）

- [ ] **Step 1: 實機**：四種寬度；在政策設定頁改白名單（CodeList 編輯器）、模組覆寫留言附件數，於任務對話框確認提示文字跟著變。
- [ ] **Step 2: gate**（同 Task 6 Step 6）
- [ ] **Step 3: Commit**（`docs: 附件政策手冊、部署說明與 p1 完成`）

---

## 自我檢查結果

1. **設計覆蓋**：§3.1 查值順序與兩層→Task 3；§3.2 權限→Task 4；§3.3 型別與上下限→Task 1、Global Constraints 表；§3.4 出廠值與附件設定檔→Task 1、17；§3.4.1 收件角色→Task 15；§3.5 改嚴只管新增→Task 5、10、18；§3.6 前端取用→Task 10、19；§5 資料與壞值→Task 2、3；§6 讀值入口→Task 3、5、13–15、18；§7 API→Task 4；§8 兩個畫面→Task 8、9；§9 p1 七項政策→Task 5、6、13、14、15、17–19。A1 的「每條規則產生的任務數」＝`task.maxTasksPerRule`。
2. **步驟掃描**：每個測試步驟列了測試名與關鍵斷言值；程式步驟給簽章與檔案；沒有「處理邊界情況」之類的空話。
3. **型別一致**：`IPolicyReader` 五個方法、`PolicyResolution`、`PolicyEnvironmentDefaults.FactoryOf/Build`、`UploadBodyLimit`、`ValidateAsync(..., TaskRule? existing, ...)`、前端 `validateDraft(..., limits)` 在使用處名稱一致。
4. **Review Focus**：五條都有指定的測試與所屬 Task。
5. **比例**：計畫只放簽章與斷言，沒有函式本體；比設計長，主因是逐檔列出要改的既有測試。

**設計本身的不一致：** 原先指出的 A13（§4 與 §3.4.1）、A2（沿用 A1 會改變上線行為）兩處，設計已在 2026-11-11 修正（§4、§12 第 7 條），本計畫已照修正後的版本寫（判斷 10、12、23）。目前沒有其他需要使用者決定的矛盾。
