# 階段 1c 設計：套件單據能力、附件、列印範本、內建 CRM 與銷售

> 2026-11-07｜路線階段 1 的收尾｜前置：[路線](2026-11-06-erp-roadmap-design.md)、[應用程式套件](2026-11-06-app-package-design.md)、[單據能力](2026-11-06-document-flow-design.md)
> 標「（我的判斷）」的是我代為決定、可以改的。這份文件只寫決定與理由，細節以程式與測試為準。

## 1. 範圍與順序

五段，各自 commit，順序不改；做不完就停在段落邊界。

1. 套件支援單據能力（狀態機、轉換、帶值、報表、ExportApp）。
2. 附件欄位。
3. 列印範本。
4. 內建「CRM 與銷售」應用程式。
5. Demo。

鐵則不變：程式裡不寫業務名詞，CRM 只是 `apps/*.app.json` 加上測試；採購情境只放測試專案。

## 2. 第 1 段：套件格式升到 3

### 2.1 版本

`formatVersion` 提高到 3（單據能力設計 §7 的約定）。讀取器接受 2 與 3：

- 版本 2 的檔案照舊：`workflows`、`conversions`、`reports`、`printTemplates` 必須是空陣列，`config` 不認得 `fill`。這維持 v2 讀取器「保留 section 非空就拒絕」的 fail-closed 語意。
- 版本 3 才能帶這幾個 section 與 `fill`。`dashboards`、`events` 仍是保留 section。
- 寫出（ExportApp）一律用 3。

### 2.2 形狀

全部用代號，不出現資料庫 Id。選項用「選項代號」（模組內唯一），欄位用限定名 `<模組代號>.<欄位代號>`。

- `workflows[]`：`{ module, statusField, initial, states[{option, readOnly}], transitions[{from, to, groups[]}] }`，一模組一個。
- `conversions[]`：`{ code, name, active, source, target, sourceStates[], header{sourceField, copy[]}, lines{sourceModule, targetModule, sourceLineField, quantity{from,to}, copy[]} }`。`code` 就是 `Conversion.Key`（全站唯一，慣例以應用程式 id 當前綴）。
- `reports[]`：`{ code, name, shareWith[], definition }`。`definition` 對應 `ReportDefinition`，欄位引用換成限定名，選項值寫 `opt_<選項代號>`。
- 帶值：欄位 `config.fill = { mode, map[{from, to}] }`，`from` 是被參照模組的限定欄位名，`to` 是本模組的欄位代號。

### 2.3 開啟

沿用既有交易：實例化 → 啟用 → 以下新步驟 → 記帳，任一步失敗整批回滾。

- 帶值在實例化的「預設值」步驟寫入（那時所有欄位都已存在）。指向被排除欄位的對應會被丟掉。
- 啟用之後：狀態機（`SaveWorkflowRequest`）、轉換（`CreateConversionRequest`）、報表（`CreateReportRequest`＋`SetReportSharesRequest`），全部走既有指令，既有驗證照跑。
- **轉移的群組沒有對應時，整條轉移不建並警告**（我的判斷）：轉移的空群組代表「有 Edit 權限的人都可以」，如果把略過的群組拿掉就變成放寬，所以寧可少一條轉移。代價：套件開出來後該轉移沒人能做（含管理員），要事後到設計器補。
- 轉換 `code` 與現有轉換重複時，預覽與開啟回錯誤，不自動改名。
- 新增資料表 `Dynamic_AppReport`（`AppId`、`ReportId`、`Code`），用途是匯出重建 `reports[]` 與刪除應用程式時一併刪除。狀態機屬於模組、轉換以「來源與目標模組都是成員」判定，不另建關聯表。

### 2.4 匯出（ExportApp）

`GET apps/{key}/export`：由目前的成員模組重新產生套件，不保留開啟時的原文。

- 模組代號用 `AppModule.Code`；欄位、區塊、選項沿用範本匯出的 `Field.Key`、`b_<id>`、`o_<id>`。
- 參照目標：成員模組用代號；別的應用程式的模組寫成 `{key: 代號, app}` 並補進 `requires.apps`；既有實體補進 `requires.entities`；指向「不屬於任何應用程式的模組」表達不了，該欄位不匯出並記 warning。
- 補上範本匯出缺的欄位層例外與選項範圍（`fieldAccess`、`optionScopes`），群組以 `Key` 當代號輸出到 `groups[]`。
- 狀態機、轉換、報表、帶值照上面形狀輸出；引用到停用或未匯出的欄位時該項不匯出並記 warning。

### 2.5 刪除

`DeleteApp` 同一交易內多刪三樣：應用程式的報表（`AppReport`）、成員模組間的轉換（既有的 `ModuleRemover` 已會刪）、狀態機（隨模組級聯）。盤點時，屬於這個應用程式的報表不算「外部引用」。

### 2.6 驗收

`AppPackageV3Tests`（WebAPI 整合測試）：

1. v3 套件開啟後，狀態機實際擋非法轉移、轉換實際執行、帶值實際帶、報表建出且分享。
2. 匯出再匯入到另一個空資料庫，上述行為都還在。
3. v2 檔案帶 `workflows` 被拒；`fill` 在 v2 被拒；v3 的引用錯誤整份拒絕；開啟中途失敗不留殘餘。
4. 關閉後：轉換不能執行（目標寫入被擋）、狀態機不能推進；重新開啟後恢復。
5. 重複開啟冪等（409）。
