# 帳號安全 設計文件（段 au1）

> 2026-11-21｜平台共用能力（非應用程式）｜前置：[使用者與群組](2026-11-05-users-groups-design.md)（§4 登入）、[維運與觀測](2026-11-11-ops-observability-design.md)（§14 U1、§17.2.6 寄信設定與告警佇列、§17.9 安全）、[政策設定](2026-11-11-policy-settings-design.md)、[MCP 伺服器](2026-11-11-mcp-server-design.md)（§5.1、§13 個人存取權杖）、[權限軸](2026-11-19-permission-axes-design.md)、bk1 備份設計（同日，§5.7 備份不帶金鑰）
> 狀態：主控撰寫（2026-11-21），這次只寫設計、不改程式。第二版（同日）：收同家審查必修 1–12 與建議 15 條、套用使用者 2026-11-20 已確認的平台分流答案（A 區照推薦、B 區全部確認）與 2026-11-21 新裁決。第三、四版（同日）：收兩次安全複審（N1–N7、建議 13 條與 A–F）與領域顧問分流（Q17–Q22）、使用者 2026-11-21 裁決 Q23。
> 標記：「（使用者裁決）」「（使用者確認）」是使用者已拍板的；「（Q#，顧問拍板）」是行為類，領域顧問先定、待使用者確認；兩者彙整在 §20.1；「（主控裁決）」是安全守衛類的主控定案，列 §22；「（T#）」是技術類，主控已定、可推翻，彙整在 §20.2。分流答案以題號引用（例：au1-6），出處是 2026-11-19 的平台分流文件。

## 1. 目的

雲端訂閱版的站台對外開放，現在只靠帳號密碼，忘記密碼只能找管理員（使用者與群組設計 §4：「忘記密碼由管理員重設；系統不寄信」）。使用者 2026-11-17 裁決：雲端版對外開放前必須有寄信重設密碼與雙因素登入（`docs/feature.md` 第 56、57 列）。

au1 做三件事：

1. **核心寄信佇列**：帳號信（重設密碼、Email 驗證、安全通知）的寄送佇列，放在核心、不依賴任何應用程式。SMTP 連線沿用 o2e 已抽成核心的「寄信設定」，與告警信共用連線、佇列分開；帳號信不節流、不摘要（使用者 2026-11-19 裁決，au1-1）。之後簽核、任務的 Email 通知（第二版候選）也寄這個佇列。
2. **寄信重設密碼**：登入頁「忘記密碼？」→ 寄一封有時效、只能用一次的連結到帳號已驗證的 Email → 設新密碼。照 OWASP Forgot Password Cheat Sheet：不透露帳號是否存在、連結短時效、單次使用、改密碼後既有連結作廢、重設後登出其他 session。
3. **雙因素登入（TOTP）**：驗證器 App（RFC 6238），附復原碼與「記住這台裝置」；系統管理員可設選用或強制，系統管理員出廠就強制（使用者 2026-11-17 裁決＋au1-漏1、au1-6）；手機遺失由系統管理員重設。

SSO、AD／LDAP 列第二版候選（使用者 2026-11-17 裁決），au1 不做。

## 2. 範圍

**範圍內**

- 帳號 Email：`AppUser` 加 Email 與驗證時間；管理員在使用者管理設定、本人在「帳號安全」頁設定（Q3）；不論誰填，都要點驗證信才生效（使用者 2026-11-21 裁決，§6.5）。
- 核心寄信佇列 `Dynamic_MailOutbox`、寄送服務、重試、失敗事件、「帳號信寄送紀錄」清單（寄信設定頁）。
- 站台名稱：外觀設定新增「站台名稱」，帳號信的寄件名稱、主旨前綴與信中公司名稱讀它（au1-漏3，Q18）。
- 核心站台網址設定 `App:PublicUrl`，保留 `Ops:PublicUrl` 舊鍵相容（主控技術決定 D20）。
- 忘記密碼：申請頁、重設頁、連結權杖表 `Dynamic_AccountToken`、節流（每帳號每小時次數是政策，au1-4）、安全通知信（Q5）。
- 管理員替別人重設密碼：對方有已驗證 Email 時預設寄重設連結，暫時密碼保留（au1-11）。
- 密碼最短長度改成政策（出廠 8；D25）。
- 雙因素：綁定、登入第二步、復原碼、記住這台裝置（au1-9）、本人關閉、管理員重設、強制政策與寬限天數（au1-6、au1-7）、強制綁定期間的 API 封鎖、帳號層猜碼上限。
- 建 AI 連線權杖時，已綁雙因素的人另要輸入驗證碼（au1-10）。
- 安全事件：管理員帳號用寄信重設密碼（au1-5）、重設另一位管理員的雙因素（au1-8）、第二步連續失敗達上限、救援設定沒移除。
- 「帳號安全」頁 `/account/security`（Q14）。
- 政策：新分類「帳號安全」十項、維運分類一項（§9）。
- 啟動救援 `Auth:ResetAdminPassword` 一併清除 `admin` 的雙因素與記住的裝置（au1-漏2）。
- Demo：示範帳號的虛構 Email、雙因素示範帳號、Demo 收件匣（§13）。

**範圍外**

- SSO、AD／LDAP、Passkey／WebAuthn、簡訊與 Email 驗證碼當第二因素：第二版候選或不做。
- 帳號信內容自訂：第一版用內建四語範本（au1-漏3）。
- 自助註冊（功能清單「不做」不變）。
- 業務 Email（簽核、任務通知）：仍是第二版候選；au1 只把佇列做成核心，不加任何業務信種類。
- 密碼組成規則（大小寫、符號）：不做，只有最短長度。
- 告警信主旨的「[Formwright]」改讀站台名稱：不在 au1，開 backlog 票（§16 第 10 點）。
- 雲端版的郵件服務選擇（SMTP 還是 HTTP 郵件 API）：au1 只留抽象，實際選擇與查證併入 O3（T18）。
- 個資告知文字與雲端版委託處理條款（分流 C 區「其他」）：不影響資料結構，雲端版對外開放前另問。
- 多實例部署下的記憶體節流：沿用現有「單一站台實例」假設（同 `LoginThrottle`），已知限制；帳號層的第二步失敗計數存資料庫，不受這個限制。

## 3. 現況盤點（main 84dd2a05）

| 項目 | 現況 | 證據 |
|---|---|---|
| 帳號欄位 | 沒有 Email | `Domain/Entities/Dynamic/AppUser.cs`；維運設計 §17.2.6（O2-U1） |
| 登入 | PBKDF2；失敗統一 401；帳號不存在等也跑假雜湊；稽核 `login.succeeded`／`login.failed` | `AccountService/Commands/Login/LoginHandler.cs` |
| 登入限流 | 記憶體、單一實例；密碼正確時 `Complete(success: true)` 清掉帳號＋IP 層 | `LoginHandler.cs:71`、`Common/Security/LoginThrottle.cs:11`、`:33–37`、`:68` |
| Session | cookie HttpOnly、SameSite=Lax、8 小時滑動、關瀏覽器失效；claim 帶 `SecurityStamp`，每個請求比對 | `WebAPI/Program.cs:119–126`、`WebAPI/Security/AuthCookies.cs`、`SessionMiddleware.cs` |
| 匿名白名單、強制改密碼 | 白名單四條；強制改密碼放行 `/auth/*` 與 `GET /me` | `SessionMiddleware.cs:57`、`:67`、`:81–83` |
| 改密碼、管理員重設 | 本人改不撤 AI 權杖、沒有限流；管理員重設撤 AI 權杖；稽核 `user.resetPassword` 一般寫入 | `ChangePasswordHandler.cs`、`ResetPasswordHandler.cs`、`Common/Audit/AuditActions.cs:34` |
| 停用帳號 | 換 stamp、撤 AI 權杖 | `SetUserActiveHandler.cs:29–31` |
| 密碼最短長度 | 後端 `ChangePasswordValidator.MinLength`（`CreateUserValidator`、`ResetPasswordValidator` 引用它）；另有 `AdminBootstrap.cs:15`、前端 `UsersPage.vue:22` `MIN_PASSWORD`、`ChangePasswordPage.vue:17` `MIN_LENGTH` 各一份 | 同左 |
| 救援 | `Auth:ResetAdminPassword` 重設 `admin` | `WebAPI/Security/AdminBootstrap.cs`、`docs/manual.md:28` |
| 寄信設定 | 核心單列 `Dynamic_MailSettings`；Demo 站唯讀 | `Domain/Entities/Dynamic/MailSettings.cs`、`MailService/MailSettingsHandlers.cs` |
| 寄送 | MailKit，安全邊界寫死；介面以告警為名 | `WebAPI/Ops/AlertMail.cs`、`OpsService/Alerts/AlertContracts.cs:56` |
| 告警佇列 | 條件式領取、交易外寄送、重試 1／5／15／60 分鐘最多 5 次、失敗開 `channel.smtp` | 維運設計 §17.2.6、`AlertContracts.cs:86–95`、`AlertDelivery.cs:107` |
| 信件網址 | `Ops:PublicUrl`，沒設告警信就不放連結 | `AlertContracts.cs:73–79`、`Program.cs:101` |
| Data Protection | 金鑰目錄 `keysDirectory`＝設定的 `DataProtection:KeysDirectory`；沒設而資料庫是檔案（SQLite）時是 `<資料庫目錄>/keys`，照樣落地；PostgreSQL、記憶體資料庫與 `file:` URI 寫法的 SQLite 沒設時為 null（不落地；`file:` URI 是持久檔案，但 `FileDirectory` 認不出來），PostgreSQL 只出啟動警告 | `Program.cs:139–147`、`Persistence/DatabaseProvider.cs:33–43` |
| 外觀設定 | 只有模式與風格兩個值，存 `SystemSetting`；Demo 站擋寫入；沒有站台名稱 | `ThemeService/Commands/UpdateSiteTheme/UpdateSiteThemeHandler.cs`、`Domain/ValueObjects/ThemeOptions.cs` |
| AI 權杖 | 建立時重新輸入密碼、與登入同一套降速；可用條件 `IsActive && !IsDemoAccount && !MustChangePassword` | `McpService/Tokens/McpTokenRequests.cs:15`、`McpTokenHandlers.cs:42` |
| 政策 | `PolicyCatalog` 唯一登記處；系統政策快取 60 秒；政策 API 在 Demo 站沒擋寫入 | `Domain/Policies/PolicyCatalog.cs`、`PolicyService/CachedSystemPolicy.cs:30`、`PolicyHandlers.cs` |
| Demo | 13 個示範帳號沒有密碼、一鍵登入；一鍵登入也收 `admin`（`IsDemoAccount` 為否）；Demo 的 `admin` 密碼啟動時隨機；映像建置時產生資料後刪金鑰，執行期重新產生 | `DemoPeople.cs`、`DemoLoginHandler.cs:57`、`AccountRepository.cs:16`、`AdminBootstrap.cs:34`、`Dockerfile.demo:44–45` |
| 前端 | 頁首「變更密碼」「AI 連線」「登出」；沒有 QR 碼套件；錯誤回報只送路由樣板 | `layouts/MainLayout.vue:19–31`、`frontend/package.json`、`error-reporter.ts:70–78` |

## 4. 已定決定（本設計照用，不再問）

- 帳號信與告警信共用核心寄信設定的連線，佇列、收件人、語系分開；帳號信佇列不節流、不摘要（使用者 2026-11-19 裁決，au1-1）。
- 雙因素用 TOTP 驗證器 App；SSO、AD／LDAP 第二版（使用者 2026-11-17 裁決）。
- 出廠「選用」，但系統管理員一律強制，客戶可在政策放寬（使用者確認，au1-漏1 A）。
- 強制範圍：選用或強制；強制時可選「全部使用者」或「指定群組」，系統管理員一律在強制對象內（使用者確認，au1-6）。
- 被強制但還沒綁定：登入後直接進綁定畫面；寬限天數是政策，出廠 0＝立即；寄信重設密碼後登入仍要驗證碼（使用者確認，au1-7）。
- 「這台裝置 N 天內不再問」：做，天數政策出廠 30、0＝關閉；改密碼、重設雙因素時全部失效（使用者確認，au1-9）。
- 手機遺失：10 組一次性復原碼（可重新產生）；系統管理員可「重設雙因素」，寫稽核並寄通知給本人；重設別的管理員時另發安全事件（使用者確認，au1-8）。
- 建 AI 權杖時重新輸入密碼，已綁雙因素的人另要驗證碼；權杖照常使用；重設雙因素不撤銷權杖（使用者確認，au1-10）。
- 管理員替別人重設密碼：對方有 Email 時預設寄重設連結，也可直接設暫時密碼（使用者確認，au1-11）。
- 系統管理員也能用「忘記密碼」；已綁雙因素的仍要驗證碼；重設後寄通知，並對其他管理員發安全事件（使用者確認，au1-5 A）。
- Email 可重複；忘記密碼頁輸入「帳號」（使用者確認，au1-3）。
- 忘記密碼一律回同一句；連結單次有效；效期出廠 30 分鐘、同帳號每小時次數出廠 3 次，兩者都是政策；重設後所有 session 失效並寄通知（使用者確認，au1-4）。
- 救援設定同時清掉 `admin` 的雙因素與記住的裝置，log 留 Warning（使用者確認，au1-漏2）。
- 帳號信不能自訂內容；寄件名稱與信中公司名稱讀站台設定（使用者確認，au1-漏3）。
- **Email 一律要驗證，管理員填的也要**（使用者 2026-11-21 裁決）。這條推翻 au1-2 的「管理員在使用者管理填，立即生效」，理由見 §6.5。
- 業界依據：OWASP Forgot Password／Authentication／MFA Cheat Sheet、RFC 6238。

## 5. 資料模型與 migration（T1）

一組 migration `AccountSecurity`（SQLite 與 PostgreSQL 各一個、同名，`scripts/db/add-migration.sh` 產生），全部放在 au1a，au1b、au1c 不再改表（比照 o2a）。站台名稱存既有的 `SystemSetting` 鍵值表，不加欄。

### 5.1 `Dynamic_AppUser` 加欄（T2）

| 欄位 | 型別 | 說明 |
|---|---|---|
| Email | string(255)，可空 | 帳號 Email，只用於帳號信；不唯一（au1-3）；存去空白、原大小寫 |
| EmailVerifiedUtc | DateTime，可空 | null＝未驗證；Email 一改或清空就清成 null |
| TotpSecretProtected | string(500)，可空 | 已啟用的 TOTP 金鑰，Data Protection 加密（purpose `account-totp`，T8） |
| TotpEnabledUtc | DateTime，可空 | 啟用時間；null＝沒啟用 |
| TotpLastStep | long，可空 | 最近一次驗證成功的時間步，擋重放（T7） |
| TotpPendingProtected | string(500)，可空 | 綁定中的金鑰；加密同上 |
| TotpPendingUtc | DateTime，可空 | 綁定開始時間；超過 10 分鐘作廢 |
| TwoFactorFailedCount | int，預設 0 | 帳號層第二步失敗累計；密碼正確不歸零，第二步成功才歸零（§8.3） |
| TwoFactorLockedUntilUtc | DateTime，可空 | 帳號層第二步暫停到何時 |
| TwoFactorGraceStartUtc | DateTime，可空 | 第一次被判定「應啟用而沒啟用」的時間，寬限天數從這裡算（§8.7） |

### 5.2 `Dynamic_UserRecoveryCode`（新表）

| 欄位 | 型別 | 說明 |
|---|---|---|
| Id | long，自動編號主鍵 | |
| UserId | string(50) | 不設外鍵（同 `McpToken`） |
| CodeHash | string(64) | SHA-256 十六進位（T10） |
| CreatedUtc | DateTime | |
| UsedUtc | DateTime，可空 | 條件式更新 `WHERE Id = ? AND UsedUtc IS NULL`，只能用一次 |

索引：`(UserId, CodeHash)` 唯一。

### 5.3 `Dynamic_TrustedDevice`（新表，記住的裝置）

| 欄位 | 型別 | 說明 |
|---|---|---|
| Id | long，自動編號主鍵 | |
| UserId | string(50) | |
| TokenHash | string(64) | 裝置 cookie 明文 32 bytes 的 SHA-256 |
| SecurityStamp | string(50) | 建立當下的 stamp；和帳號目前的 stamp 不同就失效（T23） |
| Label | string(100) | 瀏覽器家族與主版本＋作業系統（例「Chrome 131／macOS」），同前端錯誤回報的取法，不存完整 User-Agent |
| CreatedUtc | DateTime | |
| ExpiresUtc | DateTime | 建立時＝建立時間＋政策天數 |
| LastUsedUtc | DateTime，可空 | |

索引：`TokenHash` 唯一；`UserId`；`ExpiresUtc`（清理）。

### 5.4 `Dynamic_AccountToken`（新表）

| 欄位 | 型別 | 說明 |
|---|---|---|
| Id | long，自動編號主鍵 | |
| UserId | string(50) | |
| Purpose | string(20) | `passwordReset`、`emailVerify` |
| TokenHash | string(64) | 明文 32 bytes 隨機值的 SHA-256 |
| Email | string(255) | 寄到哪個地址；使用時必須等於帳號目前的 Email（§6.3） |
| CreatedUtc | DateTime | ＝寄送時間 |
| ExpiresUtc | DateTime | 依政策（§9） |
| UsedUtc | DateTime，可空 | 用過或被作廢時填 |
| RevokedReason | string(20)，可空 | `used`、`superseded`、`passwordChanged`、`emailChanged`、`deactivated` |

索引：`TokenHash` 唯一；`(UserId, Purpose)`；`ExpiresUtc`。

### 5.5 `Dynamic_MailOutbox`（新表，核心寄信佇列）

| 欄位 | 型別 | 說明 |
|---|---|---|
| Id | long，自動編號主鍵 | |
| Kind | string(30) | §6.4 的信件種類；之後的業務信加種類，不改表 |
| UserId | string(50)，可空 | 收件帳號 |
| ToAddress | string(255) | 寫入時的收件地址快照 |
| Locale | string(10) | 信件語系（§6.4） |
| ParamsJson | string(1000)，可空 | 白名單參數（例：時間、操作者是管理員或本人、裝置標籤）；**不放權杖、密碼、驗證碼** |
| CreatedUtc | DateTime | |
| NotAfterUtc | DateTime，可空 | 過了就不寄、記 `expired` |
| NextAttemptUtc | DateTime | |
| Attempts | int | |
| Status | string(20) | `pending`、`sending`、`sent`、`failed`、`skipped`、`expired` |
| ClaimedBy、ClaimedUntilUtc | string(100)、DateTime，可空 | 同告警佇列 |
| SentUtc | DateTime，可空 | |
| LastError | string(500)，可空 | 階段＋SMTP 回應碼，或略過原因代碼 |
| PreviewBody | string(4000)，可空 | **只在 Demo 站填**（Demo 收件匣，Q12）；正式環境永遠 null |

索引：`(Status, NextAttemptUtc)`；`CreatedUtc`；`(UserId, Status)`（改 Email、改密碼時取消該帳號還沒寄的列）。

### 5.6 SQLite 與 PostgreSQL

只有 EF 依 provider 產生的型別差異（同維運設計 §17.4 末段）；沒有部分索引、沒有 provider 專屬 SQL，兩份 migration 都不手改。新欄全部可空或有預設值，既有列不需搬資料；存 UTC 的欄位名稱都以 `Utc` 結尾。Email 不分大小寫的比對一律在 C# 做（`OrdinalIgnoreCase`），不寫成 SQL 比對（PostgreSQL 的字串相等分大小寫）。

## 6. 核心寄信佇列（au1a）

### 6.1 寄件元件抽到核心（T6）

- `IAlertMailSender` 改名搬到 `MailService` 成為 `IMailSender`，告警與帳號信共用；MailKit 實作與安全邊界一字不改。
- 寄件設定一律讀 `Dynamic_MailSettings`。
- 傳輸方式只有 SMTP；介面是「給一封已組好的信、回階段與回應碼」，O3 若加 HTTP 郵件 API（T18）只多一個實作。

### 6.2 寄送服務（T5）

- 獨立的 hosted service `MailDeliveryService`，不走 `IScheduledJob`（排程器最短 1 分鐘一輪，重設信要更快）。
  - 寫入佇列的交易提交後發記憶體訊號，立刻跑一輪；另外每 30 秒（技術限制）掃一次到期的重試與過期的領取。
  - 部署設定 `Mail:Delivery:Enabled`（預設 true；測試主機關掉，直接呼叫一輪）。
- 每封三步，同告警佇列：**領取**（條件式更新 `pending → sending`、寫 `ClaimedBy` 與 2 分鐘期限）→ **寄送**（交易外，逾時 30 秒）→ **記結果**（`WHERE Id = ? AND ClaimedBy = 我`）。正確性靠條件式更新，不靠租約。
- 不節流、不摘要：沒有每小時上限、沒有通道列鎖。
- 重試 1、5、15、60 分鐘，最多 5 次（沿用 `AlertLimits`）；過了 `NotAfterUtc` 記 `expired`。
- 略過：寄信設定沒設好、密碼解不開、或資格不符（§6.3）→ `skipped`，`LastError` 記原因代碼。
- 失敗：第 5 次仍失敗記 `failed`，開或更新事件 `channel.accountMail`（來源 `channel`、只站內、`channel` 事件永遠不寄信）；之後任何一封帳號信寄成功就結案。
- 資料庫寫不進時沒有記憶體直送（帳號信不是告警）；申請端的處理見 §7.2。

### 6.3 寄送時產生權杖，並重新確認資格（T4；審查必修 2）

佇列不存任何秘密。`passwordReset`、`emailVerify` 兩種信在**領取之後、寄送之前**的短交易裡：

1. 重新檢查資格，不符合就記 `skipped`、不產生權杖：
   - 帳號存在且啟用；
   - `ToAddress` 等於帳號目前的 Email（不分大小寫）；
   - `passwordReset` 另外要 Email 已驗證、而且符合 §7.1 的帳號資格；自助申請的（忘記密碼）還要 `auth.passwordResetEnabled` 為是，管理員寄的（§7.5，`ParamsJson` 標 `by: admin`）不看這項政策（Q20）。使用連結時不再查政策：連結有效期很短，關政策只擋之後的申請。
2. 把同一帳號、同一用途、**同一個 Email** 還沒用過的權杖標 `superseded`（不同 Email 的不動：a 的舊驗證信重寄時，不會作廢 b 的驗證連結）。
3. 產生新權杖（32 bytes 隨機），存雜湊、`ExpiresUtc`＝現在＋政策時效。
4. 明文只放在記憶體裡組信，寄完就丟。

同一封信重試時換一把新權杖、舊的作廢。程式在產生權杖之後、寄出之前當掉：列停在 `sending`，期限過後重領、再換一把，前一把從沒寄出也已作廢。

**改了會影響資格的事，同交易取消還沒寄的信**：改 Email、清空 Email、任何路徑的改密碼（§7.1 第 5 點）、停用帳號時，該帳號 `pending`／`sending` 的 `passwordReset`、`emailVerify` 列標 `skipped`（原因代碼 `emailChanged`、`passwordChanged`、`deactivated`），未用的權杖一併作廢。`sending` 中的那一封若在交易後才寄出，寄出的連結因為第 1、2 步或使用時的檢查（§7.1 第 5 點）而無效。

### 6.4 信件內容與語系

- 種類：`passwordReset`、`emailVerify`、`passwordChanged`、`emailChanged`、`twoFactorEnabled`、`twoFactorDisabled`、`recoveryCodeUsed`、`twoFactorFailures`。
- 文案放 `AccountMailText`（鍵 `accountMail.`），C# 四語表，走 `IMessageCatalog`（語系包可覆寫），同 `AlertMailText`。純文字信。
- 語系：從 `ICurrentLocale` 取（已依啟用語系正規化，不直接讀標頭）。使用者自己申請的信（忘記密碼、本人改 Email 的驗證信）用請求語系；管理員或系統觸發的用系統預設語系（D26；使用者沒有存個人語系）。存在列上，寄送時才組字，**不存內文快照**（帳號信內容只由種類與白名單參數決定，存快照反而要處理權杖明文）。
- **站台名稱**（au1-漏3）：主旨前綴「[站台名稱]」、內文開頭「站台名稱 帳號通知」、寄件顯示名稱都讀外觀設定的「站台名稱」（§12、Q18）。寄件顯示名稱的順序：寄信設定填了寄件人名稱就用它，否則用站台名稱。站台名稱沒設時，主旨不加前綴、內文不寫公司名，寄件人只顯示地址；程式裡不寫死「[Formwright]」。
- 連結網址只用核心站台網址 `App:PublicUrl`（沒設時讀舊鍵 `Ops:PublicUrl`，D20），**不用請求的 Host 標頭**（OWASP：Host 可偽造）。都沒設時寄不出帶連結的信：忘記密碼為 `contactAdmin`、驗證信 `skipped`，寄信設定頁顯示「部署設定沒有站台網址（App:PublicUrl），重設密碼與 Email 驗證信寄不出去」。Demo 站例外（§13.2）。
- 連結格式：`{站台網址}/reset-password#t={權杖}`、`{站台網址}/verify-email#t={權杖}`。權杖在 `#` 後面，不進伺服器 access log、不隨 Referer 送出；前端錯誤回報只送路由樣板（已查證，`error-reporter.ts:70–78`）；頁面讀到就 `history.replaceState` 清掉（T3）。
- 主旨例（繁中，站台名稱「示範科技」）：「[示範科技] 重設密碼」「[示範科技] 驗證你的 Email」「[示範科技] 你的密碼已變更」「[示範科技] 你的帳號 Email 已變更」「[示範科技] 你啟用了雙因素驗證」「[示範科技] 你的雙因素驗證已關閉」「[示範科技] 有人用復原碼登入你的帳號」「[示範科技] 你的帳號驗證碼錯誤次數過多」。內文寫帳號、時間（營運時區並標時區）、連結與時效、「不是你本人操作請聯絡系統管理員」。不寫來源 IP。

### 6.5 Email 與驗證（使用者 2026-11-21 裁決、Q3）

- 管理員：使用者管理每列多「設定 Email」動作，`PUT admin/users/{id}/email`（T17）。
- 本人：「帳號安全」頁改 Email，要輸入目前密碼（密碼檢查走 `LoginThrottle`，§8.9），`PUT account/email`。
- **不論誰填，一律要點驗證信才生效**（使用者 2026-11-21 裁決）：
  - 這條推翻 au1-2 的「管理員在使用者管理填，立即生效」。
  - 理由：Email 是重設密碼的鑰匙。管理員把 `chen@example.com` 打成 `chen@exmaple.com` 時，若立即生效，之後的重設信會寄給陌生人，對方點連結就能接管帳號；要驗證的話，打錯的地址永遠不會被驗證。代價是寄信沒設好的系統裡 Email 永遠是未驗證，忘記密碼也就不可用，但這種系統本來就寄不出重設信。
- 改了（含清空）就：`EmailVerifiedUtc` 清空、作廢該帳號全部未用權杖、取消還沒寄的信（§6.3）；有新地址就寄驗證信；舊地址若已驗證，另寄「Email 已變更」通知（Q5）。
- 驗證：`/verify-email#t=` 頁把權杖送 `POST auth/verify-email`（匿名可呼叫）。權杖有效、沒用過、權杖的 Email 等於帳號目前的 Email → 填 `EmailVerifiedUtc`、權杖標 `used`。
- 重寄驗證信：本人與管理員各一個按鈕；同一帳號 60 秒一次（技術限制）。
- 寄信不可用時：Email 照存，狀態顯示「未驗證（系統目前寄不出驗證信）」。
- 只有已驗證的 Email 會收到重設密碼信與安全通知信。

### 6.6 帳號信寄送紀錄

寄信設定頁多一區「帳號信寄送紀錄」，`GET system/mail-outbox?page=`（系統管理員），依建立時間新到舊、每頁 50 筆：時間、種類、帳號、收件地址、狀態、嘗試次數、最近錯誤。清單不含內文；Demo 站多「看信」按鈕顯示 `PreviewBody`（Q12）。

保留：政策 `mail.outboxRetentionDays`（出廠 30 天，分類 `ops`，同其他保留天數），由既有的 `ops.upkeep` 清理。**只清終態**（`sent`、`failed`、`skipped`、`expired`），不清 `pending`／`sending`。同一次清理也刪過期超過同樣天數的 `Dynamic_AccountToken` 與已過期的 `Dynamic_TrustedDevice`。

## 7. 寄信重設密碼（au1b）

### 7.1 流程

1. 登入頁「忘記密碼？」→ `/forgot-password`，輸入帳號（au1-3）→ `POST auth/forgot-password { userId }`。輸入照 `LoginHandler` 正規化：trim、轉小寫、限流鍵截 128 字。
2. 伺服器**一律**回 202 與同一句：「如果這個帳號存在並設定了已驗證的 Email，我們已寄出重設密碼的信。信裡的連結 {minutes} 分鐘內有效。」
3. 符合資格才寫一列 `passwordReset`（`NotAfterUtc`＝現在＋連結時效）。資格：
   - 帳號存在、啟用、Email 已驗證；
   - 政策 `auth.passwordResetEnabled` 為是、寄信可用（§7.4）；
   - 正式環境：不是示範帳號；Demo 站：**只限** `mfa01`，其他示範帳號與 `admin` 一律排除（§13、審查必修 4、Q22）；
   - 系統管理員可以用（au1-5）。
4. 使用者點連結 → `/reset-password#t=`，頁面先 `POST auth/reset-password/check { token }`（不消耗），有效才顯示新密碼表單，無效顯示「連結已失效或已使用過，請重新申請」與「重新申請」。
5. `POST auth/reset-password { token, newPassword }`：權杖有效、沒用過、用途對、帳號仍啟用、**權杖的 Email 等於帳號目前已驗證的 Email** → 同一交易：
   - 設新密碼雜湊；`MustChangePassword`＝false；`FailedLoginCount`＝0、`LockedUntil`＝null；
   - 換 `SecurityStamp`（全部 session 與記住的裝置失效，T23）；
   - 作廢全部未用的 `passwordReset` 權杖、取消還沒寄的重設信；
   - 撤銷全部 AI 權杖（Q4）；
   - 寫 `passwordChanged` 通知（au1-4）；
   - 帳號是系統管理員時開安全事件（au1-5，§8.10）；
   - 稽核 `account.passwordResetByMail`。
6. 回 200，前端導到登入頁並提示「密碼已重設，請用新密碼登入」。不自動登入（Q4）；有雙因素的帳號登入時照樣要驗證碼（au1-7）。
7. **其他改密碼路徑也作廢重設連結**（審查必修 3）：本人改密碼、管理員設暫時密碼、啟動救援，同交易作廢未用的 `passwordReset` 權杖、取消還沒寄的重設信、寫 `passwordChanged` 通知。

### 7.2 不透露帳號狀態

- 申請 API 的回應碼、內容、欄位都相同；每次都做同樣的查詢與一次雜湊，處理時間拉近（D21）。
- 節流以**正規化後的輸入**計次，不論帳號存不存在（§7.3）；被節流時照樣跑同樣的查詢。
- 資料庫寫不進（唯讀、空間滿）時照樣回 202，只記 Warning log；`auth/options` 在看門狗判定資料庫不可寫時回 `contactAdmin`（不讓 503／202 的差異變成探測帳號的管道）。
- 重設 API 對無效權杖一律回 400 `resetLinkInvalid`，不分原因。
- 稽核：申請寫 `account.passwordResetRequested`（Success＝排進佇列；Denied＋原因代碼 `unknownAccount`、`inactive`、`noVerifiedEmail`、`disabled`、`unavailable`、`demoRestricted`（Demo 站非 `mfa01`，§13.2））。帳號不存在時不記輸入。被節流的不逐筆寫，只加計數。

### 7.3 防濫用（申請端節流）

- 同一個正規化輸入 1 小時內最多 `auth.passwordResetMaxPerHour` 次（出廠 3，政策，au1-4）。
- 同一來源 IP 15 分鐘內最多 10 次申請（寫死，安全檢查，§22）。
- 用權杖的匿名端點（`reset-password/check`、`reset-password`、`verify-email`）另有每 IP 15 分鐘 30 次（寫死，§22；複審建議 12）：權杖 256 位元猜不到，這是擋濫用與 PBKDF2 造成的負載。
- 都存記憶體、單一實例，同 `LoginThrottle`。超過時照樣回 202、不寄。

這是擋「替別人一直按忘記密碼」與「掃帳號」，不是佇列節流；使用者裁決的「不節流」指佇列裡已排的信不會被延後或併成摘要。

### 7.4 可不可用

`GET auth/options` 多回 `passwordReset: "available" | "contactAdmin"` 與 `passwordMinLength`：

- `available`：政策開，且「寄信設定已設好、站台網址有設、資料庫可寫」三項都成立；或 Demo 站（Demo 收件匣，§13.2）。
- `contactAdmin`：其餘情況。登入頁「忘記密碼？」照樣顯示，點進去只寫「這個系統目前無法寄送重設密碼信，請聯絡系統管理員重設密碼」（Q11）。

這個值只透露系統有沒有設寄信，不透露帳號狀態。

### 7.5 管理員替別人重設密碼（au1-11）

- 使用者管理的「重設密碼」對話框有兩個選項：
  - 「寄重設連結到 Email」：對方 Email 已驗證時預設選這個。
  - 「設定暫時密碼（下次登入要改）」：既有行為。對方沒有已驗證 Email 時只有這個。
- 寄連結：`POST admin/users/{id}/send-reset-link`，寫一列 `passwordReset`（系統預設語系），**不**改現有密碼、不登出對方；不受 `auth.passwordResetMaxPerHour` 限制，但同一帳號 60 秒一次（同重寄驗證信）；稽核 `user.sendResetLink`。Demo 站只能寄給 `mfa01`（Q22）。
- 暫時密碼照既有流程，另加 §7.1 第 7 點的作廢與通知。

### 7.6 密碼最短長度（D25）

- 新政策 `auth.passwordMinLength`（出廠 8，技術下限 8、上限 128）。
- 後端 `ChangePasswordValidator`、`CreateUserValidator`、`ResetPasswordValidator`、重設頁、`AdminBootstrap` 都讀政策生效值；刪掉 `AdminBootstrap.cs:15` 與 `ChangePasswordValidator.MinLength` 的常數。
- 前端 `UsersPage.vue` 的 `MIN_PASSWORD`、`ChangePasswordPage.vue` 的 `MIN_LENGTH` 改讀 `auth/options` 的 `passwordMinLength`。
- 政策調嚴不溯及既有：既有密碼照常能登入，下次改密碼時才檢查。

## 8. 雙因素登入 TOTP（au1c）

### 8.1 演算法（T7）

- RFC 6238：HMAC-SHA1、6 位數、30 秒一步、容許前後各一步（技術限制，主流 App 相容）。
- 自己寫在 Domain（純函式），用 RFC 6238 附錄 B 的測試向量驗證；不引入套件。比對用 `CryptographicOperations.FixedTimeEquals`。
- 金鑰 20 bytes 隨機，Base32 顯示；`otpauth://totp/{issuer}:{userId}?secret=...&issuer={issuer}`，label 與 issuer 都做 URL 編碼。issuer 用站台名稱，沒設用站台網址的主機名稱，都沒有用 `Formwright`（T12）。
- 重放：驗證成功記 `TotpLastStep`；同一步或更早的碼一律拒絕（RFC 6238 §5.2）。寫入用條件式更新 `WHERE TotpLastStep IS NULL OR TotpLastStep < ?`，並行送同一碼只有一個成功。

### 8.2 綁定（Q15）

在「帳號安全」頁按「啟用雙因素驗證」，對話框分四步：

1. 輸入目前密碼 → `POST account/two-factor/setup { password }`：產生金鑰存進 `TotpPendingProtected`，回 Base32 金鑰與 `otpauth` URI（回應加 `Cache-Control: no-store`）。前端畫 QR 碼（T11），同時顯示可複製的金鑰。
2. 用 App 掃碼。
3. 輸入 6 位數 → `POST account/two-factor/enable { code }`：通過才轉正，清掉綁定中欄位；產生 10 組復原碼；換 `SecurityStamp` 並重發本 session cookie；稽核 `account.twoFactorEnable`；寄 `twoFactorEnabled` 通知（審查必修 11：只拿到密碼的人可能替還沒啟用的帳號綁上自己的驗證器）。
4. 顯示復原碼（只這一次），可複製、可下載；勾「我已保存復原碼」才能按完成。

- 綁定中超過 10 分鐘沒完成就作廢。
- **重新綁定**（換手機）：同上四步，第 1 步另要目前的驗證碼或一組復原碼（用復原碼會用掉那一組）；完成時舊金鑰與舊復原碼全部取代。
- 金鑰不落地（`KeyPersistenceStatus.Persisted` 為否，§8.7）時第 1 步回 409 `keysNotPersisted`：「伺服器沒有保存加密金鑰，重啟後雙因素會失效，請管理員先完成部署設定」（T8、審查必修 12）。
- Demo 站：只有種子的 `mfa01` 有雙因素，任何帳號都不能綁定，API 回 403 `demoSite`（§13）。

### 8.3 登入第二步與猜碼上限（T9；審查必修 1）

1. `POST auth/login` 密碼正確、帳號已啟用雙因素：
   - 帶著有效的「記住這台裝置」cookie（§8.5）→ 直接發 session，稽核 `login.succeeded`、`method` 為 `password+device`。
   - 否則**不發 session cookie**，改發 5 分鐘的第二步憑證 cookie：Data Protection 加密（purpose `login-second-step`，和登入 cookie 分開）；內容是帳號、`SecurityStamp`、發出時間、隨機編號；HttpOnly、SameSite=Strict、`SecurePolicy` 比照 session cookie、Path 限 `/api/v3/dynamic/auth/two-factor`。回 200 `{ twoFactorRequired: true }`。
   - 這時**不算成功、也不算失敗**（複審 N1）：`LoginHandler` 對密碼那一次保留的名額呼叫 `LoginThrottle.Release`（歸還帳號＋IP、IP、帳號三層的進行中名額，不清失敗紀錄、也不記一次失敗），不寫 `login.succeeded`。
   - 第二步每次送碼（含復原碼）自己呼叫一次 `TryBegin`（同一組帳號、IP 鍵）：被擋就回暫停訊息；碼錯呼叫 `Complete(false)`；碼對呼叫 `Complete(true)`（這時才清帳號＋IP 層）；TOTP 金鑰解不開、帳號層暫停中、例外時 `Release`（金鑰解不開的那一次反正猜不中，不讓本人被帳號＋IP 層擋住；複審建議 D）。
   - 憑記住的裝置直接登入時，密碼那一次照現行 `Complete(true)`。
   - Demo 一鍵登入的 `mfa01` 進到第二步時比照：`DemoLoginHandler` 對那一次改呼叫 `Release`，第二步照上述結算。
   - 結果：正常的雙因素登入不會被算成失敗（同一個 NAT 出口的整間辦公室不會被擋），名額也不會卡住（F33）。
2. 前端在同一個登入頁換成第二步：6 位數輸入框（`inputmode="numeric"`、`autocomplete="one-time-code"`）、「記住這台裝置 {days} 天」勾選框（政策為 0 時不顯示）、「改用復原碼」、「回到登入」。
3. `POST auth/two-factor { code | recoveryCode, rememberDevice }`：
   - 先檢查：憑證解得開、未過期、隨機編號沒用過、stamp 沒變、帳號仍啟用（停用會換 stamp，這裡另外明寫一次，避免日後改停用流程時漏掉）、帳號層沒有暫停。
   - 驗 `recoveryCode` 只比對雜湊，不解 TOTP 金鑰；送的是 `code` 時，**在佔帳號層名額之前**先解金鑰，解不開就直接回「驗證碼錯誤或已過期」，不佔帳號層名額（稽核原因 `twoFactorUnreadable`，避免本人因金鑰遺失被暫停，之後仍能用復原碼登入；複審 N6）。
   - 處理順序：憑證檢查 → `TryBegin` → （`code` 時）解金鑰 → 佔帳號層名額並提交（第 4 點）→ 比對碼 → 結算。
   - 碼正確 → 發 session cookie、清憑證 cookie、把隨機編號在記憶體標成已用（同一張憑證不能換第二個 session）、`TwoFactorFailedCount` 與 `TwoFactorLockedUntilUtc` 一起清掉（複審 N6）；勾了記住就發裝置 cookie（§8.5）；稽核 `login.succeeded`，`method` 為 `password+totp` 或 `password+recovery`；用復原碼時寄 `recoveryCodeUsed` 通知（Q5）。
4. 錯誤處理，三層：
   - **同一張憑證**最多 5 次（記憶體），用完要重新輸入密碼。
   - **帳號＋IP**：每次錯誤計入 `LoginThrottle`，和猜密碼同一個額度。
   - **帳號層**（存資料庫，換 IP、反覆重新輸入密碼都不歸零）：`TwoFactorFailedCount` 累計到 10 次就暫停這個帳號的第二步 15 分鐘（`TwoFactorLockedUntilUtc`），暫停期間碼正確也拒絕；開安全事件 `security.twoFactorFailures`（§8.10）、寄 `twoFactorFailures` 通知到已驗證的 Email（「密碼正確但驗證碼錯誤多次」，Q19）。能走到第二步的人已經知道密碼，所以按帳號擋不會讓匿名者鎖住任何人。
     - 佔名額（複審建議 1、N6）：驗碼前用**一條**原子更新同時遞增計數與寫暫停時間，到期重置也在同一條：

       ```sql
       UPDATE Dynamic_AppUser
       SET TwoFactorFailedCount = CASE WHEN TwoFactorLockedUntilUtc IS NOT NULL THEN 1
                                       ELSE TwoFactorFailedCount + 1 END,
           TwoFactorLockedUntilUtc = CASE WHEN TwoFactorLockedUntilUtc IS NULL
                                           AND TwoFactorFailedCount + 1 >= 10 THEN @until
                                          ELSE NULL END
       WHERE UserId = @id
         AND ((TwoFactorLockedUntilUtc IS NULL AND TwoFactorFailedCount < 10)
              OR TwoFactorLockedUntilUtc <= @now)
       ```

       以上是語意示意，實作用 `ExecuteUpdate` 或經 `ISqlDialect` 的 raw SQL（識別字加引號）。`SET` 裡讀的都是更新前的值（PostgreSQL 與 SQLite 相同）。影響 0 列就當暫停中拒絕。欄位只會處在「計數小於 10、沒有暫停時間」或「計數 10、有暫停時間」兩種狀態，不會卡在「計數 10、沒有暫停時間」；除了這一條、成功時的清除（同一列的 `SET TwoFactorFailedCount = 0, TwoFactorLockedUntilUtc = NULL`）與 §8.8、§8.11 的整組清除，沒有其他地方單獨寫這兩欄。多個 IP 同時送出也不會超過 10 次（PostgreSQL 在 READ COMMITTED 下取得列鎖後重新評估 `WHERE`，SQLite 寫入本來就序列化）。
     - 佔到名額就算一次嘗試：之後碼錯、程式例外或重啟都不退還，15 分鐘內最多就是 10 次，暫停到期自動恢復。
     - **在請求交易之外提交**（複審 N5）：`TransactionBehavior` 在結果失敗時整個交易回滾（`Application/Common/Behaviors/TransactionBehavior.cs:20–21`），失敗路徑的寫入若在交易內就全部消失。所以 `auth/two-factor` 比照 `LoginHandler`，**不**實作 `ITransactionalRequest`：佔名額的更新立即提交；失敗路徑的稽核、安全事件、通知也各自提交；成功路徑的寫入（清計數、`TotpLastStep`、標復原碼已用、記住的裝置、`login.succeeded`）由 handler 自己開一個短交易一起提交，這時名額已提交，不受影響。**不**在交易內另開 scope 寫計數：SQLite 的外層交易已 `BEGIN IMMEDIATE` 並持有 `WriteGate`（`Persistence/EfTransactionManager.cs:13`），另開的連線會等鎖到逾時。
     - 要不要開安全事件、寄通知：佔名額的更新提交後重讀該列，暫停時間等於這一次的 `@until`（也就是這一次觸發了暫停）才處理。
     - 通知去重（使用者 2026-11-21 裁決，Q23）：同一帳號 24 小時內只通知一次，信（`twoFactorFailures`）與站內通知都一樣；安全事件同指紋聚合、只累加次數。24 小時寫死，列 §22。
     - **Demo 站不套帳號層**（領域顧問必修）：任何訪客都知道 `mfa01` 怎麼進第二步，套了就能反覆把 `mfa01` 鎖住；Demo 站只留同一憑證 5 次與 `LoginThrottle` 兩層（F35）。已知限制（複審建議 F）：`LoginThrottle` 的帳號層是全站共用的 `mfa01` 鍵，有人從多個 IP 每小時錯 50 次以上時，所有訪客的 `mfa01` 第二步會被拖慢到每 10 秒放行一次；影響只在 Demo，記進 `docs/demo.md`，不另寫 Demo 專屬分支。
   - 數字寫死（安全檢查，§22）。估算：每 15 分鐘最多 10 次，一天最多約 960 次，TOTP 一次猜中約 3/10⁶，一天猜中機率約 0.3%，而且第一個 15 分鐘就會寄信、開安全事件讓本人與管理員知道。
5. 稽核 `login.failed`，原因 `twoFactorWrong`、`recoveryCodeWrong`、`twoFactorExpired`、`twoFactorLocked`；既有的 `security.loginFailures` 健康檢查因此也算到猜碼。
6. 回應訊息一律「驗證碼錯誤或已過期」；暫停時是「驗證碼錯誤次數過多，請 15 分鐘後再試」。
7. 帳號同時要改密碼：先過第二步，進來後再改密碼。
8. Demo 一鍵登入：帳號已啟用雙因素（只有 `mfa01`）時同樣走第二步；其餘照舊一鍵進入。

### 8.4 復原碼（T10、T24）

- 10 組，每組 16 個 Base32 字元，顯示成 `ABCD-EFGH-JKLM-NPQR`（輸入時忽略大小寫、空白與連字號）；存 SHA-256，比對用固定時間比較。
- 雜湊的輸入是**正規化後的 16 個大寫 Base32 字元**（去掉連字號與空白、轉大寫），產生與比對用同一個 Domain 函式；這是給 bk1 的契約的一部分（§19，複審建議 10）。
- 每組只能用一次。用復原碼登入成功後提示「已使用 1 組復原碼，還剩 N 組」；剩 2 組以下時，登入後與「帳號安全」頁提示重新產生。
- 重新產生：`POST account/two-factor/recovery-codes { password, code }`（回應 `no-store`），舊的全部作廢。稽核 `account.recoveryCodesRegenerate`。
- TOTP 金鑰解不開時（§19 風險 1）驗證碼驗不了，所以重新產生也做不了；流程是「用復原碼登入 → 用復原碼重新綁定（§8.2）→ 綁定完成時自動拿到 10 組新復原碼」。

### 8.5 記住這台裝置（au1-9）

- 政策 `auth.twoFactorRememberDays`（出廠 30，0＝關閉，上限 90）。
- 第二步勾了「記住這台裝置」→ 發 cookie `fw.device`：明文 32 bytes 隨機值，HttpOnly、SameSite=Strict、`SecurePolicy` 比照 session、Path 限 `/api/v3/dynamic/auth`、`Expires`＝建立時間＋政策天數；資料庫存雜湊與當下的 `SecurityStamp`（§5.3）。
- 有效條件（全部成立才跳過第二步）：雜湊對得到、屬於這個帳號、沒過期、`SecurityStamp` 等於帳號目前的 stamp、`CreatedUtc`＋**目前**政策天數還沒到（政策調短或設 0 立即生效，T23）。
- **全部失效**（au1-9）：改密碼（任何路徑）、重設或關閉或重新綁定雙因素、管理員重設、停用帳號、救援設定都會換 stamp，所有記住的裝置隨之失效；同交易刪掉該帳號全部列（清理用，不靠它判斷）。
- 「帳號安全」頁顯示「記住的裝置：N 台」與每台的標籤、最近使用時間，附「全部取消記住」（Q17）。Demo 站的「全部取消記住」回 403 `demoSite`（`mfa01` 是共用帳號，一位訪客按了會讓其他訪客記住的裝置失效）。
- 只跳過第二步，密碼照樣要輸入。
- 記住的裝置**不受帳號層暫停影響**（複審建議 3，刻意的取捨）：本人被攻擊時，自己記住的裝置仍能登入；攻擊者沒有那台裝置的 cookie。
- 每個帳號最多 20 台（技術限制，§22），超過時刪最舊的一台。

### 8.6 本人關閉（Q15）

- 本人不在強制對象內（§8.7）時，「帳號安全」頁有「關閉雙因素驗證」：要目前密碼＋驗證碼（或復原碼），`POST account/two-factor/disable`。
- 清金鑰、刪復原碼、換 stamp（其他裝置登出、記住的裝置失效）；寄 `twoFactorDisabled` 通知；稽核 `account.twoFactorDisable`。
- 在強制對象內時不提供關閉（API 回 409 `twoFactorRequired`），只能重新綁定。

### 8.7 強制政策（使用者確認：au1-漏1、au1-6、au1-7）

四項政策（全系統），政策頁顯示成「雙因素驗證：選用／強制」，強制時再選範圍：

| 代碼 | 值 | 出廠 |
|---|---|---|
| `auth.twoFactorMode` | `optional`（選用）、`all`（強制全部使用者）、`groups`（強制指定群組） | `optional` |
| `auth.twoFactorGroups` | 群組清單，`groups` 模式才用 | 空 |
| `auth.twoFactorRequireAdmins` | 選用模式下系統管理員是否仍強制 | 是 |
| `auth.twoFactorGraceDays` | 寬限天數，0＝立即（上限 90） | 0 |

- **強制對象**：
  - `all`：全部使用者。
  - `groups`：`auth.twoFactorGroups` 任一群組的成員，加上全部系統管理員（au1-6：系統管理員一律在內）。
  - `optional`：`auth.twoFactorRequireAdmins` 為是時，只有系統管理員；為否時沒有人。
  - 「系統管理員」指 `IsSystemAdmin`，不含只持有系統權限的一般使用者。
- 強制模式下 `auth.twoFactorRequireAdmins` 在政策頁顯示為「強制時固定包含系統管理員」、不能改（存檔時忽略）。
- **應啟用而沒啟用**的帳號：
  1. 第一次被判定時記 `TwoFactorGraceStartUtc`；之後判定不在強制對象內（政策放寬、離開群組、不再是系統管理員）時清空，下次再進入範圍時寬限從頭算（複審建議 8）。
     - 判定發生在使用者自己的請求裡，期間沒登入的人起點不會被清（複審建議 C）。所以存強制政策（`auth.twoFactorMode`、`auth.twoFactorGroups`、`auth.twoFactorRequireAdmins`）時，同一交易用和 middleware 同一個範圍條件，一次清掉所有不在新範圍內、還沒啟用的帳號的起點（F40）。
     - 群組成員異動、拿掉系統管理員不做這個批次清除，只靠下次請求時的判定：期間沒登入、之後又回到範圍內的人，寬限接著舊起點算。這是刻意的取捨（這類異動多、批次清除要掛在好幾個 handler 上），手冊寫明。
  2. 寬限期間（`TwoFactorGraceStartUtc`＋`auth.twoFactorGraceDays` 之前）：照常使用，每頁頂端顯示「請在 {date} 前啟用雙因素驗證」與「現在啟用」。
  3. 寬限期過了（出廠 0＝第一次就過了）：發一般 session，但 `SessionMiddleware` 只放行 `/auth/*`、`GET /me`、`account/two-factor/setup`、`account/two-factor/enable`，其餘回 403 `twoFactorEnrollmentRequired`；前端導到強制綁定頁 `/account/two-factor-setup`（同 §8.2，沒有「取消」）。
  4. 綁定完成時清 `TwoFactorGraceStartUtc`。
- 已登入的人：政策改成強制後，下一個請求起依上述判斷（系統政策快取 60 秒，最多晚一分鐘）。
- 同時要改密碼：先改密碼，再綁定。
- `SessionMiddleware` 每個請求在同一次依主鍵的查詢裡多讀 `IsSystemAdmin`、`TotpEnabledUtc`、`TwoFactorGraceStartUtc`，`groups` 模式時多一個群組成員的 `EXISTS` 子查詢（同一次往返）。
- `GET /me` 多回 `twoFactor: { enabled, required, graceUntil, recoveryCodesLeft, rememberedDevices }` 與 `twoFactorEnrollmentRequired`。
- 政策頁改這幾項時，若操作者自己因此要立即綁定而還沒綁，存檔前提示「存檔後你也必須立刻啟用雙因素驗證」。
- 政策讀不到時沿用最後一次讀到的值，啟動後從沒讀到過就用出廠值（T13）。資料庫讀不到時本來就登不進來，實際上影響不到。
- **金鑰落地的判斷與正式環境守衛**（T27，主控裁決，複審 N2、N3）：
  - 判斷以 `Program.cs` 實際算出的 `keysDirectory` 為準，只算一次：`KeysPersisted = keysDirectory is not null`，以單一服務 `KeyPersistenceStatus` 注入，§8.2、本節、§14、監控頁都引用它，不各自讀設定鍵。SQLite 檔案資料庫（絕對路徑的 `Data Source=`）沒設 `DataProtection:KeysDirectory` 時金鑰在 `<資料庫目錄>/keys`，**算落地**（客戶用的 `Dockerfile` 與 `Dockerfile.demo` 都是 `Data Source=/app/data/poc.db`，屬於這種），綁定與強制照常。
  - **非 Development 環境拒絕啟動**（複審 N3、N7）：環境不是 Development、`keysDirectory` 為 null 時，**不分資料庫種類**，啟動直接丟例外結束，訊息「非開發環境必須設定 DataProtection:KeysDirectory（否則重啟後登入、寄信密碼與雙因素全部失效）」。涵蓋 PostgreSQL、SQLite 記憶體資料庫、`file:` URI 寫法的 SQLite；例外在 `builder.Build()` 之前丟出，還沒連資料庫。取代 `Program.cs:145–147` 現在的 PostgreSQL 啟動警告。非 Development 環境因此不存在「金鑰不落地」的狀態，強制政策不會被暫停（不 fail-open，守住 au1-漏1 的「系統管理員一律強制」）。列 §22。
  - **金鑰目錄有設、但不持久**（複審建議 B）：拒絕啟動只檢查有沒有設，偵測不到「設在容器裡沒掛 volume 的路徑」。補一道偵測：啟動時在 Data Protection 第一次使用前看金鑰目錄裡有沒有 `key-*.xml`；沒有（等於這次會產生新金鑰）而資料庫裡已有 `TotpSecretProtected` 不是空的帳號，開基礎設施事件 `infra.keysMayBeLost`「金鑰可能遺失：已綁定的雙因素與寄信密碼可能解不開」。Demo 站建置時刪掉金鑰，但建置的種子不寫 `mfa01` 的密文（§13.3），而且偵測排在重新加密 `mfa01` 之前，所以不會誤報。
  - **Development 環境**金鑰不落地時：新綁定照樣 409；強制對所有人暫停（否則開發機與測試主機的管理員會卡在綁不了的綁定頁），啟動記 Warning、開基礎設施事件 `infra.keysNotPersisted`，帳號安全頁顯示「伺服器沒有保存加密金鑰，雙因素無法啟用」。這個暫停只在 Development 環境出現，其他環境走不到。
- **Demo 站**（審查必修 4，守衛以站為準、不以帳號為準）：開 `Demo:LoginEnabled` 時強制政策對**所有帳號**（含 `admin`）都不生效；`auth` 分類的政策在 Demo 站唯讀（PUT 回 403 `demoSite`），訪客改不了。出廠的「系統管理員強制」因此不會讓 Demo 的 `admin` 卡在綁定頁（Demo 的 `admin` 密碼是啟動時隨機產生的，沒人能完成綁定）。

### 8.8 管理員重設雙因素（使用者確認，au1-8、au1-10）

- 使用者管理每列多「重設雙因素」（對方已啟用時才出現），確認對話框寫明「會清除這位使用者的驗證器、復原碼與記住的裝置，並登出他所有的裝置」。`POST admin/users/{id}/reset-two-factor`（`ISystemAdminRequest`）。
- `id` 是自己時拒絕（400 `useAccountSecurity`），要走本人關閉或重新綁定（要驗證碼），避免偷到 session 的人不用驗證碼就拿掉管理員的雙因素。
- 清金鑰、刪復原碼、刪記住的裝置、清 `TwoFactorFailedCount`、`TwoFactorLockedUntilUtc`、`TotpPending*`、`TwoFactorGraceStartUtc`（暫停中的本人因此能立刻重新進來，Q19）、換 stamp；**不**撤銷 AI 權杖（au1-10）；寄 `twoFactorDisabled` 通知（參數標「管理員」）；稽核 `user.resetTwoFactor`（一般寫入，T16）。
- 對方是系統管理員時開安全事件（au1-8，§8.10）。
- 重設後對方若在強制對象內，下次登入照 §8.7 重新綁定（寬限從頭算）。
- Demo 站回 403 `demoSite`（重設 `mfa01` 會讓當天的示範壞掉）。
- 唯一的系統管理員手機遺失又沒有復原碼：啟動救援（§8.11）。

### 8.9 重新驗證的端點也要限流（審查必修 8）

- `PUT account/email`、`account/two-factor/setup`、`disable`、`recovery-codes`、建 AI 權杖、改密碼：密碼檢查一律走 `LoginThrottle`（帳號＋IP），失敗寫稽核 `account.reauthFailed`。
- 驗證碼與復原碼檢查共用 §8.3 的帳號層計數與暫停，處理順序與「金鑰解不開不佔名額」同 §8.3。
- **結算與第二步一致**（複審建議 D）：只要密碼的端點，密碼對 `Complete(true)`、錯 `Complete(false)`；同時要密碼與驗證碼的，密碼對時 `Release`，由驗證碼的結果結算 `Complete`。現在 `CreateMcpTokenHandler` 的 `VerifyPassword`（`McpTokenHandlers.cs:164–181`）密碼對就 `Complete(true)`，會清掉帳號＋IP 層，跟著改。
- **拆成兩段**（複審 N5）：這些端點都會寫業務資料，照後端規則是 `ITransactionalRequest`，失敗時交易回滾會吃掉帳號層名額與 `reauthFailed`。所以每個端點拆成外層與內層：
  - 外層請求**不**開交易：驗密碼、驗碼，佔名額與失敗路徑的稽核、事件、通知各自立即提交；驗不過就回失敗。
  - 驗過才送內層的 `ITransactionalRequest` 做業務寫入（改 Email、綁定、關閉、重新產生、建權杖、改密碼）。
  - 重新驗證的共用邏輯收在一個服務（例：`IReauthVerifier`），六個端點共用。
- 既有的 `ChangePasswordHandler` 沒有限流，au1b 一起補上同一套（含拆兩段）。

### 8.10 安全事件

- 新增事件代碼，類別「安全」，同 `admin.changed` 的規則（每次發生都通知站內收件人 `ops.alertRecipients`，Email 依告警通道設定）。**例外是 `security.twoFactorFailures`**：同帳號 24 小時內只通知一次，之後只累加次數（Q23，§8.3）。
  - `security.adminEmailChanged`：系統管理員改了另一位系統管理員的 Email（複審建議 11，Q21；Email 就是重設密碼的鑰匙）。在管理員**送出時**就開，不等新信箱驗證（接管的前置動作是「改」，驗證只是後續）；清空另一位管理員的 Email 也開。歸 au1a。
  - `security.adminPasswordReset`：系統管理員帳號用寄信重設了密碼（au1-5）。歸 au1b。
  - `security.adminTwoFactorReset`：重設了另一位系統管理員的雙因素（au1-8）。
  - `security.twoFactorFailures`：某帳號第二步連續失敗達上限（§8.3）。
  - `security.rescueConfigLeft`：啟動時偵測到 `Auth:ResetAdminPassword` 仍設著（§8.11）。
  - 後三種（`adminTwoFactorReset`、`twoFactorFailures`、`rescueConfigLeft`）歸 au1c。安全事件代碼的登記機制隨第一個用到的段（au1a）建立。
- 樣本只存稽核 Id 與帳號，不存碼、權杖、IP 以外的明細（同維運設計 §17.2.2）。
- 收件人是政策設定的站內收件人（出廠全部系統管理員），會包含當事人自己；分流題 au1-5 的「對其他管理員發安全事件」以「全部管理員都看得到」落實（T25）。

### 8.11 啟動救援（使用者確認，au1-漏2）

- `Auth:ResetAdminPassword` 重設 `admin` 密碼時，同時清除雙因素（`TotpSecretProtected`、`TotpEnabledUtc`、`TotpLastStep`、`TotpPending*`）、復原碼、記住的裝置、`TwoFactorFailedCount`、`TwoFactorLockedUntilUtc`、`TwoFactorGraceStartUtc`，作廢重設連結；log 留 Warning（複審建議 7：避免救援後第一次綁定卡在舊的暫停或舊的寬限起點）。
- 每次啟動都偵測到這個設定時開 `security.rescueConfigLeft`（審查建議），提醒用完要移除；否則每次重啟都會再清一次。

### 8.12 AI 連線權杖（使用者確認，au1-10）

- 建權杖本來就要重新輸入密碼；帳號已啟用雙因素時另要驗證碼或復原碼（`CreateMcpTokenRequest` 加 `Code`），共用 §8.3 的帳號層計數。
- 權杖照常使用，不因雙因素狀態暫停；重設雙因素不撤銷權杖。初稿的「應啟用而沒啟用時權杖暫停」（初稿 T14）依 au1-10 拿掉。

## 9. 政策項（單一來源）

全部登記在 `PolicyCatalog`，只限全系統；前端從政策 API 取得，`shared/policy-catalog.json` 補文案代碼。新分類 `auth`（畫面名稱「帳號安全」）十項；保留天數放既有的 `ops` 分類。

| 代碼 | 分類 | 型別 | 出廠 | 技術上下限 | 用在 |
|---|---|---|---|---|---|
| `auth.passwordResetEnabled` | auth | Boolean | true | — | §7.4；政策頁名稱「登入頁可自助申請重設密碼」，說明寫明不影響管理員寄重設連結（Q20） |
| `auth.passwordResetLinkMinutes` | auth | Integer（分鐘） | 30 | 10–1440 | 連結時效；也是重設信最晚寄出時間 |
| `auth.passwordResetMaxPerHour` | auth | Integer（次） | 3 | 1–20 | §7.3（au1-4） |
| `auth.emailVerifyLinkHours` | auth | Integer（小時） | 24 | 1–168 | 驗證連結時效 |
| `auth.passwordMinLength` | auth | Integer（字元） | 8 | 8–128 | §7.6（D25） |
| `auth.twoFactorMode` | auth | Choice | `optional` | `optional`、`all`、`groups` | §8.7 |
| `auth.twoFactorGroups` | auth | 群組清單 | 空 | 0–50 項 | §8.7（T26） |
| `auth.twoFactorRequireAdmins` | auth | Boolean | true | — | §8.7 |
| `auth.twoFactorGraceDays` | auth | Integer（天） | 0 | 0–90 | §8.7（au1-7） |
| `auth.twoFactorRememberDays` | auth | Integer（天） | 30 | 0–90 | §8.5（au1-9） |
| `mail.outboxRetentionDays` | ops | Integer（天） | 30 | 1–365 | §6.6 |

- 時效與次數在 Application 讀政策生效值；前端提示文字裡的數字（分鐘、天數、最短長度）由 API 帶回，不另寫一份。
- 政策調嚴：連結時效只影響之後寄出的連結；密碼長度下次改密碼才檢查；記住裝置的天數立即套用到既有裝置（安全時間窗，T23）；強制政策依 §8.7 與寬限天數生效。

## 10. API

全部在 `/api/v3/dynamic` 底下。

| 方法 | 路徑 | 身分 | 用途 |
|---|---|---|---|
| GET | `auth/options` | 匿名 | 多回 `passwordReset`、`passwordMinLength`、`rememberDeviceDays` |
| POST | `auth/forgot-password` | 匿名 | 申請重設信；一律 202 |
| POST | `auth/reset-password/check` | 匿名 | 檢查權杖有效（不消耗） |
| POST | `auth/reset-password` | 匿名 | 用權杖設新密碼 |
| POST | `auth/verify-email` | 匿名 | 驗證 Email |
| POST | `auth/login` | 匿名 | 既有；有雙因素時回 `{ twoFactorRequired: true }` 或憑裝置 cookie 直接登入 |
| POST | `auth/two-factor` | 第二步憑證 | 驗證碼或復原碼，成功發 session |
| GET | `account/security` | 本人 | Email、驗證狀態、雙因素狀態、復原碼剩餘、記住的裝置、強制與寬限 |
| PUT | `account/email` | 本人 | `{ email, password }` |
| POST | `account/email/resend` | 本人 | 重寄驗證信 |
| POST | `account/two-factor/setup` | 本人 | `{ password, code?, recoveryCode? }`（重新綁定時要後兩者之一） |
| POST | `account/two-factor/enable` | 本人 | `{ code }`，回復原碼 |
| POST | `account/two-factor/disable` | 本人 | `{ password, code \| recoveryCode }` |
| POST | `account/two-factor/recovery-codes` | 本人 | `{ password, code }`，回新復原碼 |
| DELETE | `account/trusted-devices` | 本人 | 全部取消記住 |
| PUT | `admin/users/{id}/email` | 系統管理員 | `{ email }`（null＝清除） |
| POST | `admin/users/{id}/email/resend` | 系統管理員 | 重寄驗證信 |
| POST | `admin/users/{id}/send-reset-link` | 系統管理員 | 寄重設連結（§7.5） |
| POST | `admin/users/{id}/reset-two-factor` | 系統管理員 | 重設雙因素（不能對自己） |
| GET | `admin/users` | 系統管理員 | 既有；每列多 `email`、`emailVerified`、`twoFactor`（`off`、`on`、`unreadable`） |
| GET | `system/mail-outbox` | 系統管理員 | 帳號信寄送紀錄 |
| PUT | `theme` | 系統管理員 | 既有；多選填的 `siteName`（null＝不變，§12） |
| POST | `mcp/tokens` | 本人 | 既有；已啟用雙因素時多要 `code` |

- 匿名白名單加五個 `auth/*` 匿名端點與 `auth/two-factor`；強制綁定放行清單見 §8.7。
- Demo 站：本人的雙因素端點、`DELETE account/trusted-devices`、`reset-two-factor`、`auth` 分類政策的修改、站台名稱的修改一律 403 `demoSite`；`send-reset-link` 與忘記密碼只對 `mfa01` 有效（Q22）。
- 回應不帶 TOTP 金鑰明文，只有 `setup` 那一次回給本人；`setup`、`enable`、`recovery-codes` 加 `Cache-Control: no-store`。

## 11. 權限

| 動作 | 誰 | 檢查在哪 |
|---|---|---|
| 申請重設、用連結重設、驗證 Email | 匿名（持權杖） | handler：權杖雜湊、用途、時效、帳號啟用、權杖 Email 等於目前 Email |
| 第二步登入 | 持第二步憑證 | handler：憑證、stamp、帳號啟用、帳號層暫停 |
| 改自己的 Email、綁定、關閉、復原碼、裝置 | 本人，重新輸入目前密碼（關閉與重新產生另要驗證碼） | handler；限流見 §8.9 |
| 設別人的 Email、寄重設連結、重設別人的雙因素 | 系統管理員（`ISystemAdminRequest`） | `SystemAdminBehavior`；重設雙因素另擋自己 |
| 看寄送紀錄、改 `auth.*` 政策、改站台名稱 | 系統管理員 | 既有 |

- 驗證都在後端；前端只是提早提示。
- 讀不到使用者資料、解不開憑證或 TOTP 金鑰，一律當失敗（fail closed）。
- 權杖雜湊、復原碼雜湊、TOTP 碼的比對都用固定時間比較（同 `McpSecrets.HashEquals`）。

## 12. 畫面（四種寬度）

所有新頁面在 390、820、1366、1920 驗過，不橫向捲動；清單在 390 改卡片。

| 畫面 | 內容 |
|---|---|
| 登入頁 | 「忘記密碼？」連結；第二步在同一張卡片換成驗證碼、「記住這台裝置 30 天」、「改用復原碼」、「回到登入」；Demo 的 `mfa01` 另顯示示範驗證碼 |
| `/forgot-password` | 帳號輸入、送出、統一結果訊息；`contactAdmin` 時只顯示請聯絡管理員；Demo 站多一行提示「Demo 站只有 mfa01 可示範重設密碼」（Q22） |
| `/reset-password` | 新密碼（提示最短長度）、再輸入一次、送出；失效時顯示說明與「重新申請」 |
| `/verify-email` | 「Email 已驗證」或「連結已失效」，附回到系統／登入 |
| `/account/security`（Q14） | 四張卡：Email（地址、已驗證／未驗證、變更、重寄）、密碼（連到變更密碼）、雙因素（狀態、啟用時間、復原碼剩餘、啟用／重新綁定／重新產生／關閉；金鑰無法讀取時顯示「驗證器設定已無法讀取，請用復原碼重新綁定」）、記住的裝置（清單與「全部取消記住」） |
| `/account/two-factor-setup` | 強制綁定頁：「系統管理員要求啟用雙因素驗證」＋綁定四步；只有登出能離開 |
| 寬限提示 | 每頁頂端一條提示「請在 {date} 前啟用雙因素驗證」＋「現在啟用」 |
| 頁首 | 「變更密碼」換成「帳號安全」（窄螢幕只顯示圖示＋tooltip） |
| 使用者管理 | 清單多「Email」（含未驗證標記）與「雙因素」（未啟用／已啟用／無法讀取）兩欄；每列動作多「設定 Email」「重設雙因素」；「重設密碼」對話框兩個選項（§7.5） |
| 寄信設定 | 多「帳號信寄送紀錄」區塊；沒設站台網址時頂端警示 |
| 外觀設定 | 多「站台名稱（顯示在系統寄出的信件與驗證器 App）」輸入框，100 字內（Q18） |
| 政策頁 | 新分類「帳號安全」；雙因素四項依 §8.7 呈現 |

- 新增介面文字放 `shared/i18n/{zh-TW,en-US,th,vi}/ui.json`，四語鍵集合一致；QR 碼與復原碼區塊加 `aria-label`。
- 綁定對話框在 390 滿版、QR 碼 200px 置中、金鑰可換行。

## 13. Demo 種子與示範路徑

### 13.1 種子（T22）

- 寫在 Demo 產生器，不放進 `DemoPeople.EnsureAsync`（測試基底不受影響）。
- 13 個示範帳號補虛構 Email `<帳號>@example.com`（RFC 2606 保留網域），已驗證；`floor02` 留未驗證。
- 新增示範帳號 `mfa01`「雙因素示範」：不加入任何群組（避免改動既有群組成員數與提及候選），已啟用雙因素，固定金鑰寫在 `docs/demo.md`，10 組復原碼。改種子前照後端規則 grep 既有對帳號數的斷言。
- 站台名稱設成虛構的「示範科技」。

### 13.2 Demo 收件匣（Q12；審查必修 6）

- 開 `Demo:LoginEnabled` 時帳號信改由 `DemoMailSink` 處理：不連外，照常組信，把內文寫進 `PreviewBody`、記 `sent`。正式環境不註冊 sink、永遠不寫 `PreviewBody`。
- 連結網址：Demo 沒設 `App:PublicUrl`（`Dockerfile.demo` 沒有這個變數），sink 寫站內相對路徑 `/reset-password#t=…`，「看信」視窗裡點得開。
- `auth/options` 在 Demo 站的 `passwordReset` 是 `available`（不看寄信設定與站台網址）。
- 忘記密碼與寄重設連結只對 `mfa01` 有效（Q22；複審建議 4）：`admin` 排除是審查必修 4（否則訪客能替 `admin` 設自己知道的密碼）；其他示範帳號排除，是因為重設會換 stamp，訪客反覆對 `sales01` 申請就能把所有正在用 `sales01` 的訪客登出。示範帳號重設後可以用密碼登入（`LoginHandler.cs:88` 只擋沒有密碼的帳號），示範路徑走得通。忘記密碼頁在 Demo 站多一行提示「Demo 站只有 mfa01 可示範重設密碼」（Q22），訪客不會以為對 `sales01` 申請卻沒信是壞掉。

### 13.3 `mfa01` 的金鑰在執行期解得開（審查必修 5）

`Dockerfile.demo` 建置時產生資料後刪掉金鑰，執行期重新產生，建置時加密的 `TotpSecretProtected` 會解不開。所以：

- 建置時的種子**不寫** `mfa01` 的 `TotpSecretProtected`，只寫帳號、`TotpEnabledUtc` 與復原碼雜湊。
- Demo 站每次啟動（`Demo:LoginEnabled`）用目前的金鑰把固定金鑰加密寫進 `mfa01`，可重複執行。
- 測試模擬「產生資料 → 刪金鑰 → 啟動 → `mfa01` 用提示碼登入成功」。

### 13.4 Demo 驗證碼提示（Q13）

- `mfa01` 走到第二步時，畫面顯示「Demo 站示範用：目前的驗證碼是 482913」與一組還沒用過的復原碼。
- 伺服器依固定金鑰算出目前時間步的碼；只在 `Demo:LoginEnabled` 且帳號是 `mfa01` 時回傳。
- Demo 站的 `mfa01` **不檢查重放**（不寫也不比 `TotpLastStep`）：同一個 30 秒內有三位以上訪客進來時，前一版「取下一個時間步」的做法會讓第三位拿到超出 ±1 的碼而被拒（複審建議 5）。正式環境照常檢查重放。
- 復原碼在同一段喚醒期間會被用完（喚醒時資料庫回到映像狀態，兩次喚醒之間不會）；`docs/demo.md` 註明。

### 13.5 示範路徑（AGENTS.md「Demo 資料」）

| 路徑 | 怎麼走 | 看到什麼 |
|---|---|---|
| 成功：Email 驗證 | `admin` 把 `floor02` 的 Email 改成 `floor02b@example.com` → 寄送紀錄看信 → 開連結 | 使用者管理顯示已驗證 |
| 成功：忘記密碼＋雙因素 | 登出 → 忘記密碼 → `mfa01` → 以 `admin` 看信 → 設新密碼 → 用新密碼登入 → 輸入提示的驗證碼 | 統一訊息；新密碼登入後仍要第二步（au1-7）；寄送紀錄多一筆「密碼已變更」（用 `mfa01` 而不是常用帳號，避免換 stamp 把其他訪客登出） |
| 失敗：連結用過 | 再開同一連結 | 「連結已失效或已使用過」 |
| 失敗：其他帳號收不到 | 對 `floor02` 申請 | 同一句統一訊息；沒有新信；稽核原因 `demoRestricted`（正式環境的「沒有已驗證 Email」分支以自動測試驗） |
| 失敗：`admin` 不收重設信 | 對 `admin` 申請 | 同一句統一訊息；沒有新信 |
| 成功：記住這台裝置 | `mfa01` 第二步勾記住 → 登出 → 再點 `mfa01` | 不再問驗證碼 |
| 失敗：驗證碼錯 | 輸入 `000000` | 「驗證碼錯誤或已過期」 |
| 成功：復原碼 | 改用復原碼，輸入提示的那組 | 進入首頁，提示剩幾組 |
| 被擋：Demo 不能綁定 | `sales01` 開帳號安全 → 啟用 | 按鈕停用、寫「Demo 站不能啟用雙因素驗證」 |
| 被擋：政策唯讀 | `admin` 開政策頁「帳號安全」 | 唯讀 |
| 被擋：不能取消記住 | `mfa01` 開帳號安全 →「全部取消記住」 | 按鈕停用、寫「Demo 站不能變更」 |
| 個人化：Email 狀態 | `floor02` 開帳號安全 | 「未驗證」 |

強制綁定、寬限、管理員重設雙因素、帳號層暫停在 Demo 站不示範（Demo 的守衛就是要讓它們不生效），以自動測試與本機走查驗收，交付說明寫明。

## 14. 開關組合下誰負責

| 情況 | 忘記密碼 | 雙因素 | 誰處理 |
|---|---|---|---|
| 寄信設定與站台網址都設好 | 可用 | 照政策 | — |
| 寄信設定沒設好 | `contactAdmin`；Email 照存、驗證信 `skipped` | 照常（不需寄信；通知信 `skipped`） | 系統管理員 |
| 沒設站台網址 | `contactAdmin`；寄信設定頁警示 | 照常；issuer 用站台名稱或 `Formwright` | 部署者 |
| SMTP 寄不出去 | 重試 5 次後 `failed`，開 `channel.accountMail` | 照常 | 系統管理員 |
| 政策關掉忘記密碼 | `contactAdmin` | 照常 | — |
| 使用者沒有已驗證 Email | 收不到信（畫面不透露）；管理員只能設暫時密碼 | 照常；收不到安全通知 | 本人或管理員補 Email |
| 金鑰不落地（`keysDirectory` 為 null） | 照常 | 非 Development 環境不分資料庫一律拒絕啟動；Development 不能新綁定（409）、強制暫停並開 `infra.keysNotPersisted`。SQLite 檔案（絕對路徑）沒設設定鍵時金鑰在資料庫旁，不算這一列 | 部署者 |
| 金鑰目錄有設、但每次部署換新（沒掛 volume） | 照常 | 所有 TOTP 金鑰失效，同下一列處理；啟動時開 `infra.keysMayBeLost`（§8.7） | 部署者 |
| **不帶金鑰還原備份**（bk1 §5.7，使用者裁決備份不帶金鑰） | 照常（權杖存雜湊） | 所有 TOTP 金鑰失效：使用者用復原碼登入、用復原碼重新綁定；記住的裝置仍有效（存雜湊）；唯一管理員且沒有復原碼時走救援設定 | 部署者通知使用者；管理員在使用者管理看「無法讀取」名單 |
| 資料庫唯讀或連不上 | `contactAdmin`、申請一律 202 | 登入本來就不能用 | 同維運設計 §17.2.11 |
| Demo 站 | Demo 收件匣；只限 `mfa01` | 只有 `mfa01`；不能綁定；強制不生效；不套帳號層暫停；`auth` 政策唯讀 | — |
| 雲端版 | 廠商在客戶的寄信設定填郵件服務；Render 是否擋 SMTP 埠待查證（T18） | 照政策 | 廠商（O3） |
| 沒開任何應用程式 | 照常 | 照常 | — |

## 15. ERP 對照

以下依主控對主流產品的了解整理，未另行上網查證。

- **Odoo**：登入頁「重設密碼」寄有時效的連結，信件進核心郵件佇列由排程寄出；`auth_totp` 讓使用者在偏好設定的「帳號安全」啟用驗證器 App、可信任裝置，管理員可替使用者關閉；另可強制「員工」或「全部使用者」。Formwright 的選用／強制全部／強制群組、「帳號安全」頁、記住裝置對應這一套；寄信改為即時喚醒，因為重設信使用者在等。
- **ERPNext**：系統設定開雙因素，依角色決定誰要用；第二因素可選 App、簡訊、Email。Formwright 依群組強制對應依角色；只做 App。
- **主檔**：帳號 Email 屬於核心的系統使用者（ERP 資料模型 §2.6）。HRM 員工主檔的聯絡 Email 是人事資料，與帳號 Email 分開、不同步；帳號 Email 不出現在使用者欄位的顯示、提及、MCP 工具或業務畫面。
- **單據與數量契約**：au1 不新增模組、主檔或單據，ERP 資料模型 §7 檢核 1–7 不適用。核心寄信佇列是平台能力（檢核 9）。

## 16. 與既有設計或程式的矛盾

1. **使用者與群組設計 §3 非目標、§4「忘記密碼由管理員重設；系統不寄信」**：使用者 2026-11-17 裁決推翻。沒設寄信的系統照舊由管理員重設。實作時回填。
2. **維運設計 O2-U1「`AppUser` 沒有 Email 欄位」、§17.1 範圍外**：au1 加欄，只用於帳號信；告警收件人仍在通道設定。實作時回填 §17.1 與 O2-U1，§17.2.6、§17.13 註記「連線設定已在核心寄信設定，帳號信佇列見 au1」（分流 E 第 9 點）。
3. **維運設計 §17.2.6 寄件介面**：`IAlertMailSender` 改名 `IMailSender`（T6）。
4. **維運設計 §14 U1「Render 免費方案擋 SMTP 埠（未查證）」**：Demo 用收件匣，不受影響；雲端版併入 O3（T18）。
5. **`Ops:PublicUrl` → `App:PublicUrl`**（D20）：新鍵是核心設定，告警信、MCP 的 `ServerBaseUrl` 與 Origin 檢查都改讀新鍵、沒設時退回舊鍵；部署文件（`docs/deploy.md`）與 `Dockerfile.demo` 若有用到也改用新鍵（repo 沒有 `render.yaml`，Render 的環境變數在後台設定，交付說明列出要改的變數）。
6. **主控技術決定 D24「Demo 站忘記密碼與雙因素停用」**：本設計改成 Demo 收件匣與 `mfa01`（§13），理由是 AGENTS.md 要求有畫面的功能在 Demo 示範成功路徑與主要失敗分支；D24 要防的「訪客把示範帳號鎖住」改由站層守衛處理（§8.7、§13.2）。待使用者確認 Q12、Q13；若不採用，退回 D24（拿掉 `PreviewBody` 欄與 `mfa01`，Demo 交付說明寫「無可示範」）。
7. **分流 au1-2「管理員填的 Email 立即生效」**：使用者 2026-11-21 裁決推翻（§6.5）。
8. **初稿 T14（強制而沒綁時暫停 AI 權杖）**：和 au1-10「權杖照常使用」衝突，拿掉（§8.12）。MCP 設計 §13 改為「已綁雙因素者建權杖另要驗證碼」。
9. **手冊**：第 28 行救援段補「一併清除 admin 的雙因素與記住的裝置，沒移除會開安全事件」；第 41 行、第 663 行附近、第 680–681 行「系統不寄信」「沒有雙因素」改寫；使用者管理、寄信設定（第 1783 行起）、外觀設定、政策、登入、AI 連線段補新行為；部署段把 `DataProtection:KeysDirectory` 改成「啟用雙因素前必設」，並建議金鑰目錄和資料目錄分開放（金鑰檔在 Linux 上是明文 XML）。
10. **告警信主旨寫死「[Formwright]」**：au1-漏3 的精神也適用告警信，但不在 au1 範圍；開 backlog 票改讀站台名稱。
11. **密碼最短長度**：改成政策（§7.6），`ChangePasswordValidator.MinLength`、`AdminBootstrap.cs:15` 的常數、前端兩份常數一起收掉。
12. **`LoginHandler` 與 `LoginThrottle`**：「密碼正確即成功」的語意在有雙因素時改成「第二步通過才成功」（§8.3），註解跟著改。
13. **bk1 設計 §5.7 不帶金鑰還原的代價清單**：要補「所有 TOTP 金鑰失效」與處理方式（§14），手冊的還原步驟同步；由主控轉給 bk1。
14. **功能清單與路線**：`docs/feature.md` 第 56、57、379 列與 roadmap au1 列由主控更新；roadmap au1 的連結改指本設計（分流 E 第 4 點）。
15. **部署文件 `docs/deploy.md:107`**：`DataProtection__KeysDirectory` 那一列現在寫「記憶體資料庫與 PostgreSQL 則不落地（重啟後要重新登入，PostgreSQL 會記 Warning）」，au1c 改寫成「非 Development 環境沒有金鑰目錄（PostgreSQL、記憶體資料庫、`file:` URI 寫法的 SQLite）就拒絕啟動；啟用雙因素前必設，建議和資料目錄分開放」。由實作段回填（§23）。
16. **附件目錄刻意不一起強制**：PostgreSQL 沒設 `Attachments:Root` 時附件暫存在暫存目錄，現在只記啟動警告（`Program.cs:111–112`）。au1 只對金鑰目錄拒絕啟動，附件目錄維持警告：金鑰遺失會讓**所有人**的登入、寄信密碼與雙因素一起失效，而且牽涉不 fail-open 的強制政策；附件遺失是資料問題，由備份（bk1）與維運的磁碟監控處理，不在帳號安全範圍。

## 17. 切段

依序 au1a → au1b → au1c；au1b、au1c 都只依賴 au1a，但共用 `SessionMiddleware.cs`、`AuthController.cs`、`LoginPage.vue`、`ui.json`，不並行。

| 段 | 內容 | migration | 共用檔 | 依賴 |
|---|---|---|---|---|
| au1a 核心寄信佇列、帳號 Email、站台設定 | 全部表與欄（§5，含 au1b、au1c 要用的）；`IMailSender` 改名；`MailDeliveryService`；寄送前重查資格與取消（§6.3）；`AccountMailText` 四語；Email 設定與驗證、`/verify-email`；`emailChanged` 通知；安全事件 `security.adminEmailChanged`（含安全事件代碼的登記）；「帳號安全」頁（Email、密碼兩張卡）與頁首入口；寄送紀錄；站台名稱（外觀設定）；`App:PublicUrl`；`mail.outboxRetentionDays`、`auth.emailVerifyLinkHours`；清理接 `ops.upkeep`；Demo 收件匣、示範帳號 Email、站台名稱種子；手冊 | **有**（`AccountSecurity`，SQLite＋PostgreSQL 各一） | `AppUser.cs`、`AppUserConfiguration.cs`、新設定類、`DynamicDbContext`、兩份 model snapshot、`Application/DependencyInjection.cs`、`Program.cs`、`SessionMiddleware.cs`、`PolicyCatalog.cs`、`shared/policy-catalog.json`、`AuditActions.cs`、`MessageText.cs`、`ui.json`×4、`routes.ts`、`MainLayout.vue`、`UsersPage.vue`、`MailSettingsPage.vue`、`AppearancePage.vue`、`UpdateSiteThemeHandler.cs`、`AlertDelivery.cs`、`AlertContracts.cs`、`McpTokenRequests.cs`（站台網址改鍵）、Demo 產生器、`docs/deploy.md` | 無 |
| au1b 寄信重設密碼 | 申請、檢查、重設 API；節流與 `auth.passwordResetMaxPerHour`；統一回應與稽核；所有改密碼路徑作廢連結與通知（§7.1 第 7 點）；管理員寄重設連結（§7.5）；安全事件 `security.adminPasswordReset`；匿名權杖端點節流；登入頁連結、兩個新頁；`auth/options`；`auth.passwordResetEnabled`、`auth.passwordResetLinkMinutes`、`auth.passwordMinLength` 與收斂（§7.6）；改密碼限流；Demo 路徑；手冊 | 無 | `SessionMiddleware.cs`、`AuthController.cs`、`AdminUsersController.cs`、`GetAuthOptionsHandler.cs`、`AccountResponses.cs`、`ChangePasswordHandler.cs`、`ChangePasswordValidator.cs`、`CreateUserValidator.cs`、`ResetPasswordHandler.cs`、`ResetPasswordValidator.cs`、`SetUserActiveHandler.cs`、`AdminBootstrap.cs`、`LoginPage.vue`、`ChangePasswordPage.vue`、`UsersPage.vue`、`ui.json`×4、`routes.ts`、`PolicyCatalog.cs`、`AuditActions.cs` | au1a |
| au1c 雙因素登入 | Domain TOTP；綁定、重新綁定、關閉、復原碼；第二步與三層猜碼上限；記住這台裝置；強制政策四項與寬限、middleware 封鎖、強制綁定頁、寬限提示；管理員重設；其餘三種安全事件（`adminTwoFactorReset`、`twoFactorFailures`、`rescueConfigLeft`）；救援清除；建 AI 權杖要驗證碼；`KeyPersistenceStatus`、非 Development 環境拒絕啟動、`keysNotPersisted`、`keysMayBeLost`；重新驗證端點拆兩段（§8.9）；QR 碼；`mfa01` 種子、啟動時重新加密、Demo 驗證碼提示、`auth` 政策 Demo 唯讀；手冊與 `docs/demo.md` | 無 | `SessionMiddleware.cs`、`AuthController.cs`、`LoginHandler.cs`、`LoginThrottle.cs`、`DemoLoginHandler.cs`、`AuthCookies.cs`、`AdminBootstrap.cs`、`GetMeHandler.cs`、`McpTokenHandlers.cs`、`McpTokenRequests.cs`、`PolicyHandlers.cs`、`LoginPage.vue`、`AiTokensPage.vue`、`MainLayout.vue`、`auth-guard.ts`、`ui.json`×4、`routes.ts`、`UsersPage.vue`、`PolicyCatalog.cs`、`AuditActions.cs`、`frontend/package.json`、Demo 產生器、`Program.cs`、`docs/deploy.md`、`AuthApiTests.cs`、`RequestContextTests.cs`（補金鑰目錄） | au1a |

- au1a 是本段唯一的 migration，不和其他會加 migration 的段平行。
- 三段都有畫面，各自在本機四種寬度走查。
- 每段合併後 Demo 不壞：au1a 多 Email、收件匣與站台名稱；au1b 多忘記密碼；au1c 多 `mfa01`。
- au1c 估約 50 個檔，太大時可把「記住這台裝置」與強制政策拆成 au1c2（不改表）。

## 18. 可驗收案例

### 18.1 寄信佇列、Email、站台設定（au1a）

| # | Given | When | Then |
|---|---|---|---|
| M1 | 寄信設定已設好、站台網址有設；假寄件器 | 管理員把 `sales01` 的 Email 設成 `a@example.com` | 佇列一列 `emailVerify`、系統預設語系；5 秒內 `sent`；信裡有 `/verify-email#t=`；權杖表只存雜湊 |
| M2 | M1 的信 | 開連結 | `EmailVerifiedUtc` 有值；同一連結再開回「連結已失效」 |
| M3 | M1 寄出後 | 改成 `b@example.com`，再開 M1 的連結 | 舊連結失效；`b@` 收到驗證信；`a@` 收到「Email 已變更」 |
| M4 | 假寄件器第 1、2 次失敗 | 寫一封驗證信 | 1、5 分鐘後重試，第 3 次 `sent`；只有最後一封的連結有效 |
| M5 | 假寄件器一直失敗 | 時間快轉 | 第 5 次後 `failed`；開 `channel.accountMail`（站內）；沒寄告警信；之後一封成功事件結案 |
| M6 | 寄信設定沒設好 | 設 Email | Email 存好、列 `skipped`／`notConfigured`；顯示「未驗證（系統目前寄不出驗證信）」 |
| M7 | 一列卡在 `sending`、期限已過 | 掃描 | 重領、權杖換新、前一把 `superseded` |
| M8 | a 的驗證信 V1 卡在重試；改成 b，b 的 V2 先寄出 | V1 重試 | V1 `skipped`（`ToAddress` 不等於目前 Email）；b 的連結仍有效 |
| M9 | 管理員清空 Email | — | 驗證時間清掉；未用權杖作廢；還沒寄的驗證信 `skipped` |
| M10 | Demo 站 | 管理員改 `floor02` 的 Email | 沒有連外；列 `sent`、`PreviewBody` 有整封信、連結是站內相對路徑；正式環境同情境 `PreviewBody` 為 null |
| M11 | 超過 30 天的 `sent`、`failed` 列、過期權杖，及一筆 31 天前建立仍 `pending` 的列 | `ops.upkeep` | 前三者被清；`pending` 留著 |
| M12 | 站台名稱「示範科技」、寄信設定沒填寄件人名稱 | 寄任一帳號信 | 主旨「[示範科技] …」、寄件顯示名稱「示範科技」；站台名稱清空後主旨沒有前綴 |
| M13 | 只設舊鍵 `Ops:PublicUrl` | 寄驗證信 | 連結用舊鍵的網址；兩個都設時用 `App:PublicUrl` |
| M14 | 本人改 Email、密碼打錯 6 次（走完整 MediatR 管線的 WebAPI 測試） | — | 前 5 次 400，第 6 次被限流；6 筆 `account.reauthFailed` 都留在資料庫（沒有被交易回滾） |
| M15 | 系統管理員 A 改系統管理員 B 的 Email；A 清空 B 的 Email | — | 兩次都在送出時開 `security.adminEmailChanged`（不等驗證）；改或清空一般使用者的 Email 不開 |

### 18.2 寄信重設密碼（au1b）

| # | Given | When | Then |
|---|---|---|---|
| R1 | `sales01` 有已驗證 Email | 申請 | 202 統一訊息；一封 `passwordReset`；連結 30 分鐘有效 |
| R2 | 帳號不存在／停用／沒有 Email／未驗證／政策關閉 | 各申請一次 | 狀態碼與內容完全相同；都沒有佇列列；稽核原因代碼正確；帳號不存在不記輸入 |
| R3 | R1 的連結 | 設新密碼 | 舊 session 401；AI 權杖撤銷；權杖 `used`；`passwordChanged` 通知；不自動登入 |
| R4 | R3 之後 | 再用同一連結 | `resetLinkInvalid` |
| R5 | 申請兩次 | 用第一封 | 失效；第二封可用 |
| R6 | 31 分鐘後使用 | — | `resetLinkInvalid` |
| R7 | 寄信一直失敗 30 分鐘 | 重試 | `expired`，不寄 |
| R8 | 同一輸入 1 小時內 4 次（帳號存在、不存在各測一次） | — | 兩種情況都是前 3 次照常處理、第 4 次不處理，回應相同 |
| R9 | 同 IP 15 分鐘 11 次 | — | 第 11 次 202、不寄、不逐筆寫稽核 |
| R10 | `MustChangePassword` 帳號 | 用連結重設 | 旗標清掉 |
| R11 | 重設信重試中 | 本人把 Email 換掉 | 這封 `skipped`；若已寄出，連結因 Email 不符而無效 |
| R12 | 寄出重設連結後 | 本人改密碼／管理員設暫時密碼／救援設定 | 三種情況連結都失效、還沒寄的重設信 `skipped`、各寄 `passwordChanged` |
| R13 | 以 `Host: evil.example` 標頭打申請 API | 看信 | 連結是站台網址 |
| R14 | `auth.passwordMinLength` 改 10 | 重設時新密碼 9 碼 | 400，訊息帶 10；前端提示 10；既有 8 碼密碼照常登入 |
| R15 | 系統管理員 `admin2` 用寄信重設 | 完成 | 開 `security.adminPasswordReset`，全部管理員收到站內通知 |
| R16 | `sales01` 有已驗證 Email | 管理員開重設密碼對話框 | 預設「寄重設連結」；送出後一封信、`sales01` 舊密碼照常能登入；沒有 Email 的帳號只有暫時密碼 |
| R17 | 資料庫唯讀 | 申請 | 202；`auth/options` 回 `contactAdmin` |
| R18 | Demo 站 | 對 `admin`、`sales01` 申請；管理員對 `admin`、`sales01` 寄重設連結；對 `mfa01` 申請 | 前兩者 202 沒有信；寄連結 403；`mfa01` 有信 |
| R19 | 寄信設定沒設好 | 開忘記密碼 | `contactAdmin`、「請聯絡系統管理員」 |
| R20 | `auth.passwordResetEnabled` 關閉 | 自助申請；管理員寄重設連結 | 前者沒有信；後者照寄、連結可用 |
| R22 | Demo 站 | 開忘記密碼頁 | 顯示「Demo 站只有 mfa01 可示範重設密碼」；正式環境沒有這行 |
| R21 | 同一 IP | 15 分鐘內對 `reset-password/check` 送 31 次 | 第 31 次被限流 |

### 18.3 雙因素（au1c）

| # | Given | When | Then |
|---|---|---|---|
| F1 | RFC 6238 附錄 B 的 SHA1 測試向量 | Domain 計算 | 全部相符 |
| F2 | 本人在帳號安全頁 | 啟用 | 回 10 組復原碼；其他裝置 401；本 session 照常；`twoFactorEnabled` 通知 |
| F3 | 已啟用 | 密碼登入 | 200 `twoFactorRequired`；沒有 session；稽核沒有 `login.succeeded`；帳號＋IP 層失敗紀錄沒被清 |
| F4 | F3 | 送正確碼 | 發 session；`password+totp`；同一張憑證再送一次正確碼被拒 |
| F5 | 用過的碼 | 30 秒內再登入送同一碼 | 拒絕；並行兩個請求只有一個成功 |
| F6 | 同一張憑證 | 連錯 5 次 | 第 6 次正確也拒絕，要重輸密碼 |
| F7 | 換 IP、反覆重新輸入密碼（走完整 MediatR 管線的 WebAPI 測試） | 第二步累計錯 10 次 | 之後 15 分鐘內正確碼也拒絕；`security.twoFactorFailures` 一件；`twoFactorFailures` 通知一封；15 分鐘後恢復；`Demo:LoginEnabled` 關閉的對照組明寫帳號層生效 |
| F8 | 憑證發出 6 分鐘 | 送正確碼 | `twoFactorExpired` |
| F9 | 已啟用 | 用復原碼登入 | 成功、剩 9 組、`recoveryCodeUsed` 通知；同一組再用拒絕 |
| F10 | 有雙因素的帳號 | 用重設信改密碼後登入 | 仍要第二步 |
| F11 | 勾記住這台裝置 | 登出再登入 | 不問驗證碼、`password+device` |
| F12 | F11 之後 | 改密碼／管理員重設雙因素／政策改 0 | 三種情況各自讓那台裝置要重新驗證 |
| F13 | 出廠政策 | 沒啟用的系統管理員登入 | 被導到強制綁定頁；一般使用者照常 |
| F14 | `auth.twoFactorRequireAdmins` 改否 | 同上 | 管理員照常 |
| F15 | 模式 `groups`、群組「業務」 | `sales01`、`floor01`、系統管理員各登入 | `sales01` 與管理員被要求綁定；`floor01` 照常；`auth.twoFactorRequireAdmins` 在政策頁顯示固定 |
| F16 | 模式 `all`、寬限 7 天 | `sales01` 第一次登入；第 8 天再登入 | 第一次照常並顯示「請在 {date} 前啟用」；第 8 天 403 `twoFactorEnrollmentRequired` |
| F17 | 模式 `all`、帳號 `MustChangePassword` | 登入 | 先改密碼再綁定 |
| F18 | 在強制對象內 | 本人按關閉 | 409 |
| F19 | 選用模式 | 本人關閉 | 清除；記住的裝置失效；`twoFactorDisabled` 通知 |
| F20 | 已啟用 | 管理員重設；管理員重設自己 | 前者清除、對方 session 401、AI 權杖保留、通知參數標管理員；後者 400 `useAccountSecurity` |
| F21 | 對方是系統管理員 | 重設其雙因素 | 開 `security.adminTwoFactorReset` |
| F22 | 已啟用 | 建 AI 權杖只給密碼；再加驗證碼 | 前者 400；後者成功；權杖之後照常可用 |
| F23 | `admin` 已啟用 | 設救援設定重啟兩次 | 雙因素、復原碼、記住的裝置清除；第二次啟動開 `security.rescueConfigLeft` |
| F24 | Development 環境、記憶體資料庫（金鑰不落地） | 開始綁定 | 409 `keysNotPersisted` |
| F25 | 已啟用；換一套金鑰（模擬不帶金鑰還原） | 登入 | 驗證碼拒絕；復原碼可登入；帳號安全頁提示無法讀取；用復原碼重新綁定成功；使用者管理顯示「無法讀取」直到重綁 |
| F26 | Demo 站 | 產生資料 → 刪金鑰 → 啟動 → 點 `mfa01` → 輸入提示碼 | 登入成功 |
| F27 | Demo 站 | 三位訪客同一個 30 秒內點 `mfa01` 並輸入各自看到的提示碼 | 三位都成功 |
| F28 | Demo 站 | `sales01` 啟用雙因素；`admin` 改 `auth.twoFactorMode`；`admin` 啟用雙因素 | 三者都 403 `demoSite` |
| F29 | Demo 站、測試直接把政策寫成 `all` | 點 `admin`、`sales01` | 都照常一鍵進入 |
| F30 | 第二步、帳號在憑證發出後被停用 | 送正確碼 | 拒絕 |
| F31 | 唯一的系統管理員 `admin` 已啟用雙因素、沒有復原碼；換一套金鑰（模擬不帶金鑰還原），有設金鑰目錄 | 設救援設定重啟 → 用新密碼登入 | 不經第二步；先改密碼；再被導去綁定並成功；帳號層計數沒有因金鑰解不開而累加 |
| F32 | Development 環境、記憶體資料庫、出廠政策 | 沒啟用的系統管理員登入 | 不被要求綁定；開 `infra.keysNotPersisted`；帳號安全頁顯示無法啟用的原因 |
| F33 | 同一 IP、10 個帳號都已啟用雙因素 | 各用正確密碼＋正確驗證碼登入，共 25 次 | 第 21 次之後仍照常登入；沒有任何一次被算成失敗 |
| F34 | 已啟用（走完整 MediatR 管線的 WebAPI 測試） | `disable`、`recovery-codes` 用錯的驗證碼合計 10 次 | 帳號層暫停；第二步也被暫停；每次失敗的 `account.reauthFailed` 都留在資料庫 |
| F35 | Demo 站 | 對 `mfa01` 第二步連錯 12 次（換三張憑證） | 每張憑證第 6 次起被拒；不出現帳號層暫停；下一位訪客照常登入 |
| F36 | Production、PostgreSQL（假連線字串即可，例外在連資料庫前丟出，SQLite 的 CI 也能跑）、沒設 `KeysDirectory` | 啟動 | 啟動失敗並印出必須設定金鑰目錄的訊息 |
| F36b | Production、SQLite 記憶體資料庫；Production、`Data Source=file:/…?cache=shared`；都沒設 `KeysDirectory` | 啟動 | 兩者都啟動失敗；同樣設定換成 Development 則照常啟動、開 `infra.keysNotPersisted` |
| F37 | SQLite 檔案資料庫、沒設 `KeysDirectory`、出廠政策 | 沒啟用的系統管理員登入、開始綁定 | 被要求綁定；綁定成功（金鑰在 `<資料庫目錄>/keys`）；沒有 `infra.keysNotPersisted` |
| F38 | 帳號被暫停中 | 管理員重設其雙因素 | 計數與暫停清除；本人用密碼登入後被導去綁定 |
| F39 | 帳號第二步連錯 10 次被暫停兩輪（24 小時內） | — | 只寄一封 `twoFactorFailures`；安全事件一件、次數 2 |
| F40 | 第一次被判定應啟用、寬限 7 天；第 3 天政策改回選用；第 20 天再改強制；期間本人都沒登入 | 第 20 天登入 | 重新顯示 7 天寬限，不立即擋（存政策時批次清了起點） |
| F41 | 已啟用；測試在第 10 次佔名額後、驗碼前丟例外 | 15 分鐘後送正確碼 | 登入成功；計數與暫停時間都清掉 |
| F42 | 已啟用；換一套金鑰（TOTP 金鑰解不開） | 第二步連送 12 次驗證碼，再送復原碼 | 驗證碼都拒絕、計數一直是 0；復原碼登入成功 |
| F43 | 已有帳號綁了雙因素；金鑰目錄換成空目錄 | 啟動 | 開 `infra.keysMayBeLost`；Demo 站建置流程（刪金鑰後啟動）不開 |

### 18.4 畫面

| # | Given | When | Then |
|---|---|---|---|
| U1 | 390、820、1366、1920 | 走登入第二步、忘記密碼、重設、驗證 Email、帳號安全、綁定對話框、強制綁定頁、寬限提示、使用者管理、寄送紀錄、外觀設定 | 都不橫向捲動；390 的清單是卡片；QR 碼完整 |
| U2 | 英、泰、越文介面 | 同上 | 沒有缺鍵；信件語系照 §6.4 |

## 19. 測試重點與風險

**測試重點**

- Domain：TOTP 測試向量、時間步邊界、Base32、復原碼正規化。
- Application：統一回應（R2、R8 逐欄比對）、權杖生命週期與寄送前重查（M8、R11、R12）、三層猜碼上限（F6、F7）、記住裝置的失效條件（F12）、強制對象計算（F13–F16）、Demo 站層守衛（F28、F29）。
- WebAPI：匿名白名單與強制綁定放行清單（逐條列出沒列進去的端點會被擋）、兩種 cookie 的屬性、Host 標頭不影響連結、`no-store`。
- PostgreSQL 與 SQLite 都跑：條件式更新（領取、復原碼、`TotpLastStep`、帳號層計數）只成功一次。
- 帳號層計數與 `reauthFailed` 不被回滾（F7、F34、M14）要走完整 MediatR 管線，不能只測 handler（複審 N5）。
- 測試主機的金鑰（複審建議 A）：`TestApp` 預設是記憶體 SQLite＋Development，`KeysPersisted` 永遠為否，au1c 的 WebAPI 測試預設都會被 409 擋住綁定、強制也被暫停。F2、F13–F16、F33、F34 等要設 `DataProtection:KeysDirectory` 暫存目錄，或替換 `KeyPersistenceStatus`；拒絕啟動之後，兩條既有的 Production 環境測試也要設金鑰目錄（§23）。
- 前端：auth-guard 的 `passwordChangeRequired`、`twoFactorEnrollmentRequired` 先後；登入頁兩步狀態；fragment 讀完即清。

**風險**

1. **Data Protection 金鑰遺失＝所有 TOTP 金鑰解不開**（最大風險）。兩種情況：
   - 沒設金鑰目錄而重啟。緩解：非 Development 環境沒設就拒絕啟動（§8.7）；Development 沒設就不准新綁定（§8.2）。
   - 金鑰目錄有設、但不持久（容器內沒掛 volume）：拒絕啟動擋不到。緩解：啟動時偵測到金鑰環是新的而已有綁定，開 `infra.keysMayBeLost`（§8.7）。殘餘：事件只是事後告知，已綁定的使用者仍要走復原碼重新綁定。
   - **不帶金鑰還原備份**（使用者裁決 bk1 備份不帶金鑰）：還原後所有 TOTP 金鑰失效。處理：使用者用復原碼登入、用復原碼重新綁定；唯一的系統管理員沒有復原碼時走救援設定（§8.11）；管理員在使用者管理看「無法讀取」名單逐一提醒。
   - 共同緩解：復原碼存雜湊；管理員重設；手冊把金鑰目錄改成必設並建議和資料目錄分開放。

   **給 bk1 的契約**（bk1 設計 55d753d6 的還原路徑以此為前提）：復原碼、記住的裝置、重設與驗證權杖一律存 SHA-256 雜湊，**不經 Data Protection 加密**，換一套金鑰仍驗得過；復原碼雜湊的輸入是正規化後的 16 個大寫 Base32 字元（§8.4）。只有 TOTP 金鑰與既有的 SMTP 密碼、登入 cookie 依賴 Data Protection。實作不得把復原碼改成加密存放，也不得改正規化方式。

   **兩條救回路徑在本設計成立的理由**（F25、F31 驗收）：

   - 一般使用者：第二步收到 `recoveryCode` 時只比對雜湊，**不解 TOTP 金鑰**（§8.3 第 3 點），所以金鑰解不開也登得進來；重新綁定的第 1 步收「密碼＋復原碼」，同樣不解舊金鑰（§8.2），完成時用新金鑰加密新金鑰並拿到 10 組新復原碼。金鑰解不開的帳號若在強制對象內，登入後被導去綁定，走的就是同一條重新綁定路徑。
   - 唯一的系統管理員：`Auth:ResetAdminPassword` 直接清欄位，不需解任何密文（§8.11）。重啟後用新密碼登入（不需第二步，雙因素已清），被要求改密碼，再依出廠「系統管理員強制」被導去綁定；還原到的新主機金鑰有落地時綁定照常（SQLite 檔案的預設 `keys` 目錄也算）；非 Development 環境沒設金鑰目錄根本啟動不了（§8.7），所以不會出現「卡在綁不了的綁定頁」。
2. **雲端版重設信寄不出去**：Render 免費方案是否擋 SMTP 埠未查證（T18）。若擋，O3 定郵件服務前雲端版忘記密碼只能 `contactAdmin`。
3. **單一實例假設**：申請節流、帳號＋IP 層、同一憑證次數在記憶體，多實例時放大 N 倍；帳號層第二步計數存資料庫，猜碼上限不受影響。
5. **Demo 的 `mfa01` 可能被拖慢**：見 §8.3 的已知限制（複審建議 F）。
4. **信箱被盜**：重設信不繞過雙因素（au1-7）；沒啟用雙因素的一般使用者則信箱即帳號。出廠強制系統管理員（au1-漏1）把最大的風險收掉。

## 20. 待確認清單

### 20.1 行為類

**已由使用者拍板，不再問**：Q2、Q23（使用者 2026-11-21 裁決）、Q6（au1-漏1、au1-6）、Q7（au1-7）、Q8（au1-9）、Q1（au1-3）、Q10（au1-8、au1-9、au1-10；登出所有裝置靠換 stamp，現行換 stamp 的寫法見 `ResetPasswordHandler.cs:25`、`SetUserActiveHandler.cs:29`），以及 Q3、Q9 的主體（au1-2、au1-7）。內容見 §4。領域顧問分流確認 Q1、Q10 成立。

**顧問拍板，待使用者確認**：

- **Q3 本人改 Email 要輸入目前密碼。**（本人可改、新信箱要驗證、通知舊信箱是 au1-2 已確認的部分。）
  - 理由：防止有人借用沒登出的電腦改掉 Email 再申請重設。代價：本人改 Email 多輸一次密碼。
  - 選錯的代價：極小。
- **Q4 用重設信改完密碼：不自動登入、撤銷全部 AI 連線權杖。**（登出所有 session、寄通知是 au1-4 已確認的部分。）
  - 背景：系統分不出「忘記密碼」與「帳號被盜、本人在搶回」，照比較嚴的處理，同管理員重設密碼的現行做法。
  - 替代：保留 AI 權杖（同本人改密碼）。代價：帳號被盜時，攻擊者建的權杖在重設後仍有效。
  - 選錯的代價：小，一行程式。
- **Q5 安全通知信的種類**：密碼被變更（任何方式；au1-4 已確認重設那一種）、Email 被改（寄舊地址）、啟用或重新綁定雙因素（審查必修 11）、雙因素被關閉或被管理員重設（au1-8 已確認管理員重設那一種）、有人用復原碼登入、第二步錯誤次數過多（Q19）。重新產生復原碼不寄。只寄到已驗證的地址。
  - 例子：有人拿到小陳的密碼，替還沒啟用的帳號綁上自己的手機；小陳的信箱收到「你啟用了雙因素驗證，不是你本人操作請聯絡系統管理員」。
  - 替代：只寄已確認的兩種。代價：帳號被動手腳時本人不知道。
  - 選錯的代價：極小。
- **Q9 細節：手機遺失又忘記密碼時，用復原碼登入、用復原碼重新綁定；沒有復原碼就找管理員。**（重設信不繞過雙因素是 au1-7 已確認。）
  - 選錯的代價：小。
- **Q11 系統沒設寄信時，「忘記密碼？」照樣顯示，點進去寫「請聯絡系統管理員重設密碼」。**
  - 背景：主控技術決定 D20 寫「沒設定就停用忘記密碼」；本題決定「停用」時連結要不要藏起來。
  - 理由：使用者忘記密碼時第一個找的就是這個連結，看到該找誰比找不到連結好。替代：隱藏。代價：使用者不知道怎麼辦。
  - 選錯的代價：極小。
- **Q12 Demo 站用「Demo 收件匣」示範重設成功路徑（顧問拍板，取代 D24）。**
  - 背景：AGENTS.md 要求 Demo 示範成功路徑；Demo 站不能設 SMTP。主控技術決定 D24 原本是 Demo 停用忘記密碼與雙因素（§16 第 6 點），領域顧問改定為 Demo 收件匣加 `mfa01`。
  - 例子：訪客替 `mfa01` 申請重設，再以 `admin` 到寄送紀錄按「看信」，點連結設新密碼。
  - 代價：訪客看得到 `mfa01` 的重設連結（`mfa01` 本來就能一鍵登入；`admin` 與其他示範帳號排除，Q22）。存信件內文的欄位 `PreviewBody` 是技術做法，改列 T28。
  - 替代：照 D24 停用。代價：線上 Demo 看不到這個功能。
  - 選錯的代價：小。
- **Q13 Demo 的雙因素示範帳號在第二步直接顯示目前的驗證碼與一組可用的復原碼。**
  - 替代：只公布金鑰，訪客自己用 App 掃。代價：大部分訪客卡在第二步。
  - 選錯的代價：極小。
- **Q14 頁首「變更密碼」換成「帳號安全」頁，Email、密碼、雙因素、記住的裝置都在這頁。**
  - 理由：頁首在手機寬度已有三個按鈕。替代：另加按鈕。代價：390 寬度要再收。
  - 選錯的代價：極小。
- **Q15 不在強制對象內時本人可以自己關閉雙因素（要密碼＋驗證碼）；在強制對象內只能重新綁定。**
  - 替代：只有管理員能關。代價：換手機或不想用都要找管理員。
  - 選錯的代價：極小。

**顧問拍板，待使用者確認（第二版新增）**：

- **Q17 「帳號安全」頁列出記住的裝置，附「全部取消記住」。**
  - 背景：使用者確認要做記住裝置（au1-9），但沒定本人能不能自己取消。
  - 例子：小陳在網咖勾了記住，回家後到帳號安全頁按「全部取消記住」。
  - 推薦：列清單（標籤、最近使用）＋一鍵全部取消，不做逐台取消。代價：一張卡、一個 API。
  - 替代：不提供，只能靠改密碼讓它們失效。代價：要改密碼才能取消，使用者會覺得繞。
  - 選錯的代價：極小。
- **Q18 外觀設定新增「站台名稱」欄位。**
  - 背景：使用者確認帳號信的寄件名稱與公司名稱讀站台主題設定（au1-漏3），但現在的外觀設定只有模式與風格，沒有名稱可讀。
  - 例子：管理員在外觀設定填「示範科技」，重設信主旨變成「[示範科技] 重設密碼」，驗證器 App 裡顯示「示範科技：sales01」。
  - 推薦：外觀設定加一格單一字串（100 字內，不分語系），沒填時主旨不加前綴。代價：多一個欄位與 Demo 唯讀。
  - 替代：多語名稱。代價：信件語系和名稱語系要再對一次，第一版用不到。
  - 選錯的代價：小，改成多語只動一個設定值的格式。
- **Q19 第二步帳號層連續錯 10 次暫停 15 分鐘，寄信通知本人並開安全事件。**
  - 背景：審查必修 1 指出原設計可以換 IP 慢慢猜中驗證碼。
  - 例子：攻擊者有小陳的密碼，一直試驗證碼；第 10 次錯之後小陳的帳號 15 分鐘內第二步一律拒絕，小陳收到「密碼正確但驗證碼錯誤多次」的信，管理員看到安全事件。小陳本人這 15 分鐘內也登不進來：可以等 15 分鐘，或請管理員重設雙因素（重設會一併清掉計數與暫停，小陳立刻能用密碼登入再重新綁定，§8.8）；自己記住的裝置不受暫停影響（§8.5）。Demo 站不套這一層（§8.3）。
  - 推薦：10 次、15 分鐘，寫死列可寫死清單。代價：被攻擊時本人會被擋 15 分鐘。
  - 替代：做成政策。代價：多兩個政策；調太鬆就失去保護。
  - 選錯的代價：小。

**顧問拍板，待使用者確認（第三版新增，領域顧問分流）**：

- **Q20 管理員寄的重設連結不受「忘記密碼」政策開關影響。**
  - 背景：政策 `auth.passwordResetEnabled` 管的是登入頁的自助申請。若管理員寄的連結也看這項，政策關掉時管理員按「寄重設連結」會靜默變成略過。
  - 推薦：不看（§6.3）。理由：關掉自助申請的客戶，多半是想只讓管理員控管重設，管理員寄連結正是那條路。代價：政策名稱要寫清楚只管自助，政策頁顯示「登入頁可自助申請重設密碼」（§9）。
  - 替代：也看政策，關掉時管理員對話框只剩暫時密碼。代價：管理員要另外記得改政策。
  - 選錯的代價：極小。
- **Q21 系統管理員改另一位系統管理員的 Email 時開安全事件。**
  - 背景：Email 就是重設密碼的鑰匙；改掉某位管理員的 Email 再替他申請重設，就能接管那個管理員帳號。比照 au1-8（重設另一位管理員的雙因素發安全事件）。
  - 推薦：開 `security.adminEmailChanged`，全部管理員看得到；在管理員送出時就開、不等新信箱驗證，清空另一位管理員的 Email 也開（§8.10）。代價：管理員之間改 Email 會多一則站內通知。
  - 替代：只寫稽核。代價：接管的前置動作沒人看到。
  - 選錯的代價：極小。
- **Q22 Demo 站的忘記密碼與寄重設連結只開放 `mfa01`。**
  - 背景：重設會換 stamp，訪客反覆替 `sales01` 申請重設，就能把所有正在用 `sales01` 的訪客登出。
  - 推薦：只開放 `mfa01`（示範路徑本來就用它），Demo 站忘記密碼頁加一行提示「Demo 站只有 mfa01 可示範重設密碼」（§12、§13.2）。代價：訪客不能拿其他示範帳號試忘記密碼。
  - 替代：開放全部示範帳號。代價：常用帳號會被反覆登出。
  - 選錯的代價：極小。
**使用者 2026-11-21 裁決**：

- **Q23 第二步暫停的通知：同一帳號 24 小時內只通知一次（信與站內通知都一樣），安全事件只累加次數。** 24 小時寫死，列 §22；`security.twoFactorFailures` 因此是 §8.10「每次發生都通知」的例外。
  - 背景：攻擊者手上有密碼時每 15 分鐘就能觸發一次暫停，不去重一天最多 96 封信與 96 則站內通知。
  - 代價：同一天第二波攻擊不會再收到信（安全事件的次數仍會增加，監控頁看得到）。

### 20.2 技術類（主控已定，可推翻）

- **T1** 一組 migration `AccountSecurity`，全部表與欄在 au1a 建好（§5）。
- **T2** TOTP、帳號層計數、寬限起點放 `AppUser`；復原碼、記住的裝置另開表（§5）。
- **T3** 連結權杖 32 bytes、只存 SHA-256、單次使用；放在 `#` 後面，讀完即清。
- **T4** 權杖在寄送時產生；寄送前重查資格；只作廢同一 Email 的舊權杖；使用時再比 Email；影響資格的事件同交易取消還沒寄的信（§6.3）。
- **T5** 寄送服務是獨立的 hosted service，即時喚醒＋30 秒掃描；領取協定沿用告警佇列；不節流不摘要（§6.2）。
- **T6** `IAlertMailSender` 改名 `IMailSender`（§6.1）。
- **T7** TOTP 自寫在 Domain，固定時間比較，`TotpLastStep` 擋重放（§8.1）。
- **T8** TOTP 金鑰 Data Protection 加密；`KeyPersistenceStatus.Persisted` 為否時不准新綁定（§8.2）。
- **T9** 第二步憑證 cookie：獨立 purpose、HttpOnly、SameSite=Strict、Secure 比照 session、Path 限定、隨機編號單次使用；密碼這一步不算成功；三層猜碼上限（§8.3）。
- **T10** 復原碼 10 組 16 字元、SHA-256（§8.4）。
- **T11** QR 碼在前端產生，新增 MIT 授權的 npm 套件（候選 `qrcode`），只在綁定對話框動態 import（D27）。
- **T12** issuer：站台名稱 → 站台網址主機名稱 → `Formwright`（§8.1）。
- **T13** 強制政策讀快取；讀不到用最後的值，從沒讀到用出廠值（§8.7）。
- **T14** （已拿掉，見 §16 第 8 點。）
- **T15** 救援設定清雙因素、復原碼、裝置、重設連結；沒移除開安全事件（§8.11）。
- **T16** 稽核動作：`account.passwordResetRequested`、`account.passwordResetByMail`、`account.setEmail`、`account.emailVerified`、`account.twoFactorEnable`、`account.twoFactorDisable`、`account.recoveryCodesRegenerate`、`account.trustedDevicesClear`、`account.reauthFailed`、`user.setEmail`、`user.sendResetLink`、`user.resetTwoFactor`，全部一般寫入（後端規則：高風險只留權限類）；涉及管理員帳號的另開安全事件（§8.10）。
- **T17** 管理員設 Email 用獨立端點，不併進整筆取代的 `PUT admin/users/{id}`。
- **T18** Render 免費方案是否擋 SMTP 埠（25、465、587）：**未查證**，au1 不下結論。待查：Render 官方文件的對外 SMTP 限制；若擋，候選是郵件服務的非標準埠（例 2525）或 HTTP 郵件 API（O3 比較時一起定）。
- **T19** 信件連結只用站台網址（`App:PublicUrl`，退回 `Ops:PublicUrl`），不用 Host 標頭（§6.4、D20）。
- **T20** 申請端 IP 節流寫死、每帳號次數是政策；以正規化輸入計次；資料庫寫不進照回 202（§7.2、§7.3）。
- **T21** 帳號 Email 不唯一（au1-3）。
- **T22** Demo：守衛以站為準；只限 `mfa01` 收重設信（Q22）；`mfa01` 啟動時重新加密；提示碼取目前時間步、`mfa01` 不檢查重放（§13、T32）。
- **T23** 記住的裝置綁 `SecurityStamp`，stamp 一換就全部失效；政策天數以目前值即時套用（§8.5）。
- **T24** 復原碼組數 10、剩 2 組提醒，寫死列可寫死清單（初稿 Q16 改列技術類）。
- **T25** 安全事件的收件人沿用 `ops.alertRecipients`，含當事人（§8.10）。
- **T27**（主控裁決，安全守衛）金鑰落地以程式算出的 `keysDirectory` 為準；環境不是 Development、`keysDirectory` 為 null 就拒絕啟動，不分資料庫種類（複審 N7）；「暫停強制」只留給 Development（§8.7）。金鑰目錄有設但不持久時開 `infra.keysMayBeLost`（複審建議 B）。復原碼與其他雜湊值不經 Data Protection，是給 bk1 還原路徑的契約（§19）。
- **T28** Demo 收件匣的信件內文存在 `Dynamic_MailOutbox.PreviewBody`（只在 Demo 寫，正式環境永遠 null；§5.5、§13.2）。初稿列在 Q12，改列技術類。
- **T29** 第二步的限流結算：密碼那一次 `Release`、第二步每次送碼（含復原碼）自己 `TryBegin`／`Complete`，金鑰解不開、暫停中、例外時 `Release`（§8.3，複審 N1、建議 D）；帳號層用一條帶 `CASE` 的原子更新同時遞增與寫暫停，成功時兩欄一起清，金鑰解不開不佔名額（複審 N6）；帳號層與失敗路徑的寫入在請求交易外提交，第二步不是 `ITransactionalRequest`，重新驗證的端點拆成外層驗證、內層交易（§8.9，複審 N5）。
- **T33** 存強制政策時批次清掉不在新範圍內的寬限起點；群組異動與拿掉系統管理員不批次清（§8.7，複審建議 C）。
- **T34** Demo 的 `mfa01` 被拖慢列已知限制，不寫 Demo 專屬限流分支（§8.3，複審建議 F）。
- **T30** 記住的裝置每帳號上限 20 台、超過刪最舊；不受帳號層暫停影響（§8.5）。
- **T31** 寬限起點離開強制範圍時清空（§8.7）；救援與管理員重設清掉全部雙因素相關欄位（§8.8、§8.11）。
- **T32** Demo 站：不套帳號層暫停、`mfa01` 不檢查重放、「全部取消記住」403（§8.3、§13.4、§8.5）。
- **T26** `auth.twoFactorGroups` 先試用既有 `Recipients` 型別只收 `group:<id>`（Application 驗證）；政策頁的編輯器無法限定只選群組時，新增 `GroupSet` 型別。

### 20.3 審查建議的處置（同家審查 15 條）

| # | 建議 | 處置 |
|---|---|---|
| 1 | 第二步 cookie：Secure 比照、獨立 purpose、隨機編號單次、密碼步不寫成功 | 採用（§8.3、T9） |
| 2 | 第二步明寫檢查帳號啟用 | 採用（§8.3 第 3 點、F30） |
| 3 | 固定時間比較、`no-store` | 採用（§8.1、§8.2、§11） |
| 4a | Demo 提示碼並行會撞重放 | 採用；第三版改成 Demo 的 `mfa01` 不檢查重放（安全複審建議 5，§13.4、F27） |
| 4b | Demo 復原碼被用完 | 採用，提示一組可用的、demo.md 註明（§13.4） |
| 4c | Demo 重設常用帳號會登出其他訪客 | 採用，示範路徑改用 `mfa01`（§13.5） |
| 5 | 管理員不能用管理員端點重設自己的雙因素 | 採用（§8.8、F20） |
| 6 | 清理只清終態；分類寫明 | 採用（§6.6、§9、M11） |
| 7 | 申請節流依正規化輸入計次、被節流也跑同樣查詢 | 採用（§7.2、R8） |
| 8 | 資料庫寫不進時也回 202 | 採用（§7.2、R17） |
| 9 | 信件語系從 `ICurrentLocale` 取 | 採用（§6.4） |
| 10 | Email 比對在 C# 做 | 採用（§5.6） |
| 11 | 共用檔清單補齊（`AuthenticateMcpToken.cs`、middleware 欄位、Demo Email 寫在哪） | 採用；權杖可用條件依 au1-10 不改，所以改列 `McpTokenHandlers.cs`、`McpTokenRequests.cs`、`AiTokensPage.vue`（§17） |
| 12 | 救援設定沒移除開安全事件 | 採用（§8.11、F23） |
| 13 | 金鑰檔明文、和資料目錄分開放 | 採用，寫進手冊（§16 第 9 點、§19） |
| 14 | 復原碼登入、連續失敗寄信 | 採用（§6.4、§8.3、Q5、Q19） |
| 15 | 用復原碼重新綁定會用掉一組；金鑰解不開時的流程；otpauth URL 編碼 | 採用（§8.2、§8.4、§8.1、F25） |

### 20.4 安全複審的處置（必修 N1–N4、建議 13 條）

| # | 項目 | 處置 |
|---|---|---|
| N1 | 第二步前的限流結算 | 採用：密碼那一次 `Release`，第二步自己 `TryBegin`／`Complete`（§8.3、T29、F33） |
| N2 | 金鑰落地以程式算出的目錄為準 | 採用：`KeyPersistenceStatus`，SQLite 預設 `keys` 目錄算落地（§8.7、§14、F24、F37） |
| N3 | T27 在正式環境 fail-open | 採 (a)：正式環境拒絕啟動，暫停強制只留非正式環境；主控裁決，列 §22（§8.7、T27、F36）。第四版依 N7 改成「非 Development 一律拒絕、不分 provider」（§20.5） |
| N4 | M3 跨段；`security.adminPasswordReset` 歸屬 | 採用：`emailChanged` 移到 au1a；`adminPasswordReset` 歸 au1b、au1c 是其餘三種（§8.10、§17） |
| 1 | 帳號層計數並行 race | 採用，條件式遞增（§8.3、T29） |
| 2 | 暫停通知去重 | 採用，使用者裁決 Q23（F39） |
| 3 | 記住的裝置繞過帳號層暫停 | 採用，明寫是刻意取捨並設每帳號 20 台（§8.5、T30） |
| 4 | Demo 忘記密碼只開放 `mfa01` | 採用，顧問拍板 Q22（§7.1、§13.2、R18、R22） |
| 5 | Demo 提示碼的第三位訪客 | 採用，Demo 的 `mfa01` 不檢查重放（§13.4、T32、F27） |
| 6 | 管理員寄連結與政策開關 | 採用，管理員寄的不看政策、使用時不再查，顧問拍板 Q20（§6.3、§9、R20） |
| 7 | 救援要清的欄位補齊 | 採用（§8.11、T31） |
| 8 | 寬限起點會過時 | 採用（§8.7、T31、F40） |
| 9 | §8.9 驗收補案例 | 採用（F34） |
| 10 | 復原碼雜湊的輸入 | 採用，寫進 bk1 契約（§8.4、§19） |
| 11 | 管理員改另一位管理員的 Email 開安全事件 | 採用，顧問拍板 Q21（§8.10、M15） |
| 12 | 匿名權杖端點節流 | 採用，每 IP 15 分鐘 30 次（§7.3、§22、R21） |
| 13 | `render.yaml` 不存在 | 採用，改指 `docs/deploy.md` 與 Render 後台環境變數（§16 第 5 點、§17） |

另依領域顧問分流：Demo 站不套帳號層暫停（§8.3、F35）；管理員重設雙因素一併清計數與暫停（§8.8、F38）；Demo 站「全部取消記住」403（§8.5、§10）；`PreviewBody` 改列技術類 T28；Q17–Q19、Q12（取代 D24）標顧問拍板；Q1、Q10 確認成立。

### 20.5 第二次安全複審的處置（必修 N5–N7、建議 A–F）

| # | 項目 | 處置 |
|---|---|---|
| N5 | 帳號層計數被請求交易回滾 | 採用：第二步不開交易、重新驗證端點拆兩段、不在交易內另開 scope（§8.3、§8.9、T29、F7、F34、M14） |
| N6 | 計數卡在 10 而沒有暫停時間 | 採用：一條帶 `CASE` 的原子更新、成功兩欄一起清、金鑰判斷排在佔名額之前（§8.3、F41、F42） |
| N7 | 拒絕啟動只看 PostgreSQL | 採第一種：非 Development 且 `keysDirectory` 為 null 就拒絕，不分 provider（§8.7、§14、§22、T27、F36、F36b、§23） |
| A | 既有 Production 測試與 au1c 測試的金鑰目錄 | 採用（§19 測試重點、§23） |
| B | 金鑰目錄有設但不持久 | 採用：`infra.keysMayBeLost`（§8.7、§14、§19、F43） |
| C | F40 照原機制過不了 | 採第一種的一半：存強制政策時批次清；群組與系統管理員異動列刻意取捨（§8.7、T33、F40） |
| D | 結算語意 | 採用：「含復原碼」、金鑰解不開 `Release`、重新驗證端點比照第二步（§8.3、§8.9、T29） |
| E | 文字殘留 | 採用：§20.3 第 4a 列改寫、`demoRestricted` 補進 §7.2、R22 移成 M15 |
| F | Demo 的 `mfa01` 被拖慢 | 列已知限制（§8.3、§19、T34），記進 `docs/demo.md` |

另依領域顧問分流：Q20–Q22 改標顧問拍板，並補政策頁名稱（§9）、`adminEmailChanged` 的開立時機（§8.10）、Demo 忘記密碼頁提示（§12、§13.2）；Q23 依使用者 2026-11-21 裁決改寫（§8.3、§8.10、§22）；附件目錄刻意不一起強制、`docs/deploy.md:107` 回填列 §16。

## 21. 宗旨檢查（formwright-purpose-check）

1. **機制還是政策**：通過。佇列、權杖、TOTP、記住裝置是平台機制；忘記密碼開關、連結時效、每小時次數、驗證時效、密碼最短長度、強制範圍、寬限、記住天數、保留天數都是政策（§9）。節流、TOTP 參數、復原碼組數、猜碼上限是安全檢查或技術限制，列 §22。
2. **寫死掃描**：設計階段不適用；審查 diff 時照 skill 的 grep 再跑，命中項對照 §22。信件不寫死產品名稱（au1-漏3）。
3. **單一來源**：通過。時效、次數、最短長度只在 `PolicyCatalog`，前端從 API 讀；密碼最短長度的四份常數收成政策（§7.6）；重試表沿用 `AlertLimits`。
4. **兩個應用程式**：通過。帳號安全是平台功能；寄信佇列之後簽核、任務通知都會用。
5. **空系統**：通過。不開任何應用程式時全部照常。
6. **套件只用定義組成**：不適用。
7. **ERP 對照**：通過，見 §15。
8. **權限 fail closed、驗證不只在前端、沒有執行期 DDL**：通過。權杖、憑證、TOTP 金鑰讀不到都當失敗；強制綁定由 middleware 擋；金鑰不落地不准綁，非 Development 環境金鑰不落地拒絕啟動（不 fail-open）；schema 走 EF migration。
9. **不限產業**：通過。共用組信箱（工廠現場）、外出業務換手機、門市共用電腦都用同一套規則，不假設產業。

## 22. 可寫死清單

| 項目 | 值 | 類別 |
|---|---|---|
| TOTP 演算法、位數、步長、容許步數 | SHA1、6、30 秒、±1 | 技術限制（App 相容） |
| TOTP 金鑰長度 | 20 bytes | 安全檢查 |
| 綁定中金鑰時效 | 10 分鐘 | 安全檢查 |
| 第二步憑證時效、同一憑證次數 | 5 分鐘、5 次 | 安全檢查 |
| 非 Development 環境金鑰不落地拒絕啟動（不分資料庫種類） | — | 安全守衛（主控裁決，T27） |
| 第二步暫停的通知去重 | 同帳號 24 小時一次（信與站內通知） | 安全檢查（使用者裁決，Q23） |
| 記住的裝置每帳號上限 | 20 台 | 技術限制（T30） |
| 帳號層第二步上限、暫停時間 | 10 次、15 分鐘 | 安全檢查（Q19） |
| 復原碼組數、長度、提醒門檻 | 10 組、16 字元、剩 2 組 | 安全慣例（T24） |
| 連結權杖、裝置 cookie 長度 | 32 bytes | 安全檢查 |
| 記住天數上限、寬限天數上限 | 90、90 | 技術上限（政策的上下限） |
| 重設申請 IP 節流 | 15 分鐘 10 次 | 安全檢查（同 `LoginThrottle`） |
| 用權杖的匿名端點 IP 節流 | 15 分鐘 30 次 | 安全檢查 |
| 重寄驗證信、管理員寄重設連結間隔 | 同帳號 60 秒 | 技術限制 |
| 寄送重試、領取期限、寄送逾時、掃描間隔 | 1／5／15／60 分鐘最多 5 次、2 分鐘、30 秒、30 秒 | 技術限制（沿用 `AlertLimits`） |
| 密碼最短長度的技術下限 | 8 | 安全下限（政策下限） |
| Email 長度、格式；站台名稱長度 | 255、`MailAddressRules.IsEmail`；100 | 格式守衛 |
| 重設信不繞過雙因素、不自動登入 | — | 安全檢查 |
| 信件連結只用站台網址 | — | 安全檢查 |
| 申請回應不透露帳號狀態 | — | 安全檢查 |
| 管理員不能用管理員端點重設自己的雙因素 | — | 安全檢查 |
| Demo 站：只有 `mfa01` 有雙因素、強制不生效、`auth` 政策唯讀、只有 `mfa01` 收重設信、不套帳號層暫停、`mfa01` 不檢查重放、不能取消記住的裝置 | — | Demo 完整性守衛 |

## 23. 留給實作者

- `AccountMailText` 八種信的四語主旨與內文；第二步、帳號安全頁、強制綁定頁、寬限提示的四語介面文字。
- QR 碼套件的版本、授權與打包大小確認（T11）。
- `mfa01` 的固定金鑰、復原碼與 `docs/demo.md` 新小節。
- `Recipients` 型別能否限定只選群組（T26）。
- 非 Development 環境拒絕啟動（T27）會讓兩條既有測試失敗，同一段補上 `DataProtection:KeysDirectory` 暫存目錄：`WebAPITests/AuthApiTests.cs:556–565`（`Swagger_NotExposedOutsideDevelopment`，類別標 `Category=Postgres`，也在 PostgreSQL CI 裡跑）與 `WebAPITests/Ops/RequestContextTests.cs:223`（`ConsoleFormatter_JsonOutsideDevelopment`）。實作時再 grep 一次 `UseEnvironment("Production")` 與 `UseEnvironment("Staging")`，有新增的一起補。
- `docs/deploy.md:107` 的金鑰目錄說明改寫（§16 第 15 點）。
- 交易外的 `ExecuteUpdate`（帳號層佔名額與清除）不經 PostgreSQL 的全庫寫入鎖，照後端規則在交付說明列出（§8.3）。
- au1a、au1c 太大時的拆法見 §17，migration 只能留在 au1a。
- 手冊各段的實際畫面文字以實作為準回填（§16 第 9 點）。
