# 動態模組 POC 第 1 階段：基礎 CRUD 實作計畫

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 管理員可以建模組、加區塊、加欄位和選項並啟用模組；使用者可以在通用頁面新增、修改、刪除、檢視這個模組的記錄，畫面完全由 metadata 長出來。

**Architecture:**
- 後端是 .NET 8，分層為：Domain、Application（MediatR）、Persistence、WebAPI。用 EF Core 搭 SQLite 寫入，值存在 `Record.ValuesJson`，每種欄位型別由一個 `IFieldTypeHandler` 負責正規化與驗證。
- 前端是 Vue 3 + Quasar，分層對應後端的 Clean Architecture 分層。用 `fieldTypeRegistry` 把型別對到輸入元件和 zod 驗證片段，通用頁面依 schema API 渲染。

**Tech Stack:**
- 後端：.NET 8、ASP.NET Core、EF Core 8（`Microsoft.EntityFrameworkCore.Sqlite` 8.0.x）、MediatR 12.4.1、FluentValidation 11.x、Swashbuckle 6.x。
- 後端測試：xUnit、FluentAssertions 7.0.0、`Microsoft.AspNetCore.Mvc.Testing` 8.0.x。
- 前端：Bun 1.4、Vue 3.5、Quasar 2.18、`@quasar/app-vite` 2、TypeScript 5.9 strict、Pinia 3、`@tanstack/vue-query` 5、vee-validate 4、`@vee-validate/zod`、zod 3、vue-i18n 10、axios。
- 前端測試：vitest、`@vue/test-utils`。

**Spec:** `docs/superpowers/specs/2026-11-03-dynamic-modules-design.md`（本階段對應 §13.2 第 1 項，以及 §6、§7、§11、§12 中屬於本階段的部分）

## 本階段範圍

**做：**
- metadata：模組、區塊、欄位、選項的 CRUD，含啟用與停用。
- 16 種欄位型別：`text`、`textarea`、`integer`、`decimal`、`currency`、`percent`、`date`、`datetime`、`time`、`boolean`、`email`、`phone`、`url`、`enum`、`multienum`、`user`。
- schema API 與選單 API。
- 記錄的新增、修改、軟刪除、單筆查詢、簡易分頁列表（依 `Id` 遞減排序，沒有篩選）。
- 模擬身分（`X-User`）。
- 建模組時登記 ProgramCode。
- 前端的通用列表、表單、明細，以及設計器。

**不做（留給後續階段）：**
- `RecordIndex`、篩選、排序、清單檢視（第 2 階段）。
- 權限判定（第 3 階段）：本階段只登記 ProgramCode，不判定權限。
- 規則、`formula`、`autonumber`（第 4 階段）。
- `reference`（第 5 階段）、表頭與明細、`rollup`（第 6 階段）。
- 歷程與匯出入（第 7 階段）、範本（第 8 階段）。

## Global Constraints

- 所有路徑都相對於 repo 根目錄 `dynamic-fields-design/`；後端在 `poc/backend/`，前端在 `poc/frontend/`。
- API 基底路徑是 `/api/v3/dynamic`，路由一律小寫。
- SQLite 檔案是 `poc/backend/data/poc.db`。表名用 `Dynamic_` 前綴，例如 `Dynamic_Module`。
- `Field.Key` 一律 `f_<Field.Id>`，由系統產生，不接受輸入。
- 選項在 JSON 值裡的格式是 `opt_<FieldOption.Id>`，`multienum` 存成這種字串的陣列。
- 多語系 JSON 的鍵固定是 `zh-TW`、`en-US`、`th`、`vi`。`zh-TW` 必填，其他語系缺值時退回 `zh-TW`。
- ProgramCode 格式是 `DYN` 加 5 位數字，總長 8 碼。`DYN00000` 保留給設計器，第一個模組從 `DYN00001` 開始。
- `Module.Status` 只有 `Draft`、`Active`、`Inactive`；`Field.Status` 只有 `Active`、`Inactive`。
- `Module.Key`：格式 `^[a-z][a-z0-9-]{1,39}$`，全系統唯一，建立後不可以改。
- 分頁：請求參數 `CurrentPage`（預設 1）、`PagesSize`（預設 10，上限 200）；回應 `{ items, totalCount, currentPage, pagesSize }`。
- 命令回傳 `ServiceResult`，Controller 映射規則：
  - 成功：200；新增成功回 201。
  - `IsNotFound`：404。
  - `IsConflict`：409。
  - 驗證失敗：400，body 是 `{ traceId, errors: { "<Field.Key 或屬性名>": ["訊息"] } }`。
- 時間一律經過 `IBusinessTime`，業務時區是 Asia/Taipei；禁止在 Application 或 Domain 層直接用 `DateTime.Now`。
- 每張表都有稽核欄位 `CreatedUserId`、`CreatedTime`、`ModifiedUserId`、`ModifiedTime`；`Record` 另有 `DeleteFlag`。
- Controller 只能呼叫 MediatR，不可以 import Persistence。
- 寫入的 Request 實作 `ITransactionalRequest`。
- 文字長度限制是「字元數」，不是位元組數。
- 介面文字、commit 訊息一律繁體中文；程式識別字用英文。
- commit 訊息格式：`<type>: <繁中描述>`，結尾加 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`。
- 本機埠：後端 `http://127.0.0.1:5180`，前端 `http://127.0.0.1:9180`，前端開發伺服器把 `/api` 代理到後端。

## Review Focus

1. **改欄位型別**：`PUT /fields/{id}` 的 body 帶了不同的 `type`，應該回 400「欄位型別建立後不能更改」，原型別不變。→ Task 7 的 `UpdateField_ChangingType_ReturnsValidationError`。
2. **payload 帶了未知或停用欄位的鍵**：應該靜默忽略，記錄照常存檔，那個鍵不會出現在 `ValuesJson`。→ Task 9 的 `SaveRecord_UnknownAndInactiveKeys_AreIgnored`。
3. **停用的選項**：既有記錄保留停用選項，修改其他欄位時應該存檔成功；新記錄選停用選項應該回 400。→ Task 9 的 `SaveRecord_KeepsExistingInactiveOption_RejectsNewOne`。
4. **兩人同時修改同一筆**：帶舊 `rowVersion` 的修改應該回 409，資料不被覆蓋。→ Task 10 的 `PutRecord_StaleRowVersion_Returns409`。
5. **數字欄位收到字串**（例如 `"1,000"`），或日期不是 `yyyy-MM-dd`：應該回 400，錯誤掛在該欄位的 `Field.Key` 下，不可以猜測轉換。→ Task 5 的 `DecimalHandler_RejectsStringAndLocaleFormatted`、`DateHandler_RejectsNonIsoDate`。

---

## 檔案結構

```
poc/backend/
  DynamicModules.sln
  Directory.Build.props                      Nullable、TreatWarningsAsErrors、LangVersion
  src/DynamicModules.Domain/
    Entities/Dynamic/{Module,Block,Field,FieldOption,Record,Program,AppUser}.cs
    Entities/Dynamic/{ModuleStatus,FieldStatus,FieldType}.cs
    ValueObjects/I18nText.cs                 多語系文字＋退回規則
    ValueObjects/FieldConfig.cs              欄位設定（依型別使用其中的屬性）
    FieldTypes/IFieldTypeHandler.cs          正規化與驗證介面
    FieldTypes/FieldTypeRegistry.cs
    FieldTypes/Handlers/*.cs                 每種型別一個處理器
    Repositories/Dynamic/{IModuleRepository,IRecordRepository,IProgramRepository,IUnitOfWork}.cs
    Data/DynamicDbContext.cs                 放在 Domain
    Data/Configurations/*Configuration.cs
  src/DynamicModules.Application/
    Common/{ServiceResult,ITransactionalRequest,PagedRequest,PagedResponse}.cs
    Common/Behaviors/{ValidationBehavior,TransactionBehavior}.cs
    Common/Security/ICurrentUser.cs
    Common/Time/IBusinessTime.cs
    Dynamic/ModuleService/{Commands,Queries}/...
    Dynamic/FieldService/{Commands}/...      區塊、欄位、選項
    Dynamic/SchemaService/Queries/...        schema 與選單
    Dynamic/RecordService/{Commands,Queries}/...
    DependencyInjection.cs
  src/DynamicModules.Persistence/
    Repositories/Dynamic/*.cs
    UnitOfWork.cs
    Migrations/*                             EF migration
    Seed/SeedData.cs
    DependencyInjection.cs
  src/DynamicModules.WebAPI/
    Program.cs
    Controllers/v3/Dynamic/{ModulesController,FieldsController,SchemaController,RecordsController}.cs
    Controllers/ServiceResultExtensions.cs   ServiceResult 映射成 HTTP
    Security/HeaderCurrentUser.cs            從 X-User 取身分
    Time/TaipeiBusinessTime.cs
  tests/DynamicModules.DomainTests/
  tests/DynamicModules.ApplicationTests/     SQLite in-memory
  tests/DynamicModules.WebAPITests/          WebApplicationFactory

poc/frontend/
  package.json  quasar.config.ts  tsconfig.json  vitest.config.ts
  src/boot/{axios,tanstack-query,i18n}.ts
  src/services/api/{http,dynamic-api}.ts     手寫 axios client（POC 用，上線時換成生成的 client）
  src/domain/dynamic/types.ts                schema、記錄、欄位型別的 TS 型別
  src/domain/dynamic/i18n-text.ts            多語系退回
  src/components/framework/*.vue             framework 同名薄包裝
  src/presentation/dynamic/field-type-registry.ts
  src/presentation/dynamic/build-zod-schema.ts
  src/presentation/dynamic/components/{DynamicField,DynamicForm,DynamicDetail}.vue
  src/application/dynamic/queries/{schema,records,modules}-queries.ts
  src/pages/dynamic/{ListPage,FormPage,DetailPage}.vue
  src/pages/system/dynamic-modules/{ModuleListPage,ModuleEditorPage}.vue
  src/layouts/MainLayout.vue                 側邊選單＋身分切換
  src/stores/use-identity-store.ts
  src/router/routes.ts
  src/i18n/{zh-TW,en-US,th,vi}.json
```

---

### Task 1：Repo 與後端骨架

**Files:**
- Create：`.gitignore`，內容 `bin/`、`obj/`、`node_modules/`、`poc/backend/data/*.db*`、`dist/`、`.quasar/`、`*.user`。
- Create：`poc/backend/DynamicModules.sln`、`Directory.Build.props`，以及四個 src 專案、三個 tests 專案。
- Create：`poc/backend/src/DynamicModules.WebAPI/Program.cs`，加一個健康檢查端點 `GET /api/health`，回 `{ "status": "ok" }`。
- Test：`poc/backend/tests/DynamicModules.WebAPITests/HealthTests.cs`

**Interfaces:**
- Produces：
  - `public class Program` 宣告成 `public partial class Program {}`，讓 `WebApplicationFactory<Program>` 用得到。
  - 專案相依：Domain ← Application ← Persistence；WebAPI 參考 Application 與 Persistence。

- [ ] **Step 1：`git init`**，第一次 commit 只放 `.gitignore`、`README.md`、`docs/`。
- [ ] **Step 2：用 `dotnet new` 建 sln 與專案**，`TargetFramework` 是 `net8.0`；`Directory.Build.props` 設 `<Nullable>enable</Nullable>`、`<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`。
- [ ] **Step 3：寫失敗的測試**

```csharp
[Fact]
public async Task Health_ReturnsOk()
{
    using var factory = new WebApplicationFactory<Program>();
    var res = await factory.CreateClient().GetAsync("/api/health");
    res.StatusCode.Should().Be(HttpStatusCode.OK);
    (await res.Content.ReadAsStringAsync()).Should().Contain("\"status\":\"ok\"");
}
```

- [ ] **Step 4：執行** `dotnet test poc/backend --filter Health_ReturnsOk`。預期 FAIL（找不到端點）。
- [ ] **Step 5：在 `Program.cs` 實作端點**，並設定 `LowercaseUrls = true`、Swashbuckle、`Urls = http://127.0.0.1:5180`。
- [ ] **Step 6：執行同一個指令**，預期 PASS。
- [ ] **Step 7：Commit** `chore: 建立 POC 後端骨架`

### Task 2：共用基礎

**Files:**
- Create：`Application/Common/ServiceResult.cs`、`ITransactionalRequest.cs`、`PagedRequest.cs`、`PagedResponse.cs`、`Behaviors/ValidationBehavior.cs`、`Behaviors/TransactionBehavior.cs`、`Security/ICurrentUser.cs`、`Time/IBusinessTime.cs`、`DependencyInjection.cs`。
- Create：`WebAPI/Security/HeaderCurrentUser.cs`、`WebAPI/Time/TaipeiBusinessTime.cs`、`WebAPI/Controllers/ServiceResultExtensions.cs`。
- Test：`ApplicationTests/Common/ServiceResultTests.cs`、`ApplicationTests/Common/ValidationBehaviorTests.cs`、`WebAPITests/ServiceResultMappingTests.cs`。

**Interfaces:**
- Produces：
  - `ServiceResult`：屬性 `IsSuccess`、`IsNotFound`、`IsConflict`、`Message`、`Data`（object?）、`Errors`（`IReadOnlyDictionary<string,string[]>`）。
  - 靜態方法：`Success(object? data = null)`、`Created(object data)`、`NotFound(string message)`、`Conflict(string message)`、`Invalid(IDictionary<string,string[]> errors)`。
  - `ServiceResult<T>` 不做，`Data` 一律是 object。
  - `ITransactionalRequest`：標記介面。
  - `TransactionBehavior<TReq,TRes>` 只包有標記的 Request：`ServiceResult.IsSuccess == false` 時 rollback，例外時也 rollback。
  - `ValidationBehavior` 跑所有 `IValidator<TReq>`，有錯就回 `ServiceResult.Invalid(...)`，不丟例外。條件是 `TRes` 必須是 `ServiceResult`；不是的話就丟 `ValidationException`。
  - `ICurrentUser { string UserId { get; } }`
  - `IBusinessTime { DateTime Now { get; } DateOnly Today { get; } }`
  - `PagedRequest { int CurrentPage = 1; int PagesSize = 10; }`，`PagesSize` 超過 200 時截成 200。
  - `PagedResponse<T>(IReadOnlyList<T> Items, int TotalCount, int CurrentPage, int PagesSize)`
  - `IActionResult ToActionResult(this ServiceResult r, ControllerBase c, string? createdAtAction = null, object? routeValues = null)`
  - `HeaderCurrentUser`：讀 `X-User` header；沒帶或不在種子使用者清單裡就是 `anonymous`。種子使用者在 Task 3 定義。

- [ ] **Step 1：寫失敗的測試**
  - `ValidationBehavior_InvalidRequest_ReturnsInvalidResultWithFieldErrors`：一個 Name 必填的假 Request，Name 為空時，`result.Errors["Name"]` 要有一則訊息，而且 handler 沒被呼叫。
  - `Mapping_Invalid_Returns400WithTraceIdAndErrors`。
  - `Mapping_NotFound_Returns404`、`Mapping_Conflict_Returns409`、`Mapping_Created_Returns201`。
  - 映射測試可以用一個測試專用的 Controller。
- [ ] **Step 2：執行** `dotnet test poc/backend --filter "FullyQualifiedName~Common|FullyQualifiedName~ServiceResultMapping"`。預期 FAIL。
- [ ] **Step 3：實作上列型別。** DI 註冊 MediatR 時用 `AddOpenBehavior`，順序是 Validation → Transaction。
- [ ] **Step 4：執行同一個指令**，預期 PASS。
- [ ] **Step 5：Commit** `feat: 共用結果型別、驗證與交易管線`

### Task 3：實體、DbContext、migration 與種子資料

**Files:**
- Create：`Domain/Entities/Dynamic/*.cs`（共 10 個）、`Domain/Data/DynamicDbContext.cs`、`Domain/Data/Configurations/*Configuration.cs`。
- Create：`Persistence/Migrations/`（由 `dotnet ef migrations add Initial` 產生）、`Persistence/Seed/SeedData.cs`、`Persistence/DependencyInjection.cs`。
- Test：`ApplicationTests/Persistence/SchemaTests.cs`

**Interfaces:**
- Produces（實體，屬性名稱必須照寫）：
  - `Module`：`Id` long、`Key` string、`ProgramCode` string(8)、`NameI18n` I18nText、`Icon` string?、`Section` string?、`TitleFieldId` long?、`Status` ModuleStatus、`Version` int、稽核欄位、`Blocks` 集合。
  - `Block`：`Id`、`ModuleId`、`LabelI18n`、`Sequence` int、`Collapsed` bool、稽核欄位、`Fields` 集合。
  - `Field`：`Id`、`ModuleId`、`BlockId`、`Key` string、`LabelI18n`、`Type` FieldType、`Config` FieldConfig、`Sequence`、`Status` FieldStatus、稽核欄位、`Options` 集合。
  - `FieldOption`：`Id`、`FieldId`、`LabelI18n`、`Color` string?、`Sequence`、`Active` bool。
  - `Record`：`Id` long、`ModuleId`、`Title` string、`OwnerUserId` string、`ValuesJson` string、`RowVersion` string（GUID 字串，每次寫入都換新值）、`DeleteFlag` bool、稽核欄位。
  - `Program`：`ProgramCode`（主鍵）、`NameI18n`、`IsBeta` bool、`Active` bool。
  - `AppUser`：`UserId`（主鍵）、`DisplayName`。這是 POC 的使用者清單。
  - `FieldType` enum 的成員：`Text, Textarea, Integer, Decimal, Currency, Percent, Date, DateTime, Time, Boolean, Email, Phone, Url, Enum, MultiEnum, User`。JSON 序列化成小寫字串：`text`、`multienum`、`datetime`……
- `I18nText` 與 `FieldConfig` 用 EF value converter 存成 JSON 字串欄（`nvarchar(max)`／SQLite `TEXT`），型別在 Task 4 定義。這個 Task 先放只有 `Values` 字典的最小型別，讓 migration 能建出來。
- `Field.Key` 在 SQLite 用「先 Insert 取 Id，再 Update Key」的方式產生，兩步都在同一個交易內。
- 唯一索引：`Module.Key`、`(Field.ModuleId, Field.Key)`。
- 種子資料（`SeedData.EnsureSeededAsync(DynamicDbContext)`，可以重複執行）：
  - 使用者：`admin`（系統管理員）、`sales01`（業務一）、`sales02`（業務二）、`manager01`（業務主管）、`floor01`（現場人員）。
  - `Program`：`DYN00000`，名稱 `{ "zh-TW": "動態模組設計" }`。
- WebAPI 啟動時執行 `Database.MigrateAsync()` 再跑種子；設定 `ConnectionStrings:Dynamic = Data Source=data/poc.db`。

- [ ] **Step 1：寫失敗的測試**
  - `Migrate_CreatesAllDynamicTables`：用 SQLite in-memory 跑 migration，`sqlite_master` 裡要有這 7 張表：`Dynamic_Module`、`Dynamic_Block`、`Dynamic_Field`、`Dynamic_FieldOption`、`Dynamic_Record`、`Dynamic_Program`、`Dynamic_AppUser`。
  - `Seed_IsIdempotent`：種子跑兩次，使用者仍然是 5 筆、`DYN00000` 仍然是 1 筆。
- [ ] **Step 2：執行** `dotnet test poc/backend --filter SchemaTests`。預期 FAIL。
- [ ] **Step 3：實作實體、configurations、DbContext**，再用 `dotnet ef migrations add Initial -p src/DynamicModules.Persistence -s src/DynamicModules.WebAPI` 產生 migration，最後寫 `SeedData`。
- [ ] **Step 4：執行同一個指令**，預期 PASS。
- [ ] **Step 5：Commit** `feat: 動態模組資料表與種子資料`

### Task 4：多語系文字與欄位設定

**Files:**
- Modify：`Domain/ValueObjects/I18nText.cs`、`Domain/ValueObjects/FieldConfig.cs`
- Test：`DomainTests/ValueObjects/I18nTextTests.cs`、`FieldConfigTests.cs`

**Interfaces:**
- Produces：
  - `I18nText`：`IReadOnlyDictionary<string,string> Values`、`string Resolve(string locale)`。`Resolve` 遇到缺值或空字串就退回 `zh-TW`。
  - `static readonly string[] SupportedLocales = ["zh-TW","en-US","th","vi"]`
  - `bool IsValid(out string? error)`：`zh-TW` 為空時無效；出現不支援的語系鍵也無效。
  - `FieldConfig`（record，全部屬性可為 null）：`Required` bool、`ReadOnly` bool、`MaxLength` int?、`Precision` int?、`Scale` int?、`DefaultValue` JsonElement?、`ShowInList` bool。`IsFilterable`、`IsSortable` 先保留給第 2 階段。
  - JSON 用 camelCase。

- [ ] **Step 1：寫失敗的測試**
  - `Resolve_MissingLocale_FallsBackToZhTw`：`{"zh-TW":"訂單"}.Resolve("vi")` 要等於 `"訂單"`。
  - `Resolve_EmptyString_FallsBackToZhTw`
  - `IsValid_EmptyZhTw_ReturnsFalse`
  - `IsValid_UnknownLocale_ReturnsFalse`：例如 `{"zh-TW":"a","ja":"b"}`。
  - `FieldConfig_RoundTripsJson`
- [ ] **Step 2：執行** `dotnet test poc/backend --filter "I18nTextTests|FieldConfigTests"`。預期 FAIL。
- [ ] **Step 3：實作**，並更新 Task 3 的 value converter 用新型別。如果需要重產 migration 就重產；欄位型別不變的話不必。
- [ ] **Step 4：執行同一個指令**，預期 PASS。
- [ ] **Step 5：Commit** `feat: 多語系文字與欄位設定值物件`

### Task 5：欄位型別處理器

**Files:**
- Create：`Domain/FieldTypes/IFieldTypeHandler.cs`、`FieldTypeRegistry.cs`、`Handlers/{Text,Textarea,Integer,Decimal,Currency,Percent,Date,DateTime,Time,Boolean,Email,Phone,Url,Enum,MultiEnum,User}Handler.cs`
- Test：`DomainTests/FieldTypes/*HandlerTests.cs`

**Interfaces:**
- Produces：

```csharp
public interface IFieldTypeHandler {
    FieldType Type { get; }
    // 輸入 null 或 JSON null 一律回 Ok(null)；必填檢查不在這裡，在 Task 9。
    FieldValueResult Normalize(JsonElement? input, Field field, FieldTypeContext ctx);
}
public sealed record FieldValueResult(bool IsValid, JsonElement? Value, string? Error);
public sealed record FieldTypeContext(
    IReadOnlySet<long> ActiveOptionIds,  // 這個欄位目前啟用的選項
    IReadOnlySet<long> AllOptionIds,     // 含停用的選項
    IReadOnlyDictionary<long,int> OptionSequence, // 選項 Id → Sequence，multienum 排序用
    JsonElement? PreviousValue,          // 修改時的舊值；新增時是 null
    IReadOnlySet<string> UserIds);
public sealed class FieldTypeRegistry {
    public FieldTypeRegistry(IEnumerable<IFieldTypeHandler> handlers);
    public IFieldTypeHandler Get(FieldType type); // 找不到就丟 InvalidOperationException
}
```

各型別的正規化規則（錯誤訊息照寫）：

| 型別 | 接受 | 錯誤訊息 |
|---|---|---|
| text、textarea | JSON string；trim 後空字串視為 null；超過 `MaxLength`（text 預設 255，textarea 預設 4000）就錯 | `超過 {n} 個字元` |
| integer | JSON number 且為整數 | `必須是整數` |
| decimal、currency、percent | JSON number；小數位超過 `Scale`（預設 2）就錯 | `必須是數字`、`小數最多 {n} 位` |
| date | 字串，格式 `yyyy-MM-dd` | `日期格式必須是 yyyy-MM-dd` |
| datetime | ISO 8601 字串，含時區位移；正規化成 Asia/Taipei 的 `yyyy-MM-ddTHH:mm:ss+08:00` | `日期時間格式錯誤` |
| time | 字串，格式 `HH:mm` | `時間格式必須是 HH:mm` |
| boolean | JSON true／false | `必須是是或否` |
| email | 字串，符合 `^[^@\s]+@[^@\s]+\.[^@\s]+$` | `Email 格式錯誤` |
| phone | 字串，只含數字、空白、`+-()#`，長度 1 到 30 | `電話格式錯誤` |
| url | 字串，以 `http://` 或 `https://` 開頭的絕對 URI | `網址必須以 http:// 或 https:// 開頭` |
| enum | 字串 `opt_<id>`，id 在 `ActiveOptionIds` 內；或者等於 `PreviousValue`（保留既有的停用選項） | `選項不存在或已停用` |
| multienum | `opt_<id>` 字串陣列；去重並依選項 `Sequence` 排序；每個值都要符合 enum 的規則（逐個比對 `PreviousValue` 陣列） | `選項不存在或已停用` |
| user | 字串，在 `UserIds` 內 | `使用者不存在` |

- [ ] **Step 1：每個處理器寫失敗的測試**，至少涵蓋「合法值」「非法值與錯誤訊息」「null」三種情況。必須有下列這些：
  - `DecimalHandler_RejectsStringAndLocaleFormatted`：`"1,000"` 和 `"3.5"`（字串）都要回 `必須是數字`。
  - `DecimalHandler_ScaleExceeded_ReturnsError`：Scale 2、值 1.234，回 `小數最多 2 位`。
  - `DateHandler_RejectsNonIsoDate`：`"2026/11/03"` 回 `日期格式必須是 yyyy-MM-dd`。
  - `DateTimeHandler_NormalizesToTaipeiOffset`：`"2026-11-03T01:00:00Z"` 要變成 `"2026-11-03T09:00:00+08:00"`。
  - `EnumHandler_InactiveOption_AllowedOnlyWhenUnchanged`
  - `MultiEnumHandler_DedupesAndOrdersBySequence`
  - `TextHandler_WhitespaceOnly_BecomesNull`
  - `Registry_AllSixteenTypesRegistered`：16 個 `FieldType` 成員都能 `Get` 到處理器。
- [ ] **Step 2：執行** `dotnet test poc/backend --filter FieldTypes`。預期 FAIL。
- [ ] **Step 3：逐一實作處理器。** 數字的小數位用 `decimal` 判斷，不用 double。
- [ ] **Step 4：執行同一個指令**，預期 PASS。
- [ ] **Step 5：Commit** `feat: 16 種欄位型別處理器`

### Task 6：模組命令與查詢

**Files:**
- Create：`Application/Dynamic/ModuleService/Commands/{CreateModule,UpdateModule,ActivateModule,DeactivateModule}/{*Request,*Handler,*Validator}.cs`
- Create：`Application/Dynamic/ModuleService/Queries/{GetModules,GetModule}/...`
- Create：`Domain/Repositories/Dynamic/{IModuleRepository,IProgramRepository,IUnitOfWork}.cs`
- Create：`Persistence/Repositories/Dynamic/{ModuleRepository,ProgramRepository}.cs`、`Persistence/UnitOfWork.cs`
- Test：`ApplicationTests/Dynamic/ModuleServiceTests.cs`

**Interfaces:**
- Consumes：Task 2 的 `ServiceResult`、`ITransactionalRequest`、`ICurrentUser`、`IBusinessTime`；Task 3 的實體；Task 4 的 `I18nText.IsValid`。
- Produces：
  - `CreateModuleRequest(string Key, I18nText Name, string? Icon, string? Section)`，實作 `ITransactionalRequest`。成功回 `ServiceResult.Created(ModuleResponse)`。
  - `UpdateModuleRequest(string Key, I18nText Name, string? Icon, string? Section, long? TitleFieldId)`
  - `ActivateModuleRequest(string Key)`、`DeactivateModuleRequest(string Key)`
  - `GetModulesRequest()`：回 `List<ModuleResponse>`，包含所有狀態。
  - `GetModuleRequest(string Key)`：回 `ModuleDetailResponse`，含區塊、欄位、選項，不分狀態。
  - `ModuleResponse(long Id, string Key, string ProgramCode, I18nText Name, string? Icon, string? Section, long? TitleFieldId, string Status, int Version)`
  - `IModuleRepository`：`Task<Module?> GetByKeyAsync(string key, bool includeChildren, CancellationToken ct)`、`Task<bool> KeyExistsAsync(string key, CancellationToken ct)`、`void Add(Module m)`、`Task<List<Module>> ListAsync(CancellationToken ct)`、`Task<List<Module>> ListActiveAsync(CancellationToken ct)`。
  - `IProgramRepository`：`Task<string> NextModuleProgramCodeAsync(CancellationToken ct)`（取 `DYN` 開頭最大值加一，從 `DYN00001` 起）、`void Add(Program p)`。
  - `IUnitOfWork`：`Task CommitAsync(CancellationToken ct)`、`Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken ct)`。
  - `Module.TouchVersion(string userId, DateTime now)`（Domain 實體上的方法）：`Version++`，並更新 `ModifiedUserId` 與 `ModifiedTime`。之後所有 metadata 異動都要呼叫它。
- 建模組的行為：
  - 狀態是 `Draft`，`Version = 1`。
  - 登記 ProgramCode：`Program` 的名稱等於模組名稱，`IsBeta = true`。
  - 自動建一個區塊「基本資料」（`{ "zh-TW": "基本資料", "en-US": "General" }`），在區塊下建一個 `text` 欄位「名稱」（`{ "zh-TW": "名稱", "en-US": "Name" }`，`Required = true`、`ShowInList = true`），並把它設成 `TitleFieldId`。這是照 vtiger 的必要欄位作法。
  - 以上全部在同一個交易內完成。
- 驗證錯誤的鍵與訊息：
  - `Key` 格式不符：`模組代碼只能用小寫英文、數字、連字號，2 到 40 字，英文開頭`。
  - `Key` 重複：回 Conflict `模組代碼已存在`。
  - `Name` 無效：`名稱必須有繁體中文`。
- 啟用時必須有 `TitleFieldId`，而且那個欄位要是 `Active` 狀態的 `text` 型別，否則回 400，鍵 `TitleFieldId`，訊息 `啟用前必須指定記錄標題欄位`。`TitleFieldId` 只能是 `text` 型別，這是本階段的限制。

- [ ] **Step 1：寫失敗的測試**
  - `CreateModule_AssignsSequentialProgramCodes`：建兩個模組，代碼分別是 `DYN00001`、`DYN00002`，`Program` 表裡各有一筆。
  - `CreateModule_CreatesDefaultBlockAndTitleField`
  - `CreateModule_DuplicateKey_ReturnsConflict`
  - `CreateModule_InvalidKey_ReturnsValidationError`：測 `Order`、`1abc`、`a`。
  - `CreateModule_MissingZhTwName_ReturnsValidationError`
  - `ActivateModule_WithoutTitleField_ReturnsValidationError`
  - `UpdateModule_BumpsVersion`
- [ ] **Step 2：執行** `dotnet test poc/backend --filter ModuleServiceTests`。預期 FAIL。
- [ ] **Step 3：實作 Request、Handler、Validator 與 Repository。**
- [ ] **Step 4：執行同一個指令**，預期 PASS。
- [ ] **Step 5：Commit** `feat: 模組建立、修改、啟用與 ProgramCode 登記`

### Task 7：區塊、欄位、選項命令

**Files:**
- Create：`Application/Dynamic/FieldService/Commands/{AddBlock,UpdateBlock,AddField,UpdateField,SetFieldStatus,ReorderFields,AddOption,UpdateOption}/...`
- Test：`ApplicationTests/Dynamic/FieldServiceTests.cs`

**Interfaces:**
- Consumes：Task 6 的 `IModuleRepository` 與 `Module.TouchVersion`。
- Produces（全部實作 `ITransactionalRequest`，成功後都要 `TouchVersion`）：
  - `AddBlockRequest(string ModuleKey, I18nText Label)`：`Sequence` 取最大值加一。
  - `UpdateBlockRequest(long BlockId, I18nText Label, bool Collapsed)`
  - `AddFieldRequest(string ModuleKey, long BlockId, FieldType Type, I18nText Label, FieldConfig Config)`：回 `FieldResponse(long Id, string Key, FieldType Type, I18nText Label, FieldConfig Config, long BlockId, int Sequence, string Status, List<OptionResponse> Options)`。
  - `UpdateFieldRequest(long FieldId, FieldType Type, I18nText Label, FieldConfig Config, long BlockId)`：Type 必須和原本相同，否則回 400，鍵 `Type`，訊息 `欄位型別建立後不能更改`。
  - `SetFieldStatusRequest(long FieldId, FieldStatus Status)`：如果這個欄位是模組的 `TitleFieldId`，就不能停用，回 400，訊息 `記錄標題欄位不能停用`。
  - `ReorderFieldsRequest(long BlockId, List<long> FieldIds)`：依清單順序重寫 `Sequence`；清單和區塊內的欄位不一致時回 400。
  - `AddOptionRequest(long FieldId, I18nText Label, string? Color)`：只有 `enum`、`multienum` 欄位可以加選項，否則回 400 `只有下拉欄位可以有選項`。
  - `UpdateOptionRequest(long OptionId, I18nText Label, string? Color, bool Active)`
- `FieldConfig` 依型別檢查：
  - `MaxLength` 只允許 text 與 textarea，text 上限 4000。
  - `Scale` 只允許 decimal、currency、percent，範圍 0 到 6。
  - `DefaultValue` 必須通過該型別處理器的 `Normalize`。檢查預設值時，選項以「新增欄位當下還沒有選項」處理，所以下拉欄位的預設值只能在加完選項後用 `UpdateField` 設定。

- [ ] **Step 1：寫失敗的測試**
  - `AddField_AssignsKeyFromId`：`Key == $"f_{Id}"`。
  - `AddField_BumpsModuleVersion`
  - `UpdateField_ChangingType_ReturnsValidationError`
  - `SetFieldStatus_TitleField_CannotDeactivate`
  - `AddOption_OnTextField_ReturnsValidationError`
  - `UpdateField_InvalidDefaultValue_ReturnsValidationError`：decimal 欄位的預設值是 `"abc"`。
  - `ReorderFields_MismatchedIds_ReturnsValidationError`
- [ ] **Step 2：執行** `dotnet test poc/backend --filter FieldServiceTests`。預期 FAIL。
- [ ] **Step 3：實作。**
- [ ] **Step 4：執行同一個指令**，預期 PASS。
- [ ] **Step 5：Commit** `feat: 區塊、欄位、選項管理`

### Task 8：schema 與選單查詢

**Files:**
- Create：`Application/Dynamic/SchemaService/Queries/{GetModuleSchema,GetMenu}/...`
- Test：`ApplicationTests/Dynamic/SchemaServiceTests.cs`

**Interfaces:**
- Consumes：Task 6 的 `IModuleRepository`。
- Produces：
  - `GetModuleSchemaRequest(string ModuleKey)`：只有 `Active` 狀態的模組才回，其他回 NotFound。
  - 回傳 `ModuleSchemaResponse(string Key, string ProgramCode, I18nText Name, string? Icon, string TitleFieldKey, int Version, List<SchemaBlock> Blocks)`。
  - `SchemaBlock(long Id, I18nText Label, bool Collapsed, List<SchemaField> Fields)`
  - `SchemaField(string Key, FieldType Type, I18nText Label, FieldConfig Config, List<SchemaOption> Options)`
  - `SchemaOption(string Value, I18nText Label, string? Color, bool Active)`：`Value` 是 `opt_<id>`。
  - 只含 `Active` 的欄位；選項含停用的（標 `Active=false`），讓前端能顯示既有值。區塊與欄位依 `Sequence` 排序，沒有欄位的區塊不回。
  - `GetMenuRequest()`：回 `List<MenuItem(string Key, I18nText Name, string? Icon, string? Section)>`，只含 `Active` 模組，依 `Name.Resolve("zh-TW")` 排序。
  - schema 的 ETag 是 `"<Key>-v<Version>"`。Controller 在 Task 10 處理 `If-None-Match` 回 304。

- [ ] **Step 1：寫失敗的測試**
  - `Schema_DraftModule_ReturnsNotFound`
  - `Schema_ExcludesInactiveFields_IncludesInactiveOptions`
  - `Schema_OrdersBlocksAndFieldsBySequence`
  - `Menu_OnlyActiveModules`
- [ ] **Step 2：執行** `dotnet test poc/backend --filter SchemaServiceTests`。預期 FAIL。
- [ ] **Step 3：實作。**
- [ ] **Step 4：執行同一個指令**，預期 PASS。
- [ ] **Step 5：Commit** `feat: 執行期 schema 與選單`

### Task 9：記錄命令與查詢

**Files:**
- Create：`Application/Dynamic/RecordService/Commands/{CreateRecord,UpdateRecord,DeleteRecord}/...`
- Create：`Application/Dynamic/RecordService/Queries/{GetRecord,GetPagedRecords}/...`
- Create：`Application/Dynamic/RecordService/RecordValueProcessor.cs`
- Create：`Domain/Repositories/Dynamic/IRecordRepository.cs`、`Persistence/Repositories/Dynamic/RecordRepository.cs`
- Test：`ApplicationTests/Dynamic/RecordServiceTests.cs`

**Interfaces:**
- Consumes：Task 5 的 `FieldTypeRegistry`、Task 6 的 `IModuleRepository`、Task 8 的模組必須是 `Active`。
- Produces：
  - `CreateRecordRequest(string ModuleKey, Dictionary<string, JsonElement?> Values)`，實作 `ITransactionalRequest`。
  - `UpdateRecordRequest(long Id, Dictionary<string, JsonElement?> Values, string RowVersion)`，實作 `ITransactionalRequest`；採部分更新，沒帶的鍵保留舊值。
  - `DeleteRecordRequest(long Id, string RowVersion)`：軟刪除，`DeleteFlag = true`。
  - `GetRecordRequest(long Id)`
  - `GetPagedRecordsRequest(string ModuleKey, int CurrentPage, int PagesSize)`：依 `Id` 遞減排序。
  - `DynamicRecordResponse(long Id, string ModuleKey, string Title, Dictionary<string, JsonElement?> Values, string OwnerUserId, string CreatedUserId, DateTime CreatedTime, string ModifiedUserId, DateTime ModifiedTime, string RowVersion)`。`References` 屬性留到第 5 階段再加。
  - `RecordValueProcessor.Process(Module module, IReadOnlyDictionary<string, JsonElement?> input, IReadOnlyDictionary<string, JsonElement?>? previous) → (Dictionary<string, JsonElement?> values, Dictionary<string,string[]> errors)`
  - `IRecordRepository`：`Task<Record?> GetAsync(long id, CancellationToken ct)`（排除已刪除的）、`void Add(Record r)`、`Task<(List<Record> items, int total)> PageAsync(long moduleId, int page, int size, CancellationToken ct)`。
- `RecordValueProcessor` 的處理順序：
  1. 丟掉不認得的鍵與停用欄位的鍵，靜默忽略。
  2. 新增時，沒帶值的欄位套 `Config.DefaultValue`。
  3. 用處理器正規化，錯誤鍵是 `Field.Key`。
  4. 檢查必填：值為 null，或 `multienum` 是空陣列，就回 `此欄位必填`。修改時用「合併後的值」來檢查。
  5. 修改時，停用欄位的舊值原樣保留在 `ValuesJson`，不會被刪除。
- `Title` = 標題欄位正規化後的值，最多 200 字元；值為 null 時存空字串。
- `RowVersion` 不符時回 Conflict `資料已被其他人修改，請重新載入`。
- 模組不是 `Active` 時，新增回 NotFound `模組不存在或未啟用`；查詢與修改既有記錄時，模組 `Inactive` 也回 NotFound（我的判斷：停用就是全部隱藏）。
- 新增時 `OwnerUserId` 等於 `ICurrentUser.UserId`。

- [ ] **Step 1：寫失敗的測試**
  - `SaveRecord_UnknownAndInactiveKeys_AreIgnored`
  - `SaveRecord_KeepsExistingInactiveOption_RejectsNewOne`
  - `CreateRecord_AppliesDefaultValues`
  - `CreateRecord_MissingRequired_ReturnsErrorUnderFieldKey`：`errors["f_<名稱欄 Id>"]` 包含 `此欄位必填`。
  - `CreateRecord_SetsTitleAndOwner`
  - `UpdateRecord_PartialUpdate_KeepsOmittedValues`
  - `UpdateRecord_StaleRowVersion_ReturnsConflict`
  - `UpdateRecord_ChangesRowVersion`
  - `DeleteRecord_ThenGet_ReturnsNotFound`
  - `CreateRecord_InactiveModule_ReturnsNotFound`
  - `GetPaged_OrdersByIdDescAndExcludesDeleted`
- [ ] **Step 2：執行** `dotnet test poc/backend --filter RecordServiceTests`。預期 FAIL。
- [ ] **Step 3：實作。** `ValuesJson` 用 `System.Text.Json` 序列化，鍵依 `Field.Id` 排序，讓存檔結果固定、方便比對。
- [ ] **Step 4：執行同一個指令**，預期 PASS。
- [ ] **Step 5：Commit** `feat: 記錄新增、修改、刪除與查詢`

### Task 10：API 端點與整合測試

**Files:**
- Create：`WebAPI/Controllers/v3/Dynamic/{ModulesController,FieldsController,SchemaController,RecordsController}.cs`
- Test：`WebAPITests/Dynamic/{ModulesApiTests,FieldsApiTests,SchemaApiTests,RecordsApiTests}.cs`、`WebAPITests/TestApp.cs`

**Interfaces:**
- Consumes：Task 6 到 Task 9 的所有 Request。
- Produces（路由全部掛在 `/api/v3/dynamic` 下）：

| 方法與路由 | Request |
|---|---|
| `GET /modules`、`GET /modules/{key}` | GetModules、GetModule |
| `POST /modules`、`PUT /modules/{key}` | CreateModule、UpdateModule |
| `POST /modules/{key}/activate`、`POST /modules/{key}/deactivate` | ActivateModule、DeactivateModule |
| `POST /modules/{key}/blocks`、`PUT /blocks/{id}` | AddBlock、UpdateBlock |
| `POST /modules/{key}/fields`、`PUT /fields/{id}` | AddField、UpdateField |
| `PUT /fields/{id}/status`、`PUT /blocks/{id}/field-order` | SetFieldStatus、ReorderFields |
| `POST /fields/{id}/options`、`PUT /options/{id}` | AddOption、UpdateOption |
| `GET /modules/{key}/schema` | GetModuleSchema（支援 ETag 與 304） |
| `GET /menu` | GetMenu |
| `GET /modules/{key}/records/paged` | GetPagedRecords |
| `POST /modules/{key}/records` | CreateRecord |
| `GET /records/{id}`、`PUT /records/{id}`、`DELETE /records/{id}` | GetRecord、UpdateRecord、DeleteRecord |

- `DELETE` 的 `rowVersion` 放在 query string。
- `TestApp`：`WebApplicationFactory<Program>` 把連線換成共用的 SQLite in-memory（`Data Source=file:test{guid}?mode=memory&cache=shared`，連線保持開啟），並提供 `HttpClient AsUser(string userId)`。
- JSON 設定：camelCase、enum 用小寫字串、忽略 null。

- [ ] **Step 1：寫失敗的整合測試**
  - `EndToEnd_CreateModuleAddFieldActivateCreateRecord`：建模組 → 加一個 decimal 欄位 → 啟用 → 新增記錄 → `GET /records/{id}`，值要相符，`ownerUserId` 等於 `X-User`。
  - `PostRecord_InvalidValue_Returns400WithFieldKeyError`
  - `PutRecord_StaleRowVersion_Returns409`
  - `GetSchema_WithMatchingETag_Returns304`
  - `GetSchema_AfterAddField_ETagChanges`
  - `GetMenu_ReturnsOnlyActive`
  - `PutField_ChangeType_Returns400`
- [ ] **Step 2：執行** `dotnet test poc/backend --filter Dynamic`。預期 FAIL。
- [ ] **Step 3：實作 Controller**，每個 action 都只做 `mediator.Send` 再 `ToActionResult`。
- [ ] **Step 4：執行全部後端測試** `dotnet test poc/backend`，預期全部 PASS。
- [ ] **Step 5：手動確認**：執行 `dotnet run --project poc/backend/src/DynamicModules.WebAPI`，打開 `http://127.0.0.1:5180/swagger`，要看得到上表的端點。
- [ ] **Step 6：Commit** `feat: 動態模組 API 端點`

### Task 11：前端骨架

**Files:**
- Create：`poc/frontend/package.json`、`quasar.config.ts`、`tsconfig.json`、`vitest.config.ts`、`index.html`
- Create：`src/boot/{axios,tanstack-query,i18n}.ts`、`src/services/api/http.ts`、`src/stores/use-identity-store.ts`、`src/layouts/MainLayout.vue`、`src/router/routes.ts`、`src/i18n/{zh-TW,en-US,th,vi}.json`、`src/App.vue`
- Create：`src/domain/dynamic/types.ts`、`src/domain/dynamic/i18n-text.ts`
- Test：`src/domain/dynamic/i18n-text.vitest.spec.ts`、`src/stores/use-identity-store.vitest.spec.ts`

**Interfaces:**
- Produces：
  - `types.ts`：鏡像後端的 `ModuleSchema`、`SchemaBlock`、`SchemaField`、`SchemaOption`、`DynamicRecord`、`PagedResponse<T>`、`FieldType`（16 個小寫字串的 union）、`FieldConfig`、`I18nText = Partial<Record<'zh-TW'|'en-US'|'th'|'vi', string>>`。
  - `resolveI18n(text: I18nText, locale: string): string`：規則和後端一樣，退回 `zh-TW`。
  - `useIdentityStore()`：`userId`（預設 `admin`），用 `localStorage` 保存（包 try/catch）；`setUser(id)`；`users` 是固定的 5 個種子使用者。
  - `http`：axios instance，`baseURL = '/api/v3/dynamic'`；攔截器自動加 `X-User`；錯誤正規化成 `ApiError { status: number, errors?: Record<string,string[]>, message?: string }`。
  - `MainLayout`：
    - 左側選單：上半是 `GET /menu` 產生的「自訂功能」分區；下半是固定的「系統管理 > 動態模組設計」。
    - 右上角是身分切換，切換後 `queryClient.clear()`。
  - 路由：`/dynamic/:moduleKey`、`/dynamic/:moduleKey/new`、`/dynamic/:moduleKey/:id`、`/dynamic/:moduleKey/:id/edit`、`/system/dynamic-modules`、`/system/dynamic-modules/:moduleKey`；`/` 轉到 `/system/dynamic-modules`。
  - 開發伺服器 port 9180，`/api` 代理到 `http://127.0.0.1:5180`。

- [ ] **Step 1：建立 Quasar 專案**，依 Tech Stack 裝套件，`bun install`。
- [ ] **Step 2：寫失敗的測試**
  - `resolveI18n falls back to zh-TW for missing locale`
  - `resolveI18n falls back for empty string`
  - `identity store persists userId and survives storage errors`：mock 一個會丟例外的 `localStorage`，store 仍然可用，預設 `admin`。
- [ ] **Step 3：執行** `cd poc/frontend && bunx vitest run`。預期 FAIL。
- [ ] **Step 4：實作上列檔案。**
- [ ] **Step 5：執行** `bunx vitest run && bunx vue-tsc --noEmit`，預期都通過。
- [ ] **Step 6：Commit** `chore: 建立 POC 前端骨架`

### Task 12：欄位元件與型別註冊表

**Files:**
- Create：`src/components/framework/{TextInput,TextareaInput,MoneyNumberInput,DateSelect,TimeInput,ToggleInput,GenericSelect}.vue`
- Create：`src/presentation/dynamic/field-type-registry.ts`、`src/presentation/dynamic/build-zod-schema.ts`
- Test：`src/presentation/dynamic/build-zod-schema.vitest.spec.ts`、`field-type-registry.vitest.spec.ts`

**Interfaces:**
- Consumes：Task 11 的 `types.ts`、`resolveI18n`。
- Produces：
  - framework 薄包裝元件：名稱比照常見元件庫的元件。統一 props 為 `modelValue`、`label`、`error?: string`、`readonly?: boolean`、`required?: boolean`；`GenericSelect` 多 `options: {value,label,color?,disable?}[]`、`multiple?: boolean`；`MoneyNumberInput` 多 `scale?: number`。內部用 Quasar 的 `q-input`、`q-select`、`q-toggle`、`q-date`、`q-time` 實作。
  - `fieldTypeRegistry: Record<FieldType, { component: Component; props(field: SchemaField, locale: string): Record<string, unknown>; zod(field: SchemaField): ZodTypeAny; format(value: unknown, field: SchemaField, locale: string): string }>`
  - `buildZodSchema(schema: ModuleSchema, mode: 'create'|'edit', existing?: DynamicRecord): ZodObject`：
    - 鍵是 `Field.Key`。
    - 必填欄位：值為 null、空字串、空陣列時，訊息 `此欄位必填`。
    - 其他規則和後端 Task 5 的表一致，錯誤訊息也一樣。
    - 下拉欄位：`edit` 模式時，`existing` 的舊值可以是停用的選項。
  - `format` 規則：
    - `boolean` 顯示 `是`／`否`。
    - `enum`、`multienum` 顯示選項標籤，多個用 `、` 串接。
    - `user` 顯示使用者的顯示名稱，從 identity store 的 `users` 取。
    - `currency` 用千分位，小數位依 `scale`。
    - `percent` 加 `%`。
    - null 顯示空字串。

- [ ] **Step 1：寫失敗的測試**
  - `required text rejects whitespace-only`
  - `decimal rejects more decimals than scale`：scale 2、值 1.234，訊息 `小數最多 2 位`。
  - `date rejects non-ISO`：`2026/11/03`。
  - `enum in edit mode accepts existing inactive option but create mode rejects it`
  - `format renders multienum labels joined by 、`
  - `format renders currency with thousands separator`：`1234567.5`、scale 2 要變成 `1,234,567.50`。
  - `registry covers all 16 field types`
- [ ] **Step 2：執行** `bunx vitest run src/presentation/dynamic`。預期 FAIL。
- [ ] **Step 3：實作。**
- [ ] **Step 4：執行同一個指令**，預期 PASS。
- [ ] **Step 5：Commit** `feat: 欄位元件與型別註冊表`

### Task 13：通用列表、表單、明細頁

**Files:**
- Create：`src/services/api/dynamic-api.ts`、`src/application/dynamic/queries/{schema,records}-queries.ts`
- Create：`src/presentation/dynamic/components/{DynamicField,DynamicForm,DynamicDetail}.vue`
- Create：`src/pages/dynamic/{ListPage,FormPage,DetailPage}.vue`
- Test：`src/presentation/dynamic/components/DynamicForm.vitest.spec.ts`

**Interfaces:**
- Consumes：Task 12 的 `fieldTypeRegistry`、`buildZodSchema`；Task 11 的 `http`。
- Produces：
  - `dynamicApi`：一個後端端點對一個函式，名稱照 Task 10 的 Request 去掉 `Request` 字尾，例如 `getModuleSchema(key)`、`createRecord(key, values)`、`updateRecord(id, values, rowVersion)`。
  - `useModuleSchema(key: Ref<string>)`：staleTime 10 分鐘。
  - `usePagedRecords(key, page, size)`
  - `useRecord(id)`
  - `useSaveRecord()`：成功後 invalidate 該模組的 paged 與單筆查詢。
  - `useDeleteRecord()`
  - `DynamicForm`：props `schema`、`mode`、`record?`；emit `submit(values)`。
    - 每個區塊用 `q-expansion-item` 呈現，`Collapsed` 決定預設收合。
    - 每個欄位用 `DynamicField`，它依 registry 渲染元件。
    - 用 vee-validate 的 `useForm`，搭配 `toTypedSchema(buildZodSchema(...))`。
    - 後端回 400 時，把 `errors` 依 `Field.Key` 回填到 `setErrors`。
  - `ListPage`：
    - `q-table` 的欄位依序是：標題欄位、`ShowInList` 為 true 的欄位、修改時間。
    - 伺服器分頁，每頁 20 筆。
    - 點列進入明細；右上角有「新增」按鈕。
    - POC 用 `q-table`；上線時換成 framework 的 `DataTable`，這點要在檔案頂端加註解說明。
  - `DetailPage`：依區塊顯示 `format` 後的值，有「編輯」「刪除」按鈕；刪除前用 `q-dialog` 確認。
  - 送出時遇到 409，用 `Notify` 顯示 `資料已被其他人修改，請重新載入`，並提供「重新載入」按鈕。

- [ ] **Step 1：寫失敗的元件測試**
  - `shows backend field errors under the right field`：mock 後端回 400 `{errors:{f_12:['此欄位必填']}}`，對應欄位要顯示這段訊息。
  - `collapsed block starts collapsed`
  - `submits only changed values in edit mode`：送出的 values 只含使用者改過的鍵，和後端部分更新的語意一致。
- [ ] **Step 2：執行** `bunx vitest run src/presentation/dynamic/components`。預期 FAIL。
- [ ] **Step 3：實作。**
- [ ] **Step 4：執行同一個指令**，預期 PASS。
- [ ] **Step 5：Commit** `feat: 通用列表、表單、明細頁`

### Task 14：設計器頁面

**Files:**
- Create：`src/application/dynamic/queries/modules-queries.ts`
- Create：`src/pages/system/dynamic-modules/{ModuleListPage,ModuleEditorPage}.vue`
- Create：`src/presentation/dynamic/designer/{FieldEditorDialog,OptionEditor,I18nTextInput}.vue`
- Test：`src/presentation/dynamic/designer/FieldEditorDialog.vitest.spec.ts`、`I18nTextInput.vitest.spec.ts`

**Interfaces:**
- Consumes：Task 13 的 `dynamicApi`。
- Produces：
  - `ModuleListPage`：
    - 表格欄位：代碼、名稱、ProgramCode、狀態、版本。
    - 「新增模組」對話框，填代碼、名稱（`I18nTextInput`）、圖示。
    - 每一列有「啟用」「停用」按鈕。
  - `ModuleEditorPage`：
    - 左邊是區塊清單，可以新增、改名、設定收合。
    - 右邊是選定區塊的欄位清單，可以用上下按鈕排序（POC 不做拖拉）、停用、編輯。
    - 「新增欄位」打開 `FieldEditorDialog`。
    - 頂端可以設定記錄標題欄位：下拉只列出 text 型別的欄位。
  - `FieldEditorDialog`：
    - 編輯時型別選單鎖定，旁邊顯示 `欄位型別建立後不能更改`。
    - 依型別顯示設定：必填、唯讀、列表顯示、長度（text 與 textarea）、小數位（decimal、currency、percent）、預設值（用 registry 的元件輸入）。
    - 下拉型別嵌入 `OptionEditor`，可以新增、改標籤、改顏色、停用選項。
  - `I18nTextInput`：四個語系的輸入框，`zh-TW` 必填，其他語系預設收合。
  - 型別選單用繁中名稱：文字、多行文字、整數、小數、金額、百分比、日期、日期時間、時間、是否、Email、電話、網址、下拉、多選、使用者。
  - 每次異動成功後，invalidate 這個模組的 GetModule 和 schema 查詢。

- [ ] **Step 1：寫失敗的元件測試**
  - `type selector is locked in edit mode`
  - `shows scale input only for decimal currency percent`
  - `I18nTextInput requires zh-TW`
- [ ] **Step 2：執行** `bunx vitest run src/presentation/dynamic/designer`。預期 FAIL。
- [ ] **Step 3：實作。**
- [ ] **Step 4：執行同一個指令**，預期 PASS。
- [ ] **Step 5：Commit** `feat: 動態模組設計器`

### Task 15：啟動腳本與第 1 階段驗收

**Files:**
- Create：`poc/start.command`（macOS）、`poc/start.cmd`（Windows）：同時啟動後端與前端，並開瀏覽器到 `http://127.0.0.1:9180/`。
- Create：`poc/backend/tests/DynamicModules.WebAPITests/Phase1AcceptanceTests.cs`
- Modify：`README.md`，加上「POC 啟動」一節，寫需求（.NET 8 SDK、Bun 1.4 以上）、啟動方式、重設資料的方法（刪除 `poc/backend/data/poc.db`）。
- Create：`docs/acceptance/phase1.md`，記錄驗收結果。

**Interfaces:**
- Consumes：所有前面的任務。

- [ ] **Step 1：寫驗收測試** `Phase1_Scenario`，用 API 走完下面的流程，每一步都斷言：
  1. 建立模組 `repair-request`（模具維修申請），代碼要是 `DYN00001`。
  2. 加區塊「故障資訊」。
  3. 加以下欄位：
     - 申請日：`date`，必填。
     - 故障類別：`enum`，選項有模具損壞、機台異常、外包。
     - 檢查項目：`multienum`。
     - 預估費用：`currency`，scale 0。
     - 是否急件：`boolean`，預設 false。
     - 聯絡 Email：`email`。
     - 承辦人：`user`。
     - 說明：`textarea`。
  4. 啟用模組。
  5. 以 `sales01` 新增一筆記錄，確認 `ownerUserId` 是 `sales01`、「是否急件」欄位（鍵由步驟 3 的回應取得）套用了預設值 false。
  6. 修改說明，確認 `rowVersion` 改變了。
  7. 停用「外包」選項，再修改一筆原本就選「外包」的記錄的其他欄位，要成功。
  8. 刪除記錄，確認它不在 paged 列表裡。
- [ ] **Step 2：執行** `dotnet test poc/backend --filter Phase1_Scenario`，預期 PASS。如果失敗，修正對應任務的程式碼。
- [ ] **Step 3：執行全部自動測試**：`dotnet test poc/backend` 和 `cd poc/frontend && bunx vitest run && bunx vue-tsc --noEmit`，預期全部通過。
- [ ] **Step 4：實機走一遍**：執行 `poc/start.command`，在瀏覽器手動走完 Step 1 的同一個情境，包括設計器建模組、選單出現新模組、新增、編輯、明細、刪除。每個畫面截一張圖存到 `docs/acceptance/phase1/`，結果記在 `docs/acceptance/phase1.md`；手動步驟逐項標「通過」或「失敗」。
- [ ] **Step 5：Commit** `test: 第 1 階段驗收`
