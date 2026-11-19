# 設計與授權分成兩條權限軸 設計文件（段 權限軸）

> 2026-11-19｜平台共用能力（非應用程式）｜前置：[核心設計](2026-11-03-dynamic-modules-design.md) §15「上線前必做：拆開設計與授權」、[共用主檔](2026-11-10-shared-master-design.md) §6、[維運與觀測](2026-11-11-ops-observability-design.md) §17（稽核表、安全事件）｜相關：[MCP server](2026-11-11-mcp-server-design.md)（mcp1a／mcp1b「不提供給 AI」旗標、mcp3 設計器工具）、[應用程式套件](2026-11-06-app-package-design.md)（pkg1 套件更新）
> 狀態：主控撰寫（2026-11-19），這次只寫設計、不改程式；同日依審查修正 10 條必修與 3 條建議，再依第二輪短核對修正 4 條（§24）。路線位置照使用者 2026-11-19 裁決：O2 之後、p2 之前，分權限軸-a（後端）、權限軸-b（畫面）兩個子單元（§19）。
> 標記：「（使用者裁決）」是使用者已拍板的；「（顧問裁決，待使用者確認）」是 2026-11-19 顧問分流 B 區照推薦採用的；「（主控裁決，待使用者確認）」是本設計延伸或推翻顧問與分流結論的行為類決定；「（T#）」是技術類決定，主控可推翻。全部彙整在 §20，待使用者確認的總表在 §20.6。

## 1. 目的與為什麼

現在「改模組設計」和「決定誰看得到、改得到資料」是同一個權限：持有設計器代碼 `DYN00000` 修改權的人，就能在模組的「權限」分頁替自己開任何模組的全部權限，包括金額、解除欄位隱藏（核心設計 §15「上線前必做」，`SaveModulePermissionsRequest` 只要求設計器修改）。

這段把兩件事拆成兩個可分別授予的系統權限：

- **設計器**（既有 `DYN00000`，讀取／新增／修改／刪除）：建模組、欄位、規則、流程、報表、檢視。
- **權限設定**（新 `SYS00001`）：決定誰能看、能做什麼。

例：公司讓 IT 小王改表單，但不讓他決定業務能不能看單價。拆軸後小王有設計器修改、沒有權限設定，他看得到權限分頁但改不了。

## 2. 目標、不做、反目標

**目標**

1. 新系統權限「權限設定」，與設計器分開授予；系統管理員自動擁有兩者（§5）。
2. 所有寫入授權資料的路徑改要求「權限設定」，fail closed；新寫入路徑忘了宣告時，追蹤式寫入在執行期丟例外，批次與原生 SQL 寫入讓清單測試失敗（§7.4）。
3. 系統管理員能在群組頁、使用者頁授予這兩個系統權限（今天沒有任何畫面能授予設計器權限）。
4. 授予或收回這兩個權限記稽核、發安全事件，比照 `admin.changed`（§9）。
5. 升級時沒有人失去改權限的能力；多出或收緊的能力逐條列出，不做無條件的「能力一樣」宣稱（§6.3）。
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

## 4. 現況（2026-11-19 main，5cb719e8；審查修正時以 856364f5 重查）

- 設計器權限是 `Dynamic_Program` 一列 `DYN00000`，授權存在 `Dynamic_GroupProgramPrivilege`／`Dynamic_UserProgramPrivilege`（和業務模組同兩張表，`ProgramCode` 長度 8，沒有到 `Dynamic_Program` 的外鍵）。`SeedData.EnsureSeededAsync` 冪等建立它，並把 `DYN00000` 以外的 program 依模組狀態同步 `Active`（`SeedData.cs:38`）。
- 業務模組代碼由 `IProgramRepository` 從 `DYN00001` 起編，核心設計 §15 原本提議的 `DYN00001` 已被佔用。
- 守衛：`IDesignerRequest`＋`DesignerAccessBehavior`（宣告動作、回傳型別非 `ServiceResult` 就丟例外）、`ISystemAdminRequest`＋`SystemAdminBehavior`（啟用中的系統管理員）、`IMasterDesignRequest`＋`MasterDesignerBehavior`（動到共用主檔模組另要系統管理員）。管線順序在 `Application/DependencyInjection.cs`：Designer → SystemAdmin → MasterDesigner → Validation → … → Transaction → Audit。
- 系統管理員：`PermissionResolver.Resolve` 對管理員回全部權限；`PermissionResolver.ResolveProgram`（`PermissionResolver.cs:88-89`）對管理員回設計器全部動作，**先看管理員旗標、不看啟用**（停用的管理員也回全部；今天停用者 session 已失效才沒出事，T5 改順序）。手冊 §18「誰能做」（`docs/manual.md:1185`）卻寫「系統管理員身分不會自動帶設計器權限，設計器權限要由群組授予」，**手冊錯，程式對**（§5 定案，手冊待改，§21）。
- 沒有任何畫面能授予 `DYN00000`：權限分頁只列業務模組，群組頁、使用者頁沒有系統權限區塊，Demo 也沒給任何人。實際上今天只有系統管理員能設計。
- 寫入授權資料的路徑：初稿只列了經 `ReplaceModuleRowsAsync`、`MasterGrants`、`AppOpener` 的三條，**不完整**——`AddField` 經 `MasterDesigner.BindFieldAsync` 寫貢獻欄位可見群組；刪模組、欄位、區塊經 `DeletionRepository` 清授權列；刪群組經 `AccountRepository.DeleteGroupAsync` 清成員與五種授權列；成員異動寫 `Dynamic_UserGroupMapping`。對照全部 controller 的完整分類見 §7.4。
  - 產生 Demo 資料：`StartDemoDataRequest` 只要設計器新增＋修改，`DemoDataRunner` 在背景（`ExecutionContext.SuppressFlow`）以 `ActingScope.Begin("admin")` 送 `GenerateDemoDataRequest`（沒有任何守衛介面），內層 `DemoStructure` 直接呼叫 `AppOpener` 開套件並寫欄位權限。**這是今天設計者間接寫授權的路徑**。
- 讀取：`GetModulePermissionsRequest`、`GetGroupsRequest`、`GetUsersRequest` 宣告設計器讀取。
- 錯誤回應：`ServiceResultExtensions.Body` 輸出 `{ message, code?, params? }`；`ServiceResult.Forbidden(MessageRef)` 只設訊息、不設 `Code`，所以 403 今天不會帶 `code`（§10 補建構方式）。
- 前端：`PermissionsTab.vue` 以 `me.designer.edit` 切唯讀；選單 `menu-nav.ts` 以 `designer.read` 顯示設計器、`isSystemAdmin && designer.read` 顯示應用程式。
- 稽核：`AuditBehavior` 只記成功（O2-U9，被權限擋下的不記）。`permission.update` 只記變動的格子。系統管理員旗標異動由 handler 呼叫 `AuditContext.MarkAdminChanged`，`AuditBehavior` 寫完稽核後同交易呼叫 `SecurityIncidents.AdminChangedAsync`（事件 `admin.changed`）。

## 5. 系統管理員與授予

- **系統管理員自動擁有設計器全部動作與權限設定**（顧問裁決權限軸-2，待使用者確認）。符合現行程式與「空系統是合法起點」：全新安裝的第一個管理員開箱就能設計、授權，沒裝任何應用程式也一樣。代價：做不出「只管帳號、不碰設計」的管理員；之後要做，改 resolver 一行＋測試，不改表。
- **只有系統管理員能授予或收回設計器與權限設定**（顧問裁決權限軸-1，待使用者確認）。權限設定者能改模組權限，但不能把這兩個權限給任何人；授權的最上層只有一個角色，才不會繞回自我提權。群組成員與使用者的群組本來就只有系統管理員能改（`SetGroupMembers`、`UpdateUser` 宣告 `ISystemAdminRequest`），所以權限設定者也無法把自己加進持有設計器的群組。寫入邊界另有第二道：系統權限列與成員表只准在系統管理員請求內寫（§7.4 列層規則）。
- **授予對象是群組或個人**（主控裁決 M7，待使用者確認）：沿用業務模組的群組權限＋個人權限兩張表，有效權限是兩邊逐項合併。理由：小公司常常只有一個人管權限，為一個人開群組多一步；表本來就支援。API 與畫面把「直接設定」和「有效結果」分開回傳，不混用（§8、§11）。
- **權限設定隱含設計器讀取**（主控裁決 M1，待使用者確認）：有權限設定的人，設計器動作至少有「讀取」，才進得了設計器的模組清單與權限分頁。設計器讀取只看得到定義（模組、欄位、規則、流程），看不到資料；而權限設定者本來就能替自己開模組讀取（顧問裁決權限軸-漏2），不會多開一條看資料的路。**這是新增的隱含能力**：升級時被帶入權限設定、原本只有修改沒有讀取的人，會多出設計器讀取（§6.3 N1）。替代做法是「勾權限設定時必須同時勾設計器讀取」，多一條驗證、多一個會忘記的步驟，不採用。
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
- 建完 program 列後呼叫 `PermissionAxesImport.RunAsync`（§6.3）。

### 6.3 升級帶入：既有持有者怎麼拿到權限設定

**不用 EF migration**（T12）：這一步只有資料、沒有 schema 變更。專案規則是 migration 只用 EF 產生、不手改（`.claude/rules/backend.md`），資料 migration 得手寫兩份 provider SQL，而且 EF 第二次 `Migrate` 靠歷程跳過、證明不了 SQL 冪等，`Down` 也只能整批刪。改成啟動時的冪等匯入，以 `Dynamic_SystemSetting` 的鍵守門，照 `IndexBackfillJob` 以 `index.backfillVersion` 守門的既有寫法，用 EF LINQ 寫、兩個 provider 共用。

**能力契約**（主控裁決 M2，待使用者確認；延伸分流 D5「不自動給任何人」——D5 的前提是沒人被授予過 `DYN00000`，在 Demo 與目前所有部署成立，但契約不能靠當前部署的資料，要對任何資料庫成立）。以下「升級前」指舊版程式加升級前的資料，「升級後」指新版程式加帶入後的資料：

保留（升級前後相同）：

- **K1 能存模組權限的人**：升級前＝啟用中的系統管理員，加上有效 `DYN00000` 修改權（群組列與個人列逐項合併，任一來源列 `CanEdit = true`）的啟用使用者。升級後這些人都有權限設定：管理員自動有；其餘人的每一個 `CanEdit` 來源列都被帶入，合併後一定為 true，不論他在幾個群組、修改權來自群組還是個人。
- **K2 不能存模組權限的人仍不能**：沒有任何 `CanEdit` 來源列的人不會被帶入；首次帶入前先清掉既存的 `SYS00001` 授權列（步驟 3），所以殘留列也不會讓人多出權限設定。
- **K3 設計器動作**：`DYN00000` 的列一列不動；設計器的有效動作只多 M1 的讀取（N1）。
- **K4 產生 Demo 資料、勾「允許複製被隱藏的值」**（M4、M5 改要權限設定）：升級前要設計器修改（Demo 另要新增），持有修改權的人正好被帶入，升級當下能做的人相同。
- **K5 停用帳號**：帶入照列寫、不看啟用。停用期間權限設定為 false（T5）；重新啟用後和升級前一樣，同時拿回設計器修改與改權限。

新增（升級後多出的能力，待使用者確認）：

- **N1（M1）** 有 `DYN00000` 修改、沒有讀取的人（畫面做不出來，只有直接寫庫才會有）：升級前能盲改權限（`PUT modules/{key}/permissions` 不需要讀取）但讀不到設計器；升級後多了設計器讀取，看得到模組定義，看不到資料。
- **N2（M3）** 有設計器新增＋修改、沒有權限設定的人：升級當下不存在（修改權持有者都被帶入），要等管理員事後拆開才會出現；他們從範本建立會由「整批 403」變成「建立成功、略過權限建議」。

收緊（有意改變結果的安全修正）：

- **S1（M5 擴大）** 事件的對應引用「引用了隱藏欄位的公式」（逐層）時，升級後要勾旗標，也就要權限設定（§12.3）。既有事件照常執行，下次存檔才會被要求。
- **S2（M6）** 直接呼叫 API、對表頭欄位看不到、又省略明細鍵的呼叫端，明細欄位改成空值（§12.2）。
- **S3（T5）** 停用的系統管理員：`IsPermissionAdminAsync`、`DesignerActionsAsync` 先看啟用、再看管理員。今天停用者的 session 已失效，實際沒有差別。
- **S4（M8，mcp1a 起）** MCP 通道不能行使權限設定（§14）。

**步驟**（`PermissionAxesImport.RunAsync`，在 `SeedData` 內、MigrateAsync 之後執行）：

1. 開一個交易（PostgreSQL 的全庫寫入鎖 `PostgresWriteLockInterceptor` 讓多個實例同時啟動時依序進來；SQLite 是單一檔案寫鎖）；步驟 2–5 在 `AuthorizationWriteScope.RunAsync(Seed, …)` 內執行，含存檔（§7.4）。步驟 3、4 用追蹤式寫入（先查出再 `RemoveRange`／`AddRange`），每一列照樣經攔截器檢查。
2. 讀 `Dynamic_SystemSetting` 的 `permissionAxes.importedOn`；有就結束（重跑、回滾後重新升級都不重做）。
3. 刪除全部 `ProgramCode = 'SYS00001'` 的群組與個人授權列。舊版不認這個代碼，存在的列沒有意義；不清的話，`CanEdit = false` 的殘列會擋住第 4 步的插入（K1 破功），`CanEdit = true` 的殘列會讓本來不能改權限的人取得權限設定（K2 破功）。
4. 對每一列 `ProgramCode = 'DYN00000' AND CanEdit = true` 的群組列、個人列，插入 `(GroupId 或 UserId, 'SYS00001', CanEdit = true, 其餘 false)`。
5. 寫入 `permissionAxes.importedOn`（UTC，ISO 8601）並提交。任一步失敗整個交易回滾、啟動失敗，下次啟動重來。
6. 不動系統管理員、`DYN00000` 的任何列、業務模組的任何列。

帶入不發安全事件（不經 MediatR，也沒有行為者）；手冊升級說明寫「升級後到群組頁、使用者頁檢查誰有權限設定，需要拆開的就取消」（§21）。

**回滾**（退回上一版程式；沒有 migration 要 `Down`）：

- 不丟資料：權限設定列、群組頁與使用者頁授予的系統權限、標記、稽核與安全事件都留著。
- 規則退回舊版：舊版不認 `SYS00001`，有設計器修改權的人重新能改權限；只有權限設定、沒有設計器修改的人在舊版改不了權限。舊版 SeedData 會把 `SYS00001` 的 program 列設成 `Active = false`，新版再啟動時改回 true（§6.2）。舊版的稽核頁遇到 `group.systemPrivileges` 等新代碼照原字串顯示。
- 重新升級：標記還在，不重做帶入；回滾前的授予與收回原樣恢復，已收回的不會復活。回滾期間若有人直接寫庫新增設計器修改權，不會被帶入權限設定，手冊升級說明寫「重新升級後到群組頁、使用者頁檢查」。
- 要強制重新帶入（例如整庫從回滾前的備份還原、又想重來），刪掉標記列再啟動；這會照當時的設計器修改權重算，事後的授予與收回全部作廢，手冊寫在升級說明、標「一般不需要」。

## 7. 後端守衛

### 7.1 新介面與 behavior（權限軸-a）

- `IPermissionAdminRequest`（空介面，`Common/Security/`）：送出的人必須有權限設定。
- `PermissionAdminBehavior<TReq,TRes>`：照 `DesignerAccessBehavior` 的寫法——不是這個介面就放行；回傳型別不是 `ServiceResult` 就丟 `InvalidOperationException`（fail closed）；`PermissionAdminGate.CheckAsync` 不是 `Allowed` 就回對應的 403（§10）。通過後以 `PermissionAdminGate.RunAsync` 在 `Authorized` 寫入許可內執行 `next()`（§7.4；`TransactionBehavior` 在 `next()` 裡，存檔與提交都在許可內）；請求同時是 `ISystemAdminRequest` 時許可帶 `SystemAdminVerified`（`SystemAdminBehavior` 在前面已通過）。管線放在 `SystemAdminBehavior` 之後、`MasterDesignerBehavior` 之前（T4）：同時宣告系統管理員的請求先被「不是系統管理員」擋下，錯誤訊息才不會誤導成「去要權限設定」。
- `IDynamicAccessGuard` 加 `Task<bool> IsPermissionAdminAsync(CancellationToken)`：判斷順序固定為「使用者不存在 → false；已停用 → false；系統管理員 → true；否則 `PermissionResolver.ResolveProgram(SYS00001, inputs).Contains(Edit)`」（T5，fail closed）。`DesignerActionsAsync` 改成同樣的順序（停用回空集合，含停用的管理員），有權限設定時把 `Read` 併進回傳集合（M1）。
- 條件式檢查用 `PermissionAdminGate`（Application 服務）：`CheckAsync` 回 `Allowed`／`NotPermissionAdmin`／`McpChannel`，只做判斷、不開許可；`RunAsync(body)` 先檢查，不是 `Allowed` 就回對應的 403，是就在 `Authorized` 許可內執行 `body`（`body` 要包含守衛寫入與它的存檔，§7.4）。給「只有某些請求內容才算授權」的 handler 呼叫（§7.3）；不在 handler 各自讀權限表（T6）。許可只能以 callback 形式開，沒有「先拿許可、再自己寫」的用法。
- **通道**：mcp1a 加入「請求通道」後，gate 在通道是 MCP 時一律回 `McpChannel`（M8，§14）；`PermissionAdminBehavior` 也經 gate，所以同一條規則。權限軸-a 時還沒有通道概念，`McpChannel` 分支與 PAX-22 留給 mcp1a。

### 7.2 受影響 API 守衛清單

| 請求 | 端點 | 改前 | 改後 |
|---|---|---|---|
| `SaveModulePermissionsRequest` | `PUT modules/{key}/permissions` | 設計器修改＋主檔守衛 | 權限設定＋主檔守衛（拿掉 `IDesignerRequest`） |
| `GetModulePermissionsRequest` | `GET modules/{key}/permissions` | 設計器讀取 | 不變（權限設定隱含讀取） |
| `GetGroupsRequest`、`GetUsersRequest` | `GET groups`、`GET users` | 設計器讀取 | 不變 |
| `StartDemoDataRequest` | `POST demo-data/generate` | 設計器新增（修改在 handler） | 再加權限設定 |
| `GenerateDemoDataRequest` | 背景執行，無端點 | 無 | 加系統管理員＋權限設定（背景以 admin 送，通過；示範帳號的成員寫入要 `SystemAdminVerified`，T13） |
| `InstantiateTemplateRequest` | `POST templates/{key}/instantiate` | 設計器新增（巢狀請求要修改） | 不變；權限步驟改走 gate（§7.3） |
| `OpenAppRequest`、`ReopenAppRequest`、`CloseAppRequest`、`DeleteAppRequest` | `apps/...` | 設計器動作＋系統管理員 | 再加權限設定 |
| `SetGroupMembersRequest`、`CreateUserRequest`、`UpdateUserRequest`、`DeleteGroupRequest` | `admin/groups/...`、`admin/users/...` | 系統管理員 | 再加權限設定 |
| `UpdateGroupSystemPrivilegesRequest`（新） | `PUT admin/groups/{id}/system-privileges` | — | 系統管理員＋權限設定 |
| `UpdateUserSystemPrivilegesRequest`（新） | `PUT admin/users/{id}/system-privileges` | — | 系統管理員＋權限設定 |
| `CreateEventRequest`、`UpdateEventRequest` | `POST events`、`PUT events/{key}` | 設計器修改＋主檔守衛 | 不變；含「允許複製被隱藏的值」時另走 gate（§7.3、M5） |
| `GetMeRequest` | `GET me` | 登入即可 | 不變；回應多 `permissionAdmin` |

表外說明：

- 宣告 `IPermissionAdminRequest` 的正式請求共 13 個（表中「再加」與「新」的列）。只要求系統管理員的那些，對管理員來說宣告是冗餘的（管理員自動有），刻意宣告有三個作用：「寫授權資料的請求都宣告」這條規則沒有例外；寫入邊界的 `Authorized` 許可只由 `PermissionAdminGate` 開，behavior 也經它（§7.4）；MCP 通道經同一個 gate 一律拒絕（M8）。哪天權限軸-2 改成管理員不自動擁有，這些請求不必再回頭找（T7）。
- `SetUserActive`、`ResetPassword`、部門指派、`RetryIntegrationRequest`、`SetIntegrationEnabledRequest`、`ImportAppPackageRequest` 不寫授權資料，不宣告。
- 仍歸「設計」、不改守衛的（使用者裁決）：簽核流程（`ApprovalFlowRequests.cs` 的建立、修改、草稿、發布、試算）、狀態機（`SaveWorkflowRequest`，含轉移的群組限制）、共用檢視（清單頁「共用」切換，設計器修改）、任務規則、跨記錄檢查、轉換、列印範本、分錄、欄位與參照帶值、帶表頭的值、公式、彙總。
- 其餘 50 多個 `IDesignerRequest` 不動。

### 7.3 條件式檢查

**從範本建立**（主控裁決 M3，待使用者確認；推翻分流 D4 對範本的部分「要求兩軸都有」）：從範本建立實際要設計器新增＋修改——依範本內容，巢狀請求會用到 `UpdateBlock`、`UpdateField`、`ReorderFields`、`UpdateModule`、`SaveRules`、`SetFieldStatus`，都宣告設計器修改。`TemplateInstantiator` 第 10 步在 `app is null`（從範本建立，不是開啟套件）且 `PermissionAdminGate.CheckAsync` 不是 `Allowed` 時，整步略過，在回應既有的 `warnings` 加一則「你沒有『權限設定』權限，已略過範本的權限建議；請洽權限設定者到權限分頁授權」，不送 `SaveModulePermissionsRequest`。理由：範本主要是給設計者用的，內建範本幾乎都帶群組權限建議；D4 的做法會讓沒有權限設定的設計者完全用不了範本，而略過權限只是回到「新模組預設沒人有權限」（顧問裁決權限軸-5），沒有開洞。開啟套件（`app` 不為 null）不略過：照常送出，沒有權限設定就 403、整批回滾（fail closed；實際上開啟要系統管理員，不會發生）。

**產生 Demo 資料**（主控裁決 M4，待使用者確認）：`StartDemoDataRequest`、`GenerateDemoDataRequest` 都宣告 `IPermissionAdminRequest`。檢查發生在 `StartDemoDataHandler` 之前（behavior），也就在 `runner.TryStart()` 切到背景、`ActingScope.Begin("admin")` 之前；背景的 `GenerateDemoDataRequest` 以 admin 身分送，自己的 behavior 通過並開寫入許可（背景工作 `SuppressFlow`，帶不到前台的許可）；它另宣告 `ISystemAdminRequest`，許可才帶 `SystemAdminVerified`，`DemoPeople` 寫示範帳號的群組成員才不會被擋（T13）。它以管理員身分開套件、寫欄位權限與選項範圍，等於批次授權；Demo 站訪客以 admin 登入，不受影響。

**連動事件「允許複製被隱藏的值」**（主控裁決 M5，待使用者確認）：`CreateEventRequest`、`UpdateEventRequest` 的內容只要有任一個對應 `allowHiddenSource = true`，handler 在任何驗證之前先問 `PermissionAdminGate.CheckAsync`，`NotPermissionAdmin` 回 403 `eventHiddenSourceNeedsPermissionAdmin`、`McpChannel` 回 403 `mcpPermissionAdminDenied`，不寫入。理由：這個旗標字面上就是「允許把某些群組看不到的值複製到別的模組」，連動事件又以系統身分執行（`SystemIdentity`，不看任何人的權限），性質和「不提供給 AI」旗標一樣是放寬可見性的授權決定。判斷只看請求內容、不比對既有定義（T8）：已勾旗標的事件，沒有權限設定的設計者連改其他地方也會被擋，訊息說明原因；fail closed，比「只擋新勾」少一次讀庫和一種繞法（先取消再重勾）。`allowHiddenSource` 存在事件定義的 JSON 裡，寫入邊界看不到（§7.4），所以這條只靠 gate 與 PAX-13 固定。

**M5 擴大：一般隱藏欄位也納入公式依賴閉包**（主控裁決，待使用者確認）：今天 `EventValidator.HiddenAsync` 只對「限定可見的貢獻欄位」做公式閉包，對一般 `Hidden` 欄位沒有。設計者建立公式 F1 = `{薪資} * 1`（薪資對某群組隱藏），事件從 F1 複製，就不必勾旗標。改成：`Hidden` 欄位與限定可見欄位合併後一起逐層做閉包（`FormulaGraph.EvaluationOrder`），引用它們的公式（含公式引用公式）都算隱藏來源。`UpdateFieldHandler` 改公式時已經比對 `HiddenAsync` 前後差異擋下「公式改了之後變成看得到隱藏值」，會自動跟著新規則。效果見 §6.3 S1、PAX-13b。

**「不提供給 AI」欄位旗標**（使用者裁決歸授權；旗標本身在 mcp1a 後端、mcp1b 畫面才出現）：契約寫在這裡，由 mcp1a 照做——`AddFieldRequest` 帶 true、`UpdateFieldRequest` 的值和現存值不同，都要把欄位的修改與存檔包進 `PermissionAdminGate.RunAsync`，沒有權限設定就 403 `permissionAdminRequired`（MCP 通道 `mcpPermissionAdminDenied`）；寫入邊界把這個欄位的變動列為守衛（§7.4）。套件與範本帶的旗標照開啟／從範本建立的規則（開啟要管理員；從範本建立沒有權限設定時旗標一律寫 false 並加一則 warnings，和 M3 同精神）。

**帶表頭的值依呼叫端可見性**（主控裁決 M6，待使用者確認；修核心設計 §15 第 55 條）：見 §12.2。

### 7.4 授權寫入邊界與回歸防線

反射列出「宣告了 `IPermissionAdminRequest` 的型別」只能發現宣告數量改變，抓不到「新 handler 忘了宣告、直接寫表」。所以防線放在資料寫入本身：**追蹤式寫入**（`Add`／`Remove`／改屬性後 `SaveChanges`）一律經 SaveChanges 攔截器逐列檢查；**批次寫入**（`ExecuteDelete*`／`ExecuteUpdate*`）只准走四個登記的批次入口，入口在執行期檢查許可與參數；其他地方的批次寫入與任何對守衛表的原生 SQL 由清單測試禁止（T9；不採 Roslyn 分析：要另寫並維護分析器規則）。第二輪核對後拿掉了指令層的 SQL 文字比對（取捨見本節末）。

**守衛表**（Domain 常數 `AuthorizationTables`）：`Dynamic_GroupProgramPrivilege`、`Dynamic_UserProgramPrivilege`、`Dynamic_FieldPermission`、`Dynamic_ModuleDataScope`、`Dynamic_FieldOptionScope`、`Dynamic_FieldGrant`、`Dynamic_AppGrant`、`Dynamic_ModuleActionScope`、`Dynamic_UserGroupMapping`；另加 `Dynamic_Field` 的兩個守衛欄：`VisibilityRestricted` 改成 false（放寬可見性；今天沒有任何程式這樣改）、`ExcludeFromAi` 的任何寫入（mcp1a 起，含新增時為 true）。不列：`Dynamic_PermissionGroup`（群組本身沒有權限內容）、報表分享、儀表板指派、任務檢視分享（共用檢視，使用者裁決歸設計）。

**寫入許可**（Domain 的 `AuthorizationWriteScope`）：許可是一個物件，掛在 `AsyncLocal` 上，**只能用 `AuthorizationWriteScope.RunAsync(permit, body)` 開**——同一個 async 方法裡設定、`await body()`、`finally` 還原外層並把許可標為已關閉。沒有回傳許可給呼叫端的 API（不提供 `BeginAsync` 或 `using` 形式），所以不會出現「在 async helper 裡設定 `AsyncLocal`、回到呼叫端已經不在」的誤擋。規則：

- 守衛寫入的存檔（`SaveChanges`）必須發生在 `body` 內；`body` 結束時 ChangeTracker 還有未存的守衛變更就丟 `InvalidOperationException`，訊息寫「守衛變更要在許可內存檔」（不然會在之後的存檔才被擋，原因難查）。
- 巢狀時只看最內層許可（內層一定比外層窄：例如 `Authorized` 內開 `Cleanup`）。許可沿 `await` 往內傳，巢狀 mediator 請求沿用外層許可。
- 已關閉的許可一律無效：被 `Task.Run` 或未等待的工作帶走的副本，在 `body` 結束後寫入會被擋。`ExecutionContext.SuppressFlow` 起的背景工作帶不到許可，要自己的請求開。

| 種類 | 唯一開的位置 | 追蹤式寫入准許 | 批次入口准許 |
|---|---|---|---|
| Authorized | `PermissionAdminGate.RunAsync`（behavior 與條件式都經它） | 守衛表全部（列層規則見下） | `ReplaceModuleRowsAsync`、`ReplaceActionScopesAsync` |
| Derived（欄位、應用程式、群組集合） | `MasterDesigner.BindFieldAsync` | 只准新增 `FieldGrant`，`SourceKind = App` 且欄位、應用程式、群組都在許可參數內 | 無 |
| Cleanup（模組或欄位集合） | `DeletionRepository.RunCleanupAsync` | 只准刪除屬於目標的列 | `PurgeModuleAsync`、`PurgeFieldsAsync`，目標要在許可參數內 |
| Seed | `SeedData`（含 `PermissionAxesImport`） | 全部 | 全部 |
| Test | 測試專案的 `RunForTestsAsync` | 全部 | 全部 |

- 列層規則：寫 `ProgramCode ∈ SystemProgramCodes.All` 的列、寫 `Dynamic_UserGroupMapping`，許可上要有 `SystemAdminVerified`（只有同時宣告 `ISystemAdminRequest` 的請求經 gate 開的許可才有；Seed、Test 視為有）。權限設定者的請求沒有這個標記，就算哪天 `SaveModulePermissions` 被改壞、寫到系統代碼列，也會丟例外。
- `Cleanup` 由 `RunCleanupAsync(目標, body)` 開：`FieldPurge.RemoveAsync`、`ModuleRemover.RemoveAsync` 把整段清除包進去；`body` 結束後在同一交易內查目標欄位或模組已不存在，還在就丟例外、整筆回滾。這保證「刪掉的授權列一定隨上層一起消失」：只刪 `FieldPermission`、不刪欄位，會讓原本被隱藏的群組看得到，所以不能只看「是不是刪除」。目標模組涵蓋它的全部欄位、選項與 program 代碼。
- `DemoPeople`（產生 Demo 資料時加示範帳號與成員）不自己開許可，沿用背景 `GenerateDemoDataRequest` 的 `Authorized`；成員寫入要 `SystemAdminVerified`，所以該請求另宣告 `ISystemAdminRequest`（T13，背景以 admin 送，通過）。

**批次入口**（`ExecuteDeleteAsync`、`ExecuteUpdateAsync` 對守衛表只准出現在這四個方法；方法第一個寫入之前呼叫 `AuthorizationWriteScope.Demand(入口, 目標)`，不符就丟例外）：

| 入口 | 刪除範圍（由參數固定） | 另外檢查 |
|---|---|---|
| `PermissionRepository.ReplaceModuleRowsAsync(module, rows)` | 該模組 program 代碼的群組與個人列、該模組的資料範圍、欄位權限、選項範圍 | `module.ProgramCode ∈ SystemProgramCodes.All` 一律拒絕，不看許可 |
| `SharedMasterRepository.ReplaceActionScopesAsync(moduleId, rows)` | 該模組的動作範圍 | — |
| `DeletionRepository.PurgeModuleAsync(module)` | 該模組全部授權列與 program 列 | 模組是 Cleanup 目標 |
| `DeletionRepository.PurgeFieldsAsync(fields)` | 這些欄位的欄位權限與選項範圍 | 欄位都在 Cleanup 目標內 |

批次刪除之後的新列都用 `AddRange` 追蹤寫入，所以每一列照樣經攔截器的列層檢查。`Dynamic_Field` 的兩個守衛欄不准批次寫入（`ExecuteUpdate*`），只能經追蹤式寫入改；欄位定義本身整列刪除不放寬可見性，不在此限。

**`AuthorizationSaveChangesInterceptor`**（Persistence，`SaveChangesInterceptor`，SQLite 與 PostgreSQL 都註冊，照既有 `SqliteMmapInterceptor`、`PostgresWriteLockInterceptor` 的掛法）：`SavingChanges` 先呼叫 `ChangeTracker.DetectChanges()`（EF 在攔截器之後才偵測變更，不先做會漏掉只改屬性、沒標記的變更），再逐筆看守衛型別的 Added／Modified／Deleted，照上表與列層規則判斷。`Field` 的守衛欄看「屬性被標為已修改且新值是放寬的值」（`VisibilityRestricted` 為 false；`ExcludeFromAi` 任何值；新增時 `ExcludeFromAi` 為 true），不靠原值，離線實體 `Update` 的原值不可信時一樣擋（寧可誤擋）。

**EF migration**：`MigrateAsync` 不經 `SaveChanges`，攔截器不涉入，不需要許可。migration 只用 EF 產生（`.claude/rules/backend.md`），清單測試另禁止 migration 檔對守衛表寫資料；PAX-01b 以兩個 provider 跑「上一版 schema 加資料 → 新版啟動」的升級冒煙。

**資料庫外鍵的 cascade**（刪欄位連帶 `FieldGrant`；刪群組、模組、應用程式連帶 `AppGrant`、`ModuleActionScope`、`FieldGrant`）不經 EF 指令，也不經攔截器。它們屬刪除清理：上層列已刪除，消失的列不會放寬任何人對仍存在資料的可見性。

**寫入路徑分類**（對照全部 controller，2026-11-19 以 856364f5 逐一查 `ReplaceModuleRowsAsync`、`ISharedMasterRepository` 的寫入方法、`ExecuteDeleteAsync`、`new 守衛型別` 的呼叫端）：

| 端點（controller） | 寫入點 | 分類 | 守衛 |
|---|---|---|---|
| `PUT modules/{key}/permissions`（Permissions） | `ReplaceModuleRowsAsync`；`MasterGrants.SaveAsync`、`SaveFieldGrantsAsync` | 授權修改 | 權限設定＋主檔守衛 |
| `POST templates/{key}/instantiate`（Templates） | 第 10 步巢狀 `SaveModulePermissionsRequest` | 授權修改 | gate；巢狀請求自己的 behavior |
| `POST apps/packages/{id}/open`、`POST apps/{key}/close`、`/reopen`、`DELETE apps/{key}`（Apps） | `AppOpener`（`AddFieldGrant`、`ReplaceModuleRowsAsync`）、`MasterGrants.GrantAsync`、`RecalcAsync`、`MasterLifecycle.RecomputeAsync`、`DeleteApp` 的清除 | 授權修改 | 系統管理員＋權限設定 |
| `POST demo-data/generate`（DemoData） | 背景 `GenerateDemoDataRequest` → `DemoStructure` → `AppOpener`、`DemoPeople` | 授權修改、成員 | Start、Generate 都宣告權限設定；Generate 另宣告系統管理員 |
| `PUT admin/groups/{id}/system-privileges`、`PUT admin/users/{id}/system-privileges`（新） | 系統代碼列 | 系統權限 | 系統管理員＋權限設定，列層要 `SystemAdminVerified` |
| `PUT admin/groups/{id}/members`、`POST admin/users`、`PUT admin/users/{id}`（AdminGroups、AdminUsers） | `SetGroupMembersAsync`、`SetMembershipsAsync` | 成員 | 同上 |
| `DELETE admin/groups/{id}`（AdminGroups） | `DeleteGroupAsync`（成員與五種授權列） | 授權修改 | 同上 |
| `POST modules/{key}/fields`（Fields，貢獻者區塊裡） | `MasterDesigner.BindFieldAsync` → `AddFieldGrant` | 受保護的衍生寫入 | 主檔守衛（系統管理員）＋ Derived |
| `DELETE modules/{key}`、`DELETE fields/{id}`、`DELETE blocks/{id}`（Deletion） | `ModuleRemover`、`FieldPurge` 經 `RunCleanupAsync` → `PurgeModuleAsync`、`PurgeFieldsAsync` | 刪除清理 | 設計器刪除＋ Cleanup（結束時驗目標已刪） |

表外說明：

- `BindFieldAsync` 只在共用主檔模組上、貢獻者區塊裡新增欄位時寫；它把「這個貢獻者應用程式在此模組已有權限來源的群組」複製成新欄位的可見列，不會讓新的群組看到東西。主檔模組本來就要系統管理員（`MasterDesignerBehavior`），所以分在「受保護的衍生寫入」而不是「授權修改」，Derived 只准它這一種寫法。
- 刪除清理只刪「隨上層一起消失」的列：被刪欄位的欄位權限、被刪選項的選項範圍、被刪模組的全部授權列與 program 列。
- 無關（不寫守衛表）：AdminDashboards、ApprovalFlows、Approvals、Attachments、Auth、Checks、Conversions、Dashboards、Departments、Entities、Events（`allowHiddenSource` 在 JSON 定義裡，走 gate，§7.3）、ImportExport、Ledger、Modules、Notifications、Ops、Policies、PrintTemplates、Records、Relations、Reports、Rules、Schema、TaskBoard、TaskRules、Tasks、TaskViews、Theme、Views、Workflow；Apps 的匯入、預覽、整合、匯出；Templates 的匯入、匯出；Permissions 的三支 GET；AdminUsers 的重設密碼、停用、啟用、部門。`ApprovalReadGrant` 只在記憶體組臨時權限輸入，不寫庫。

**清單測試**（`AuthorizationWriteInventoryTests`，PAX-23；原始碼掃描照既有 `NoBusinessWordsTests` 的寫法掃 `backend/src/**/*.cs`）：

1. 反射：實作 `IPermissionAdminRequest` 的正式型別恰為 §7.2 的 13 個。
2. 開許可的位置：`AuthorizationWriteScope.RunAsync` 的呼叫恰為上表四處（gate、`BindFieldAsync`、`RunCleanupAsync`、`SeedData`），種類相符；`RunForTestsAsync` 不得出現在 `src`。新增要改清單並在測試註解寫理由。
3. 守衛實體的取得點：`Set<守衛型別>` 只准出現在登記檔案（`PermissionRepository`、`SharedMasterRepository`、`AccountRepository`、`DeletionRepository`、`DemoPeople`、`PermissionAxesImport`）；`src` 不得有型別參數不具體的 `Set<T>()`、`Set(typeof…)`；`src` 任何成員（含 Domain 的 repository 介面）的回傳型別、參數、欄位、屬性都不得是 `IQueryable<守衛型別>`、`DbSet<守衛型別>` 或其衍生介面，守衛列只以已具體化的 `List`／`IReadOnlyList` 交出登記檔案。
4. 模型掃描：沒有任何 navigation 指向守衛型別（否則別的實體能經導覽屬性查出守衛列再批次刪）。加上 3，`Set<守衛型別>` 就是在 EF 裡取得守衛列的唯一途徑。
5. 批次指令（全域、逐方法登記）：`src` 每一個呼叫 `ExecuteDelete*`／`ExecuteUpdate*` 的方法都要在清單裡（檔案＋方法名），分兩類：四個守衛批次入口（第一個寫入前呼叫 `Demand`），以及其餘非守衛的批次方法（現有約 14 個檔案，實作時照掃描結果登記，例如 `OpsIncidentRepository`、`SeedData` 的 `OwnerUserId` 更新）。非守衛批次方法的每個呼叫，接收的查詢要能在同一個方法內、或同檔案的私有屬性／欄位定義裡，沿呼叫鏈追到 `Set<具體的非守衛型別>()`；追不到（接收者是參數、別的檔案的成員、方法回傳值）就判違規，寧可誤擋。`src` 中呼叫 `ExecuteUpdate*` 的方法內不得出現 `VisibilityRestricted`、`ExcludeFromAi`（`SetProperty` 的 lambda 與字串屬性名都含這兩個識別字，經 `Module` 的導覽屬性取得欄位也一樣抓得到）。新增批次方法要改清單並在測試註解寫理由。
6. 原生 SQL：EF 的 `ExecuteSql*`／`FromSql*`／`SqlQuery*`、Dapper、`CreateCommand` 的使用檔案恰為登記清單（現有：記錄與報表查詢、報表快照、SQLite 健康檢查與 mmap、PostgreSQL 寫入鎖與啟動鎖，實作時照掃描結果登記並逐檔確認不寫守衛表）。`src` 的字串常值不得含守衛表名或 `Dynamic_Field`（含加引號、加 schema 的寫法），窄例外只有三處：`Domain/Data/Configurations/` 的 `ToTable` 等模型設定、`AuthorizationTables` 常數檔本身、兩個 provider 的 `Migrations/` 目錄（migration、`.Designer.cs`、`ModelSnapshot` 的 schema 定義）。例外處的資料寫入仍由 7 禁止。
7. migration 檔（兩個 provider 的 `Migrations/`，不含 `.Designer.cs`、`ModelSnapshot`）不得有對守衛表或 `Dynamic_Field` 的 `Sql`、`InsertData`、`UpdateData`、`DeleteData`；`CreateTable`、`AddColumn`、`CreateIndex` 等 schema 操作允許。
8. 分層：Application 不得引用 `DbContext`；WebAPI 只准登記的 `Program.cs`、`SessionMiddleware`、`AdminBootstrap`，且這三個檔案不得出現守衛型別。
9. 模型裡表名含 `Privilege`、`Permission`、`Scope`、`Grant`、`GroupMapping` 的實體必須在守衛表或排除清單（`Dynamic_PermissionGroup`）裡；守衛表常數裡的每一張表都存在於模型（防改名後漏守）。
10. 掃描器自測（違規樣本各回報一筆、合法樣本回報零，兩邊都要測）：
    - 違規：對 `UserGroupMapping` 下 `ExecuteDeleteAsync`；經 `Set<Module>()` 的導覽屬性取得欄位再 `ExecuteUpdateAsync` 改 `VisibilityRestricted`；`"DELETE FROM public.\"Dynamic_FieldPermission\""`；`WITH x AS (…) DELETE FROM "Dynamic_FieldGrant"`；兩個陳述式串在一起；未登記檔案用 Dapper `ExecuteAsync`；登記檔案內經變數或屬性取得 `Set<FieldGrant>()` 再批次刪；**跨檔傳遞查詢**——登記 repository 的方法回傳 `IQueryable<FieldGrant>`，另一個檔案接住後 `ExecuteDeleteAsync`；接收者是方法參數 `IQueryable<T>` 的批次刪；未登記的方法對非守衛型別批次刪；migration 的 `UpdateData`。
    - 合法：`SharedMasterConfigurations` 的 `ToTable("Dynamic_FieldGrant")`；`AuthorizationTables` 的常數；migration 的 `CreateTable(name: "Dynamic_FieldGrant", …)`；`.Designer.cs` 與 `ModelSnapshot` 的 `b.ToTable("Dynamic_FieldGrant")`；登記的非守衛批次方法對 `Set<Record>()`（含以子查詢當條件）批次刪；同檔案私有屬性 `Incidents => db.Set<OpsIncident>()` 再 `ExecuteUpdateAsync`。

**涵蓋範圍與取捨**（不宣稱更多）：

- 執行期 fail closed 的是**追蹤式寫入**（攔截器逐列）與**四個批次入口**（`Demand` 查許可與參數）。
- 其餘批次與原生 SQL 寫入是**開發期** fail closed：程式裡沒有不經 3、4、5、6 的方式取得守衛表，出現就讓清單測試失敗、CI 擋下合併；執行期不另檢查。直接 ADO.NET 或 Dapper 不在攔截器範圍內，靠 6 禁止。
- 掃不到的：登記的原生 SQL 檔案裡在執行期拼出守衛表名的 SQL、反射。登記時逐檔審查，現有登記檔案只動記錄表、報表、PRAGMA 與鎖。程式外直接連資料庫的寫入不在本設計範圍。
- 拿掉指令層 SQL 文字比對（`DbCommandInterceptor`）：要 fail closed 就得拒絕所有無法分類的寫入，而 EF 自己的存檔批次（多陳述式、`RETURNING`、`MERGE`）也會落入無法分類，等於要另寫 SQL 解析器；它又看不到列內容，擋得下的只有「有沒有許可」，列層規則終究要靠窄入口。留著只會給「執行期全涵蓋」的錯覺，又多一個會誤擋 migration 的地方。

**負向案例**（測試專案內的測試用請求與 handler，不進正式組件）：見 PAX-23a–k。

## 8. API

- `GET /api/v3/dynamic/me`：`MeResponse` 加 `permissionAdmin: boolean`；`designer` 四個布林與 `permissionAdmin` 都是**有效結果**（系統管理員自動擁有、群組與個人合併、M1 的隱含讀取、停用為 false）。例：`{ ..., "designer": { "read": true, "add": false, "edit": false, "delete": false }, "permissionAdmin": true }`。
- `PUT /api/v3/dynamic/admin/groups/{id}/system-privileges`、`PUT /api/v3/dynamic/admin/users/{id}/system-privileges`：
  - 請求（整份取代，存的是該群組或該使用者個人的**直接設定**）：

    ```json
    { "designer": { "read": true, "add": false, "edit": true, "delete": false }, "permissionAdmin": true }
    ```

  - 五個布林都必填。body 型別全用 `bool?`、`designer` 物件可為 null，不讓模型繫結把缺漏當成 false；controller 照既有 `SetMembers` 的 null 檢查寫法，缺鍵或 `null` 回 400，一次列出全部缺的鍵：

    ```json
    { "traceId": "0HN…", "errors": { "designer.edit": ["必填"], "permissionAdmin": ["必填"] } }
    ```

    錯誤文字用 `MessageText.Required`，依 `X-Locale` 回語系。值不是布林（例如字串）照 ASP.NET 既有的繫結錯誤回 400。
  - 200：回應同請求形狀，是存檔後的直接設定。
  - 寫法：`DYN00000` 列照四個布林（全 false 就刪列，`CanPrint`、`CanAmt`、`CanApprove` 一律 false）；`SYS00001` 列只寫 `CanEdit`（false 就刪列）。同一交易。
  - 宣告：`ISystemAdminRequest`、`IPermissionAdminRequest`、`ITransactionalRequest`、`IAuditedRequest`（`group.systemPrivileges`／`user.systemPrivileges`）。
  - 錯誤：不是系統管理員 403 `{ "message": "沒有權限執行此操作" }`（沒有 `code`，既有 `Forbidden`）；MCP 通道 403 `mcpPermissionAdminDenied`（§10）；群組或使用者不存在 404 `{ "message": "…" }`（`ResourceNotFound`）。
  - 目標是系統管理員也照存（管理員旗標日後取消時才生效），回應照存的值；畫面另外標示「系統管理員自動擁有」（§11）。
- `GET admin/groups`：每一列加 `systemPrivileges`＝該群組的直接設定（群組列本身就是來源，沒有繼承）。
- `GET admin/users`：每一列加三個欄位，**直接設定與有效結果分開**：

  ```json
  {
    "userId": "wang",
    "systemPrivileges": { "designer": { "read": false, "add": false, "edit": false, "delete": false }, "permissionAdmin": false },
    "effectiveSystemPrivileges": { "designer": { "read": true, "add": true, "edit": true, "delete": false }, "permissionAdmin": true },
    "systemPrivilegeSources": [
      { "kind": "group", "groupId": 3, "groupName": "IT", "designer": { "read": true, "add": true, "edit": true, "delete": false }, "permissionAdmin": true }
    ]
  }
  ```

  - `systemPrivileges`：個人直接設定；編輯對話框載入與存回的只有它。
  - `effectiveSystemPrivileges`：和 `GET me` 同一套算法（含 M1、管理員自動、停用為 false），唯讀，清單標籤用它。
  - `systemPrivilegeSources`：提供系統權限的群組（只列有任一系統權限的群組），系統管理員另有一筆 `{ "kind": "systemAdmin" }`；個人那份不重複列。
- `GET admin/groups/{id}/impact`：加 `systemPrivileges`（該群組的直接設定），刪除對話框據此提醒（§11）。
- 沒有新的讀取端點；權限分頁的讀取照舊。

## 9. 稽核與安全事件

**稽核**（都只記成功，照 O2-U9）：

- `permission.update`：行為者改成「權限設定者」語意，內容不變（只記變動的格子）。權限設定者改自己的群組或個人權限也是這一筆，可從「行為者」與格子裡的群組或使用者對出來（顧問裁決權限軸-漏2）。
- `group.systemPrivileges`、`user.systemPrivileges`（新）：`Detail("before")`、`Detail("after")` 記兩邊的直接設定（§8 的請求形狀）。
- `template.instantiate`：M3 略過權限時 `Detail("permissionsSkipped", true)`。
- `event.create`、`event.update`：既有；含 `allowHiddenSource` 的對應已在定義快照裡，稽核可查誰在何時勾的。
- 簽核流程發布（`approvalFlow.publish`）、狀態機存檔：既有稽核就是「設計者把自己設成簽核人」的查證點（§13 L1）。

**安全事件 `privilege.changed`**（顧問裁決權限軸-6，待使用者確認；觸發點由主控裁決 M9 擴大，待使用者確認）：

- 判斷依「實際持有者」而不是「哪支 API」：有人因為這次請求開始或不再持有設計器任一動作或權限設定，就發一次。例如系統管理員把小王加進持有權限設定的群組，雖然沒呼叫系統權限 API，小王實際上拿到了權限設定，一樣要通知。
- **快照**：`SystemPrivilegeHolders.SnapshotAsync()` 回「非系統管理員使用者 → 有效值」，有效值＝`{ designer: ResolveProgram(DYN00000) ∩ {讀取, 新增, 修改, 刪除}，有權限設定時再併入讀取（M1）; permissionAdmin: ResolveProgram(SYS00001) 含修改 }`，群組與個人逐項合併。不看帳號啟用與否（停用、啟用帳號不算異動，不必掛在 `SetUserActive`）。兩者皆空的人不列（等同沒有）。系統管理員不在快照裡，管理員旗標異動照舊由 `admin.changed` 負責。
- **比對**：以下 handler 在寫入前後、同一交易內各取一次：`UpdateGroupSystemPrivileges`、`UpdateUserSystemPrivileges`、`SetGroupMembers`、`CreateUser`（帶群組）、`UpdateUser`（改群組）、`DeleteGroup`。前後聯集裡有效值不同的人就是異動者。`SaveModulePermissions` 寫不到系統代碼列（§7.4），不必比對。
- **沒有異動就不發**：空群組被授予、目標是系統管理員、移出一個群組但另有群組給同樣權限、原值存檔，都只記稽核、不發事件。
- **事件種類**：任一人多了任一動作或權限設定就是 granted（同時有人被收回也算 granted，明細裡兩邊都列）；只有收回才是 revoked。
- **明細形狀**：handler 呼叫 `AuditContext.MarkPrivilegeChanged(granted)`，並 `Detail("privilegeHolders", …)`：

  ```json
  {
    "total": 2,
    "truncated": false,
    "items": [
      { "userId": "u3", "before": { "designer": [], "permissionAdmin": false }, "after": { "designer": ["read", "edit"], "permissionAdmin": true } },
      { "userId": "u4", "before": { "designer": ["read", "edit"], "permissionAdmin": false }, "after": { "designer": ["read"], "permissionAdmin": false } }
    ]
  }
  ```

  `designer` 陣列固定依讀取、新增、修改、刪除的順序（`read`、`add`、`edit`、`delete`）；`items` 依 `userId` 序數排序；最多 20 筆，`total` 是實際異動人數，超過時 `truncated: true`。
- `AuditBehavior` 寫完稽核、有 Id 之後同交易呼叫 `SecurityIncidents.PrivilegeChangedAsync`，寫法照 `AdminChangedAsync`：指紋 `security`＋`privilege.changed` 聚合成一件、累加次數；每次異動都發站內通知、寫寄送佇列（受 `ops.alertEmailMaxPerHour` 節流，不另設安全專用節流）；樣本只存稽核 Id 與行為者；連結 `/system/audit?id=…`。快照、寫入、稽核、事件、通知同一交易，任一步失敗全部回滾（PAX-21b）。
- 標題四語（`IncidentTitles.PrivilegeChanged`、`AlertMailText.TitlePrivilegeGranted／Revoked`）：「有人取得設計器或權限設定權限」「有人的設計器或權限設定權限被收回」。
- 一般的模組權限存檔（`permission.update`）只記稽核、不發事件（權限軸-6 選項 A）。

## 10. 錯誤碼

`code` 是給前端與整合方判斷的機器代碼（camelCase，照既有 `stale`、`smtpPlaintextAuth`）；訊息鍵在 `MessageText`（PascalCase），四語文案在 `MessageText.cs`，依 `X-Locale` 回語系。`ServiceResult` 新增 `Forbidden(MessageRef message, string code)` 建構方式（設 `IsForbidden`、`MessageSource`、`Code`），`ServiceResultExtensions.Body` 照既有邏輯輸出 `code`，所以 403 回應形狀是：

```json
{ "message": "修改權限需要「權限設定」權限，請洽系統管理員", "code": "permissionAdminRequired" }
```

| code | 訊息鍵 | HTTP | 何時 |
|---|---|---|---|
| `permissionAdminRequired`（新） | `PermissionAdminRequired` | 403 | `IPermissionAdminRequest` 或 gate 擋下（含 mcp1a 的 AI 旗標） |
| `eventHiddenSourceNeedsPermissionAdmin`（新） | `EventHiddenSourceNeedsPermissionAdmin` | 403 | 事件含「允許複製被隱藏的值」而沒有權限設定 |
| `mcpPermissionAdminDenied`（新，mcp1a 起） | `McpPermissionAdminDenied` | 403 | 通道是 MCP，任何要權限設定的請求或 gate |
| 無 | `Forbidden`（既有） | 403 | 不是系統管理員（系統權限 API、成員、主檔模組權限）；沒有設計器動作 |
| 無 | `Required`（既有） | 400 | 系統權限 API 缺鍵或 null，`errors` 逐鍵列出 |
| 無 | `ResourceNotFound`（既有） | 404 | 系統權限 API 的群組或使用者不存在 |

繁中文案：`PermissionAdminRequired`「修改權限需要「權限設定」權限，請洽系統管理員」；`EventHiddenSourceNeedsPermissionAdmin`「允許複製被隱藏的值需要「權限設定」權限，請洽系統管理員」；`McpPermissionAdminDenied`「AI 連線不能修改權限或帳號，請到系統畫面操作」。

測試斷言 HTTP 狀態、`code`、以及至少 `zh-TW` 與 `en` 兩種 `X-Locale` 的 `message`，不只比對繁中文字。範本略過權限是成功回應裡的 warnings 文字，不是錯誤碼。

## 11. 畫面（權限軸-b）

- **群組頁編輯對話框**、**使用者頁編輯對話框**：加「系統權限」區塊——設計器「讀取、新增、修改、刪除」四個勾選框，「權限設定（含設計器讀取）」一個開關；說明文字一行「權限設定可以決定誰看得到、改得到資料，但不能授予這兩個系統權限」。區塊載入與存回的都是直接設定（`systemPrivileges`）。使用者對話框另顯示唯讀一行「另經群組取得：設計器（讀取、修改）來自『IT』；權限設定來自『IT』」（`systemPrivilegeSources`），沒有來源就不顯示。存檔時主資料存成功後再送 `system-privileges`（兩個請求；第二個失敗時提示「名稱與成員已儲存，系統權限未儲存：原因」）。使用者是系統管理員時區塊照常可編，上方提示「系統管理員自動擁有設計器與權限設定；以下設定在取消系統管理員後才生效」。
- **群組清單、使用者清單**：加一欄「系統權限」，顯示「設計器」「權限設定」小標籤；群組清單用直接設定，使用者清單用有效結果（`effectiveSystemPrivileges`）；手機版卡片同樣顯示。
- **刪除群組對話框**：影響清單多一行「此群組提供的系統權限會移除：設計器（○○）、權限設定；成員若從其他群組或個人設定取得，仍會保留」。
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
- **處理（M6）**：`DetailWriter.PrepareAsync` 加參數 `headerPermissions`，由 `CreateRecordHandler`（新增動作）、`UpdateRecordHandler`（修改動作）傳入它們已經拿到的**表頭有效權限**。注意 `PrepareAsync` 迴圈裡的 `perms` 是 `AuthorizeDetailAsync` 回的**明細**權限，不能拿來過濾表頭值。`HeaderOnceAsync` 取得表頭值後，拿掉 `!headerPermissions.CanSee(field)` 的欄位；`HiddenFieldIds` 已涵蓋欄位層隱藏、限定可見的貢獻欄位、沒有金額權限時的金額欄位與金額彙總、引用隱藏欄位或金額結果的公式（`PermissionResolver.Resolve`）。明細欄位因此得到空值（和沒有預設值一樣）。帶表頭的值只套在新增的明細列（`rows[i].Id is null`），既有明細列不改寫。畫面行為不變（前端本來就拿不到隱藏欄位的值），只有直接呼叫 API 的結果改變（§6.3 S2）。核心設計 §15 第 55 條標「權限軸-a 已處理」（§21）。

### 12.3 連動事件

- **證據**：`EventService/EventValidator.CheckSetsAsync` 與 `HiddenAsync`。事件以系統身分執行（`SystemIdentity`、`DynamicAccessGuard.SystemPermissions`），誰存檔觸發都一樣。建立時檢查來源欄位是否被某群組設成「隱藏」或是限定可見的貢獻欄位，有就要求勾 `allowHiddenSource`。**初稿寫「及引用它們的公式」不完全符合程式**：`HiddenAsync` 只對限定可見欄位做公式閉包，一般 `Hidden` 欄位沒有。
- **路徑一（勾旗標）**：設計者勾「允許複製被隱藏的值」，就能把任何人看不到的欄位複製到他讀得到的模組。**M5 改成要權限設定**（§7.3）。
- **路徑一之一（經公式繞過旗標）**：設計者建公式 F1 = `{薪資} * 1`（薪資被設成隱藏），事件從 F1 複製或在事件公式寫 `{F1}`，今天不必勾旗標。**M5 擴大，本段封住**：一般隱藏欄位也納入公式閉包（§7.3），多層公式（F2 = `{F1} + 0`）一併涵蓋（PAX-13b）。
- **路徑二（不必勾旗標）**：`HiddenAsync` 不看「金額遮罩」與「讀不到整個來源模組」。設計者讀不到的模組，或沒有金額權限看不到的金額，只要沒有被明確設成隱藏，事件照樣能複製；金額欄位只能複製到金額欄位（`FieldRules.CopyCompatible`，目的端照樣受金額遮罩），但事件的公式對應（`s.Formula`）結果型別是數字時可以寫進小數欄位，例如 `{單價} * 1`，金額就變成不受金額遮罩的數字。
- **處理**：路徑二寫進手冊已知限制（§13 L4，待使用者接受），稽核可查（`event.create`／`event.update` 的定義快照）。💡 後續可把 `HiddenAsync` 擴大成「任一群組的有效權限看不到」（含金額遮罩），不在本段：要定義「對哪些群組算隱藏」（全部群組？讀得到目標模組的群組？），規則要另外設計。

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

- **L1 自設簽核人**：有設計器修改權的人可以把自己設成簽核關卡的簽核人。單據送簽、流到那一關、他是那個待辦的可動作者時，他就能唯讀那一筆單據（含金額與附件；欄位例外照他自己群組的設定，被隱藏的仍隱藏；不含清單、匯出、反向關聯）；那一關處理完、流程結束或撤回後就失效。查證：稽核的簽核流程發布紀錄、簽核歷程。（使用者裁決）
- **L2 彙總不看可見性**：一般參照的彙總會加總來源模組裡你看不到的數字欄位；不是金額型別的數字（例如設成小數的薪資）可以藉彙總算出。彙總計入所有子記錄，不依讀者的資料範圍過濾；來源只有一筆時，合計就是那一筆的原值。查證：稽核的欄位新增、修改紀錄。（使用者裁決）
- **L3 帶值與轉換可由他人轉手**：參照帶值、單據轉換用執行的人的權限複製；看得到來源欄位的人在別的模組選參照或轉單時，值會複製過去，讀得到那個模組的人就看得到。帶表頭的值已在本段修掉「自己用 API 取值」（M6），但看得到表頭欄位的同事新增明細時，值照樣寫進明細，同屬轉手路徑。設計時要把敏感欄位的帶值目的欄位也設成隱藏。（主控列入，待使用者接受）
- **L4 連動事件以系統身分執行**：事件會把來源模組的值寫進目標模組，不看觸發者或設計者的權限。「允許複製被隱藏的值」要權限設定權；被設成隱藏的欄位、限定可見的欄位，以及引用它們的公式（逐層）都要勾。沒有被明確設成隱藏的欄位（包括金額，以及設計者讀不到的整個模組）不需要勾就能複製，事件的公式還能把金額算成一般數字。查證：稽核的事件建立、修改紀錄。（主控列入，待使用者接受）
- **L5 設計器試算可推出隱藏值**：用一筆記錄試算簽核流程時，你看不到的欄位照用記錄的值；可以藉條件反覆試算推出隱藏值。試算不記稽核，事後查不到。（主控列入，待使用者接受）
- **L6 權限設定者可以改自己**：有權限設定的人可以替自己所在的群組或自己開權限，記稽核、不另外通知。（顧問裁決權限軸-漏2，待使用者確認）
- **L7 升級帶入**：升級時，原本有設計器修改權的群組與個人會被帶入權限設定，能改權限的人和升級前一樣；例外是原本只有設計器修改、沒有讀取的人，會多出設計器讀取（只看定義、不看資料）。要拆開請到群組頁、使用者頁取消；退回舊版再重新升級時不會重新帶入。（M2）
- 既有的規則推論管道（核心設計 §15 第 24 條）不變，手冊已有。

## 14. 與其他段的接點

- **mcp1a**（後端地基）：
  - 加入請求通道後，`PermissionAdminGate` 在 MCP 通道一律回 `McpChannel`（M8 擴大，待使用者確認）；`PermissionAdminBehavior` 經 gate，所以所有宣告 `IPermissionAdminRequest` 的請求在 MCP 通道都是 403 `mcpPermissionAdminDenied`，即使權杖擁有者是系統管理員。理由：mcp3-4（AI 能不能改權限）還在 A 區等使用者答，推薦是不提供；在沒有對應範圍的情況下，最保守的預設是 AI 這條路完全拿不到權限設定。使用者若選「提供」，再加一個權杖範圍（例如 `permissions.write`），gate 改成「通道是 MCP 時要有該範圍」，不改表。
  - 通道是 request 範圍的服務，巢狀 mediator 請求沿用；背景工作（Demo）換了 DI scope 會變回預設通道，所以 MCP 進來的請求一律在排入背景前被擋（M4）。
  - 「不提供給 AI」旗標的寫入規則照 §7.3 的契約。
  - 測試 PAX-22 由 mcp1a 實作，算 mcp1a 的驗收；權限軸-a 不宣稱它通過。
- **MCP 通道可用與禁止的操作**（M8 落地後、mcp3-4 答覆前）：
  - 可用（照設計器動作與權杖範圍）：建模組、加欄位（含主檔貢獻者區塊，Derived 不經 gate）、改欄位（不動「不提供給 AI」旗標時）、加區塊、狀態機、檢視、報表、刪欄位、刪模組（Cleanup）、從範本建立（權限建議一律略過並回 warnings）、建立或修改不含 `allowHiddenSource = true` 的事件。
  - 禁止（403 `mcpPermissionAdminDenied`）：改模組權限、開啟／關閉／重新開啟／刪除應用程式、產生 Demo 資料、含 `allowHiddenSource = true` 的事件、設定或變更「不提供給 AI」旗標、系統權限 API、群組成員、建立或修改使用者、刪除群組。
  - 管理 API 的保證：系統權限、成員、帳號這些請求即使將來被誤登記成工具，也會被 gate 擋下；不靠「工具清單沒列」當唯一保證。
- **mcp1b**（畫面）：欄位設定的「不提供給 AI 工具」勾選框依 `me.permissionAdmin` 切停用並說明，和權限分頁同一句提示。
- **mcp3**（設計器工具）：MCP 設計 §7.2 原寫「工具一律走 `IDesignerRequest`」，與本段矛盾（`SaveModulePermissionsRequest` 已移除 `IDesignerRequest`，套件開關多了 `IPermissionAdminRequest`），本次同步改成「照對應 request 原本的宣告把關」。mcp3 原列的高風險工具 `apply_app_package`、`close_app`、`set_module_permissions` 在 M8 下一律被拒，mcp3 寫設計時依 mcp3-4 的答案決定保留、改範圍或拿掉，並在 feature.md 補「⛔ 等：權限軸」（分流 E5）。
- **pkg1**（套件更新）：套用新模組的權限建議走 `SaveModulePermissionsRequest`（要權限設定；pkg1 已定要系統管理員，管理員自動有）；既有模組的權限修改只列出不套用（pkg1-4 推薦）。pkg1 若新增寫授權表的服務呼叫，§7.4 的寫入邊界會在執行期擋下、清單測試會要求登記。
- **O2**：沿用 `AuditBehavior`、`SecurityIncidents`、`IncidentTitles`、寄送佇列；o2e（監控頁、稽核頁）若已上線，稽核頁要認得 `group.systemPrivileges`、`user.systemPrivileges` 兩個新代碼的顯示名稱（權限軸-b 補四語）。

## 15. 各開關組合下誰負責

| 身分 | 改設計 | 看權限分頁 | 改模組權限 | 授予設計器／權限設定 | 共用主檔設計與權限 |
|---|---|---|---|---|---|
| 系統管理員 | 可 | 可 | 可 | 可 | 可 |
| 設計器（依動作）＋權限設定 | 依動作 | 可 | 可 | 不可 | 不可 |
| 只有設計器 | 依動作 | 讀取以上可 | 不可 | 不可 | 不可 |
| 只有權限設定 | 不可（只能讀） | 可 | 可 | 不可 | 不可 |
| 都沒有 | 不可 | 不可 | 不可 | 不可 | 不可 |

停用的帳號（含停用的系統管理員）一律等同「都沒有」。經 MCP 通道時，「改模組權限」「授予」兩欄一律不可（M8）。沒裝任何應用程式時，只有系統管理員一人也能完整運作（自動擁有兩者）。Demo 站訪客以 admin 登入，行為與現在相同。

## 16. 可寫死清單（新增，屬安全與權限檢查）

- 系統權限代碼 `DYN00000`（既有）、`SYS00001`。
- 系統管理員自動擁有設計器全部動作與權限設定；停用者一律沒有（先看啟用再看管理員）。
- 只有系統管理員能授予或收回這兩個系統權限。
- 權限設定隱含設計器讀取。
- 權限設定只用 `CanEdit` 位元。
- MCP 通道不能行使權限設定（mcp1a 起）。
- 共用主檔模組的權限只有系統管理員能改（既有）。
- 從範本建立沒有權限設定時略過權限建議（行為規則，不是數值）。
- 守衛表清單、寫入許可的種類與開許可的位置（§7.4）。
- 升級帶入的標記鍵 `permissionAxes.importedOn`。
- 安全事件 `privilege.changed` 的每筆異動列出上限 20 人（技術限制，照 `admin.changed` 的「最近 20 筆」）。

這些都不是業務政策（不是上限、天數、預設值、特定群組或模組代碼），屬鐵則 2 允許寫死的安全與權限檢查。

## 17. ERP 對照

- **SAP**：角色維護（PFCG，定義角色有哪些授權）與使用者指派（SU01）分給不同人；開發與客製（ABAP Workbench、Customizing，`S_DEVELOP`）和授權管理（`S_USER_*`）是不同的授權物件。開發者在測試系統自己有權限，正式系統沒有。本設計的「設計器」對應客製，「權限設定」對應角色維護，「授予系統權限只有管理員」對應使用者與角色指派的最上層。
- **Odoo**：存取權（Access Rights、Record Rules）只有「Administration／Settings」群組能改；Studio 客製也要同一群組。Odoo 沒有把兩者拆開，Formwright 比它多拆一層，是因為目標客戶常把「改表單」交給 IT 而不讓他碰資料可見性。
- 小型企業慣例：權限通常由老闆或一兩位主管決定，所以權限設定全系統一份、不分模組（權限軸-4）。

## 18. 驗收案例

標記：【雙跑】＝`[Trait("Category", "Postgres")]` 進 PostgreSQL 雙跑集；沒標的只在 SQLite 跑（不涉及 repository 新查詢或資料匯入）。後端測試在 ApplicationTests（handler、behavior、interceptor）或 WebAPITests（端點、HTTP、`code`、語系）。錯誤案例一律斷言 HTTP 狀態與 `code`（沒有 `code` 的斷言沒有 `code` 鍵）。**未提供 fixture、標 Skip、或屬尚未實作段（PAX-22）的案例不得算通過**；驗收回報逐條寫實際結果。

**共用 fixture**（`PermissionAxesFixture`，測試資料全部虛構）：

| 代號 | 身分 | 直接權限 | 群組 |
|---|---|---|---|
| admin | 系統管理員、啟用 | 沒有任何系統權限列 | — |
| 小王 wang | 啟用 | — | G-design：`DYN00000` 讀＋新增＋修改＋刪除；A、T、H、D 讀＋新增＋修改（無金額）；M 沒有列 |
| 小李 li | 啟用 | — | G-perm：`SYS00001`；沒有 `DYN00000` 列；A 沒有列 |
| 小陳 chen | 啟用 | — | G-both：`DYN00000` 讀＋新增＋修改、`SYS00001`；A、T 讀＋修改 |
| 小張 zhang | 啟用 | 個人 `DYN00000` 讀＋修改 | — |
| 小周 zhou | 已停用、session 仍有效 | — | G-both |
| 老林 lin | 系統管理員、已停用 | — | G-both |

模組（皆 Active）：A 一般模組（欄位：單價 Currency、薪資 Decimal 對 G-design 設 Hidden、F1 公式 `{薪資} * 1`、F2 公式 `{F1} + 0`）；M 一般模組；T 事件目標模組（小數欄位 N、金額欄位 P，Currency）；H 表頭與 D 明細（H.薪資對 G-design Hidden、H.單價 Currency、H.F1 `{薪資} * 1`；D.X、D.Y、D.Z 帶表頭的值分別指向 H.薪資、H.單價、H.F1）；S 共用主檔成員模組；範本 `t-perm`（測試範本：一個模組、群組權限建議指向 G-design、含一個 `UpdateField` 步驟）。

**資料與升級帶入**

- **PAX-01**【雙跑】全新資料庫啟動後 `Dynamic_Program` 有 `SYS00001` 且 `Active = true`；重啟一次仍是 true；`permissionAxes.importedOn` 存在、沒有任何 `SYS00001` 授權列。
- **PAX-01b**【雙跑】升級冒煙：以上一個已發布版本的最後一個 migration 建庫、置入 PAX-02 的資料，照 `Program.cs` 的順序執行 `MigrateAsync` 與 `SeedData.EnsureSeededAsync`：兩個 provider 都啟動成功、沒有寫入許可例外；守衛表除了 `SYS00001` 帶入列以外逐列不變。
- **PAX-02**【雙跑】升級帶入：用不跑 `SeedData` 的專用 fixture 建到最新 schema（本段沒有 schema 變更，等同上一版）後置入資料：G1（`DYN00000` 讀＋改）、G2（只讀）、G3（只改、沒有讀）、G5（沒有 `DYN00000`，但有既存 `SYS00001` `CanEdit = true`）、G6（`DYN00000` 改，且有既存 `SYS00001` `CanEdit = false`）；u1 個人 `DYN00000` 改；u2 個人只新增；u4 在 G2 與 G7（新增）且個人讀取（合併後沒有改）；u5 已停用、個人 `DYN00000` 改；u8 在 G2 與 G1（多群組合併有改）；業務模組列若干。執行 `SeedData.EnsureSeededAsync`：G1、G3、G6、u1、u5 恰有一列 `SYS00001`（`CanEdit` true、其餘 false），G2、G5、G7、u2、u4 沒有；`DYN00000` 列與業務模組列逐列不變；標記存在。
- **PAX-02b**【雙跑】回滾與重新升級：接 PAX-02，以 API 收回 G1 的權限設定、授予 G2 權限設定；模擬回滾（不經新程式，直接把 `SYS00001` program 列設成 `Active = false`，照舊版 SeedData 的效果）；再執行 `EnsureSeededAsync` 兩次：G1 仍沒有、G2 仍有（不復活、不丟失），program 列回到 `Active = true`。另刪掉標記列再執行一次：照當時的設計器修改權重算（G1 恢復、G2 消失），證明強制重帶的說明正確。
- **PAX-02c**【雙跑】多實例：兩個 DbContext 同時對同一個未帶入的庫執行 `PermissionAxesImport.RunAsync`：兩個都成功結束、授權列恰為一份、沒有主鍵衝突。
- **PAX-03**【雙跑】能力對照（§6.3 K1–K5、N1）：同 PAX-02 的資料帶入後，以舊規則（有效 `DYN00000` 修改、啟用）算出的「能改權限」名單，與新程式 `PUT modules/A/permissions` 的結果逐人相同（200 或 403 `permissionAdminRequired`）；G3 的成員 `GET modules` 由 403 變 200（N1，記為新增能力）；u5 `IsPermissionAdminAsync` 為 false，重新啟用後為 true。

**系統管理員與 fail closed**

- **PAX-04** admin（沒有任何系統權限列）`GET me` 回 `designer` 四項 true、`permissionAdmin` true；`PUT modules/A/permissions` 200。
- **PAX-05** 小張（個人設計器讀＋改、沒有權限設定）：`GET modules/A/permissions` 200；`PUT` 403，`code` 為 `permissionAdminRequired`，`X-Locale: zh-TW` 與 `en` 的 `message` 各為該語系文案；權限表逐列不變。
- **PAX-06** fail closed：使用者不存在、小周（停用、仍持有兩軸授權）、老林（停用的系統管理員）三種，`IsPermissionAdminAsync` 皆為 false、`DesignerActionsAsync` 皆為空集合（確認停用判斷在管理員捷徑之前）；宣告 `IPermissionAdminRequest` 但回傳型別不是 `ServiceResult` 的測試用請求，送出時丟 `InvalidOperationException`。
- **PAX-07** 小李（只有權限設定）：`GET modules`、`GET modules/A`、`GET modules/A/permissions` 200（隱含讀取）；`PUT modules/A/permissions` 200；`POST modules/A/fields` 403 且沒有 `code`（設計器修改不足，`Forbidden`）。`GET me` 的 `designer.read` true、其餘 false、`permissionAdmin` true。

**自我提權嘗試**

- **PAX-08** 小王對他讀不到的模組 M 送 `PUT modules/M/permissions` 給 G-design 讀取＋金額 → 403 `permissionAdminRequired`；之後小王 `GET` M 的 schema 仍 403、M 的權限列不變。
- **PAX-09** 小王送 `PUT admin/groups/{G-design}/system-privileges` 給權限設定 → 403、沒有 `code`（先被「不是系統管理員」擋下）；小李送同一請求 → 403、沒有 `code`；兩次都沒有寫入、沒有稽核、沒有安全事件。
- **PAX-10** 小王、小李各送 `PUT admin/groups/{G-perm}/members` 把自己加進去 → 403、沒有 `code`；成員不變。
- **PAX-11** 小王（設計器讀＋新增＋修改＋刪除、沒有權限設定）`POST templates/t-perm/instantiate` → 201，模組建立、`Dynamic_GroupProgramPrivilege` 沒有新模組的列，回應 warnings 含略過權限的提示，稽核 `template.instantiate` 的 `permissionsSkipped` 為 true；小陳同一請求 → 201，權限建議照常套用、沒有略過的 warnings。只有設計器新增的使用者 → 403（巢狀 `UpdateField` 要修改，整批回滾；確認 M3 不宣稱「只有新增就能建」）。
- **PAX-12** 小王 `POST demo-data/generate` → 403 `permissionAdminRequired`，執行器狀態沒有變成 Running；小陳 → 200、狀態 running；直接以小王身分送 `GenerateDemoDataRequest` → 403、沒有任何寫入。
- **PAX-13** 事件旗標：小王 `POST events`（某個對應 `allowHiddenSource = true`）→ 403 `eventHiddenSourceNeedsPermissionAdmin`、事件不存在；小王改成 false 且來源欄位沒被隱藏 → 201；小陳勾旗標 → 201；小李勾旗標 → 403、沒有 `code`（沒有設計器修改，gate 不取代設計器守衛）；admin 先建一個勾旗標的事件，小王 `PUT events/{key}` 只改名稱（旗標仍在）→ 403 `eventHiddenSourceNeedsPermissionAdmin`；小陳同樣修改 → 200。
- **PAX-13b** 公式閉包（M5 擴大）：小陳（有權限設定）在旗標為 false 時建立事件，來源是 A：對應 `from` F2 → T.N → 400，錯誤鍵 `…set[0].allowHiddenSource`；對應公式 `{F1} * 1` → 400；對應 `from` 單價（Currency，未設隱藏）→ T.P 201（L4 路徑二，固定已知限制）；把旗標改成 true → 201。`HiddenAsync(A)` 回傳集合含薪資、F1、F2。另：小陳把 A 的公式 F3 由 `{單價}` 改成 `{F1}`，而已有事件從 F3 複製且旗標為 false → `PUT fields/{F3}` 400（既有 `UpdateFieldHandler` 比對接上新規則）。
- **PAX-14** 帶表頭的值（M6）：小王以 API 修改既有 H 記錄、新增一列 D 並省略 X、Y、Z → 該列 X、Y、Z 都是空值；admin 做同樣操作 → X、Y、Z 等於表頭的薪資、單價、F1。小王新增表頭（`POST`）時同樣檢查一次。H 既有明細列的 X 值在小王修改表頭後不變。另以 `DetailWriter` 單元測試傳入 `HiddenFieldIds` 含某限定可見欄位的表頭權限，確認被過濾；並確認傳入的是表頭權限而非明細權限（明細權限沒有隱藏任何欄位時，結果仍是空值）。

**跨模組與共用主檔**

- **PAX-15** 小李在模組 A、M 存權限都 200（全系統一份）；在 S 存權限 403、沒有 `code`（主檔守衛）；admin 在 S 200。
- **PAX-16** 明細模組：小李存 D 的欄位例外 200；小張存 D 的欄位例外 403 `permissionAdminRequired`。
- **PAX-17** 小李把 G-perm 在模組 A 的讀取打開 → 200，稽核 `permission.update` 的行為者是他、格子含 G-perm；沒有安全事件（權限軸-漏2）。

**授予與安全事件**

- **PAX-18**【雙跑】群組授予：G-x 有成員 u3（沒有其他系統權限來源）。admin `PUT admin/groups/{G-x}/system-privileges`（設計器讀取＋修改、權限設定）→ 200，回應等於請求；`GET admin/groups` 的 G-x 帶同值 `systemPrivileges`；`DYN00000` 列 `CanRead`、`CanEdit` true，`SYS00001` 列 `CanEdit` true；稽核 `group.systemPrivileges` 有 before／after；`privilege.changed` 事件次數加一、種類 granted、`privilegeHolders` 為 `{ total: 1, truncated: false, items: [u3: before { designer: [], permissionAdmin: false } → after { designer: ["read","edit"], permissionAdmin: true }] }`、樣本指向該稽核 Id、管理員收到站內通知與寄送佇列一筆。全部取消 → 兩列都刪除、事件種類 revoked。沒有成員的 G-empty 送同樣授予 → 200、有稽核、事件次數不變。缺 `designer.edit` 或 `permissionAdmin` 為 null → 400，`errors` 含那兩個鍵，權限列不變。
- **PAX-19**【雙跑】個人授予：admin `PUT admin/users/u3/system-privileges` → 200、稽核 `user.systemPrivileges`、事件 granted。目標是另一位啟用的系統管理員 → 200、照存、有稽核、**不發事件**（管理員不在快照），以該管理員 `GET me` 不受影響。使用者不存在 → 404。
- **PAX-20**【雙跑】實際持有者變化：G-p1、G-p2 都持有權限設定，G-r 只持有設計器讀取，G-re 持有讀取＋修改。
  - `SetGroupMembers` 把 u3 加進 G-p1 → granted，items 只有 u3；把 u4 加進沒有系統權限的群組 → 不發。
  - u5 同在 G-p1、G-p2，從 G-p2 移出 → 不發（重疊授權，有效值不變）。
  - u6 同在 G-r、G-re，從 G-re 移出 → revoked，before `["read","edit"]`、after `["read"]`（部分收回）。
  - 對 G-p1 的成員整份取代：u3 移出、u7 加入 → granted，items 含 u3（收回）與 u7（取得），依 `userId` 排序。
  - 一次把 25 人加進 G-p1 → items 20 筆、`total` 25、`truncated` true。
  - `GET admin/groups/{G-p1}/impact` 回 `systemPrivileges`；`DELETE admin/groups/{G-p1}` → 只靠 G-p1 的人列入、種類 revoked，同時在 G-p2 的 u5 不列入。
  - `UpdateUser` 改 u3 的群組、`CreateUser` 帶 G-p2 建 u9，同樣判斷。
- **PAX-21** 原值存檔：系統權限 API 送和現狀相同的內容 → 200、有稽核列、不發安全事件。
- **PAX-21b**【雙跑】交易失敗全回滾：以測試替身讓 `SecurityIncidents.PrivilegeChangedAsync` 丟例外，admin 送 PAX-18 的授予 → 500；`DYN00000`、`SYS00001` 列、成員、稽核、事件、通知、寄送佇列全部不變。

**接點與回歸**

- **PAX-22**（mcp1a 實作，算 mcp1a 的驗收）MCP 通道（在 ApplicationTests 把通道服務設成 MCP、權杖擁有者是 admin）：
  - behavior：`SaveModulePermissionsRequest`、`OpenAppRequest`、`CloseAppRequest`、`ReopenAppRequest`、`DeleteAppRequest`、`StartDemoDataRequest`、系統權限兩支、`SetGroupMembersRequest`、`CreateUserRequest`、`UpdateUserRequest`、`DeleteGroupRequest` 皆 403 `mcpPermissionAdminDenied`，沒有任何寫入。
  - 條件式 gate：建立含 `allowHiddenSource = true` 的事件 → 403 `mcpPermissionAdminDenied`；`AddFieldRequest` 帶「不提供給 AI」true → 403 同碼。
  - 巢狀：從範本建立 `t-perm` → 201、權限建議略過、warnings 有提示；開啟套件（內含巢狀權限存檔）→ 403、整批回滾。
  - 同一位 admin 經畫面通道送同樣請求 → 200／201。
- **PAX-23** `AuthorizationWriteInventoryTests`：§7.4 清單測試 1–5 全過。
  - **PAX-23a** 刻意漏宣告：測試用 `ForgotToDeclareRequest`（只宣告 `IDesignerRequest`）的 handler 呼叫 `ReplaceModuleRowsAsync` → `InvalidOperationException`（`Demand` 沒有許可），權限表逐列不變。
  - **PAX-23b** 刻意漏宣告的追蹤式寫入：同一個 handler 以追蹤式寫入新增一列 `FieldPermission` 後存檔 → 例外、列不變。
  - **PAX-23c** `Cleanup` 許可下新增 `FieldPermission`、刪除不屬於目標欄位的 `FieldPermission` → 例外；`RunCleanupAsync` 的 `body` 只呼叫 `PurgeFieldsAsync`、沒刪欄位本身 → 結束時例外、整筆回滾，欄位權限列還在。
  - **PAX-23d** `Derived` 許可下：新增 `SourceKind = Manual` 的 `FieldGrant`、新增群組不在許可參數內的 `FieldGrant` → 例外；呼叫任一批次入口（例如 `ReplaceActionScopesAsync`）→ 例外（Derived 不准批次）。
  - **PAX-23e** 小李（`Authorized`、沒有 `SystemAdminVerified`）的請求內：追蹤式寫 `SYS00001` 列、寫成員表 → 例外；以 `ProgramCode = SYS00001` 的測試模組呼叫 `ReplaceModuleRowsAsync` → 例外（批次入口拒絕系統代碼，就算持有 `Authorized`、甚至 `SystemAdminVerified` 也一樣）。
  - **PAX-23f** 沒有許可時把欄位的 `VisibilityRestricted` 由 true 改 false 後存檔 → 例外；以離線實體 `Update` 一個 `VisibilityRestricted = false` 的欄位 → 例外（不靠原值）。
  - **PAX-23g** 在 `ExecutionContext.SuppressFlow` 的背景工作內、沒有自己的許可時寫守衛表 → 例外（前台許可不會被帶進背景）。
  - **PAX-23h** 合法的條件式寫入：測試用請求在 handler 內以 `PermissionAdminGate.RunAsync` 包住「改守衛欄＋存檔」，小陳送 → 成功、值已寫入；小王送 → 403 `permissionAdminRequired`、沒有寫入。
  - **PAX-23i** 離開許可後：`RunAsync` 的 `body` 只改守衛列、不存檔 → `body` 結束時例外，訊息說明要在許可內存檔；`body` 內以 `Task.Run` 起、未等待的工作在 `body` 結束後才寫守衛表 → 例外（許可已關閉）。
  - **PAX-23j** 清單測試的掃描器自測（§7.4 清單測試 10）：每個違規樣本各回報一筆，移除樣本後回報為零。
  - **PAX-23k**【雙跑】PAX-23b、23c、23e、23f 在 PostgreSQL 再跑一次：攔截器與批次入口在兩個 provider 行為相同。
- **PAX-24** 既有測試回歸：設計器相關的既有測試裡「設計器修改能存權限」的案例改成「要權限設定」；直接寫授權表的 fixture 改用 `RunForTestsAsync`；其他設計器行為的測試不改即通過（全套 SQLite＋雙跑集）。
- **PAX-25**【雙跑】resolver 與持有者查詢整合：`IsPermissionAdminAsync`、`DesignerActionsAsync`、`SystemPrivilegeHolders.SnapshotAsync` 在兩種資料庫對 fixture 全員回相同結果（群組與個人合併、M1、停用、管理員排除）。

**畫面（權限軸-b，Vitest＋本機走查）**

- **PAX-30** 權限分頁：`permissionAdmin` false 時所有輸入停用、存檔鈕停用、顯示唯讀說明；true 時可編可存；共用主檔模組且不是系統管理員時唯讀並顯示主檔說明。
- **PAX-31** 使用者編輯對話框：系統權限區塊載入 `systemPrivileges`（直接設定），另顯示經群組取得的唯讀一行；改完存檔送 `PUT admin/users/{id}/system-privileges`，body 只含直接設定。回歸：只經群組取得權限設定、個人沒有的使用者，打開對話框不改就存，送出的 `permissionAdmin` 是 false（不把繼承的存成個人）。目標是系統管理員時顯示自動擁有的提示；第二個請求失敗時的提示文字。
- **PAX-32** 群組編輯對話框同上（沒有繼承行）；刪除群組對話框在群組持有系統權限時列出「此群組提供的系統權限會移除…成員若從其他群組或個人設定取得，仍會保留」。
- **PAX-33** 群組清單（直接設定）、使用者清單（有效結果）顯示「設計器」「權限設定」標籤（桌機表格與手機卡片）。
- **PAX-34** 事件編輯：沒有權限設定時旗標勾選框停用；載入已勾旗標的事件時存檔鈕停用並說明。
- **PAX-35** 從範本建立對話框在沒有權限設定時顯示略過提示；Demo 資料頁沒有權限設定時產生鈕停用並說明。
- **PAX-36** 只有權限設定的帳號登入：選單有「動態模組設計」、沒有「使用者」「群組」「應用程式」；能從設計器進權限分頁存檔。
- **PAX-37** 四語鍵齊全（`locales.vitest.spec.ts`）；手冊第 5、9、11、12、18 章與畫面文字一致。
- **PAX-38** 本機走查（`scripts/unattended/local-demo.sh`）390、820、1366、1920 四種寬度：使用者、群組編輯對話框、權限分頁、事件編輯不得橫向捲動。情境：admin 給小王設計器修改 → 小王開權限分頁看得到、改不了 → admin 再給權限設定 → 監控頁出現 `privilege.changed` → 小王能存。

## 19. 子單元切分

### 權限軸-a 後端

- 範圍：`SystemProgramCodes`；SeedData 與 `PermissionAxesImport`（沒有 EF migration）；`IPermissionAdminRequest`、`PermissionAdminBehavior`、`PermissionAdminGate`、guard 兩個方法、隱含讀取與停用順序；`AuthorizationTables`、`AuthorizationWriteScope`（`RunAsync`、`Demand`）、`AuthorizationSaveChangesInterceptor`（兩個 provider 都註冊）、四個批次入口的 `Demand`、`RunCleanupAsync` 與 `FieldPurge`、`ModuleRemover` 的包裝；§7.2 的 13 個宣告；M3 範本略過、M4 Demo、M5 事件旗標與公式閉包、M6 帶表頭的值；`ServiceResult.Forbidden(message, code)`；兩支系統權限 API 與管理清單、群組影響的系統權限欄位；`GetMe` 的 `permissionAdmin`；稽核代碼、`SystemPrivilegeHolders`、`privilege.changed` 與四語標題與信件文案；`MessageText` 三個新鍵（`McpPermissionAdminDenied` 先放鍵，mcp1a 才用到）；PAX-01–21b（含 01b）、23–25（含 23a–k）；核心設計 §15 第 55 條與「上線前必做」兩處的回寫（§21）。
- 相依：O2 收完（`SecurityIncidents`、`AuditBehavior` 已在 o2a–o2b；等 o2e 合併再開工，避免和它同時改 `IncidentTitles`、四語檔）。
- 規模估計：後端約 25–30 個檔案（寫入邊界與既有 fixture 改用測試許可是主要增量），沒有 migration；無前端改動（`GetMe` 多一個欄位，前端型別在 -b 補，舊前端忽略未知欄位）。

### 權限軸-b 畫面

- 範圍：§11 全部；`Me`、`AdminUser`、`AdminGroup`、`GroupImpact` 型別；四語；手冊第 5 章（開頭與 5.5 主檔說明補權限設定）、第 9 章已知限制 L1–L7、第 11、12 章系統權限、第 18 章「誰能做」改寫、升級說明（§21）；o2e 稽核頁的兩個新稽核代碼名稱；PAX-30–38。
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

- **M1 權限設定隱含設計器讀取**（§5）。理由：不然權限設定者進不了設計器；讀取只看定義不看資料。這是新增能力（§6.3 N1）。選錯代價：改成必須另外授予讀取，改 guard 一行＋畫面一個驗證，不改表。
- **M2 升級時帶入既有設計器修改權持有者**（§6.3）。延伸分流 D5。理由：升級不得讓人突然失去改權限的能力。契約以 K1–K5／N1–N2／S1–S4 表達，不宣稱「能力完全一樣」。選錯代價：改成不帶入，拿掉 `PermissionAxesImport` 的第 4 步；已升級的庫要手動清。
- **M3 從範本建立沒有權限設定時略過權限建議並警告**（§7.3）。推翻分流 D4 對範本的部分。理由：D4 會讓沒有權限設定的設計者用不了內建範本，略過只是回到新模組預設沒人有權限。從範本建立仍要設計器新增＋修改。選錯代價：改成擋下，handler 一個判斷。
- **M4 產生 Demo 資料要權限設定**（§7.3）。理由：它以管理員身分批次授權。選錯代價：拿掉兩個介面宣告。
- **M5 連動事件「允許複製被隱藏的值」歸授權，且一般隱藏欄位納入公式閉包**（§7.3、§12.3）。理由：這個旗標就是放寬可見性，和「不提供給 AI」同類；使用者 11-19 的授權清單沒列到它，是因為當時還沒查到這條路徑。閉包擴大會讓「事件引用了引用隱藏欄位的公式」從不必勾變成必須勾（§6.3 S1）。選錯代價：拿掉 handler 的 gate 呼叫與畫面停用；閉包還原一行。
- **M6 帶表頭的值依呼叫端在表頭的可見性**（§12.2）。理由：拆軸後它變成設計者自助取得隱藏值的路徑，修法只是過濾一份字典，畫面行為不變；直接呼叫 API 的結果會變（§6.3 S2）。選錯代價：還原一行過濾。
- **M7 系統權限可授予群組或個人，清單顯示有效結果、編輯只動直接設定**（§5、§8、§11）。理由：沿用既有兩張表，小公司常只有一人管權限；分開兩種值才不會把繼承的權限存成個人。選錯代價：只開群組時，畫面拿掉使用者頁的區塊、API 拿掉一支。
- **M8 MCP 通道一律不能行使權限設定，範圍擴大到套件開關、Demo、帳號與成員管理**（§14），直到使用者答 mcp3-4 並加對應權杖範圍。這會讓 mcp3 原列的 `apply_app_package`、`close_app` 在 MCP 上不能用。選錯代價：加一個範圍與 gate 一個條件，不改表。
- **M9 安全事件依實際持有者變化觸發**，含成員異動與刪除群組（§9）。理由：只看系統權限 API 會漏掉「把人加進持有權限設定的群組」。選錯代價：只留兩支 API 的觸發，拿掉四個 handler 的比對。
- **M10 升級帶入只做一次，回滾不刪資料、重新升級不重帶**（§6.3）。理由：避免「回滾再升級」讓已收回的權限設定復活或讓事後的授予消失。代價：回滾期間直接寫庫新增的設計器修改權不會被帶入，要人工檢查；要強制重帶得刪標記。選錯代價：改成每次啟動都補帶，`RunAsync` 拿掉標記判斷。

### 20.4 技術決定（主控，可推翻）

- **T1** 新代碼 `SYS00001`（分流 D1）。
- **T2** 只用 `CanEdit` 位元，不加表不加欄。
- **T3** 系統代碼集中在 Domain 的 `SystemProgramCodes`。
- **T4** `PermissionAdminBehavior` 放在 SystemAdmin 之後、MasterDesigner 之前，比照既有回傳型別檢查（分流 D2 原定放在 SystemAdmin 之前，審查修正改順序，讓「不是系統管理員」先回）。
- **T5** 停用與不存在的使用者一律沒有權限設定與設計器動作，判斷順序「存在 → 啟用 → 管理員 → 列」。
- **T6** 條件式檢查集中在 `PermissionAdminGate`，三種結果。
- **T7** 開關應用程式、成員與帳號管理、系統權限 API 冗餘宣告 `IPermissionAdminRequest`（延伸分流 D4 對套件的部分）。
- **T8** 事件旗標只看請求內容、不比對既有定義。
- **T9** 守衛防線：追蹤式寫入由 SaveChanges 攔截器逐列檢查；批次寫入只准四個批次入口，入口以 `Demand` 檢查許可與參數；其他批次與原生 SQL 寫入由清單測試禁止；許可只能以 callback（`RunAsync`）開。不做指令層 SQL 文字比對、不用反射清單、不用 Roslyn 分析（§7.4）。
- **T13** `GenerateDemoDataRequest` 另宣告 `ISystemAdminRequest`，`DemoPeople` 不自己開許可（§7.3、§7.4）。
- **T10** 403 的機器代碼用 `ServiceResult.Forbidden(message, code)` 輸出，camelCase。
- **T11** 系統權限 API 的 body 用 `bool?`，缺鍵回 400，不靠繫結預設值。
- **T12** 升級帶入用啟動時的冪等匯入，不用 EF migration（§6.3）。
- 分流 D3（`GetModulePermissions` 設計者可讀不可寫）、D6（`GetMe` 回權限設定；本設計用單一布林 `permissionAdmin`，因為只有一個位元）、D7（授予 API 宣告 `ISystemAdminRequest`）照採用。

### 20.5 A 區其他權限軸題目

分流 A 區權限軸的題目只有權限軸-漏1 與權限軸-3，使用者 2026-11-19 都已答；沒有其他待答題。mcp3-4（AI 能不能改權限）屬 mcp3，本設計以 M8 的保守預設銜接，不代答。

### 20.6 待使用者確認總表（實作者不得代為定案）

- 顧問裁決：§20.2 全部七項——系統管理員自動擁有兩者、只有管理員能授予、全系統一份、新模組不自動授權、權限設定者可改自己、主檔限管理員、通知政策（授予收回發事件、一般存檔不發）。
- 主控裁決：M1–M10。其中審查修正新增或擴大的是 M5（公式閉包）、M7（直接與有效分開）、M8（範圍擴大到套件、Demo、帳號，與 mcp3 工具衝突）、M10（新）。
- 已知限制的風險接受：L3、L4、L5 待使用者接受；L6 隨權限軸-漏2 確認。L1、L2 已由使用者裁決接受。

## 21. 文件待改

- **手冊待改**（權限軸-b 改）：
  - `docs/manual.md:1185`（第 18 章「誰能做」）：「系統管理員身分不會自動帶設計器權限，設計器權限要由群組授予」改成「系統管理員自動擁有設計器與權限設定」；選單條件說明一併簡化。
  - 第 5 章開頭：「設計器的權限分頁」改寫成「有『權限設定』權限的人可以改；有設計器權限的人只能看」。
  - 第 11、12 章：系統權限區塊與標籤，說明直接設定與經群組取得的差別。
  - 第 9 章：L1–L7。
  - 升級說明：L7 的檢查步驟、回滾與重新升級不重帶、強制重帶（一般不需要）。
- **核心設計 §15 待改**（權限軸-a 合併時改）：「上線前必做：決定是否拆開設計與授權」標已處理並連到本文件，刪掉 `DYN00001` 的提議（分流 E3）；第 55 條標「權限軸-a 已處理」。
- **MCP 設計**：§7.2 mcp3 的「工具一律走 `IDesignerRequest`」已在本次審查修正同步改寫並連回 §14。
- **roadmap**：順序串已含權限軸（分流 E2 已由 5cb719e8 處理）；權限軸列的「設計」欄已連到本文件。
- **feature.md**：權限軸-a、-b 上線時打勾；mcp3 的「⛔ 等：權限軸」依 mcp3-4 的答案補（分流 E5，不在本段）。

## 22. 宗旨檢查（formwright-purpose-check）

1. 機制還是政策：通過。新增的都是權限機制與安全檢查，列在 §16；沒有新的上限、天數、預設值或群組代碼。
2. 寫死掃描：不適用（尚無 diff）；實作審查時跑。
3. 單一來源：通過。系統代碼集中在 `SystemProgramCodes`、守衛表集中在 `AuthorizationTables`；前端唯讀條件從 `GET me` 取，不自己算；清單的有效結果與 `GET me` 同一套算法。
4. 兩個應用程式：通過。權限設定對所有模組一體適用，CRM、進銷存、MES 都用同一套權限分頁。
5. 空系統：通過。沒裝任何應用程式時系統管理員自動擁有兩者，群組頁、使用者頁照常能授予；全新安裝的帶入是空操作。
6. 套件只用定義組成：通過。套件帶的權限建議仍是資料，開啟時走同一支權限存檔請求。
7. ERP 對照：通過，見 §17。
8. fail closed、驗證不只在前端、沒有執行期 DDL：修正後通過。behavior 對錯誤回傳型別丟例外；停用者為 false 且先於管理員捷徑；漏宣告的追蹤式寫入在執行期丟例外、批次與原生 SQL 寫入由清單測試擋下（開發期，§7.4 寫明涵蓋範圍）；MCP 預設拒絕；所有限制都在後端；沒有 migration、沒有 DDL。
9. 不限產業：通過。取捨理由是公司規模（權限管理的人少），不依產業。

## 23. 不確定、留給實作者

- 攔截器的時機：EF 呼叫 `SavingChanges` 攔截器時還沒做 `DetectChanges`（`DynamicDbContext` 的註解與此一致），所以攔截器自己先呼叫；兩個 provider 由 PAX-23f、23k 實測固定。
- 既有 fixture 直接寫授權表的筆數未盤點；改用 `RunForTestsAsync` 的工作量可能比估計大，PAX-24 以全套綠燈為準。
- `SeedData` 之外是否還有啟動程式寫守衛表、原生 SQL 的登記檔案有哪些，以清單測試 2、3、6 的掃描結果為準；啟動程式有就登記為 Seed，原生 SQL 檔案逐檔確認不寫守衛表再登記。
- 清單測試 5 以方法簽章切段比對；多載、local function、expression-bodied 成員的切法由實作者定，切不準時寧可把整個檔案算違規。
- `SystemPrivilegeHolders.SnapshotAsync` 要一次查出全部持有者（不要逐人呼叫 `LoadProgramInputsAsync`），也不要在列舉查詢結果時再發查詢（Npgsql 限制）。
- `TemplateInstantiator` 的 warnings 目前是繁中字面字串（例如「群組『X』不存在」）；新增的那則照同樣寫法，四語化不在本段。
- 事件編輯前端（`event-draft.ts` 一帶）停用旗標的位置，實作者照現有畫面決定；只要求「沒有權限設定時不能勾、已勾的不能存」。
- 帶表頭的值的修法（M6）會讓「直接呼叫 API、對表頭欄位隱藏、又省略明細鍵」的整合方行為改變；目前沒有已知的外部整合方。
- 依程式閱讀判定的路徑（§12）沒有另寫重現測試；PAX-13、PAX-13b、PAX-14 會把 M5、M6 固定下來，L3–L5 若實作時發現與描述不符，修正手冊文字並回寫本節。

## 24. 審查修正（2026-11-19）

審查全文在本機 `.tmp/plans/20261119-perm-axes/review.md`（基準 856364f5）。使用者已裁決的排程、授權範圍、L1、L2 不變。

**必修**

1. 反射清單抓不到漏宣告 → 改成資料寫入邊界：守衛表、寫入許可、攔截器，加清單測試管住開許可的位置與批次指令（§7.4，T9；PAX-23、PAX-23a–g；第二輪收斂成攔截器＋批次入口，見下）。
2. 寫入路徑清單不完整、「沒有例外」不成立 → 對照全部 controller 分成授權修改、系統權限、成員、受保護的衍生寫入、刪除清理、無關，各自的守衛；`BindFieldAsync`、刪除清理、刪群組、成員表、`GenerateDemoDataRequest` 補進來（§4、§7.2、§7.4；PAX-12、PAX-23d、PAX-23e）。
3. M5 經公式繞過旗標 → 一般隱藏欄位納入公式依賴閉包（逐層），預設封住，L4 改寫（§7.3、§12.3、§13 L4；PAX-13b）。
4. 「每個人能力完全不變」不成立 → 改成能力契約：保留 K1–K5、新增 N1–N2、收緊 S1–S4，補修改無讀取、多群組與個人合併、停用帳號、既存 `SYS00001` 列（首次帶入前清掉）的案例（§6.3、§13 L7、M2；PAX-02、PAX-03）。
5. 可重跑與回滾未驗收 → 不用 EF migration（專案規則禁手改 migration），改成以標記守門的啟動匯入；寫明回滾不丟資料、重新升級不重帶、強制重帶方式；兩個 provider 實測帶入、回滾、重新升級與多實例（§6.3、M10、T12；PAX-02b、PAX-02c）。
6. 403 不會輸出 `code` → 新增 `ServiceResult.Forbidden(message, code)`，定義完整錯誤 JSON 與 camelCase 代碼，測試斷言 HTTP、`code`、兩種語系訊息（§10，T10；PAX-05、PAX-08、PAX-18）。
7. 整份取代 API 缺欄位規則、直接設定與有效結果混用 → `bool?` 缺鍵回 400 並列出所有缺的鍵；`systemPrivileges` 是直接設定、`effectiveSystemPrivileges` 與 `systemPrivilegeSources` 另列，`me` 是有效結果（§8、§11，M7、T11；PAX-18、PAX-31、PAX-33）。
8. MCP 接點矛盾 → 列出 MCP 可用與禁止的操作；帳號、成員、系統權限請求加宣告，經同一個 gate 拒絕，不靠工具清單；修正 MCP 設計 §7.2 的「一律走 `IDesignerRequest`」；PAX-22 涵蓋 behavior、條件式 gate、巢狀範本與套件、管理員身分（§7.1、§14，M8；PAX-22）。
9. 事件案例與 M9 不一致 → 快照用合併後有效值含 M1，固定 `privilegeHolders` 的形狀、排序、截斷；補空群組、管理員、重疊授權、部分收回、同時授予與收回、交易失敗全回滾（§9；PAX-18–21b）。
10. 案例會測錯或假綠 → 加共用 persona 與模組 fixture；PAX-05 改成讀＋改；PAX-11 改 201、說明巢狀請求要修改並補「只有新增 → 403」；PAX-13 成功者有設計器修改；PAX-03 與新增的 resolver／持有者整合案例雙跑；未實作或 Skip 的案例不算通過（§18；PAX-03、05、11、13、25）。

**建議**

- M6 指定傳入表頭有效權限、不用明細的 `perms`，補金額遮罩、衍生公式、限定可見欄位、既有明細不改寫（§12.2；PAX-14）。
- 刪除群組提示改成「此群組提供的系統權限會移除…成員若另有來源仍保留」（§11；PAX-32）。
- PAX-06 補停用管理員、停用但仍持有兩軸授權，確認停用判斷在管理員捷徑之前（§7.1、T5；PAX-06）。

**第二輪短核對（2026-11-19）**

短核對全文在本機 `.tmp/plans/20261119-perm-axes/verify.md`（基準 8c616a03）。

1. 🔴 SQL 文字比對不能宣稱 fail closed、`Dynamic_Field` 守衛欄可被批次更新繞過 → 拿掉指令層 SQL 比對；批次寫入只准四個批次入口，其他批次與原生 SQL（含 ADO.NET、Dapper）由清單測試禁止，守衛欄禁止 `ExecuteUpdate`，涵蓋範圍明寫執行期與開發期之分（§7.4、§23、T9；PAX-23f、23j）。
2. 🔴 指令層看不到列內容、Derived 缺批次限制、raw SQL 管不住 → 列內容一律在追蹤式寫入時逐列檢查；批次入口以 `Demand` 檢查許可與參數、拒絕系統代碼，Derived 不准批次；Cleanup 結束時驗目標已刪；模型禁止指向守衛型別的 navigation，`Set<守衛型別>` 限登記檔案（§7.4；PAX-23a–e、23k）。
3. `BeginAsync` 與 `AsyncLocal` 生命週期 → 許可只能以 `RunAsync(permit, body)` 開，`body` 內必須存檔、結束時關閉許可；拿掉 `BeginAsync`；`DemoPeople` 不自己開許可，`GenerateDemoDataRequest` 另宣告系統管理員（§7.1、§7.3、§7.4、§6.3、T13；PAX-23h、23i）。
4. PAX-13b 的 T 金額欄位不在 fixture → fixture 的 T 加金額欄位 P（§18；PAX-13b）。另：EF migration 不經 SaveChanges，不需要 Migration 許可，改由清單測試禁止 migration 對守衛表寫資料，並補雙 provider 升級冒煙（§7.4；PAX-01b）。

**第三輪短核對（2026-11-19）**

短核對全文在本機 `.tmp/plans/20261119-perm-axes/verify2.md`（基準 c5c83129）。

1. 清單測試邊界：登記 repository 回傳 `IQueryable` 後跨檔批次刪會漏擋、表名字串禁令會誤擋常數與 schema 定義 → 批次呼叫全域逐方法登記，接收者追不到 `Set<具體非守衛型別>()` 就判違規；禁止對外交出守衛型別的查詢型別；表名禁令明列設定、常數、`Migrations/` 三處窄例外，migration 資料寫入仍由 7 禁止；自測補跨檔傳遞的違規樣本與應通過的合法樣本（§7.4 清單測試 3、5、6、7、10；PAX-23j）。
