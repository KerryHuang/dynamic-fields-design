# 部署與環境變數

Formwright 的 Demo 站與容器部署做法。本機開發與測試見 [CONTRIBUTING.md](../CONTRIBUTING.md)；操作畫面見[操作手冊](manual.md)。

## Demo 部署（Render 免費方案）

`poc/Dockerfile` 把前端建置結果和後端打包成一個容器，API 與畫面在同一個網址；`render.yaml` 是 Render 的 Blueprint。

1. 在 [Render](https://render.com) 用 GitHub 登入，New → Blueprint，選這個 repo，Render 會讀 `render.yaml` 建立 `dynamic-modules-demo`。
2. 之後每次推上 `main` 都會自動重新部署。

限制：免費方案閒置 15 分鐘會休眠，下次開啟要等約 1 分鐘；沒有永久磁碟，休眠重啟後資料回到映像建置時的 Demo 資料（`poc/Dockerfile.demo` 在建置時就產生好，喚醒後不用等），日期停在建置那天，重新部署才會更新。Demo 站開了一鍵登入（`Demo__LoginEnabled`），任何人點一下就能以任何 Demo 帳號含 `admin` 登入，所以站上只放虛構的示範資料，請勿輸入真實資料。Render 上 cookie 帶 `secure; samesite=lax; httponly`（2026-11-05 實測）。

啟動時自動產生 Demo 資料：設定環境變數 `Demo__SeedOnStartup=true`，站台會在背景產生訂單、故障報修、模具保養、品質異常四組模組與範例記錄；本機預設關閉，Render 改用 `Dockerfile.demo` 在建置時產生，不需要這個設定。也可以由系統管理員在「系統管理 → Demo 資料」手動產生。各情境怎麼示範見 [Demo 腳本](demo.md)。

## 環境變數

| 變數 | 用途 |
|---|---|
| `Auth__InitialAdminPassword` | 全新安裝時 `admin` 的初始密碼（至少 8 碼）；沒設就產生隨機密碼，印在啟動 log |
| `Auth__ResetAdminPassword` | 緊急救援：啟動時重設 `admin` |
| `Auth__ClientIpHeader` | 反向代理後面時，登入限流改讀這個標頭的來源 IP（須是代理自己填、不接受客戶端帶入的標頭，如 `True-Client-IP`）；沒設就用連線來源，所有人會共用一個額度 |
| `Auth__TrustForwardedHeaders` | 在反向代理後面時設 `true`，cookie 才會加 `Secure` |
| `Demo__LoginEnabled` | 登入頁列出 Demo 帳號一鍵登入 |
| `Demo__SeedOnStartup` | 啟動時背景產生 Demo 資料 |
| `Attachments__Root` | 附件存放目錄；沒設就放在資料庫旁的 `attachments`（記憶體資料庫放暫存目錄） |
| `Attachments__UploadEnabled` | 是否開放上傳；沒設時 `Demo__LoginEnabled` 為 `true` 就關閉，否則開放 |
| `Attachments__DisabledReason` | 關閉上傳時顯示給使用者的原因 |
| `Attachments__MaxBytes`、`Attachments__MaxFilesPerField`、`Attachments__AllowedExtensions__0`… | 附件政策在這個環境的出廠值，見下方「附件設定與政策出廠值」 |
| `Ops__Edition` | 版別，`Cloud` 或 `OnPrem`（其他值記 Warning 並當 `OnPrem`）；顯示在系統管理員的 ops info |
| `Ops__TrustIncomingTraceParent` | 預設 `false`：忽略外來的 `traceparent`，request id 由伺服器產生；放在自家反向代理後面、要串接上游 trace 時才設 `true` |
| `Sqlite__MmapSizeMb` | SQLite 每條連線的記憶體對映大小，單位 MB，預設 `0`（關閉）；也可寫在 `appsettings.json` 的 `Sqlite:MmapSizeMb` |

`Auth__InitialAdminPassword` 只在資料庫還沒有可用的管理員時有效，之後改它不會覆寫既有密碼。`Auth__ResetAdminPassword` 至少 8 碼，會重設 `admin` 的密碼、啟用、解鎖、要求改密碼並讓所有 session 失效；救回後要把它移除，否則每次重啟都會再重設。`Demo__LoginEnabled` 絕不能在正式站開啟，它不驗密碼。Swagger 只在 Development 環境開放。

`Sqlite__MmapSizeMb` 是大型部署的效能設定，用在資料庫檔案達數 GB、查詢以索引篩選為主的站台：負載測試中索引型查詢單人快 30% 至 45%（[儲存層負載報告](superpowers/specs/2026-11-06-storage-load-report.md) §5）。對映大小要接近資料庫檔案的大小才有效果（只開 256 MB 對 3 GB 的資料庫看不出差別），數值大於檔案時只會用到檔案那麼多；代價是占用位址空間，頁面快取由作業系統管理，實體記憶體不足的主機請維持關閉。設定逐連線生效，改值後要重啟站台；0、負數與非數字都視為關閉。只有 SQLite 會套用。

## 附件設定與政策出廠值

`Attachments` 節（環境變數寫成 `Attachments__名稱`，也可寫在 `appsettings.json`）分兩類：

- **部署設定**：`Root`、`UploadEnabled`、`DisabledReason`，只能在這裡設定。
- **政策出廠值**：`MaxBytes`（單檔大小，位元組）、`MaxFilesPerField`（每個附件欄位的檔案數）、`AllowedExtensions`（副檔名白名單，陣列；環境變數用 `Attachments__AllowedExtensions__0`、`__1`…）。有寫才算，沒寫用程式出廠值。這幾個值是該環境的**出廠值**，系統管理員可以在「系統管理 → 政策設定」覆蓋它（單檔大小、檔案數也可以再由模組覆寫）；畫面上按「恢復出廠值」回到這裡的值。改設定檔要重啟站台才會生效。

政策有技術上下限（單檔 1 到 52428800 位元組、每欄位 1 到 100 個檔案、白名單最多 40 個）。設定檔的值超出範圍時，啟動會記一則 Warning 並夾到上下限（例如 `MaxBytes=999999999` 會變成 52428800）；非數字的值記 Warning 並改用程式出廠值。白名單裡的網頁、腳本與可執行檔副檔名（html、svg、js、exe 等，完整清單見[操作手冊](manual.md#系統管理政策設定)）不論誰設都不收：設定檔裡的這類項目會被丟掉並記 Warning。

## log、健康檢查與版本

- **log**：非開發環境輸出 JSON 到 stdout（`appsettings.json` 的 `Logging:Console`），由 Docker、systemd 或 Windows 服務包裝程式收集；開發環境維持純文字。4xx、5xx 與超過慢請求門檻的請求結束時記一行（只用路由樣板，不記查詢字串）；一般成功的請求是 Debug，預設不輸出，要看時設 `Logging__LogLevel__DynamicModules.WebAPI.Ops=Debug`，同一個請求的所有 log 都帶 `requestId` 欄位，回應標頭 `X-Request-Id` 與錯誤回應主體的 `traceId` 是同一個值。框架自帶的 `RequestId` 欄位（連線序號格式）是另一個東西，查同一請求請用 `requestId`。慢請求門檻在「系統管理 → 政策設定」。
- ⚠️ **產品 log 可能含例外原文**：框架的例外訊息可能帶輸入值，前端錯誤回報也會把原始訊息與 stack 記進 log，所以 log 視同敏感資料控管存取。
- **健康檢查**：`/api/health/live`（程序活著）、`/api/health/ready`（資料庫可連線，逾時 2 秒回 503）、`/api/health`（同 live，相容舊設定）。`render.yaml` 的 `healthCheckPath` 已改成 `/api/health/ready`；⚠️ 上線時請在 Render 後台確認免費方案真的使用 `healthCheckPath`、Blueprint 有同步。
- **版本**：`GET /api/version` 回 `version`、`commit`、`builtAt`。Docker build-arg `APP_VERSION`、`GIT_COMMIT`（沒給時退回 Render 的 `RENDER_GIT_COMMIT`）、`BUILD_TIME`（沒給時用建置當下）注入；沒注入的欄位顯示 `dev`。原始碼沒變時 Docker 沿用快取層，`builtAt` 可能停在舊時間。
- **自訂指標**：目前沒有匯出端；要看用 `dotnet-counters monitor --counters Formwright`（執行次數、前端錯誤收件數、規則失敗數）。

## 單一 instance 與 volume

登入失敗計數用程序內的鎖保護，假設每家客戶只跑一個後端 instance。Docker 部署要把資料庫目錄（`/app/data`）掛成 volume，登入金鑰才會保留。

## 本機試跑容器

容器以 Production 環境執行，`admin` 沒有預設密碼：請加 `-e Auth__InitialAdminPassword=...`，或從啟動 log 找隨機密碼。

```bash
cd poc && docker build -t dynamic-modules . && docker run -p 8080:8080 dynamic-modules
```

開 `http://localhost:8080`。
