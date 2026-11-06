# LoadTest：儲存層負載測試工具

量動態模組在大資料量與多人同時使用下的回應時間（階段 0 的儲存負載測試，結果見
`docs/superpowers/specs/2026-11-06-storage-load-report.md`）。工具在同一個行程內直接送
MediatR 請求（與 WebAPI 相同的服務組態、每個請求一個 DbContext 與連線），所以不含 HTTP 的成本。

```bash
cd poc/backend
dotnet build tools/LoadTest -c Release
T=tools/LoadTest/bin/Release/net8.0/LoadTest.dll

dotnet $T selfcheck                                  # 驗證批次灌入的列與 CreateRecordHandler 產生的一致
dotnet $T seed --db /path/load.db --years 10 --per-year 300000 --accounts 300 --customers 20000
dotnet $T check --db /path/load.db                   # journal_mode、檔案大小、筆數
dotnet $T run --db /path/load.db --users 20 --duration 60 --solo-runs 20 --out result.json
```

- `seed` 會刪掉同名資料庫重建（科目、客戶、分錄三個模組；分錄日期涵蓋 `years` 年，截止在固定的 2026-11-06），
  用固定亂數種子，結果可重現。300 萬筆分錄約 90 秒、資料庫約 3 GB。
- `run` 會寫入資料庫（新增與修改分錄），要回到乾淨狀態請複製一份種好的資料庫來跑。
  階段：`solo`（單人逐項）、`mixed`（每人每 1–3 秒做一件事，約 80% 讀 20% 寫）、
  `stress`（不停手）、`writes`（只寫；`--write-ops create_entry` 可只跑新增）。
- 實驗用：環境變數 `LT_PRAGMAS="mmap_size=4294967296;busy_timeout=30000"` 會在每條連線開啟時套用這些 PRAGMA。
- 報表的 `state=tooMany`（來源模組超過載入上限）算操作失敗，不算成功。
