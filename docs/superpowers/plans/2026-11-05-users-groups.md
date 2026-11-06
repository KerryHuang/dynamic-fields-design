# 使用者與群組管理 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 用真正的帳密登入取代 `X-User`，讓系統管理員在畫面上管理使用者與群組，全新安裝只有一個管理員，示範資料全部交給 Demo 產生器。

**Architecture:** ASP.NET Core 內建 cookie 驗證加一個 session middleware（每個請求核對 stamp、啟用狀態、是否須改密碼）。帳號邏輯照現有 MediatR 分層放在 `AccountService`，密碼雜湊和 cookie 留在 WebAPI。示範人員抽成 `DemoPeople`，產生器和測試共用。

**Tech Stack:** .NET 8、MediatR、EF Core（SQLite）、FluentValidation、xUnit + FluentAssertions；Vue 3、Quasar、TanStack Query、vitest。不新增第三方套件。

**Spec:** `docs/superpowers/specs/2026-11-05-users-groups-design.md`

## Global Constraints

- 不引入第三方套件；`TreatWarningsAsErrors` 開著。
- 帳號存小寫，格式 `^[a-z0-9._-]{2,50}$`，建立後不可改；登入時 trim 並轉小寫，密碼不 trim。
- 密碼至少 8 碼；連續輸錯 5 次鎖 15 分鐘；cookie 8 小時滑動逾期、關閉瀏覽器失效。
- 登入失敗一律回 401，訊息固定為「帳號或密碼錯誤，或帳號暫時無法登入」。
- 新畫面文字要有 zh-TW、en-US、th、vi 四語系。
- 鎖定判斷用 `TimeProvider`（真實時間），不走 `IBusinessTime`。
- 不寫 SQLite 專屬語法。
- 不出現真實公司名；示範客戶沿用虛構名稱。
- Git：逐檔 add，不用 `git add -A`；commit 結尾加 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`；不 push。
- 指令都從 worktree 根目錄跑。後端 `dotnet test poc/backend`；前端 `cd poc/frontend && bunx vitest run && bunx vue-tsc --noEmit`。
- Task 2 到 Task 6 之間，真實站台的前端無法登入，這是預期的；分支在 Task 9 完成前不合併。

## Review Focus

1. 編輯到一半 session 逾期：下一個 API 回 401 時導到 `/login?redirect=原路徑`，登入後回原頁；`redirect` 是 `//evil.example` 這類站外路徑時回首頁（Task 6）。
2. 管理員取消自己的系統管理員身分（還有其他管理員時允許）：下一個請求就 403，選單立刻收掉系統管理項目，不停在一個會一直報錯的頁面（Task 7）。
3. 員工離職：停用後，有人編輯一筆「處理人」是這位員工的舊記錄，存檔不能失敗，歷程和匯出照樣顯示姓名（Task 4）。
4. 群組被刪時，另一個分頁的模組權限畫面還開著：存檔帶到已刪除的群組 id，要回 400 而不是寫入孤兒列（Task 5）。
5. Demo 站第一位訪客喚醒站台時，產生器正持有寫入鎖：一鍵登入 admin 不能因為要寫資料庫而卡住或失敗（Task 3）。

---

### Task 1: 帳號欄位與種子資料拆分

把示範人員移出核心種子，全部測試維持綠燈。這個 task 還不動登入。

**Files:**
- Modify: `poc/backend/src/DynamicModules.Domain/Entities/Dynamic/AppUser.cs`
- Modify: `poc/backend/src/DynamicModules.Domain/Data/Configurations/AppUserConfiguration.cs`
- Create: migration `AccountFields`（`dotnet ef migrations add`，看既有 migration 的專案設定）
- Modify: `poc/backend/src/DynamicModules.Persistence/Seed/SeedData.cs`
- Create: `poc/backend/src/DynamicModules.Persistence/Seed/DemoPeople.cs`
- Create: `poc/backend/src/DynamicModules.Application/Dynamic/DemoService/IDemoPeople.cs`
- Modify: `DemoService/DemoPreflight.cs`、`DemoService/Commands/GenerateDemoData/GenerateDemoDataHandler.cs`
- Modify: `poc/backend/src/DynamicModules.Persistence/DbKnownUsers.cs`、`DependencyInjection.cs`
- Modify: `poc/backend/tests/DynamicModules.WebAPITests/TestApp.cs` 和所有呼叫 `SeedData.EnsureSeededAsync` 的測試
- Test: `poc/backend/tests/DynamicModules.ApplicationTests/Persistence/SeedDataTests.cs`、`Dynamic/Demo/` 底下新增 `DemoPeopleTests.cs`

**Interfaces:**
- Produces:
  - `AppUser` 新欄位：`string? PasswordHash`、`bool MustChangePassword`、`bool IsActive = true`、`bool IsDemoAccount`、`int FailedLoginCount`、`DateTimeOffset? LockedUntil`、`string SecurityStamp = Guid.NewGuid().ToString("N")`。
  - `DynamicModules.Persistence.Seed.DemoPeople`：`static Task EnsureAsync(DynamicDbContext db, CancellationToken ct = default)`，補齊示範使用者（`IsDemoAccount = true`、`PasswordHash = null`）、群組、成員、模具、客戶，已存在的不覆寫。給測試用，也給 `IDemoPeople` 的實作用。
  - `IDemoPeople`：`Task<IReadOnlyList<string>> ConflictsAsync(CancellationToken ct)`、`Task AddAsync(CancellationToken ct)`。
  - `TestApp(bool seedDemoPeople = true, bool realAuth = false)`：`realAuth` 在 Task 2 才有作用，這裡先加 `seedDemoPeople`。

- [ ] **Step 1: 寫失敗的測試**（`SeedDataTests`，先刪掉斷言示範使用者、`firstUpgrade` 授權的舊測試）
  - `FreshDatabase_HasOnlyAdmin`：`AppUser` 只有 `admin`，`IsSystemAdmin == true`，`PermissionGroup`、`MockMold`、`MockCustomer` 都是 0 筆。
  - `Reseed_DoesNotRestoreRemovedMembers`：`EnsureAsync` 後移除 `sales01` 的成員對應，再跑 `EnsureSeededAsync`，對應仍不存在。
  - `Reseed_DoesNotForceAdminFlag`：另建一位管理員、取消 `admin` 的 `IsSystemAdmin`，再跑 `EnsureSeededAsync`，`admin.IsSystemAdmin == false`。
  - `DemoPeopleTests.Generate_CreatesPeopleFlaggedAsDemo`：跑產生器後 `sales01.IsDemoAccount == true`、`PasswordHash == null`，`admin.IsDemoAccount == false`。
  - `DemoPeopleTests.Generate_ExistingDemoUser_Conflict`：先手動建 `sales01`，產生回 409，`items` 含 `使用者 sales01 已存在`，模組數為 0。
  - `DemoPeopleTests.Generate_Failure_RollsBackPeople`：沿用既有回滾測試的做法，失敗後 `sales01` 不存在。
  - `WebAPITests` 新增 `Template_MissingGroups_WarnsAndSkips`：在 `TestApp(seedDemoPeople: false)` 套用 `order` 範本，回應成功，`warnings` 含 `群組「sales-manager」不存在，已略過它的權限建議`。
- [ ] **Step 2: 跑測試確認失敗**：`dotnet test poc/backend --filter "FullyQualifiedName~SeedDataTests|FullyQualifiedName~DemoPeopleTests"`，預期因 `DemoPeople` 不存在而編譯失敗。
- [ ] **Step 3: 實作。**
  - `IsActive` 的設定要 `HasDefaultValue(true).ValueGeneratedNever()`，否則 EF 會把明確寫入的 `false` 換成資料庫預設值。`SecurityStamp` 的 migration 預設值用 `"initial"`。
  - `SeedData.Users`、`Groups`、`Molds`、`Customers`、`SeedMockEntitiesAsync`、`SeedGroupsAsync` 移到 `DemoPeople`；移除 `UpgradeGrantGroups`、`GrantExistingModulesAsync`、`firstUpgrade`、每次強制 `admin.IsSystemAdmin = true`。
  - 核心種子：`DYN00000`；資料庫沒有任何系統管理員時才建 `admin`（`IsSystemAdmin = true`）；其餘資料修復步驟保留。
  - `DemoPeopleSeeder : IDemoPeople` 放在 Persistence，用 scoped `DynamicDbContext`，`AddAsync` 呼叫 `DemoPeople.EnsureAsync` 後 `SaveChanges`。衝突訊息：`使用者 {id} 已存在`、`群組 {key} 已存在`、`模具 {no} 已存在`、`客戶 {no} 已存在`。
  - `DemoPreflight.ConflictsAsync` 合併 `IDemoPeople.ConflictsAsync`。`GenerateDemoDataHandler` 順序：衝突檢查 → `people.AddAsync` → `structure.BuildAsync`，同一個交易。409 訊息改成 `已有相同的資料，未產生 Demo 資料`。
  - `DbKnownUsers` 改成每次查資料庫，因為產生器會新增使用者。
  - 測試：在每個 `await SeedData.EnsureSeededAsync(...)` 後面補 `await DemoPeople.EnsureAsync(...)`；**例外**是 `DemoGeneratedFixture` 和所有跑產生器的測試，它們只跑核心種子，否則會衝突。`TestApp` 覆寫 `CreateHost`，`seedDemoPeople` 為 true 時在 host 建好後呼叫 `DemoPeople.EnsureAsync`。`DemoDataApiTests` 改用 `seedDemoPeople: false`，但它用到的非 admin 身分要在測試內自行建立，或在產生完成後才使用。
- [ ] **Step 4: 跑全部後端測試**：`dotnet test poc/backend`，預期全過。
- [ ] **Step 5: 跑前端確認未受影響**：`cd poc/frontend && bunx vitest run`，預期全過。
- [ ] **Step 6: Commit** `refactor: 示範人員移出核心種子，改由 Demo 產生器建立`

---

### Task 2: 登入、登出、改密碼與 session middleware

**Files:**
- Create: `poc/backend/src/DynamicModules.Application/Common/Security/IPasswordService.cs`
- Create: `poc/backend/src/DynamicModules.Application/Dynamic/AccountService/Commands/{Login,ChangePassword}/`、`AccountService/Data/AccountResponses.cs`
- Create: `poc/backend/src/DynamicModules.Domain/Repositories/Dynamic/IAccountRepository.cs`、`poc/backend/src/DynamicModules.Persistence/Repositories/Dynamic/AccountRepository.cs`
- Create: `poc/backend/src/DynamicModules.WebAPI/Security/{ClaimsCurrentUser,SessionMiddleware,PasswordService,AdminBootstrap,AuthCookies}.cs`
- Create: `poc/backend/src/DynamicModules.WebAPI/Controllers/v3/Dynamic/AuthController.cs`
- Modify: `Program.cs`、`appsettings.Development.json`、`poc/start.command`、`PermissionService/Data/PermissionResponses.cs`、`GetMeHandler.cs`
- Delete: `WebAPI/Security/HeaderCurrentUser.cs`、`IdentityMiddleware.cs`、`Persistence/DbKnownUsers.cs`、`IKnownUsers`
- Modify: `tests/DynamicModules.WebAPITests/TestApp.cs`，刪除 `HeaderCurrentUserTests.cs`（行為改由下面的測試涵蓋）
- Create: `tests/DynamicModules.WebAPITests/TestAuthHandler.cs`、`AuthApiTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `AppUser` 欄位、`TestApp`。
- Produces:
  - `IPasswordService`：`string Hash(string password)`、`bool Verify(string hash, string password)`。實作用 `PasswordHasher<AppUser>`（在 ASP.NET Core 共用框架內）。
  - `IAccountRepository`：`Task<AppUser?> GetTrackedAsync(string userId, CancellationToken ct)`；後續 task 會擴充。
  - `LoginRequest(string UserId, string Password)` → 成功時 `ServiceResult.Success(new AuthUserResponse(UserId, DisplayName, MustChangePassword, SecurityStamp))`，失敗時回新增的 `ServiceResult.Unauthorized(string message)`（`ToActionResult` 對應 401）。**不是** `ITransactionalRequest`：失敗計數要在回傳失敗時也寫入，由 handler 自己 `SaveChanges`。
  - `AuthCookies.SignInAsync(HttpContext, AuthUserResponse)`：簽發 `NameIdentifier` 和 `"stamp"` 兩個 claim。Task 3 共用。
  - `MeResponse` 新增 `bool MustChangePassword`。
  - 403 回應本體：`{ "message": "請先變更密碼", "code": "passwordChangeRequired" }`。
  - 401 回應本體：`{ "message": "請先登入" }`。
  - 匿名白名單（Task 3 會用到後兩個）：`POST /auth/login`、`GET /auth/options`、`POST /auth/demo-login`。

- [ ] **Step 1: 寫失敗的測試**（`AuthApiTests`，全部用 `new TestApp(realAuth: true)`，設定 `Auth:InitialAdminPassword = "initial-pass-1"`，client 保留 cookie）
  - `XUserHeader_WithoutCookie_Returns401`：送 `X-User: admin` 打 `GET /me`，401。
  - `Users_Anonymous_Returns401`：匿名打 `GET /users`，401。
  - `Login_Correct_SetsHttpOnlySameSiteLaxCookie_MeReturnsUser`。
  - `Login_NormalizesCaseAndWhitespace`：` Admin ` 可登入；密碼前後加空白則失敗。
  - `Login_Failures_AllReturnSameMessage`：不存在、密碼錯、已停用、已鎖定四種都是 401 且訊息完全相同。
  - `Login_FiveFailures_Locks15Minutes`：輸錯 5 次後正確密碼也失敗；`TimeProvider` 快轉 15 分鐘後成功，`FailedLoginCount == 0`。
  - `Login_UserWithoutPassword_Fails`：`PasswordHash == null` 回 401。
  - `MustChange_BlocksApiExceptAuthAndMe`：登入後 `GET /menu` 是 403 且 `code == "passwordChangeRequired"`，`GET /me` 是 200 且 `mustChangePassword == true`；改密碼後 `GET /menu` 是 200。
  - `ChangePassword_WrongOld_400`、`ChangePassword_SameAsOld_400`、`ChangePassword_TooShort_400`。
  - `ChangePassword_InvalidatesOtherSessions_KeepsCurrent`：兩個 client 登入，A 改密碼後 A 仍 200、B 401。
  - `Logout_ThenMe_401`。
  - `Bootstrap_SecondStartup_DoesNotOverwritePassword`：同一個資料庫換 `Auth:InitialAdminPassword` 重啟，舊密碼仍可登入。
  - `Bootstrap_NoEnvPassword_LogsGeneratedPassword`：不設環境變數時，log 有一則 Warning 含產生的密碼，且該密碼可登入。
- [ ] **Step 2: 跑測試確認失敗**：`dotnet test poc/backend --filter "FullyQualifiedName~AuthApiTests"`。
- [ ] **Step 3: 實作後端。**
  - `SessionMiddleware`（`UseAuthentication` 之後），只管 `/api/v3/dynamic` 開頭、白名單以外的路徑：未驗證回 401；查 `AppUser`，不存在、`!IsActive` 或 stamp 不符就 `SignOutAsync` 並回 401；`MustChangePassword` 且不是 `/auth/*`、`GET /me` 就回 403。
  - `ClaimsCurrentUser : ICurrentUser` 讀 `NameIdentifier`，沒有就回空字串；`AddActingOverrides()` 的呼叫順序不變。
  - cookie 設定：`HttpOnly`、`SameSite=Lax`、`SecurePolicy=SameAsRequest`、`SlidingExpiration`、`ExpireTimeSpan=8h`、不設 `IsPersistent`；`OnRedirectToLogin`、`OnRedirectToAccessDenied` 改回 401、403。
  - `Auth:TrustForwardedHeaders=true` 時才 `UseForwardedHeaders`（只處理 `XForwardedProto`，清空 `KnownNetworks`、`KnownProxies`），放在最前面。
  - DataProtection：資料庫是檔案路徑時 `PersistKeysToFileSystem(<資料庫目錄>/keys)`。
  - `AdminBootstrap.EnsureAsync`：在 `InitializeDatabaseAsync` 的 `EnsureSeededAsync` 之後呼叫。沒有「`IsActive && IsSystemAdmin && PasswordHash != null`」的使用者時，取得或建立 `admin`，設 `IsActive`、`IsSystemAdmin`、密碼、`MustChangePassword = !Demo:LoginEnabled`。密碼來自 `Auth:InitialAdminPassword`，否則用 `RandomNumberGenerator` 產生並以 Warning 印出。
  - 改密碼成功時更新 `SecurityStamp`，controller 用新 stamp 重發 cookie。登入成功時，`FailedLoginCount` 不為 0 才寫入，平常登入不寫資料庫。
  - log 登入成功、失敗（含真實原因）、鎖定。
  - `appsettings.Development.json` 加 `Auth:InitialAdminPassword`，值為 `admin1234`，讓本機開發不用翻 log；專案沒有 `launchSettings.json`，所以在 `poc/start.command` 的 `dotnet run` 前設 `ASPNETCORE_ENVIRONMENT=Development`。Dockerfile 維持預設的 Production，不會讀到這個值。
  - 註冊 `TimeProvider.System` 為 singleton，`LoginHandler` 用它判斷鎖定。
- [ ] **Step 4: 實作測試 scheme。** `TestAuthHandler`：讀 `X-User`，從 `DynamicDbContext` 查使用者，有就發和真實 cookie 相同的 `NameIdentifier`、`stamp` claim，沒有就 `NoResult`。`realAuth: false` 時 `TestApp` 把它設為預設 scheme，`SessionMiddleware` 不做任何特例。`realAuth: true` 不註冊它，並可用 `TestApp.Time` 控制 `TimeProvider`。
- [ ] **Step 5: 跑全部後端測試**：`dotnet test poc/backend`，預期全過。斷言舊訊息「請先選擇使用者」的測試改成「請先登入」。
- [ ] **Step 6: Commit** `feat: 帳密登入與 session 驗證，移除 X-User`

---

### Task 3: Demo 一鍵登入

**Files:**
- Create: `AccountService/Queries/GetAuthOptions/`、`AccountService/Commands/DemoLogin/`、`Common/Security/AuthSettings.cs`
- Modify: `AuthController.cs`、`Program.cs`、`render.yaml`
- Test: `tests/DynamicModules.WebAPITests/DemoLoginApiTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `AuthCookies.SignInAsync`、`AuthUserResponse`；既有的 `IDemoDataRunner.Current`。
- Produces:
  - `AuthSettings(bool DemoLoginEnabled)`，singleton，讀 `Demo:LoginEnabled`。
  - `GET /auth/options` → `{ demoLoginEnabled, demoGenerating, demoAccounts: [{ userId, displayName }] }`。未開啟時 `demoAccounts` 是空陣列。
  - `POST /auth/demo-login { userId }`。

- [ ] **Step 1: 寫失敗的測試**（`realAuth: true`）
  - `Options_Disabled_NoAccounts`：`demoLoginEnabled == false`、`demoAccounts` 空。
  - `DemoLogin_Disabled_404`。
  - `Options_Enabled_ListsActiveDemoAccountsAndAdmin`：`admin` 排第一，其後是 `IsDemoAccount` 且啟用中的帳號；已停用的不列。
  - `DemoLogin_NonDemoAccount_401`：一般帳號（`IsDemoAccount == false` 且不是 `admin`）。
  - `DemoLogin_InactiveAccount_401`。
  - `DemoLogin_Admin_NoPasswordChangeRequired`：Demo 模式全新資料庫，一鍵登入 admin 後 `GET /menu` 是 200。
  - `DemoLogin_LockedAccount_Succeeds`：`LockedUntil` 在未來仍可一鍵登入。
  - `DemoLogin_Admin_DoesNotWrite`：`MustChangePassword == false` 時，登入前後 `AppUser` 列完全相同（對應 Review Focus 5）。
  - `DemoLogin_MustChangeSet_ClearsFlag`：先把 admin 的 `MustChangePassword` 設成 true，一鍵登入後 `GET /menu` 是 200。
  - `Options_WhileGenerating_DemoGeneratingTrue`：用既有 `DemoDataApiTests` 的 Gate 手法，在產生中讀 options。
- [ ] **Step 2: 跑測試確認失敗**：`dotnet test poc/backend --filter "FullyQualifiedName~DemoLoginApiTests"`。
- [ ] **Step 3: 實作。** 開啟時啟動 log 印 Warning。`render.yaml` 新增 `Demo__LoginEnabled=true`、`Auth__TrustForwardedHeaders=true`，檔頭註解保留。
- [ ] **Step 4: 跑測試確認通過**：`dotnet test poc/backend`，預期全過。
- [ ] **Step 5: Commit** `feat: Demo 站一鍵登入`

---

### Task 4: 使用者管理 API

**Files:**
- Create: `Common/Security/ISystemAdminRequest.cs`、`Common/Behaviors/SystemAdminBehavior.cs`（照 `DesignerAccessBehavior` 的寫法與註冊順序）
- Create: `AccountService/Queries/AdminListUsers/`、`AccountService/Commands/{CreateUser,UpdateUser,ResetPassword,SetUserActive}/`（各含 validator）
- Create: `WebAPI/Controllers/v3/Dynamic/AdminUsersController.cs`
- Modify: `IAccountRepository.cs`、`AccountRepository.cs`、`PermissionService/Queries/GetUsers/GetUsersHandler.cs`、`ImportService/ImportPlanner.cs`
- Test: `tests/DynamicModules.WebAPITests/AdminUsersApiTests.cs`、`tests/DynamicModules.ApplicationTests/Dynamic/DeactivatedUserTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `IPasswordService`、`IAccountRepository`。
- Produces:
  - `ISystemAdminRequest` 標記介面；非系統管理員回 `ServiceResult.Forbidden()`。
  - `GET /admin/users` → `[{ userId, displayName, isActive, isSystemAdmin, isDemoAccount, isLocked, groupIds }]`，含停用者。
  - `POST /admin/users { userId, displayName, password, groupIds, isSystemAdmin }`。
  - `PUT /admin/users/{id} { displayName, groupIds, isSystemAdmin }`。
  - `POST /admin/users/{id}/reset-password { password }`。
  - `POST /admin/users/{id}/deactivate`、`POST /admin/users/{id}/activate`。
  - `IAccountRepository` 新增：`AddUser(AppUser)`、`Task<bool> UserExistsAsync(string, CancellationToken)`、`Task<int> CountActiveAdminsAsync(CancellationToken)`、`Task SetMembershipsAsync(string userId, IReadOnlyCollection<long> groupIds, CancellationToken)`、`Task<bool> GroupsExistAsync(IReadOnlyCollection<long>, CancellationToken)`。

- [ ] **Step 1: 寫失敗的測試**
  - `AdminEndpoints_NonAdmin_403`：以 `sales01` 打全部 6 個端點，都是 403。
  - `CreateUser_NormalizesToLowercase_MustChangePassword`：`Ann.Lee` 存成 `ann.lee`，`MustChangePassword == true`，雜湊不是明文。
  - `CreateUser_Duplicate_409`、`CreateUser_InvalidId_400`（`a`、含空白、含中文）、`CreateUser_ShortPassword_400`、`CreateUser_UnknownGroup_400`。
  - `CreateUser_MultipleGroups_AppearInMeAndList`：屬於 `sales`、`qa` 兩群組，`GET /me`（以該身分）列出兩個。
  - `CreateUser_ThenLoginWithRealAuth`：`realAuth: true`，新帳號立刻能登入，證明沒有快取。
  - `UpdateUser_UserIdUnchanged_GroupsReplaced`。
  - `ResetPassword_UnlocksAndRequiresChange_InvalidatesSessions`：鎖定中的帳號重設後可登入、必須改密碼、舊 session 401。
  - `Deactivate_InvalidatesSessionImmediately`：`realAuth: true`，停用後該使用者下一個請求 401。
  - `Deactivate_Self_400`、`Deactivate_LastAdmin_400`、`Demote_LastAdmin_400`。
  - `Demote_Self_WhenOtherAdminExists_Allowed`：之後該使用者打 `/admin/users` 是 403。
  - `Users_ExcludesInactive`：`GET /users` 不含停用者，`GET /admin/users` 含。
  - `Import_UserFieldWithInactiveUser_RowError`：匯入時指定停用的使用者，該列報錯。
  - `DeactivatedUserTests.EditRecord_WithInactiveUserFieldValue_Succeeds`：處理人是停用者的記錄，改別的欄位存檔成功。
  - `DeactivatedUserTests.History_ShowsInactiveUserName`、`Export_ShowsInactiveUserName`。
- [ ] **Step 2: 跑測試確認失敗**：`dotnet test poc/backend --filter "FullyQualifiedName~AdminUsersApiTests|FullyQualifiedName~DeactivatedUserTests"`。
- [ ] **Step 3: 實作。** 寫入類指令都是 `ITransactionalRequest`。最後一位管理員的判斷在交易內計數。重設密碼、停用都更新 `SecurityStamp`；重設時 `FailedLoginCount = 0`、`LockedUntil = null`、`MustChangePassword = true`。`GetUsersHandler` 只回 `IsActive`；`ListUsersAsync` 不變。`ImportPlanner` 比對只接受 `IsActive`。
- [ ] **Step 4: 跑全部後端測試**：`dotnet test poc/backend`。
- [ ] **Step 5: Commit** `feat: 使用者管理 API`

---

### Task 5: 群組管理 API

**Files:**
- Create: `AccountService/Queries/{AdminListGroups,GetGroupImpact}/`、`AccountService/Commands/{CreateGroup,RenameGroup,SetGroupMembers,DeleteGroup}/`
- Create: `WebAPI/Controllers/v3/Dynamic/AdminGroupsController.cs`
- Modify: `IAccountRepository.cs`、`AccountRepository.cs`、`PermissionService/Commands/SaveModulePermissions/`（handler 或 validator）
- Test: `tests/DynamicModules.WebAPITests/AdminGroupsApiTests.cs`

**Interfaces:**
- Consumes: Task 4 的 `ISystemAdminRequest`、`IAccountRepository`。
- Produces:
  - `GET /admin/groups` → `[{ id, key, nameI18n, memberUserIds }]`。
  - `POST /admin/groups { name, locale }`：`Key = "g-" + 8 碼小寫英數亂碼`。
  - `PUT /admin/groups/{id} { name, locale }`：只改該語系，其他語系保留。
  - `PUT /admin/groups/{id}/members { userIds }`。
  - `GET /admin/groups/{id}/impact` → `{ memberCount, modules: [string], fieldPermissionCount, dataScopeCount, blockingOptions: [{ module, field, option }] }`。
  - `DELETE /admin/groups/{id}`：有 `blockingOptions` 回 409 並帶同一份清單。

- [ ] **Step 1: 寫失敗的測試**
  - `AdminEndpoints_NonAdmin_403`。
  - `Create_GeneratesKey_ShowsInGroups`：`Key` 符合 `^g-[a-z0-9]{8}$`，`GET /groups` 看得到。
  - `Create_DuplicateNameInLocale_400`、`Create_EmptyName_400`。
  - `Rename_KeepsKeyAndOtherLocales`。
  - `SetMembers_ReplacesMembers_SurvivesRestart`：重新執行 `SeedData.EnsureSeededAsync` 後成員不變。
  - `SetMembers_UnknownUser_400`。
  - `NewGroup_GrantedModule_MemberGetsAccess`：建群組 → 加成員 → 用既有權限 API 授權 → 該成員 `GET /menu` 看得到模組。
  - `UserWithNoGroups_SeesNoModules`：fail-closed。
  - `Impact_CountsAllRelatedRows`。
  - `Delete_WithOptionScope_409_ListsOptions_NothingDeleted`。
  - `Delete_RemovesMembershipsPrivilegesFieldPermissionsDataScopes`：四張表都沒有該群組的列，其他群組的列不受影響。
  - `SavePermissions_DeletedGroupId_400`：對應 Review Focus 4，帶已刪除群組 id 的 `GroupPermissionDto`、`FieldPermissionDto`、`OptionScopeDto` 都回 400，且沒有寫入。
- [ ] **Step 2: 跑測試確認失敗**：`dotnet test poc/backend --filter "FullyQualifiedName~AdminGroupsApiTests"`。
- [ ] **Step 3: 實作。** 寫入類指令都是 `ITransactionalRequest`。刪除時明確刪四張表的列，再刪群組；現有關聯沒有 FK cascade。`nameI18n` 重複檢查只比對同一個 locale。
- [ ] **Step 4: 跑全部後端測試**：`dotnet test poc/backend`。
- [ ] **Step 5: Commit** `feat: 群組管理 API`

---

### Task 6: 前端登入流程

**Files:**
- Create: `poc/frontend/src/pages/auth/{LoginPage,ChangePasswordPage}.vue` 與各自的 `.vitest.spec.ts`
- Create: `poc/frontend/src/router/auth-guard.ts`、`auth-guard.vitest.spec.ts`
- Create: `poc/frontend/src/application/dynamic/queries/auth-queries.ts`
- Modify: `router/routes.ts`、`router/index.ts`、`layouts/MainLayout.vue`、`MainLayout.vitest.spec.ts`
- Modify: `stores/use-identity-store.ts`、其 spec、`services/api/http.ts`、`http.vitest.spec.ts`、`services/api/dynamic-api.ts`、`domain/dynamic/types.ts`
- Modify: `poc/frontend/src/i18n/{zh-TW,en-US,th,vi}.json`

**Interfaces:**
- Consumes: Task 2、3 的端點與回應本體。
- Produces:
  - `useIdentityStore`：`userId: Ref<string>`（空字串表示未登入）、`users: Ref<User[]>`、`setSession(userId: string)`、`setUsers(list: User[])`、`clear()`。移除 localStorage 與 `syncUsers`。
  - `http.ts`：`setAuthHandlers({ onUnauthorized(): void, onPasswordChangeRequired(): void })`；`ApiError` 新增 `code?: string`。`/auth/` 開頭的請求回 401 不觸發 `onUnauthorized`，因為登入失敗就是 401。
  - `safeRedirect(path: string | undefined): string`（`auth-guard.ts`）：只接受 `/` 開頭且第二字元不是 `/` 或 `\` 的路徑，否則回 `/`。
  - `auth-queries.ts`：`useAuthOptions()`、`useLogin()`、`useDemoLogin()`、`useLogout()`、`useChangePassword()`。

- [ ] **Step 1: 寫失敗的測試**
  - `http.vitest`：不再送 `X-User`；一般請求 401 呼叫 `onUnauthorized`；`/auth/login` 回 401 不呼叫；403 且 `code == passwordChangeRequired` 呼叫 `onPasswordChangeRequired`。
  - `auth-guard.vitest`：未登入進 `/dynamic/order` → `/login?redirect=/dynamic/order`；已登入進 `/login` → `/`；`mustChangePassword` 進其他頁 → `/change-password`；`safeRedirect` 擋 `//evil.example`、`/\evil.example`、`https://evil.example`。
  - `LoginPage.vitest`：登入失敗顯示後端訊息且留在頁面；成功後導到 `redirect`；Demo 模式列出帳號，點一下呼叫 `demo-login`；`demoGenerating` 時顯示 `auth.demoGenerating`；Demo 關閉時沒有帳號清單。
  - `ChangePasswordPage.vitest`：兩次新密碼不一致不送出；少於 8 碼不送出；成功後導回首頁；後端 400 訊息顯示在頁面。
  - `MainLayout.vitest`：沒有 `identity-select`；顯示目前使用者姓名、改密碼、登出；登出會呼叫 API、清空 query cache 與 store、導到 `/login`。
- [ ] **Step 2: 跑測試確認失敗**：`cd poc/frontend && bunx vitest run src/services/api/http.vitest.spec.ts src/router src/pages/auth src/layouts`。
- [ ] **Step 3: 實作。**
  - 路由：`/login`、`/change-password` 放在 `MainLayout` 之外。guard 第一次導頁時呼叫 `GET /me` 取得 session，401 視為未登入。
  - `router/index.ts` 註冊 `setAuthHandlers`：`onUnauthorized` 清空 store 與 query cache，導到 `/login?redirect=<目前路徑>`。
  - `MainLayout`：移除下拉選單與 `switchUser`；`useUsers` 的結果寫入 `identity.setUsers`；`<router-view :key="identity.userId">` 保留。
  - `useMe`、`useMenu` 的 query key 維持用 `identity.userId`。登入成功時先 `queryClient.clear()` 再 `setSession`。
  - i18n 鍵放在 `auth.*`，`identity.*` 用不到的鍵刪除。
- [ ] **Step 4: 跑全部前端驗證**：`cd poc/frontend && bunx vitest run && bunx vue-tsc --noEmit`，預期全過。
- [ ] **Step 5: Commit** `feat(frontend): 登入頁、改密碼與路由保護`

---

### Task 7: 前端使用者管理頁

**Files:**
- Create: `poc/frontend/src/pages/system/UsersPage.vue`、`UsersPage.vitest.spec.ts`
- Create: `poc/frontend/src/application/dynamic/queries/admin-queries.ts`
- Modify: `router/routes.ts`、`layouts/MainLayout.vue`、`services/api/dynamic-api.ts`、`domain/dynamic/types.ts`、四個 i18n 檔

**Interfaces:**
- Consumes: Task 4 的端點；Task 6 的 store。
- Produces: `admin-queries.ts`：`useAdminUsers()`、`useCreateUser()`、`useUpdateUser()`、`useResetPassword()`、`useSetUserActive()`。每個 mutation 成功都 invalidate `['admin-users']`、`['users']`、`['me']`（`useUsers` 是 `staleTime: Infinity`，不 invalidate 就看不到新人）。路由 `system/users`，選單 `data-testid="users-menu"`，`me.isSystemAdmin` 才顯示。

- [ ] **Step 1: 寫失敗的測試**（`UsersPage.vitest`）
  - 列出帳號、姓名、群組名稱、狀態、系統管理員，停用者也在。
  - 新增：送出的 body 正確；後端 409、400 訊息顯示在對話框；帳號欄編輯時唯讀。
  - 重設密碼：少於 8 碼不送出；成功後通知。
  - 停用自己的按鈕是停用狀態；停用他人要先確認。
  - 非系統管理員收到 403 時顯示 `ForbiddenNotice`。
  - 取消自己的系統管理員成功後，`me` 重新取得，`users-menu` 消失（`MainLayout.vitest`）。
- [ ] **Step 2: 跑測試確認失敗**：`cd poc/frontend && bunx vitest run src/pages/system/UsersPage.vitest.spec.ts`。
- [ ] **Step 3: 實作。** 照 `DemoDataPage.vue`、`ModuleListPage.vue` 的版面與 `notifyApiError`。系統管理區塊的顯示條件現為 `me?.designer.read`，改成兩者任一成立就顯示區塊標題，使用者、群組項目各自看 `me.isSystemAdmin`。
- [ ] **Step 4: 跑全部前端驗證**：`cd poc/frontend && bunx vitest run && bunx vue-tsc --noEmit`。
- [ ] **Step 5: Commit** `feat(frontend): 使用者管理頁`

---

### Task 8: 前端群組管理頁

**Files:**
- Create: `poc/frontend/src/pages/system/GroupsPage.vue`、`GroupsPage.vitest.spec.ts`
- Modify: `application/dynamic/queries/admin-queries.ts`、`router/routes.ts`、`layouts/MainLayout.vue`、`services/api/dynamic-api.ts`、`domain/dynamic/types.ts`、四個 i18n 檔

**Interfaces:**
- Consumes: Task 5 的端點；Task 7 的 `admin-queries.ts`、系統管理選單條件。
- Produces: `useAdminGroups()`、`useCreateGroup()`、`useRenameGroup()`、`useSetGroupMembers()`、`useGroupImpact(id)`、`useDeleteGroup()`。每個 mutation 成功都 invalidate `['admin-groups']`、`['groups']`、`['admin-users']`。路由 `system/groups`，選單 `data-testid="groups-menu"`。

- [ ] **Step 1: 寫失敗的測試**（`GroupsPage.vitest`）
  - 列出名稱（依目前語系解析）與成員數。
  - 新增、改名送出目前語系；重複名稱的 400 訊息顯示在對話框。
  - 成員維護只列啟用中的使用者，送出 `userIds`。
  - 刪除先呼叫 impact，對話框顯示成員數、模組、欄位例外、資料範圍，以及「成員會失去這個群組的授權與限制」。
  - 有 `blockingOptions` 時列出清單，刪除按鈕停用。
  - 刪除時後端回 409（另一人剛加了選項範圍）：顯示清單，不關閉對話框。
- [ ] **Step 2: 跑測試確認失敗**：`cd poc/frontend && bunx vitest run src/pages/system/GroupsPage.vitest.spec.ts`。
- [ ] **Step 3: 實作。**
- [ ] **Step 4: 跑全部前端驗證**：`cd poc/frontend && bunx vitest run && bunx vue-tsc --noEmit`。
- [ ] **Step 5: Commit** `feat(frontend): 群組管理頁`

---

### Task 9: 文件與實機走查

**Files:**
- Modify: `docs/demo.md`（切換身分改成登出再點帳號）、`docs/manual.md`（新增第一次安裝、登入、使用者、群組；§9 已知限制）、`README.md`（環境變數 `Auth__InitialAdminPassword`、`Auth__TrustForwardedHeaders`、`Demo__LoginEnabled`）
- Create: `docs/superpowers/plans/2026-11-05-users-groups-rulings.md`（執行時的裁決）

- [ ] **Step 1: 更新文件。** 手冊已知限制要寫：沒有寄信重設密碼；沒有公司帳號登入；舊資料庫的使用者要重設密碼才能登入；列表的使用者欄位仍顯示帳號；`Demo__LoginEnabled` 絕不能在正式站開啟；Docker 部署要把資料庫目錄掛成 volume，金鑰才會保留。
- [ ] **Step 2: 跑全部測試**：`dotnet test poc/backend`；`cd poc/frontend && bunx vitest run && bunx vue-tsc --noEmit && bunx quasar build`。
- [ ] **Step 3: 確認公司資訊與示範資料沒有外洩到全新安裝**：掃新增行沒有公司相關字串（清單不進 repo）；全新資料庫啟動後 `GET /admin/users` 只有 `admin`。
- [ ] **Step 4: 實機走查正式模式**（chrome-devtools，先 `list_pages`，有頁面就用；`poc.db` 先移開，結束後放回）：全新資料庫 → 用 `admin` 登入 → 被導到改密碼 → 建群組 → 建使用者並加入兩個群組 → 模組權限授權 → 登出 → 新使用者登入、改密碼、看到授權的模組 → 管理員停用他 → 他的下一個操作被導回登入頁。重開後端，確認成員和密碼都還在。
- [ ] **Step 5: 實機走查 Demo 模式**：`Demo__SeedOnStartup=true Demo__LoginEnabled=true` 啟動 → 登入頁顯示「Demo 資料產生中」和 admin → 產生完成後列出所有 Demo 帳號 → 一鍵登入 `sales01` 只看到自己的訂單 → 登出換 `manager01` 看到金額 → 照 `docs/demo.md` 每節走一次。走不到的標「未實機驗證」，Render 上的行為標「未驗證」。
- [ ] **Step 6: Commit** `docs: 登入、使用者與群組的手冊和 Demo 腳本`
