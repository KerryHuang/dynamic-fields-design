# 設計與授權分成兩條權限軸 設計文件（段 權限軸）

> 2026-11-19｜平台共用能力（非應用程式）｜前置：[核心設計](2026-11-03-dynamic-modules-design.md) §15「上線前必做：拆開設計與授權」、[共用主檔](2026-11-10-shared-master-design.md) §6、[維運與觀測](2026-11-11-ops-observability-design.md) §17（稽核表、安全事件）｜相關：[MCP server](2026-11-11-mcp-server-design.md)（mcp1a／mcp1b「不提供給 AI」旗標、mcp3 設計器工具）、[應用程式套件](2026-11-06-app-package-design.md)（pkg1 套件更新）
> 狀態：主控撰寫（2026-11-19），這次只寫設計、不改程式。路線位置照使用者 2026-11-19 裁決：O2 之後、p2 之前，分權限軸-a（後端）、權限軸-b（畫面）兩個子單元（§19）。
> 標記：「（使用者裁決）」是使用者已拍板的；「（顧問裁決，待使用者確認）」是 2026-11-19 顧問分流 B 區照推薦採用的；「（主控裁決，待使用者確認）」是本設計延伸或推翻顧問與分流結論的行為類決定；「（T#）」是技術類決定，主控可推翻。全部彙整在 §20。

## 1. 目的與為什麼

現在「改模組設計」和「決定誰看得到、改得到資料」是同一個權限：持有設計器代碼 `DYN00000` 修改權的人，就能在模組的「權限」分頁替自己開任何模組的全部權限，包括金額、解除欄位隱藏（核心設計 §15「上線前必做」，`SaveModulePermissionsRequest` 只要求設計器修改）。

這段把兩件事拆成兩個可分別授予的系統權限：

- **設計器**（既有 `DYN00000`，讀取／新增／修改／刪除）：建模組、欄位、規則、流程、報表、檢視。
- **權限設定**（新 `SYS00001`）：決定誰能看、能做什麼。

例：公司讓 IT 小王改表單，但不讓他決定業務能不能看單價。拆軸後小王有設計器修改、沒有權限設定，他看得到權限分頁但改不了。

## 2. 目標、不做、反目標

**目標**

1. 新系統權限「權限設定」，與設計器分開授予；系統管理員自動擁有兩者（§5）。
2. 所有寫入授權資料的路徑改要求「權限設定」，fail closed（§7）。
3. 系統管理員能在群組頁、使用者頁授予這兩個系統權限（今天沒有任何畫面能授予設計器權限）。
4. 授予或收回這兩個權限記稽核、發安全事件，比照 `admin.changed`（§9）。
5. 升級時沒有人突然失去或得到能力（§6.3）。
6. 把已知的「設計者繞過授權取得資料」路徑查清楚，能便宜堵的堵、堵不了的寫進手冊已知限制（§12、§13）。

**不做**

- 依部門的資料範圍（HRM 段）。
- 有效權限快取（核心設計 §15 另一條上線前必做）。
- 權限設定分模組或分應用程式授予（顧問裁決權限軸-4，§20.2）。
- 把設計器代碼 `DYN00000` 改名（只是代碼，改名要搬資料，沒有收益）。
- 設計者行為的「雙人覆核」或核准流程。

**反目標（做了也算失敗）**

- 設計者能經任何 API（含直接呼叫、從範本建立、產生 Demo 資料）寫入群組權限、個人權限、欄位權限、選項範圍或貢獻欄位可見群組，而沒有「權限設定」。
- 權限設定者能把設計器或權限設定授予任何人（含自己）。
- 守衛只做在前端；或新請求忘了宣告就預設放行。
- 升級後既有群組的成員默默失去一直在用的能力。

## 3. 已定決定

- 另立專管權限設定的代碼（使用者 2026-11-11 裁決，`docs/feature.md` 該項）。
- 開啟、關閉、重新開啟、刪除應用程式要「系統管理員＋設計器對應動作」（套件設計 §12 決策 7）。
- MCP 高風險地板含「改權限」（MCP 設計 §6.4，C7 使用者 2026-11-12 裁決）。
- 排在 O2 之後、p2 之前，約兩個子單元（使用者 2026-11-19 裁決，回答分流權限軸-漏1）。
- 歸「授權」、要有權限設定才能改：模組權限分頁四塊（群組動作與資料範圍、個人權限、欄位權限、選項範圍）、共用主檔貢獻欄位的開放、開啟套件時的群組對應、「不提供給 AI」欄位旗標（mcp1b 起）。仍歸「設計」：簽核人規則、狀態轉移的群組限制、共用檢視。設計者把自己設成簽核人取得臨時讀取權、彙總欄位不看可見性兩條路徑寫進手冊已知限制，稽核可查（使用者 2026-11-19 裁決，回答分流權限軸-3 選 A）。

## 4. 現況（2026-11-19 main，5cb719e8）

- 設計器權限是 `Dynamic_Program` 一列 `DYN00000`，授權存在 `Dynamic_GroupProgramPrivilege`／`Dynamic_UserProgramPrivilege`（和業務模組同兩張表，`ProgramCode` 長度 8）。`SeedData.EnsureSeededAsync` 冪等建立它，並把 `DYN00000` 以外的 program 依模組狀態同步 `Active`（`SeedData.cs:38`）。
- 業務模組代碼由 `IProgramRepository` 從 `DYN00001` 起編，核心設計 §15 原本提議的 `DYN00001` 已被佔用。
- 守衛：`IDesignerRequest`＋`DesignerAccessBehavior`（宣告動作、回傳型別非 `ServiceResult` 就丟例外）、`ISystemAdminRequest`＋`SystemAdminBehavior`（啟用中的系統管理員）、`IMasterDesignRequest`＋`MasterDesignerBehavior`（動到共用主檔模組另要系統管理員）。管線順序在 `Application/DependencyInjection.cs`：Designer → SystemAdmin → MasterDesigner → Validation → … → Transaction → Audit。
- 系統管理員：`PermissionResolver.Resolve` 對管理員回全部權限；`PermissionResolver.ResolveProgram`（`PermissionResolver.cs:88-89`）對管理員回設計器全部動作。手冊 §18「誰能做」（`docs/manual.md:1185`）卻寫「系統管理員身分不會自動帶設計器權限，設計器權限要由群組授予」，**手冊錯，程式對**（§5 定案，手冊待改，§21）。
- 沒有任何畫面能授予 `DYN00000`：權限分頁只列業務模組，群組頁、使用者頁沒有系統權限區塊，Demo 也沒給任何人。實際上今天只有系統管理員能設計。
- 寫入授權資料的路徑（全部查過，`grep ReplaceModuleRowsAsync|SaveModulePermissionsRequest|GroupProgramPrivilege`）：
  - `SaveModulePermissionsRequest`（`PUT modules/{key}/permissions`）：四塊加貢獻欄位手動可見群組（`FieldGrants`），宣告設計器修改＋主檔守衛。
  - `TemplateInstantiator` 第 10 步經 mediator 送 `SaveModulePermissionsRequest`（範本的群組權限建議；開啟套件時另帶欄位例外與選項範圍）。
  - `AppOpener`（`OpenApp.cs:229`）直接寫貢獻欄位例外；`MasterGrants` 重算主檔群組權限。都在系統管理員才能送的開啟、關閉、重新開啟、刪除應用程式之內。
  - 產生 Demo 資料：`StartDemoDataRequest` 只要設計器新增＋修改，內層以 `ActingScope.Begin("admin")` 用管理員身分跑，會開套件並寫欄位權限。**這是今天設計者間接寫授權的路徑**。
  - 群組刪除會一併刪授權列（系統管理員）。
- 讀取：`GetModulePermissionsRequest`、`GetGroupsRequest`、`GetUsersRequest` 宣告設計器讀取。
- 前端：`PermissionsTab.vue` 以 `me.designer.edit` 切唯讀；選單 `menu-nav.ts` 以 `designer.read` 顯示設計器、`isSystemAdmin && designer.read` 顯示應用程式。
- 稽核：`AuditBehavior` 只記成功（O2-U9，被權限擋下的不記）。`permission.update` 只記變動的格子。系統管理員旗標異動由 handler 呼叫 `AuditContext.MarkAdminChanged`，`AuditBehavior` 寫完稽核後同交易呼叫 `SecurityIncidents.AdminChangedAsync`（事件 `admin.changed`）。

## 5. 系統管理員與授予

- **系統管理員自動擁有設計器全部動作與權限設定**（顧問裁決權限軸-2，待使用者確認）。符合現行程式與「空系統是合法起點」：全新安裝的第一個管理員開箱就能設計、授權，沒裝任何應用程式也一樣。代價：做不出「只管帳號、不碰設計」的管理員；之後要做，改 resolver 一行＋測試，不改表。
- **只有系統管理員能授予或收回設計器與權限設定**（顧問裁決權限軸-1，待使用者確認）。權限設定者能改模組權限，但不能把這兩個權限給任何人；授權的最上層只有一個角色，才不會繞回自我提權。群組成員與使用者的群組本來就只有系統管理員能改（`SetGroupMembers`、`UpdateUser` 宣告 `ISystemAdminRequest`），所以權限設定者也無法把自己加進持有設計器的群組。
- **授予對象是群組或個人**（主控裁決 M7，待使用者確認）：沿用業務模組的群組權限＋個人權限兩張表，有效權限是兩邊逐項合併。理由：小公司常常只有一個人管權限，為一個人開群組多一步；表本來就支援。
- **權限設定隱含設計器讀取**（主控裁決 M1，待使用者確認）：有權限設定的人，設計器動作至少有「讀取」，才進得了設計器的模組清單與權限分頁。設計器讀取只看得到定義（模組、欄位、規則、流程），看不到資料；而權限設定者本來就能替自己開模組讀取（顧問裁決權限軸-漏2），不會多開一條看資料的路。替代做法是「勾權限設定時必須同時勾設計器讀取」，多一條驗證、多一個會忘記的步驟，不採用。
- **權限設定者可以改自己所在群組或自己的個人權限**（顧問裁決權限軸-漏2，待使用者確認），記稽核、不另發通知。拆軸要擋的是「設計者 → 授權」，不是「授權者 → 自己」。
- **共用主檔模組的權限維持只有系統管理員能改**（顧問裁決權限軸-漏3，待使用者確認）：`SaveModulePermissionsRequest` 繼續宣告 `IMasterDesignRequest`，權限設定者對主檔模組也是 403。

## 6. 資料與遷移

### 6.1 新代碼

- `Dynamic_Program` 新增一列 `SYS00001`，名稱四語「權限設定／Permission settings／การตั้งค่าสิทธิ์／Cài đặt quyền」，`Active = true`（T1）。前綴 `SYS` 不會撞到 `IProgramRepository` 的 `DYN` 流水號；長度 8 符合欄位上限。
- 只用 `CanEdit` 一個位元表示「有權限設定」，其餘六個位元一律寫 false、讀時忽略（T2）。不另開表、不加欄位，沿用 `LoadProgramInputsAsync` 與個人權限合併規則。
- 代碼集中成一個常數類別 `SystemProgramCodes`（`Designer = "DYN00000"`、`PermissionAdmin = "SYS00001"`、`All`），放 Domain（Persistence 的 `SeedData` 與 Application 都要用），取代 `DynamicAccessGuard.DesignerProgramCode` 與 `SeedData` 的字面值（T3）。

### 6.2 SeedData

- `EnsureSeededAsync` 冪等建立 `SYS00001`（同 `DYN00000` 的寫法），全新安裝與舊庫都會有。
- 依模組同步 `Active` 的那段改成排除 `SystemProgramCodes.All`；否則 `SYS00001` 沒有對應模組，每次啟動都會被設成 `Active = false`（雖然系統代碼不看 `Active`，但狀態錯就是錯）。

### 6.3 Migration：既有持有者怎麼帶入

**原則：升級前後，每個人能做的事完全一樣。**

升級前，能存模組權限的人＝系統管理員＋持有 `DYN00000` 修改權的群組成員與個人。拆軸後，前者自動擁有權限設定；後者若不帶入，就會突然失去「改權限」這個今天在用的能力。所以 migration 照下面做（主控裁決 M2，待使用者確認；延伸分流 D5「不自動給任何人」——D5 的前提是沒人被授予過 `DYN00000`，在 Demo 與目前所有部署成立，但 migration 不能靠這個前提，要對任何資料庫都保持能力不變）：

1. 新 migration `PermissionAxes`，SQLite（`Persistence/Migrations`）與 PostgreSQL（`Persistence.Postgres/Migrations`）各一份，只有資料、沒有 schema 變更：
   1. `Dynamic_Program` 沒有 `SYS00001` 就插入（與 6.2 重複無妨，兩邊都冪等）。
   2. 每一列 `Dynamic_GroupProgramPrivilege` 中 `ProgramCode = 'DYN00000' AND CanEdit = true` 的群組，若還沒有 `SYS00001` 列，插入 `(GroupId, 'SYS00001', CanEdit = true, 其餘 false)`。
   3. `Dynamic_UserProgramPrivilege` 同上（以 `UserId`）。
   4. `Down` 刪掉所有 `SYS00001` 的授權列與 program 列。
2. 不動系統管理員、不動 `DYN00000` 本身的任何列、不動業務模組的任何列。
3. 結果：持有設計器修改權的人照舊能改權限（因為他們被帶入權限設定），沒有設計器修改權的人照舊不能。**沒有人得到新能力**：被帶入的人本來就能改權限；「權限設定隱含設計器讀取」（M1）對被帶入的人也不是新能力——持有修改權的群組在今天的資料裡沒有只給修改不給讀取的情況（畫面不存在，只有測試直接寫庫）。
4. 帶入不發安全事件（migration 不經 MediatR，也沒有行為者）；手冊在升級說明寫「升級後到群組頁、使用者頁檢查誰有權限設定，需要拆開的就取消」（§21）。
5. 同一版程式碼另有兩處行為改變也是「不增不減」：
   - 從範本建立：今天沒有設計器修改權的人送出含權限建議的範本，內層 `SaveModulePermissions` 會 403、整批回滾；拆軸後改成略過權限建議並警告（§7.3、M3）。這讓「有設計器新增、沒有修改」的人從失敗變成成功建出沒有授權的模組，沒有多開授權能力。
   - 產生 Demo 資料改要權限設定（§7.3、M4）：今天能產生的人＝設計器新增＋修改；被 migration 帶入權限設定的正好是修改權持有者，所以能產生的人不變。

## 7. 後端守衛

### 7.1 新介面與 behavior（權限軸-a）

- `IPermissionAdminRequest`（空介面，`Common/Security/`）：送出的人必須有權限設定。
- `PermissionAdminBehavior<TReq,TRes>`：照 `DesignerAccessBehavior` 的寫法——不是這個介面就放行；回傳型別不是 `ServiceResult` 就丟 `InvalidOperationException`（fail closed）；`guard.IsPermissionAdminAsync` 為 false 回 `ServiceResult.Forbidden(MessageRef.Of(MessageText.PermissionAdminRequired))`。管線放在 `DesignerAccessBehavior` 之後、`SystemAdminBehavior` 之前（T4）。
- `IDynamicAccessGuard` 加 `Task<bool> IsPermissionAdminAsync(CancellationToken)`：讀 `SYS00001` 的程式權限，`PermissionResolver.ResolveProgram(SYS00001, inputs).Contains(Edit)`；系統管理員為 true；使用者不存在或已停用為 false（T5，fail closed；`DesignerActionsAsync` 一併補「已停用回空集合」，今天停用的人 session 已失效，這只是第二道）。
- `DesignerActionsAsync`：有權限設定時把 `Read` 併進回傳集合（M1）。
- 條件式檢查用 `PermissionAdminGate`（Application 服務，包 `IsPermissionAdminAsync`），給「只有某些請求內容才算授權」的 handler 呼叫（§7.3）；不在 handler 各自讀權限表（T6）。
- **通道**：mcp1a 加入「請求通道」後，`PermissionAdminBehavior` 與 `PermissionAdminGate` 在通道是 MCP 時一律回 false（M8，§14）。權限軸-a 時還沒有通道概念，留成 mcp1a 的接點與測試 PAX-22。

### 7.2 受影響 API 守衛清單

| 請求 | 端點 | 改前 | 改後 |
|---|---|---|---|
| `SaveModulePermissionsRequest` | `PUT modules/{key}/permissions` | 設計器修改＋主檔守衛 | 權限設定＋主檔守衛（拿掉 `IDesignerRequest`） |
| `GetModulePermissionsRequest` | `GET modules/{key}/permissions` | 設計器讀取 | 不變（權限設定隱含讀取） |
| `GetGroupsRequest`、`GetUsersRequest` | `GET groups`、`GET users` | 設計器讀取 | 不變 |
| `StartDemoDataRequest` | `POST demo-data/generate` | 設計器新增＋修改 | 再加權限設定（`IPermissionAdminRequest`） |
| `InstantiateTemplateRequest` | `POST templates/{key}/instantiate` | 設計器新增（內層權限步驟要修改） | 設計器新增；權限步驟改走 gate（§7.3） |
| `OpenAppRequest`、`ReopenAppRequest`、`CloseAppRequest`、`DeleteAppRequest` | `apps/...` | 設計器動作＋系統管理員 | 再加 `IPermissionAdminRequest`（管理員自動有，行為不變） |
| `CreateEventRequest`、`UpdateEventRequest` | `POST events`、`PUT events/{key}` | 設計器修改＋主檔守衛 | 不變；含「允許複製被隱藏的值」時另走 gate（§7.3、M5） |
| `UpdateGroupSystemPrivilegesRequest`（新） | `PUT admin/groups/{id}/system-privileges` | — | 系統管理員 |
| `UpdateUserSystemPrivilegesRequest`（新） | `PUT admin/users/{id}/system-privileges` | — | 系統管理員 |
| `GetMeRequest` | `GET me` | 登入即可 | 不變；回應多 `permissionAdmin` |

表外說明：

- 開啟、關閉、重新開啟、刪除應用程式宣告 `IPermissionAdminRequest` 是冗餘的（管理員自動有），刻意宣告是為了讓「寫授權資料的請求都宣告」這條規則沒有例外；哪天權限軸-2 改成管理員不自動擁有，這四個請求不必再回頭找（T7）。
- `RetryIntegrationRequest`、`SetIntegrationEnabledRequest`、`ImportAppPackageRequest` 不寫授權資料，不宣告。
- 仍歸「設計」、不改守衛的（使用者裁決）：簽核流程（`ApprovalFlowRequests.cs` 的建立、修改、草稿、發布、試算）、狀態機（`SaveWorkflowRequest`，含轉移的群組限制）、共用檢視（清單頁「共用」切換，設計器修改）、任務規則、跨記錄檢查、轉換、列印範本、分錄、欄位與參照帶值、帶表頭的值、公式、彙總。
- 其餘 50 多個 `IDesignerRequest` 不動。

### 7.3 條件式檢查

**從範本建立**（主控裁決 M3，待使用者確認；推翻分流 D4 對範本的部分「要求兩軸都有」）：`TemplateInstantiator` 第 10 步在 `app is null`（從範本建立，不是開啟套件）且 `PermissionAdminGate` 為 false 時，整步略過，在回應既有的 `warnings` 加一則「你沒有『權限設定』權限，已略過範本的權限建議；請洽權限設定者到權限分頁授權」，不送 `SaveModulePermissionsRequest`。理由：範本主要是給設計者用的，內建範本幾乎都帶群組權限建議；D4 的做法會讓沒有權限設定的設計者完全用不了範本，而略過權限只是回到「新模組預設沒人有權限」（顧問裁決權限軸-5），沒有開洞。開啟套件（`app` 不為 null）不略過：照常送出，沒有權限設定就 403、整批回滾（fail closed；實際上開啟要系統管理員，不會發生）。

**產生 Demo 資料**（主控裁決 M4，待使用者確認）：`StartDemoDataRequest` 加 `IPermissionAdminRequest`。它以管理員身分開套件、寫欄位權限與選項範圍，等於批次授權；Demo 站訪客以 admin 登入，不受影響。

**連動事件「允許複製被隱藏的值」**（主控裁決 M5，待使用者確認）：`CreateEventRequest`、`UpdateEventRequest` 的內容只要有任一個對應 `allowHiddenSource = true`，handler 先問 `PermissionAdminGate`，false 就回 403 `EventHiddenSourceNeedsPermissionAdmin`，不寫入。理由：這個旗標字面上就是「允許把某些群組看不到的值複製到別的模組」，連動事件又以系統身分執行（`SystemIdentity`，不看任何人的權限），性質和「不提供給 AI」旗標一樣是放寬可見性的授權決定。判斷只看請求內容、不比對既有定義（T8）：已勾旗標的事件，沒有權限設定的設計者連改其他地方也會被擋，訊息說明原因；fail closed，比「只擋新勾」少一次讀庫和一種繞法（先取消再重勾）。既有能力不變：今天能建這種事件的是設計器修改權持有者，migration 已帶入權限設定（§6.3）。

**「不提供給 AI」欄位旗標**（使用者裁決歸授權；旗標本身在 mcp1a 後端、mcp1b 畫面才出現）：契約寫在這裡，由 mcp1a 照做——`AddFieldRequest` 帶 true、`UpdateFieldRequest` 的值和現存值不同，都要 `PermissionAdminGate` 為 true，否則 403 `PermissionAdminRequired`；套件與範本帶的旗標照開啟／從範本建立的規則（開啟要管理員；從範本建立沒有權限設定時旗標一律寫 false 並加一則 warnings，和 M3 同精神）。

**帶表頭的值依呼叫端可見性**（主控裁決 M6，待使用者確認；修核心設計 §15 第 55 條）：見 §12.2。

### 7.4 回歸防線

`PermissionAdminRequestInventoryTests`（T9）：用反射列出實作 `IPermissionAdminRequest` 的型別，和清單（§7.2 宣告的 6 個）比對；另列出會寫授權表的服務（`IPermissionRepository.ReplaceModuleRowsAsync`、`MasterGrants`、`AppOpener`）的呼叫端類別清單，新增呼叫端時測試失敗，要求作者在清單裡寫明它走哪一道守衛。目的是之後的 pkg1、mcp3 加寫權限的路徑時一定會被提醒。

## 8. API

- `GET /api/v3/dynamic/me`：`MeResponse` 加 `permissionAdmin: boolean`；`designer` 四個布林照有效動作（含 M1 的隱含讀取）。
- `PUT /api/v3/dynamic/admin/groups/{id}/system-privileges`、`PUT /api/v3/dynamic/admin/users/{id}/system-privileges`：
  - 請求與回應：`{ "designer": { "read": bool, "add": bool, "edit": bool, "delete": bool }, "permissionAdmin": bool }`，整份取代。
  - 寫法：`DYN00000` 列照四個布林（全 false 就刪列）；`SYS00001` 列只寫 `CanEdit`（false 就刪列）。同一交易。
  - 宣告：`ISystemAdminRequest`、`ITransactionalRequest`、`IAuditedRequest`（`group.systemPrivileges`／`user.systemPrivileges`）。群組或使用者不存在回 404。
  - 目標是系統管理員也照存（管理員旗標日後取消時才生效），回應照存的值；畫面另外標示「系統管理員自動擁有」（§11）。
- `GET admin/groups`、`GET admin/users`：每一列加 `systemPrivileges`（同上形狀）。
- `GET admin/groups/{id}/impact`：加 `systemPrivileges`，刪除對話框據此提醒「成員會失去：設計器（修改）、權限設定」。
- 沒有新的讀取端點；權限分頁的讀取照舊。

## 9. 稽核與安全事件

**稽核**（都只記成功，照 O2-U9）：

- `permission.update`：行為者改成「權限設定者」語意，內容不變（只記變動的格子）。權限設定者改自己的群組或個人權限也是這一筆，可從「行為者」與格子裡的群組或使用者對出來（顧問裁決權限軸-漏2）。
- `group.systemPrivileges`、`user.systemPrivileges`（新）：`Detail("before")`、`Detail("after")` 記兩邊的系統權限。
- `template.instantiate`：M3 略過權限時 `Detail("permissionsSkipped", true)`。
- `event.create`、`event.update`：既有；含 `allowHiddenSource` 的對應已在定義快照裡，稽核可查誰在何時勾的。
- 簽核流程發布（`approvalFlow.publish`）、狀態機存檔：既有稽核就是「設計者把自己設成簽核人」的查證點（§13 L1）。

**安全事件 `privilege.changed`**（顧問裁決權限軸-6，待使用者確認；觸發點由主控裁決 M9 擴大，待使用者確認）：

- 判斷依「實際持有者」而不是「哪支 API」：有人因為這次請求開始或不再持有設計器任一動作或權限設定，就發一次。例如系統管理員把小王加進持有權限設定的群組，雖然沒呼叫系統權限 API，小王實際上拿到了權限設定，一樣要通知。
- 計算：`SystemPrivilegeHolders.SnapshotAsync()` 回「非系統管理員使用者 → (設計器動作集合, 權限設定)」，不看帳號啟用與否（所以停用、啟用帳號不算異動，不必掛在 `SetUserActive`）。以下 handler 在寫入前後各取一次比對：`UpdateGroupSystemPrivileges`、`UpdateUserSystemPrivileges`、`SetGroupMembers`、`CreateUser`（帶群組）、`UpdateUser`（改群組）、`DeleteGroup`。系統管理員不在快照裡，管理員旗標異動照舊由 `admin.changed` 負責。
- 有差異時 handler 呼叫 `AuditContext.MarkPrivilegeChanged(granted)`（任一人得到就是 granted，只有收回才是 revoked），並 `Detail("privilegeHolders", [...])`（每人 `userId`、`before`、`after`，最多 20 筆，超過記 `truncated`）。
- `AuditBehavior` 寫完稽核、有 Id 之後同交易呼叫 `SecurityIncidents.PrivilegeChangedAsync`，寫法照 `AdminChangedAsync`：指紋 `security`＋`privilege.changed` 聚合成一件、累加次數；每次異動都發站內通知、寫寄送佇列（受 `ops.alertEmailMaxPerHour` 節流，不另設安全專用節流）；樣本只存稽核 Id 與行為者；連結 `/system/audit?id=…`。
- 標題四語（`IncidentTitles.PrivilegeChanged`、`AlertMailText.TitlePrivilegeGranted／Revoked`）：「有人取得設計器或權限設定權限」「有人的設計器或權限設定權限被收回」。
- 一般的模組權限存檔（`permission.update`）只記稽核、不發事件（權限軸-6 選項 A）。

## 10. 錯誤碼

錯誤碼是 `MessageText` 的鍵，HTTP 狀態照 `ServiceResult`；四語文案在 `MessageText.cs`。

| 鍵 | HTTP | 何時 | 繁中文案 |
|---|---|---|---|
| `PermissionAdminRequired`（新） | 403 | `IPermissionAdminRequest` 或 gate 擋下（含 mcp1a 的 AI 旗標） | 修改權限需要「權限設定」權限，請洽系統管理員 |
| `EventHiddenSourceNeedsPermissionAdmin`（新） | 403 | 事件含「允許複製被隱藏的值」而沒有權限設定 | 允許複製被隱藏的值需要「權限設定」權限，請洽系統管理員 |
| `Forbidden`（既有） | 403 | 系統權限 API 不是系統管理員；主檔模組權限不是系統管理員 | 沒有權限執行此操作 |
| `ResourceNotFound`（既有） | 404 | 系統權限 API 的群組或使用者不存在 | 既有文案 |

範本略過權限是成功回應裡的 warnings 文字，不是錯誤碼。

## 11. 畫面（權限軸-b）

- **群組頁編輯對話框**、**使用者頁編輯對話框**：加「系統權限」區塊——設計器「讀取、新增、修改、刪除」四個勾選框，「權限設定（含設計器讀取）」一個開關；說明文字一行「權限設定可以決定誰看得到、改得到資料，但不能授予這兩個系統權限」。存檔時主資料存成功後再送 `system-privileges`（兩個請求；第二個失敗時提示「名稱與成員已儲存，系統權限未儲存：原因」）。使用者是系統管理員時區塊照常可編，上方提示「系統管理員自動擁有設計器與權限設定；以下設定在取消系統管理員後才生效」。
- **群組清單、使用者清單**：加一欄「系統權限」，顯示「設計器」「權限設定」小標籤；手機版卡片同樣顯示。
- **刪除群組對話框**：影響清單多一行「成員會失去：設計器（○○）、權限設定」。
- **模組權限分頁**：唯讀條件改成 `!me.permissionAdmin`（原本是 `!designer.edit`）；唯讀時頂端一行說明「你可以查看權限設定，修改需要『權限設定』權限，請洽系統管理員」，存檔鈕停用。共用主檔模組另外在不是系統管理員時唯讀，說明「共用主檔的權限只有系統管理員能改」（後端早就擋，畫面補上）。
- **連動事件編輯**：沒有權限設定時，「允許複製被隱藏的值」勾選框停用並附說明；事件已勾這個旗標時，存檔鈕停用並說明原因（後端照樣擋）。
- **從範本建立對話框**：沒有權限設定時加一行「範本的權限建議會略過，建好後請權限設定者到權限分頁授權」；成功後照既有方式顯示 warnings。
- **Demo 資料頁**：產生鈕條件加 `permissionAdmin`，沒有時說明原因。
- 選單不改：權限設定者有隱含的設計器讀取，看得到「動態模組設計」。
- 四語文案；390、820、1366、1920 四種寬度不得橫向捲動。

## 12. 「帶值」與其他間接路徑：調查結果與處理

「設計者能不能把自己看不到的值，搬進自己看得到的地方」逐條查程式（2026-11-19 main，依程式閱讀，未另寫測試驗證；權限軸-a 的測試會把結論固定下來）。

### 12.1 參照帶值（`Config.Fill`）

- **證據**：`References/ReferenceFill.cs`。帶值在存檔時以**存檔的人**對來源模組的有效權限複製：先 `guard.AuthorizeAsync(sourceModule, Read)`，沒有讀取權就不帶；逐欄 `sourcePerms.CanSee(from)`，看不到的來源欄位在「鎖定」模式寫成空值、「預設」模式略過。參照鍵也只接受 `ReferenceValidator` 判定存檔者可讀的（`validRefKeys`）。
- **結論：設計者自己存檔拿不到**。他設好帶值後自己新增記錄，看不到的欄位帶進來是空的。
- **但有轉手路徑**：設計者在自己讀得到的模組 B 加一個欄位，帶值對應到模組 A 他看不到的欄位；之後**看得到 A 那個欄位的同事**在 B 選參照時，值會以快照寫進 B，B 的欄位權限預設沒有隱藏，設計者就讀得到。前提是有人在 B 做這個操作。
- **處理**：寫進手冊已知限制（§13 L3）。不在本段防堵：真正的防堵是「目的欄位繼承來源欄位的可見性」，牽涉欄位權限語意，工作量接近一段；帶值設定在欄位存檔稽核裡查得到。

### 12.2 帶表頭的值（`ParentDefaultFieldId`）——本段修掉

- **證據**：`RecordValueProcessor.DynamicDefaultOf` 直接從 `parentValues` 取值；`CreateRecordHandler`、`UpdateRecordHandler` 傳給 `DetailWriter.PrepareAsync` 的表頭值是「這次存檔後的表頭值」，修改表頭時含呼叫端看不到的既有欄位值（`RecordValueProcessor.Process` 保留 `previous` 中不能寫的欄位）。核心設計 §15 第 55 條已記：呼叫端對表頭欄位是欄位層隱藏時，值照樣寫進新的明細列；前端每次都送出所有明細欄位鍵，只有直接呼叫 API 又省略那個鍵時才發生。
- **拆軸後這變成設計者自助的路徑**：設計者把明細欄位 X 設成帶表頭的「薪資」，自己（有表頭讀取、修改）用 API 新增明細列、省略 X，表頭的薪資就進了 X；X 是明細模組的欄位，他的群組在明細上沒有隱藏 X 就看得到。
- **處理（M6）**：`HeaderOnceAsync` 取得表頭值後，拿掉呼叫端在表頭看不到的欄位（`perms.HiddenFieldIds`，表頭的有效權限）。明細欄位因此得到空值（和沒有預設值一樣）。畫面行為不變（前端本來就拿不到隱藏欄位的值），只有直接呼叫 API 的結果改變。核心設計 §15 第 55 條標「權限軸-a 已處理」（§21）。

### 12.3 連動事件

- **證據**：`EventService/EventValidator.CheckSetsAsync` 與 `HiddenAsync`。事件以系統身分執行（`SystemIdentity`、`DynamicAccessGuard.SystemPermissions`），誰存檔觸發都一樣。建立時只檢查來源欄位是否被某群組設成「隱藏」或是限定可見的貢獻欄位（及引用它們的公式），有就要求勾 `allowHiddenSource`。
- **路徑一（勾旗標）**：設計者勾「允許複製被隱藏的值」，就能把任何人看不到的欄位複製到他讀得到的模組。**M5 改成要權限設定**（§7.3）。
- **路徑二（不必勾旗標）**：`HiddenAsync` 不看「金額遮罩」與「讀不到整個來源模組」。設計者讀不到的模組，或沒有金額權限看不到的金額，只要沒有被明確設成隱藏，事件照樣能複製；金額欄位只能複製到金額欄位（`FieldRules.CopyCompatible`，目的端照樣受金額遮罩），但事件的公式對應（`s.Formula`）結果型別是數字時可以寫進小數欄位，例如 `{單價} * 1`，金額就變成不受金額遮罩的數字。
- **處理**：路徑二寫進手冊已知限制（§13 L4），稽核可查（`event.create`／`event.update` 的定義快照）。💡 後續可把 `HiddenAsync` 擴大成「任一群組的有效權限看不到」（含金額遮罩），不在本段：要定義「對哪些群組算隱藏」（全部群組？讀得到目標模組的群組？），規則要另外設計。

### 12.4 單據轉換

- **證據**：`ConversionService/Commands/ConvertRecord/ConvertRecordHandler.cs:209` 逐欄 `perms.CanSee(from)`，以執行轉換的人的權限複製，看不到的略過；另要求來源讀取、目標新增（`:49`、`:62`）。
- **結論**：和參照帶值同，只有轉手路徑（看得到的人執行轉換，值進入設計者讀得到的目標模組）。併入 L3。

### 12.5 簽核相關

- **自設簽核人**（使用者裁決列為已知限制）：設計者在簽核流程把自己設成第一關，之後每張送簽的單據他都能以 `ApprovalReadGrant` 臨時讀取（含金額，手冊 §21.7）。稽核：流程發布記 `approvalFlow.publish`，簽核實例記處理人。L1。
- **設計器試算的推論**：`PreviewApprovalFlowRequest`／`PreviewApprovalDraftRequest`（設計器讀取）帶記錄試算時，要求呼叫者對那筆記錄有讀取且在資料範圍內（`ApprovalHandlers.cs` 的 `PreviewApprovalFlowHandler` 註解），但 `ApprovalPreviewer` 在 `designer: true` 時不套隱藏欄位，條件引用的隱藏欄位照用記錄的值（手冊 §21.9「你看不到的欄位也用記錄的值」）。設計者可以寫「薪資 > 50000」這類條件反覆試算，從走哪條路推出隱藏值。試算是查詢，不記稽核。L5。

### 12.6 彙總（使用者裁決列為已知限制）

核心設計 §15 第 32、37 條：彙總計入所有子記錄、不看讀者的資料範圍；一般參照的彙總只依自己的結果型別判斷金額權限，不套來源模組的欄位隱藏。設計者在讀得到的模組加一個一般參照彙總，加總來源模組裡他看不到的「小數」型別欄位（例如設成小數的薪資），就算得出合計；來源只有一筆時就是那筆的值。L2。

### 12.7 查過、沒有路徑的

- 報表：任何地方用到看不到的欄位就整張擋（手冊 §13「權限」）。
- 公式：引用隱藏欄位或金額結果沒有金額權限，公式一併隱藏（`PermissionResolver.Resolve`）。
- 明細彙總的彙總對象隱藏時彙總也隱藏（`HiddenSourceFieldsAsync`）。
- 設計器預覽只有表單結構，沒有資料。

## 13. 已知限制（手冊第 9 章與第 5 章待寫，權限軸-b）

- **L1 自設簽核人**：有設計器修改權的人可以把自己設成簽核關卡的簽核人，之後送簽的單據他都能臨時讀取（含金額）。查證：稽核的簽核流程發布紀錄、簽核歷程。（使用者裁決）
- **L2 彙總不看可見性**：一般參照的彙總會加總來源模組裡你看不到的數字欄位；不是金額型別的數字（例如設成小數的薪資）可以藉彙總算出。查證：稽核的欄位新增、修改紀錄。（使用者裁決）
- **L3 帶值與轉換可由他人轉手**：參照帶值、單據轉換用執行的人的權限複製；看得到來源欄位的人在別的模組選參照或轉單時，值會複製過去，讀得到那個模組的人就看得到。設計時要把敏感欄位的帶值目的欄位也設成隱藏。（主控列入）
- **L4 連動事件以系統身分執行**：事件會把來源模組的值寫進目標模組，不看觸發者或設計者的權限。「允許複製被隱藏的值」要權限設定權；但沒有被明確設成隱藏的欄位（包括金額，以及設計者讀不到的整個模組）不需要勾就能複製，事件的公式還能把金額算成一般數字。查證：稽核的事件建立、修改紀錄。（主控列入）
- **L5 設計器試算可推出隱藏值**：用一筆記錄試算簽核流程時，你看不到的欄位照用記錄的值；可以藉條件反覆試算推出隱藏值。試算不記稽核。（主控列入）
- **L6 權限設定者可以改自己**：有權限設定的人可以替自己所在的群組或自己開權限，記稽核、不另外通知。（顧問裁決權限軸-漏2）
- **L7 升級帶入**：升級時，原本有設計器修改權的群組與個人會被帶入權限設定，能力和升級前一樣；要拆開請到群組頁、使用者頁取消。（M2）
- 既有的規則推論管道（核心設計 §15 第 24 條）不變，手冊已有。

## 14. 與其他段的接點

- **mcp1a**（後端地基）：
  - 加入請求通道後，`PermissionAdminBehavior`、`PermissionAdminGate` 在 MCP 通道一律回 false（M8，待使用者確認）。理由：mcp3-4（AI 能不能改權限）還在 A 區等使用者答，推薦是不提供；在沒有對應範圍的情況下，最保守的預設是 AI 這條路完全拿不到權限設定。使用者若選「提供」，再加一個權杖範圍（例如 `permissions.write`），behavior 改成「通道是 MCP 時要有該範圍」，不改表。測試 PAX-22 由 mcp1a 實作。
  - 「不提供給 AI」旗標的寫入規則照 §7.3 的契約。
- **mcp1b**（畫面）：欄位設定的「不提供給 AI 工具」勾選框依 `me.permissionAdmin` 切停用並說明，和權限分頁同一句提示。
- **mcp3**（設計器工具）：工具「一律走 `IDesignerRequest`」的原則不變；若保留 `set_module_permissions`，它對應 `SaveModulePermissionsRequest`，由 `PermissionAdminBehavior` 把關，加上 M8 的通道規則。mcp3 寫設計時依 mcp3-4 的答案定，並在 feature.md 補「⛔ 等：權限軸」（分流 E5）。
- **pkg1**（套件更新）：套用新模組的權限建議走 `SaveModulePermissionsRequest`（要權限設定；pkg1 已定要系統管理員，管理員自動有）；既有模組的權限修改只列出不套用（pkg1-4 推薦）。pkg1 若新增寫授權表的服務呼叫，§7.4 的清單測試會要求登記。
- **O2**：沿用 `AuditBehavior`、`SecurityIncidents`、`IncidentTitles`、寄送佇列；o2e（監控頁、稽核頁）若已上線，稽核頁要認得 `group.systemPrivileges`、`user.systemPrivileges` 兩個新代碼的顯示名稱（權限軸-b 補四語）。

## 15. 各開關組合下誰負責

| 身分 | 改設計 | 看權限分頁 | 改模組權限 | 授予設計器／權限設定 | 共用主檔設計與權限 |
|---|---|---|---|---|---|
| 系統管理員 | 可 | 可 | 可 | 可 | 可 |
| 設計器（依動作）＋權限設定 | 依動作 | 可 | 可 | 不可 | 不可 |
| 只有設計器 | 依動作 | 讀取以上可 | 不可 | 不可 | 不可 |
| 只有權限設定 | 不可（只能讀） | 可 | 可 | 不可 | 不可 |
| 都沒有 | 不可 | 不可 | 不可 | 不可 | 不可 |

沒裝任何應用程式時，只有系統管理員一人也能完整運作（自動擁有兩者）。Demo 站訪客以 admin 登入，行為與現在相同。

## 16. 可寫死清單（新增，屬安全與權限檢查）

- 系統權限代碼 `DYN00000`（既有）、`SYS00001`。
- 系統管理員自動擁有設計器全部動作與權限設定。
- 只有系統管理員能授予或收回這兩個系統權限。
- 權限設定隱含設計器讀取。
- 權限設定只用 `CanEdit` 位元。
- MCP 通道不能行使權限設定（mcp1a 起）。
- 共用主檔模組的權限只有系統管理員能改（既有）。
- 從範本建立沒有權限設定時略過權限建議（行為規則，不是數值）。
- 安全事件 `privilege.changed` 的每筆異動列出上限 20 人（技術限制，照 `admin.changed` 的「最近 20 筆」）。

這些都不是業務政策（不是上限、天數、預設值、特定群組或模組代碼），屬鐵則 2 允許寫死的安全與權限檢查。

## 17. ERP 對照

- **SAP**：角色維護（PFCG，定義角色有哪些授權）與使用者指派（SU01）分給不同人；開發與客製（ABAP Workbench、Customizing，`S_DEVELOP`）和授權管理（`S_USER_*`）是不同的授權物件。開發者在測試系統自己有權限，正式系統沒有。本設計的「設計器」對應客製，「權限設定」對應角色維護，「授予系統權限只有管理員」對應使用者與角色指派的最上層。
- **Odoo**：存取權（Access Rights、Record Rules）只有「Administration／Settings」群組能改；Studio 客製也要同一群組。Odoo 沒有把兩者拆開，Formwright 比它多拆一層，是因為目標客戶常把「改表單」交給 IT 而不讓他碰資料可見性。
- 小型企業慣例：權限通常由老闆或一兩位主管決定，所以權限設定全系統一份、不分模組（權限軸-4）。

## 18. 驗收案例

標記：【雙跑】＝`[Trait("Category", "Postgres")]` 進 PostgreSQL 雙跑集；沒標的只在 SQLite 跑（不涉及 repository 新查詢或 migration）。後端測試在 ApplicationTests（handler、behavior）或 WebAPITests（端點、權限碼）。

**資料與遷移**

- **PAX-01**【雙跑】全新資料庫啟動後 `Dynamic_Program` 有 `SYS00001` 且 `Active = true`；重啟一次仍是 true（SeedData 不把它設成 false）。
- **PAX-02**【雙跑】migration 帶入：準備群組 G1（`DYN00000` 讀取＋修改）、G2（只有讀取）、使用者 u1 個人 `DYN00000` 修改、u2 個人只有新增；跑 `PermissionAxes` 後，G1、u1 有 `SYS00001`（`CanEdit` true、其餘 false），G2、u2 沒有；`DYN00000` 列與業務模組列逐列不變；再跑一次（冪等）不重複插入；`Down` 後 `SYS00001` 的列全清。
- **PAX-03** 升級前後能力不變：同 PAX-02 的資料，升級前 G1 成員能存模組權限、G2 成員 403；升級後結果相同。

**系統管理員與 fail closed**

- **PAX-04** 系統管理員（沒有任何系統權限列）`GET me` 回 `designer` 四項 true、`permissionAdmin` true；能存任一模組權限。
- **PAX-05** 只有設計器修改的使用者：`GET modules/{key}/permissions` 200；`PUT` 403、錯誤碼 `PermissionAdminRequired`；權限表逐列不變。
- **PAX-06** fail closed：使用者不存在、或已停用但仍帶 session，`IsPermissionAdminAsync` 為 false、`DesignerActionsAsync` 為空；宣告 `IPermissionAdminRequest` 但回傳型別不是 `ServiceResult` 的測試用請求，送出時丟 `InvalidOperationException`。
- **PAX-07** 只有權限設定的使用者：`GET modules`、`GET modules/{key}`、`GET modules/{key}/permissions` 200（隱含讀取）；`PUT modules/{key}/permissions` 200；`POST modules/{key}/fields` 403。`GET me` 的 `designer.read` true、其餘 false。

**自我提權嘗試**

- **PAX-08** 設計者小王（設計器全部動作、沒有權限設定，所屬群組 G）對他讀不到的模組 M 送 `PUT modules/M/permissions` 給 G 讀取＋金額 → 403；之後小王 `GET` M 的 schema 仍 403、M 的權限列不變。
- **PAX-09** 小王送 `PUT admin/groups/{G}/system-privileges` 給 G 權限設定 → 403；權限設定者（非管理員）送同一請求 → 403；兩次都沒有寫入、沒有安全事件。
- **PAX-10** 小王送 `PUT admin/groups/{持有權限設定的群組}/members` 把自己加進去 → 403（系統管理員才能改成員）。
- **PAX-11** 小王 `POST templates/{含群組權限建議的範本}/instantiate` → 200，模組建立、`Dynamic_GroupProgramPrivilege` 沒有新模組的列，回應 warnings 含略過權限的提示，稽核 `template.instantiate` 的 `permissionsSkipped` 為 true；同範本由權限設定者（也有設計器新增）建立 → 權限建議照常套用。
- **PAX-12** 小王 `POST demo-data/generate` → 403 `PermissionAdminRequired`；設計器新增＋修改＋權限設定 → 照常開始。
- **PAX-13** 小王建立事件，某個對應 `allowHiddenSource = true` → 403 `EventHiddenSourceNeedsPermissionAdmin`、事件不存在；改成 false 且來源欄位沒被隱藏 → 成功；系統管理員先建一個勾旗標的事件，小王 `PUT` 修改它的名稱（旗標仍在）→ 403；權限設定者同樣修改 → 200。
- **PAX-14** 帶表頭的值（M6）：表頭模組 H 的「薪資」對小王的群組隱藏，明細模組 D 的欄位 X 設成帶表頭的薪資；小王以 API 修改既有表頭、新增一列明細並省略 X → 該列 X 為空；看得到薪資的使用者做同樣操作 → X 等於表頭薪資。新增表頭時同樣檢查一次。

**跨模組與共用主檔**

- **PAX-15** 權限設定者在模組 A 存權限 200、在模組 B 存權限 200（全系統一份）；在共用主檔模組存權限 403；系統管理員在主檔模組 200。
- **PAX-16** 明細模組：權限設定者存明細模組的欄位例外 200；只有設計器修改的人存明細模組的欄位例外 403。
- **PAX-17** 權限設定者把自己所在群組的模組 A 讀取打開 → 200，稽核 `permission.update` 的行為者是他、格子含該群組；沒有安全事件（權限軸-漏2）。

**授予與安全事件**

- **PAX-18**【雙跑】系統管理員 `PUT admin/groups/{G}/system-privileges`（設計器讀取＋修改、權限設定）→ 200；`GET admin/groups` 的 G 帶 `systemPrivileges`；`DYN00000` 列 `CanRead`、`CanEdit` true，`SYS00001` 列 `CanEdit` true；稽核 `group.systemPrivileges` 有 before／after；`privilege.changed` 事件次數加一、樣本指向該稽核 Id、管理員收到站內通知與寄送佇列一筆。全部取消 → 兩列都刪除、事件再加一（revoked）。
- **PAX-19**【雙跑】使用者個人同上（`user.systemPrivileges`）；目標是系統管理員也照存、回應照存的值，`GET me`（以該管理員）不受影響。
- **PAX-20**【雙跑】實際持有者變化：G 持有權限設定，系統管理員用 `SetGroupMembers` 把 u3 加進 G → 發 `privilege.changed`（granted），`privilegeHolders` 含 u3；加進沒有系統權限的群組 → 不發；`DELETE admin/groups/{G}` → 發（revoked），`GET admin/groups/{G}/impact` 事先回 `systemPrivileges`；用 `UpdateUser` 改 u3 的群組同樣判斷。
- **PAX-21** 原值存檔：系統權限 API 送和現狀相同的內容 → 200、有稽核列、不發安全事件。

**接點與回歸**

- **PAX-22**（mcp1a 實作）MCP 通道的請求送 `PUT modules/{key}/permissions`，即使權杖擁有者是系統管理員 → 403；同一人經畫面通道 → 200。
- **PAX-23** `PermissionAdminRequestInventoryTests`：宣告 `IPermissionAdminRequest` 的型別恰為 §7.2 的 6 個；寫授權表服務的呼叫端類別恰為登記清單。
- **PAX-24** 既有測試回歸：設計器相關的既有測試裡「設計器修改能存權限」的案例改成「要權限設定」，其他設計器行為的測試不改即通過（全套 SQLite＋雙跑集）。

**畫面（權限軸-b，Vitest＋本機走查）**

- **PAX-30** 權限分頁：`permissionAdmin` false 時所有輸入停用、存檔鈕停用、顯示唯讀說明；true 時可編可存；共用主檔模組且不是系統管理員時唯讀並顯示主檔說明。
- **PAX-31** 使用者編輯對話框：系統權限區塊載入現值、改完存檔送 `PUT admin/users/{id}/system-privileges`；目標是系統管理員時顯示自動擁有的提示；第二個請求失敗時的提示文字。
- **PAX-32** 群組編輯對話框同上；刪除群組對話框在群組持有系統權限時列出「成員會失去」。
- **PAX-33** 群組清單、使用者清單顯示「設計器」「權限設定」標籤（桌機表格與手機卡片）。
- **PAX-34** 事件編輯：沒有權限設定時旗標勾選框停用；載入已勾旗標的事件時存檔鈕停用並說明。
- **PAX-35** 從範本建立對話框在沒有權限設定時顯示略過提示；Demo 資料頁沒有權限設定時產生鈕停用並說明。
- **PAX-36** 只有權限設定的帳號登入：選單有「動態模組設計」、沒有「使用者」「群組」「應用程式」；能從設計器進權限分頁存檔。
- **PAX-37** 四語鍵齊全（`locales.vitest.spec.ts`）；手冊第 5、9、11、12、18 章與畫面文字一致。
- **PAX-38** 本機走查（`scripts/unattended/local-demo.sh`）390、820、1366、1920 四種寬度：使用者、群組編輯對話框、權限分頁、事件編輯不得橫向捲動。情境：admin 給小王設計器修改 → 小王開權限分頁看得到、改不了 → admin 再給權限設定 → 監控頁出現 `privilege.changed` → 小王能存。

## 19. 子單元切分

### 權限軸-a 後端

- 範圍：`SystemProgramCodes`；SeedData；`PermissionAxes` migration（SQLite、PostgreSQL 各一）；`IPermissionAdminRequest`、`PermissionAdminBehavior`、`PermissionAdminGate`、guard 兩個方法與隱含讀取；§7.2 守衛改宣告；M3 範本略過、M4 Demo、M5 事件旗標、M6 帶表頭的值；兩支系統權限 API 與管理清單、群組影響的 `systemPrivileges`；`GetMe` 的 `permissionAdmin`；稽核代碼、`SystemPrivilegeHolders`、`privilege.changed` 與四語標題與信件文案；`MessageText` 兩個新鍵；PAX-01–21、23、24；核心設計 §15 第 55 條與「上線前必做」兩處的回寫（§21）。
- 相依：O2 收完（`SecurityIncidents`、`AuditBehavior` 已在 o2a–o2b；等 o2e 合併再開工，避免和它同時改 `IncidentTitles`、四語檔）。
- 規模估計：後端約 15–20 個檔案，一個 migration；無前端改動（`GetMe` 多一個欄位，前端型別在 -b 補，舊前端忽略未知欄位）。

### 權限軸-b 畫面

- 範圍：§11 全部；`Me`、`AdminUser`、`AdminGroup`、`GroupImpact` 型別；四語；手冊第 5 章（開頭與 5.5 主檔說明補權限設定）、第 9 章已知限制 L1–L7、第 11、12 章系統權限、第 18 章「誰能做」改寫（§21）；o2e 稽核頁的兩個新稽核代碼名稱；PAX-30–38。
- 相依：權限軸-a 上線（API 形狀）。

## 20. 決定彙整

### 20.1 使用者裁決

- 另立權限設定代碼（2026-11-11）。
- 排在 O2 之後、p2 之前，兩個子單元（2026-11-19）。
- 授權範圍與仍歸設計的項目、L1 與 L2 寫進手冊已知限制（2026-11-19，§3）。

### 20.2 顧問裁決（待使用者確認）

照 2026-11-19 分流 B 區採用：

- 權限軸-1 只有系統管理員能授予設計器與權限設定（§5）。
- 權限軸-2 系統管理員自動擁有兩者，手冊改成和程式一致（§5、§21）。
- 權限軸-4 權限設定全系統一份、不分模組（§2 不做）。
- 權限軸-5 設計者新建的模組不自動給自己權限，用預覽分頁看表單，要試資料請權限設定者授權。
- 權限軸-6 授予或收回兩個系統權限發安全事件，一般模組權限存檔只記稽核（§9；觸發點由 M9 擴大）。
- 權限軸-漏2 權限設定者可以改自己所在群組與自己的個人權限，記稽核不通知（§5、L6）。
- 權限軸-漏3 共用主檔模組權限維持只有系統管理員能改（§5）。

### 20.3 主控裁決（待使用者確認）

- **M1 權限設定隱含設計器讀取**（§5）。理由：不然權限設定者進不了設計器；讀取只看定義不看資料。選錯代價：改成必須另外授予讀取，改 guard 一行＋畫面一個驗證，不改表。
- **M2 migration 帶入既有設計器修改權持有者**（§6.3）。延伸分流 D5。理由：任務要求升級不得讓人突然失去能力。選錯代價：改成不帶入，只刪 migration 的兩段插入；已升級的庫要手動清。
- **M3 從範本建立沒有權限設定時略過權限建議並警告**（§7.3）。推翻分流 D4 對範本的部分。理由：D4 會讓沒有權限設定的設計者用不了內建範本，略過只是回到新模組預設沒人有權限。選錯代價：改成擋下，handler 一個判斷。
- **M4 產生 Demo 資料要權限設定**（§7.3）。理由：它以管理員身分批次授權。選錯代價：拿掉一個介面宣告。
- **M5 連動事件「允許複製被隱藏的值」歸授權**（§7.3、§12.3）。理由：這個旗標就是放寬可見性，和「不提供給 AI」同類；使用者 11-19 的授權清單沒列到它，是因為當時還沒查到這條路徑。選錯代價：拿掉 handler 的 gate 呼叫與畫面停用。
- **M6 帶表頭的值依呼叫端可見性**（§12.2）。理由：拆軸後它變成設計者自助取得隱藏值的路徑，修法只是過濾一份字典，畫面行為不變。選錯代價：還原一行過濾。
- **M7 系統權限可授予群組或個人**（§5）。理由：沿用既有兩張表，小公司常只有一人管權限。選錯代價：只開群組時，畫面拿掉使用者頁的區塊、API 拿掉一支。
- **M8 MCP 通道一律不能行使權限設定**（§14），直到使用者答 mcp3-4 並加對應權杖範圍。選錯代價：加一個範圍與 behavior 一個條件，不改表。
- **M9 安全事件依實際持有者變化觸發**，含成員異動與刪除群組（§9）。理由：只看系統權限 API 會漏掉「把人加進持有權限設定的群組」。選錯代價：只留兩支 API 的觸發，拿掉四個 handler 的比對。

### 20.4 技術決定（主控，可推翻）

- **T1** 新代碼 `SYS00001`（分流 D1）。
- **T2** 只用 `CanEdit` 位元，不加表不加欄。
- **T3** 系統代碼集中在 Domain 的 `SystemProgramCodes`。
- **T4** `PermissionAdminBehavior` 放在 Designer 之後、SystemAdmin 之前，比照既有回傳型別檢查（分流 D2）。
- **T5** 停用與不存在的使用者一律沒有權限設定與設計器動作。
- **T6** 條件式檢查集中在 `PermissionAdminGate`。
- **T7** 開啟、關閉、重新開啟、刪除應用程式冗餘宣告 `IPermissionAdminRequest`（延伸分流 D4 對套件的部分）。
- **T8** 事件旗標只看請求內容、不比對既有定義。
- **T9** 守衛清單用反射測試固定（§7.4）。
- 分流 D3（`GetModulePermissions` 設計者可讀不可寫）、D6（`GetMe` 回權限設定；本設計用單一布林 `permissionAdmin`，因為只有一個位元）、D7（授予 API 宣告 `ISystemAdminRequest`）照採用。

### 20.5 A 區其他權限軸題目

分流 A 區權限軸的題目只有權限軸-漏1 與權限軸-3，使用者 2026-11-19 都已答；沒有其他待答題。mcp3-4（AI 能不能改權限）屬 mcp3，本設計以 M8 的保守預設銜接，不代答。

## 21. 文件待改

- **手冊待改**（權限軸-b 改）：
  - `docs/manual.md:1185`（第 18 章「誰能做」）：「系統管理員身分不會自動帶設計器權限，設計器權限要由群組授予」改成「系統管理員自動擁有設計器與權限設定」；選單條件說明一併簡化。
  - 第 5 章開頭：「設計器的權限分頁」改寫成「有『權限設定』權限的人可以改；有設計器權限的人只能看」。
  - 第 11、12 章：系統權限區塊與標籤。
  - 第 9 章：L1–L7。
  - 升級說明：L7 的檢查步驟。
- **核心設計 §15 待改**（權限軸-a 合併時改）：「上線前必做：決定是否拆開設計與授權」標已處理並連到本文件，刪掉 `DYN00001` 的提議（分流 E3）；第 55 條標「權限軸-a 已處理」。
- **roadmap**：順序串已含權限軸（分流 E2 已由 5cb719e8 處理）；本次只把權限軸列的「設計」欄連到本文件。
- **feature.md**：權限軸-a、-b 上線時打勾；mcp3 的「⛔ 等：權限軸」依 mcp3-4 的答案補（分流 E5，不在本段）。

## 22. 宗旨檢查（formwright-purpose-check）

1. 機制還是政策：通過。新增的都是權限機制與安全檢查，列在 §16；沒有新的上限、天數、預設值或群組代碼。
2. 寫死掃描：不適用（尚無 diff）；實作審查時跑。
3. 單一來源：通過。系統代碼集中在 `SystemProgramCodes`；前端唯讀條件從 `GET me` 取，不自己算。
4. 兩個應用程式：通過。權限設定對所有模組一體適用，CRM、進銷存、MES 都用同一套權限分頁。
5. 空系統：通過。沒裝任何應用程式時系統管理員自動擁有兩者，群組頁、使用者頁照常能授予。
6. 套件只用定義組成：通過。套件帶的權限建議仍是資料，開啟時走同一支權限存檔請求。
7. ERP 對照：通過，見 §17。
8. fail closed、驗證不只在前端、沒有執行期 DDL：通過。behavior 對錯誤回傳型別丟例外、停用者為 false、MCP 預設拒絕；所有限制都在後端；migration 只有資料。
9. 不限產業：通過。取捨理由是公司規模（權限管理的人少），不依產業。

## 23. 不確定、留給實作者

- `PermissionAxes` migration 的 SQL 要在兩個 provider 各寫：SQLite 的布林是整數、PostgreSQL 是 `boolean`；欄位名照 `Dynamic_GroupProgramPrivilege`、`Dynamic_UserProgramPrivilege` 的實際欄名（`CanRead` … `CanApprove`），識別字加雙引號。
- `TemplateInstantiator` 的 warnings 目前是繁中字面字串（例如「群組『X』不存在」）；新增的那則照同樣寫法，四語化不在本段。
- 事件編輯前端（`event-draft.ts` 一帶）停用旗標的位置，實作者照現有畫面決定；只要求「沒有權限設定時不能勾、已勾的不能存」。
- 帶表頭的值的修法（M6）會讓「直接呼叫 API、對表頭欄位隱藏、又省略明細鍵」的整合方行為改變；目前沒有已知的外部整合方。
- 依程式閱讀判定的路徑（§12）沒有另寫重現測試；PAX-13、PAX-14 會把 M5、M6 固定下來，L3–L5 若實作時發現與描述不符，修正手冊文字並回寫本節。
