# 語系包 i4a Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 語系清單從寫死的四語變成資料：新表 `Dynamic_Locale`（啟動時同步內建四語）、`LocaleRegistry` 快照（版本戳記）、三個語系政策的選項改讀已安裝語系、多語文字只驗代碼格式與必填語系（M1）、退回鏈加上上層語系並涵蓋全部已安裝語系（U3）、`auth/options` 與生效政策回語系資訊、前端拿掉所有寫死的四語清單、Quasar 語言包全開。使用者看得到的行為：**無變化（仍是四語）**。

**Architecture:** 內建四語的清單（代碼、自稱、Quasar 語言包）放在 Domain 的 `BuiltinLocales`，是「隨程式出貨、一定已安裝」的唯一一份；啟動時同步成 `Dynamic_Locale` 的 `builtin` 列。`LocaleRegistry`（Application，singleton）實作 p3 留下的 `IInstalledLocales`：內建語系一律取自 `BuiltinLocales`，匯入的語系取自資料表，換新方式比照 `CachedSystemPolicy`（啟動載入、每個請求過期就檢查、只讓一個呼叫跑、讀不到沿用上次），過期時先查版本戳記，沒變只延長有效期。退回鏈由 Domain 的純函式 `LocaleChain.Fallbacks` 組（要求的語系 → 上層語系 → 系統預設 → 繁中 → 其餘已安裝語系），`CachedSystemPolicy.FallbacksFor(locale)` 結合 registry 與預設語系，`RequestLocale` 與 WebAPI 的訊息解析點都改用它；前端用同一份共用案例檔驗同一條鏈。前端的語系資訊（自稱、Quasar 語言包、上層語系）登入前從 `auth/options`、登入後從生效政策取，集中在 `locale-settings.ts`（`useLocales()`）。

**Tech Stack:** .NET 8、EF Core（SQLite 研發、PostgreSQL 正式）、MediatR、FluentValidation、xUnit＋FluentAssertions；Vue 3、Quasar 2、TanStack Query、vue-i18n 11、vitest、bun。不新增相依套件。

**Spec:** `docs/superpowers/specs/2026-11-17-language-packs-design.md`（§1 寫死清單、§2 反目標、§4.1–§4.4、§8.3 退回鏈、§8.4、§8.5、§10.1、§14、§15.1、§17.4 M1、§18 第 2–5、12 點、§19 語系表／p3 接法／`IsValid`／退回鏈、§20 i4a 列）。前置：p3 計畫 `docs/superpowers/plans/2026-11-11-policy-settings-p3.md` 判斷 13、15、22–25（p3 留的接口）。repo 規範：`AGENTS.md`、`.claude/rules/{backend,frontend,docs,app-packages}.md`（含 2026-11-20 的「語系退回」「寫進資料的系統文字」「上限與政策的通則」「時間顯示」「停用的現值」）、`docs/ai/testing.md`。

**路徑慣例：** 後端相對 `backend/`，前端相對 `frontend/`，`shared/` 與文件相對 repo 根目錄。行號以 main `2b71e593` 為準；不符時以符號為準。設計 §1 的行號是 2026-11-17 的，已位移，本計畫的「§1 對照」以現值重列。

---

## 前置條件（開工前逐項確認，不符就停下回報）

1. **p3 已在 main**（`git log --oneline main | grep -E "24bdb239|bc8d7203"` 有結果）。
2. **p3 的接口在原位**：
   - `IInstalledLocales`、`FactoryInstalledLocales`：`backend/src/DynamicModules.Application/Common/IInstalledLocales.cs`；註冊 `DependencyInjection.cs:49`（`TryAddSingleton`）。
   - `PolicyOptionSource`：`Application/Dynamic/PolicyService/PolicyOptionSource.cs`；`PolicyCatalog.IsLocaleOptions`（`Domain/Policies/PolicyCatalog.cs:112`，以 `ReferenceEquals(def.Options, FactoryLocales)` 判斷）。
   - `LocaleContext`：`Domain/ValueObjects/LocaleContext.cs`；`RequestLocale`（`Current`、`SystemDefault`、`For`）：`Application/Common/RequestLocale.cs`。
   - `CachedSystemPolicy.LocaleState`（`CachedSystemPolicy.cs:41`）、`LocaleFallbacks`（`:98`）、`Display(locale)`（`:101`）。
   - `I18nText.UnknownLocale`（`I18nText.cs:66`）是唯一判斷「支援語系」的地方；`I18nText.IsWellFormed` 不驗必填（必填在 `RequiredTextGuard`）。
   - 前端 `frontend/src/i18n/locale-settings.ts`（`setLocaleSettings`、`applyLocalePolicies`、`enabledLocales()`）。
3. **i4 其他段沒開工**：`grep -rn "LocaleRegistry\|useLocales\|Dynamic_Locale" backend/src frontend/src` 只有 `locale-settings.ts:4` 的註解。
4. **沒有其他會加 migration 的工作在跑**（`.claude/rules/backend.md`：兩邊 model snapshot 會衝突）。
5. **PostgreSQL 測試環境可用**（`docs/ai/testing.md`，`docker info` 成功或設了 `FORMWRIGHT_TEST_PG`）。

## p3 已做與 i4a 要做（設計 §18、p3 計畫頂端的「先留接口」對照）

| 設計要求 | p3 現況（main） | i4a |
|---|---|---|
| 三政策 `Options` 改「已安裝語系」（§4.3、§18-2） | 已有 `IInstalledLocales`＋`PolicyOptionSource`，出廠實作回四語 | 換成 `LocaleRegistry`（Task 4） |
| 判斷 13 退回鏈（§18-3） | 「要求 → 預設 → 繁中 → 四語順序」，`LocaleState.Fallbacks` 是固定的 `[預設, 繁中]` | 加上層語系與其餘已安裝語系；鏈依語系而不同，改成 `FallbacksFor(locale)`（Task 1、5） |
| `IsValid` 只驗格式與必填（M1、§18-5） | `IsValid` 已拆成 `IsWellFormed`＋`RequiredTextGuard`（必填依政策）；「支援語系」隔離在 `UnknownLocale` | `UnknownLocale` 換成代碼格式檢查；套件格式另立四語清單（Task 2） |
| `policy-catalog.json`、`policy-i18n` 排除語系類（§18-12） | 已排除（`choices` 沒有語系代碼） | 只驗證不改 |
| `auth/options` 只加欄位（§18-4） | 有 `defaultLocale`、`enabledLocales` | 加 `locales`（Task 6） |
| 前端只有一處取啟用語系（判斷 25） | `locale-settings.ts`；`PrintTab`、`I18nTextInput` 已改讀它 | 加語系資訊與 `useLocales()`；p3 新增的 7 個 `LOCALES` 使用點一起改（Task 7、8） |
| 選項名稱用自稱（§1、§4.3） | `policy-types.ts` 的 `choiceLabel` 用前端寫死的 `LOCALES` 自稱 | 改用 API 給的 `nativeName`（Task 8） |

## 子段切法

| 批 | 內容 | Task | migration |
|---|---|---|---|
| i4a-1 | 資料與機制：Domain 的代碼格式、內建清單、退回鏈純函式；拿掉 `SupportedLocales`（M1）；`Dynamic_Locale` 與內建同步；`LocaleRegistry` 接上政策選項 | 1–4 | 一個（`InstalledLocales`，SQLite 與 PostgreSQL 各一） |
| i4a-2 | 接線與畫面：退回鏈接到請求與訊息解析點；`auth/options`、生效政策回語系資訊；前端改從 API 取；文件、走查、gate | 5–9 | 無 |

同一分支 `i4a`，每個 Task 各自 commit；i4a-1 收尾跑一次窄範圍雙跑（不跑 gate），i4a-2 收尾才跑 gate、合併。分界理由：i4a-1 不改任何 API 形狀、前端不受影響，可以先審；i4a-2 是「API 新欄位＋它的消費端」，放同一批才不會出現沒人用的欄位。i4a-1 單獨存在時行為與 p3 相同（只有四語、鏈的結果不變），中斷也安全。

---

## 計畫期間的判斷

〔行為〕＝影響使用者看到或能做的事；〔技術〕＝實作選擇。已拍板的不再問。

1. 〔技術〕**內建四語清單放 Domain 的 `BuiltinLocales`**（`Domain/Localization/BuiltinLocales.cs`：`zh-TW` 繁體中文／`zh-TW`、`en-US` English／`en-US`、`th` ไทย／`th`、`vi` Tiếng Việt／`vi`，第三欄是 Quasar 語言包）。它是設計 §4.1「程式內嵌的 manifest」在 i4a 的形狀；i4b 搬到 `shared/i18n/<code>/locale.json` 內嵌時，這個類別改成讀資源，呼叫端不改。理由：Domain 的 `I18nText`（單參數 `Resolve` 的安全網）、`PolicyCatalog` 出廠選項、Application 的 C# 四語字串表（`MessageText`、`ReportText`、`AlertMailText`，它們就是內建語系的「內建層」）都要同一份；放 Application 的話 Domain 拿不到。`I18nText.SupportedLocales` 刪除，不留別名。
2. 〔技術〕**`LocaleRegistry` 的內建語系一律取自 `BuiltinLocales`，資料表只貢獻 `installed` 列**；資料表的 `builtin` 列由啟動同步寫入，供之後的上層語系參照、語系頁列表與稽核欄位用。理由：Application 測試多數不跑啟動流程、資料庫讀不到時也一樣，registry 永遠至少有內建四語，不會讓政策選項或退回鏈變空（反目標「匯入一份壞檔讓整個介面壞掉」的同類風險）；從沒載入成功時就只有內建四語，等於 p3 行為。
3. 〔技術〕**版本戳記是 `Dynamic_Locale` 的（筆數, 最大 `ModifiedUtc`）**；`Dynamic_LocalePack` 在 i4d1 建表時加進戳記。`ModifiedUtc` 不可空（新增時等於建立時間）。直接改資料庫又不動 `ModifiedUtc` 的修補不會被偵測，列已知限制（本計畫「已知限制」節），維運要改語系列就連 `ModifiedUtc` 一起改，或重啟。
4. 〔技術〕**registry 的 TTL、失敗快取、讀值逾時沿用 `CachedSystemPolicy.Ttl`／`FailureTtl`／`ReadTimeout`**（60 秒、10 秒、2 秒），不另立常數；換新用同一套「`SemaphoreSlim.WaitAsync(0)` 只讓一個跑、不可變快照以 `Volatile.Write` 發布」。另有 `Invalidate()`（標成過期）：i4a 的使用者是測試與啟動同步，i4d1 的匯入在交易提交後呼叫它（設計 §4.2「語系相關寫入的交易提交後立即換新」）。i4a 沒有任何會寫語系的 API，不做提交後立即換新。
5. 〔技術〕**請求路徑的換新順序：registry 先、政策快照後**（`SystemPolicyRefreshMiddleware`、`Program.InitializeDatabaseAsync` 都是）。理由：政策快照重載時經 `PolicyReader` → `PolicyOptionSource` → `IInstalledLocales` 正規化語系政策，registry 舊的話新語系的啟用值會被判成無效。
6. 〔技術〕**退回鏈＝要求的語系 → 上層語系（最多 3 層、遇到環或未安裝就停）→ 系統預設語系 → 繁中 → 全部已安裝語系（依安裝順序），去重**（設計 §8.3、§10.1，U3）。`fallbacks` 清單是「要求的語系之後」那幾步：上層語系＋預設＋繁中＋已安裝語系，不特地剔除要求的語系本身（重複無害；沒有上層語系時，同一個預設語系下所有語系的清單相同，例：出廠 `["zh-TW","en-US","th","vi"]`）。`I18nText.Resolve(locale, fallbacks)` 的最後一步原本是「`SupportedLocales` 順序第一個有值的」，改成「`BuiltinLocales` 順序第一個有值的」（安全網；registry 組的鏈已含全部已安裝語系，到這一步時結果不變）。**不挑沒安裝的語系鍵**：文字只有 `ja` 而 `ja` 沒安裝時回空字串（U6「存起來但不顯示」）。單參數 `Resolve(locale)`（中文訊息、套件格式用）行為與現在逐字相同。
7. 〔技術〕**語系代碼格式守衛 `LocaleCode`**（`Domain/Localization/LocaleCode.cs`）：`IsValid(string)` 只認正規形式 `語言[-文字][-地區]`（語言 2–3 小寫、文字 4 字母首大寫、地區 2 大寫或 3 位數字，正則 `^[a-z]{2,3}(-[A-Z][a-z]{3})?(-([A-Z]{2}|[0-9]{3}))?$`）；`TryNormalize(string, out string)` 把 `JA`、`zh-hant-hk` 轉成 `ja`、`zh-Hant-HK`（i4d1 安裝時用；i4a 只有測試）。`I18nText` 的鍵要求正規形式（`"JA"` 不收），錯誤文字沿用 `不支援的語系：{key}`，不改訊息。`UnknownLocale` 改名 `InvalidLocale`，語意從「不在支援清單」改成「格式不對」。
8. 〔技術〕**套件格式（`formatVersion` 2–8）仍只收四語鍵**（`.claude/rules/app-packages.md`「舊格式不放寬」、設計 §10.5；放寬是 i4f）。`AppPackageValidator` 新增 `internal static readonly string[] FormatLocales = ["zh-TW","en-US","th","vi"]`，`CheckText`（`AppPackageValidator.cs:175-182`）與 `V7.CheckDescription`（`AppPackageValidator.V7.cs:86`）改用它擋鍵，錯誤文字不變。它是**格式定義**，不引用 `BuiltinLocales`（之後內建語系變多，舊格式仍是四語）。`TemplateValidator.HasZhTw`（v1 不擋未知鍵）不動。
9. 〔技術〕**C# 四語字串表改引用 `BuiltinLocales.Codes`**：`MessageText.AsI18n`（`MessageText.cs:485`，i4c 再改成已安裝語系）、`FieldImportParser.IsDisplayed`（`FieldImportParser.cs:82`，設計 §15 已知限制）、`AlertMailText.Locales`（`AlertMailText.cs:12`，p3 現況檢查指出設計 §1 漏列）。這三處的語意是「這張 C# 表有翻譯的語系」＝內建語系；`switch` 本身（`"en-US" => e.En`…）是表的欄位對應，i4c 處理。
10. 〔技術〕**`CachedSystemPolicy` 的 `LocaleFallbacks` 屬性改成 `FallbacksFor(string locale)` 方法**（依語系不同，不能再是一個固定清單），`LocaleState` 拿掉 `Fallbacks`；`CachedSystemPolicy` 建構子多收 `LocaleRegistry`（singleton 對 singleton，registry 不依賴快照，沒有循環）。`Display(locale)` 改用它。WebAPI 的兩個解析點（`ServiceResultExtensions.cs:14-15`、`FrozenFieldExceptionHandler.cs:14-15`）改取 `RequestLocale.Current`（一個 `LocaleContext`），不再各自拼語系與鏈。
11. 〔技術〕**設計 §4.3「啟用清單裡的語系被移除時，讀取端把它當成沒啟用」做在 `PolicyReader.Usable`**：語系類 RoleSet 的已存值先濾掉沒安裝的項目再正規化（濾完為空就當成沒設、記 Warning）；PUT 照舊嚴格檢查（不在已安裝語系就 400）。預設語系（Choice）指向沒安裝的語系時照現行規則當成沒設、退回上一層。i4a 沒有移除語系的入口（i4d1 才有，且會擋啟用中的），這條只為資料修補。
12. 〔技術〕**`auth/options` 的 `locales` 在 i4a 回 `{ code, nativeName, quasarLang, fallbackCode }`，`uiHash` 留到 i4b**（設計 §4.4 列了 `uiHash`，但它是 i4b 合併介面文字後才算得出的值；i4b 再**加**這個欄位，同樣不改形狀）。多回 `fallbackCode` 是給前端組同一條退回鏈（設計 §10.1「鏈隨 `locales`／生效政策給」）。只含啟用中的語系，順序同 registry。
13. 〔技術〕**生效政策 `GET policies/effective` 加 `locales`（全部已安裝語系，同上四欄）**，所有登入者都回（設計 §4.4：登入後同一份資料隨生效政策回傳）。代碼與自稱不是機密；只回給管理員的話，前端要多一條權限分支，而未啟用的語系只會出現在管理員的政策頁（選項）裡。`moduleKey` 有值時也回（同一個 handler，不分支）。
14. 〔技術〕**前端 `LocaleCode`、`Locale` 改成 `string` 的別名**（不加品牌欄位）。理由：品牌型別要在上百個 spec 的字面值加轉型，換不到實際保護；合法性由「在不在 API 給的清單」判斷。`I18nText` 型別改 `Partial<Record<string, string>>`。
15. 〔技術〕**`readLocale()` 不再對照寫死清單**：localStorage 的值去空白、非空、長度 ≤ 35 就回傳；是否啟用由 auth guard 拿到 `enabledLocales` 後判斷（p3 現行：記的語系沒啟用就套預設）。boot 期間短暫套用一個沒有翻譯包的語系時，vue-i18n 退回繁中、Quasar 找不到語言包就維持原狀，下一步 guard 修正。
16. 〔技術〕**一次產生多語系的兩處（`LedgerTab` 沖銷欄位標籤、`designer-graph` 新節點名稱）改成「已安裝語系中前端有翻譯包的」**（`installedLocales()` ∩ `i18n.global.availableLocales`）。M3 的口徑是「每個已安裝語系」，但 i4a 還沒有執行期載入（i4b 的 `ensureLocaleMessages`）；對沒有翻譯包的語系呼叫 `t()` 會拿到繁中，等於把中文寫進日文那一格，比空著更糟。i4a 只有內建四語，結果與現在相同；i4b 接上 `ensureLocaleMessages` 後這個交集就等於全部已安裝語系。
17. 〔技術〕**Quasar 語言包 glob 改 `../../node_modules/quasar/lang/*.js`**（設計 §8.5，各自 lazy chunk）；`applyDocumentLocale(code)` 取 `localeInfo(code)?.quasarLang`，沒有資訊時用代碼本身，找不到檔就用 `en-US`。`shared/quasar-langs.json` 與後端驗證 `QuasarLang` 在 i4b（i4a 的 `QuasarLang` 只來自 `BuiltinLocales`）。
18. 〔技術〕**前端退回鏈與後端用同一份共用案例**：新增 `shared/locale-chain-cases.json`（安裝清單含上層語系、預設語系、要求的語系、文字、期望結果），Domain 的 `LocaleChainTests` 與前端 `i18n-text.vitest.spec.ts` 都跑，同 `rule-cases.json` 的做法（設計 T15 精神；§19「三處同結果」的前兩處，後端訊息那處在 i4c）。前端語系資訊還沒載入（登入前的公開頁）時鏈退成「要求 → 預設 → 繁中」。
19. 〔技術〕**語系類政策在前端的判斷**：`isLocalePolicy(meta)` 原本靠「選項都在寫死的 `LOCALES`」，改成「分類 `locale`、型別 choice／roleSet、選項都在 `installedLocales()`」；`choiceLabel` 用 `nativeName(code)`（找不到回代碼）。不在 API 加旗標（`calendar.weekStart` 的選項是 `monday` 這類，不會撞語系代碼）。
20. 〔技術〕**`RequiredTextGuard` 的語系名稱表 `LocaleNames.Zh`（`繁體中文`、`英文`…）不動**；不在表內的代碼原樣輸出（p3 判斷 9 的接口註解就是這樣設計給 i4a 的）。
21. 〔技術〕**測試用的「安裝一個語系」helper**：`backend/tests/DynamicModules.WebAPITests/LocaleTestKit.cs` 與 `backend/tests/DynamicModules.ApplicationTests/Common/LocaleTestKit.cs`，直接寫 `Dynamic_Locale` 一列（`Origin = installed`），再 `registry.Invalidate()` 並 `await registry.EnsureFreshAsync(...)`。i4a 沒有安裝 API，這是唯一的造資料方式；i4d1 有 API 後測試可改走 API。`PolicyApiTests` 的 `ThreeLocales` 替身（`PolicyApiTests.cs:164-186`）改用它，刪掉替身。

---

## 待決（設計沒涵蓋、會改使用者行為；不阻擋 i4a-1，Task 7 前要先問使用者）

- **Q1 伺服器連不上時（登入頁取不到 `auth/options`），語言選單列哪些語言？**
  背景：p3 判斷 15 是「取不到時維持繁中、全部語系可選」，「全部」來自前端寫死的四語清單（`auth-guard.ts:26` 的 `LOCALES`）。i4a 要拿掉這份清單（§1 寫死清單、反目標「單一來源」），取不到伺服器時前端就不知道有哪些語言、也不知道它們的自稱。
  - **推薦：只列繁中**（前端只留「最後退回語系是繁中、自稱 繁體中文」這一個基準常數，與 `FALLBACK_LOCALE` 同一處）。理由：連不上伺服器時本來就登入不了；i4b 起前端只打包繁中，那時選其他語言也沒有文字可顯示，現在先對齊；U4 已定最後退回是繁中。代價：連不上的那幾秒，登入頁的語言選單從四項變成一項。
  - 另一選項：前端另留一份內建四語的代碼與自稱，只在連不上時用。代價：多一份要和後端 `BuiltinLocales` 同步的清單（i4b 只打包繁中後，選了英文也只會看到繁中），違反單一來源。
  - 選錯代價：選「另留一份」，之後內建語系增減要改兩處，且 i4b 會再拆一次；選「只列繁中」，斷線時想先切英文的人要等連上。

---

## Global Constraints

- **使用者看得到的行為不變**：沒有安裝任何新語系時，四語的語言選單、多語輸入框、政策選項（含自稱）、退回結果、錯誤訊息都與 main 相同；既有測試的預期值不改，例外只有下列（各 Task 列明）：
  - `I18nTextTests`（`DomainTests/ValueObjects/I18nTextTests.cs:33-35`）：`jp`、`ja` 現在格式合法；改成 `JP`、`zh_TW`、`e` 被拒。
  - `ModulesApiTests.cs:42` 的 `[InlineData("ja", "マスタ", "不支援的語系：ja")]`、`ModuleServiceTests.cs:181-184`：分區含 `ja` 改成通過（M1），另加格式錯的案例保留「不支援的語系」斷言。
  - `CachedSystemPolicyTests.LocaleFallbacks_DefaultFirst`（`:154-174`）、`RequestLocaleTests.cs:38,46`：鏈多了其餘已安裝語系（例：預設 `en-US` 時 `["en-US","zh-TW","th","vi"]`）。
  - `PolicyApiTests.cs:164-186` 的 `ThreeLocales` 替身改走 `LocaleTestKit`。
  - 前端 `locale.vitest.spec.ts:10,74` 引用 `LOCALES`：改比對自稱字串陣列（`:75` 已有字面值斷言）。
- **鐵則**：語系清單只有一個來源（`Dynamic_Locale`＋`BuiltinLocales`），前端從 API 取；啟用、預設、必填只在 p3 政策（反目標「啟用存兩份」）；`I18nText.IsWellFormed` 仍是 Domain 純函式、不碰 registry。
- **可寫死、要列清單的**（交付說明列出，設計 §14 已涵蓋）：語系代碼格式守衛與正規化；繁中是平台基準語系（退回鏈最後的基準、套件必填）；`BuiltinLocales` 的四列；套件格式 2–8 的 `FormatLocales`；registry 的 60／10／2 秒；上層語系最多 3 層。
- **PostgreSQL 相容**：沒有 raw SQL；`ModifiedUtc`／`CreatedUtc` 用 `.AsUtc()`（`timestamptz`），寫入時 `Kind=Utc`；版本戳記用 LINQ 的 `Count` 與 `Max`，不依賴回傳順序；列表依 `Origin`、`CreatedUtc`、`Code` 明確排序（都不可空）；列舉結果先 `ToListAsync` 再處理。
- **PostgreSQL 驗證**：
  - 進雙跑集（`[Trait("Category", "Postgres")]`）：Task 3 的 `LocaleRepositoryTests`、`BuiltinLocaleSyncTests`；Task 4 的 `InstalledLocalePolicyTests`；Task 5 的 `LocaleChainApiTests` 新案例；Task 6 的 `AuthApiTests` 新案例與 `PolicyApiTests.Effective_IncludesInstalledLocales`。
  - 標了 `Postgres` 的 Task，Step 4 另跑 `FORMWRIGHT_TEST_DB=postgres dotnet test <專案> --filter "(<該 Task 的 filter>)&Category!=SqliteOnly&Category!=Benchmark"`；Docker 起不來就停下回報，不跳過。
  - Task 3 加 migration、動 Persistence：i4a-1 收尾另跑一次 PostgreSQL 全套（不進 gate），結果寫進交付說明。
- **migration**：整段只有一組 `InstalledLocales`（`scripts/db/add-migration.sh InstalledLocales` 一次產兩份），只用 EF 產生、不手改；`MigrationConsistencyTests` 把關。
- 後端新的使用者訊息：i4a 沒有（不新增 `*Messages.cs` 句子）。
- 前端新字串：i4a 沒有（Q1 推薦的繁中自稱是基準常數，不是翻譯）；若實作中需要，四語鍵一致、單行 JSON。
- 有畫面的改動（語言選單、政策頁選項、維運通道語言下拉）在 390、820、1366、1920 四種寬度不橫捲、不露出鍵名。
- 測試只跑窄範圍；後端、前端、gate 不並行；vitest `--maxWorkers=2`。
- git：逐檔 `git add`；commit 前 `git status`；不用 `--no-verify`、不 force push、不 merge commit；公開 repo 不寫公司字串、個人絕對路徑、內網 IP。
- 段收尾用 `formwright-purpose-check` skill 對照鐵則；`formwright-ship` 上線。

## 檔案地圖

**共用檔（並行工作要協調；標 ★ 的本段會動）**

| 共用檔 | 本段 | 說明 |
|---|---|---|
| ★ DbContext 與 EF migration | Task 3 | `Domain/Data/Configurations/` 新增設定；`Persistence/Migrations`、`Persistence.Postgres/Migrations` 各一個 `InstalledLocales` 與兩份 `DynamicDbContextModelSnapshot.cs` |
| ★ 後端 DI 註冊 | Task 3、4 | `Application/DependencyInjection.cs:49`（`IInstalledLocales` 改指向 registry）、`Persistence/DependencyInjection.cs:84` 附近（`ILocaleRepository`） |
| ★ 啟動與 middleware | Task 3、4 | `WebAPI/Program.cs:229` 前後、`WebAPI/Ops/SystemPolicyRefreshMiddleware.cs` |
| i18n 語系檔 `frontend/src/i18n/*.json` | 不動 | i4a 沒有新句子（Q1 選「另留一份」也不需要新鍵） |
| 前端路由與選單 | Task 7 | 只動 `router/auth-guard.ts` 的語系載入；`routes.ts`、選單結構不動 |
| `backend/apps/*.app.json` | 不動 | 套件語系鍵放寬是 i4f |
| `shared/` | Task 1 | 新增 `shared/locale-chain-cases.json` |

**後端（i4a-1：Task 1–4；i4a-2：Task 5–6）**

- Create: `backend/src/DynamicModules.Domain/Localization/{LocaleCode,BuiltinLocales,LocaleChain}.cs`、`backend/src/DynamicModules.Domain/Entities/Dynamic/InstalledLocale.cs`（含 `LocaleOrigin`）、`backend/src/DynamicModules.Domain/Data/Configurations/LocaleConfiguration.cs`、`backend/src/DynamicModules.Domain/Repositories/Dynamic/ILocaleRepository.cs`、`backend/src/DynamicModules.Persistence/Repositories/Dynamic/LocaleRepository.cs`、`backend/src/DynamicModules.Persistence/Seed/BuiltinLocaleSync.cs`、`backend/src/DynamicModules.Application/Common/LocaleRegistry.cs`、`backend/src/DynamicModules.Application/Common/LocaleInfoResponse.cs`
- Modify: `Domain/ValueObjects/I18nText.cs`、`Domain/Policies/PolicyCatalog.cs:93-97`、`Application/Common/IInstalledLocales.cs`（刪 `FactoryInstalledLocales`）、`Application/Common/MessageText.cs:485`、`Application/Common/RequestLocale.cs`、`Application/Dynamic/ImportService/FieldImportParser.cs:82`、`Application/Dynamic/OpsService/Alerts/AlertMailText.cs:12`、`Application/Dynamic/ModuleService/Data/ModuleRules.cs:32`、`Application/Dynamic/TaskRuleService/TaskRuleValidator.cs:124`、`Application/Dynamic/AppService/AppPackageValidator.cs:175-182`、`AppPackageValidator.V7.cs:86`、`Application/Dynamic/PolicyService/{CachedSystemPolicy,PolicyReader,PolicyHandlers,PolicyModels}.cs`、`Application/Dynamic/AccountService/{Queries/GetAuthOptions/GetAuthOptionsHandler.cs,Data/AccountResponses.cs:10-11}`、`Application/Dynamic/ReportService/ReportRunner.Output.cs:54,75`、`Application/DependencyInjection.cs`、`Persistence/DependencyInjection.cs`、`WebAPI/Program.cs`、`WebAPI/Ops/SystemPolicyRefreshMiddleware.cs`、`WebAPI/Controllers/ServiceResultExtensions.cs:14-15`、`WebAPI/Errors/FrozenFieldExceptionHandler.cs:14-15`

**前端（i4a-2：Task 7–8）**

- Modify: `frontend/src/i18n/{locale.ts,locale-settings.ts,translate.ts}`、`frontend/vitest.setup.ts`、`frontend/src/router/auth-guard.ts`、`frontend/src/application/dynamic/session-settings.ts`、`frontend/src/application/dynamic/queries/use-business-time-watch.ts`、`frontend/src/domain/dynamic/{types.ts,i18n-text.ts,menu-nav.ts,policy-types.ts}`、`frontend/src/presentation/locale/LocaleSwitcher.vue`、`frontend/src/presentation/ops/OpsChannelTab.vue`、`frontend/src/presentation/dynamic/ledger/LedgerTab.vue`、`frontend/src/presentation/dynamic/approval-designer/graph/designer-graph.ts`

**文件（Task 9）**：`docs/roadmap.md`（i4 列進度）、設計文件 §20 補 i4a 完成與本計畫判斷 12 的 `uiHash` 移段、`docs/feature.md`（i4 列改 🚧，跑腳本）。`docs/manual.md` 不改（沒有使用者看得到的變化；Q1 選推薦時斷線行為也不在手冊範圍）。

---

# i4a-1 資料與機制

### Task 1: 語系代碼格式、內建語系清單、退回鏈（Domain 純函式）

**Files:**
- Create: `backend/src/DynamicModules.Domain/Localization/LocaleCode.cs`、`BuiltinLocales.cs`、`LocaleChain.cs`
- Create: `shared/locale-chain-cases.json`
- Test: `backend/tests/DynamicModules.DomainTests/Localization/LocaleCodeTests.cs`（新）、`LocaleChainTests.cs`（新，讀共用案例）

**Interfaces:**
- `public static class LocaleCode { public static bool IsValid(string? code); public static bool TryNormalize(string? raw, out string code); public const int MaxLength = 35; }`（判斷 7）。
- `public sealed record BuiltinLocale(string Code, string NativeName, string QuasarLang);` `public static class BuiltinLocales { public static IReadOnlyList<BuiltinLocale> All { get; } public static IReadOnlyList<string> Codes { get; } public static bool Contains(string code); }`，順序 zh-TW、en-US、th、vi（判斷 1）。類別註解寫「隨程式出貨、一定已安裝；i4b 改讀 `shared/i18n/<code>/locale.json`」。
- `public static class LocaleChain { public const int MaxParentDepth = 3; public static IReadOnlyList<string> Fallbacks(string locale, string defaultLocale, IReadOnlyList<(string Code, string? FallbackCode)> installed); }`：照判斷 6 組鏈；上層語系只走已安裝的、遇到環或第 4 層就停；`defaultLocale` 或 `I18nText.FallbackLocale` 沒安裝也照放（繁中永遠是內建）；去重、保留第一次出現的位置。
- `shared/locale-chain-cases.json`：`{ "installed": [{ "code", "fallback" }], "cases": [{ "name", "locale", "default", "text": {…}, "fallbacks": [...], "expected": "…" }] }`。至少收：出廠四語（`th` 缺 → 繁中）；預設 `en-US`；`ja`→`en-US` 上層、預設 `th`、文字只有繁中與泰文 → 泰文（U3 例）；上層兩層 `zh-Hant-HK`→`zh-HK`→`zh-TW`；環 `a`↔`b`；上層未安裝；文字只有未安裝的 `ko` → 空字串（U6）；文字只有匯入語系 `ja`、要求 `vi` → `ja`（已安裝語系在鏈尾）。

- [ ] **Step 1: 寫失敗的測試**
  - `LocaleCodeTests`：`ja`、`zh-TW`、`zh-Hant-HK`、`es-419`、`fil` 合法；`JA`、`zh_TW`、`e`、`abcd`、`zh-tw`、`zh-Hant-hk`、`""`、`null`、36 字元不合法；`TryNormalize("JA")` → `ja`、`TryNormalize("zh-hant-hk")` → `zh-Hant-HK`、`TryNormalize("es-419")` 不變、`TryNormalize("zh_TW")` false。
  - `LocaleChainTests.SharedCases`：逐案 `Fallbacks(...)` 等於 `fallbacks`，且 `new I18nText(text).Resolve(locale, fallbacks)` 等於 `expected`（現行 `Resolve` 的最後一步只看四語，所以 `ko` 與 `ja` 兩案在 Task 1 就成立；Task 2 改安全網後仍要成立）。
  - `LocaleChainTests.Factory_SameAsP3`：只裝內建四語、預設 `zh-TW`，`Fallbacks("th","zh-TW")` == `["zh-TW","en-US","th","vi"]`；預設 `en-US` 時 `Fallbacks("th","en-US")` == `["en-US","zh-TW","th","vi"]`（用 `Resolve` 解出來的結果與 p3 相同）。
  - `BuiltinLocalesTests`（同檔）：`Codes` 依序四個、每個 `LocaleCode.IsValid`。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.DomainTests --filter "FullyQualifiedName~Localization"`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2
- [ ] **Step 5: Commit**（`feat(i18n): 語系代碼格式、內建語系清單與退回鏈`）

**驗收**：Step 4 全過；三個類別都在 Domain、沒有 IO。

### Task 2: 拿掉 `I18nText.SupportedLocales`；多語文字只驗代碼格式（M1）；套件格式仍只收四語

**Files:**
- Modify: `backend/src/DynamicModules.Domain/ValueObjects/I18nText.cs:12,25-36,47-66`（刪 `SupportedLocales`；`Resolve` 安全網改 `BuiltinLocales.Codes`；`UnknownLocale` → `InvalidLocale`，用 `LocaleCode.IsValid`；`IsWellFormed` 註解改「沒有格式不對的語系鍵」）
- Modify: `backend/src/DynamicModules.Domain/Policies/PolicyCatalog.cs:93-97`（`FactoryLocales = BuiltinLocales.Codes`，註解改「內建語系；執行期選項經 `PolicyOptionSource` 取已安裝語系」）
- Modify（改呼叫 `InvalidLocale`，語意變格式檢查）：`Application/Dynamic/ModuleService/Data/ModuleRules.cs:24-32`、`Application/Dynamic/TaskRuleService/TaskRuleValidator.cs:116,124`
- Modify（套件格式，判斷 8）：`Application/Dynamic/AppService/AppPackageValidator.cs:174-182`（`FormatLocales` 擋鍵，再呼叫 `IsWellFormed`）、`AppPackageValidator.V7.cs:83-86`
- Modify（C# 四語表，判斷 9）：`Application/Common/MessageText.cs:483-485`（註解「展成內建語系」）、`Application/Dynamic/ImportService/FieldImportParser.cs:80-82`、`Application/Dynamic/OpsService/Alerts/AlertMailText.cs:12`
- Test: `backend/tests/DynamicModules.DomainTests/ValueObjects/I18nTextTests.cs`、`backend/tests/DynamicModules.WebAPITests/Dynamic/ModulesApiTests.cs:42`、`backend/tests/DynamicModules.ApplicationTests/Dynamic/ModuleServiceTests.cs:181-184`、`backend/tests/DynamicModules.WebAPITests/Dynamic/AppPackageFormatTests.cs`、`backend/tests/DynamicModules.ApplicationTests/Dynamic/Policies/RequiredTextPolicyTests.cs`

- [ ] **Step 1: 寫失敗的測試**
  - `I18nTextTests`：`{"ja":"受注"}` 的 `IsWellFormed` true；`{"JP":"x"}` false 且 `不支援的語系：JP`；`{"zh_TW":"x"}` false；`InvalidLocale(["zh-TW","ja","e"]) == "e"`；`Resolve("vi")` 對 `{"ko":"x"}` 回 `""`；`Resolve("th")` 對 `{"vi":"Đơn"}` 回 `Đơn`（安全網不變）。
  - `ModulesApiTests`：原 `ja` 那列改成 `[InlineData("zh_TW", "x", "不支援的語系：zh_TW")]`；新案 `Section_WithJapanese_Accepted`：分區 `{zh-TW:"主檔", ja:"マスタ"}` 建立成功、讀回保留 `ja`。
  - `ModuleServiceTests`：`ja` 分區通過；`zh_TW` 分區回 `不支援的語系：zh_TW`。
  - `RequiredTextPolicyTests.Rename_WithUninstalledLocaleText_Allowed`（M1、§19）：欄位標籤 `{zh-TW:"客戶", ja:"顧客"}`（`ja` 沒安裝）建立成功；改繁中標籤後存檔成功、`ja` 保留；`TaskRuleValidator` 的任務標題同樣收 `ja`。
  - `AppPackageFormatTests.Structure_UnsupportedLocaleKey_IsRejected` 不改（`fr` 仍被拒）；新案 `Structure_JapaneseKey_StillRejectedInFormat8`：名稱帶 `ja` → 錯誤含 `ja`（舊格式不放寬）；`TaskRule_DescriptionJapanese_Rejected`（V7 說明欄）。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.DomainTests --filter "FullyQualifiedName~I18nText|FullyQualifiedName~Localization"`；`dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~ModuleServiceTests|FullyQualifiedName~RequiredTextPolicyTests"`；`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~ModulesApiTests|FullyQualifiedName~AppPackageFormatTests"`
- [ ] **Step 3: 實作**（先刪 `SupportedLocales`，讓編譯錯誤列出全部使用點，逐一依上面處置）
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；另跑 ApplicationTests `--filter "FullyQualifiedName~TaskRule|FullyQualifiedName~Import|FullyQualifiedName~MessageText|FullyQualifiedName~Alert"`、WebAPITests `--filter "FullyQualifiedName~AppFormat|FullyQualifiedName~AppPackage|FullyQualifiedName~Template|FullyQualifiedName~AlertChannel"`；`grep -rn "SupportedLocales\|UnknownLocale" backend/src backend/tests` 無結果
- [ ] **Step 5: Commit**（`feat(i18n): 多語文字只驗語系代碼格式，套件舊格式仍只收四語（M1）`）

**驗收**：Step 4 全過；grep 無結果；套件「舊格式帶新語系鍵被擋」有測試。

### Task 3: `Dynamic_Locale` 表、repository、啟動同步內建四語

**Files:**
- Create: `backend/src/DynamicModules.Domain/Entities/Dynamic/InstalledLocale.cs`：`Code`（PK）、`NativeName`、`QuasarLang?`、`FallbackCode?`、`Origin`（`LocaleOrigin { Builtin, Installed }`）、`CreatedUserId?`、`CreatedUtc`、`ModifiedUserId?`、`ModifiedUtc`。註解寫設計 §4.1 的「不存的欄位」與 T19。
- Create: `backend/src/DynamicModules.Domain/Data/Configurations/LocaleConfiguration.cs`：`ToTable("Dynamic_Locale")`；`Code` 35、`NativeName` 50、`QuasarLang` 20、`FallbackCode` 35、使用者欄 50；`Origin` 用 `.AsCamelText()`（存 `builtin`／`installed`，同 `MailSettingsConfiguration` 的 enum 寫法）；兩個時間 `.AsUtc()`。不設外鍵到自己（`FallbackCode` 的完整性由 i4d1 的安裝驗證與移除守衛負責，設計 §4.5）。
- Create: `backend/src/DynamicModules.Domain/Repositories/Dynamic/ILocaleRepository.cs`：`Task<List<InstalledLocale>> ListAsync(CancellationToken ct)`（依 `Origin`（builtin 在前）、`CreatedUtc`、`Code` 排序）；`Task<LocaleVersionStamp> StampAsync(CancellationToken ct)`；`public sealed record LocaleVersionStamp(int Count, DateTime? MaxModifiedUtc)`。
- Create: `backend/src/DynamicModules.Persistence/Repositories/Dynamic/LocaleRepository.cs`；Modify: `backend/src/DynamicModules.Persistence/DependencyInjection.cs`（`AddScoped<ILocaleRepository, LocaleRepository>()`）
- Create: `backend/src/DynamicModules.Persistence/Seed/BuiltinLocaleSync.cs`：`static Task EnsureAsync(DynamicDbContext db, TimeProvider time, CancellationToken ct)`：依 `BuiltinLocales.All` 新增缺的列、`NativeName`／`QuasarLang` 不同才更新並設 `ModifiedUtc`、相同不寫；`Origin` 被改成 `installed` 的內建代碼改回 `builtin`；不刪任何列、不碰 `installed` 列。
- Modify: `backend/src/DynamicModules.WebAPI/Program.cs:228-229`（`MigrateAsync` 之後、`CachedSystemPolicy.EnsureFreshAsync` 之前呼叫 `BuiltinLocaleSync.EnsureAsync`，仍在啟動鎖內）
- Create（EF 產生）：`scripts/db/add-migration.sh InstalledLocales`
- Test: `backend/tests/DynamicModules.ApplicationTests/Persistence/LocaleRepositoryTests.cs`（新，`Category=Postgres`）、`backend/tests/DynamicModules.ApplicationTests/Persistence/BuiltinLocaleSyncTests.cs`（新，`Category=Postgres`）、`MigrationConsistencyTests`

- [ ] **Step 1: 寫失敗的測試**
  - `BuiltinLocaleSyncTests`：空表 → 四列 `builtin`、自稱與 Quasar 語言包同 `BuiltinLocales`；再跑一次 → 四列的 `ModifiedUtc` 不變（不變不寫）；把 `th` 的 `NativeName` 改成 `x` → 再跑後回 `ไทย` 且 `ModifiedUtc` 變新；已有一列 `ja`（`installed`）→ 原樣保留。
  - `LocaleRepositoryTests`：`ListAsync` 排序（四個 builtin，再依建立時間的 `ko`、`ja`）；`StampAsync` 空表 `(0, null)`；新增一列後 `Count`＋1、`MaxModifiedUtc` 變大；`MaxModifiedUtc.Kind == Utc`。
  - `MigrationConsistencyTests` 照過（兩邊都沒有待產生的變更）。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~LocaleRepositoryTests|FullyQualifiedName~BuiltinLocaleSyncTests"`
- [ ] **Step 3: 實作**（entity、設定、repository、同步；再 `bash scripts/db/add-migration.sh InstalledLocales`，檢查產生的兩份只有建 `Dynamic_Locale` 一張表）
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；`--filter "FullyQualifiedName~MigrationConsistencyTests|FullyQualifiedName~SeedDataTests"`；WebAPITests `--filter "FullyQualifiedName~PermissionAxesUpgradeTests|FullyQualifiedName~AuthApiTests"`（啟動流程多了同步）；PostgreSQL：ApplicationTests `(FullyQualifiedName~LocaleRepositoryTests|FullyQualifiedName~BuiltinLocaleSyncTests|FullyQualifiedName~MigrationConsistencyTests)`
- [ ] **Step 5: Commit**（`feat(i18n): 已安裝語系表與啟動時同步內建四語`；migration 檔與兩份 snapshot 逐檔 add）

**驗收**：Step 4 全過（含 PostgreSQL）；一組 migration、只建一張表。

### Task 4: `LocaleRegistry` 接上政策選項（版本戳記、換新順序、讀取端濾掉已移除的語系）

**Files:**
- Create: `backend/src/DynamicModules.Application/Common/LocaleRegistry.cs`
- Modify: `backend/src/DynamicModules.Application/Common/IInstalledLocales.cs`（刪 `FactoryInstalledLocales`；介面註解改「實作是 `LocaleRegistry`」）
- Modify: `backend/src/DynamicModules.Application/DependencyInjection.cs:49`（`AddSingleton<LocaleRegistry>()`；`AddSingleton<IInstalledLocales>(sp => sp.GetRequiredService<LocaleRegistry>())`）
- Modify: `backend/src/DynamicModules.WebAPI/Ops/SystemPolicyRefreshMiddleware.cs`（先 `registry.EnsureFreshAsync`、再 `cache.EnsureFreshAsync`，判斷 5；類別註解同步）、`backend/src/DynamicModules.WebAPI/Program.cs`（同步之後、政策快照之前 `registry.EnsureFreshAsync`）
- Modify: `backend/src/DynamicModules.Application/Dynamic/PolicyService/PolicyReader.cs:88-95`（判斷 11：語系類 RoleSet 已存值先濾掉沒安裝的項目）
- Create: `backend/tests/DynamicModules.WebAPITests/LocaleTestKit.cs`、`backend/tests/DynamicModules.ApplicationTests/Common/LocaleTestKit.cs`（判斷 21）
- Test: `backend/tests/DynamicModules.ApplicationTests/Common/LocaleRegistryTests.cs`（新，純記憶體，用假 `ILocaleRepository` 計次與 `ManualTimeProvider`）、`backend/tests/DynamicModules.WebAPITests/Dynamic/InstalledLocalePolicyTests.cs`（新，`Category=Postgres`）、`backend/tests/DynamicModules.WebAPITests/Dynamic/PolicyApiTests.cs:150-186`、`backend/tests/DynamicModules.ApplicationTests/Dynamic/Policies/PolicyReaderTests.cs:198`

**Interfaces:**
- `public sealed record InstalledLocaleInfo(string Code, string NativeName, string? QuasarLang, string? FallbackCode, bool Builtin);`
- `public sealed class LocaleRegistry(IServiceScopeFactory scopes, TimeProvider time, ILogger<LocaleRegistry> logger) : IInstalledLocales`
  - `IReadOnlyList<InstalledLocaleInfo> Locales`（內建四語取自 `BuiltinLocales`，再接資料表的 `installed` 列；判斷 2）、`IReadOnlyList<string> Codes`、`InstalledLocaleInfo? Find(string code)`。
  - `IReadOnlyList<string> FallbacksFor(string locale, string defaultLocale)` → `LocaleChain.Fallbacks(locale, defaultLocale, Locales.Select(l => (l.Code, l.FallbackCode)))`。
  - `Task EnsureFreshAsync(CancellationToken ct)`：沒過期直接返回；`WaitAsync(0)` 拿不到就返回；過期時先 `StampAsync`，與上次相同只把 `LoadedAt` 延到現在，不同才 `ListAsync` 重建；讀不到（例外或 `ReadTimeout`）記 Warning、沿用上次（從沒成功過就是內建四語），標 `Failed` 用 `FailureTtl`（判斷 4）。
  - `void Invalidate()`。
  - 註解寫設計 §4.2、判斷 2–5，以及「i4b 的介面文字合併表與雜湊也放這裡」。

- [ ] **Step 1: 寫失敗的測試**
  - `LocaleRegistryTests`：
    - `NeverLoaded_HasBuiltinFour`：沒呼叫過 `EnsureFreshAsync` 時 `Codes` 是四語、`Find("th").NativeName == "ไทย"`。
    - `Loaded_AppendsInstalledInOrder`：假 repository 回四個 builtin（`th` 的自稱故意寫成 `x`）＋`ja`（上層 `en-US`）→ `Codes` 是 `zh-TW,en-US,th,vi,ja`，`th` 的自稱仍是 `ไทย`（內建取自程式）。
    - `StampUnchanged_DoesNotReloadList`：過 61 秒、戳記相同 → `StampAsync` 叫 2 次、`ListAsync` 只叫 1 次。
    - `StampChanged_Reloads`、`ReadFails_KeepsLastAndRetriesAfterFailureTtl`（10 秒後再試）、`ConcurrentRefresh_ReadsOnce`、`Invalidate_ForcesReload`。
    - `FallbacksFor_UsesParentsAndDefault`：`ja`→`en-US`、預設 `th` → `FallbacksFor("ja","th") == ["en-US","th","zh-TW","vi","ja"]`。
  - `InstalledLocalePolicyTests`（`Category=Postgres`；用 `LocaleTestKit.InstallAsync(app, "ja", "日本語", fallback: "en-US")`）：
    - `Options_FollowInstalledTable`：GET 系統政策 `locale.enabled` 的 `options` 是 `zh-TW,en-US,th,vi,ja`；PUT `["zh-TW","ja"]` 之前先 PUT 預設與必填維持繁中 → 200；`locale.default` 可設 `ja`（啟用後）。
    - `UninstalledLocale_Rejected`：沒裝 `ja` 時 PUT `["zh-TW","ja"]` → 400、`只能從以下語系挑選：zh-TW、en-US、th、vi`（同 p3 既有斷言）。
    - `RemovedLocale_TreatedAsDisabled`（§4.3、判斷 11）：裝 `ja`、啟用 `["zh-TW","ja"]` 後直接刪掉 `ja` 那列並 `Invalidate`、讓政策快照過期 → `auth/options` 的 `enabledLocales` 是 `["zh-TW"]`、請求不失敗；log 有 Warning。
    - `NoLocalePack_SameAsP3`：不裝任何語系時 `options` 四語、`Options_IncludesLocaleSettings` 的形狀不變（呼叫同一組斷言）。
  - `PolicyApiTests.LocaleOptions_FromInstalledLocales`：刪 `ThreeLocales` 替身，改用 `LocaleTestKit`（斷言不變）。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~LocaleRegistryTests|FullyQualifiedName~PolicyReaderTests"`；`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~InstalledLocalePolicyTests|FullyQualifiedName~PolicyApiTests"`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；另跑 ApplicationTests `--filter "FullyQualifiedName~CachedSystemPolicyTests|FullyQualifiedName~Polic"`、WebAPITests `--filter "FullyQualifiedName~AuthApiTests|FullyQualifiedName~DemoLoginApiTests|FullyQualifiedName~LocaleApiTests|FullyQualifiedName~LocaleDefaultApiTests"`；PostgreSQL：WebAPITests `(FullyQualifiedName~InstalledLocalePolicyTests)`；`grep -rn "FactoryInstalledLocales\|class ThreeLocales" backend` 無結果
- [ ] **Step 5: Commit**（`feat(i18n): 語系政策的選項改讀已安裝語系（LocaleRegistry）`）

**驗收**：Step 4 全過（含 PostgreSQL）；grep 無結果。

**i4a-1 收尾**：PostgreSQL 全套試跑一次（`FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.ApplicationTests --filter "Category!=SqliteOnly&Category!=Benchmark"`，WebAPITests 同），結果記進交付說明；`formwright-purpose-check` 對 Task 1–4 跑一次。不推分支、不跑 gate。

---

# i4a-2 接線與畫面

### Task 5: 退回鏈接到請求語系與訊息解析點（含上層語系，U3）；`X-Locale` 收已安裝且啟用的新語系

**Files:**
- Modify: `backend/src/DynamicModules.Application/Dynamic/PolicyService/CachedSystemPolicy.cs:25,41,65-72,98,101,151,202`（建構子收 `LocaleRegistry`；`LocaleState` 拿掉 `Fallbacks`；`LocaleFallbacks` → `FallbacksFor(string locale)`；`Display(locale)` 用它；判斷 10）
- Modify: `backend/src/DynamicModules.Application/Common/RequestLocale.cs`（三處 `cache.LocaleFallbacks` → `cache.FallbacksFor(<該 LocaleContext 的語系>)`；類別註解的退回鏈改成判斷 6 的完整鏈）
- Modify: `backend/src/DynamicModules.WebAPI/Controllers/ServiceResultExtensions.cs:13-15`、`backend/src/DynamicModules.WebAPI/Errors/FrozenFieldExceptionHandler.cs:14-15`（改取 `RequestLocale.Current`；沒有 `RequestServices` 的單元測試路徑照舊繁中）
- Modify: `backend/src/DynamicModules.Application/Dynamic/ReportService/ReportRunner.Output.cs:54,75`（`display.LocaleFallbacks ?? [I18nText.FallbackLocale]` 維持；`Display` 已給完整鏈，只確認沒有其他地方自己組鏈）
- 不改：`HeaderCurrentLocale`（已是「在啟用語系就用、否則預設」，清單來源經政策快照已換成已安裝語系；只補測試）；`RequiredTextGuard`（判斷 20）；`ApprovalMessages` 的 `AsI18n`（i4c）。
- Test: `backend/tests/DynamicModules.ApplicationTests/Common/CachedSystemPolicyTests.cs:154-174`、`RequestLocaleTests.cs`、`backend/tests/DynamicModules.ApplicationTests/Dynamic/FieldDisplayTests.cs:35`、`backend/tests/DynamicModules.WebAPITests/Dynamic/LocaleChainApiTests.cs`（加案例，`Category=Postgres`）

- [ ] **Step 1: 寫失敗的測試**
  - `CachedSystemPolicyTests.FallbacksFor_*`（取代 `LocaleFallbacks_DefaultFirst`）：出廠 `FallbacksFor("th") == ["zh-TW","en-US","th","vi"]`；預設 `en-US` 時 `["en-US","zh-TW","th","vi"]`；預設 `th` 時 `["th","zh-TW","en-US","vi"]`。
  - `RequestLocaleTests`：既有斷言改成完整鏈（`["en-US","zh-TW","th","vi"]`，各情況相同）；新案 `InstalledJapanese_ChainHasParentThenDefault`：裝 `ja`（上層 `en-US`）、預設 `th`、啟用含 `ja`、標頭 `ja` → `Current.Locale == "ja"`、`Fallbacks == ["en-US","th","zh-TW","vi","ja"]`；`For("ja")` 同；`ja` 沒啟用時 `For("ja")` → `SystemDefault`（D4）。
  - `LocaleChainApiTests`（`Category=Postgres`）：
    - `Japanese_MissingText_FallsToParentThenDefault`（U3 例）：裝 `ja`（上層 `en-US`）、啟用 `["zh-TW","en-US","th","ja"]`、預設 `th`；模組名 `{zh-TW:"訂單", th:"คำสั่งซื้อ"}`、欄位 `{zh-TW:"客戶", en-US:"Customer"}`；帶 `X-Locale: ja` 匯出 CSV → 表頭 `Customer`（上層）；報表目錄主模組名 `คำสั่งซื้อ`（預設語系），都不是繁中。
    - `Japanese_ErrorMessageInterpolation_UsesChain`：同設定，觸發一個內插欄位標籤的錯誤（沿用 p3 `LocaleLabelTests` 用過的檢查索引訊息），內插的是 `Customer`。
    - `JapaneseNotEnabled_HeaderFallsToDefault`：裝了沒啟用 → `X-Locale: ja` 的錯誤訊息語系是預設語系（p3 判斷 14 照舊）。
    - `Factory_Unchanged`：沒裝語系時 p3 既有的 `LocaleChainApiTests` 全部照過（不改）。
  - `FieldDisplayTests.Option_UsesLocaleFallbacks`：改用 `cache.Display("th")`，結果不變。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~CachedSystemPolicyTests|FullyQualifiedName~RequestLocaleTests|FullyQualifiedName~FieldDisplayTests"`；`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~LocaleChainApiTests"`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；另跑 WebAPITests `--filter "FullyQualifiedName~Locale|FullyQualifiedName~MessageText|FullyQualifiedName~Report|FullyQualifiedName~Export|FullyQualifiedName~Print|FullyQualifiedName~Frozen"`、ApplicationTests `--filter "FullyQualifiedName~LocaleLabelTests|FullyQualifiedName~Report"`；PostgreSQL：WebAPITests `(FullyQualifiedName~LocaleChainApiTests)`；`grep -rn "LocaleFallbacks" backend/src` 只剩 `FieldDisplay.cs`（`FieldDisplayContext` 的屬性名）與 `ReportRunner.Output.cs`（讀那個屬性）
- [ ] **Step 5: Commit**（`feat(i18n): 退回鏈加上層語系並涵蓋全部已安裝語系（U3）`）

**驗收**：Step 4 全過（含 PostgreSQL）；共用案例的鏈與 API 結果一致。

### Task 6: `auth/options` 與生效政策回語系資訊

**Files:**
- Create: `backend/src/DynamicModules.Application/Common/LocaleInfoResponse.cs`：`public sealed record LocaleInfoResponse(string Code, string NativeName, string? QuasarLang, string? FallbackCode)`（判斷 12；`uiHash` i4b 再加）
- Modify: `backend/src/DynamicModules.Application/Dynamic/AccountService/Data/AccountResponses.cs:10-11`（`AuthOptionsResponse` 最後加 `IReadOnlyList<LocaleInfoResponse> Locales`）、`AccountService/Queries/GetAuthOptions/GetAuthOptionsHandler.cs:11,17,20-22`（兩個回傳點都帶；只含 `policies.EnabledLocales`，順序依 registry）
- Modify: `backend/src/DynamicModules.Application/Dynamic/PolicyService/PolicyModels.cs:21`（`EffectivePoliciesResponse` 加 `IReadOnlyList<LocaleInfoResponse> Locales`）、`PolicyHandlers.cs:158-175`（注入 `LocaleRegistry`，回全部已安裝語系；判斷 13）
- Test: `backend/tests/DynamicModules.WebAPITests/AuthApiTests.cs`、`backend/tests/DynamicModules.WebAPITests/Dynamic/PolicyApiTests.cs`、`backend/tests/DynamicModules.WebAPITests/DemoLoginApiTests.cs`（既有 `auth/options` 斷言照過）

- [ ] **Step 1: 寫失敗的測試**
  - `AuthApiTests.Options_IncludesLocaleInfos`（`Category=Postgres`）：未登入 GET → `locales` 四筆，`th` 是 `{code:"th", nativeName:"ไทย", quasarLang:"th", fallbackCode:null}`；`Options_IncludesLocaleSettings`、`Options_DemoLoginDisabled_StillIncludesLocales` 不改並照過。
  - `AuthApiTests.Options_LocaleInfos_OnlyEnabled`：裝 `ja` 未啟用 → `locales` 沒有 `ja`；啟用後有，且 `fallbackCode == "en-US"`、`quasarLang` 是 null。
  - `PolicyApiTests.Effective_IncludesInstalledLocales`（`Category=Postgres`）：一般使用者 GET `policies/effective` → `locales` 含未啟用的 `ja`（判斷 13）；帶 `moduleKey` 也有。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~AuthApiTests|FullyQualifiedName~PolicyApiTests|FullyQualifiedName~DemoLoginApiTests"`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；PostgreSQL：WebAPITests `(FullyQualifiedName~AuthApiTests|FullyQualifiedName~PolicyApiTests)`
- [ ] **Step 5: Commit**（`feat(i18n): 登入前與生效政策回傳語系自稱與上層語系`）

**驗收**：Step 4 全過（含 PostgreSQL）；`enabledLocales` 形狀不變。

### Task 7: 前端語系資訊集中到 `locale-settings.ts`（`useLocales()`）、型別改字串、退回鏈、Quasar 全開

> 開工前先問 Q1（AskUserQuestion），照裁決寫 `auth-guard` 的斷線分支；裁決寫回本計畫「待決」與設計 §18 第 4 點（p3 判斷 15 的說明更新）。

**Files:**
- Modify: `frontend/src/i18n/locale.ts`（刪 `LOCALES`、`isLocale`；`LocaleCode = string`（判斷 14）；`readLocale()` 照判斷 15；Quasar glob `*.js` 與 `applyDocumentLocale`（判斷 17）；`FALLBACK_LOCALE` 保留，Q1 推薦時旁邊加 `FALLBACK_LOCALE_NAME = '繁體中文'` 並註明「伺服器連不上時語言選單唯一的一項」）
- Modify: `frontend/src/i18n/locale-settings.ts`：
  - `export interface LocaleInfo { code: LocaleCode; nativeName: string; quasarLang: string | null; fallbackCode: string | null }`
  - `setLocaleInfos(list: LocaleInfo[])`（整份取代）；`installedLocales(): LocaleCode[]`、`localeInfo(code)`、`nativeName(code)`（找不到回代碼）；`localeChain(locale): LocaleCode[]`（同後端 `LocaleChain.Fallbacks`，判斷 18；沒有資訊時 `[default, 'zh-TW']` 去重）；`useLocales()` 回 `{ installed, enabled, required, defaultLocale }` 四個 `computed`（元件用）。
  - `known`、`toLocaleCodes` 改成「在 `localeInfos` 內」；`applyLocalePolicies(values, locales?)` 多收生效政策的 `locales`。
  - 檔頭註解改：「語系清單與語系政策的唯一入口（設計 §4.4）；登入前資訊來自 `auth/options`（只有啟用語系），登入後來自生效政策（全部已安裝語系）」。
- Modify: `frontend/src/router/auth-guard.ts:21-36`（`setLocaleInfos(o.locales)`；斷線分支照 Q1）、`frontend/src/application/dynamic/session-settings.ts:19-22`、`frontend/src/application/dynamic/queries/use-business-time-watch.ts:24`（把 `eff.locales` 一併交給 `applyLocalePolicies`）
- Modify: `frontend/src/domain/dynamic/types.ts:4-5,329-337`（`Locale = LocaleCode`、`I18nText = Partial<Record<string, string>>`；`AuthOptions` 加 `locales: LocaleInfo[]`）、`frontend/src/domain/dynamic/policy-types.ts:37-40`（`EffectivePolicies` 加 `locales`）
- Modify: `frontend/src/domain/dynamic/i18n-text.ts:1-9`（`resolveI18n` 改 `[locale, ...localeChain(locale)]` 依序取；拿掉 `LOCALES`）、`frontend/src/domain/dynamic/menu-nav.ts:26`（分組鍵「繁中 → 已安裝語系順序第一個有值的」）
- Modify: `frontend/vitest.setup.ts:18-19`（另 `setLocaleInfos` 內建四語，測試用）
- Test: `frontend/src/i18n/locale.vitest.spec.ts`、`locale-settings.vitest.spec.ts`、`frontend/src/domain/dynamic/i18n-text.vitest.spec.ts`（讀 `shared/locale-chain-cases.json`）、`menu-nav.vitest.spec.ts`、`frontend/src/router/auth-guard.vitest.spec.ts`、`frontend/src/application/dynamic/queries/use-business-time-watch.vitest.spec.ts`

- [ ] **Step 1: 寫失敗的測試**
  - `locale-settings`：`setLocaleInfos` 後 `installedLocales()` 依序、`nativeName('ja') === '日本語'`、未知代碼回代碼；`localeChain` 跑共用案例；`applyLocalePolicies` 帶 `locales` 時更新清單、`locale.enabled` 含不在清單的代碼時濾掉。
  - `i18n-text`：共用案例逐案 `resolveI18n(text, locale)` 等於 `expected`（先 `setLocaleInfos`／`setLocaleSettings` 依案例）；出廠四語既有案例照過。
  - `locale`：`readLocale()` 對 localStorage `ja` 回 `ja`、空白回 null；`applyDocumentLocale('ja')`（`quasarLang: 'ja'`）載入 Quasar `ja` 包；`quasarLang: null` 的 `xx` 載入 `en-US` 包；既有 `th` 案例照過。
  - `auth-guard`：`getAuthOptions` 回 `locales` 含 `ja` 時 `installedLocales()` 有 `ja`；記過 `ja` 但 `ja` 沒啟用 → 套用預設（p3 既有語意）；斷線分支照 Q1 裁決（推薦：`enabledLocales()` 只有 `zh-TW`、選單一項 `繁體中文`）。
  - `menu-nav`：分區只有 `ja` 時以 `ja` 分組。
  - `use-business-time-watch`：生效值多了 `ja` → `installedLocales()` 跟著變、不跳提示。
- [ ] **Step 2: 跑測試確認失敗**：`cd frontend && bunx vitest run src/i18n src/domain/dynamic/i18n-text.vitest.spec.ts src/domain/dynamic/menu-nav.vitest.spec.ts src/router src/application/dynamic/queries/use-business-time-watch.vitest.spec.ts --maxWorkers=2`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；`bun run typecheck`（`LocaleCode` 改字串後的型別錯誤逐一修，不加 `as any`）
- [ ] **Step 5: Commit**（`feat(i18n): 前端語系清單改從 API 取，退回鏈與後端共用案例`）

**驗收**：Step 4 全過；typecheck 0 錯。

### Task 8: 前端各使用點改讀 `useLocales()`（語言選單、政策選項自稱、維運通道、兩個多語產生點）

**Files:**
- Modify: `frontend/src/presentation/locale/LocaleSwitcher.vue:4,9`（`enabled` 的每項配 `nativeName`）
- Modify: `frontend/src/domain/dynamic/policy-types.ts:1,59-65`（`isLocalePolicy`、`choiceLabel` 照判斷 19）；`PolicyValueEditor.vue` 不改（經 `choiceLabel`）
- Modify: `frontend/src/presentation/ops/OpsChannelTab.vue:7,26,32`（`localeName` → `nativeName`；選項從 `enabledLocales()`；N3 的「已停用」照舊）
- Modify: `frontend/src/presentation/dynamic/ledger/LedgerTab.vue:21,91`、`frontend/src/presentation/dynamic/approval-designer/graph/designer-graph.ts:67-70`（判斷 16；`designer-graph` 的 `LOCALES` 匯出刪除）
- Modify: `frontend/src/i18n/translate.ts:2,7`（`as LocaleCode` 轉型已無意義，刪掉）
- Test: `frontend/src/presentation/locale/` 的 `LocaleSwitcher` 案例（在 `src/i18n/locale.vitest.spec.ts:63-80`）、`frontend/src/domain/dynamic/policy-types.vitest.spec.ts`、`frontend/src/presentation/policies/PolicyValueEditor.vitest.spec.ts`、`frontend/src/presentation/ops/OpsChannelTab.vitest.spec.ts`、`frontend/src/presentation/dynamic/ledger/LedgerTab.vitest.spec.ts`、`frontend/src/presentation/dynamic/approval-designer/graph/designer-graph.vitest.spec.ts`

- [ ] **Step 1: 寫失敗的測試**
  - `LocaleSwitcher`：預設四語時選項仍是 `['繁體中文','English','ไทย','Tiếng Việt']`（改成不引用 `LOCALES`）；`setLocaleInfos` 加 `ja` 且啟用 → 多一項 `日本語`、點選後 `i18n.locale === 'ja'`。
  - `policy-types`：已安裝含 `ja` 時 `locale.enabled` 的 `isLocalePolicy` 為 true、`choiceLabel(meta,'ja')` 是 `日本語`；`calendar.weekStart` 仍走 `policies.choices.monday`。
  - `PolicyValueEditor`：`locale.enabled` 選項有 `日本語`，不出現 `policies.choices.ja`（設計 §1、§19）。
  - `OpsChannelTab`：啟用 `ja` 時下拉有 `日本語`；已存 `th` 又停用時仍顯示 `ไทย（已停用）`（N3 既有案例照過）。
  - `LedgerTab`、`designer-graph`：已安裝四語時產生四語（既有斷言照過）；已安裝多一個沒有翻譯包的 `ja` 時產生的物件**沒有** `ja` 鍵（判斷 16）。
- [ ] **Step 2: 跑測試確認失敗**：`cd frontend && bunx vitest run src/i18n/locale.vitest.spec.ts src/domain/dynamic/policy-types.vitest.spec.ts src/presentation/policies src/presentation/ops/OpsChannelTab.vitest.spec.ts src/presentation/dynamic/ledger src/presentation/dynamic/approval-designer/graph/designer-graph.vitest.spec.ts --maxWorkers=2`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；`bunx vitest run src/presentation src/pages src/domain src/i18n --maxWorkers=2`；`bun run typecheck`；本計畫「整段驗收」的前端掃描
- [ ] **Step 5: Commit**（`feat(i18n): 語言選單、政策選項與多語產生點改讀已安裝語系`）

**驗收**：Step 4 全過；前端掃描只剩白名單。

### Task 9: 文件、實機走查、gate

**Files:**
- Modify: `docs/roadmap.md`（i4 列進度「i4a 完成」）；`docs/superpowers/specs/2026-11-17-language-packs-design.md` §20 i4a 列後補一行「i4a 完成；`auth/options` 的 `uiHash` 移到 i4b（計畫判斷 12）」，§18 第 4 點補 Q1 裁決；`docs/feature.md` i4 列改 🚧（跑 `feature_checklist.py docs/feature.md --write`）
- 不改：`docs/manual.md`（沒有使用者看得到的變化）、`.claude/rules/*`、`AGENTS.md`（設計 §18 第 11 點排在 i4b、i4c）

- [ ] **Step 1: 整段驗收掃描**（下一節），逐條勾在交付說明。
- [ ] **Step 2: 實機**（`scripts/unattended/local-demo.sh`，390、820、1366、1920）：登入頁語言選單四項、切 English／ไทย 正常；政策設定「地區與語系」三項的選項顯示自稱；維運通道語言下拉；簽核設計器新增節點、分錄沖銷欄位各產生四語；全程不露出鍵名、不橫捲。再用資料庫直接插一列 `ja`（`installed`，上層 `en-US`）並等 60 秒：政策頁的選項出現 `日本語`；啟用後登入頁選單出現 `日本語`（介面文字退回繁中是預期，i4b 才有執行期載入）；刪掉該列前先停用。
- [ ] **Step 3: gate**：推分支前外洩掃描；`bash scripts/unattended/ci-wait.sh <sha>`（含 PostgreSQL 兩步），`exit=0`。
- [ ] **Step 4: `formwright-purpose-check`**（八項，重點：單一來源、空系統、權限 fail closed、無執行期 DDL）
- [ ] **Step 5: Commit**（`docs(i18n): i4a 完成，路線與設計補註`），之後照 `formwright-ship` fast-forward 推 main。

**驗收**：掃描全符合；實機逐項記錄；gate `exit=0`；宗旨檢查無違反。

---

## 整段驗收（設計 §1 寫死清單逐條勾驗）

設計 §1 的 i4a 範圍以 main `2b71e593` 重列；「p3 已做」的列只驗證。屬於 i4b（介面文字 JSON、`locales.vitest.spec.ts`）、i4c（`MessageText`／`ReportText` 四語欄位、`TaskReportFields`、`AsI18n` 改已安裝）、i4f（`app.schema.json`、套件 C# 驗證器放寬）的項目不在此列。

| # | §1 項目 | main 現況 | 處置 | 驗證 |
|---|---|---|---|---|
| 1 | `locale.ts` 的 `LocaleCode` | 四語聯集（`:3`） | 改字串（Task 7） | 掃描 F1 |
| 2 | `locale.ts` 的 `LOCALES` | `:6-11` | 刪除（Task 7） | F2 |
| 3 | Quasar 語言包 glob | 四種（`:28`） | `*.js`（Task 7） | F3 |
| 4 | `PrintTab.vue` 的 `LOCALES` | p3 已改成 `computed(() => enabledLocales())`（`:20`，區域變數同名） | 只驗證 | F2 白名單 |
| 5 | `LedgerTab.vue` 的 `LOCALES` | `:21` 寫死 | Task 8 | F2、F4 |
| 6 | `designer-graph.ts` 的 `LOCALES` | `:68` 寫死並匯出 | Task 8 | F2、F4 |
| 7 | `types.ts` 的 `Locale` | `:4` 四語聯集 | Task 7 | F1 |
| 8 | `I18nTextInput.vue` 的 `others` | p3 已改讀 `enabledLocales()`（`:20`） | 只驗證 | F2 |
| 9 | `PolicyValueEditor` 選項名稱 | p3 改用 `choiceLabel`，但自稱來自寫死 `LOCALES` | Task 8 | F5 |
| 10 | p3 新增的 `LOCALES` 使用點：`locale-settings.ts:7`、`i18n-text.ts:8`、`policy-types.ts:61,65`、`menu-nav.ts:26`、`LocaleSwitcher.vue:9`、`OpsChannelTab.vue:26,32`、`auth-guard.ts:26` | 寫死 | Task 7、8 | F2 |
| 11 | 後端 `ModuleRules` | 經 `UnknownLocale`（`:32`） | Task 2 | B1、B2 |
| 12 | 後端 `CreateGroupValidator`、`RenameGroupValidator` | p3 已拿掉語系檢查 | 只驗證 | B1 |
| 13 | 後端 `AppPackageValidator.V7` | 經 `UnknownLocale`（`:86`） | 改 `FormatLocales`（Task 2） | B1、B3 |
| 14 | 後端 `TaskRuleValidator` | 經 `UnknownLocale`（`:124`） | Task 2 | B1、B2 |
| 15 | 後端 `FieldImportParser` | `SupportedLocales`（`:82`） | `BuiltinLocales.Codes`（Task 2） | B1、B4 |
| 16 | 後端 `MessageText` | `SupportedLocales`（`:485`） | `BuiltinLocales.Codes`（Task 2；i4c 改已安裝） | B1、B4 |
| 17 | 後端 `HeaderCurrentLocale` | p3 已改讀 `EnabledLocales` | 只驗證＋Task 5 的 `ja` 測試 | B5 |
| 18 | `ICurrentLocale` 註解寫死四語 | p3 已改 | 只驗證 | B5 |
| 19 | p3 新增：`PolicyCatalog.FactoryLocales`（`:97`）、`I18nText` 內部（`:12,33,66`） | `SupportedLocales` | Task 2 | B1 |
| 20 | §1 漏列：`AlertMailText.Locales`（`:12`） | 寫死四語 | `BuiltinLocales.Codes`（Task 2） | B4 |
| 21 | 三政策 `Options` 動態 | 出廠實作固定四語 | `LocaleRegistry`（Task 4） | B6 |
| 22 | `auth/options` 加 `locales` | 無 | Task 6 | 測試 |

**後端掃描**（repo 根目錄執行）：

```bash
# B1 支援清單已拿掉
grep -rn "SupportedLocales\|UnknownLocale\|FactoryInstalledLocales" backend/src backend/tests            # 無結果
# B2 格式守衛只有一處
grep -rn "LocaleCode.IsValid" backend/src | grep -v "Domain/Localization/"                             # 只有 I18nText.cs
# B3 套件格式的四語清單只有一處
grep -rn "FormatLocales" backend/src                                                                    # 只在 AppPackageValidator*.cs
# B4 內建語系清單的使用點（白名單）
grep -rln "BuiltinLocales\." backend/src | grep -v "Domain/Localization/"
#   預期恰為：Domain/ValueObjects/I18nText.cs、Domain/Policies/PolicyCatalog.cs、Application/Common/MessageText.cs、
#   Application/Common/LocaleRegistry.cs、Application/Dynamic/ImportService/FieldImportParser.cs、
#   Application/Dynamic/OpsService/Alerts/AlertMailText.cs、Persistence/Seed/BuiltinLocaleSync.cs
# B5 其他寫死的四語陣列
grep -rnE '"zh-TW", ?"en-US", ?"th", ?"vi"' backend/src | grep -v "/Migrations/"                       # 只有 AppPackageValidator.cs 的 FormatLocales（BuiltinLocales 逐列寫，不會命中）
grep -n "四語\|zh-TW、en-US" backend/src/DynamicModules.Application/Common/ICurrentLocale.cs backend/src/DynamicModules.WebAPI/Security/HeaderCurrentLocale.cs   # 無結果
# B6 鏈不再是固定清單
grep -rn "\.LocaleFallbacks\b" backend/src | grep -v "Display/FieldDisplay.cs\|ReportService/ReportRunner.Output.cs"   # 無結果
```

**前端掃描**（`frontend/` 下執行）：

```bash
# F1 型別不再是四語聯集
grep -rnE "'zh-TW' \| 'en-US'" src --include='*.ts' --include='*.vue'                                   # 無結果
# F2 寫死清單
grep -rn "LOCALES" src --include='*.ts' --include='*.vue' | grep -v "spec.ts"                           # 只剩 PrintTab.vue:20 的區域 computed（讀 enabledLocales()）
grep -rnE "\['zh-TW', 'en-US', 'th', 'vi'\]" src --include='*.ts' --include='*.vue' | grep -v "spec.ts\|test-kit\|fixtures"   # 無結果（vitest.setup.ts 在 src 外）
# F3 Quasar 語言包全開
grep -n "quasar/lang/\*\.js" src/i18n/locale.ts                                                          # 1 筆
# F4 多語產生點走已安裝語系
grep -n "installedLocales\|useLocales" src/presentation/dynamic/ledger/LedgerTab.vue src/presentation/dynamic/approval-designer/graph/designer-graph.ts   # 各至少 1 筆
# F5 語系選項不進 policies.choices
grep -n "policies.choices" src/domain/dynamic/policy-types.ts                                            # 只在非語系分支
```

**PostgreSQL 雙跑要求（彙總）**：Task 3 `LocaleRepositoryTests`、`BuiltinLocaleSyncTests`、`MigrationConsistencyTests`；Task 4 `InstalledLocalePolicyTests`；Task 5 `LocaleChainApiTests`；Task 6 `AuthApiTests`、`PolicyApiTests` 的新案例，都標 `Category=Postgres` 並在 PostgreSQL 跑過；i4a-1 收尾跑一次 PostgreSQL 全套；gate 的 PostgreSQL 兩步 `exit=0`。交付說明另列：沒有交易外的 `ExecuteUpdate`／`ExecuteDelete`、沒有 raw SQL、沒有 provider 專屬類別。

## 預估規模

| 批 | Task | 新檔 | 改檔 | 手寫行數（不含 EF 產生的 migration 與 snapshot） |
|---|---|---|---|---|
| i4a-1 | 1 | 6（3 src、2 測試、1 shared） | 0 | ≈ 280（測試約 150） |
| | 2 | 0 | 13 | ≈ 150 |
| | 3 | 7＋migration 4 | 3＋snapshot 2 | ≈ 330 |
| | 4 | 5 | 8 | ≈ 420 |
| | 小計 | 18 | 24 | ≈ 1180 |
| i4a-2 | 5 | 0 | 8 | ≈ 230 |
| | 6 | 1 | 7 | ≈ 150 |
| | 7 | 0 | 15 | ≈ 380 |
| | 8 | 0 | 12 | ≈ 230 |
| | 9 | 0 | 3 | ≈ 20 |
| | 小計 | 1 | 45 | ≈ 1010 |
| 合計 | | 19 | 約 65（有重疊） | ≈ 2200，其中測試約 55% |

超過單批約 800 行的指引，主因是測試（Domain 共用案例、registry 的換新與失敗案例、PostgreSQL 雙跑）。再拆會讓「API 欄位」與「消費端」分在不同批，所以維持兩批；若 i4a-1 的 Task 4 實作超過 500 行，停下回報再拆成「registry」與「接上政策與讀取端濾除」兩個 commit。

## 已知限制（i4a，交付時寫回設計 §15）

- 直接改 `Dynamic_Locale` 又不動 `ModifiedUtc` 時，registry 偵測不到，要重啟或 60 秒後再改一次 `ModifiedUtc`（判斷 3）。
- `auth/options` 還沒有 `uiHash`（i4b）。
- `LedgerTab`、`designer-graph` 只對有前端翻譯包的語系產生文字（i4b 接 `ensureLocaleMessages` 後補齊，判斷 16）。
- 新語系的使用者看到的後端訊息、介面文字仍是繁中或預設語系（i4b、i4c）。

## 自我檢查

1. **設計覆蓋（§20 i4a 列）**：`Dynamic_Locale` 與兩種 migration → Task 3；內建四語同步 → Task 3；`LocaleRegistry`（版本戳記）→ Task 4；`IsValid` 只驗格式與必填（M1）→ Task 2（必填在 p3 已完成）；新退回鏈（含上層語系，U3）→ Task 1、5；`X-Locale` → Task 5；三政策動態選項、自稱 → Task 4、8；`auth/options` 加 `locales` → Task 6；後端 8 處、前端 6 處、`PolicyValueEditor` → 整段驗收表；Quasar glob → Task 7；使用者可見行為無變化 → Global Constraints 與 Task 9 走查。
2. **§4.3** 第 1 點 → Task 4；第 2 點（不能移除啟用中的語系）屬 i4d1（i4a 沒有移除入口）；第 3 點 → 判斷 11、Task 4；第 4 點出廠值不變 → `NoLocalePack_SameAsP3`。
3. **型別一致**：`LocaleCode`（Domain 類別）與前端型別 `LocaleCode` 同名不同物，前端只在 `src/i18n`；`BuiltinLocales.Codes`、`LocaleChain.Fallbacks`、`InstalledLocale`、`LocaleOrigin`、`ILocaleRepository.{ListAsync,StampAsync}`、`LocaleVersionStamp`、`BuiltinLocaleSync.EnsureAsync`、`LocaleRegistry.{Locales,Codes,Find,FallbacksFor,EnsureFreshAsync,Invalidate}`、`InstalledLocaleInfo`、`LocaleInfoResponse`、`CachedSystemPolicy.FallbacksFor`；前端 `LocaleInfo`、`setLocaleInfos`、`installedLocales`、`localeInfo`、`nativeName`、`localeChain`、`useLocales`。
4. **待決**：Q1（Task 7 前問）。

## 使用者裁決（沿用，不再問）

- 2026-11-20：語系包設計 A 區照推薦、B 區顧問裁決照推薦；沒指定語系用預設語系；寫進資料的系統文字用預設語系（N1）；通知退路用預設語系（N2）；停用的現值列出並標「已停用」（N3）；明確指定停用或不認得的語系 → 系統預設語系（D4）。
- 2026-11-17／11-18：U1–U10、M2；不做執行期 DDL、權限不 fail-open、驗證不只做在前端。
