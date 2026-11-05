# 動態模組 POC 第 4 階段：規則引擎、公式、自動編號、下拉連動 實作計畫

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 設計者能在設計器設定條件顯示、條件必填、條件唯讀、跨欄位驗證、下拉連動，以及公式與自動編號欄位。前端即時套用規則，後端存檔時重算一次並以後端為準。

**Architecture:**
- 規則條件沿用列表篩選的 §8 條件語法與 `ConditionParser`，新增 `ConditionUsage.Rule`（允許 `fieldRef`、不要求 `IsFilterable`）。
- 後端 `RuleEngine`（Domain，純函式）算出一筆記錄的規則結果：隱藏、必填、唯讀、可選選項、驗證失敗。前端 `rule-engine.ts` 是同一套語意的 TypeScript 版。兩邊用同一份 `poc/shared/rule-cases.json` 驗證結果一致。
- 公式由 Domain 的 `FormulaParser` 解析成 AST，`FormulaEvaluator` 在存檔時以 `decimal` 求值；不翻成 SQL。自動編號在寫入交易內由 `Dynamic_NumberSequence` 取號。
- `RecordValueProcessor` 是唯一的寫入管線：權限 → 規則唯讀 → 正規化 → 公式 → 規則（必填、選項、驗證）；新增時最後取自動編號。

**Tech Stack:** 沿用第 1–3 階段（見 `docs/superpowers/plans/2026-11-03-phase1-foundation.md` 的 Tech Stack），不加新套件。

**Spec:** `docs/superpowers/specs/2026-11-03-dynamic-modules-design.md`。本階段對應：
- §13.2 第 4 項；
- §8 規則引擎、公式、自動編號（彙總除外）；
- §7 的 `formula`、`autonumber` 兩列；
- §6.2 的 `Rule`、`NumberSequence`；
- §11.2 的 `/modules/{key}/rules`；
- §11.4 第 2–4 步；
- §12.3 的規則求值器；
- §12.4 的規則編輯器；
- §13.1 的 `Targets = ["*"]`。

**前置文件**：第 1、2、3 階段裁決（`docs/superpowers/plans/2026-11-03-phase{1,2,3}-rulings.md`）全部仍然有效。

## 本階段範圍

**做：**
- **規則**：
  - 五種：`Visibility`、`Required`、`ReadOnly`、`Validation`、`OptionFilter`。
  - 設計器的規則分頁。
  - schema 帶規則給前端。
  - 前端即時套用，後端存檔時權威重算。
- **公式欄位**（`formula`）：四則運算、`ROUND`、`IF`、`DATEDIFF`、`DATEADD`、`TODAY`。
  - 建立或修改時檢查循環與型別。
  - 公式定義變更後重算模組內全部記錄。
- **自動編號欄位**（`autonumber`）：
  - 格式如 `REP-{yyyyMM}-{0000}`，可依年或月重置。
  - 新增記錄時取號。
  - 可以當記錄標題欄位。
- **下拉連動**：用 `OptionFilter` 規則實作。
- **設計器 `Config.ReadOnly` 的寫入強制**：第 3 階段延後到本階段。

**不做（留給後續階段）：**
- 彙總欄位（`rollup`）、自動編號的 `scope = "parent"`：第 6 階段。
- 前端即時試算公式：公式只在後端存檔時計算，表單上顯示存檔後的值。spec 也說「存檔時由後端解析成 AST 再求值」。
- 新增自動編號欄位時，替既有記錄補號：既有記錄維持空白（我的判斷：補號的期別與順序都沒有正確答案）。
- 設計器「預覽」分頁：第 7 階段。

## 已定決策（我的判斷，spec 沒說死的部分）

1. **規則目標（`Targets`）**：
   - 字串陣列，元素是 `field:<Field.Id>`、`block:<Block.Id>`，或 `*`（整筆記錄）。
   - `*` 只允許用在 `ReadOnly`。spec §13.1 寫 `Visibility`、`ReadOnly` 都可以，但「整筆記錄隱藏」沒有意義，只有「全單唯讀」有用例。
   - `block:` 只允許用在 `Visibility`、`ReadOnly`，代表區塊內全部欄位。
   - `Required` 只能指向 `field:`。
   - `Validation` 至少一個 `field:` 目標，錯誤訊息掛在目標欄位上。
   - `OptionFilter` 恰好一個 `field:` 目標，且必須是 `enum` 或 `multienum`。
2. **`OptionFilter` 的選項清單**：
   - `Rule` 表多一欄 `OptionIds`（JSON `long[]`），只有 `OptionFilter` 使用。spec §6.2 沒列這一欄，但下拉連動一定要存「剩哪些選項」。
   - 選項必須屬於目標欄位。
3. **多條規則作用在同一目標**：
   - `Visibility`：全部成立才顯示（任一不成立就隱藏）。
   - `Required`、`ReadOnly`：任一成立就生效。
   - `OptionFilter`：成立的規則各自給一組選項，取交集。沒有規則成立時，不限制。
   - `Validation`：每條各自檢查，各自報錯。
4. **條件的求值對象**：
   - `ReadOnly` 用**已儲存的值**求值：修改時看舊記錄，新增時一律不成立。這樣把狀態改成「已結案」的那一次存檔仍然可以寫，存完之後才整單唯讀；前端也照這個語意，用載入時的記錄值算，不用表單即時值。
   - 其餘四種用**這次要存的值**（舊值合併新輸入，再加上公式結果）求值。
5. **空值的比較**（前後端共用、測試案例鎖定）：
   - 左邊欄位沒有值時，只有 `isEmpty`、`ne`、`notIn` 成立，其他運算子一律不成立。
   - `fieldRef` 指向的欄位沒有值時，`ne` 成立，其他一律不成立。
   - 跨欄位驗證「交期不得早於訂單日期」要寫成 `any: [交期 isEmpty, 交期 gte {fieldRef: 訂單日期}]`，否則交期空白會報錯。設計器說明文字會提醒。
6. **`fieldRef`**：
   - 只允許 `eq`、`ne`、`gt`、`gte`、`lt`、`lte`。
   - 兩邊的索引種類必須相同：數字對數字、日期對日期、文字對文字。
   - `fieldRef` 只在規則裡可以用，列表篩選維持拒絕（`列表篩選不支援 fieldRef`）。
7. **規則條件可用的欄位與運算子**：
   - 任何啟用中的欄位，不看 `IsFilterable`；運算子沿用該型別的篩選運算子。
   - `textarea` 例外，另外開放 `isEmpty`、`isNotEmpty`。
   - `formula` 依結果型別使用數字或日期運算子，`autonumber` 用文字運算子。
8. **優先序**（spec §8）：權限隱藏與唯讀 > 規則 > 欄位設定。
   - 權限隱藏的欄位：規則不能讓它顯示；規則的必填、唯讀、選項限制對它都不生效。
   - 被規則隱藏，或因權限、規則、`Config.ReadOnly` 而唯讀的欄位：值保留不清除，必填檢查略過（包括 `Config.Required`）。
   - 送了無權寫入的欄位不算錯，直接忽略，沿用 §11.4 第 2 步。
9. **全單唯讀**（`*` 成立）：
   - 修改請求一律回 400，錯誤鍵 `_record`，訊息 `此記錄目前不可修改`。不是靜默忽略，否則使用者會以為存成功了。
   - 刪除不受規則影響，只看權限。
10. **錯誤鍵與訊息語系**：
    - 驗證失敗訊息取 `MessageI18n` 的 zh-TW（後端不知道使用者語系；前端會先用自己的語系擋下）。
    - 錯誤掛在使用者看得到的第一個目標欄位上；目標全部看不到時，掛在 `_record`。
    - 下拉連動不符的訊息：`選項不在可選範圍`。
11. **schema 帶規則**：
    - 條件引用了我看不到的欄位：整條規則不送，後端照樣權威重算。如果送出，會洩漏欄位 id 和條件值。
    - 目標裡我看不到的欄位或區塊：從目標拿掉；拿到一個不剩（`*` 除外）就不送。
    - `OptionFilter` 的選項以 `opt_<id>` 字串送出。
12. **公式語法**：
    - 欄位引用寫成 `{<Field.Id>}`。運算子 `+ - * /`、括號、一元負號；比較 `= <> < <= > >=` 只能用在 `IF` 的第一個參數。
    - 函式：
      - `ROUND(x, n)`：n 是 0–6 的整數常數。
      - `IF(cond, a, b)`：a、b 型別必須相同。
      - `DATEDIFF(a, b)`：回傳 a − b 的天數。
      - `DATEADD(d, n)`：回傳 d 加 n 天的日期。
      - `TODAY()`：今天的日期。
    - 運算式最長 1000 字元，巢狀深度最多 20 層。
13. **公式的型別與值**：
    - `Config.ResultType` 是 `number`、`currency`、`date` 三選一。
    - 可引用的欄位：
      - 數字：`integer`、`decimal`、`currency`、`percent`，以及數字或金額公式。
      - 日期：`date` 與日期公式。
      - `datetime` 不能引用。
    - 一律用 `decimal` 計算，避免浮點誤差。
    - `percent` 在公式裡以百分比換算：存的是 5，公式讀到 0.05。這樣 spec 的 `ROUND(未稅合計 × 稅率, 0)` 字面就是對的。
    - 數字結果依 `Config.Scale` 四捨五入（預設 2；`ROUND` 另外指定的位數照樣先算）。
    - 任一運算元沒有值、除以零、`IF` 走到的分支沒有值，結果都是沒有值（從 `ValuesJson` 移除）。
14. **公式不洩漏看不到的值**：公式欄位的「傳遞輸入」（含引用公式的輸入）只要有任一欄位對我隱藏，這個公式就也對我隱藏。
    - 判定放在 `PermissionResolver`，一併涵蓋「沒有 `Pricing` 看不到金額」。
    - `ResultType = currency` 的公式本身也受 `Pricing` 遮罩，和 `currency` 欄位一樣。
15. **停用被引用的欄位**：欄位被啟用中的公式或規則引用時，停用回 400，鍵 `Status`，訊息 `此欄位被公式或規則引用，不能停用`。避免公式和規則默默失效。
16. **自動編號格式**：
    - 允許的變數：`{yyyy}`、`{yy}`、`{MM}`、`{yyyyMM}`，以及恰好一個流水號 `{0…0}`（1–10 個 0，代表補零位數）。其他字元原樣輸出。
    - 格式最長 50 字元。
    - 有月份就每月重置，只有年就每年重置，都沒有就永不重置。期別鍵是 `yyyyMM`、`yyyy` 或空字串。
    - 時間取 `IBusinessTime.Now`。流水號超過位數照樣輸出完整數字，不截斷。
17. **`NumberSequence` 表**：
    - 欄位 `ModuleId`、`FieldId`、`Period`、`NextValue`，主鍵 `(FieldId, Period)`。
    - 不存 spec §6.2 的 `Prefix`：前綴是格式的一部分，期別鍵已足以區分序號。
18. **計算順序**：
    - 正規化輸入 → 依相依順序算公式 → 求規則（必填、下拉連動、驗證）→ 新增時取自動編號。
    - 公式排在規則前面，規則才能引用公式結果。spec §11.4 把公式寫在第 4 步，這裡提前；自動編號仍然最後，驗證失敗不會用掉號碼（交易也會回滾）。
19. **系統計算欄位**：`formula`、`autonumber` 不接受輸入（送了就忽略），不能設 `Required`、`DefaultValue`、`ReadOnly`、`MaxLength`。
    - 都可以設 `IsFilterable`、`IsSortable`、`ShowInList`。
    - `formula` 可以設 `Scale`（結果型別是 `date` 時不行）。
20. **記錄標題欄位**：可以是 `text` 或 `autonumber`（訂單範本的標題會是訂單編號）。
21. **規則 API**：
    - `GET /modules/{key}/rules` 回該模組全部規則，依 `Sequence`。
    - `PUT /modules/{key}/rules` 一次送全部規則，在一個交易內整批取代，陣列順序就是 `Sequence`，並遞增 `Module.Version`。
    - 每個模組最多 100 條規則。
    - 設計器權限：GET 要 `Read`，PUT 要 `Edit`。

## Global Constraints

第 1–3 階段計畫的 Global Constraints 全部沿用，另加以下幾條：

- **新表**用一個 EF migration `Phase4Rules` 建立，在 Task 4 產生：`dotnet ef migrations add Phase4Rules --project poc/backend/src/DynamicModules.Persistence --startup-project poc/backend/src/DynamicModules.Persistence`，不手寫。

  | 表 | 欄位 | 鍵 |
  |---|---|---|
  | `Dynamic_Rule` | `Id` long、`ModuleId`、`Kind` nvarchar(20)、`Condition` JSON、`Targets` JSON、`OptionIds` JSON、`MessageI18n` JSON（可 null）、`Sequence` int、`Active` bool，加稽核欄位 | `Id`，索引 `(ModuleId, Sequence)` |
  | `Dynamic_NumberSequence` | `ModuleId`、`FieldId`、`Period` nvarchar(6)、`NextValue` long | `(FieldId, Period)` |

- **列舉**：
  - `FieldType` 加 `Formula`、`AutoNumber`，JSON 名稱 `formula`、`autonumber`。
  - `RuleKind { Visibility, Required, ReadOnly, Validation, OptionFilter }`，JSON camelCase：`visibility`、`required`、`readOnly`、`validation`、`optionFilter`。
- **`FieldConfig` 新屬性**：
  - `Formula`：string?，公式運算式。
  - `ResultType`：string?，`number`、`currency`、`date`。
  - `Format`：string?，自動編號格式。
- **錯誤訊息**（逐字使用）：
  - `此記錄目前不可修改`
  - `選項不在可選範圍`
  - `此欄位被公式或規則引用，不能停用`
  - `公式不正確：{原因}`，原因列在 Task 2。
  - `公式有循環引用`
  - `自動編號格式不正確`
  - `規則最多 100 條`
  - `規則目標不正確`
  - `規則條件不正確：{ConditionParser 的訊息}`
  - `驗證規則必須有訊息`
  - `選項不屬於目標欄位`
  - `系統計算欄位不能設定必填、預設值、唯讀或長度`
  - `記錄標題欄位必須是啟用中的文字或自動編號欄位`：取代第 1 階段的「…文字欄位」訊息。
- **共用測試案例** `poc/shared/rule-cases.json` 是規則語意的唯一真相：後端 xUnit 與前端 vitest 都讀它。改語意時，先改這份檔。
- **前端文字**：
  - i18n 放 `designer.rules.*`、`designer.types.formula`、`designer.types.autonumber`、`designer.formula.*`、`designer.autonumber.*`、`dynamic.recordReadOnly`。
  - 四個語系都要有；th／vi 用英文佔位（第 2 階段 R7）。

## Review Focus

1. **權限隱藏的欄位出現在別人建的規則或公式裡**：
   - 業務看不到「預估費用」。
   - 有一條規則「預估費用 > 1000 時必填備註」，以及一個公式 `{預估費用} * 1.05`。
   - 預期：
     - 業務拿到的 schema 沒有這條規則，也沒有這個公式欄位。
     - 業務存檔時，後端照樣用完整的值算這條規則。
     - 回應裡沒有公式值。
   - 測試：Task 7 的 `Schema_RuleReferencingHiddenField_Omitted`，以及 Task 3 的 `Resolve_FormulaWithHiddenInput_Hidden`。
2. **把狀態改成「已結案」的那一次存檔**：可以存，而且同一次送的其他欄位也會存。下一次修改回 400 `此記錄目前不可修改`。→ Task 6 的 `Update_ReadOnlyAllBecomesTrue_SaveSucceedsThenLocks`。
3. **下拉連動切換上層後，下層的舊值已不在可選範圍**：
   - 只改上層、不動下層時，存檔回 400 `選項不在可選範圍`，前端也在送出前擋下。
   - 既有記錄沒動到這兩個欄位時，修改其他欄位照常可以存。
   - 測試：Task 6 的 `Update_OptionFilterParentChanged_StaleChildRejected`、`Update_OptionFilterUntouched_OtherEditSucceeds`。
4. **公式的輸入是空的或除以零**：結果沒有值、存檔成功，不是 500。→ Task 2 的 `Evaluate_NullOperand_ReturnsNull`、`Evaluate_DivideByZero_ReturnsNull`。
5. **被規則隱藏的必填欄位**：條件不成立時，「外包廠商」是隱藏的，即使 `Config.Required` 為 true，存檔也要成功；條件成立後才必填。→ `rule-cases.json` 的 `hiddenRequiredSkipped` 案例，前後端都跑。

---

## 檔案結構

只列本階段新增或修改的檔案。後端路徑省略 `poc/backend/src/DynamicModules.` 前綴。

```
poc/shared/
  filter-operators.json                    + formula、formula:date、autonumber
  rule-cases.json                          規則語意共用案例（新）

Domain/
  Entities/Dynamic/FieldType.cs            + Formula、AutoNumber
  Entities/Dynamic/{Rule,RuleKind,NumberSequence}.cs
  Entities/Dynamic/Module.cs               + Rules
  ValueObjects/FieldConfig.cs              + Formula、ResultType、Format
  Data/Configurations/{Rule,NumberSequence}Configuration.cs
  FieldTypes/IFieldTypeHandler.cs          + OperatorsFor(Field)
  FieldTypes/Handlers/{Formula,AutoNumber}Handler.cs
  FieldTypes/FieldTypeCapabilities.cs
  Conditions/ConditionNode.cs              FieldCondition + RefField
  Conditions/ConditionParser.cs            + ConditionUsage.Rule
  Formulas/{FormulaNode,FormulaParser,FormulaEvaluator,FormulaGraph}.cs
  Rules/{RuleTarget,RuleEngine,RuleOutcome,ConditionEvaluator}.cs
  AutoNumbers/AutoNumberFormat.cs
  Repositories/Dynamic/INumberSequenceRepository.cs

Application/
  Common/Security/PermissionResolver.cs    公式傳遞隱藏
  Dynamic/FieldService/**                  公式、自動編號設定驗證；停用被引用欄位
  Dynamic/FieldService/FormulaRecalc.cs    公式定義變更後重算
  Dynamic/ModuleService/**                 標題可為 autonumber；載入 Rules
  Dynamic/RuleService/**                   GET/PUT rules
  Dynamic/RecordService/RecordValueProcessor.cs   寫入管線
  Dynamic/RecordService/Commands/**        取號、全單唯讀
  Dynamic/SchemaService/**                 schema 帶規則

Persistence/
  Repositories/Dynamic/{ModuleRepository,NumberSequenceRepository}.cs
  Migrations/<時間戳>_Phase4Rules.cs

WebAPI/Controllers/v3/Dynamic/RulesController.cs

poc/frontend/src/
  domain/dynamic/types.ts                  + formula、autonumber、Rule 型別
  domain/dynamic/rule-engine.ts            前端規則引擎（新）
  presentation/dynamic/field-type-registry.ts
  presentation/dynamic/components/{DynamicForm,DynamicField,DynamicDetail}.vue
  presentation/dynamic/list/{FilterPanel,ConditionEditor}.vue   抽出 ConditionEditor
  presentation/dynamic/designer/FieldEditorDialog.vue
  presentation/dynamic/rules/{RulesTab,RuleEditorDialog}.vue
  presentation/dynamic/designer/schema-fields.ts   ModuleDetail → SchemaField[]
  services/api/dynamic-api.ts              + getRules、putRules
  application/dynamic/queries/rules-queries.ts
  pages/system/dynamic-modules/ModuleEditorPage.vue   + 規則分頁
```

---

## 任務

### Task 1：公式與自動編號的欄位型別

**Files:**
- Modify：
  - `Domain/Entities/Dynamic/FieldType.cs`
  - `Domain/ValueObjects/FieldConfig.cs`
  - `Domain/FieldTypes/{IFieldTypeHandler,FieldTypeRegistry,FieldTypeCapabilities}.cs`
  - `Application/Dynamic/FieldService/Data/FieldRules.cs`
  - `Application/Dynamic/ModuleService/Commands/UpdateModule/UpdateModuleHandler.cs`
  - `poc/shared/filter-operators.json`
- Create：`Domain/FieldTypes/Handlers/{FormulaHandler,AutoNumberHandler}.cs`、`Domain/AutoNumbers/AutoNumberFormat.cs`
- Test：
  - `DomainTests/FieldTypes/IndexAndOperatorTests.cs`
  - `DomainTests/AutoNumbers/AutoNumberFormatTests.cs`
  - `ApplicationTests/` 既有的 AddField 與 UpdateModule 測試檔

**Interfaces:**
- Produces：
  - `IFieldTypeHandler.OperatorsFor(Field field) → IReadOnlyList<FilterOperator>`：預設實作回 `Operators`。`FormulaHandler` 依 `Config.ResultType` 回數字或日期運算子；`ConditionParser` 改呼叫它。
  - `FormulaHandler.ToIndex`：`date` 結果放 `DateValue`，數字結果放 `NumberValue`。
  - `AutoNumberHandler.ToIndex`：放 `TextValue`，運算子是 `TextOperators`。
  - 兩個 handler 的 `Normalize` 都回 `Ok(null)`：系統計算欄位不接受輸入。
  - `AutoNumberFormat.TryParse(string? format, out AutoNumberFormat parsed) → bool`
  - `AutoNumberFormat.PeriodKey(DateTime now) → string`
  - `AutoNumberFormat.Render(DateTime now, long number) → string`
  - `FieldRules.IsComputed(FieldType type) → bool`

- [ ] **Step 1：寫失敗測試**
  - `filter-operators.json` 加三個鍵：
    - `"formula"`：與 `integer` 相同。
    - `"formula:date"`：與 `date` 相同。
    - `"autonumber"`：與 `text` 相同。
  - 既有的對照測試要能處理 `formula:date`：用 `ResultType = "date"` 的欄位呼叫 `OperatorsFor`。
  - `AutoNumberFormatTests`：

    ```csharp
    [Theory]
    [InlineData("REP-{yyyyMM}-{0000}", "2026-11-03", 7, "202611", "REP-202611-0007")]
    [InlineData("SO{yy}{MM}{000}", "2026-01-15", 12, "202601", "SO2601012")]
    [InlineData("Y{yyyy}-{00}", "2026-11-03", 123, "2026", "Y2026-123")]
    [InlineData("NO-{000000}", "2026-11-03", 1, "", "NO-000001")]
    public void Render_And_Period(string fmt, string date, long n, string period, string expected)
    // TryParse 成功；PeriodKey 等於 period；Render 等於 expected

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("NO-{yyyy}")]          // 沒有流水號
    [InlineData("{000}-{00}")]                                              // 兩個流水號
    [InlineData("{00000000000}")]                                           // 11 個 0
    [InlineData("A-{dd}-{000}")]                                            // 不支援的變數
    [InlineData("A-{000")]                                                  // 括號不成對
    public void TryParse_Invalid_ReturnsFalse(string? fmt)
    ```

  - AddField 測試：
    - `AddField_Formula_WithRequired_Rejected`：400，鍵 `Config`，訊息 `系統計算欄位不能設定必填、預設值、唯讀或長度`。
    - `AddField_AutoNumber_BadFormat_Rejected`：400，鍵 `Config.Format`，訊息 `自動編號格式不正確`。
    - `AddField_AutoNumber_Valid_Created`。
  - UpdateModule 測試：
    - `UpdateModule_TitleToAutoNumber_Allowed`。
    - `UpdateModule_TitleToInteger_RejectedWithNewMessage`。
- [ ] **Step 2：跑測試確認失敗**：`cd poc/backend && dotnet test --filter "AutoNumberFormat|IndexAndOperator|AddField|UpdateModule"`。
- [ ] **Step 3：實作**
  - **`FieldRules.ValidateConfig`**，針對 `formula` 與 `autonumber`：
    - 有 `Required`、`ReadOnly`、`DefaultValue` 或 `MaxLength` 就報錯，鍵 `Config`。
    - `autonumber` 用 `AutoNumberFormat.TryParse` 檢查 `Format`。
    - `formula` 只檢查 `ResultType` 是三值之一，以及 `date` 不可設 `Scale`。公式本身要模組 context，留給 Task 2、3。
  - **`FieldTypeCapabilities`**：兩個型別都可以篩選、可以排序。
  - **`UpdateModuleHandler`**：標題欄位接受 `Text` 或 `AutoNumber`。
- [ ] **Step 4：跑測試確認通過**：同上，再跑 `dotnet test` 全部，確認既有測試沒有退步。
- [ ] **Step 5：Commit**：`feat: 公式與自動編號欄位型別`

### Task 2：公式解析與求值

**Files:**
- Create：`Domain/Formulas/{FormulaNode,FormulaParser,FormulaEvaluator,FormulaGraph}.cs`
- Test：`DomainTests/Formulas/{FormulaParserTests,FormulaEvaluatorTests,FormulaGraphTests}.cs`

**Interfaces:**
- Consumes：Task 1 的 `FieldType.Formula`、`FieldConfig.Formula`／`ResultType`／`Scale`。
- Produces：
  - `enum FormulaValueType { Number, Date, Boolean }`
  - `FormulaParser.Parse(string? expression, Module module) → FormulaParseResult(FormulaNode? Node, FormulaValueType Type, IReadOnlySet<long> FieldRefs, string? Error)`：只看啟用中欄位。`Error` 是不含前綴的原因。
  - `FormulaEvaluator.Evaluate(FormulaNode node, Module module, IReadOnlyDictionary<string, JsonElement?> values, DateOnly today) → JsonElement?`
    - 數字結果是 JSON number，日期結果是 `"yyyy-MM-dd"`。
    - 依欄位 `Config.Scale`（預設 2）四捨五入，`MidpointRounding.AwayFromZero`。
  - `FormulaGraph.EvaluationOrder(Module module) → IReadOnlyList<Field>`：啟用中的公式欄位按相依排序。被引用的排前面。
  - `FormulaGraph.HasCycle(Module module, long fieldId, IReadOnlySet<long> newRefs) → bool`：假設 `fieldId` 改成引用 `newRefs`，是否形成循環。

- [ ] **Step 1：寫失敗測試**。`FormulaParserTests` 用一個測試模組，欄位如下：

  | 欄位 | 型別 | 設定 |
  |---|---|---|
  | `{1}` | integer | |
  | `{2}` | currency | |
  | `{3}` | percent | |
  | `{4}` | date | |
  | `{5}` | text | |
  | `{6}` | formula | `{1}*{2}`，number |
  | `{7}` | datetime | |

  - **錯誤原因**（逐字斷言 `Error`）：

    | 輸入 | 原因 |
    |---|---|
    | `""` | `運算式是空的` |
    | `"{1} +"` | `語法錯誤` |
    | `"{99}"` | `欄位 99 不存在或未啟用` |
    | `"{5} + 1"` | `欄位 5 的型別不能用在公式` |
    | `"{7}"` | `欄位 7 的型別不能用在公式` |
    | `"{4} + 1"` | `日期不能直接加減，請用 DATEADD` |
    | `"IF({1}, 1, 2)"` | `IF 的第一個參數必須是比較` |
    | `"IF({1} > 0, 1, TODAY())"` | `IF 兩個結果的型別必須相同` |
    | `"ROUND({1}, 7)"` | `ROUND 的位數必須是 0 到 6 的整數` |
    | `"FOO(1)"` | `不支援的函式 FOO` |
    | `"{1} > 2"` | `比較只能用在 IF 裡` |

  - **成功案例**：
    - `"ROUND({2} * {3}, 0)"`：Number，FieldRefs = {2, 3}。
    - `"DATEDIFF({4}, TODAY())"`：Number。
    - `"DATEADD({4}, 7)"`：Date。
    - `"{6} + 1"`：Number，FieldRefs = {6}。
  - **`FormulaEvaluatorTests`**：
    - `Evaluate_PercentAsFraction`：`{2}=1000`、`{3}=5`，運算式 `ROUND({2}*{3},0)`，結果 50。
    - `Evaluate_DecimalExact`：運算式 `0.1+0.2`，結果 0.3（decimal）。
    - `Evaluate_NullOperand_ReturnsNull`：`{1}` 沒有值。
    - `Evaluate_DivideByZero_ReturnsNull`。
    - `Evaluate_IfBranch`：`IF({1} >= 10, 1, 2)` 在 `{1}=10` 時得 1。
    - `Evaluate_DateDiff`：today 2026-11-03、`{4}=2026-11-10`，`DATEDIFF({4}, TODAY())` 得 7。
    - `Evaluate_DateAdd`：`DATEADD({4}, -10)` 得 `"2026-10-31"`。
    - `Evaluate_ScaleRounding`：欄位 Scale = 0、結果 2.5，得 3。
  - **`FormulaGraphTests`**：
    - `EvaluationOrder_DependencyFirst`：6 引用 1、2；8 引用 6；順序 6 在 8 前面。
    - `HasCycle_SelfAndIndirect_True`：6 引用 6；6 引用 8 而 8 引用 6。
    - `HasCycle_Unrelated_False`。
- [ ] **Step 2：跑測試確認失敗**：`dotnet test --filter Formula`。
- [ ] **Step 3：實作**。用遞迴下降 parser：
  - 先切 token：數字、`{id}`、識別字、運算子、括號、逗號。
  - 優先序：比較 < 加減 < 乘除 < 一元負號 < 原子。
  - 函式名不分大小寫。
  - 深度上限 20，超過回 `運算式太複雜`；超過 1000 字元回 `運算式太長`。
  - 求值時取 `Field.Key` 的值：`percent` 除以 100，日期解析成 `DateOnly`。
  - 公式欄位的值，就是 `values` 裡已經算好的結果；呼叫端照 `EvaluationOrder` 依序寫回。
- [ ] **Step 4：跑測試確認通過**。
- [ ] **Step 5：Commit**：`feat: 公式解析與求值`

### Task 3：公式欄位接上設計器、寫入與權限

**Files:**
- Modify：
  - `Application/Dynamic/FieldService/Commands/{AddField,UpdateField,SetFieldStatus}/*Handler.cs`
  - `Application/Dynamic/RecordService/RecordValueProcessor.cs`
  - `Application/Common/Security/PermissionResolver.cs`
- Create：`Application/Dynamic/FieldService/FormulaRecalc.cs`
- Test：ApplicationTests 的 AddField、UpdateField、SetFieldStatus、CreateRecord、PermissionResolver 測試檔；WebAPITests 新增 `FormulaApiTests.cs`

**Interfaces:**
- Consumes：Task 2 的 `FormulaParser`、`FormulaEvaluator`、`FormulaGraph`。
- Produces：
  - `RecordValueProcessor.ApplyFormulas(Module module, Dictionary<string, JsonElement?> values, DateOnly today) → void`：依 `EvaluationOrder` 算出每個公式並寫回，結果為 null 就移除鍵。Task 6 會把它接進 `Process`。
  - `FormulaRecalc.RecalculateAsync(Module module, IRecordRepository records, FieldTypeRegistry registry, DateOnly today, CancellationToken ct) → Task`：對模組內每筆記錄重算公式、重建索引，並換新 `RowVersion`。

- [ ] **Step 1：寫失敗測試**
  - **`AddField_Formula_InvalidExpression_Rejected`**：400，鍵 `Config.Formula`，訊息 `公式不正確：語法錯誤`。
  - **`AddField_Formula_TypeMismatch_Rejected`**：`ResultType = "date"`，公式是數字，訊息 `公式不正確：結果型別與設定不符`。數字結果可以配 `number` 或 `currency`。
  - **`UpdateField_Formula_Cycle_Rejected`**：訊息 `公式有循環引用`。
  - **`AddField_Formula_RecalculatesExistingRecords`**：先有兩筆記錄，加一個 `{qty}*{price}` 公式欄位，兩筆的值都算好、索引也寫入。
  - **`SetFieldStatus_ReferencedByFormula_Rejected`**：鍵 `Status`，訊息 `此欄位被公式或規則引用，不能停用`。
  - **`Create_FormulaComputed_InputIgnored`**：送了公式欄位的值，存的是計算結果。
  - **`Resolve_FormulaWithHiddenInput_Hidden`**：
    - 公式 A 引用的欄位對群組隱藏 → A 在 `HiddenFieldIds`。
    - 公式 B 引用 A → B 也在 `HiddenFieldIds`。
  - **`Resolve_FormulaWithCurrencyInput_NoPricing_Hidden`**，以及 **`Resolve_CurrencyResultFormula_NoPricing_Hidden`**。
- [ ] **Step 2：跑測試確認失敗**。
- [ ] **Step 3：實作**
  - **AddField／UpdateField**：
    - 公式型別先跑 `FormulaParser.Parse`，失敗時訊息是 `公式不正確：{Error}`。
    - 再檢查結果型別。
    - 再用 `FormulaGraph.HasCycle` 檢查循環。
    - 新增欄位當下，模組裡還沒有自己的 Id，循環只需要檢查 UpdateField。
  - **重算時機**：公式被建立或 `Config.Formula`、`Config.Scale` 改變時，在同一個交易內呼叫 `FormulaRecalc`。
  - **`PermissionResolver`**：
    - 原本的 `HiddenFieldIds` 算完後，照 `EvaluationOrder` 走一遍：引用任一隱藏欄位的公式加入隱藏。
    - `ResultType == "currency"` 在沒有 `Pricing` 時也加入隱藏。
  - **`SetFieldStatus`**：停用時，檢查有沒有啟用中的公式引用它。Task 5 再加上規則的檢查。
  - **`RecordValueProcessor`**：`formula`、`autonumber` 一律視為不可寫，輸入忽略。
  - **`Create`／`UpdateRecordHandler`**：本 task 先在 `Process` 之後呼叫 `ApplyFormulas`，Task 6 再把它整進管線。
- [ ] **Step 4：跑測試確認通過**，再跑全部後端測試。
- [ ] **Step 5：Commit**：`feat: 公式欄位接上設計器、寫入與權限`

### Task 4：自動編號取號與規則資料表

**Files:**
- Create：
  - `Domain/Entities/Dynamic/{Rule,RuleKind,NumberSequence}.cs`
  - `Domain/Data/Configurations/{Rule,NumberSequence}Configuration.cs`
  - `Domain/Repositories/Dynamic/INumberSequenceRepository.cs`
  - `Persistence/Repositories/Dynamic/NumberSequenceRepository.cs`
  - migration `Phase4Rules`
- Modify：
  - `Domain/Entities/Dynamic/Module.cs`：加 `List<Rule> Rules`。
  - `Domain/Data/DynamicDbContext.cs`
  - `Persistence/Repositories/Dynamic/ModuleRepository.cs`：`includeChildren` 一併載入 `Rules`，依 `Sequence` 排序。
  - `Application/Dynamic/RecordService/Commands/CreateRecord/CreateRecordHandler.cs`
  - Persistence 的 DI 註冊。
- Test：`WebAPITests/AutoNumberApiTests.cs`

**Interfaces:**
- Produces：
  - `INumberSequenceRepository.NextAsync(long moduleId, long fieldId, string period, CancellationToken ct) → Task<long>`：第一次回 1，之後遞增。在呼叫端的交易內做讀取加更新。
  - `Rule` 的屬性：`Id`、`ModuleId`、`Kind`、`Condition`（JsonElement）、`Targets`（`List<string>`）、`OptionIds`（`List<long>`）、`MessageI18n`（`I18nText?`）、`Sequence`、`Active`，加稽核欄位。

- [ ] **Step 1：寫失敗測試**
  - **`Create_AutoNumber_IncrementsAndFormats`**：用固定的 `IBusinessTime`（2026-11-03），格式 `REP-{yyyyMM}-{0000}`，連建 3 筆，值依序是 `REP-202611-0001`、`-0002`、`-0003`。
  - **`Create_AutoNumber_ResetsNextMonth`**：把時間改成 2026-12-01，下一筆是 `REP-202612-0001`。
  - **`Create_AutoNumber_InputIgnored`**：送了值，存的是取號結果。
  - **`Update_AutoNumber_Unchanged`**：修改其他欄位，編號不變。
  - **`Create_ValidationFails_NumberNotConsumed`**：必填沒填，回 400；下一次成功的那筆仍然是 `-0001`。
  - **`Create_AutoNumberIsTitle_TitleSet`**：標題欄位是自動編號，`title` 等於編號。
- [ ] **Step 2：跑測試確認失敗**。
- [ ] **Step 3：實作**
  - 實體與 configuration 照 Global Constraints 的表。
  - 產生 migration `Phase4Rules`，用 `dotnet ef migrations add`。
  - `CreateRecordHandler` 在 `Process` 與公式都通過之後，對每個啟用中的 `autonumber` 欄位：
    1. 用 `AutoNumberFormat` 和 `time.Now` 算出期別。
    2. 呼叫 `NextAsync` 取號，用 `Render` 寫入 `values`。
    3. 最後才算標題與索引。
  - 測試替換 `IBusinessTime` 的方式，照既有 WebAPITests 的作法；沒有就在 `TestApp` 加一個可設定的假時鐘。
- [ ] **Step 4：跑測試確認通過**，再跑全部後端測試。
- [ ] **Step 5：Commit**：`feat: 自動編號取號與規則資料表`

### Task 5：規則條件解析與規則設定 API

**Files:**
- Modify：
  - `Domain/Conditions/{ConditionNode,ConditionParser}.cs`
  - `Application/Dynamic/FieldService/Commands/SetFieldStatus/SetFieldStatusHandler.cs`
- Create：
  - `Domain/Rules/RuleTarget.cs`
  - `Application/Dynamic/RuleService/{Commands/SaveRules,Queries/GetRules,Data/RuleResponses}/*`
  - `WebAPI/Controllers/v3/Dynamic/RulesController.cs`
- Test：`DomainTests/Conditions/ConditionParserRuleTests.cs`、`WebAPITests/RulesApiTests.cs`

**Interfaces:**
- Produces：
  - `ConditionUsage.Rule`。
  - `FieldCondition` 加一個可選參數 `Field? RefField = null`：有 `fieldRef` 時，`Values` 是空清單。
  - `abstract record RuleTarget`：
    - `FieldTarget(long FieldId)`、`BlockTarget(long BlockId)`、`RecordTarget()`。
    - `RuleTarget.TryParse(string s, out RuleTarget target) → bool`：格式 `field:12`、`block:3`、`*`。
  - `SaveRulesRequest(string ModuleKey, List<RuleInput> Rules)`，實作 `IDesignerRequest`（`Edit`）與 `ITransactionalRequest`。
  - `RuleInput(RuleKind Kind, JsonElement Condition, List<string> Targets, List<long>? OptionIds, I18nText? Message, bool Active)`。
  - `RuleResponse(long Id, RuleKind Kind, JsonElement Condition, List<string> Targets, List<long> OptionIds, I18nText? Message, bool Active)`。
  - `GET /modules/{key}/rules` 回 `RuleResponse[]`。
  - `PUT /modules/{key}/rules`：body `{ rules: RuleInput[] }`，回 `RuleResponse[]`。

- [ ] **Step 1：寫失敗測試**
  - **`ConditionParserRuleTests`**：
    - `Rule_FieldRef_Parsed`：`{field: 交期, op: "gte", value: {fieldRef: 訂單日期}}` 得到 `RefField`。
    - `Rule_FieldRef_TypeMismatch_Error`：日期對數字，訊息 `欄位 {id} 的 fieldRef 型別不符`。
    - `Rule_FieldRef_BadOperator_Error`：`contains` 搭 fieldRef，訊息 `欄位 {id} 不支援運算子 contains`。
    - `Rule_NotFilterableField_Allowed`。
    - `Rule_TextareaIsEmpty_Allowed`。
    - `ListFilter_FieldRef_StillRejected`。
  - **`RulesApiTests`**，以 admin 操作：
    - `PutRules_ReplacesAll_AndBumpsVersion`：先 PUT 兩條、再 PUT 一條，GET 只剩一條，且 `Version` 增加。
    - `PutRules_TargetsInvalid_Rejected`，以下各回 400、訊息 `規則目標不正確`，鍵 `rules[i].targets`：
      - `*` 用在 Visibility；
      - `block:` 用在 Required；
      - OptionFilter 有兩個目標；
      - OptionFilter 的目標是 text；
      - 目標欄位不屬於此模組或未啟用。
    - `PutRules_ValidationWithoutMessage_Rejected`：鍵 `rules[i].message`，訊息 `驗證規則必須有訊息`。
    - `PutRules_OptionNotOfTarget_Rejected`：鍵 `rules[i].optionIds`，訊息 `選項不屬於目標欄位`。
    - `PutRules_BadCondition_Rejected`：鍵 `rules[i].condition`，訊息以 `規則條件不正確：` 開頭。
    - `PutRules_Over100_Rejected`：鍵 `rules`。
    - `PutRules_NonDesigner_403`：用 sales01。
    - `SetFieldStatus_ReferencedByRule_Rejected`：欄位被啟用中規則的條件或目標引用。
- [ ] **Step 2：跑測試確認失敗**。
- [ ] **Step 3：實作**
  - **`ConditionParser`**：
    - `Usage == Rule` 時，略過 `IsFilterable` 與 `SupportsFilter` 檢查。
    - 運算子用 `handler.OperatorsFor(field)`，`textarea` 加上兩個空值運算子。
    - `fieldRef` 的值必須是同模組、啟用中的欄位 id，兩邊索引種類要一致：用 `ToIndex` 會填哪個欄位來分類。
    - `withinNextDays`、`withinLastDays` 在規則裡照常支援。
  - **`SaveRulesHandler`**：
    - 依序驗證每一條，錯誤鍵用 `rules[{i}].{屬性}`。
    - 全部通過才刪掉舊規則、寫入新規則，`Sequence` 依陣列順序從 1 開始，最後 `TouchVersion`。
    - 空的條件群組（`{"all": []}`）代表一律成立。
  - **`SetFieldStatus`**：停用時也要檢查啟用中規則的條件欄位，以及 `field:` 目標。
- [ ] **Step 4：跑測試確認通過**，再跑全部後端測試。
- [ ] **Step 5：Commit**：`feat: 規則條件解析與規則設定 API`

### Task 6：後端規則引擎與寫入管線

**Files:**
- Create：
  - `Domain/Rules/{ConditionEvaluator,RuleEngine,RuleOutcome}.cs`
  - `poc/shared/rule-cases.json`
- Modify：
  - `Application/Dynamic/RecordService/RecordValueProcessor.cs`
  - `Application/Dynamic/RecordService/Commands/{CreateRecord,UpdateRecord}/*Handler.cs`
- Test：
  - `DomainTests/Rules/SharedRuleCasesTests.cs`：讀共用案例。
  - `ApplicationTests/` 的 RecordValueProcessor 測試檔。
  - `WebAPITests/RuleWriteTests.cs`

**Interfaces:**
- Consumes：Task 5 的 `ConditionParser`（`Rule` 用途）與 `RuleTarget`；Task 3 的 `ApplyFormulas`。
- Produces：
  - `ConditionEvaluator.Evaluate(ConditionNode? node, IReadOnlyDictionary<string, JsonElement?> values, FieldTypeRegistry registry, DateOnly today) → bool`：null 一律成立。
  - `RuleEngine.Evaluate(Module module, IReadOnlyDictionary<string, JsonElement?> current, IReadOnlyDictionary<string, JsonElement?>? stored, FieldTypeRegistry registry, IReadOnlySet<string> userIds, DateOnly today) → RuleOutcome`：
    - 只看啟用中的規則。
    - 條件解析失敗的規則略過，不讓存檔失敗；設計時已經驗證過，只可能因為資料被直接改動。
  - `RuleOutcome`：
    - `HiddenFieldIds`：`IReadOnlySet<long>`。
    - `RequiredFieldIds`：`IReadOnlySet<long>`。
    - `ReadOnlyFieldIds`：`IReadOnlySet<long>`。
    - `ReadOnlyAll`：bool。
    - `AllowedOptions`：`IReadOnlyDictionary<long, IReadOnlySet<long>>`。
    - `FailedValidations`：`IReadOnlyList<(IReadOnlyList<long> TargetFieldIds, I18nText Message)>`。

- [ ] **Step 1：寫共用案例檔與失敗測試**
  - **`rule-cases.json` 的結構**：

    ```json
    {
      "today": "2026-11-03",
      "fields": [ { "id": 1, "key": "f_1", "type": "enum", "options": [11, 12, 13] }, ... ],
      "blocks": [ { "id": 100, "fieldIds": [1, 2] }, ... ],
      "conditions": [ { "name": "...", "condition": { ... }, "values": { ... }, "expected": true } ],
      "outcomes": [ { "name": "...", "rules": [ { "kind": "...", "condition": {}, "targets": [], "optionIds": [], "message": {"zh-TW": "..."} } ],
                      "values": { ... }, "stored": { ... } | null,
                      "expected": { "hidden": [..], "required": [..], "readOnly": [..], "readOnlyAll": false,
                                    "allowedOptions": { "3": [31, 32] }, "failedValidations": [[4]] } } ]
    }
    ```

  - **`conditions` 至少要涵蓋**：
    - 每種運算子各一個成立、一個不成立的案例（依型別：text、integer、currency、percent、date、datetime、time、boolean、enum、multienum、user、textarea 的空值運算子）。
    - `between` 含端點。
    - `withinNextDays` 與 `withinLastDays` 以 `today` 為基準。
    - 決策 5 的空值規則。
    - `fieldRef` 的日期與數字比較，以及 `fieldRef` 指向空值。
    - 巢狀 `all`／`any`、空群組。
  - **`outcomes` 至少要涵蓋**：
    - `hiddenRequiredSkipped`：Visibility 不成立時，`Config.Required` 的目標也不必填。這一條案例的 field 帶 `"required": true`，`expected.required` 不含它。
    - `visibilityAnd`：兩條 Visibility 一條不成立，目標隱藏。
    - `blockTarget`：block 目標展開成區塊內的欄位。
    - `readOnlyUsesStored`：`stored` 為已結案、`values` 改成草稿，`readOnlyAll` 為 true；`stored` 為 null 時為 false。
    - `optionFilterIntersect`：兩條 OptionFilter 都成立，取交集。
    - `optionFilterNoneTrue`：沒有成立，就沒有 `allowedOptions` 鍵。
    - `validationFails`：跨欄位比較不成立，`failedValidations` 列出目標欄位。
  - **`SharedRuleCasesTests`**：每個案例各是一個 `[Theory]` 資料列，從案例的 `fields` 建出 `Module` 再解析。找檔案的方式照 `IndexAndOperatorTests` 往上層目錄搜尋。
  - **`RuleWriteTests`**（WebAPI，admin 先設好規則）：
    - `Create_ValidationRuleFails_400OnTarget`：錯誤鍵是目標欄位的 key，訊息是 `MessageI18n` 的 zh-TW。
    - `Create_RuleRequired_Enforced`、`Create_RuleHiddenRequired_Skipped`。
    - `Update_ReadOnlyAllBecomesTrue_SaveSucceedsThenLocks`：
      - 把狀態改成「已結案」，同時改備註，回 200，兩個都存了。
      - 再改一次，回 400，鍵 `_record`，訊息 `此記錄目前不可修改`。
    - `Update_RuleReadOnlyField_InputIgnored`。
    - `Update_ConfigReadOnly_InputIgnored`：第 3 階段延後項；`Config.ReadOnly` 欄位送了新值，存的是舊值；新增時套用預設值。
    - `Update_OptionFilterParentChanged_StaleChildRejected`：鍵是下層欄位，訊息 `選項不在可選範圍`。
    - `Update_OptionFilterUntouched_OtherEditSucceeds`。
    - `Create_ValidationTargetHiddenForUser_ErrorOnRecordKey`：以業務身分操作，目標欄位對他隱藏，錯誤鍵是 `_record`。
    - `Create_RuleUsesFormulaResult`：驗證規則「總額 ≤ 100000」引用公式欄位，超過就 400。
- [ ] **Step 2：跑測試確認失敗**。
- [ ] **Step 3：實作**
  - **`ConditionEvaluator`**：
    - 把記錄值用 `handler.ToIndex` 投影成 `IndexValue` 再比較：
      - 文字用 ordinal 比較。
      - `NumberValue`、`DateValue` 直接比。
      - `multienum` 的 `in` 是有交集，`notIn` 是沒有交集。
    - `textarea` 只看是否為空。
    - 空值照決策 5。
    - 相對日期以 `today` 為基準：
      - `withinNextDays` n 是 `today ≤ d ≤ today + n`。
      - `withinLastDays` n 是 `today − n ≤ d ≤ today`。
      - `datetime` 取日期部分。
  - **`RecordValueProcessor.Process`** 改成接收 `RuleContext(DateOnly Today)`，依序：
    1. 權限、`Config.ReadOnly`、系統計算欄位與規則唯讀（`RuleEngine` 以 `stored` 先算一次 `ReadOnly`）都視為不可寫。
    2. 正規化。
    3. `ApplyFormulas`。
    4. 用合併後的值再算一次 `RuleEngine`。
    5. 依結果處理：
       - 檢查必填：設定必填或規則必填，但規則隱藏或不可寫的欄位略過。
       - 檢查下拉連動：只檢查和 `stored` 不同的值；`multienum` 只檢查新加入的選項。
       - 記錄驗證失敗：錯誤鍵依決策 10。
  - **`UpdateRecordHandler`**：`ReadOnlyAll` 時直接回 400，不寫入。
  - **`CreateRecordHandler`**：`stored` 傳 null。
  - 規則需要的 `userIds` 沿用既有的 `ListUserIdsAsync`。
- [ ] **Step 4：跑測試確認通過**，再跑全部後端測試。
- [ ] **Step 5：Commit**：`feat: 規則引擎與寫入管線`

### Task 7：schema 帶規則

**Files:**
- Modify：
  - `Application/Dynamic/SchemaService/Data/SchemaResponses.cs`
  - `Application/Dynamic/SchemaService/Queries/GetModuleSchema/GetModuleSchemaHandler.cs`
- Test：`WebAPITests/` 的 schema 測試檔

**Interfaces:**
- Produces：
  - `ModuleSchemaResponse` 加 `List<SchemaRule> Rules`。
  - `SchemaRule(RuleKind Kind, JsonElement Condition, List<string> Targets, List<string> Options, I18nText? Message)`：`Options` 是 `opt_<id>`；只送啟用中的規則，依 `Sequence`。

- [ ] **Step 1：寫失敗測試**
  - `Schema_IncludesActiveRules`：停用的規則不出現。
  - `Schema_RuleReferencingHiddenField_Omitted`：條件引用業務看不到的金額欄位，業務的 schema 沒有這條規則，admin 有。
  - `Schema_RuleTargetHidden_TargetRemovedOrRuleDropped`。
  - `Schema_OptionFilterOptions_AsOptValues`。
- [ ] **Step 2：跑測試確認失敗**。
- [ ] **Step 3：實作**
  - **條件引用的欄位**：用 `ConditionParser`（`Rule` 用途）解析後，以 `ConditionFields.Collect` 加上 `RefField` 取得。
  - **目標**：`block:` 目標看區塊內是否還有我看得到的欄位，沒有就拿掉。
- [ ] **Step 4：跑測試確認通過**，再跑全部後端測試。
- [ ] **Step 5：Commit**：`feat: schema 帶規則`

### Task 8：前端型別、型別註冊表與欄位設定

**Files:**
- Modify：
  - `domain/dynamic/types.ts`
  - `domain/dynamic/field-capabilities.ts`
  - `presentation/dynamic/field-type-registry.ts`
  - `presentation/dynamic/designer/FieldEditorDialog.vue`
  - `presentation/dynamic/list/FilterPanel.vue`：運算子改用 `operatorsFor(field)`。
  - i18n 四個語系檔
- Test：
  - `field-type-registry.vitest.spec.ts`：對照 `filter-operators.json`。
  - `FieldEditorDialog.vitest.spec.ts`

**Interfaces:**
- Produces：
  - `FieldType` 加 `'formula' | 'autonumber'`。
  - `FieldConfig` 加 `formula?: string`、`resultType?: 'number' | 'currency' | 'date'`、`format?: string`。
  - `RuleKind = 'visibility' | 'required' | 'readOnly' | 'validation' | 'optionFilter'`。
  - `SchemaRule { kind: RuleKind; condition: ConditionNode; targets: string[]; options: string[]; message?: I18nText }`。
  - `ModuleSchema.rules: SchemaRule[]`。
  - `Rule { id?: number; kind: RuleKind; condition: ConditionNode; targets: string[]; optionIds: number[]; message?: I18nText; active: boolean }`：設計器使用。
  - `FieldCondition.value` 可以是 `{ fieldRef: number }`。
  - `operatorsFor(field: SchemaField) → FilterOperator[]`，放在 `field-type-registry.ts`。

- [ ] **Step 1：寫失敗測試**
  - 對照測試：
    - `operatorsFor` 對 `formula`（`resultType = 'date'` 對應 `formula:date`）與 `autonumber` 的結果，等於共用 json。
  - 註冊表：
    - formula 的 `format` 依 `resultType` 走數字、金額或日期格式。
    - 兩個新型別在表單上都是唯讀顯示。
  - `FieldEditorDialog`：
    - 選 `formula` 時，顯示運算式輸入、「插入欄位」下拉與結果型別，並隱藏必填、唯讀、預設值、長度。
    - 插入欄位會把 `{id}` 插在游標位置。
    - 選 `autonumber` 時，顯示格式輸入與即時範例（用今天的日期和流水號 1 算）。
    - 送出的 config 帶 `formula`／`resultType` 或 `format`。
    - 後端回 `Config.Formula` 錯誤時，顯示在運算式輸入下方。
- [ ] **Step 2：跑測試確認失敗**：`cd poc/frontend && bunx vitest run src/presentation/dynamic`。
- [ ] **Step 3：實作**
  - 前端的格式範例只是預覽，不驗證格式，驗證以後端為準。
  - 運算式輸入用 framework 的 `TextareaInput`。說明文字列出可用函式，並提醒 `DATEDIFF(a, b)` 是 a − b。
- [ ] **Step 4：跑測試確認通過**，加跑 `bunx vue-tsc --noEmit`。
- [ ] **Step 5：Commit**：`feat: 前端公式與自動編號欄位`

### Task 9：前端規則引擎

**Files:**
- Create：`domain/dynamic/rule-engine.ts`
- Test：`domain/dynamic/rule-engine.vitest.spec.ts`：讀 `../../../../shared/rule-cases.json`。

**Interfaces:**
- Consumes：Task 8 的型別。
- Produces：
  - `evaluateCondition(node: ConditionNode | undefined, values: Record<string, unknown>, fields: Map<number, SchemaFieldLike>, today: string) → boolean`
  - `evaluateRules(rules: SchemaRule[], current: Record<string, unknown>, stored: Record<string, unknown> | undefined, fields: SchemaFieldLike[], blocks: { id: number; fieldIds: number[] }[], today: string) → RuleState`
  - `SchemaFieldLike = Pick<SchemaField, 'id' | 'key' | 'type'> & { config?: Partial<FieldConfig> }`：共用案例的欄位只有這幾個屬性。
  - `RuleState`：
    - `hidden`、`required`、`readOnly`：`Set<number>`。
    - `readOnlyAll`：`boolean`。
    - `allowedOptions`：`Map<number, Set<string>>`，值是 `opt_<id>`。
    - `failedValidations`：`{ targets: number[]; message?: I18nText }[]`。

- [ ] **Step 1：寫失敗測試**：
  - 用 `it.each` 跑 `conditions` 與 `outcomes` 的每個案例，斷言和 `expected` 相同。
  - 共用案例裡的選項是數字 id，前端比較前轉成 `opt_<id>`。
  - `allowedOptions` 的期望值也照樣轉換。
- [ ] **Step 2：跑測試確認失敗**。
- [ ] **Step 3：實作**：語意照「已定決策」3–10，與 Task 6 的後端逐條對應。
  - 比較規則：
    - 日期用字串比較（`yyyy-MM-dd`）。
    - `datetime` 用 `Date.parse`，相對日期時取日期部分。
    - `time` 用字串比較。
    - 數字直接比。
  - 不讀 `config.required` 以外的設定。
- [ ] **Step 4：跑測試確認通過**。
- [ ] **Step 5：Commit**：`feat: 前端規則引擎`

### Task 10：表單與明細套用規則

**Files:**
- Modify：
  - `presentation/dynamic/components/{DynamicForm,DynamicField,DynamicDetail}.vue`
  - `presentation/dynamic/build-zod-schema.ts`
  - i18n
- Test：
  - `DynamicForm.vitest.spec.ts`
  - `build-zod-schema.vitest.spec.ts`
  - `pages/dynamic/DetailPage.vitest.spec.ts`

**Interfaces:**
- Consumes：
  - Task 9 的 `evaluateRules`。
  - `schema.rules`。
  - 今天的日期：沿用既有的日期工具；沒有就用 `new Date()` 取本機日期的 `yyyy-MM-dd`。
- Produces：`buildZodSchema(schema, mode, existing?, ruleState?)`：多一個可選參數，接收 `RuleState`。

- [ ] **Step 1：寫失敗測試**
  - **`DynamicForm`**：
    - 條件不成立時，Visibility 的目標欄位不顯示；改值讓條件成立後才出現。
    - 被隱藏的必填欄位不擋送出。
    - Required 規則成立後，欄位顯示必填標記，送出時報 `此欄位必填`。
    - Validation 不成立時，送出被擋，訊息顯示在目標欄位，語系照目前語系。
    - OptionFilter 成立時，下層只剩允許的選項。上層改變導致下層舊值不在範圍時，送出被擋，訊息 `選項不在可選範圍`。
    - 修改模式下，ReadOnly 用載入時的記錄值算：
      - 已結案的記錄整張表單唯讀、不顯示儲存鈕，並顯示 `dynamic.recordReadOnly`。
      - 表單內把狀態改成已結案不會立刻鎖住。
    - 伺服器回 `_record` 錯誤時，顯示在表單頂端。
  - **`DetailPage`**：被規則隱藏的欄位不顯示。
- [ ] **Step 2：跑測試確認失敗**。
- [ ] **Step 3：實作**
  - **規則狀態**：
    - `DynamicForm` 用 `computed` 依 `values` 呼叫 `evaluateRules`，`stored` 在修改模式用 `props.record.values`。
    - `RuleState` 傳給 `buildZodSchema`。
    - `buildZodSchema` 用 `superRefine` 疊加規則必填、驗證與下拉連動；被隱藏或唯讀的欄位不檢查必填。
  - **`DynamicField`**：新增 `readonly`、`required`、`allowedOptions` 三個 props。
  - **區塊**：區塊內全部欄位都被隱藏時，整個區塊不顯示。
  - **送出**：沿用既有的只送有變動欄位；被隱藏的欄位如果有變動，照樣送出（決策 8）。
- [ ] **Step 4：跑測試確認通過**，加跑 `bunx vue-tsc --noEmit`。
- [ ] **Step 5：Commit**：`feat: 表單與明細套用規則`

### Task 11：設計器規則分頁

**Files:**
- Create：
  - `presentation/dynamic/list/ConditionEditor.vue`：從 `FilterPanel.vue` 抽出。
  - `presentation/dynamic/rules/{RulesTab,RuleEditorDialog}.vue`
  - `presentation/dynamic/designer/schema-fields.ts`
  - `application/dynamic/queries/rules-queries.ts`
- Modify：
  - `presentation/dynamic/list/FilterPanel.vue`
  - `services/api/dynamic-api.ts`
  - `pages/system/dynamic-modules/ModuleEditorPage.vue`
  - i18n
- Test：
  - `ConditionEditor.vitest.spec.ts`
  - `RulesTab.vitest.spec.ts`
  - `RuleEditorDialog.vitest.spec.ts`
  - `FilterPanel.vitest.spec.ts`：既有測試要照樣通過。

**Interfaces:**
- Consumes：Task 5 的 API，以及 Task 8 的型別與 `operatorsFor`。
- Produces：
  - `ConditionEditor`：
    - props：`fields: SchemaField[]`、`modelValue?: ConditionNode`、`allowFieldRef?: boolean`。
    - emits：`update:modelValue`。
  - `toSchemaFields(module: ModuleDetail) → SchemaField[]`：只取啟用中欄位，選項 `active` 照設計器資料。
  - `dynamicApi.getRules(key) → Promise<Rule[]>`、`dynamicApi.putRules(key, rules: Rule[]) → Promise<Rule[]>`。
  - `useRulesQuery(key)`、`useSaveRulesMutation(key)`：成功後讓 rules、module、schema 的查詢失效。

- [ ] **Step 1：寫失敗測試**
  - **`ConditionEditor`**：
    - 打開 `allowFieldRef` 後，比較運算子可以切成「另一個欄位」，下拉只列索引種類相同的欄位。
    - 產生 `{fieldRef: id}`。
  - **`FilterPanel`** 改用 `ConditionEditor` 後，行為不變。
  - **`RuleEditorDialog`**：
    - 依種類切換目標選擇器：
      - Visibility、ReadOnly 可選欄位與區塊；只有 ReadOnly 多一個「整筆記錄」。
      - Required、Validation 只能選欄位。
      - OptionFilter 只能選一個下拉欄位，選完再勾選允許的選項。
    - Validation 顯示訊息輸入，用 `I18nTextInput`。
    - 送出的 `Rule` 形狀正確。
  - **`RulesTab`**：
    - 列出規則，每列顯示種類、條件摘要、目標。
    - 可以新增、編輯、刪除、上下移、停用。
    - 「儲存」把整份清單 PUT 出去。
    - 後端回 `rules[1].targets` 錯誤時，第 2 列標紅並顯示訊息。
    - 沒有 `designer.edit` 時唯讀。
- [ ] **Step 2：跑測試確認失敗**。
- [ ] **Step 3：實作**
  - `ModuleEditorPage` 加 `rules` 分頁，位置在權限分頁之前。
  - 錯誤對照沿用 `api-error.ts`，以及權限分頁的作法。
  - 條件編輯沿用 `FilterPanel` 現有的單層「全部／任一」UI。
  - 遇到巢狀條件時，同樣退回 JSON 文字編輯。
- [ ] **Step 4：跑測試確認通過**，加跑 `bunx vue-tsc --noEmit` 與 `bunx quasar build`。
- [ ] **Step 5：Commit**：`feat: 設計器規則分頁`

### Task 12：第 4 階段驗收

**Files:**
- Create：
  - `poc/backend/tests/DynamicModules.WebAPITests/Phase4AcceptanceTests.cs`
  - `docs/acceptance/phase4.md`
  - `docs/acceptance/phase4/*.png`
  - `docs/superpowers/plans/2026-11-03-phase4-rulings.md`
- Modify：
  - `README.md`：加驗收連結。
  - spec §15：如有新的待確認事項。

- [ ] **Step 1：寫驗收測試 `Phase4_Scenario`**。照 spec §13.2 Demo 情境二裡第 4 階段能做的部分，以 admin 透過 API 建立「模具維修申請」：
  1. 建立模組與欄位：
     - 申請單號：`autonumber`，`REP-{yyyyMM}-{0000}`，設為標題欄位。
     - 申請日：`date`。
     - 需求日：`date`。
     - 故障類別：`enum`，選項模具損壞、機台異常、外包。
     - 故障細項：`enum`，選項：模具損壞 2 個、機台異常 2 個、外包 1 個。
     - 外包廠商：`text`，`Config.Required = true`。
     - 預估費用：`currency`。
     - 距需求日天數：`formula`，`DATEDIFF({需求日}, {申請日})`，number。
     - 狀態：`enum`，選項草稿、已結案。
  2. PUT 規則：
     - Visibility：故障類別 = 外包 → `field:外包廠商`。
     - Validation：`any: [需求日 isEmpty, 需求日 gte {fieldRef: 申請日}]` → `field:需求日`，訊息 `需求日不得早於申請日`。
     - 3 條 OptionFilter：每個故障類別各一條，限制故障細項。
     - ReadOnly：狀態 = 已結案 → `*`。
  3. 啟用模組。建立記錄，故障類別 = 機台異常、不填外包廠商，回 201：
     - 申請單號是 `REP-{當月}-0001`，`title` 相同。
     - 天數公式算對。
  4. 故障類別 = 外包、不填外包廠商，回 400，鍵是外包廠商。
  5. 需求日早於申請日，回 400，訊息 `需求日不得早於申請日`。
  6. 故障類別 = 機台異常、故障細項選外包的細項，回 400 `選項不在可選範圍`。
  7. 把狀態改成已結案，回 200；再改，回 400 `此記錄目前不可修改`。
  8. 測試內用 `PUT /modules/{key}/permissions` 給 `floor` 群組 `Read`、`Add`、`Edit`，不給 `Pricing`。再加一條 Required 規則：「預估費用 > 1000 → `field:外包廠商`」。以 `floor01` 讀 schema：
     - 沒有引用預估費用的規則。
     - 有外包的 Visibility 規則。
- [ ] **Step 2：跑全部測試**：
  - `cd poc/backend && dotnet test`
  - `cd poc/frontend && bunx vitest run && bunx vue-tsc --noEmit && bunx quasar build`
  - 測試數字記在 `phase4.md`。
- [ ] **Step 3：寫文件**
  - `phase4.md`：自動測試結果，以及實機走查清單（標「待走查」）。
  - rulings 文件：從 ledger 整理本階段的裁決，格式照 `phase3-rulings.md`。
  - README 加一列。
- [ ] **Step 4：實機走一遍**：執行 `poc/start.command`，在瀏覽器走以下情境：
  - **設計器**：
    - 建公式與自動編號欄位；錯誤的公式顯示錯誤訊息。
    - 規則分頁新增上述 5 種規則，儲存後重新整理仍在。
  - **表單**：
    - 選「外包」才出現外包廠商，且必填。
    - 故障細項跟著故障類別變。
    - 需求日早於申請日時，送出前就擋下。
    - 存檔後顯示申請單號與天數。
  - **已結案的記錄**：打開編輯，整張表單唯讀。
  - **`floor01`**：表單照樣套用規則，看不到預估費用。

  每個畫面截一張圖，存到 `docs/acceptance/phase4/`。結果記在 `phase4.md`，逐項標「通過」或「失敗」。
- [ ] **Step 5：Commit**：`test: 第 4 階段驗收`
