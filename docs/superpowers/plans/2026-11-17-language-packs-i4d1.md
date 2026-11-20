# 語系包 i4d1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 系統管理員可以上傳語系包（`.langpack.json`）新增語系，或覆寫既有語系的部分句子：先預覽報告、再套用，一次全成功或全不動；可回到上一版、移除覆寫、移除匯入的語系。後端做語系包格式 v1、驗證器與前後端共用案例檔（含票 #68 的是／否撞字守衛）、預覽暫存（10 分鐘、最多 5 筆、綁建立者）與套用（語系或覆寫在預覽後被改就 409）、覆寫層版本表（目前版＋上一版）與 migration、稽核、Demo 開關、政策 A38（檔案大小）與 A39（已安裝語系數）；覆寫層接進介面文字（`GET i18n/ui/{code}`）與後端訊息（`IMessageCatalog`）。前端加一個最小的「系統管理 → 語系」頁：清單、上傳 → 報告 → 套用。使用者看得到的行為：**沒有匯入任何語系包時，與 main 完全相同**；Demo 站可以上傳看報告，不能套用。

**Architecture:** 驗證規則是 Domain 的純函式（`Domain/Localization/`：介面文字語法、後端範本語法、控制字元、上層語系圖），前端另有一份介面文字語法檢查，兩邊跑 `shared/i18n-message-cases.json`。Application 的 `LocaleService/Packs/` 分三層：`LocalePackParser`（位元組 → 模型，整包層級的錯誤）、`LocalePackAnalyzer`（逐句驗證、和目前覆寫層比對、完整度、是／否撞字，產生報告與「套用後的覆寫層」）、四支 handler（預覽、套用、回到上一版／移除覆寫、移除語系）加一支清單查詢。預覽結果存資料表 `Dynamic_LocalePackPreview`（多台部署可跨台），套用時比對預覽當下記下的語系版本（該語系列的修改時間＋目前版的 Id），不一致回 409。覆寫層存 `Dynamic_LocalePack`（每語系最多兩列：目前版、上一版）；兩張表同一個 migration `LocalePacks`。`LocaleRegistry` 的快照多帶每個語系的覆寫層（介面、後端兩份字典），版本戳記加上 `Dynamic_LocalePack` 的筆數與最大修改時間；`Ui()` 與 `MessageCatalog` 在每個語系的內建層前面先查覆寫層。套用、回到上一版、移除覆寫、移除語系在交易提交後 `registry.Invalidate()`。

**Tech Stack:** .NET 8、MediatR、FluentValidation、EF Core（SQLite 研發、PostgreSQL 正式）、xUnit＋FluentAssertions；Vue 3＋Quasar、TanStack Query、vitest。不新增套件（後端 JSON 用 `System.Text.Json`；前端語法檢查用已直接相依的 `@intlify/message-compiler`）。

**Spec:** `docs/superpowers/specs/2026-11-17-language-packs-design.md`：§4.1（代碼、上層語系規則）、§4.3（不能移除啟用中的語系）、§4.5、§5、§6（6.1–6.4；6.5 只做最小清單）、§11、§12（Demo 列）、§13（13.1、13.2、13.4；13.3 的 `<script>` 覆寫驗證）、§14、§15、§17（U1、U2、U5、U7、U10、T4–T7、T15、T16、T18）、§19（語系包驗證、匯入交易、合併與回滾、安全、稽核）、§20 i4d1 列、§22（`Dynamic_LocalePack` 欄位、`previewId` 存放）。前置計畫（介面來源）：i4a（`3d0dd59b`，判斷 2–5、7、11、21、23）、i4b（`7843faa9`，判斷 5–8、11）、i4c（`bf6fef7b`，判斷 1–4、12、14、Q2）。票 #68。repo 規範：`AGENTS.md`、`.claude/rules/{backend,frontend,docs}.md`、`CONTRIBUTING.md`、`docs/ai/testing.md`。

**路徑慣例：** 後端相對 `backend/`，前端相對 `frontend/`，文件與 `shared/` 相對 repo 根目錄。`Application/…` 是 `backend/src/DynamicModules.Application/…` 的縮寫，`Domain/…`、`Persistence/…`、`WebAPI/…`、`ApplicationTests/…`、`WebAPITests/…`、`DomainTests/…` 同理。符號以 main `e24d8859` 為準（不寫行號，用符號定位）。

---

## 前置條件（開工前逐項確認，不符就停下回報）

1. **i4a、i4b、i4c 已在 main**：`git log --oneline main | grep -E "3d0dd59b|7843faa9|bf6fef7b"` 三筆都有；`Application/Common/Localization/{MessageCatalog,BuiltinServerText,BuiltinLocaleResources}.cs`、`Application/Common/LocaleRegistry.cs`、`Domain/Localization/{LocaleCode,LocaleChain,UiTextMerge}.cs`、`WebAPI/Controllers/v3/Dynamic/I18nController.cs` 都在。
2. **i4d1 沒開工**：`grep -rn "LocalePack" backend/src frontend/src` 無結果；`Dynamic_LocalePack` 不在任何 migration。
3. **沒有其他段在加 migration**（`.claude/rules/backend.md`：會加 migration 的兩項工作不並行）；也沒有段在改 `LocaleRegistry.cs`、`MessageCatalog.cs`、`MessageText.cs`、`AuditActions.cs`、`PolicyCatalog.cs`、`shared/i18n/*/ui.json`（i3 若同時開工會動 `ui.json` 與套件格式，先協調誰先合）。
4. **政策編號**：`docs/superpowers/specs/2026-11-11-policy-settings-design.md` §4 表沒有 A38、A39（設計 §14 暫用；看板設計已註明「A38、A39 已被語系包設計暫用」）。若已被占用，改用表上下一個空號，Task 4 的文件與程式註解一律用實際編號。
5. **基準數字**（記進交棒說明，不符以當下為準）：介面文字基準鍵 `BuiltinLocaleResources.BaseKeys.Count` 2701；後端訊息基準鍵 `BuiltinServerText.BaseKeys.Count`（`MessageText`＋`report.`＋`alert.`）；`ServerTextBaseline/` 三個檔（`message-report.json`、`task-report.json`、`alert.json`）。
6. **PostgreSQL 測試環境可用**（`docker info` 成功或設了 `FORMWRIGHT_TEST_PG`）。
7. **plugin 更新**（AGENTS.md「票」）：`claude plugin marketplace update plugharbor && claude plugin update tickets@plugharbor --scope project`，再 `/reload-plugins`；失敗就照舊版跑並記進回報。票 #68 改 in-progress。

## 子段切法（四個實作者交棒點）

| 批 | 內容 | Task | 手寫行數（估） | migration |
|---|---|---|---|---|
| i4d1-1 | 驗證規則與共用案例（後端）、資料表與 migration、覆寫層接進快照與後端訊息、政策 A38／A39 | 1–4 | ≈ 1550（測試約 55%） | `LocalePacks`（Task 2，唯一一個） |
| i4d1-2 | 語系包解析與分析（含 #68）、預覽 API（暫存、Demo 不暫存、上限 5 筆） | 5–6 | ≈ 1250（測試約 55%） | 無 |
| i4d1-3 | 套用 API（409、合併、安裝、稽核）、回到上一版／移除覆寫／移除語系、清單 API | 7–8 | ≈ 1150（測試約 55%） | 無 |
| i4d1-4 | 前端語法檢查與共用案例、最小語系頁、文件與手冊、gate、走查 | 9–11 | ≈ 1250（測試約 40%） | 無 |

同一分支 `i4d1`（worktree `../formwright-i4d1`），每個 Task 各自 commit。前三批收尾各跑一次窄範圍 PostgreSQL 雙跑（不跑 gate）；i4d1-4 收尾才跑 gate、合併。

**交棒條件**：
- **i4d1-1 → i4d1-2**：Task 1–4 驗收全過；`Domain/Localization` 的四個規則類別簽章（判斷 3）、兩張表的欄位與 repository 介面（判斷 7、8）、`LocaleRegistry` 的覆寫層取用方法（判斷 10）、A38／A39 的政策代碼寫進交棒說明。i4d1-1 單獨存在時使用者行為與 main 相同（沒有寫覆寫層的入口）。
- **i4d1-2 → i4d1-3**：Task 5–6 驗收全過；報告的 JSON 形狀（判斷 13）、`LocalePackPlan`（套用要用的內容）與預覽暫存列的欄位寫進交棒說明。此時只能預覽，不能寫入覆寫層。
- **i4d1-3 → i4d1-4**：Task 7–8 驗收全過；全部 API 的路由、回應形狀、錯誤碼（判斷 12、13）寫進交棒說明，前端照它做。

分界理由：i4d1-1 是地基（每個請求都經過的快照與訊息解析，審查重點是「沒有覆寫層時逐字不變」）；i4d1-2 是純計算加一支唯讀為主的 API；i4d1-3 是寫入與交易（審查重點是 409、全有或全無、稽核）；i4d1-4 是畫面與收尾。

---

## 計畫期間的判斷

〔行為〕＝影響使用者看到或能做的事（列在「代為決定的事」節，附例子與推薦，主控裁決待使用者確認）；〔技術〕＝實作選擇。設計 §17 已拍板的不再問。

1. 〔技術〕**語系包格式 v1 只收 JSON**（設計 §5.1、T4）。`format` 必須是 `formwright-langpack`、`formatVersion` 必須是整數 1（比 1 大整包拒收「格式版本太新」，小於 1 或非整數當格式不對）。不認得的頂層欄位略過、不報錯（給之後的版本加欄位）。`ui` 收扁平鍵；若某個值是物件，用 `UiTextMerge.Flatten` 壓平（內建 `ui.json` 是巢狀，翻譯者常照它的形狀交檔），壓平後同鍵出現兩次＝該鍵拒收（`duplicate`）。`server` 只收扁平鍵。`designData` 見〔行為〕Q1。
2. 〔技術〕**檔案判斷看內容不看副檔名**（設計 §13.1）：去掉 UTF-8 BOM 後第一個非空白字元是 `{` 才當 JSON；開頭是 `PK\x03\x04`（xlsx 是 zip）回「本版只收 .langpack.json」（Q2）；其他回「檔案不是語系包」。JSON 用 `JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 })`，超過深度回「JSON 層數太多」。非 UTF-8 的位元組由 `JsonDocument` 擋（`JsonException`），同「檔案不是語系包」。
3. 〔技術〕**驗證規則放 Domain，純函式**（`Domain/Localization/`，Domain 不碰 IO，基準由呼叫端傳入）：
   - `UiMessageSyntax.Check(string text, IReadOnlySet<string> allowedNames) → string?`（回拒收原因代碼或 null）：掃描器只接受一般文字、`{名稱}`（名稱是 `[A-Za-z_][A-Za-z0-9_]*`）、`{'字面'}`（字面內不含 `'`）；裸 `@`、`|`、`%{`、未配對或巢狀的大括號 → `syntax`；名稱不在 `allowedNames` → `placeholder`（Q7）。字面量裡的 `{{欄位}}` 合法（內建 35 句這樣寫）。
   - `ServerTemplateSyntax.Check(string text, string baseTemplate) → string?`：只收 `{n}`（n 是非負整數）與 `{{`、`}}`；`{0,5}`、`{0:D3}` 這類對齊與格式子句 → `formatClause`；大括號不成對 → `syntax`；參數編號集合與 `baseTemplate`（內建 zh-TW）不同 → `placeholder`；最後用 `max(n)+1` 個假參數實跑 `string.Format(CultureInfo.InvariantCulture, …)`，丟例外 → `syntax`。設計 §9.3 說內建四語參數集合一致，`BuiltinServerTextTests.Placeholders_MatchZh_AndFormatDoesNotThrow` 已鎖住，所以後端用「等於 zh-TW」不會誤傷。
   - `LocaleTextSafety.Check(string text, int maxLength) → string?`：Unicode 雙向控制字元（U+202A–202E、U+2066–2069）與其他 `char.IsControl`（`\n`、`\t` 除外）→ `control`；超過 `maxLength`（2000）→ `tooLong`。介面文字、後端範本、`nativeName` 都過它。
   - `LocaleGraphRules.Check(IReadOnlyList<(string Code, string? FallbackCode)> installedAfter, string target) → string?`：`target` 的上層語系必須已安裝、不能是自己、不成環；套用後**每個**已安裝語系的上層鏈都不超過 `LocaleChain.MaxParentDepth`（3）層（改掉某語系的上層可能讓它的下游超過 3 層）。回 `fallbackNotInstalled`／`fallbackSelf`／`fallbackCycle`／`fallbackTooDeep`。
4. 〔技術〕**介面文字的佔位符基準是「內建四語在該鍵用到的名稱聯集」**，不是只看 zh-TW（Q7 的技術面）。依據：main 上有 9 句四語的名稱集合不同（`approval.stagesMore` 繁中是 `{count}`、`{first}`，en-US／th／vi 是 `{first}`、`{rest}`；`approval.panel.notDelegated.initiator`／`.duplicate` 外語多一次 `{user}`）。只比 zh-TW 會把照英文翻的日文拒收。比對前先去掉 `{'…'}` 與 `{{…}}`（同 `locales.vitest.spec.ts` 的 `placeholders`）。聯集由 `BuiltinLocaleResources` 算一次（新增 `UiPlaceholderNames: IReadOnlyDictionary<string, IReadOnlySet<string>>`，`Lazy`）。
5. 〔技術〕**長度與數量上限**（設計 §13.1、§14，可寫死的技術限制）：每句 2000 字元；`pack.version` 50、`pack.note` 500、`pack.builtFor` 20、`pack.baseLocale` 35、`locale.nativeName` 50（`InstalledLocale` 欄長）；`ui`、`server` 各自的鍵數超過該段基準鍵數的 2 倍整包拒收；JSON 深度 16。預覽保留 10 分鐘、同時最多 5 筆、拒收清單前 50 筆也放這裡。這些常數集中在 `Domain/Localization/LocalePackLimits.cs`（一處宣告，Application 與測試都引用它）。檔案大小是政策 A38（判斷 14）。
6. 〔技術〕**是／否撞字（票 #68）在分析時以「套用後」的狀態模擬**：對套用後的每個已安裝語系（含這次新裝的、含已停用的，同 i4c Q2 口徑），沿 `LocaleChain.Fallbacks` 取 `report.Yes`／`report.No`（每層先查套用後的覆寫層、再查內建層，用 `MessageCatalog.FirstInChain`），組成 `BooleanWords.From(...)`（i4c 的既有類別，加一個 `public IReadOnlySet<string> Ambiguous`）。撞到的字若來自**這份語系包**的 `report.Yes` 或 `report.No`，就拒收那一句（原因 `booleanCollision`，參數是撞到的字與另一個語系），重算一次；剩下的撞字不是這份包造成的（例如兩個舊語系本來就撞），只列警告、不擋。整包不拒收（Q4）。
7. 〔技術〕**`Dynamic_LocalePack`（設計 §6.4、§22）**：`Id`（long，自增）、`Code`（35）、`IsCurrent`（bool）、`UiJson`、`ServerJson`、`BaseHashesJson`（text，不壓縮：泰文全套約 170 KB，PostgreSQL 的 TOAST 會自己壓）、`FileHash`（64，SHA-256 十六進位全長）、`FileName`（200）、`PackVersion`（50）、`BuiltFor`（20）、`BaseLocale`（35）、`Note`（500）、`ReportJson`（text，套用當下的報告摘要：各段筆數與完整度，不含拒收清單）、`ImportedUserId`（50）、`ImportedUtc`、`ModifiedUtc`。索引 `(Code)`，**不設** `(Code, IsCurrent)` 唯一索引：回到上一版要對調兩列的 `IsCurrent`，PostgreSQL 的唯一索引逐列檢查會在第一個 UPDATE 就撞（寫入本來就在全庫寫入鎖內序列化，完整性由 repository 的方法保證，見判斷 9）。`UiJson`／`ServerJson` 是**整份**覆寫層（扁平 JSON 物件，鍵 Ordinal 排序），`BaseHashesJson` 是每個覆寫鍵匯入當時內建 zh-TW 原文的雜湊（SHA-256 前 16 個十六進位字元，同 `uiHash` 的長度）；保留舊鍵時沿用舊雜湊、有新值的鍵換新雜湊（設計 §6.5 的「原文已更新」由 i4d2 顯示，本段只存）。基準原文一律取 zh-TW 內建層（基準鍵集就是由它定義），不取 `pack.baseLocale`。
8. 〔技術〕**`previewId` 存資料庫**（設計 §22 的二選一；多個實例可以同時啟動（`CONTRIBUTING.md`），存記憶體的話套用打到另一台就失效）：`Dynamic_LocalePackPreview`：`Id`（字串 32，`Guid.NewGuid().ToString("N")`）、`UserId`（50）、`Code`（35）、`FileHash`（64）、`FileName`（200）、`PlanJson`（text，判斷 11 的 `LocalePackPlan`）、`ReportJson`（text，完整報告）、`LocaleModifiedUtc`（可空：預覽當下該語系列的修改時間，沒安裝是 null）、`CurrentPackId`（可空：預覽當下目前版的 Id）、`CreatedUtc`、`ExpiresUtc`。和 `Dynamic_LocalePack` 放**同一個** migration（設計 §20「`previewId` 若存資料庫再加一個」；放同一個 migration 守住「每段一組」）。暫存的是**解析後、只含要寫入的句子**的計畫，不是原始檔。
9. 〔技術〕**repository**（`Domain/Repositories/Dynamic/ILocalePackRepository.cs`，`Persistence/Repositories/Dynamic/LocalePackRepository.cs`）：
   - `Task<List<LocalePack>> ListCurrentAsync(CancellationToken)`（不追蹤，給快照）；`Task<LocalePack?> CurrentAsync(string code, …)`、`Task<LocalePack?> PreviousAsync(string code, …)`（追蹤）；`Task<int> CountAsync(string code, …)`。
   - `void AddVersion(LocalePack next, LocalePack? current, LocalePack? previous)`：刪 `previous`、把 `current` 標成上一版（`IsCurrent=false`、`ModifiedUtc=now`）、加入 `next`（`IsCurrent=true`）。三步在同一個 `SaveChanges`，呼叫端在交易內。
   - `void Swap(LocalePack current, LocalePack previous)`：對調 `IsCurrent`，兩列都更新 `ModifiedUtc`（讓版本戳記一定變）。
   - `Task DeleteForLocaleAsync(string code, …)`：刪該語系的版本與預覽（移除語系用）。
   - 預覽：`AddPreview`、`FindPreviewAsync(string id)`、`RemovePreview`、`Task PrunePreviewsAsync(DateTime now, int keep, …)`（刪過期的，再把超過 `keep` 筆的最舊的刪掉）。`PrunePreviewsAsync` 用 `ExecuteDeleteAsync`，**在交易內**呼叫（預覽 handler 是 `ITransactionalRequest`），不屬於 `backend.md`「交易外的 ExecuteDelete」那類。
   - `ILocaleRepository.StampAsync` 的 `LocaleVersionStamp` 加兩欄：`PackCount`、`PackMaxModifiedUtc`（`Dynamic_LocalePack` 的筆數與最大修改時間；預覽表不算）。`FakeLocaleRepository`（ApplicationTests）同步加覆寫層的假資料與戳記。
10. 〔技術〕**覆寫層接進 `LocaleRegistry`**（設計 §4.2、§6.1、T6、T18）：
    - `Published` 多一欄 `Overlays: IReadOnlyDictionary<string, LocaleOverlay>`；`public sealed record LocaleOverlay(IReadOnlyDictionary<string, string> Ui, IReadOnlyDictionary<string, string> Server, long PackId)`。`EnsureFreshAsync` 戳記變了才重讀：`ListAsync` 加 `ILocalePackRepository.ListCurrentAsync`（同一個 scope），解析 JSON；某一列解析失敗（只可能是資料修補）就當成那個語系沒有覆寫層並記 Warning，不讓整個快照失敗。只留基準鍵以內的鍵（`BuiltinLocaleResources.BaseKeys`、`BuiltinServerText.BaseKeys`）、只留已安裝語系的覆寫層。
    - 覆寫層有變（比對每個語系的 `PackId`）也換新的 `Ui` 快取字典；`Generation` 仍只在**清單**改變時遞增（政策快照只關心清單，覆寫層改了不必重載政策）。
    - `Ui()`：鏈上每個語系的層改成「覆寫層 → 內建層」兩層（覆寫在前），其餘照 i4b 判斷 6、7。
    - 新增 `public LocaleTexts Texts()`：一次 `Volatile.Read` 拿到 `(Infos, Overlays)` 的不可變檢視，提供 `ContextsFor(string defaultLocale)` 與 `ServerOverride(string code, string key) → string?`。既有的 `ContextsFor` 改成 `Texts().ContextsFor(...)`，行為不變。
11. 〔技術〕**`MessageCatalog` 查覆寫層**（i4c 判斷 1「i4d1 起先取覆寫層」）：`Lookup` 從 `internal static` 改成兩個版本——`internal static string Lookup(LocaleContext, string key, Func<string, string, string?>? overlay)`（`BuiltinMessageCatalog` 傳 null，行為不變）；`MessageCatalog.Get` 每次讀一份 `registry.Texts()`，`AsI18n` 整批只讀一份（守住 i4c 判斷 2 的 `AsI18n_RefreshMidway_UsesOneSnapshot`）。每個語系先試覆寫層再試內建層。**覆寫範本 `string.Format` 丟 `FormatException` 時**改用同語系內建層（內建層沒有就繼續往鏈下一個），記 Warning 並帶鍵名：匯入時驗過參數，但平台升級可能改了內建範本的參數個數，覆寫層的舊範本不能讓 API 變 500。
12. 〔技術〕**API**（`I18nController`，路由前綴 `api/v3/dynamic/i18n`；全部 `ISystemAdminRequest`，權限由既有的 `SystemAdminBehavior` 擋，fail closed）：
    - `GET locales`：清單（判斷 13 的形狀）。
    - `POST packs/preview?fileName=…`：本文是原始檔案位元組（`Content-Type` 不限；照 `AppsController.Import` 讀 `Request.Body`），回報告。resource filter `LocalePackBodyLimitFilter`（照 `ImportBodyLimitFilter`）：請求上限＝2 倍 A38＋`UploadGuard.MultipartOverhead`，`Content-Length` 超過直接 413、不讀主體；沒超過的由 handler 精確比對 A38，回「語系包不能超過 {0} MB」（`backend.md`：超過上限的訊息帶出上限數字）。
    - `POST packs/apply`，本文 `{ previewId, fileHash }`。
    - `POST locales/{code}/rollback`、`POST locales/{code}/clear`、`DELETE locales/{code}`。
    - 錯誤：沒權限 403（既有）；Demo 開關關閉時套用、回到上一版、移除覆寫、移除語系 403（`LocalePackImportDisabled`）；`previewId` 不存在、過期、不是自己的一律 404 加「預覽已過期，請重新上傳預覽」（不分辨「別人的」，不洩漏存在與否）；`fileHash` 對不上、語系版本不一致 409（`LocalePackStale`）；沒有上一版、沒有覆寫 409；語系不存在 404；整包層級的格式錯誤 400（`errors.file`）。
13. 〔技術〕**報告的形狀**（前端照它畫，鍵 camelCase）：
    ```json
    {
      "previewId": "…或 null", "expiresUtc": "…或 null", "importEnabled": true,
      "fileName": "ja.langpack.json", "fileHash": "…",
      "locale": { "code": "ja", "isNew": true, "builtin": false, "nativeName": "日本語", "quasarLang": "ja", "fallbackCode": "en-US",
                  "changes": ["nativeName"], "ignored": [] },
      "pack": { "version": "1.2.0", "builtFor": "2026.11", "baseLocale": "zh-TW", "note": "…" },
      "ui":     { "added": 0, "changed": 0, "unchanged": 0, "skipped": 0, "sameAsBuiltin": 0, "extra": 0, "extraKeys": ["…前 50"],
                  "rejected": 0, "rejectedItems": [{ "key": "…", "reason": "placeholder", "args": ["{count}"] }] },
      "server": { "…欄位同 ui…": 0 },
      "completeness": { "ui": { "have": 1790, "total": 2701 }, "server": { "have": 160, "total": 300 } },
      "designData": { "count": 0 },
      "warnings": [{ "code": "booleanCollisionExisting", "args": ["…"] }]
    }
    ```
    逐句拒收原因與警告一律是**代碼**，畫面用前端 i18n 鍵（`localePacks.reasons.<代碼>`、`localePacks.warnings.<代碼>`）組字；整包層級的錯誤（判斷 1、2、5、Q2、Q3 的拒收）走 400 的 `errors.file`，文字經 `IMessageCatalog`（新鍵進 `MessageText`，判斷 18）。`completeness.have` 是套用後「該語系自己有句子」的鍵數（覆寫層＋該語系的內建層，不含走退回鏈補的）。`rejectedItems`、`extraKeys` 只回前 50 筆（設計 §6.3）。
14. 〔技術〕**政策 A38、A39**（設計 §14；Task 4）：`locale.packMaxBytes`（Integer、S、出廠 5242880、範圍 1024–20971520、`Unit: "bytes"`）、`locale.maxInstalled`（Integer、S、出廠 10、範圍 4–30、`Unit: "count"`；下限 4＝內建四語，含內建）。分類 `locale`。A39 只在**新安裝**時檢查（`backend.md` 政策調嚴不溯及既有）：改成比目前已安裝數小不擋存檔，只擋下一次安裝，訊息「最多只能安裝 {0} 個語系（含內建）」。
15. 〔技術〕**Demo 開關**（設計 §13.4）：部署設定 `Localization:PackImportEnabled`（環境變數 `Localization__PackImportEnabled`），沒設時 `Demo:LoginEnabled` 為 true 就關閉、否則開放（同 `AttachmentSettingsFactory` 的 `UploadEnabled`）。`public sealed record LocalePackSettings(bool ImportEnabled)` 在 `Program.cs` 註冊 singleton。關閉時：預覽照常產生報告但**不寫預覽列**（`previewId` 為 null，設計 §6.3）；套用、回到上一版、移除覆寫、移除語系回 403。`Dockerfile.demo` 不用改（它已設 `Demo__LoginEnabled=true`），`docs/deploy.md` 補這個變數。
16. 〔技術〕**套用的流程**（設計 §6.3 第 3 步、T16；Task 7）：交易內 → 取預覽列（不存在、過期、`UserId` 不是自己 → 404）→ `fileHash` 比對 → 重讀該語系列的 `ModifiedUtc` 與目前版 `Id`，和預覽列記下的不同 → 409 → 新安裝時再查一次 A39 與 `LocaleGraphRules`（預覽之後別人可能裝了別的語系）→ 寫語系列（新安裝：`Origin=installed`、`CreatedUtc=ModifiedUtc=now`；既有的匯入語系有 `locale` 欄位變動才更新並改 `ModifiedUtc`；內建語系不動，Q3）→ 組新版本：目前版的覆寫層＋計畫裡的句子（U5 逐句合併；「與內建相同」的句子從覆寫層拿掉，Q6）→ `AddVersion` → 刪預覽列 → 稽核 → `afterCommit.Add("locale-registry", registry.Invalidate)`。不重新驗句子：計畫是預覽時驗過、存進預覽列的，409 保證基底沒變。
17. 〔技術〕**稽核**（設計 §11）：`AuditActions` 加分組 `locale`（四語標籤 `audit.groups.locale`）與動作 `locale.install`、`locale.pack.import`、`locale.pack.rollback`、`locale.pack.clear`、`locale.remove`（都是 `W`：`backend.md` 的 HighRisk 只留權限類）；`AuditTargets.Locale = "locale"`。套用請求宣告 `locale.pack.import`，handler 在**新安裝**時呼叫新的 `AuditContext.Action("locale.install")` 換掉動作代碼（只在最外層生效，`AuditBehavior` 寫列時用 `context.ActionCode ?? action.Code`；換成沒登記的代碼丟 `InvalidOperationException`，`AuditCoverageTests` 加一案）。摘要：語系、檔名、檔案雜湊、各段新增／變更／略過／拒收筆數、`designData` 筆數（本段一律 0 套用）、是否新安裝；不記句子內容。目標名稱用語系的 `NativeName`（自稱，不隨語系翻，符合「寫進資料的系統文字用預設語系」的精神）。預覽不稽核：`AuditCoverageTests.Exempt` 加 `PreviewLocalePackRequest`，理由「只寫 10 分鐘的預覽暫存，不改任何設定；套用時才稽核」。
18. 〔技術〕**新的後端訊息進 `MessageText`（四語）**，集中在 `LocaleService/LocalePackMessages.cs` 組 `MessageRef`（`backend.md`：使用者看得到的訊息集中在同功能的 `*Messages.cs`）。新鍵另建範本基準檔 `ServerTextBaseline/locale-pack.json`（Task 6 Step 1 由新表產生後凍結），`BuiltinServerTextTests.BaselineFiles` 加這個檔名；**既有三個基準檔不准改**（S5 驗）。新鍵也會成為語系包 `server` 段的基準鍵（語系包本身的錯誤訊息也翻得到）。
19. 〔技術〕**移除語系**（設計 §4.5；Task 8）：只限 `Origin=installed`（內建回 409「內建語系不能移除，只能停用」）；讀三個語系政策的生效值，在啟用、預設或必填裡就 409「請先在政策設定停用這個語系」；有其他已安裝語系的 `FallbackCode` 指向它就 409「請先改掉{0}的上層語系」（`{0}` 列自稱，多個用「、」接）。通過就刪語系列、版本與預覽（`DeleteForLocaleAsync`），設計資料裡的文字不動。告警通道的信件語系剛好是它時不擋（Q9）。
20. 〔技術〕**回到上一版、移除覆寫**（設計 §6.4、§6.5；Task 8）：回到上一版＝`Swap`，沒有上一版 409；移除覆寫＝`AddVersion` 一個空的新版本（Q5），目前版本來就是空的（或沒有任何版本）409「這個語系沒有覆寫可以移除」。兩者都不動語系列。
21. 〔技術〕**測試造資料**：`WebAPITests/LocaleTestKit.cs` 加 `ApplyPackAsync(TestApp app, string json, string fileName = "t.langpack.json")`：以 admin 呼叫預覽、套用，再照既有 `RefreshAsync` 的重試寫法等到 `registry.Texts()` 反映這個語系的新 `PackId`（`EnsureFreshAsync` 遇到別人正在換新會直接返回，設計如此；不等的話下一個請求可能拿到舊快照，i4a 的 CI 偶發就是這個）。所有「套用後立刻取文字」的測試都經它，不直接 `Task.Delay`。`InstallAsync`／`RemoveAsync` 保留（既有測試在用）。
22. 〔技術〕**前端語法檢查**（設計 §13.2、T15；Task 9）：`frontend/src/i18n/message-check.ts` 的 `checkUiMessage(text, allowed): string | null` 實作判斷 3 的同一套規則（含控制字元與長度），再加 `baseCompile` 不得報錯。`shared/i18n-message-cases.json` 的 `ui` 段前後端都跑；`server`、`boolean` 段只有後端跑（前端沒有後端範本與撞字的檢查）。`locales.vitest.spec.ts` 加「四份內建檔每句都通過 `checkUiMessage`（allowed＝該鍵四語名稱聯集）」。執行期的 `prepareMessages` **不改**（仍只做 `baseCompile` 縱深防禦；伺服器回的文字已在匯入時驗過，前端再多一層白名單會讓兩邊規則漂移時整句消失而沒人發現）。
23. 〔技術〕**前端最小語系頁**（Task 10）：路由 `/system/locales`，`systemLinks` 只對 `isSystemAdmin` 加 `{ to: '/system/locales', label: 'menu.locales', icon: 'translate', testid: 'locales-menu' }`（放在「政策設定」之後）。頁面 `pages/system/LocalesPage.vue`＋`presentation/locale/LocalePackImportDialog.vue`＋`presentation/locale/LocalePackReport.vue`；查詢 `application/dynamic/queries/locale-packs-queries.ts`；API 加在 `services/api/dynamic-api.ts`。清單用 `q-list`（390 寬也是同一個列表，不用 `q-table`），每列顯示自稱、代碼、來源（內建／匯入）、啟用／預設標籤、覆寫句數（介面、後端訊息）、目前版本與匯入時間（營運時區加 `+08:00` 標示，`frontend.md`「時間顯示」）。上傳用隱藏的 `<input type="file" accept=".json,application/json">`（同 `AppsPage.vue`）讀成 `ArrayBuffer` 原樣送出。套用成功後 invalidate 語系清單、生效政策（`policies/effective`）與 `auth/options` 查詢；i4b 的 `refreshLoadedMessages` 在生效政策回來後就換上新文字，不重新整理頁面。
24. 〔技術〕**`pack.builtFor` 本段只顯示、不比對**（設計 §5.2「`builtFor` 與目前版本不同：只提示」）：平台目前沒有可比的版本號（main 上沒有任何版本字串可取），硬湊一個會每次都誤報。i4d2 的匯出樣板開始寫 `builtFor` 時，再一起定平台版本的來源與提示。

---

## 代為決定的事

### 使用者行為類（答案反過來，使用者的操作、畫面或權限會變；主控裁決，待使用者確認）

- **Q1 語系包裡帶了 `designData`（設計資料翻譯）段，本段怎麼處理？**
  - 例：廠商給的日文包同時帶了「訂單」模組的欄位譯名 300 段。本段（i4d1）還不會套用設計資料（那是 i4e）。
  - 推薦：**照樣匯入介面文字與後端訊息，設計資料段略過**，報告寫「設計資料 300 段：這個版本還不會套用，之後的版本可再匯入同一個檔」。
  - 另一個選項：整包拒收，請管理員拿掉 `designData` 再傳。代價是同一個檔 i4e 之後又得改回來，而且官方語系包常會同時帶兩種。
  - 選錯代價：選「略過」的代價是管理員可能沒看報告、以為譯名已生效；選「拒收」是多一道手工。
- **Q2 上傳的是 Excel（`.xlsx`）怎麼辦？**
  - 例：翻譯社交回一本 Excel。Excel 翻譯樣板（匯出與匯入）排在 i4e。
  - 推薦：**本段直接拒絕**，訊息「本版只收 .langpack.json，Excel 翻譯樣板之後支援」。
  - 選錯代價：沒有別的合理選項（本段沒有 Excel 的轉換器）；只是訊息要講清楚，不要只說「格式不對」。
- **Q3 匯入內建語系（繁中、English、ไทย、Tiếng Việt）的語系包時，`locale` 段改了自稱、上層語系或日期元件語言包，要不要照改？**
  - 例：泰國廠的泰文包把 `nativeName` 寫成「ภาษาไทย」、`fallback` 寫成 `en-US`。
  - 推薦：**內建語系的 `locale` 段一律不套用，句子照常匯入**，報告列「內建語系的名稱與上層語系不能改，已略過：nativeName、fallback」。理由：內建四語的資訊隨程式出貨（`BuiltinLocales`），語系快照本來就不讀資料庫裡內建列的欄位；開放改要多一套「內建列的覆寫」，而且繁中是平台基準語系、不能有上層。
  - 另一個選項：開放改內建語系的上層語系（例如讓泰文缺字先退英文）。代價是多一個資料欄與快照規則，退回鏈的共用案例要加情境。
  - 選錯代價：選「不套用」，想讓泰文缺字先退英文的客戶做不到（只能改系統預設語系）；選「開放」的代價如上。
- **Q4 是／否撞字（票 #68）擋哪一層：整包還是那一句？**
  - 例：日文包把 `report.No` 誤翻成「はい」，而 `report.Yes` 也是「はい」。
  - 推薦：**只拒收撞到的那一句**（報告列「與「是」的翻譯相同，會讓匯入無法判斷是否欄位」），其他句子照常匯入；`report.No` 缺了就沿退回鏈用上層或預設語系的「否」。理由：與設計 §5.2「佔位符不一致：該句拒收，不是整包拒收」同一個口徑；撞字只壞那一句。
  - 另一個選項：整包拒收（票面寫「整包拒收或列為錯誤」）。代價是一份 2700 句的包因為一個字整份重傳。
  - 選錯代價：選「該句拒收」，管理員可能沒注意那句被拒、日文使用者匯出 CSV 時是否欄顯示上層語系的字（例如英文 No）；選「整包」是重工。
- **Q5 「移除覆寫」之後能不能用「回到上一版」救回來？**
  - 例：管理員誤按泰文的「移除覆寫」，30 句校對過的泰文回到機器翻譯。
  - 推薦：**能**。移除覆寫做成「新增一個空的版本」，原本的版本變成上一版，按「回到上一版」就回來。
  - 另一個選項：移除覆寫把兩版都刪掉（真正的清空）。代價是誤按救不回來，只能重新匯入原檔。
  - 選錯代價：選「能」的代價是「移除覆寫」之後上一版仍佔一列（每語系最多兩列，不會累積）；確認框要寫明「可以用『回到上一版』復原」。
- **Q6 匯入的句子和內建翻譯一字不差，要不要存成覆寫？**
  - 例：管理員把整份泰文匯出（i4d2 之後）、只改 30 句再匯回，其餘 2670 句和內建一模一樣。
  - 推薦：**不存**，報告列「與內建相同 2670 句（未存成覆寫）」；之前覆寫過、這次改回內建原文的句子，從覆寫層拿掉。理由：存了的話那 2670 句會被「釘住」，之後平台升級修正泰文翻譯時，這個客戶看不到修正，語系頁也會顯示 2700 句覆寫，客服看不出他們真正改了哪 30 句。
  - 另一個選項：照存。好處是語系包就是完整狀態、好理解。
  - 選錯代價：選「不存」，客戶想「凍結」某句、不讓升級改它時做不到（要改成不同的字）；選「照存」如上。只影響內建語系（新語系沒有內建層）。
- **Q7 介面文字的佔位符（`{n}`、`{name}`）怎麼比？**
  - 例：內建「已選 {count} 筆」，日文寫成「{n} 件選択」→ 拒收（設計 §5.2 的例子）。但內建裡有 9 句英文、泰文、越南文用的名稱和繁中不同（例如繁中「{first} 等 {count} 關」、英文「{first} and {rest}」），照英文翻的日文若只能用繁中那組，會被誤擋。另一種情況：翻譯者漏掉 `{count}`，句子少了數字。
  - 推薦：**只能用內建四語在這句用過的名稱；少用不擋**。理由：多出不認得的名稱會在畫面上顯示成空白或 `{x}`，一定要擋；少用只是句子少一個資訊，不會壞畫面，擋了反而讓「照英文語序翻」的句子進不來。
  - 另一個選項：名稱集合必須和繁中完全相同（設計 §5.2 原文）。代價是上面 9 句照英文翻的會被拒，翻譯者要去對照繁中。
  - 選錯代價：選「少用不擋」，漏掉數字的句子要靠母語者看畫面才發現；選「完全相同」如上。後端範本不受影響（四語參數一致，照設計比 zh-TW）。
- **Q8 最小語系頁要不要已經有「回到上一版」「移除覆寫」「移除語系」與匯入後的「現在啟用」？**
  - 例：管理員匯入 60% 的日文試看，想先移除；或匯入後想直接啟用。
  - 推薦：**要**，列上用一個「⋯」選單放三個動作（各有確認框，移除語系的確認框照設計 §4.5 的文字），匯入完成畫面在新語系未啟用時顯示「現在啟用」（走既有的政策 API，`locale.enabled` 加上這個語系；被政策約束擋下就顯示後端訊息）。完整度、匯出樣板、「以此語系預覽」、390 寬的卡片版型留給 i4d2。理由：後端 API 本段就有，沒有畫面的話這些動作只能靠 API 工具，而 U2 的「匯入後另有現在啟用按鈕」是已裁決的行為。
  - 另一個選項：本段只有「上傳 → 報告 → 套用」，其餘到 i4d2。代價是 i4d1 上線到 i4d2 之間，誤匯入的語系沒有畫面能撤。
  - 選錯代價：選「要」多約 200 行前端；選「不要」如上。
- **Q9 移除語系時，告警通道的「信件語言」剛好是它，要不要擋？**
  - 例：管理員把告警信語言設成「日本語」，之後移除日文。
  - 推薦：**不擋**，信件改用退回鏈（預設語系 → 繁中）；手冊與已知限制寫明。理由：設計 §4.5 只列了政策與上層語系兩種阻擋條件；告警信的語言本來就容許「已停用」的值照寄。
  - 另一個選項：擋下，訊息「告警通道的信件語言使用中」。代價是多一條檢查與訊息。
  - 選錯代價：選「不擋」，告警信在移除後悄悄換語言；選「擋」是多一步。
- **Q10 Demo 站要不要附一份範例語系包讓訪客試預覽？**
  - 例：Demo 訪客想看匯入報告長什麼樣，但手上沒有語系包檔。
  - 推薦：**本段不附**；Demo 種子不改。Demo 上看得到語系頁、內建四語的清單、上傳後的報告與「這個環境不允許匯入語系包」。i4d2 的「匯出樣板」會給訪客一個可以直接上傳預覽的檔。走查用 repo 裡的測試檔（`backend/tests/Fixtures/langpacks/`）。
  - 另一個選項：語系頁加「下載範例語系包」（一個虛構的日文小包）。代價是多一份要維護的檔與一個只有 Demo 用得到的按鈕。
  - 選錯代價：選「不附」，i4d1 到 i4d2 之間 Demo 訪客自己做不出可預覽的檔；選「附」如上。

### 技術類（主控決定，可推翻）

判斷 1–24 全部是技術類。重點摘要：預覽存資料庫並和版本表同一個 migration（判斷 8）；409 只比「該語系」的版本（判斷 16：匯入 `ja` 不會讓同時在預覽 `th` 的人失敗；新安裝時另外重驗 A39 與上層語系圖）；後端範本照設計比 zh-TW、介面文字比四語聯集（判斷 3、4）；覆寫範本執行期格式錯誤時退回內建層（判斷 11）；稽核新增 `locale` 分組與「新安裝換動作代碼」（判斷 17）；前端執行期不加白名單（判斷 22）。

---

## Global Constraints

- **沒有匯入語系包時使用者看得到的行為不變**：內建四語的介面文字、後端訊息、信件、報表文字與 main 逐字相同。守衛：既有 `ServerTextBaseline/{message-report,task-report,alert}.json` 不改（S5）；`MessageCatalogTests.Builtin_SameAsBaseline`、`BuiltinServerTextTests.Table_EqualsBaselines`、`LocaleRegistryUiTests`、`I18nUiApiTests`、`InstalledLocalePolicyTests`（含 `NoLocalePack_SameAsP3`）、`AuthApiTests.Options_IncludesLocaleSettings` 照過；Task 3 Step 0 記下四個內建語系目前的 `uiHash`，Step 4 確認覆寫層接上後仍相同（Task 10 加介面文字之後雜湊本來就會變，所以這個比對只在 Task 3 做一次、記進交棒說明，不寫成永久測試）。
- **既有測試的預期值不改**，例外只有：`LocaleRepositoryTests` 的 `new LocaleVersionStamp(0, null)` 補兩欄；`menu-nav.vitest.spec.ts` 的系統管理連結清單加 `/system/locales`；`BuiltinServerTextTests.BaselineFiles` 加 `locale-pack.json`；`AuditCoverageTests.Exempt` 加預覽請求。
- **鐵則**：業務政策只有 A38、A39 兩條，都可設定；可寫死的列在下一節；權限只限系統管理員、fail closed（`SystemAdminBehavior`）；驗證全在後端（前端的語法檢查只用在測試）；沒有執行期 DDL（新語系的文字存在既有 JSON 欄）；空系統照樣能裝語系、匯入介面文字（不依賴任何應用程式）。
- **PostgreSQL**：新表、新 repository、匯入交易、409、回滾、移除的測試都標 `Category=Postgres`，每個 Task 的 Step 4 在 PostgreSQL 跑過一次。不寫 raw SQL。
- **前端**：新句子加進 `shared/i18n/zh-TW/ui.json`，en-US、th、vi 同步（不留中文當英文），維持單行 JSON；跑 `locales.vitest.spec.ts`、`policy-i18n.vitest.spec.ts`、`src/css/themes/no-hardcoded-colors.vitest.spec.ts`、`src/i18n/table-no-data-label.vitest.spec.ts`。
- **測試只跑窄範圍**；後端與其他吃 CPU 的測試不並行；窄範圍 vitest 加 `--maxWorkers=2`。完整套件交 CI（i4d1-4 收尾的 gate）。
- **git**：逐檔 `git add`；commit 前 `git status`；不用 `--no-verify`、不 force push、不 merge commit；公開 repo 不寫公司字串、個人絕對路徑、內網 IP；測試資料全部虛構。
- **本機走查**：`bash scripts/unattended/local-demo.sh <worktree 絕對路徑>`（一律絕對路徑，背景執行等 READY）。
- 段收尾用 `formwright-purpose-check`；上線用 `formwright-ship`。

## 寫死清單（本段新增，逐條標性質；Task 11 寫進設計 §14）

| 項目 | 性質 | 理由 |
|---|---|---|
| 語系包只收單一 JSON；`format`＝`formwright-langpack`、`formatVersion` 1 | 安全／技術限制 | 設計 T4、§5.2 |
| JSON 深度 16、每句 2000 字元、`pack` 各欄長度、`nativeName` 50、鍵數上限為基準 2 倍 | 安全／技術限制 | 設計 §13.1（`LocalePackLimits`，判斷 5） |
| 介面文字語法白名單（一般文字、`{名稱}`、`{'字面'}`）、佔位符只能用內建四語用過的名稱 | 安全 | 設計 §13.2；判斷 3、4 |
| 後端範本只收 `{n}`、`{{`、`}}`，參數集合同 zh-TW，實跑不丟例外 | 安全 | 設計 §9.3 |
| 拒收雙向控制字元與其他控制字元（換行、tab 除外） | 安全 | 設計 §13.2 |
| 上層語系已安裝、不成環、最多 3 層；被當成上層的不能移除 | 資料完整性 | 設計 §4.1、§4.5（`LocaleGraphRules`） |
| 內建語系不能移除；啟用、預設、必填中的語系不能移除 | 資料完整性 | 設計 §4.3、§4.5 |
| 是／否撞字的句子拒收 | 資料完整性 | 票 #68、i4c Q2（判斷 6） |
| 每語系保留目前版與上一版兩份覆寫層 | 已列（設計 §14） | — |
| 預覽保留 10 分鐘、同時最多 5 筆（全系統） | 已列（設計 §14） | — |
| 預覽綁定建立者；套用時語系版本不一致 409 | 已列（設計 §14） | — |
| 匯入相關寫入只限系統管理員；部署開關 `Localization:PackImportEnabled` | 已列（設計 §14） | — |
| 預覽、套用回應的拒收清單只列前 50 筆 | 技術限制 | 設計 §6.3 |
| 覆寫層基準原文雜湊取 zh-TW 內建層、SHA-256 前 16 字元 | 技術限制 | 判斷 7 |

**業務政策**：A38 `locale.packMaxBytes`、A39 `locale.maxInstalled`（判斷 14），不寫死。

## 檔案地圖

**共用檔（並行工作要協調；★＝本段會動）**

| 共用檔 | 本段 | 說明 |
|---|---|---|
| ★ DbContext 模型與 migration | Task 2 | 兩張新表；`scripts/db/add-migration.sh LocalePacks` 一次產 SQLite 與 PostgreSQL |
| ★ 語系快照與後端訊息 | Task 3 | `Application/Common/LocaleRegistry.cs`、`Application/Common/Localization/{MessageCatalog,BuiltinMessageCatalog,BuiltinLocaleResources}.cs` |
| ★ 政策目錄 | Task 4 | `Domain/Policies/PolicyCatalog.cs`、`shared/policy-catalog.json` |
| ★ 稽核目錄 | Task 7 | `Application/Common/Audit/{AuditActions,AuditContext}.cs`、`Application/Common/Behaviors/AuditBehavior.cs` |
| ★ 後端訊息表 | Task 6 | `Application/Common/MessageText.cs`（新鍵），基準檔 `locale-pack.json` |
| ★ 介面文字 | Task 4、7、10 | `shared/i18n/{zh-TW,en-US,th,vi}/ui.json`（政策名稱、稽核分組與動作、語系頁） |
| ★ 前端選單與路由 | Task 10 | `frontend/src/domain/dynamic/menu-nav.ts`、`frontend/src/router/routes.ts` |
| `backend/apps/*.app.json`、套件格式 | 不動 | 套件語系鍵改格式是 i4f |
| `Dockerfile`、`Dockerfile.demo` | 不動 | Demo 開關由 `Demo__LoginEnabled` 推得（判斷 15） |

**新檔（後端）**：`Domain/Localization/{UiMessageSyntax,ServerTemplateSyntax,LocaleTextSafety,LocaleGraphRules,LocalePackLimits}.cs`；`Domain/Entities/Dynamic/{LocalePack,LocalePackPreview}.cs`；`Domain/Data/Configurations/LocalePackConfiguration.cs`；`Domain/Repositories/Dynamic/ILocalePackRepository.cs`；`Persistence/Repositories/Dynamic/LocalePackRepository.cs`；migration 兩份；`Application/Dynamic/LocaleService/Packs/{LocalePackModels,LocalePackParser,LocalePackAnalyzer,LocalePackSettings}.cs`；`Application/Dynamic/LocaleService/LocalePackMessages.cs`；`Application/Dynamic/LocaleService/Commands/{PreviewLocalePack,ApplyLocalePack,RollbackLocalePack,ClearLocaleOverrides,RemoveLocale}/…`；`Application/Dynamic/LocaleService/Queries/GetLocales/…`；`WebAPI/Controllers/v3/Dynamic/LocalePackBodyLimitFilter.cs`；`shared/i18n-message-cases.json`；`backend/tests/Fixtures/langpacks/{ja-partial,th-fix,bad-mixed,rtl}.langpack.json`。

**新檔（測試）**：`DomainTests/Localization/{LocaleMessageCasesTests,LocaleGraphRulesTests}.cs`；`ApplicationTests/Persistence/LocalePackRepositoryTests.cs`；`ApplicationTests/Common/LocaleRegistryOverlayTests.cs`；`ApplicationTests/Dynamic/LocalePacks/{LocalePackParserTests,LocalePackAnalyzerTests}.cs`；`ApplicationTests/Common/ServerTextBaseline/locale-pack.json`；`WebAPITests/Dynamic/{LocalePackPreviewApiTests,LocalePackApplyApiTests,LocaleManageApiTests}.cs`。

**新檔（前端）**：`src/i18n/message-check.ts`＋`message-check.vitest.spec.ts`；`src/pages/system/LocalesPage.vue`＋spec；`src/presentation/locale/{LocalePackImportDialog,LocalePackReport}.vue`＋spec；`src/application/dynamic/queries/locale-packs-queries.ts`。

---

# i4d1-1 地基

### Task 1: 驗證規則與共用案例檔（後端）

**Files:**
- Create: `Domain/Localization/UiMessageSyntax.cs`、`ServerTemplateSyntax.cs`、`LocaleTextSafety.cs`、`LocaleGraphRules.cs`、`LocalePackLimits.cs`（判斷 3、5）
- Create: `shared/i18n-message-cases.json`（`{ "ui": [...], "server": [...], "boolean": [...] }`，判斷 22）
- Modify: `Application/Common/Localization/BuiltinLocaleResources.cs`（加 `UiPlaceholderNames`，判斷 4）
- Test: `DomainTests/Localization/LocaleMessageCasesTests.cs`、`LocaleGraphRulesTests.cs`（新）；`ApplicationTests/Common/BuiltinLocaleResourcesTests.cs`（加案例）

**Interfaces:**
```csharp
namespace DynamicModules.Domain.Localization;

public static class LocalePackReasons   // 放在 LocalePackLimits.cs 同檔
{
    public const string Syntax = "syntax", Placeholder = "placeholder", FormatClause = "formatClause", Control = "control",
        TooLong = "tooLong", NotString = "notString", Duplicate = "duplicate", BooleanCollision = "booleanCollision";
}
public static class UiMessageSyntax { public static string? Check(string text, IReadOnlySet<string> allowedNames); }
public static class ServerTemplateSyntax { public static string? Check(string text, string baseTemplate); }
public static class LocaleTextSafety { public static string? Check(string text, int maxLength); }
public static class LocaleGraphRules
{
    public const string FallbackNotInstalled = "fallbackNotInstalled", FallbackSelf = "fallbackSelf", FallbackCycle = "fallbackCycle", FallbackTooDeep = "fallbackTooDeep";
    public static string? Check(IReadOnlyList<(string Code, string? FallbackCode)> installedAfter, string target);
}
// BuiltinLocaleResources（既有類別，新增）
public static IReadOnlyDictionary<string, IReadOnlySet<string>> UiPlaceholderNames { get; }
```

案例檔形狀：`ui` 每案 `{ "name", "text", "allowed": ["n"], "expected": null | "<原因>" }`，可用 `"repeat": 2001` 代替超長文字；`server` 每案 `{ "name", "base", "text", "expected" }`；`boolean` 每案 `{ "name", "installed": [{ "code", "fallback", "yes", "no" }], "pack": { "code", "fallback", "yes", "no" }, "expectedRejected": ["report.No"], "expectedWarnings": 0 }`（`yes`／`no` 為 null＝沒有自己的句子、走退回鏈）。

- [ ] **Step 1: 寫失敗的測試與案例檔**
  - 案例檔至少收：一般文字；`{n}` 合法；`{count}` 而 allowed 只有 `n` → `placeholder`；少用佔位符合法（Q7）；裸 `@`、`a|b`、`%{x}`、`{`、`}`、`{{x}`、`{a{b}}` → `syntax`；`{'@'}`、`{'|'}` 合法；內建寫法 `{'{{欄位}}'}` 合法（從 `print.hint` 那類句子抄一句）；`{'it's'}`（字面內有 `'`）→ `syntax`；U+202E、U+2066、`\u0007` → `control`；`\n`、`\t` 合法；2001 字 → `tooLong`。`server`：`{0}` 合法；`{{0}}` 跳脫合法；缺 `{0}`、多 `{1}` → `placeholder`；`{0,100000000}`、`{0:D99999}` → `formatClause`；`{x}`、`{0` → `syntax`；雙向控制字元 → `control`。`boolean`：`ja` 的 No＝Yes＝「はい」→ 拒收 `report.No`（兩句相同時拒收 No，保留 Yes；理由寫在案例 name）；`ja` 的 Yes＝「No」→ 拒收（撞固定別名）；`ja` 的 Yes＝`th` 的「ไม่」→ 拒收；`ja` 只有 Yes「はい」、No 缺（走 `en-US` 的 No）→ 合法；兩個既有語系本來就撞、這份包沒帶是／否 → 不拒收、警告 1。
  - `LocaleMessageCasesTests`：讀 repo 的 `shared/i18n-message-cases.json`（往上找檔的寫法照 `BuiltinLocaleResourcesTests`），`ui` 段每案 `LocaleTextSafety.Check(text, 2000) ?? UiMessageSyntax.Check(text, allowed)` 等於 `expected`；`server` 段每案 `LocaleTextSafety.Check ?? ServerTemplateSyntax.Check(text, base)` 等於 `expected`。（`boolean` 段在 Task 5 的 `LocalePackAnalyzerTests` 跑，它要 catalog 的鏈。）
  - `LocaleGraphRulesTests`：上層未安裝、指自己、`a→b→a` 成環、`a→b→c→d→e`（4 層）各回對應代碼；改 `b` 的上層讓它的下游 `a` 變 4 層 → `fallbackTooDeep`；正常 3 層通過。
  - `BuiltinLocaleResourcesTests.UiPlaceholderNames_UnionOfFourLocales`：`approval.stagesMore` 的集合是 `{count, first, rest}`；沒有佔位符的鍵是空集合；鍵集＝`BaseKeys`。
  - `BuiltinLocaleResourcesTests.Builtin_PassesSyntax`：四語內建每一句都通過 `UiMessageSyntax.Check(text, UiPlaceholderNames[key])` 與 `LocaleTextSafety.Check(text, 2000)`（內建層不會被自己的驗證器拒收；有句子不過就停下回報，不放寬規則）。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.DomainTests --filter "FullyQualifiedName~LocaleMessageCasesTests|FullyQualifiedName~LocaleGraphRulesTests"`；`dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~BuiltinLocaleResourcesTests"`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；另跑 DomainTests `--filter "FullyQualifiedName~LocaleChain|FullyQualifiedName~LocaleCode"`。
- [ ] **Step 5: Commit**（`feat(i18n): 語系包驗證規則與前後端共用案例檔`）

**驗收**：Step 4 全過；`Domain/Localization` 的新檔不引用 `System.IO`、`Microsoft.EntityFrameworkCore`（`grep -n "System.IO\|EntityFrameworkCore" backend/src/DynamicModules.Domain/Localization/*.cs` 無結果）。

### Task 2: 覆寫層版本表、預覽暫存表與 migration

**Files:**
- Create: `Domain/Entities/Dynamic/LocalePack.cs`、`LocalePackPreview.cs`（判斷 7、8）
- Create: `Domain/Data/Configurations/LocalePackConfiguration.cs`（兩個 `IEntityTypeConfiguration`，`ToTable("Dynamic_LocalePack")`、`ToTable("Dynamic_LocalePackPreview")`；時間欄 `.AsUtc()`；字串欄長照判斷 7、8；JSON 欄不設長度）
- Create: `Domain/Repositories/Dynamic/ILocalePackRepository.cs`、`Persistence/Repositories/Dynamic/LocalePackRepository.cs`（判斷 9）
- Modify: `Domain/Repositories/Dynamic/ILocaleRepository.cs`（`LocaleVersionStamp` 加 `PackCount`、`PackMaxModifiedUtc`）、`Persistence/Repositories/Dynamic/LocaleRepository.cs`（`StampAsync` 一起數 `Dynamic_LocalePack`；最大時間照既有寫法 `DateTime.SpecifyKind(…, Utc)`）
- Modify: `Persistence/DependencyInjection.cs`（`AddScoped<ILocalePackRepository, LocalePackRepository>()`）
- Create（工具產生，不手改）：`Persistence/Migrations/<時間>_LocalePacks.cs`、`.Designer.cs`，`Persistence.Postgres/Migrations/<時間>_LocalePacks.cs`、`.Designer.cs`，兩份 `DynamicDbContextModelSnapshot.cs` 更新
- Modify: `ApplicationTests/Common/FakeLocaleRepository.cs`（戳記兩欄；另加 `FakeLocalePackRepository`，同檔或新檔）
- Modify: `ApplicationTests/Persistence/LocaleRepositoryTests.cs`（`new LocaleVersionStamp(0, null, 0, null)`）
- Test: `ApplicationTests/Persistence/LocalePackRepositoryTests.cs`（新，`[Trait("Category", "Postgres")]`）

**Interfaces:**
```csharp
public sealed record LocaleVersionStamp(int Count, DateTime? MaxModifiedUtc, int PackCount, DateTime? PackMaxModifiedUtc);

public interface ILocalePackRepository
{
    Task<List<LocalePack>> ListCurrentAsync(CancellationToken ct);            // 不追蹤；依 Code 排序
    Task<LocalePack?> CurrentAsync(string code, CancellationToken ct);         // 追蹤
    Task<LocalePack?> PreviousAsync(string code, CancellationToken ct);        // 追蹤
    void AddVersion(LocalePack next, LocalePack? current, LocalePack? previous, DateTime now);
    void Swap(LocalePack current, LocalePack previous, DateTime now);
    Task DeleteForLocaleAsync(string code, CancellationToken ct);
    void AddPreview(LocalePackPreview p);
    Task<LocalePackPreview?> FindPreviewAsync(string id, CancellationToken ct);
    void RemovePreview(LocalePackPreview p);
    Task PrunePreviewsAsync(DateTime now, int keep, CancellationToken ct);
}
```

- [ ] **Step 1: 寫失敗的測試**（`LocalePackRepositoryTests`，`TestDb`）
  - `AddVersion_KeepsTwo`：連加三版 → 只剩兩列，最新的 `IsCurrent`，第二新的是上一版，第一版已刪。
  - `Swap_FlipsAndTouchesModified`：對調後 `CurrentAsync` 回原本的上一版；兩列 `ModifiedUtc` 都等於 `now`。
  - `Stamp_ChangesOnPackWrite`：加一版、對調一次，`StampAsync` 的 `PackCount`／`PackMaxModifiedUtc` 都會變；只動預覽表時不變。
  - `DeleteForLocale_RemovesVersionsAndPreviews`：只刪該語系的；其他語系的版本與預覽還在。
  - `Prune_ExpiredThenOldest`：7 筆（2 筆過期）、`keep: 4` → 剩最新的 4 筆未過期。
  - `Json_RoundTripsLargeText`：200 KB 的 `UiJson` 寫入讀回逐字相同（含泰文與表情符號）。
  - `ListCurrent_NoTracking`、`Utc_KindPreserved`（讀回的時間 `Kind == Utc`）。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~LocalePackRepositoryTests"`（編譯失敗）
- [ ] **Step 3: 實作**：實體、設定、repository、DI；`bash scripts/db/add-migration.sh LocalePacks`（repo 根目錄）；檢查兩份 migration 只建兩張表與 `Dynamic_LocalePack.Code`、`Dynamic_LocalePackPreview.ExpiresUtc` 兩個索引，沒有動到別的表。
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；另跑 `--filter "FullyQualifiedName~MigrationConsistencyTests|FullyQualifiedName~LocaleRepositoryTests|FullyQualifiedName~LocaleRegistryTests"`；WebAPITests `--filter "FullyQualifiedName~InstalledLocalesUpgradeTests|FullyQualifiedName~DatabaseProviderStartupTests"`（舊資料庫升級套得上新 migration）；PostgreSQL：`FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.ApplicationTests --filter "Category=Postgres&(FullyQualifiedName~LocalePackRepositoryTests|FullyQualifiedName~LocaleRepositoryTests|FullyQualifiedName~MigrationConsistencyTests)"`。
- [ ] **Step 5: Commit**（`feat(i18n): 語系包版本表與預覽暫存表（migration LocalePacks）`）

**驗收**：Step 4 全過（含 PostgreSQL）；`git diff --stat main -- backend/src/*/Migrations` 只有 `LocalePacks` 兩組檔與兩份 snapshot；migration 檔沒有手改（重跑 `add-migration` 產生的內容相同即可，不必實際重跑）。

### Task 3: 覆寫層接進語系快照、介面文字與後端訊息

**Files:**
- Modify: `Application/Common/LocaleRegistry.cs`（`Published` 加 `Overlays`；`EnsureFreshAsync` 讀覆寫層；`Ui()` 兩層；`Texts()`、`LocaleTexts`、`LocaleOverlay`；類別註解把「i4d1 在每個語系前面插覆寫層」改成已實作的說明）
- Modify: `Application/Common/Localization/MessageCatalog.cs`（判斷 11）、`BuiltinMessageCatalog.cs`（呼叫新的 `Lookup` 多載、`overlay: null`）
- Modify: `ApplicationTests/Common/FakeLocaleRepository.cs`（若 Task 2 未加：覆寫層假資料）
- Test: `ApplicationTests/Common/LocaleRegistryOverlayTests.cs`（新）、`ApplicationTests/Common/MessageCatalogTests.cs`（加案例）

**Interfaces:**
```csharp
public sealed record LocaleOverlay(IReadOnlyDictionary<string, string> Ui, IReadOnlyDictionary<string, string> Server, long PackId);

/// <summary>一份快照的不可變檢視：清單、上層關係與覆寫層來自同一次 Volatile.Read。</summary>
public sealed class LocaleTexts
{
    public IReadOnlyList<InstalledLocaleInfo> Infos { get; }
    public IReadOnlyList<LocaleContext> ContextsFor(string defaultLocale);
    public string? ServerOverride(string code, string key);
    public LocaleOverlay? Overlay(string code);
}
// LocaleRegistry（既有類別，新增）
public LocaleTexts Texts();
```

- [ ] **Step 0: 固定基準**（改動前）：跑一次 `--filter "FullyQualifiedName~LocaleRegistryUiTests|FullyQualifiedName~MessageCatalogTests|FullyQualifiedName~BuiltinServerTextTests"` 確認是綠的；寫一個暫時測試印出出廠（預設 zh-TW）四個內建語系的 `registry.UiHash(code, "zh-TW")`，記進交棒說明後刪掉測試。
- [ ] **Step 1: 寫失敗的測試**（純記憶體；registry 照 `LocaleRegistryUiTests` 的建法，覆寫層用 Task 2 的 fake）
  - `NoOverlay_UiSameAsBuiltinMerge`：沒有任何覆寫層時，`Ui(code, d)` 的內容等於測試自己用 `UiTextMerge.Merge(BaseKeys, 鏈上各語系的 BuiltinLocaleResources.Ui)` 算出的結果（獨立的預期值，不依賴改動後的 `Ui()`）；四語 × 預設 zh-TW、th 兩種。
  - `Overlay_TakesPrecedence_ThenBuiltin`：`th` 覆寫 `menu.home` → `Ui("th")` 的 `menu.home` 是覆寫值，其他鍵是內建泰文；`uiHash` 變了。
  - `Overlay_NewLocale_ChainsToParentThenDefault`：`ja`（上層 `en-US`、預設 `th`）只覆寫 3 句 → 那 3 句是日文、其他是英文；`ja` 沒上層 → 其他是泰文（設計 §19 退回鏈那條）。
  - `Overlay_ParentOverrideFlowsDown`：`en-US` 覆寫一句、`ja` 沒那句 → `ja` 拿到覆寫後的英文。
  - `Overlay_KeysOutsideBase_Dropped`：覆寫層多一個基準沒有的鍵 → 不出現在 `Ui()`，不影響雜湊。
  - `Overlay_CorruptRow_TreatedAsEmpty`：某語系的 `UiJson` 不是合法 JSON → 那個語系沒有覆寫層、其他語系照常、記 Warning；`EnsureFreshAsync` 不丟例外。
  - `StampUnchanged_NoReload`：戳記沒變時不呼叫 `ListCurrentAsync`（fake 計數）。
  - `OverlayChange_NewUiCache_SameGeneration`：只換覆寫層 → `Generation` 不變、`Ui()` 回新內容。
  - `MessageCatalogTests.Overlay_Server_FirstThenBuiltin`：`ja` 覆寫 `Forbidden` → `Get(ja)` 是覆寫值；`ja` 沒覆寫的鍵 → 走鏈；`th` 覆寫 `report.Yes` → `Get(th, report.Yes)` 是覆寫值。
  - `MessageCatalogTests.Overlay_BadTemplate_FallsBackToBuiltin`：覆寫範本是 `{1}`（內建只有 `{0}`）、呼叫帶一個參數 → 回同語系內建層的結果、不丟例外、記 Warning。
  - `MessageCatalogTests.AsI18n_RefreshMidway_UsesOneSnapshot`（既有）照過；新增 `AsI18n_OverlaySwapMidway_OneSnapshot`：`BeforeLocaleForTests` 第一次被呼叫時換掉 `ja` 的覆寫層並刷新 → 這次 `AsI18n` 的 `ja` 是舊覆寫值。
  - `BuiltinInstance_IgnoresOverlay`：`BuiltinMessageCatalog.Instance` 不看覆寫層。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~LocaleRegistryOverlayTests|FullyQualifiedName~MessageCatalogTests"`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；另跑 ApplicationTests `--filter "FullyQualifiedName~LocaleRegistry|FullyQualifiedName~BuiltinServerTextTests|FullyQualifiedName~ServiceResultCatalogTests|FullyQualifiedName~RequestLocaleTests|FullyQualifiedName~CachedSystemPolicyTests"`、WebAPITests `--filter "FullyQualifiedName~I18nUiApiTests|FullyQualifiedName~ServerMessageChainApiTests|FullyQualifiedName~LocaleChainApiTests|FullyQualifiedName~InstalledLocalePolicyTests"`；PostgreSQL：`FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.WebAPITests --filter "Category=Postgres&(FullyQualifiedName~I18nUiApiTests|FullyQualifiedName~ServerMessageChainApiTests)"`；暫時再印一次四個內建 `uiHash`，與 Step 0 相同（記進交棒說明後刪）。
- [ ] **Step 5: Commit**（`feat(i18n): 覆寫層接進語系快照、介面文字與後端訊息`）

**驗收**：Step 4 全過；四個內建 `uiHash` 與 Step 0 相同；`MessageCatalog` 建構子仍只有 `LocaleRegistry`、`CachedSystemPolicy`（查一句不碰資料庫）。

### Task 4: 政策 A38、A39

**Files:**
- Modify: `Domain/Policies/PolicyCatalog.cs`（`LocalePackMaxBytes`、`LocaleMaxInstalled`，判斷 14；註解寫「A38／A39」與「改嚴只擋新安裝」）
- Modify: `shared/policy-catalog.json`（`codes` 加 `locale.packMaxBytes`、`locale.maxInstalled`，放在 `locale.requiredText` 之後）
- Modify: `shared/i18n/{zh-TW,en-US,th,vi}/ui.json`：`policies.items.locale.packMaxBytes.{name,help}`、`policies.items.locale.maxInstalled.{name,help}`
  - 繁中：「語系包檔案大小上限」／「匯入語系包時檔案不能超過這個大小（技術上限 20 MB）」；「已安裝語系數上限」／「含內建四種；改小不影響已安裝的語系，只擋之後的新安裝」。
- Modify: `docs/superpowers/specs/2026-11-11-policy-settings-design.md` §4 表加 A38、A39 兩列（分類地區與語系、出廠 5 MB／10、S、i4d1）
- Test: `DomainTests/Policies/PolicyCatalogFixtureTests.cs`（既有，照過）、`frontend/src/domain/dynamic/policy-i18n.vitest.spec.ts`（既有，照過）、`WebAPITests/Dynamic/PolicyApiTests.cs`（加案例）

- [ ] **Step 1: 寫失敗的測試**：`PolicyApiTests.LocalePackPolicies_Listed_WithRange`：`GET system/policies` 有兩項，範圍與出廠值照判斷 14；`PUT locale.maxInstalled` 為 3 → 400（帶出範圍數字）；為 4 → 200。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~PolicyApiTests"`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；DomainTests `--filter "FullyQualifiedName~PolicyCatalog"`；前端 `cd frontend && bunx vitest run src/domain/dynamic/policy-i18n.vitest.spec.ts src/i18n/locales.vitest.spec.ts --maxWorkers=2`。
- [ ] **Step 5: Commit**（`feat(policy): 語系包檔案大小與已安裝語系數上限（A38、A39）`）

**驗收**：Step 4 全過；`grep -n "packMaxBytes\|maxInstalled" shared/policy-catalog.json` 各一筆。

---

# i4d1-2 解析、分析與預覽

### Task 5: 語系包解析與分析（含票 #68）

**Files:**
- Create: `Application/Dynamic/LocaleService/Packs/LocalePackModels.cs`（`ParsedLocalePack`、`LocalePackReport`（判斷 13 的形狀）、`LocalePackPlan`（套用要寫的：語系欄位變動、`ui`／`server` 兩份「這次要設的鍵→值」與「這次要拿掉的鍵」（Q6 改回內建原文的））、`SectionReport`、`RejectedItem`）
- Create: `Application/Dynamic/LocaleService/Packs/LocalePackParser.cs`（判斷 1、2、5；Q2、Q3 的整包層級處理）
- Create: `Application/Dynamic/LocaleService/Packs/LocalePackAnalyzer.cs`（逐句驗證、比對、完整度、撞字（判斷 6）、警告）
- Modify: `Application/Dynamic/ImportService/FieldImportParser.cs`（`BooleanWords` 加 `public IReadOnlySet<string> Ambiguous`，不改既有行為）
- Create: `backend/tests/Fixtures/langpacks/ja-partial.langpack.json`（虛構日文 30 句介面文字＋5 句後端訊息，上層 `en-US`）、`th-fix.langpack.json`（覆寫泰文 3 句，其中 1 句和內建相同）、`bad-mixed.langpack.json`（混合合法與各種拒收原因、多鍵、空字串）、`rtl.langpack.json`
- Test: `ApplicationTests/Dynamic/LocalePacks/LocalePackParserTests.cs`、`LocalePackAnalyzerTests.cs`（新）

**Interfaces:**
```csharp
public static class LocalePackParser
{
    /// <summary>整包層級錯誤回 (null, 錯誤 MessageRef)；成功回模型（句子還沒驗）。</summary>
    public static (ParsedLocalePack? Pack, MessageRef? Error) Parse(ReadOnlySpan<byte> bytes);
}

public sealed class LocalePackAnalyzer(LocaleRegistry registry, CachedSystemPolicy policy)
{
    /// <summary>current＝該語系目前版的覆寫層（交易內讀的，沒有就 null）；其他語系的清單與覆寫層取 registry.Texts() 一份快照；maxInstalled＝A39 生效值。</summary>
    public LocalePackAnalysis Analyze(ParsedLocalePack pack, LocaleOverlay? current, int maxInstalled);
}
public sealed record LocalePackAnalysis(LocalePackReport Report, LocalePackPlan? Plan, MessageRef? Error);   // Error＝整包拒收（代碼、上層語系、A39）
```

- [ ] **Step 1: 寫失敗的測試**
  - `LocalePackParserTests`：
    - 格式：非 JSON 文字、xlsx 簽章（Q2）、BOM 開頭的合法檔、`format` 錯、`formatVersion` 2（太新）、`formatVersion` "1"（字串）、深度 17、`direction: "rtl"`（U10，訊息「本版不支援由右至左的語言」）、缺 `locale.code`、代碼 `JA` 正規化成 `ja`、代碼 `ja_JP` 拒收、`nativeName` 51 字、含 U+202E 的 `nativeName`、`quasarLang` 不在清單、`pack.version` 51 字、`note` 501 字、`builtFor` 21 字。
    - 段落：`ui` 巢狀物件被壓平；壓平後重複鍵 → 該鍵 `duplicate`；值是數字 → `notString`；`ui` 鍵數超過基準 2 倍 → 整包拒收（訊息帶出段名與上限）；三段都缺 → 合法（只改 `locale` 欄位）；不認得的頂層欄位略過。
  - `LocalePackAnalyzerTests`（registry 與 policy 照 `MessageCatalogTests` 的建法）：
    - `SharedBooleanCases`：跑 `shared/i18n-message-cases.json` 的 `boolean` 段（照案例的 `installed` 建 registry 與覆寫層），被拒收的鍵與警告數照 `expectedRejected`、`expectedWarnings`（票 #68）。
    - `NewLocale_Counts`：`ja-partial` → `isNew`、`ui.added` 30、`completeness.ui.have` 30、`total` 等於 `BaseKeys.Count`。
    - `Merge_U5`：目前覆寫 30 句、新檔另 20 句（其中 2 句與舊的相同、1 句空字串）→ `added` 18、`unchanged` 2、`skipped` 1；計畫套用後是 48 句（30＋18）。這是 U5「30＋20 句」情境的分析面。
    - `SameAsBuiltin_NotStored`（Q6）：`th-fix` 的那一句和內建相同 → `sameAsBuiltin` 1、不在計畫要設的鍵；之前覆寫過、這次改回內建原文 → 在計畫的「要拿掉的鍵」。
    - `Rejected_ReasonsAndFirst50`：`bad-mixed` 每種原因各一筆；造 60 句佔位符錯誤 → `rejected` 60、`rejectedItems` 50 筆。
    - `Extra_Dropped`：基準沒有的鍵 → `extra` 計數、`extraKeys`、不進計畫。
    - `Placeholder_UsesUnion`（Q7、判斷 4）：`approval.stagesMore` 用 `{first} {rest}` 合法；用 `{total}` → `placeholder`。
    - `Server_Placeholder_MatchesZh`：`Forbidden` 加 `{0}` → `placeholder`；`report.Yes` 覆寫含 `<script>` 與開頭 `=` → 合法（輸出時才編碼與跳脫，Task 7 驗）。
    - `Builtin_LocaleFieldsIgnored`（Q3）：`th` 包帶 `nativeName`、`fallback` → `locale.ignored` 列兩欄、計畫不改語系列。
    - `Fallback_Invalid`：上層未安裝、成環、太深 → `Error` 帶對應訊息、沒有計畫。
    - `MaxInstalled_BlocksNewOnly`（A39）：已安裝 10、上限 10 → 新語系 `Error`（訊息帶 10）；同一狀態匯入既有的 `ja` → 照常有計畫。
    - `BuiltFor_ShownNotCompared`：`builtFor` 原樣出現在 `pack.builtFor`，不產生警告（判斷 24）。
    - `DesignData_CountedNotApplied`（Q1）：帶 3 段 `designData` → `designData.count` 3、計畫不含。
    - `EmptyPack_NoChanges`：三段都缺、`locale` 沒變 → 報告全 0，計畫標 `NoChanges`（Task 7 套用時回 200、不寫新版本，只記稽核）。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~LocalePackParserTests|FullyQualifiedName~LocalePackAnalyzerTests"`
- [ ] **Step 3: 實作**（Parser 與 Analyzer 都不碰資料庫：目前覆寫層與 A39 生效值由呼叫端傳入）
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；另跑 `--filter "FullyQualifiedName~FieldImportParserTests|FullyQualifiedName~ImportPlanner"`（`BooleanWords` 行為不變）。
- [ ] **Step 5: Commit**（`feat(i18n): 語系包解析與分析，是否欄位撞字的句子拒收（#68）`）

**驗收**：Step 4 全過；`FieldImportParserTests.Boolean_Ambiguous*` 照過（匯入端守衛保留，票 #68 驗收第二條）。

> 註：`EmptyPack_NoChanges` 的處理是判斷 16 的延伸：沒有任何變更時不寫新版本（否則會把上一版擠掉），但稽核照記，讓管理員看得到這次匯入。

### Task 6: 預覽 API

**Files:**
- Create: `Application/Dynamic/LocaleService/Packs/LocalePackSettings.cs`（判斷 15）
- Create: `Application/Dynamic/LocaleService/LocalePackMessages.cs`（判斷 18）
- Modify: `Application/Common/MessageText.cs`（新鍵與四語；類別註解不動）
- Create: `ApplicationTests/Common/ServerTextBaseline/locale-pack.json`（Step 1 用新表產生後凍結）
- Modify: `ApplicationTests/Common/BuiltinServerTextTests.cs`（`BaselineFiles` 加 `locale-pack.json`）
- Create: `Application/Dynamic/LocaleService/Commands/PreviewLocalePack/{PreviewLocalePackRequest,PreviewLocalePackHandler}.cs`（`ITransactionalRequest`、`ISystemAdminRequest`，不稽核）
- Modify: `ApplicationTests/Common/Audit/AuditCoverageTests.cs`（`Exempt` 加預覽，判斷 17）
- Create: `WebAPI/Controllers/v3/Dynamic/LocalePackBodyLimitFilter.cs`（判斷 12）
- Modify: `WebAPI/Controllers/v3/Dynamic/I18nController.cs`（`POST packs/preview`）、`WebAPI/Program.cs`（註冊 `LocalePackSettings`、filter）
- Test: `WebAPITests/Dynamic/LocalePackPreviewApiTests.cs`（新，`Category=Postgres`）

新訊息（繁中；四語都要給，泰文、越南文同既有慣例待母語者校對）：`LocalePackImportDisabled`「這個環境不允許匯入語系包」、`LocalePackTooLarge`「語系包不能超過 {0} MB」、`LocalePackNotJson`「檔案不是語系包（.langpack.json）」、`LocalePackExcelNotYet`「本版只收 .langpack.json，Excel 翻譯樣板之後支援」、`LocalePackFormatUnknown`「不是 Formwright 語系包」、`LocalePackFormatTooNew`「這份語系包的格式版本 {0} 比系統支援的 {1} 新」、`LocalePackTooDeep`「語系包的 JSON 層數太多（最多 {0} 層）」、`LocalePackTooManyKeys`「{0} 段有 {1} 句，超過上限 {2} 句」、`LocalePackRtl`「本版不支援由右至左的語言」、`LocalePackCodeInvalid`「語系代碼「{0}」格式不對（例：ja、zh-Hant-HK）」、`LocalePackNativeNameRequired`「新語系要填自稱（locale.nativeName）」、`LocalePackFieldInvalid`「{0} 不能超過 {1} 字，也不能含控制字元」、`LocalePackQuasarUnknown`「找不到日期與表格元件的語言包「{0}」」、`LocaleFallbackNotInstalled`「上層語系「{0}」還沒安裝」、`LocaleFallbackSelf`「上層語系不能是自己」、`LocaleFallbackCycle`「上層語系形成循環：{0}」、`LocaleFallbackTooDeep`「上層語系最多 {0} 層」、`LocaleMaxInstalled`「最多只能安裝 {0} 個語系（含內建）」、`LocalePackPreviewExpired`「預覽已過期，請重新上傳預覽」、`LocalePackStale`「語系在預覽之後被變更了，請重新上傳預覽」、`LocaleNotFound`「找不到這個語系」、`LocaleBuiltinNotRemovable`「內建語系不能移除，只能停用」、`LocaleInUse`「請先在政策設定停用這個語系」、`LocaleIsFallback`「請先改掉{0}的上層語系」、`LocaleNoPrevious`「沒有上一版可以回復」、`LocaleNoOverrides`「這個語系沒有覆寫可以移除」。（Task 7、8 用到的也一次加，基準檔只建一次。）

- [ ] **Step 1: 寫失敗的測試**
  - 先加 `MessageText` 新鍵，寫暫時的 `Dump` 測試照 i4c 判斷 14 的形狀產生 `locale-pack.json`（只含新鍵）、人工核對幾句含 `{0}` 的、刪 `Dump`，`BaselineFiles` 加檔名。
  - `LocalePackPreviewApiTests`（`TestApp`；檔案用 `backend/tests/Fixtures/langpacks/`）：
    - `NonAdmin_403`：一般使用者 403；未登入 401。
    - `Preview_NewLocale_ReportAndStored`：`ja-partial` → 200、`previewId` 非 null、`expiresUtc`＝現在＋10 分鐘、`locale.isNew`、`ui.added` 30；資料庫有一筆預覽列、`UserId` 是 admin；`Dynamic_Locale`、`Dynamic_LocalePack` 沒變（預覽不寫入，設計 §6.3）。
    - `Preview_Bad_400`：非 JSON、xlsx、rtl、`formatVersion` 2 → 400，`errors.file` 的文字隨 `X-Locale` 換語言（en-US 一例）。
    - `Preview_TooLarge`：A38 設成 2048 → 3 KB 的檔回 400「語系包不能超過 0.002 MB」這類帶數字的訊息（數字格式：位元組換算 MB、最多三位小數，`LocalePackMessages.TooLarge(long bytes)` 統一處理）；`Content-Length` 超過 2 倍 A38 → 413、處理器沒有被呼叫。
    - `Preview_DemoOff_NotStored`：`TestApp.Settings["Localization:PackImportEnabled"] = "false"` → 200、報告完整、`previewId` 為 null、`importEnabled` false；資料庫沒有預覽列。另驗只設 `Demo:LoginEnabled=true`（沒設開關）時同樣不暫存（判斷 15 的推導）。
    - `Preview_Cap5_OldestDropped`：連續預覽 6 次 → 預覽表 5 筆，第一筆的 `previewId` 不在了。
    - `Preview_Expired_Pruned`：用 `TestApp.Clock`／`TimeProvider` 往後 11 分鐘再預覽一次 → 舊的那筆被刪。
    - `Preview_EmptySystem_Works`：沒開任何應用程式的全新 `TestApp` 照樣能預覽（空系統，鐵則 4）。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~LocalePackPreviewApiTests"`
- [ ] **Step 3: 實作**：handler 在交易內：讀 A38、A39 生效值（`IPolicyReader`）→ 大小比對 → `LocalePackParser.Parse` → 讀目前覆寫層（`ILocalePackRepository.CurrentAsync`）與該語系列的 `ModifiedUtc` → `LocalePackAnalyzer.Analyze` → 開關開著且有計畫時 `PrunePreviewsAsync(now, keep: LocalePackLimits.MaxPreviews - 1)`、`AddPreview`（存 `PlanJson`、`ReportJson`、語系版本兩欄）→ 回報告。`fileName` 只取最後一段（去掉路徑）、截到 200 字、過 `LocaleTextSafety`（不合格就存空字串）。
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；ApplicationTests `--filter "FullyQualifiedName~BuiltinServerTextTests|FullyQualifiedName~AuditCoverageTests|FullyQualifiedName~MessageTextTests"`；PostgreSQL：`FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.WebAPITests --filter "Category=Postgres&FullyQualifiedName~LocalePackPreviewApiTests"`。
- [ ] **Step 5: Commit**（`feat(i18n): 語系包預覽 API（暫存 10 分鐘、最多 5 筆、Demo 不暫存）`）

**驗收**：Step 4 全過（含 PostgreSQL）；`git diff main -- backend/tests/DynamicModules.ApplicationTests/Common/ServerTextBaseline/{message-report,task-report,alert}.json` 無變化。

---

# i4d1-3 寫入

### Task 7: 套用 API（409、逐句合併、新安裝、稽核）

**Files:**
- Create: `Application/Dynamic/LocaleService/Commands/ApplyLocalePack/{ApplyLocalePackRequest,ApplyLocalePackHandler}.cs`（`ITransactionalRequest`、`ISystemAdminRequest`、`IAuditedRequest`，`AuditAction.For("locale.pack.import", AuditTargets.Locale, null)`；判斷 16、17）
- Modify: `Application/Common/Audit/AuditActions.cs`（分組 `Locale = "locale"` 加進 `Groups`；五個動作）、`IAuditedRequest.cs`（`AuditTargets.Locale`）、`AuditContext.cs`（`Action(string code)`、`ActionCode`；`Reset` 清掉）、`Application/Common/Behaviors/AuditBehavior.cs`（用 `context.ActionCode ?? action.Code`；沒登記丟 `InvalidOperationException`）
- Modify: `shared/i18n/{zh-TW,en-US,th,vi}/ui.json`：`audit.groups.locale`（「語系」）、`audit.actions.locale.{install,remove}`、`audit.actions.locale.pack.{import,rollback,clear}`（「安裝語系」「移除語系」「匯入語系包」「語系回到上一版」「移除語系覆寫」）
- Modify: `WebAPI/Controllers/v3/Dynamic/I18nController.cs`（`POST packs/apply`）
- Modify: `WebAPITests/LocaleTestKit.cs`（`ApplyPackAsync`，判斷 21）
- Test: `WebAPITests/Dynamic/LocalePackApplyApiTests.cs`（新，`Category=Postgres`）、`ApplicationTests/Common/Audit/AuditCoverageTests.cs`（加 `ActionOverride_MustBeRegistered`）

- [ ] **Step 1: 寫失敗的測試**（`LocalePackApplyApiTests`）
  - `Apply_NewLocale_Installed_NotEnabled`（U2）：預覽＋套用 `ja-partial` → 200；`Dynamic_Locale` 有 `ja`（`installed`、上層 `en-US`）；`GET policies/effective` 的已安裝語系含 `ja`、`locale.enabled` 不含；匿名 `GET i18n/ui/ja` 404（未啟用）；admin `GET i18n/ui/ja` 的那 30 句是日文、其他是英文（經 `LocaleTestKit` 等快照）。
  - `Apply_ServerOverride_ReachesResponses`：`ja` 啟用後（走政策 API）、帶 `X-Locale: ja` 取不存在的模組 → `message` 是包裡的日文 `ModuleNotFound`。
  - `Apply_ThaiOverride_U1`：`th-fix` → `GET i18n/ui/th` 的那 2 句是新值，`uiHash` 變了；那句與內建相同的沒存（`Dynamic_LocalePack` 的 `UiJson` 只有 2 鍵，Q6）。
  - `Merge_30Plus20_U5`：先套 30 句、再套另 20 句 → 50 句都生效；`Dynamic_LocalePack` 兩列，上一版是 30 句那份。
  - `Concurrent_SecondGets409`：admin 與另一個系統管理員各自預覽 `ja` → 前者套用 200、後者套用 409（`LocalePackStale`）；後者重新預覽後可以套用。
  - `OtherLocale_NotAffected`：預覽 `th` 之後別人套用 `ja` → `th` 照常套用（判斷 16 只比該語系）。
  - `Preview_OtherUser_404`、`Preview_Expired_404`（時間往後 11 分鐘）、`FileHash_Mismatch_409`、`Preview_Consumed`（同一個 `previewId` 套用第二次 404）。
  - `MaxInstalled_RecheckedAtApply`：A39＝5、預覽 `ja` 時只裝了 4 種 → 預覽通過；套用前別人裝了 `ko` → 套用 400 帶「最多只能安裝 5 個語系」。
  - `AllOrNothing`：用 `TestApp.ExtraServices` 把 `ILocalePackRepository` 換成在 `AddVersion` 丟例外的包裝 → 500；`Dynamic_Locale` 沒有 `ja`、`Dynamic_LocalePack` 沒有新列、預覽列還在、稽核沒有列（設計 §19 匯入交易：一次全成功或全不動）。
  - `DemoOff_403`：開關關閉時套用 403（`LocalePackImportDisabled`）；資料庫沒變。
  - `Audit_InstallThenImport`：第一次套用 `ja` 的稽核動作是 `locale.install`、第二次是 `locale.pack.import`；摘要有檔名、雜湊、各段筆數，沒有任何句子內容（搜摘要不含包裡的日文）。
  - `XssAndFormula_Escaped`（設計 §13.3、§19 安全）：`th` 覆寫 `report.Yes` 為 `<script>x</script>`、`report.No` 為 `=1+1` → 列印 HTML 裡是編碼後的 `&lt;script&gt;`；CSV 匯出的是否欄被公式跳脫（照 `CsvWriter.EscapeFormula` 的既有斷言寫法）。
  - `NoChanges_NoNewVersion`：同一個檔套用兩次（第二次重新預覽）→ 第二次 200、`Dynamic_LocalePack` 仍是原本那兩列（上一版沒被擠掉）、稽核有第二列。
  - `ActionOverride_MustBeRegistered`（ApplicationTests）：`AuditContext.Action("locale.nope")` 之後 `AuditBehavior` 丟 `InvalidOperationException`。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~LocalePackApplyApiTests"`；ApplicationTests `--filter "FullyQualifiedName~AuditCoverageTests"`
- [ ] **Step 3: 實作**（判斷 16 的順序；`registry.Invalidate` 登記在 `AfterCommitActions`，回滾就不執行）
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；另跑 WebAPITests `--filter "FullyQualifiedName~Audit|FullyQualifiedName~I18nUiApiTests|FullyQualifiedName~InstalledLocalePolicyTests|FullyQualifiedName~ExportApi|FullyQualifiedName~Print"`；前端 `bunx vitest run src/i18n/locales.vitest.spec.ts src/pages/system/AuditPage.vitest.spec.ts --maxWorkers=2`；PostgreSQL：`FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.WebAPITests --filter "Category=Postgres&FullyQualifiedName~LocalePackApplyApiTests"`。
- [ ] **Step 5: Commit**（`feat(i18n): 套用語系包（預覽後被改回 409、逐句合併、安裝與稽核）`）

**驗收**：Step 4 全過（含 PostgreSQL）；`AuditCoverageTests` 全過（新請求都有宣告或白名單理由）。

### Task 8: 回到上一版、移除覆寫、移除語系、語系清單

**Files:**
- Create: `Application/Dynamic/LocaleService/Commands/RollbackLocalePack/…`（`locale.pack.rollback`）、`ClearLocaleOverrides/…`（`locale.pack.clear`）、`RemoveLocale/…`（`locale.remove`）（判斷 19、20；三者都 `ITransactionalRequest`、`ISystemAdminRequest`、`IAuditedRequest`，開關關閉 403，提交後 `Invalidate`）
- Create: `Application/Dynamic/LocaleService/Queries/GetLocales/{GetLocalesRequest,GetLocalesHandler}.cs`（`ISystemAdminRequest`，唯讀）
- Modify: `WebAPI/Controllers/v3/Dynamic/I18nController.cs`（`GET locales`、`POST locales/{code}/rollback`、`POST locales/{code}/clear`、`DELETE locales/{code}`）
- Test: `WebAPITests/Dynamic/LocaleManageApiTests.cs`（新，`Category=Postgres`）

清單回應：`{ importEnabled, packMaxBytes, maxInstalled, locales: [{ code, nativeName, origin, fallbackCode, enabled, isDefault, required, overrides: { ui, server }, current: { packVersion, fileName, importedUtc, importedBy } | null, hasPrevious }] }`，順序同 `registry.Locales`（內建在前、匯入依安裝時間）；`overrides` 是目前版的鍵數。

- [ ] **Step 1: 寫失敗的測試**
  - `List_Factory_FourBuiltin`：出廠 → 四列、`origin` 都是 `builtin`、`overrides` 都是 0、`current` null；一般使用者 403。
  - `List_AfterImport`：套用 `ja-partial` 後有第五列，`overrides.ui` 30、`current.fileName`、`hasPrevious` false。
  - `Rollback_AndBack`：套兩次 → `rollback` 後 `GET i18n/ui/ja` 回到第一次的內容（經 `LocaleTestKit` 等快照）；再 `rollback` 一次又回到第二次（設計 §6.4 可以再切回來）；沒有上一版 409。
  - `Clear_BuiltinBackToFactory_AndUndo`（Q5）：`th-fix` 後 `clear` → `GET i18n/ui/th` 等於內建泰文（與 `NoOverlay_UiSameAsBuiltinMerge` 同一個預期值算法）；`rollback` → 覆寫回來；沒有覆寫時 `clear` 409。
  - `Remove_Installed`：未啟用的 `ja` → 204；`Dynamic_Locale`、`Dynamic_LocalePack`、預覽列都沒有 `ja`；某欄位名稱裡的 `ja` 文字還在（設計 §4.5），且這個欄位照常可以改名（M1）。
  - `Remove_Blocked`：內建 `th` 409（`LocaleBuiltinNotRemovable`）；`ja` 啟用中 409（`LocaleInUse`）；`ko` 的上層是 `ja` 時移除 `ja` 409、訊息含 `ko` 的自稱（`LocaleIsFallback`）；不存在 404。
  - `Remove_AlertChannelLocale_NotBlocked`（Q9）：告警通道信件語系是 `ja` → 照常移除。
  - `Reinstall_OldTextReappears`：移除 `ja` 後再套用 `ja` → 那個欄位的日文名稱又出現在 `X-Locale: ja` 的回應裡。
  - `DemoOff_403_AllThree`：開關關閉時三個寫入都 403、清單的 `importEnabled` false。
  - `Audit_EachAction`：三種動作各一列稽核，目標是語系代碼與自稱。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~LocaleManageApiTests"`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；另跑 `--filter "FullyQualifiedName~LocalePack|FullyQualifiedName~InstalledLocalePolicyTests|FullyQualifiedName~LocaleChainApiTests|FullyQualifiedName~EndpointRoutesTests"`；PostgreSQL：`FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.WebAPITests --filter "Category=Postgres&(FullyQualifiedName~LocaleManageApiTests|FullyQualifiedName~LocalePackApplyApiTests|FullyQualifiedName~LocalePackPreviewApiTests)"`、ApplicationTests `--filter "Category=Postgres&FullyQualifiedName~LocalePack"`。
- [ ] **Step 5: Commit**（`feat(i18n): 語系清單、回到上一版、移除覆寫與移除語系`）

**驗收**：Step 4 全過（含 PostgreSQL）；`EndpointRoutesTests` 若列舉端點，把新路由加進去並確認都要求系統管理員。

---

# i4d1-4 畫面與收尾

### Task 9: 前端語法檢查與共用案例

**Files:**
- Create: `frontend/src/i18n/message-check.ts`（判斷 22）
- Test: `frontend/src/i18n/message-check.vitest.spec.ts`（新，讀 `shared/i18n-message-cases.json` 的 `ui` 段）、`frontend/src/i18n/locales.vitest.spec.ts`（加「內建每句都通過 `checkUiMessage`」）

- [ ] **Step 1: 寫失敗的測試**：`ui` 段每案 `checkUiMessage(text, new Set(allowed))` 等於 `expected`（與後端 `LocaleMessageCasesTests` 同一份案例）；另加「合法的案例 `baseCompile` 不報錯」；`locales.vitest.spec.ts`：四份內建檔每句 `checkUiMessage(v, 該鍵四語聯集)` 為 null。
- [ ] **Step 2: 跑測試確認失敗**：`cd frontend && bunx vitest run src/i18n/message-check.vitest.spec.ts src/i18n/locales.vitest.spec.ts --maxWorkers=2`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；`bun run typecheck`。
- [ ] **Step 5: Commit**（`test(i18n): 前端介面文字語法檢查跑共用案例`）

**驗收**：Step 4 全過；`shared/i18n-message-cases.json` 前後端各有一個測試在讀（`grep -rln "i18n-message-cases.json" backend/tests frontend/src` 兩筆）。

### Task 10: 最小語系頁

**Files:**
- Create: `frontend/src/pages/system/LocalesPage.vue`、`frontend/src/presentation/locale/LocalePackImportDialog.vue`、`LocalePackReport.vue`（判斷 23；Q8）
- Create: `frontend/src/application/dynamic/queries/locale-packs-queries.ts`（`useLocales…` 名稱避開既有的 `useLocales()`：`useInstalledLocaleAdmin`、`usePreviewLocalePack`、`useApplyLocalePack`、`useRollbackLocalePack`、`useClearLocaleOverrides`、`useRemoveLocale`）
- Modify: `frontend/src/services/api/dynamic-api.ts`（六支 API 與型別）、`frontend/src/router/routes.ts`（`system/locales`）、`frontend/src/domain/dynamic/menu-nav.ts`（`systemLinks`）
- Modify: `shared/i18n/{zh-TW,en-US,th,vi}/ui.json`：`menu.locales`（「語系」）、`localePacks.*`（頁面、對話框、報告、原因、警告、確認框、錯誤）
- Test: `frontend/src/pages/system/LocalesPage.vitest.spec.ts`、`frontend/src/presentation/locale/LocalePackImportDialog.vitest.spec.ts`（新）；`menu-nav.vitest.spec.ts`、`router/routes.vitest.spec.ts`（加 `/system/locales`）

畫面重點（繁中文案；四語都要給）：
- 頁首「語系」，說明一行「介面文字是公開的（登入頁也看得到），不要放機密。」（設計 §11）；右上「匯入語系包」。開關關閉時頁首下方提示「這個環境不允許匯入語系包，只能預覽。」
- 列：自稱（粗體）、代碼、「內建」或「匯入」、「啟用中」「預設」標籤（未啟用不標）、「覆寫 介面 N 句、系統訊息 M 句」、「目前版本 1.2.0，2026-11-21 10:00 +08:00 匯入」（沒有覆寫就不顯示這行）；列尾「⋯」：回到上一版（`hasPrevious` 才有）、移除覆寫（有覆寫才有）、移除語系（只有匯入的才有）。開關關閉時三項停用並在選單裡附一句原因。
- 確認框：回到上一版「回到上一版只換介面文字與系統訊息；可以再按一次切回來。」；移除覆寫「〇〇的覆寫會清空、回到出廠翻譯（匯入的語系則全部退回上層或預設語系）。可以用「回到上一版」復原。」；移除語系照設計 §4.5「介面翻譯會刪除；模組、欄位等名稱裡的〇〇文字會保留，重新安裝後會再出現」。
- 匯入對話框：選檔 → 呼叫預覽（顯示載入中）→ 報告：語系（新安裝或更新、`changes`、`ignored`）、每段新增／變更／不變／略過／與內建相同／拒收／多餘的計數、完整度（「介面 1790／2701（66%）」）、拒收清單（鍵＋原因文字，最多 50 筆並註明總數）、警告、設計資料略過說明（Q1）。按鈕「套用」（`previewId` 為 null 時停用並顯示開關說明）、「取消」。套用 409 → 顯示後端訊息與「重新預覽」（用對話框裡保留的同一個檔再預覽一次）。成功 → 「已匯入」；新語系未啟用時顯示「現在啟用」（`locale.enabled` 政策加上這個代碼；失敗顯示後端訊息）。
- 檔案大小：送出前比對清單回應的 `packMaxBytes`，超過就不送、直接顯示「語系包不能超過 N MB」（`frontend.md`：送出一定會被後端擋時才先擋）；413 也顯示同一句。
- 版型：清單用 `q-list`＋`q-item`，390 寬時文字換行、動作仍在列尾選單；對話框 `max-width: 720px`、手機全寬；報告的計數用定義清單（`dl`），不用表格，避免 390 寬橫捲。

- [ ] **Step 1: 寫失敗的測試**
  - `LocalesPage.vitest.spec.ts`（mock API，照 `AuditPage.vitest.spec.ts` 的寫法）：列出四列＋一列匯入；內建列的「⋯」沒有「移除語系」；`importEnabled: false` 時顯示提示、三個動作停用；移除語系的確認框文字含語系自稱；回到上一版成功後重取清單。
  - `LocalePackImportDialog.vitest.spec.ts`：選檔後送出的本文就是檔案位元組、帶 `fileName`；報告的計數、完整度百分比、拒收原因文字（`localePacks.reasons.placeholder` 等）正確；`previewId` null 時「套用」停用；409 顯示「重新預覽」且按下後再呼叫一次預覽；成功且新語系未啟用時有「現在啟用」，按下送 `PUT system/policies/locale.enabled`、值是原啟用清單加新代碼；超過 `packMaxBytes` 不呼叫預覽。
  - `menu-nav.vitest.spec.ts`：系統管理員的連結在 `/system/policies` 之後多 `/system/locales`；一般設計者沒有。
  - `routes.vitest.spec.ts`：`/system/locales` 開啟 `LocalesPage`。
- [ ] **Step 2: 跑測試確認失敗**：`cd frontend && bunx vitest run src/pages/system/LocalesPage.vitest.spec.ts src/presentation/locale src/domain/dynamic/menu-nav.vitest.spec.ts src/router/routes.vitest.spec.ts --maxWorkers=2`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；加跑 `src/i18n/locales.vitest.spec.ts src/css/themes/no-hardcoded-colors.vitest.spec.ts src/i18n/table-no-data-label.vitest.spec.ts src/domain/dynamic/policy-i18n.vitest.spec.ts`；`bun run typecheck`。
- [ ] **Step 5: 本機走查**（`Localization__PackImportEnabled=true bash scripts/unattended/local-demo.sh <worktree 絕對路徑>`，背景執行等 READY；`formwright-demo-verify` skill）：admin 登入 → 系統管理 → 語系 → 匯入 `ja-partial` → 看報告 → 套用 → 「現在啟用」→ 語言選單出現「日本語」、切過去那 30 句是日文其他是英文 → 回到上一版、移除覆寫、移除（先在政策停用）各一次；匯入 `bad-mixed` 看拒收清單；匯入 `rtl` 看 400 訊息。390、820、1366、1920 四種寬度截圖（放專案 `.tmp/`），沒有橫向捲動。再用預設的 `local-demo.sh`（不設開關，Demo 模式）重跑一次：上傳後有報告、「套用」停用並顯示說明、列上三個動作停用。
- [ ] **Step 6: Commit**（`feat(i18n): 系統管理的語系頁（清單、匯入預覽與套用）`）

**驗收**：Step 4 全過；Step 5 逐項記錄（含截圖路徑）；`grep -n "#[0-9a-fA-F]\{3,6\}" frontend/src/pages/system/LocalesPage.vue frontend/src/presentation/locale/LocalePack*.vue` 無結果。

### Task 11: 文件、手冊、票、gate

**Files:**
- Modify: `docs/superpowers/specs/2026-11-17-language-packs-design.md`：
  - §6.3：補一行「實作：預覽暫存在資料表 `Dynamic_LocalePackPreview`，和 `Dynamic_LocalePack` 同一個 migration；409 比對該語系的語系列修改時間與目前版（i4d1 計畫判斷 8、16）」。
  - §5.2：佔位符那條補「介面文字以內建四語在該鍵用到的名稱聯集為準，少用不擋（i4d1 Q7）」；§6.2 補「與內建相同的句子不存成覆寫（Q6）」；§6.5 補「移除覆寫是新增一個空版本，可回到上一版復原（Q5）」。（Q 若被使用者推翻，照裁決改。）
  - §14：本計畫「寫死清單」的新項目；A38、A39 標實際編號。
  - §15：加「i4d1 交付時的已知限制」（下面「已知限制」節）；「匯入是否欄位…源頭的擋法在 i4d1 的語系包驗證器（backlog 票）」改成「i4d1 起語系包驗證器拒收撞字的句子（#68）」。
  - §20：列表加「**i4d1 計畫**：[i4d1 計畫](../plans/2026-11-17-language-packs-i4d1.md)」（規劃者已在同一 commit 加過就只補「已上線」）。
- Modify: `docs/manual.md`：§20 第一段「介面有四種語言」改成「出廠有四種語言…系統管理員可以匯入語系包新增語言或修改翻譯（見〇〇）」；「某個語言缺的文案會顯示繁中」改成依退回鏈；政策設定的「三個語系政策（…只能從平台已有翻譯的四種語系挑）」改成「從已安裝的語系挑」；新增一節「系統管理：語系」（清單、匯入預覽與套用、拒收原因、回到上一版、移除覆寫、移除語系、Demo 站只能預覽、介面文字是公開的），文案照畫面實際文字；政策清單補 A38、A39。
- Modify: `docs/deploy.md`：環境變數表加 `Localization__PackImportEnabled`（沒設時 `Demo__LoginEnabled` 為 `true` 就關閉，否則開放）。
- Modify: `docs/roadmap.md` i4 列（「i4d1 已上線 <sha>」在合併後由主控補）；`docs/feature.md:294` 補「i4d1 已實作（語系包匯入、覆寫與移除，最小語系頁）」，**不改核取狀態**；跑 lifecycle 的 `feature_checklist.py docs/feature.md --write`（找不到腳本就照 `.claude/rules/docs.md` 回報「統計行待重算」）。
- 票：#68 留言「i4d1 Task 5：撞字的句子拒收（Q4）」，合併後關票並留 commit SHA；本段審查與實測發現的新待辦用 `tickets:ticket-create` 開 backlog 票。

- [ ] **Step 1: 整段驗收 grep**（下面 S1–S6）全符合。
- [ ] **Step 2: 文件回填**（上列）。
- [ ] **Step 3: gate**：推分支前外洩掃描；`bash scripts/unattended/ci-wait.sh <sha>`（含 PostgreSQL 兩步），`exit=0`。
- [ ] **Step 4: `formwright-purpose-check`**（重點：A38、A39 是政策不是常數；語系清單與覆寫層的單一來源（`Dynamic_Locale`、`Dynamic_LocalePack`，前端從 API 取）；空系統可匯入；權限 fail closed；驗證在後端）。
- [ ] **Step 5: Commit**（`docs(i18n): i4d1 完成，設計、手冊、部署與功能清單回填`），之後照 `formwright-ship` fast-forward 推 main。

**驗收**：S1–S6 全符合；gate `exit=0`；宗旨檢查無違反。Demo 資料：**種子不改**（Q10）；Demo 可示範的路徑是「語系頁 → 上傳 → 報告 → 套用被擋（這個環境不允許匯入語系包）」，成功路徑在本機走查示範，交付說明寫明。

---

## 整段驗收

設計 §20 i4d1 列逐項：

| # | 項目 | Task | 驗證 |
|---|---|---|---|
| 1 | 語系包格式 v1 | 5 | `LocalePackParserTests` |
| 2 | 驗證器與共用案例檔（含 #68） | 1、5、9 | `LocaleMessageCasesTests`、`message-check.vitest.spec.ts`、`SharedBooleanCases` |
| 3 | 預覽／套用（併發 409、暫存上限） | 6、7 | `LocalePackPreviewApiTests`、`LocalePackApplyApiTests` |
| 4 | 覆寫層版本表 migration 與回滾 | 2、8 | `LocalePackRepositoryTests`、`Rollback_AndBack` |
| 5 | 移除覆寫 | 8 | `Clear_BuiltinBackToFactory_AndUndo` |
| 6 | 安裝與移除語系 | 7、8 | `Apply_NewLocale_Installed_NotEnabled`、`Remove_*` |
| 7 | 稽核 | 7、8 | `Audit_*` |
| 8 | Demo 開關 | 6–8 | `Preview_DemoOff_NotStored`、`DemoOff_403*` |
| 9 | A38、A39 | 4、6、7 | `PolicyApiTests`、`Preview_TooLarge`、`MaxInstalled_RecheckedAtApply` |
| 10 | 最小匯入畫面 | 10 | vitest、走查四種寬度 |

```bash
# S1 Domain 規則不碰 IO
grep -rn "System.IO\|EntityFrameworkCore" backend/src/DynamicModules.Domain/Localization/   # 無結果
# S2 只有一組 migration（SQLite、PostgreSQL 各一個同名）
git diff --name-only main...HEAD -- 'backend/src/*/Migrations/*.cs' | grep -v ModelSnapshot | sed 's/.*_//' | sort -u   # 只有 LocalePacks.cs、LocalePacks.Designer.cs
# S3 新的寫入請求都要求系統管理員
grep -rLn "ISystemAdminRequest" backend/src/DynamicModules.Application/Dynamic/LocaleService/Commands/*/*Request.cs   # 無結果
# S4 上限不寫死在 handler（A38、A39 都經政策讀）
grep -rn "5242880\|20971520\|FromMinutes(10)\|keep: 5" backend/src/DynamicModules.Application/Dynamic/LocaleService   # 無結果（暫存期限與筆數只在 LocalePackLimits）
# S5 既有範本基準沒被改
git diff --stat main...HEAD -- backend/tests/DynamicModules.ApplicationTests/Common/ServerTextBaseline/message-report.json backend/tests/DynamicModules.ApplicationTests/Common/ServerTextBaseline/task-report.json backend/tests/DynamicModules.ApplicationTests/Common/ServerTextBaseline/alert.json   # 無輸出
# S6 前端沒有 v-html、html: true 的 Notify（設計 §13.3）
grep -rn "v-html\|html: true" frontend/src   # 無結果
```

## 預估規模

| 批 | Task | 新檔 | 改檔 | 手寫行數（估，含測試） |
|---|---|---|---|---|
| i4d1-1 | 1 驗證規則與共用案例 | 8 | 2 | ≈ 520（測試與案例約 300） |
| | 2 版本表與 migration | 6（不含產生的 migration） | 5 | ≈ 430（測試約 200） |
| | 3 覆寫層接進快照與訊息 | 1 | 4 | ≈ 450（測試約 270） |
| | 4 政策 A38、A39 | 0 | 8 | ≈ 150（含四語文案） |
| | 小計 | 15 | 19 | ≈ 1550 |
| i4d1-2 | 5 解析與分析（#68） | 9（含 4 個測試檔） | 1 | ≈ 780（測試約 430） |
| | 6 預覽 API | 7 | 6 | ≈ 470（測試約 220；另有約 110 行四語訊息表） |
| | 小計 | 16 | 7 | ≈ 1250 |
| i4d1-3 | 7 套用 API | 3 | 8 | ≈ 620（測試約 360） |
| | 8 管理與清單 API | 9 | 1 | ≈ 530（測試約 290） |
| | 小計 | 12 | 9 | ≈ 1150 |
| i4d1-4 | 9 前端語法檢查 | 2 | 1 | ≈ 140（測試約 70） |
| | 10 最小語系頁 | 6 | 8 | ≈ 900（測試約 330；另有約 280 行四語文案） |
| | 11 文件與收尾 | 0 | 7 | ≈ 210 |
| | 小計 | 8 | 16 | ≈ 1250 |
| 合計 | | 51 | 約 51 | ≈ 5200，其中測試約 50% |

每批都超過單批約 800 行的指引，主因是測試（每條寫入路徑都要 SQLite／PostgreSQL 雙跑、409 與全有或全無的案例）與四語文案。產生的 migration、測試用語系包檔不計入。Task 5、Task 10 若超過 600 行就拆兩個 commit（Parser／Analyzer；清單頁／匯入對話框）。

## 已知限制（i4d1，交付時寫回設計 §15）

- 本段不套用 `designData`（i4e）、不收 Excel（i4e）、沒有匯出樣板與完整度細項、沒有「以此語系預覽」捷徑（i4d2）；Demo 訪客手上沒有語系包檔時只能看到空的匯入流程（Q10）。
- 匯入後其他實例最多 60 秒才看到新翻譯（設計 §15 已列）；同一台的已開頁面靠 i4b 的 60 秒生效政策重取或套用後的查詢失效換上新文字。
- 預覽暫存全系統最多 5 筆：多位管理員同時預覽時，最舊那位的預覽會被擠掉，套用時看到「預覽已過期，請重新上傳預覽」。
- 平台升級改了某句後端訊息的參數，覆寫層的舊範本執行期格式錯誤時改用內建翻譯並記 Warning（判斷 11）；語系頁還看不到是哪幾句（i4d2 的「原文已更新」會列出）。
- 移除語系不檢查告警通道的信件語言，移除後信件改用退回鏈（Q9）。
- 內建語系的 `locale` 段（自稱、上層語系、日期元件語言包）不能經語系包改（Q3）。
- 佔位符少用不擋（Q7）：翻譯漏掉數字或名稱的句子要靠母語者看畫面發現。

## 自我檢查

1. **設計覆蓋（§20 i4d1 列）**：見「整段驗收」表 10 項；§4.5 → Task 8；§6.2 U5 → Task 5、7；§6.4 → Task 2、8；§11 → Task 7、8；§13.1 → 判斷 2、5；§13.2 → Task 1、9；§13.3 `<script>` 覆寫 → Task 7 `XssAndFormula_Escaped`；§13.4 → 判斷 15；§19 語系包驗證、匯入交易、合併與回滾、安全、稽核 → Task 1、5–8；§22 `Dynamic_LocalePack` 欄位、`previewId` 存放 → 判斷 7、8。
2. **i4c 留給本段的**：覆寫層插在內建層前面 → Task 3；`report.Yes` 含 `<script>` 的安全驗證 → Task 7；#68 → Task 5。
3. **型別一致**：`LocaleVersionStamp(Count, MaxModifiedUtc, PackCount, PackMaxModifiedUtc)`、`ILocalePackRepository`、`LocaleOverlay`、`LocaleTexts`、`LocaleRegistry.Texts()`、`MessageCatalog.Lookup(LocaleContext, string, Func<string, string, string?>?)`、`BuiltinLocaleResources.UiPlaceholderNames`、`UiMessageSyntax.Check`、`ServerTemplateSyntax.Check`、`LocaleTextSafety.Check`、`LocaleGraphRules.Check`、`LocalePackReasons`、`LocalePackLimits`、`LocalePackParser.Parse`、`LocalePackAnalyzer.Analyze`、`LocalePackAnalysis`、`LocalePackPlan`、`LocalePackReport`、`LocalePackSettings(bool ImportEnabled)`、`BooleanWords.Ambiguous`、`AuditContext.Action(string)`／`ActionCode`、`AuditTargets.Locale`、`PolicyCatalog.{LocalePackMaxBytes,LocaleMaxInstalled}`、`LocaleTestKit.ApplyPackAsync`、前端 `checkUiMessage`。
4. **前段審查踩過的坑**：逐字基準在修改前固定（Task 3 Step 0 的 `uiHash`、Task 6 新基準檔只放新鍵、S5）；新案例進 PostgreSQL 雙跑集（Task 2、6–8）；套用後等快照反映（判斷 21）；前端 i18n 鍵四語同步並跑 locales、themes 守衛（Task 4、7、10）；local-demo.sh 給絕對路徑（Task 10 Step 5）。

## 使用者裁決（沿用，不再問）

- 2026-11-17：U1（內建四語可覆寫）、U2（匯入不自動啟用，另有「現在啟用」）、U5（同語系再匯入逐句合併）、U7（只限系統管理員）、U10（由右至左整包拒收）；T4–T7、T15、T16、T18 照設計。
- 2026-11-18：M2、M3（前段已實作，本段不動）。
- 2026-11-20：業務政策不寫死、權限 fail closed、驗證不只做在前端；政策調嚴不溯及既有、超過上限的訊息帶數字；HighRisk 只留權限類；清單在手機寬度改卡片或不橫捲的列表；系統自記的時間照營運時區並標示時區。
- 2026-11-21：i4c Q2（匯入是否欄位接受已安裝語系的是／否文字，撞字當無法判斷）；源頭擋法在本段（#68）。
