# 帳號安全 設計文件（段 au1）

> 2026-11-21｜平台共用能力（非應用程式）｜前置：[使用者與群組](2026-11-05-users-groups-design.md)（§4 登入）、[維運與觀測](2026-11-11-ops-observability-design.md)（§14 U1、§17.2.6 寄信設定與告警佇列、§17.9 安全）、[政策設定](2026-11-11-policy-settings-design.md)、[MCP 伺服器](2026-11-11-mcp-server-design.md)（§13 個人存取權杖）、[權限軸](2026-11-19-permission-axes-design.md)
> 狀態：主控撰寫（2026-11-21），這次只寫設計、不改程式。依使用者 2026-11-21 裁決：會改變使用者操作的小題照推薦先定，寫成「主控暫定，待使用者確認」；技術類主控直接定。
> 標記：「（使用者裁決）」是使用者已拍板的；「（Q#）」是行為類，主控暫定、待使用者確認，彙整在 §20.1；「（T#）」是技術類，主控已定、可推翻，彙整在 §20.2。

## 1. 目的

雲端訂閱版的站台對外開放，現在只靠帳號密碼，忘記密碼只能找管理員（使用者與群組設計 §4：「忘記密碼由管理員重設；系統不寄信」）。使用者 2026-11-17 裁決：雲端版對外開放前必須有寄信重設密碼與雙因素登入（`docs/feature.md` 第 56、57 列）。

au1 做三件事：

1. **核心寄信佇列**：帳號信（重設密碼、Email 驗證、安全通知）的寄送佇列，放在核心、不依賴任何應用程式。SMTP 連線沿用 o2e 已抽成核心的「寄信設定」，與告警信共用連線、佇列分開；帳號信不節流、不摘要（使用者 2026-11-19 裁決，roadmap au1 列）。之後簽核、任務的 Email 通知（第二版候選）也寄這個佇列。
2. **寄信重設密碼**：登入頁「忘記密碼？」→ 寄一封有時效、只能用一次的連結到帳號已驗證的 Email → 設新密碼。照 OWASP Forgot Password Cheat Sheet：不透露帳號是否存在、連結短時效、單次使用、重設後登出其他 session。
3. **雙因素登入（TOTP）**：驗證器 App（RFC 6238），附復原碼；系統管理員可設強制或選用（使用者 2026-11-17 裁決）；手機遺失由系統管理員重設。

SSO、AD／LDAP 列第二版候選（使用者 2026-11-17 裁決），au1 不做。

## 2. 範圍

**範圍內**

- 帳號 Email：`AppUser` 加 Email 與驗證時間；管理員在使用者管理設定、本人在「帳號安全」頁設定（Q3）；改了就要點驗證信才生效（Q2）。
- 核心寄信佇列 `Dynamic_MailOutbox`、寄送服務、重試、失敗事件、「帳號信寄送紀錄」清單（寄信設定頁）。
- 忘記密碼：申請頁、重設頁、連結權杖表 `Dynamic_AccountToken`、節流、安全通知信（Q5）。
- 雙因素：綁定、登入第二步、復原碼、本人關閉、管理員重設、強制政策、強制綁定期間的 API 封鎖。
- 「帳號安全」頁 `/account/security`（Q14）。
- 政策：新分類「帳號安全」五項（§9）。
- 啟動救援 `Auth:ResetAdminPassword` 一併清除 `admin` 的雙因素（T15）。
- Demo：示範帳號的虛構 Email、雙因素示範帳號、Demo 收件匣（§13）。

**範圍外**

- SSO、AD／LDAP、Passkey／WebAuthn、簡訊與 Email 驗證碼當第二因素：第二版候選或不做。
- 「記住這台裝置」（Q8）。
- 自助註冊（功能清單「不做」不變）。
- 業務 Email（簽核、任務通知）：仍是第二版候選；au1 只把佇列做成核心，不加任何業務信種類。
- 密碼組成規則與最短長度政策化：沿用現有「至少 8 碼」（`ChangePasswordValidator.MinLength`），不在 au1 改（§16 第 6 點）。
- 雲端版的郵件服務選擇（SMTP 還是 HTTP 郵件 API）：au1 只留抽象，實際選擇與查證併入 O3（T18）。
- 多實例部署下的節流與第二步嘗試次數：沿用現有「單一站台實例」假設（同 `LoginThrottle`），已知限制。

## 3. 現況盤點（main 84dd2a05）

| 項目 | 現況 | 證據 |
|---|---|---|
| 帳號欄位 | `UserId`、`DisplayName`、`IsSystemAdmin`、`PasswordHash`、`MustChangePassword`、`IsActive`、`IsDemoAccount`、`FailedLoginCount`、`LockedUntil`、`SecurityStamp`、外觀兩欄；**沒有 Email** | `Domain/Entities/Dynamic/AppUser.cs`、`Domain/Data/Configurations/AppUserConfiguration.cs`；維運設計 §17.2.6「`AppUser` 沒有 Email 欄位（O2-U1）」 |
| 登入 | 帳號小寫、PBKDF2 驗密碼；帳號不存在、停用、鎖定、沒密碼都跑一次假雜湊對齊時間；失敗一律同一則 401；成功與失敗寫稽核 `login.succeeded`／`login.failed`（含 `ReasonCode`） | `Application/Dynamic/AccountService/Commands/Login/LoginHandler.cs` |
| 登入限流 | 記憶體、單一實例：帳號＋IP 15 分鐘 5 次、IP 20 次、帳號 1 小時 50 次後每 10 秒一次；常數寫死 | `Application/Common/Security/LoginThrottle.cs:11` |
| Session | ASP.NET cookie，HttpOnly、SameSite=Lax、8 小時滑動、關瀏覽器失效；claim 帶 `SecurityStamp`，每個請求比對資料庫的 `IsActive` 與 stamp | `WebAPI/Program.cs:119–126`、`WebAPI/Security/AuthCookies.cs`、`WebAPI/Security/SessionMiddleware.cs` |
| 匿名白名單 | `POST auth/login`、`GET auth/options`、`GET theme`、`POST auth/demo-login`；可選登入：前端錯誤收件、介面文字 | `SessionMiddleware.cs:67` 起 |
| 強制改密碼 | `MustChangePassword` 時只放行 `/auth/*` 與 `GET /me`，其餘 403 `passwordChangeRequired`；前端導到 `/change-password` | `SessionMiddleware.cs:57`、`frontend/src/router/auth-guard.ts` |
| 改密碼 | 本人要舊密碼；新密碼至少 8 碼；換 stamp、重發本 session cookie；不撤銷 AI 權杖 | `ChangePasswordHandler.cs`、`ChangePasswordValidator.cs` |
| 管理員重設密碼 | 設暫時密碼、要求改密碼、解鎖、換 stamp、同交易撤銷全部 AI 權杖；稽核 `user.resetPassword`（一般寫入） | `ResetPasswordHandler.cs`、`Common/Audit/AuditActions.cs:34` |
| 救援 | 啟動設定 `Auth:ResetAdminPassword` 重設 `admin` 密碼、啟用、解鎖、換 stamp | `WebAPI/Security/AdminBootstrap.cs`、`docs/manual.md:28` |
| 寄信設定 | 核心單列 `Dynamic_MailSettings`（主機、埠、加密、帳號、密碼密文、寄件地址與名稱）；`/system/mail` 頁與測試信；Demo 站唯讀、測試信 403 | `Domain/Entities/Dynamic/MailSettings.cs`、`Application/Dynamic/MailService/MailSettingsHandlers.cs`、`frontend/src/pages/system/MailSettingsPage.vue` |
| 寄送 | MailKit：`startTls` 強制、憑證一律驗證、`none` 不准帶帳密；失敗只回階段與回應碼；介面 `IAlertMailSender` 以告警為名 | `WebAPI/Ops/AlertMail.cs`、`Application/Dynamic/OpsService/Alerts/AlertContracts.cs:56` |
| 告警佇列 | `Dynamic_AlertOutbox`：條件式更新領取、交易外寄送、重試 1／5／15／60 分鐘最多 5 次、每小時上限與摘要、失敗開 `channel.smtp` 事件 | 維運設計 §17.2.6、`AlertContracts.cs:86–95`、`AlertDelivery.cs:107` |
| 信件網址 | 部署設定 `Ops:PublicUrl`，告警信沒設就不放連結 | `AlertContracts.cs:73–79`、`Program.cs:101` |
| Data Protection | 登入 cookie、SMTP 密碼、任務復原權杖共用；PostgreSQL 沒設 `DataProtection:KeysDirectory` 只出啟動警告，重啟後金鑰不見 | `Program.cs:139–147` |
| AI 權杖 | 帳號可用條件 `IsActive && !IsDemoAccount && !MustChangePassword`；Demo 站一律拒 | `McpService/Tokens/McpTokenHandlers.cs:42`、`McpService/Auth/AuthenticateMcpToken.cs:45` |
| 政策 | `PolicyCatalog` 唯一登記處；`Choice`、`Integer`、`Boolean` 等型別；系統政策快取 60 秒 | `Domain/Policies/PolicyCatalog.cs`、`Domain/Policies/PolicyDefinition.cs:13`、`PolicyService/CachedSystemPolicy.cs:30` |
| 前端 | 頁首有「變更密碼」「AI 連線」「登出」三個按鈕；沒有 QR 碼套件 | `frontend/src/layouts/MainLayout.vue:19–31`、`frontend/package.json` |
| 語系 | 使用者沒有存個人語系，請求語系來自 `X-Locale` 標頭；背景流程用系統預設語系 | `WebAPI/Security/HeaderCurrentLocale.cs` |
| Demo | 13 個示範帳號，沒有密碼、一鍵登入；Demo 站外觀、寄信設定、AI 權杖都擋寫入 | `Persistence/Seed/DemoPeople.cs`、`docs/demo.md`「登入與換人」 |

## 4. 已定決定（本設計照用，不再問）

- 帳號信與告警信共用核心寄信設定的連線，佇列、收件人、語系分開（使用者 2026-11-19 裁決）。
- 帳號信佇列不節流、不摘要（使用者裁決，roadmap au1 列）。防濫用放在「申請」那一端（§7.3），不放在佇列。
- 雙因素用 TOTP 驗證器 App；系統管理員可設強制或選用；SSO、AD／LDAP 第二版（使用者 2026-11-17 裁決）。
- 簽核、任務的 Email 通知是第二版候選（使用者 2026-11-17 裁決），au1 不做。
- 業界慣例當主控暫定的依據：OWASP Forgot Password Cheat Sheet、RFC 6238、復原碼、手機遺失由系統管理員重設（派工說明）。

## 5. 資料模型與 migration（T1）

一組 migration `AccountSecurity`（SQLite 與 PostgreSQL 各一個、同名，`scripts/db/add-migration.sh` 產生），全部放在 au1a，au1b、au1c 不再改表（比照 o2a）。

### 5.1 `Dynamic_AppUser` 加欄（T2）

| 欄位 | 型別 | 說明 |
|---|---|---|
| Email | string(255)，可空 | 帳號 Email，只用於帳號信；不唯一（T21）；存去空白、原大小寫，比對不分大小寫 |
| EmailVerifiedUtc | DateTime，可空 | null＝未驗證；Email 一改就清成 null |
| TotpSecretProtected | string(500)，可空 | 已啟用的 TOTP 金鑰，Data Protection 加密（purpose `account-totp`，T8）；null＝沒啟用 |
| TotpEnabledUtc | DateTime，可空 | 啟用時間 |
| TotpLastStep | long，可空 | 最近一次驗證成功的時間步，擋重放（T7） |
| TotpPendingProtected | string(500)，可空 | 綁定中的金鑰（尚未驗證）；加密同上 |
| TotpPendingUtc | DateTime，可空 | 綁定開始時間；超過 10 分鐘作廢（技術限制） |

`SessionMiddleware` 每個請求多讀 `IsSystemAdmin`、`TotpEnabledUtc` 兩欄（同一次依主鍵查詢，不多一次往返）。

### 5.2 `Dynamic_UserRecoveryCode`（新表）

| 欄位 | 型別 | 說明 |
|---|---|---|
| Id | long，自動編號主鍵 | |
| UserId | string(50) | 不設外鍵（同 `McpToken`） |
| CodeHash | string(64) | SHA-256 十六進位（T10） |
| CreatedUtc | DateTime | |
| UsedUtc | DateTime，可空 | 用過就填；條件式更新 `WHERE Id = ? AND UsedUtc IS NULL` 保證只能用一次 |

索引：`(UserId, CodeHash)` 唯一。重新產生或關閉雙因素時刪掉該使用者全部列。

### 5.3 `Dynamic_AccountToken`（新表）

| 欄位 | 型別 | 說明 |
|---|---|---|
| Id | long，自動編號主鍵 | |
| UserId | string(50) | |
| Purpose | string(20) | `passwordReset`、`emailVerify` |
| TokenHash | string(64) | 明文 32 bytes 隨機值的 SHA-256；明文只出現在信裡（T3） |
| Email | string(255) | 寄到哪個地址；`emailVerify` 驗證時必須等於帳號目前的 Email |
| CreatedUtc | DateTime | ＝寄送時間（T4） |
| ExpiresUtc | DateTime | 依政策（§9） |
| UsedUtc | DateTime，可空 | 用過、或被新權杖取代時填（`RevokedReason` 區分） |
| RevokedReason | string(20)，可空 | `used`、`superseded`、`passwordChanged`、`emailChanged` |

索引：`TokenHash` 唯一；`(UserId, Purpose)`；`ExpiresUtc`（清理）。

### 5.4 `Dynamic_MailOutbox`（新表，核心寄信佇列）

| 欄位 | 型別 | 說明 |
|---|---|---|
| Id | long，自動編號主鍵 | |
| Kind | string(30) | `passwordReset`、`emailVerify`、`passwordChanged`、`twoFactorDisabled`、`emailChanged`；之後的業務信加種類，不改表 |
| UserId | string(50)，可空 | 收件帳號 |
| ToAddress | string(255) | 寫入時的收件地址快照 |
| Locale | string(10) | 信件語系：申請者的請求語系，或系統預設語系（§6.4） |
| ParamsJson | string(1000)，可空 | 組信用的白名單參數（例：變更時間、操作者是管理員或本人）；**不放權杖、密碼、驗證碼** |
| CreatedUtc | DateTime | |
| NotAfterUtc | DateTime，可空 | 過了就不寄、記 `expired`（重設信＝申請時間＋連結時效，T4） |
| NextAttemptUtc | DateTime | |
| Attempts | int | |
| Status | string(20) | `pending`、`sending`、`sent`、`failed`、`skipped`、`expired` |
| ClaimedBy、ClaimedUntilUtc | string(100)、DateTime，可空 | 同告警佇列的領取欄 |
| SentUtc | DateTime，可空 | |
| LastError | string(500)，可空 | 階段＋SMTP 回應碼，不含例外全文 |
| PreviewBody | string(4000)，可空 | **只在 Demo 站填**（Demo 收件匣，Q12）；正式環境永遠 null |

索引：`(Status, NextAttemptUtc)`；`CreatedUtc`（清理與清單排序）；`UserId`。

### 5.5 SQLite 與 PostgreSQL

只有 EF 依 provider 產生的型別差異（同維運設計 §17.4 末段）；沒有部分索引、沒有 provider 專屬 SQL，兩份 migration 都不手改。新增欄位全部可空，既有列不需搬資料；Down 刪欄刪表。

## 6. 核心寄信佇列（au1a）

### 6.1 寄件元件抽到核心（T6）

- `IAlertMailSender` 改名搬到 `MailService` 成為 `IMailSender`，告警與帳號信共用；MailKit 實作與安全邊界（強制 STARTTLS、驗憑證、不准明文帶帳密、只回階段與回應碼）一字不改。
- 寄件設定一律讀 `Dynamic_MailSettings`，帳號信不另存連線。
- 傳輸方式只有 SMTP。介面設計成「給一封已組好的信、回階段與回應碼」，O3 若要加 HTTP 郵件 API（T18），只多一個實作與寄信設定的「傳輸方式」欄，佇列與呼叫端不動。

### 6.2 寄送服務（T5）

- 新的 hosted service `MailDeliveryService`，不走 `IScheduledJob`：排程器最短 1 分鐘一輪，重設信要更快。
  - 喚醒：寫入佇列的交易提交後，發記憶體訊號立刻跑一輪；另外每 30 秒（技術限制）掃一次到期的重試與過期的領取。
  - 部署設定 `Mail:Delivery:Enabled`（預設 true；測試主機關掉，直接呼叫一輪）。
- 每封三步，同告警佇列（維運設計 §17.2.6）：**領取**（短交易、條件式更新 `pending → sending`、寫 `ClaimedBy` 與 2 分鐘期限）→ **寄送**（交易外，逾時 30 秒）→ **記結果**（`WHERE Id = ? AND ClaimedBy = 我`）。正確性靠條件式更新，不靠租約，多實例也不會重複領。
- 不節流、不摘要（使用者裁決）：沒有每小時上限、沒有通道列鎖。
- 重試：1、5、15、60 分鐘，最多 5 次（沿用 `AlertLimits`，技術限制）；過了 `NotAfterUtc` 的不再試，記 `expired`。
- 略過：寄信設定沒設好、或密碼解不開 → `skipped`，`LastError` 記 `notConfigured`／`passwordUnreadable`。
- 失敗事件：第 5 次仍失敗記 `failed`，開或更新事件 `channel.accountMail`（來源 `channel`、類別基礎設施，只站內，同 `channel.smtp` 的規則：`channel` 事件永遠不寄信）。之後任何一封帳號信寄成功就結案。告警佇列與帳號佇列各自一個事件，SMTP 整個壞掉時兩件都會開，管理員看得出兩邊都受影響。
- 資料庫唯讀或連不上時：申請端寫不進佇列，直接回 503（既有行為），沒有記憶體直送（帳號信不是告警，不值得多一條路）。

### 6.3 寄送時產生權杖（T4）

佇列不存任何秘密。`passwordReset`、`emailVerify` 兩種信在**領取之後、寄送之前**的短交易裡：

1. 把同一帳號、同一用途還沒用過的權杖全部標 `superseded`；
2. 產生新權杖（32 bytes 隨機），存雜湊、`ExpiresUtc`＝現在＋政策時效；
3. 明文只放在記憶體裡組信，寄完就丟。

同一封信重試時會換一把新權杖、舊的作廢，收件人只有最後一封信的連結有效。程式在第 2、3 步之間當掉：列停在 `sending`，期限過後重領、再換一把權杖，前一把從沒寄出也已作廢，不會有能用卻沒人知道的權杖。

### 6.4 信件內容與語系

- 文案放 `AccountMailText`（鍵 `accountMail.`），C# 四語表，走 `IMessageCatalog`（語系包可覆寫），同 `AlertMailText` 的做法。純文字信，不寄 HTML。
- 語系：使用者自己申請的信（忘記密碼、本人改 Email 的驗證信）用申請請求的 `X-Locale`；管理員觸發或系統觸發的（管理員設 Email 的驗證信、安全通知）用系統預設語系（後端規則「請求沒指定語系時用系統預設語系」）。存在列上，寄送時才組字，**不存內文快照**（和告警信不同：告警信的快照是為了不隨事件改寫；帳號信內容只由種類與白名單參數決定，存了反而要處理權杖明文）。
- 連結網址只用 `Ops:PublicUrl`，**不用請求的 Host 標頭**（OWASP：Host 標頭可偽造，連結會指到攻擊者網站）。沒設 `Ops:PublicUrl` 時寄不出要帶連結的信：忘記密碼視為不可用、Email 驗證信不寄（§6.5）；寄信設定頁顯示「部署設定沒有站台網址（Ops:PublicUrl），重設密碼與 Email 驗證信寄不出去」（T19）。
- 連結格式：`{PublicUrl}/reset-password#t={權杖}`、`{PublicUrl}/verify-email#t={權杖}`。權杖放在 `#` 後面，不會進伺服器 access log、也不會隨 Referer 送出（T3）。
- 主旨例（繁中）：「[Formwright] 重設密碼」「[Formwright] 驗證你的 Email」「[Formwright] 你的密碼已變更」「[Formwright] 你的雙因素驗證已關閉」「[Formwright] 你的帳號 Email 已變更」。內文寫帳號、時間（營運時區並標時區）、連結與時效、「不是你本人操作請聯絡系統管理員」。不寫來源 IP（Demo 與雲端版都不該把 IP 寄出去）。

### 6.5 Email 與驗證（Q2、Q3）

- 管理員：使用者管理每列多「設定 Email」動作，`PUT admin/users/{id}/email`（T17）。
- 本人：「帳號安全」頁改 Email，要輸入目前密碼，`PUT account/email`。
- 改了（含清空以外的任何變更）就：`EmailVerifiedUtc` 清空、作廢該帳號全部未用權杖（`emailChanged`）、寄驗證信到新地址；舊地址若已驗證，另寄「Email 已變更」通知（Q5）。
- 驗證：`/verify-email#t=` 頁把權杖送 `POST auth/verify-email`（匿名可呼叫：點信的人不一定登入著）。權杖有效、沒用過、權杖的 Email 等於帳號目前的 Email → 填 `EmailVerifiedUtc`、權杖標 `used`。
- 重寄驗證信：本人與管理員各一個按鈕；同一帳號 60 秒一次（技術限制，避免連點寄出一串信）。
- 寄信設定沒設好或沒設 `Ops:PublicUrl` 時：Email 照存，狀態顯示「未驗證（系統目前寄不出驗證信）」。
- 只有「已驗證」的 Email 會收到重設密碼信；安全通知信寄到已驗證的地址，未驗證就不寄。

### 6.6 帳號信寄送紀錄

寄信設定頁多一區「帳號信寄送紀錄」，`GET system/mail-outbox?page=`（系統管理員），依建立時間新到舊、每頁 50 筆：時間、種類、帳號、收件地址、狀態、嘗試次數、最近錯誤。清單不含內文；Demo 站多一個「看信」按鈕顯示 `PreviewBody`（Q12）。

保留：政策 `mail.outboxRetentionDays`（出廠 30 天），由既有的 `ops.upkeep` 清理，連同過期超過同樣天數的 `Dynamic_AccountToken`。

## 7. 寄信重設密碼（au1b）

### 7.1 流程

1. 登入頁「忘記密碼？」→ `/forgot-password`，輸入帳號（Q1）→ `POST auth/forgot-password { userId }`。
2. 伺服器**一律**回 202 與同一句：「如果這個帳號存在並設定了已驗證的 Email，我們已寄出重設密碼的信。信裡的連結 {minutes} 分鐘內有效。」不論帳號存不存在、停用與否、有沒有 Email、是否被節流。
3. 符合資格才寫一列 `passwordReset` 佇列（`NotAfterUtc`＝現在＋連結時效）。資格：帳號存在、啟用、Email 已驗證、政策 `auth.passwordResetEnabled` 為是、寄信可用（§7.4）、不是示範帳號（Demo 站例外，T22）。
4. 使用者點信裡的連結 → `/reset-password#t=`，頁面先 `POST auth/reset-password/check { token }` 確認還有效（不消耗權杖），有效才顯示新密碼表單，無效直接顯示「連結已失效或已使用過，請重新申請」與「重新申請」按鈕。
5. `POST auth/reset-password { token, newPassword }`：權杖有效、沒用過、用途對、帳號仍啟用 → 同一交易：
   - 設新密碼雜湊；`MustChangePassword`＝false；`FailedLoginCount`＝0、`LockedUntil`＝null；
   - 換 `SecurityStamp`（全部 session 在下一個請求失效）；
   - 該帳號全部 `passwordReset` 權杖標 `used`／`passwordChanged`；
   - 撤銷全部 AI 權杖（`McpRevokeReason.PasswordReset`，Q4）；
   - 寫一列 `passwordChanged` 通知信（Q5）；
   - 稽核 `account.passwordResetByMail`（一般寫入，T16）。
6. 回 200，前端導到登入頁並提示「密碼已重設，請用新密碼登入」。**不自動登入**（Q4）；有雙因素的帳號登入時照樣要驗證碼（Q9）。
7. 新密碼驗證沿用改密碼的規則（至少 8 碼；`ChangePasswordValidator.MinLength`，前端從 API 讀，不另寫一份；§16 第 6 點）。

### 7.2 不透露帳號狀態

- 申請 API 的回應碼、內容、欄位都相同；處理時間靠「每次都做同樣的查詢與一次雜湊」拉近，寫佇列只多一筆 insert，差距在網路抖動以內（T20）。
- 重設 API 對無效權杖一律回 400 `resetLinkInvalid`，不分「過期」「用過」「被取代」「帳號停用」（使用者只需要知道要重新申請）。
- 稽核：申請寫 `account.passwordResetRequested`（結果 Success＝排進佇列；Denied＋原因代碼 `unknownAccount`、`inactive`、`noVerifiedEmail`、`disabled`、`unavailable`、`throttled`）。帳號不存在時不記輸入（同登入）。`throttled` 不逐筆寫，只加計數（同登入的 O2-T3）。

### 7.3 防濫用（申請端節流，T20）

記憶體、單一實例，同 `LoginThrottle` 的做法，數字寫死（安全檢查，列 §22）：

- 同一帳號 1 小時內最多排 3 封重設信；
- 同一來源 IP 15 分鐘內最多 10 次申請（不論帳號存不存在）；
- 超過時照樣回同一句 202，不寄。

這是擋「有人一直替別人按忘記密碼」與「拿申請 API 掃帳號」，不是佇列節流；使用者裁決的「不節流」指佇列裡已排的信不會被延後或併成摘要。

### 7.4 可不可用

`GET auth/options` 多回 `passwordReset: "available" | "contactAdmin"`：

- `available`：政策開、寄信設定已設好、`Ops:PublicUrl` 有設（或 Demo 站，§13）。
- `contactAdmin`：其餘情況。登入頁「忘記密碼？」照樣顯示，點進去的頁面只寫「這個系統目前無法寄送重設密碼信，請聯絡系統管理員重設密碼」（Q11）。

這個值只透露「系統有沒有設寄信」，不透露任何帳號狀態。

## 8. 雙因素登入 TOTP（au1c）

### 8.1 演算法（T7）

- RFC 6238：HMAC-SHA1、6 位數、30 秒一步、容許前後各一步。Google Authenticator、Microsoft Authenticator 等主流 App 的預設值，換別的組合有 App 不支援（技術限制）。
- 自己寫在 Domain（純函式，約 40 行），用 RFC 6238 附錄 B 的測試向量驗證；不引入套件。
- 金鑰 20 bytes 隨機，Base32 顯示；`otpauth://totp/{issuer}:{userId}?secret=...&issuer={issuer}`，issuer 用 `Ops:PublicUrl` 的主機名稱（同一人有兩套系統時 App 裡分得出來），沒設時用 `Formwright`（T12）。
- 重放：驗證成功記 `TotpLastStep`；同一步或更早的碼一律拒絕（RFC 6238 §5.2）。寫入用條件式更新 `WHERE TotpLastStep IS NULL OR TotpLastStep < ?`，兩個請求同時送同一個碼只有一個成功。

### 8.2 綁定（Q15）

在「帳號安全」頁按「啟用雙因素驗證」，對話框分四步：

1. 輸入目前密碼 → `POST account/two-factor/setup { password }`：產生金鑰存進 `TotpPendingProtected`，回 Base32 金鑰與 `otpauth` URI。前端畫 QR 碼（T11），同時顯示可複製的金鑰給不能掃碼的人。
2. 用 App 掃碼。
3. 輸入 App 顯示的 6 位數 → `POST account/two-factor/enable { code }`：對綁定中的金鑰驗證通過才轉正（`TotpSecretProtected`、`TotpEnabledUtc`、`TotpLastStep`），清掉綁定中欄位；產生 10 組復原碼（§8.4）；換 `SecurityStamp` 並重發本 session cookie（其他裝置要重新登入，T9）；稽核 `account.twoFactorEnable`；不寄通知信（啟用不是風險，Q5）。
4. 顯示復原碼（只顯示這一次），可複製、可下載成文字檔；勾「我已保存復原碼」才能按完成。

綁定中超過 10 分鐘沒完成就作廢，要重新開始。PostgreSQL 沒設 `DataProtection:KeysDirectory` 時第 1 步回 409 `keysNotPersisted`：「伺服器沒有保存加密金鑰，重啟後雙因素會失效，請管理員先完成部署設定」（T8；這是 fail closed，不讓使用者綁一把重啟就解不開的金鑰）。

### 8.3 登入第二步（T9）

1. `POST auth/login` 密碼正確、帳號已啟用雙因素 → **不發 session cookie**，改發一個 5 分鐘的「第二步憑證」cookie（Data Protection 加密：帳號、`SecurityStamp`、發出時間、隨機編號；HttpOnly、SameSite=Strict、Path 限 `/api/v3/dynamic/auth/two-factor`），回 200 `{ twoFactorRequired: true }`。
2. 前端在同一個登入頁換成第二步：6 位數輸入框（`inputmode="numeric"`、`autocomplete="one-time-code"`）、「改用復原碼」、「回到登入」。
3. `POST auth/two-factor { code }` 或 `{ recoveryCode }`：憑證有效、stamp 沒變、碼正確 → 發 session cookie、清憑證 cookie；稽核 `login.succeeded`，摘要 `method` 為 `password+totp` 或 `password+recovery`。
4. 錯誤：同一張憑證最多 5 次（技術限制，記憶體依隨機編號計），用完要重新輸入密碼；每次錯誤都計入 `LoginThrottle` 的帳號＋IP 層（第二步猜碼和猜密碼同一個額度），稽核 `login.failed`、原因 `twoFactorWrong`／`recoveryCodeWrong`／`twoFactorExpired`。既有的 `security.loginFailures` 健康檢查因此也會算到猜碼。
5. 回應訊息一律「驗證碼錯誤或已過期」，不分哪一種。
6. 帳號同時要改密碼（`MustChangePassword`）：先過第二步，進來後再照既有流程改密碼。
7. Demo 一鍵登入（`POST auth/demo-login`）：帳號已啟用雙因素時同樣走第二步（Demo 的雙因素示範帳號，§13）；其餘示範帳號沒有雙因素，照舊一鍵進入。

### 8.4 復原碼（Q16、T10）

- 10 組，每組 16 個 Base32 字元，顯示成 `ABCD-EFGH-JKLM-NPQR`（輸入時忽略大小寫、空白與連字號）；存 SHA-256。
- 每組只能用一次（條件式更新）。用復原碼登入成功後，畫面提示「已使用 1 組復原碼，還剩 N 組」。剩 2 組以下時，登入後與「帳號安全」頁顯示「復原碼快用完了，請重新產生」。
- 重新產生：`POST account/two-factor/recovery-codes { password, code }`，舊的全部作廢，新的只顯示一次。稽核 `account.recoveryCodesRegenerate`。

### 8.5 本人關閉（Q15）

- 政策是「選用」、或「只強制系統管理員」而本人不是系統管理員時，「帳號安全」頁有「關閉雙因素驗證」：要目前密碼＋驗證碼（或復原碼），`POST account/two-factor/disable`。
- 清金鑰、刪復原碼、換 stamp（其他裝置登出）；寄「雙因素驗證已關閉」通知（Q5）；稽核 `account.twoFactorDisable`。
- 政策強制時不提供關閉（API 回 409 `twoFactorRequired`）；要換手機就「重新綁定」＝先綁新的再自動取代舊的（同 §8.2，第 1 步多要一組目前驗證碼或復原碼）。

### 8.6 管理員重設（手機遺失；Q10）

- 使用者管理每列多「重設雙因素」（只在對方已啟用時出現），確認對話框寫明「會清除這位使用者的驗證器與復原碼，並登出他所有的裝置」。`POST admin/users/{id}/reset-two-factor`（`ISystemAdminRequest`）。
- 清金鑰、刪復原碼、換 stamp；**不**撤銷 AI 權杖（Q10）；寄「雙因素驗證已被管理員重設」通知（`twoFactorDisabled` 種類，參數標「管理員」；Q5）；稽核 `user.resetTwoFactor`（一般寫入，同 `user.resetPassword`，T16）。
- 重設後政策若強制，對方下次登入照 §8.7 重新綁定。
- 唯一的系統管理員自己手機遺失又沒有復原碼：用啟動救援 `Auth:ResetAdminPassword`，它一併清除 `admin` 的雙因素（T15），手冊寫明。

### 8.7 強制政策（Q6、Q7）

政策 `auth.twoFactorMode`（全系統）：

| 值 | 意思 |
|---|---|
| `optional`（出廠） | 每個人自己決定要不要啟用 |
| `admins` | 系統管理員一定要啟用，其他人自己決定 |
| `all` | 每個人都要啟用 |

- 「應啟用而沒啟用」的帳號，登入（密碼正確）後發一般 session，但 `SessionMiddleware` 只放行 `/auth/*`、`GET /me`、`account/two-factor/setup`、`account/two-factor/enable`，其餘回 403 `twoFactorEnrollmentRequired`（同強制改密碼的做法）。前端導到強制綁定頁 `/account/two-factor-setup`（內容同 §8.2，沒有「取消」）。
- 已經登入著的人：政策改成強制後，下一個請求就被導去綁定（系統政策快取 60 秒，最多晚一分鐘；Q7）。
- 同時要改密碼：先改密碼，再綁定。
- `GET /me` 多回 `twoFactor: { enabled, required, recoveryCodesLeft }` 與 `twoFactorEnrollmentRequired`。
- 政策頁改這項時，若操作者自己就會因此被要求綁定而還沒綁，存檔前提示「存檔後你也必須立刻啟用雙因素驗證」。
- 政策讀不到時（資料庫抖動、快取失敗）沿用最後一次讀到的值，啟動後從沒讀到過就用出廠值 `optional`（T13）。這只影響「要不要逼人去綁」；已啟用的人登入一定要過第二步，不受政策讀值影響。
- Demo 站：示範帳號不能綁定（§13），強制政策對示範帳號不生效，否則 Demo 一改政策就沒人登得進去。

### 8.8 AI 連線權杖（T14）

- 個人存取權杖本來就要登入後才能建立（建立時已過第二步）。
- `AccountUsable` 多一條：「依政策應啟用雙因素卻還沒啟用」的帳號，權杖暫時不能用（回既有的帳號不可用錯誤），綁定後自動恢復；不撤銷權杖。理由：強制政策的目的是每個能進系統的身分都有第二因素，權杖不該是繞過去的路。

## 9. 政策項（單一來源）

全部登記在 `PolicyCatalog`，新分類 `auth`（畫面名稱「帳號安全」），只限全系統；前端從政策 API 取得，`shared/policy-catalog.json` 補文案代碼。

| 代碼 | 型別 | 出廠 | 技術上下限 | 用在 |
|---|---|---|---|---|
| `auth.passwordResetEnabled` | Boolean | true | — | 忘記密碼可不可用（§7.4） |
| `auth.passwordResetLinkMinutes` | Integer（分鐘） | 30 | 10–1440 | 重設連結時效；也是重設信最晚寄出時間（§6.3） |
| `auth.emailVerifyLinkHours` | Integer（小時） | 24 | 1–168 | Email 驗證連結時效 |
| `auth.twoFactorMode` | Choice | `optional` | `optional`、`admins`、`all` | §8.7 |
| `mail.outboxRetentionDays` | Integer（天） | 30 | 1–365 | 寄送紀錄與過期權杖的保留（§6.6） |

- 連結時效在 Application 讀政策生效值寫進 `ExpiresUtc`；前端提示文字裡的「{minutes} 分鐘」由申請 API 的回應帶回，不另寫一份。
- 密碼最短 8 碼：沿用既有常數，前端改密碼、重設頁、管理員重設三處都讀同一個 API 值（au1b 順手把前端 `MIN_PASSWORD` 改成讀 `GET auth/options` 回的 `passwordMinLength`；§16 第 6 點）。
- 政策調嚴不溯及既有：縮短連結時效只影響之後寄出的連結；`twoFactorMode` 改成強制是「要求動作」，不是上限，依 §8.7 立即生效（Q7）。

## 10. API

全部在 `/api/v3/dynamic` 底下。

| 方法 | 路徑 | 身分 | 用途 |
|---|---|---|---|
| GET | `auth/options` | 匿名 | 多回 `passwordReset`、`passwordMinLength` |
| POST | `auth/forgot-password` | 匿名 | 申請重設信；一律 202 |
| POST | `auth/reset-password/check` | 匿名 | 檢查權杖有效（不消耗） |
| POST | `auth/reset-password` | 匿名 | 用權杖設新密碼 |
| POST | `auth/verify-email` | 匿名 | 驗證 Email |
| POST | `auth/login` | 匿名 | 既有；有雙因素時回 `{ twoFactorRequired: true }`、發第二步憑證 cookie |
| POST | `auth/two-factor` | 第二步憑證 | 驗證碼或復原碼，成功發 session |
| GET | `account/security` | 本人 | Email、驗證狀態、雙因素狀態、復原碼剩餘、政策模式 |
| PUT | `account/email` | 本人 | `{ email, password }` |
| POST | `account/email/resend` | 本人 | 重寄驗證信 |
| POST | `account/two-factor/setup` | 本人 | `{ password, code? }`（重新綁定時要 code） |
| POST | `account/two-factor/enable` | 本人 | `{ code }`，回復原碼 |
| POST | `account/two-factor/disable` | 本人 | `{ password, code \| recoveryCode }` |
| POST | `account/two-factor/recovery-codes` | 本人 | `{ password, code }`，回新復原碼 |
| PUT | `admin/users/{id}/email` | 系統管理員 | `{ email }`（null＝清除） |
| POST | `admin/users/{id}/email/resend` | 系統管理員 | 重寄驗證信 |
| POST | `admin/users/{id}/reset-two-factor` | 系統管理員 | 重設雙因素 |
| GET | `admin/users` | 系統管理員 | 既有；每列多 `email`、`emailVerified`、`twoFactorEnabled` |
| GET | `system/mail-outbox` | 系統管理員 | 帳號信寄送紀錄（§6.6） |

- 匿名白名單（`SessionMiddleware.IsAnonymous`）加上述五個 `auth/*` 匿名端點與 `auth/two-factor`；強制綁定放行清單見 §8.7。
- 「本人」端點在 Demo 站對示範帳號一律 403 `demoSite`（同外觀、AI 權杖的做法）。
- 回應不帶 TOTP 金鑰明文，只有 `setup` 那一次回給本人。

## 11. 權限

| 動作 | 誰 | 檢查在哪 |
|---|---|---|
| 申請重設、用連結重設、驗證 Email | 匿名（持權杖） | handler：權杖雜湊比對、用途、時效、帳號啟用 |
| 第二步登入 | 持第二步憑證 | handler：憑證解得開、未過期、stamp 一致 |
| 改自己的 Email、綁定與關閉雙因素、復原碼 | 本人，且要重新輸入目前密碼（關閉與重新產生另要驗證碼） | handler；重新輸入密碼不靠 session 新舊 |
| 設別人的 Email、重設別人的雙因素 | 系統管理員（`ISystemAdminRequest`） | `SystemAdminBehavior` |
| 看帳號信寄送紀錄 | 系統管理員 | 同上 |
| 改 `auth.*`、`mail.*` 政策 | 系統管理員 | 既有政策 API |

- 驗證都在後端；前端的檢查只是提早提示。
- 讀不到使用者資料、解不開憑證、解不開 TOTP 金鑰，一律當失敗（fail closed）。解不開 TOTP 金鑰時只能用復原碼或找管理員重設（§19 風險 1）。

## 12. 畫面（四種寬度）

所有新頁面在 390、820、1366、1920 驗過，不橫向捲動；清單在 390 改卡片（前端畫面通則）。

| 畫面 | 內容 | 窄螢幕（390） |
|---|---|---|
| 登入頁 | 密碼欄下方「忘記密碼？」連結；第二步在同一張卡片換成驗證碼輸入、「改用復原碼」、「回到登入」 | 卡片滿版、按鈕整列寬 |
| `/forgot-password` | 帳號輸入、送出、統一結果訊息；`contactAdmin` 時只顯示請聯絡管理員 | 同上 |
| `/reset-password` | 新密碼、再輸入一次、送出；失效時顯示失效說明與「重新申請」 | 同上 |
| `/verify-email` | 結果：「Email 已驗證」或「連結已失效」，附回到系統／登入的按鈕 | 同上 |
| `/account/security`（Q14） | 三張卡：Email（地址、已驗證／未驗證、變更、重寄驗證信）、密碼（「變更密碼」連到既有頁）、雙因素（狀態、啟用時間、復原碼剩餘、啟用／重新綁定／重新產生復原碼／關閉） | 三張卡上下排；綁定對話框滿版、QR 碼 200px 置中、金鑰可換行 |
| `/account/two-factor-setup` | 強制綁定頁：說明「系統管理員要求啟用雙因素驗證」＋綁定四步；只有登出能離開 | 同上 |
| 頁首 | 「變更密碼」按鈕換成「帳號安全」（窄螢幕只顯示圖示＋tooltip，同現在的做法） | 不新增按鈕數 |
| 使用者管理 | 清單多「Email」（含未驗證標記）與「雙因素」兩欄；每列動作多「設定 Email」「重設雙因素」 | 卡片下方動作鈕 |
| 寄信設定 | 多「帳號信寄送紀錄」區塊；沒設 `Ops:PublicUrl` 時頂端警示 | 紀錄改卡片 |
| 政策頁 | 新分類「帳號安全」五項（政策頁是通用畫面，不另寫頁） | 既有 |

- 新增介面文字放 `shared/i18n/{zh-TW,en-US,th,vi}/ui.json`，四語鍵集合一致；QR 碼與復原碼區塊加 `aria-label`。
- 驗證碼輸入框只收數字；復原碼輸入框不限格式（伺服器端忽略大小寫與連字號）。

## 13. Demo 種子與示範路徑

### 13.1 種子（T22）

- 13 個示範帳號補虛構 Email `<帳號>@example.com`（RFC 2606 保留網域，不會寄到真人），預設已驗證；`floor02` 留未驗證，示範「沒有已驗證 Email 收不到重設信」。
- 新增示範帳號 `mfa01`「雙因素示範」：不加入任何群組（避免改動既有群組成員數與提及候選；登入後首頁是「目前沒有可使用的模組」），已啟用雙因素，金鑰固定（寫在種子與 `docs/demo.md`），10 組復原碼中第 1 組標已使用。改種子前照後端規則先 grep 既有對帳號數的斷言。
- 只在 Demo 產生器建立，不放進 `DemoPeople.EnsureAsync`（測試基底不受影響）。

### 13.2 Demo 站的寄信：Demo 收件匣（Q12）

Demo 站不能設 SMTP（寄信設定唯讀），所以開 Demo 登入時帳號信改由 `DemoMailSink` 處理：不連任何外部主機，照常組信、把內文寫進 `PreviewBody`、記 `sent`。寄信設定頁的「帳號信寄送紀錄」在 Demo 站多「看信」按鈕，顯示整封信（含連結）。正式環境永遠不寫 `PreviewBody`，sink 也不會被註冊（依 `Demo:LoginEnabled` 在啟動時二選一）。

訪客在 Demo 收件匣看得到任何示範帳號的重設連結，等於能替示範帳號設密碼；示範帳號本來就能一鍵登入，沒有多給權限，Demo 資料也定期還原。

### 13.3 Demo 驗證碼提示（Q13）

`mfa01` 走到第二步時，Demo 站在驗證碼框下方顯示「Demo 站示範用：目前的驗證碼是 123456」（伺服器依固定金鑰算出，只在 `Demo:LoginEnabled` 且帳號是示範帳號時回傳）。訪客也可以用自己的驗證器 App 掃 `docs/demo.md` 的金鑰。

### 13.4 示範路徑（AGENTS.md「Demo 資料」）

| 路徑 | 怎麼走 | 看到什麼 |
|---|---|---|
| 成功：忘記密碼 | 登出 → 忘記密碼 → 輸入 `sales01` → 以 `admin` 開寄信設定的寄送紀錄 → 看信 → 開連結設新密碼 → 用新密碼登入 | 統一訊息；紀錄一筆「重設密碼 已寄出」；新密碼登入成功；紀錄多一筆「密碼已變更」通知 |
| 失敗：連結用過 | 再開一次同一個連結 | 「連結已失效或已使用過」 |
| 失敗：沒有已驗證 Email | 對 `floor02` 申請 | 同一句統一訊息；寄送紀錄沒有新信；稽核頁看到 `noVerifiedEmail` |
| 失敗：帳號不存在 | 輸入 `nobody` | 同一句統一訊息 |
| 成功：雙因素登入 | 點 `mfa01` → 輸入提示的驗證碼 | 進入首頁 |
| 失敗：驗證碼錯 | 輸入 `000000` | 「驗證碼錯誤或已過期」；稽核 `login.failed`、`twoFactorWrong` |
| 成功：復原碼 | 改用復原碼，輸入 `docs/demo.md` 的第 2 組 | 進入首頁，提示還剩 8 組 |
| 失敗：復原碼用過 | 輸入第 1 組 | 「驗證碼錯誤或已過期」 |
| 被擋：Demo 不能綁定 | 以 `sales01` 開帳號安全 → 啟用雙因素 | 按鈕停用並寫「Demo 站不能啟用雙因素驗證」 |
| 個人化：Email 狀態 | 以 `floor02` 開帳號安全 | Email 顯示「未驗證」 |

強制綁定（§8.7）與管理員重設（§8.6）在 Demo 站不示範（示範帳號不能綁定、`mfa01` 被重設會讓當天的示範壞掉，所以 Demo 站重設雙因素回 403），以自動測試與本機走查驗收，交付說明寫明。

## 14. 開關組合下誰負責

| 情況 | 忘記密碼 | 雙因素 | 誰處理 |
|---|---|---|---|
| 寄信設定與 `Ops:PublicUrl` 都設好 | 可用 | 照政策 | — |
| 寄信設定沒設好 | `contactAdmin`；Email 照存、驗證信 `skipped` | 照常（雙因素不需要寄信） | 系統管理員設寄信 |
| 沒設 `Ops:PublicUrl` | `contactAdmin`；寄信設定頁警示 | 照常；issuer 用 `Formwright` | 部署者（地端客戶 IT 或雲端廠商） |
| SMTP 設了但寄不出去 | 申請照收、信重試 5 次後 `failed`，開 `channel.accountMail` 事件 | 照常 | 系統管理員（站內事件） |
| 政策關掉忘記密碼 | `contactAdmin` | 照常 | — |
| 使用者沒有已驗證 Email | 收不到信（畫面不透露） | 照常 | 本人或管理員補 Email 並驗證 |
| PostgreSQL 沒設金鑰目錄 | 照常 | 不能新綁定（409）；已綁定的重啟後解不開 → 只能用復原碼或管理員重設 | 部署者 |
| 資料庫唯讀或連不上 | 申請回 503 | 登入本來就不能用（既有） | 同維運設計 §17.2.11 |
| Demo 站 | Demo 收件匣 | 只有 `mfa01` 有雙因素；示範帳號不能綁定；強制政策對示範帳號不生效 | — |
| 雲端版 | 由廠商在客戶的寄信設定填郵件服務；Render 是否擋 SMTP 埠待查證（T18） | 照政策 | 廠商（O3 定郵件服務） |
| 沒開任何應用程式 | 照常 | 照常 | — |

## 15. ERP 對照

以下依主控對主流產品的了解整理，未另行上網查證。

- **Odoo**：登入頁「重設密碼」寄有時效的連結（`auth_signup`），信件進核心郵件佇列（`mail.mail`）由排程批次寄出；`auth_totp` 讓使用者在偏好設定的「帳號安全」啟用驗證器 App，管理員可以替使用者關閉；另一個模組可強制「員工」或「全部使用者」啟用。Formwright 的 `optional`／`admins`／`all` 與「帳號安全」頁對應這一套；寄信改為即時喚醒，因為重設信使用者在等。
- **ERPNext**：系統設定開雙因素，依角色決定誰要用；第二因素可選 App、簡訊、Email。Formwright 只做 App（簡訊要電信商帳號，Email 當第二因素在信箱被盜時等於沒有）。
- **主檔**：帳號 Email 屬於核心的系統使用者（ERP 資料模型 §2.6：系統使用者是登入帳號，HRM 員工是人事資料、以 `user` 欄位連到系統使用者）。HRM 員工主檔之後的聯絡 Email 是人事資料，與帳號 Email 分開，不互相同步；帳號 Email 不出現在使用者欄位的顯示、提及、MCP 工具或任何業務畫面。
- **單據與數量契約**：au1 不新增模組、主檔或單據，ERP 資料模型 §7 檢核 1–7 不適用。核心寄信佇列是平台能力，之後的簽核、任務 Email 通知（第二版）用新種類接上，不替特定應用程式開後門（檢核 9）。

## 16. 與既有設計或程式的矛盾

1. **使用者與群組設計 §3 非目標、§4「忘記密碼由管理員重設；系統不寄信，因為每家客戶不一定有郵件伺服器」**：使用者 2026-11-17 裁決推翻。新做法保留原理由：沒設寄信的系統照舊由管理員重設（`contactAdmin`）。實作時回填該文件，註明 au1 起的行為。
2. **維運設計 O2-U1「`AppUser` 沒有 Email 欄位」、§17.1 範圍外「使用者帳號的 Email 欄位不排」**：au1 加欄，但只用於帳號信；告警收件人仍是通道設定的一組信箱，不改成寄給使用者。O2-U1 附註當時的顧慮「等於開了業務 email 的入口」：au1 的 Email 不給業務信用，業務信第二版要做時另行決定要不要沿用這個欄位。實作時回填維運設計 §17.1 與 O2-U1。
3. **維運設計 §17.2.6「之後的帳號信（重設密碼等）與告警信共用這一份連線」**：照做。`IAlertMailSender` 改名 `IMailSender` 搬到 `MailService`（T6），告警的呼叫端跟著改名，行為不變。
4. **維運設計 §14 U1「Render 免費方案擋 SMTP 埠（未查證）」**：研發期 Demo 站用 Demo 收件匣，不受影響；正式雲端版的郵件服務選擇（SMTP 非標準埠或 HTTP 郵件 API）與查證併入 O3（T18）。au1 不下結論。
5. **手冊**：第 41 行「系統不寄信，忘記密碼由管理員重設」、第 680–681 行「沒有寄信重設密碼」「也沒有雙因素驗證」要改；第 28 行救援段補「一併清除 admin 的雙因素」；使用者管理（§11）、寄信設定（第 1783 行起）、政策（新分類）、登入段補新行為。
6. **密碼最短長度**：`ChangePasswordValidator.MinLength = 8`、`AdminBootstrap` 的 `MinPasswordLength`、前端 `MIN_PASSWORD` 各一份（既有重複）。au1b 的重設頁會是第四個用到的地方，所以 au1b 收斂：後端統一引用 `ChangePasswordValidator.MinLength`，前端從 `auth/options` 讀。要不要把最短長度做成政策不在 au1 範圍（它是安全下限，不是業務政策），另開 backlog 票討論。
7. **`LoginHandler` 註解「失敗次數改由 LoginThrottle 依來源計算」**：第二步猜碼計入同一個額度（§8.3），註解跟著補。
8. **功能清單與路線**：`docs/feature.md` 第 56、57 列、第 379 列，roadmap au1 列由主控更新；au1a 完成時「核心寄信能力」那半句可先打勾。
9. **MCP 設計 §13 帳號可用條件**：多一條「應啟用雙因素卻未啟用」（T14），實作時回填 MCP 設計。

## 17. 切段

依序 au1a → au1b → au1c；au1b、au1c 都只依賴 au1a，但共用 `SessionMiddleware.cs`、`AuthController.cs`、`LoginPage.vue`、`auth-guard.ts`、`ui.json`，不並行。

| 段 | 內容 | migration | 共用檔 | 依賴 |
|---|---|---|---|---|
| au1a 核心寄信佇列與帳號 Email | 全部表與欄（§5，含 au1b、au1c 要用的）；`IMailSender` 改名搬家；`MailDeliveryService`（喚醒、領取、重試、過期、失敗事件 `channel.accountMail`）；`AccountMailText` 四語；Email 設定（管理員、本人）、驗證信、`/verify-email`、重寄；「帳號安全」頁（先只有 Email 與密碼兩張卡）、頁首入口換成「帳號安全」；寄送紀錄 API 與畫面；`mail.outboxRetentionDays` 與 `auth.emailVerifyLinkHours`；清理接 `ops.upkeep`；Demo 收件匣、示範帳號 Email；手冊 | **有**（`AccountSecurity`，SQLite＋PostgreSQL 各一） | `AppUser.cs`、`AppUserConfiguration.cs`、`DynamicDbContext`（新設定類）、兩份 model snapshot、`Application/DependencyInjection.cs`、`Program.cs`、`SessionMiddleware.cs`（匿名白名單）、`PolicyCatalog.cs`、`shared/policy-catalog.json`、`AuditActions.cs`、`MessageText.cs`、`ui.json`×4、`routes.ts`、`MainLayout.vue`、`UsersPage.vue`、`MailSettingsPage.vue`、`DemoPeople.cs`／Demo 產生器、`AlertDelivery.cs`（改名） | 無 |
| au1b 寄信重設密碼 | 申請、檢查、重設 API；節流；統一回應與稽核；`passwordChanged` 通知（重設、本人改、管理員重設三處都寫）；`emailChanged` 通知；登入頁連結、`/forgot-password`、`/reset-password`；`auth/options` 的 `passwordReset`、`passwordMinLength`；三個政策中的 `auth.passwordResetEnabled`、`auth.passwordResetLinkMinutes`；密碼長度收斂（§16 第 6 點）；Demo 路徑；手冊 | 無 | `SessionMiddleware.cs`、`AuthController.cs`、`GetAuthOptionsHandler.cs`、`AccountResponses.cs`、`ChangePasswordHandler.cs`、`ResetPasswordHandler.cs`、`LoginPage.vue`、`ui.json`×4、`routes.ts`、`PolicyCatalog.cs`、`AuditActions.cs` | au1a |
| au1c 雙因素登入 | Domain TOTP 與測試向量；綁定、啟用、關閉、重新綁定、復原碼；登入第二步與憑證 cookie；Demo 一鍵登入的第二步；強制政策 `auth.twoFactorMode` 與 middleware 封鎖、強制綁定頁；管理員重設；救援清除；`AccountUsable`；`keysNotPersisted`；QR 碼（新前端依賴）；`mfa01` 種子與 Demo 驗證碼提示；手冊與 `docs/demo.md` | 無 | `SessionMiddleware.cs`、`AuthController.cs`、`LoginHandler.cs`、`DemoLoginHandler.cs`、`AuthCookies.cs`、`AdminBootstrap.cs`、`GetMeHandler.cs`、`McpTokenHandlers.cs`、`LoginPage.vue`、`auth-guard.ts`、`ui.json`×4、`routes.ts`、`UsersPage.vue`、`PolicyCatalog.cs`、`AuditActions.cs`、`frontend/package.json`、`DemoPeople.cs`／Demo 產生器 | au1a |

- au1a 是本段唯一的 migration，不能和其他會加 migration 的段平行（後端規則）。
- 三段都有畫面，各自要在本機四種寬度走查。
- 每段合併後 Demo 不壞：au1a 只多 Email 與收件匣；au1b 多忘記密碼；au1c 多 `mfa01`。

## 18. 可驗收案例

### 18.1 寄信佇列與 Email（au1a）

| # | Given | When | Then |
|---|---|---|---|
| M1 | 寄信設定已設好、`Ops:PublicUrl` 有設；SMTP 換成假寄件器 | 管理員把 `sales01` 的 Email 設成 `a@example.com` | 佇列一列 `emailVerify`、系統預設語系；喚醒後 5 秒內記 `sent`；信裡有 `/verify-email#t=` 連結；`Dynamic_AccountToken` 只存雜湊 |
| M2 | M1 的信 | 開連結 | `EmailVerifiedUtc` 有值；同一連結再開一次回「連結已失效」 |
| M3 | M1 寄出後 | 管理員再把 Email 改成 `b@example.com`，然後開 M1 的舊連結 | 舊連結失效（`emailChanged`）；`b@` 收到新驗證信；Email 狀態未驗證 |
| M4 | 假寄件器第 1、2 次回連線失敗 | 寫一封驗證信 | 1 分鐘、5 分鐘後重試；第 3 次成功記 `sent`、`Attempts` 3；每次重試的連結不同，只有最後一封有效 |
| M5 | 假寄件器一直失敗 | 寫一封驗證信、時間快轉 | 第 5 次後 `failed`；開事件 `channel.accountMail`（站內）；沒有寄告警信；之後一封成功事件結案 |
| M6 | 寄信設定沒設好 | 設 Email | Email 存好、佇列列 `skipped`／`notConfigured`；帳號安全頁顯示「未驗證（系統目前寄不出驗證信）」 |
| M7 | 一列卡在 `sending`、領取期限已過 | 寄送服務掃描 | 被重領、權杖換新；前一把權杖 `superseded` |
| M8 | 本人在帳號安全頁改 Email、密碼打錯 | 送出 | 400 `oldPasswordWrong`；Email 不變 |
| M9 | 寄送紀錄 60 筆 | 管理員開寄信設定 | 第一頁 50 筆、新到舊；沒有內文欄；非管理員打 API 403 |
| M10 | Demo 站 | 申請 `sales01` 重設 | 沒有連外；列 `sent`、`PreviewBody` 有整封信；正式環境同情境 `PreviewBody` 為 null |
| M11 | 建立超過 30 天的 `sent` 列與過期權杖 | `ops.upkeep` 跑一輪 | 兩者都被清掉；未滿 30 天的留著 |

### 18.2 寄信重設密碼（au1b）

| # | Given | When | Then |
|---|---|---|---|
| R1 | `sales01` 有已驗證 Email | 申請重設 | 202 統一訊息；一封 `passwordReset`；連結 30 分鐘有效 |
| R2 | 帳號不存在／停用／沒有 Email／Email 未驗證／政策關閉 | 各申請一次 | 五次的狀態碼與內容完全相同；都沒有佇列列；稽核原因代碼各自正確；帳號不存在那筆不記輸入 |
| R3 | R1 的連結 | 設新密碼 `NewPass123` | 200；舊 session 下一個請求 401；AI 權杖全部撤銷；權杖 `used`；一封 `passwordChanged` 通知；不自動登入 |
| R4 | R3 之後 | 再用同一連結 | `check` 與重設都回 `resetLinkInvalid` |
| R5 | 申請兩次 | 用第一封的連結 | 失效（`superseded`）；第二封可用 |
| R6 | 連結時效 30 分鐘 | 31 分鐘後使用 | `resetLinkInvalid` |
| R7 | 寄信一直失敗，第一次申請後 30 分鐘才恢復 | 寄送服務重試 | 列記 `expired`，不寄過期才到的信 |
| R8 | 同帳號 1 小時內申請 4 次 | — | 前 3 次各排一封，第 4 次照樣 202、沒有新列 |
| R9 | 同 IP 15 分鐘內 11 次（不同帳號） | — | 第 11 次 202、不寄、不逐筆寫稽核 |
| R10 | `MustChangePassword` 為是的帳號 | 用連結重設 | 旗標清掉、登入後不再被要求改密碼 |
| R11 | 帳號有雙因素 | 用連結重設後登入 | 密碼正確後仍要第二步 |
| R12 | 寄信設定沒設好 | 開登入頁 → 忘記密碼 | `auth/options` 回 `contactAdmin`；頁面寫「請聯絡系統管理員重設密碼」 |
| R13 | 以 `Host: evil.example` 標頭打申請 API | 看信 | 連結的網址是 `Ops:PublicUrl`，與 Host 無關 |
| R14 | 新密碼 7 碼 | 重設 | 400，訊息帶 8；前端提示的數字來自 `auth/options` |

### 18.3 雙因素（au1c）

| # | Given | When | Then |
|---|---|---|---|
| F1 | RFC 6238 附錄 B 的 SHA1 測試向量 | Domain 計算 | 六個時間點的碼全部相符 |
| F2 | 本人在帳號安全頁 | 啟用：密碼 → 掃碼 → 正確驗證碼 | 啟用；回 10 組復原碼；其他裝置下一個請求 401；本 session 照常 |
| F3 | 綁定中 | 輸入錯的驗證碼 | 400；未啟用；綁定中金鑰保留到 10 分鐘 |
| F4 | 已啟用 | 密碼登入 | 200 `twoFactorRequired`；沒有 session cookie；`GET /me` 401 |
| F5 | T4 之後 | 送正確驗證碼 | 發 session；稽核 `login.succeeded`、`password+totp` |
| F6 | T5 用過的同一個碼 | 30 秒內再登入一次並送同一碼 | 拒絕（重放）；兩個並行請求送同一碼只有一個成功 |
| F7 | 第二步 | 連錯 5 次 | 第 6 次即使正確也拒絕，要重新輸入密碼；`login.failed` 5 筆 `twoFactorWrong` |
| F8 | 第二步憑證發出後 6 分鐘 | 送正確碼 | 拒絕 `twoFactorExpired` |
| F9 | 已啟用 | 用復原碼登入 | 成功；剩 9 組；同一組再用拒絕 |
| F10 | 剩 2 組 | 登入 | 首頁與帳號安全頁提示重新產生 |
| F11 | 政策 `all`、`sales01` 沒啟用 | 登入後打 `GET records` | 403 `twoFactorEnrollmentRequired`；前端導到強制綁定頁；`GET /me` 與 setup、enable 可用 |
| F12 | 政策 `admins` | 一般使用者與系統管理員各登入 | 一般使用者照常；管理員被要求綁定 |
| F13 | 政策 `all`、帳號同時 `MustChangePassword` | 登入 | 先改密碼，再綁定 |
| F14 | 政策 `all` | 本人按關閉 | 409 `twoFactorRequired` |
| F15 | 政策 `optional` | 本人以密碼＋驗證碼關閉 | 清除；其他裝置登出；一封 `twoFactorDisabled` 通知 |
| F16 | 已啟用 | 管理員重設 | 清除；該使用者全部 session 401；AI 權杖保留；通知信參數標管理員；稽核 `user.resetTwoFactor` |
| F17 | 政策 `all`、帳號有 AI 權杖但沒啟用雙因素 | 用權杖打 MCP | 回帳號不可用；綁定後同一把權杖恢復 |
| F18 | `admin` 已啟用雙因素 | 設 `Auth:ResetAdminPassword` 重啟 | `admin` 雙因素清除、密碼重設、要求改密碼 |
| F19 | PostgreSQL、沒設金鑰目錄 | 開始綁定 | 409 `keysNotPersisted` |
| F20 | 已啟用，換一套 Data Protection 金鑰 | 密碼登入並送驗證碼 | 拒絕（fail closed）；復原碼可登入；帳號安全頁提示「驗證器設定已無法讀取，請重新綁定」 |
| F21 | Demo 站 | 點 `mfa01` | 進第二步，顯示示範驗證碼；照輸入可登入 |
| F22 | Demo 站 | `sales01` 開帳號安全啟用雙因素；管理員重設 `mfa01` | 前者按鈕停用、API 403 `demoSite`；後者 403 |
| F23 | Demo 站、政策改 `all` | 點 `sales01` | 照常一鍵進入，不被要求綁定 |

### 18.4 畫面

| # | Given | When | Then |
|---|---|---|---|
| U1 | 390、820、1366、1920 四種寬度 | 走登入第二步、忘記密碼、重設、驗證 Email、帳號安全、綁定對話框、強制綁定頁、使用者管理、寄送紀錄 | 都不橫向捲動；390 的清單是卡片；QR 碼完整顯示 |
| U2 | 英文、泰文、越南文介面 | 同上 | 沒有缺鍵；信件主旨與內文依 §6.4 的語系 |

## 19. 測試重點與風險

**測試重點**

- Domain：TOTP 測試向量、時間步邊界（前後一步可、兩步不可）、Base32 編解碼、復原碼正規化。
- Application：統一回應（R2 的五種情況逐欄比對）、權杖生命週期（產生、取代、使用、過期、Email 變更作廢）、寄送時產生權杖的當機重領、第二步次數與重放、強制政策三種值、`AccountUsable`。
- WebAPI：匿名白名單與強制綁定放行清單（逐條列出沒列進去的端點會被擋）、第二步憑證 cookie 的屬性（HttpOnly、SameSite=Strict、Path）、Host 標頭不影響連結。
- PostgreSQL 與 SQLite 都跑：條件式更新（領取、復原碼、`TotpLastStep`）在兩種資料庫都只成功一次。
- 前端：auth-guard 的兩種 403（`passwordChangeRequired`、`twoFactorEnrollmentRequired`）先後順序、登入頁兩步狀態、URL fragment 讀權杖後立刻 `history.replaceState` 清掉。

**風險**

1. **Data Protection 金鑰遺失＝全部 TOTP 金鑰解不開**（最大風險）。PostgreSQL 沒設金鑰目錄時重啟就會發生。緩解：沒設金鑰目錄就不准新綁定（§8.2）；復原碼存雜湊、不受金鑰影響；管理員重設；救援設定清 `admin`；手冊在部署段把金鑰目錄從「建議」改成「啟用雙因素前必設」。仍有殘餘風險：已綁定之後才把金鑰目錄拿掉。
2. **雲端版重設信寄不出去**：Render 免費方案是否擋 SMTP 埠未查證（T18）。若擋，雲端版在 O3 定郵件服務前忘記密碼只能 `contactAdmin`。
3. **單一實例假設**：節流與第二步次數在記憶體；多實例時每台各算一份，可被放大 N 倍。和既有 `LoginThrottle` 同一個已知限制，多實例部署前一起改成資料庫計數。
4. **信箱被盜**：重設信不繞過雙因素（Q9），沒啟用雙因素的帳號則信箱即帳號。這是寄信重設的固有取捨，手冊建議系統管理員把政策設成至少 `admins`。

## 20. 待確認清單

### 20.1 行為類（主控暫定，待使用者確認）

- **Q1 忘記密碼輸入「帳號」（推薦）。**
  例子：業務小陳忘記密碼，在申請頁輸入 `sales01`，信寄到他帳號上已驗證的 Email。
  - 推薦：只收帳號。帳號是登入用的識別，不會重複；Email 可以多人共用（現場人員共用一個組信箱），用 Email 申請就要處理「一個地址對到多個帳號」。
  - 替代：帳號或 Email 都收。代價：Email 對到多個帳號時要每個帳號各寄一封，信裡要寫清楚是哪個帳號。
  - 選錯的代價：小，只改申請 API 與一個輸入框。
- **Q2 Email 要點驗證信才會收到重設信（推薦）。**
  例子：管理員建帳號時把 `chen@example.com` 打成 `chen@exmaple.com`。沒有驗證：之後重設信寄到陌生人的信箱，對方點連結就能設密碼進系統。有驗證：打錯的地址永遠不會被驗證，重設信不會寄出，管理員在清單看到「未驗證」。
  - 推薦：要驗證。代價：多一封驗證信與一個驗證頁；寄信沒設好時 Email 永遠是未驗證。
  - 替代：管理員填了就生效。代價：打錯一個字就是帳號被接管的路。
  - 選錯的代價：中。改成不驗證很容易；從不驗證改成要驗證，既有 Email 全部要補驗證。
- **Q3 Email 由管理員設定，本人也可以在「帳號安全」頁改（要輸入目前密碼）（推薦）。**
  - 推薦：兩邊都能改，改了都要重新驗證。雲端版客戶的管理員不一定知道每個人的 Email；本人改要密碼，防止有人借用沒登出的電腦改掉 Email 再申請重設。
  - 替代：只有管理員能設。代價：每次換信箱都要找管理員。
  - 選錯的代價：小，只差一個 API 與一張卡。
- **Q4 用重設信改完密碼：不自動登入、登出所有裝置、撤銷全部 AI 連線權杖（推薦）。**
  背景：使用者忘記密碼是一種情況，帳號被盜、本人在搶回帳號是另一種。系統分不出來，照比較嚴的那種處理。
  - 推薦：三項都做，同管理員重設密碼的現行做法。代價：本人要重新登入（還要過雙因素）、重新建立 AI 連線權杖。
  - 替代：保留 AI 權杖（同本人改密碼的現行做法）。代價：帳號被盜時，攻擊者建的權杖在改密碼後仍然有效。
  - 選錯的代價：小，一行程式。
- **Q5 寄安全通知信：密碼被變更（任何方式）、雙因素被關閉或被管理員重設、Email 被改（寄到舊地址）（推薦）。**
  例子：有人用小陳沒登出的電腦把 Email 改成自己的，小陳的舊信箱收到「你的帳號 Email 已變更，不是你本人操作請聯絡系統管理員」。
  - 推薦：這三種都寄；啟用雙因素、重新產生復原碼不寄（不是風險）。只寄到已驗證的地址。代價：多三種信的四語文案。
  - 替代：都不寄。代價：帳號被動手腳時本人不會知道。
  - 選錯的代價：極小，各是一行寫佇列。
- **Q6 雙因素強制範圍三選一：選用／只強制系統管理員／全部強制（推薦）。**
  背景：使用者裁決「系統管理員可設強制或選用」。系統管理員帳號被盜的損失最大，很多公司先只強制管理員。
  - 推薦：三個值，出廠「選用」。代價：政策多一個選項。
  - 替代：兩個值（選用、全部強制）。代價：想先保護管理員的客戶只能全部強制。
  - 替代 2：依群組指定。代價：middleware 每個請求要算群組，強制範圍不好一眼看懂；第二版再看需求。
  - 「只強制系統管理員」不含持有系統權限（例如權限設定）的一般使用者；要含的話 middleware 要多算系統權限，第二版再議。
  - 選錯的代價：小，加減選項不改表。
- **Q7 改成強制後立即生效：已登入的人下一個請求就被導去綁定（推薦）。**
  - 推薦：立即生效，同強制改密碼的做法。代價：管理員在上班時間改，正在填單的人會被打斷（表單內容留在畫面上，綁定完回來可以繼續的前提是頁面沒重新整理）。
  - 替代：給寬限天數（新政策 `auth.twoFactorGraceDays`），期間只提示不擋。代價：多一個政策、多一套提示與倒數；寬限期間帳號仍沒有保護。
  - 選錯的代價：小，寬限天數之後可以加。
- **Q8 不做「記住這台裝置」（推薦）。**
  背景：現在的 session 關瀏覽器就失效，所以每天第一次開瀏覽器都要輸入驗證碼。
  - 推薦：不做。理由：多一個長效 cookie 就是多一個被偷的東西；小公司一天輸一次 6 位數可以接受。
  - 替代：勾選後 30 天內這台瀏覽器不再問驗證碼。代價：要新表記裝置、帳號安全頁要能撤銷裝置。
  - 選錯的代價：小，之後可以加，不影響既有資料。
- **Q9 重設信不能繞過雙因素；手機遺失又忘記密碼：用復原碼，沒有復原碼就找管理員（推薦）。**
  例子：小陳的手機掉了又忘記密碼。他用重設信設好新密碼，登入時第二步改用復原碼進來，再到帳號安全頁重新綁定新手機。復原碼也沒了，就請管理員重設雙因素。
  - 推薦：照上述。代價：使用者兩樣都丟時一定要找管理員。
  - 替代：重設信順便清除雙因素。代價：信箱被盜＝雙因素失效，雙因素就沒有意義了。
  - 選錯的代價：大（安全性），所以推薦方案不該改。
- **Q10 管理員重設雙因素：清除驗證器與復原碼、登出該使用者所有裝置，但不撤銷 AI 連線權杖（推薦）。**
  背景：重設雙因素通常是本人手機掉了，不代表帳號被盜，權杖是本人建的。
  - 推薦：不撤銷；管理員要撤銷可以到 AI 連線頁另外撤。代價：真的被盜時要記得另外撤。
  - 替代：一併撤銷（同管理員重設密碼）。代價：本人要重建所有 AI 連線。
  - 選錯的代價：極小，一行程式。
- **Q11 系統沒設寄信時，登入頁的「忘記密碼？」照樣顯示，點進去寫「請聯絡系統管理員重設密碼」（推薦）。**
  - 推薦：照樣顯示。理由：使用者忘記密碼時第一個找的就是這個連結，看到該找誰比找不到連結好。
  - 替代：沒設寄信就隱藏連結。代價：使用者不知道怎麼辦。
  - 選錯的代價：極小。
- **Q12 Demo 站用「Demo 收件匣」示範重設密碼的成功路徑（推薦）。**
  背景：AGENTS.md 要求有畫面的功能在 Demo 能示範成功路徑。Demo 站不能設 SMTP，信寄不出去。
  例子：訪客替 `sales01` 申請重設，再以 `admin` 到寄信設定的寄送紀錄按「看信」，看到整封信與連結，點連結就能設新密碼。
  - 推薦：Demo 站的帳號信不連外，改存進寄送紀錄、可以看信。代價：`Dynamic_MailOutbox` 多一個只在 Demo 用的欄位；任何訪客都看得到示範帳號的重設連結（示範帳號本來就能一鍵登入，沒有多給權限）。
  - 替代：Demo 不示範成功路徑，只示範統一訊息與「請聯絡管理員」，成功路徑只在本機用假 SMTP 走查。代價：線上 Demo 看不到這個功能真正的樣子。
  - 選錯的代價：小；欄位留著不用無害。
- **Q13 Demo 的雙因素示範帳號在第二步直接顯示目前的驗證碼（推薦）。**
  例子：訪客點「雙因素示範」帳號，第二步畫面寫「Demo 站示範用：目前的驗證碼是 482913」，照打就能登入；想實際體驗的人也可以用自己的 App 掃 `docs/demo.md` 的金鑰。
  - 推薦：顯示。代價：Demo 專用的一小段程式（只在 Demo 登入開啟時回傳）。
  - 替代：只公布金鑰，訪客要自己裝 App 掃碼。代價：大部分訪客會卡在第二步。
  - 選錯的代價：極小。
- **Q14 頁首的「變更密碼」換成「帳號安全」頁，Email、密碼、雙因素都在這頁（推薦）。**
  - 推薦：一個入口。理由：頁首在手機寬度已經擠了三個按鈕，不再多加；Odoo 也把這些放在同一個「帳號安全」分頁。
  - 替代：頁首另加一個「雙因素」按鈕。代價：頁首變四個按鈕，390 寬度要再收。
  - 選錯的代價：極小。
- **Q15 選用模式下本人可以自己關閉雙因素，要輸入密碼＋驗證碼（推薦）；強制模式下不能關，只能重新綁定。**
  - 推薦：照上述。代價：無。
  - 替代：只有管理員能關。代價：本人想換手機或不想用時都要找管理員。
  - 選錯的代價：極小。
- **Q16 復原碼 10 組，剩 2 組以下時提醒重新產生（推薦）。**
  背景：組數屬於安全慣例（GitHub、Google 都是 8–10 組），主控歸為可寫死、列在 §22 給使用者看過，不做成政策。
  - 推薦：10 組、剩 2 組提醒。
  - 替代：做成政策。代價：多一個沒人會調的設定。
  - 選錯的代價：極小。

### 20.2 技術類（主控已定，可推翻）

- **T1** 一組 migration `AccountSecurity`，全部表與欄在 au1a 建好，au1b、au1c 不改表（§5）。
- **T2** TOTP 欄放 `AppUser`（一人一份，middleware 同一次查詢讀得到）；復原碼另開表以便逐組條件式更新（§5.1、§5.2）。
- **T3** 連結權杖 32 bytes、只存 SHA-256、單次使用、新的作廢舊的；放在 URL `#` 後面，頁面讀到就 `replaceState` 清掉（§5.3、§6.4）。
- **T4** 權杖在寄送時產生，佇列不存秘密；重試換新權杖；重設信最晚寄出時間＝申請時間＋連結時效（§6.3）。
- **T5** 寄送服務是獨立的 hosted service，提交後即時喚醒＋30 秒掃描；領取協定沿用告警佇列；不節流、不摘要；重試沿用 `AlertLimits`；失敗開 `channel.accountMail`（§6.2）。
- **T6** `IAlertMailSender` 改名 `IMailSender` 搬到 `MailService`，告警與帳號信共用；介面留給之後的 HTTP 郵件 API（§6.1）。
- **T7** TOTP 自己寫在 Domain（RFC 6238、SHA1、6 位、30 秒、±1 步），測試向量驗證；`TotpLastStep` 條件式更新擋重放（§8.1）。
- **T8** TOTP 金鑰用 Data Protection 加密（purpose `account-totp`）；PostgreSQL 沒設金鑰目錄時不准新綁定（§8.2）；解不開當驗證失敗。
- **T9** 登入兩段式：密碼正確發 5 分鐘的第二步憑證 cookie（加密、HttpOnly、SameSite=Strict、Path 限定），第二步成功才發 session；同一憑證 5 次、計入 `LoginThrottle`（§8.3）。啟用、關閉、管理員重設都換 `SecurityStamp`。
- **T10** 復原碼 10 組 16 字元 Base32、SHA-256（高熵，不需要 PBKDF2）（§8.4）。
- **T11** QR 碼在前端產生，新增 npm 套件 `qrcode`（MIT）；伺服器不產圖。實作時確認授權與打包大小，只在綁定對話框動態 import。
- **T12** `otpauth` 的 issuer 用 `Ops:PublicUrl` 主機名稱，沒設用 `Formwright`（§8.1）。
- **T13** 強制政策讀 `CachedSystemPolicy`；讀不到用最後一次的值，沒有就用出廠值 `optional`；只影響「要不要逼人綁」，不影響已啟用者的第二步（§8.7）。
- **T14** `AccountUsable` 加「依政策應啟用雙因素卻未啟用」不可用，不撤銷權杖（§8.8）。
- **T15** `Auth:ResetAdminPassword` 一併清除 `admin` 的雙因素與復原碼（§8.6）。
- **T16** 稽核動作：`account.passwordResetRequested`、`account.passwordResetByMail`、`account.setEmail`、`account.emailVerified`、`account.twoFactorEnable`、`account.twoFactorDisable`、`account.recoveryCodesRegenerate`、`user.setEmail`、`user.resetTwoFactor`，全部一般寫入（後端規則：高風險只留權限類；`user.resetPassword` 也是一般寫入）；登入沿用 `login.succeeded`／`login.failed`，摘要加 `method`、原因加 `twoFactorWrong` 等。
- **T17** 管理員設 Email 用獨立端點 `PUT admin/users/{id}/email`，不併進 `PUT admin/users/{id}`：後者是整筆取代、缺欄位不准默默清空，加欄會讓舊呼叫端出錯；也避免 Email 變更被記成高風險的 `user.update`。
- **T18** Render 免費方案是否擋 SMTP 埠（25、465、587）：**未查證**，au1 不下結論。待查證項目：Render 官方文件對外連 SMTP 埠的限制；若擋，候選做法是郵件服務的非標準埠（例 2525）或 HTTP 郵件 API（O3 比較 Resend、Brevo 時一起定）。au1 的 Demo 用 Demo 收件匣，不受影響。
- **T19** 信件連結只用 `Ops:PublicUrl`，不用 Host 標頭；沒設就不提供要帶連結的信（§6.4）。
- **T20** 申請端節流（帳號 1 小時 3 封、IP 15 分鐘 10 次）寫死在記憶體，同 `LoginThrottle`；統一回應與相同處理步驟拉近時間（§7.2、§7.3）。
- **T21** 帳號 Email 不唯一（§5.1；Q1 推薦只用帳號申請，所以不需要唯一）。
- **T22** 示範帳號不收重設信、不能綁定雙因素；Demo 站例外收重設信（進 Demo 收件匣）；`mfa01` 由種子直接寫入雙因素（§13）。

## 21. 宗旨檢查（formwright-purpose-check）

1. **機制還是政策**：通過。寄信佇列、權杖、TOTP 是平台機制；忘記密碼開關、連結時效、驗證連結時效、強制範圍、保留天數是政策（§9），系統管理員在政策頁改。節流、TOTP 參數、復原碼組數、第二步次數是安全檢查或技術限制，列在 §22。
2. **寫死掃描**：設計階段不適用；審查 diff 時照 skill 的 grep 再跑，命中項對照 §22。
3. **單一來源**：通過。連結時效只在 `PolicyCatalog`，前端提示的分鐘數由 API 帶回；密碼最短長度由 au1b 收斂成一份（§16 第 6 點）；重試表沿用 `AlertLimits`。
4. **兩個應用程式**：通過。帳號安全是平台功能，所有應用程式的使用者都用；寄信佇列之後的簽核、任務通知（兩個不相干的功能）都會用。
5. **空系統**：通過。不開任何應用程式時登入、忘記密碼、雙因素都照常；Demo 種子不跑時沒有 `mfa01`，不影響。
6. **套件只用定義組成**：不適用。不動應用程式套件。
7. **ERP 對照**：通過，見 §15；帳號 Email 屬核心系統使用者，與 HRM 員工分開。
8. **權限 fail closed、驗證不只在前端、沒有執行期 DDL**：通過。權杖、憑證、TOTP 金鑰任何一項讀不到或解不開都當失敗；強制綁定由 middleware 擋，不靠前端導頁；schema 走 EF migration。唯一的 fail-open 是「政策讀不到時不逼人綁定」（T13），已啟用的人不受影響，列為已知取捨。
9. **不限產業**：通過。理由是「對外開放的雲端站台要防帳號被盜」與「共用信箱、手機遺失」，服務業、貿易商、批量工廠都成立；Q1 的「共用組信箱」在工廠現場最常見，但只影響申請方式，不假設產業。

## 22. 可寫死清單

| 項目 | 值 | 類別 |
|---|---|---|
| TOTP 演算法、位數、步長、容許步數 | SHA1、6、30 秒、±1 | 技術限制（App 相容） |
| TOTP 金鑰長度 | 20 bytes | 安全檢查（RFC 4226 建議） |
| 綁定中金鑰時效 | 10 分鐘 | 安全檢查 |
| 第二步憑證時效、次數 | 5 分鐘、5 次 | 安全檢查 |
| 復原碼組數、長度 | 10 組、16 字元 | 安全慣例（Q16） |
| 復原碼剩幾組提醒 | 2 | 畫面提示門檻（Q16） |
| 連結權杖長度 | 32 bytes | 安全檢查 |
| 重設申請節流 | 帳號 1 小時 3 封、IP 15 分鐘 10 次 | 安全檢查（同 `LoginThrottle`） |
| 重寄驗證信間隔 | 同帳號 60 秒 | 技術限制 |
| 寄送重試、領取期限、寄送逾時 | 1／5／15／60 分鐘最多 5 次、2 分鐘、30 秒 | 技術限制（沿用 `AlertLimits`） |
| 寄送服務掃描間隔 | 30 秒 | 技術限制 |
| Email 長度、格式 | 255、`MailAddressRules.IsEmail` | 格式守衛（沿用） |
| 密碼最短長度 | 8 | 安全下限（沿用，§16 第 6 點） |
| 重設信不繞過雙因素、不自動登入 | — | 安全檢查（Q4、Q9） |
| 信件連結只用 `Ops:PublicUrl` | — | 安全檢查（T19） |
| 申請回應不透露帳號狀態 | — | 安全檢查 |
| 示範帳號不能綁定雙因素 | — | Demo 完整性守衛 |

## 23. 留給實作者

- `AccountMailText` 五種信的四語主旨與內文；登入第二步、帳號安全頁、強制綁定頁的四語介面文字。
- `qrcode` 套件的版本與授權確認（T11），以及要不要改用更小的替代品。
- `mfa01` 的固定金鑰、復原碼內容與 `docs/demo.md` 新小節。
- au1a 若太大（估約 45 個檔），可把「帳號安全」頁與 Demo 收件匣拆成 au1a2，但 migration 只能留在 au1a1。
- 手冊各段的實際畫面文字以實作為準回填（§16 第 5 點）。
