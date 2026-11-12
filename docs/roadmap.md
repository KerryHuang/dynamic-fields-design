# Formwright ERP 路線與進度

這份文件只放開發順序與工作段表，是進度的單一來源，每段上線後更新狀態。各段的設計理由留在設計文件（`docs/superpowers/specs/`），設計文件不維護段狀態。路線的目的、已定決定、鐵則與階段內容見[路線設計](superpowers/specs/2026-11-06-erp-roadmap-design.md)。

逐項功能的完成狀態見[功能總清單](feature-checklist.md)，每段上線時一併打勾。

## 工作段

表格列序就是開發順序：已上線的段在最前，其後依使用者裁決排序。

| 工作段 | 內容 | 狀態 | 設計 |
|---|---|---|---|
| t1a、t1b、t2、t3a、t3b | 任務資料表、權限、API；留言、@、附件；站內通知與到期提醒；前端任務區塊、我的任務、通知鈴與首頁卡片 | 已上線 | [任務管理](superpowers/specs/2026-11-10-task-management-design.md) |
| t4a1、t4a2、t4b、t4c、t4d、t5 | 任務規則（定義、引擎、手動套用）、套件格式 7、設計器分頁、逐模組開關、Demo 與文件 | 已上線 | [任務管理](superpowers/specs/2026-11-10-task-management-design.md) |
| a1、a2a、a2b | 組織骨架；簽核定義、版本、約束驗證；送出、同意、退回、撤回（含 a2b2：查詢、簽核中心 API、防少簽補強） | 已上線 | [簽核流程](superpowers/specs/2026-11-10-approval-workflow-design.md) |
| a2c | 重新比對、預覽、臨時讀取權 | 已上線（程式與測試已在 main，功能清單已勾） | [簽核流程](superpowers/specs/2026-11-10-approval-workflow-design.md) |
| m1、i1、i2a、i2b、i2c | 多語系：模組選單分區、前端寫死文字、後端語系機制、記錄寫入與簽核訊息 | 已上線 | — |
| s2c、s3a | 應用程式整合層；報表資料庫端聚合 | 已上線 | [應用程式整合](superpowers/specs/2026-11-09-app-integration-design.md)、[報表資料庫端聚合](superpowers/specs/2026-11-10-report-db-aggregation-design.md) |
| p1a | 政策後端基礎：政策登記、讀值服務、模組覆寫表與 migration、政策 API；接上 A1、A2 | 已上線 | [政策設定](superpowers/specs/2026-11-11-policy-settings-design.md) §9 |
| p1b | 政策前端：系統「政策設定」頁、模組設計「政策」分頁；任務規則畫面改讀生效值 | 已上線 | [政策設定](superpowers/specs/2026-11-11-policy-settings-design.md) §9 |
| p1c | 任務與通知政策：A6、A7、A13、A14 | 已上線 | [政策設定](superpowers/specs/2026-11-11-policy-settings-design.md) §9 |
| p1d | 附件政策：A20（含部署設定檔當出廠值） | 已上線 | [政策設定](superpowers/specs/2026-11-11-policy-settings-design.md) §9 |
| O1 | 維運地基：結構化 log 與 request id、health live／ready、版本端點、前端錯誤收集、背景排程器（o1a、o1b、o1c）；任務「即將到期」提醒改為到期前 N 天內補一次、同一任務只補一次（使用者 2026-11-12 裁決，排進 o1b） | 未開始 | [維運與觀測](superpowers/specs/2026-11-11-ops-observability-design.md) §9、§10 |
| un1 | 列表、匯出、篩選的使用者欄位顯示姓名（小段穿插，使用者 11-11 裁決；位置是主控判斷，2026-11-12 排定） | 未開始 | — |
| pg0 | PostgreSQL provider 抽象＋可在 PostgreSQL 跑的測試環境；驗收要含 O1 的排程器（兩實例搶同一工作）與通知維護（兩條路徑並行不重複）在 PostgreSQL 上通過；分 pg0a–pg0d | 未開始 | [PostgreSQL provider](superpowers/specs/2026-11-12-postgres-provider-design.md) |
| a2d | 簽核鎖定、定義守衛、附件；併入簽核類政策設定 | 未開始 | [簽核流程](superpowers/specs/2026-11-10-approval-workflow-design.md)、[政策設定](superpowers/specs/2026-11-11-policy-settings-design.md) §9 |
| a3 | 轉交、加簽、代理、逾期提醒 | 未開始 | [簽核流程](superpowers/specs/2026-11-10-approval-workflow-design.md) |
| a4a | 前端：簽核設計器 | 未開始 | [簽核流程](superpowers/specs/2026-11-10-approval-workflow-design.md) |
| a4b | 前端：送簽畫面與簽核中心 | 未開始 | [簽核流程](superpowers/specs/2026-11-10-approval-workflow-design.md) |
| a5 | 套件格式 8：簽核流程 | 未開始 | [簽核流程](superpowers/specs/2026-11-10-approval-workflow-design.md) |
| a6 | CRM 設計案與簽核 Demo、文件 | 未開始 | [簽核流程](superpowers/specs/2026-11-10-approval-workflow-design.md) |
| 權限軸 | 設計與授權分成兩條權限軸（使用者 11-11 裁決排在簽核主線之後；開工前再寫設計） | 未開始 | — |
| O2 | 事件中心：健康檢查、維運事件、稽核表（與 MCP 共用）、告警通道、監控頁、規則失控自動暫停 | 未開始 | [維運與觀測](superpowers/specs/2026-11-11-ops-observability-design.md) §9 |
| p2、p3 | 設計與處理上限；地區與組織政策（使用者 2026-11-12 確認排在 O2 之後、mcp1a 之前） | 未開始 | [政策設定](superpowers/specs/2026-11-11-policy-settings-design.md) §9、§3.7 |
| i3、i4 | 多語系：名稱欄位多語（儀表板、報表名稱），套件格式隨升版；翻譯匯出、匯入（Excel，只含設計資料，介面固定文字不開放覆寫），兩件一起做（使用者 2026-11-12 排在 p3 之後） | 未開始 | — |
| mcp1a | MCP 後端地基：權杖、範圍、工具分級與確認碼、稽核寫入點、速率限制、12 個唯讀工具；權杖與確認碼的 repository 測試在 PostgreSQL 與 SQLite 各跑一次 | 未開始 | [MCP server](superpowers/specs/2026-11-11-mcp-server-design.md) §12 |
| mcp1b | MCP 畫面：個人「AI 存取權杖」頁、管理員「AI 連線」頁、設計器欄位旗標、手冊 | 未開始 | [MCP server](superpowers/specs/2026-11-11-mcp-server-design.md) §12 |
| O3 | 廠商維運中心（Cloudflare Workers＋D1）、外部探測、匿名回報與新版本通知、告警推播 | 未開始 | [維運與觀測](superpowers/specs/2026-11-11-ops-observability-design.md) §9 |
| pkg1 | 套件更新入口：把新版內建應用程式套到客戶已開啟的應用程式（排在 O3 之後，先寫設計；第一個客戶上線前必須有結論） | 未開始（設計待寫） | — |
| O4、mcpO1 | agent L1 唯讀診斷；產品端維運唯讀工具與維運服務帳號 | 未開始 | [維運與觀測](superpowers/specs/2026-11-11-ops-observability-design.md) §9、[MCP server](superpowers/specs/2026-11-11-mcp-server-design.md) §12 |
| O5 | agent L3 修正 PR、CI gate | 未開始 | [維運與觀測](superpowers/specs/2026-11-11-ops-observability-design.md) §9 |
| O6、mcpO2 | 雲端版 L2 自動處置；產品端維運處置工具 | 未開始 | [維運與觀測](superpowers/specs/2026-11-11-ops-observability-design.md) §9、[MCP server](superpowers/specs/2026-11-11-mcp-server-design.md) §12 |
| mcp2a | MCP 一般寫入工具、冪等鍵 | 未開始 | [MCP server](superpowers/specs/2026-11-11-mcp-server-design.md) §12 |
| mcp2b | MCP 高風險業務工具 | 未開始 | [MCP server](superpowers/specs/2026-11-11-mcp-server-design.md) §12 |
| mcp3 | MCP 設計器工具（開工前另寫設計） | 未開始 | [MCP server](superpowers/specs/2026-11-11-mcp-server-design.md) §12 |
| mcp4 | MCP OAuth 2.1（Claude.ai、ChatGPT 連接器；可視雲端版第一個客戶的需求提前） | 未開始 | [MCP server](superpowers/specs/2026-11-11-mcp-server-design.md) §12 |
| s2d-1g | Demo 公司跨應用程式共用、移除模擬客戶 | 未開始 | [共用主檔](superpowers/specs/2026-11-10-shared-master-design.md) |
| s2d-1h | 主檔收尾：參照挑選器守衛、CRM 補測試等 | 未開始 | [共用主檔](superpowers/specs/2026-11-10-shared-master-design.md) |
| s2d-2 | 品項共用主檔、移除模擬實體 | 未開始 | [共用主檔](superpowers/specs/2026-11-10-shared-master-design.md) |
| s2e-a | 訂單到出貨：追溯與履約核心 | 未開始 | [訂單到出貨](superpowers/specs/2026-11-11-order-workorder-shipment-design.md) |
| s2e-b | 訂單到出貨：應用程式間整合 | 未開始 | [訂單到出貨](superpowers/specs/2026-11-11-order-workorder-shipment-design.md) |
| s2f | 訂單變更單 | 未開始 | [訂單到出貨](superpowers/specs/2026-11-11-order-workorder-shipment-design.md) |
| s3b | 期間鎖定、分組小計、損益表列結構、Top N | 未開始 | [ERP 資料模型](superpowers/specs/2026-11-10-erp-data-model-design.md) |
| s3c | 會計應用程式 | 未開始 | [ERP 資料模型](superpowers/specs/2026-11-10-erp-data-model-design.md) |
| HRM | 階段 4：員工主檔、請假、出勤、費用報支，含依部門的資料範圍；開工前再切段 | 未開始 | — |
| 階段5 | 檢視與收尾：看板、日曆、專案與工時、客服工單、應用程式管理畫面補齊（含應用程式匯出畫面）、備份還原；開工前再切段 | 未開始 | — |

HRM（階段 4）與階段 5 先用佔位列排序，開工前再切成正式工作段。

## 階段概況

| 階段 | 狀態 |
|---|---|
| 階段 0：地基 | 已完成 |
| 儀表板 | 已完成 |
| 階段 1：單據流與 CRM | 已完成 |
| 階段 2：事件、帳本、進銷存與 MES | 核心與 s2c 整合層已完成；s2d 共用主檔進行到 s2d-1f |
| 任務管理（插在 s2d-1f 之後） | 已完成 |
| 簽核流程（與任務管理一起做） | 進行中 |
| 階段 3：會計 | s3a 報表資料庫端聚合已完成；s3b、s3c 未開始 |
| 階段 4：HRM | 未開始（組織骨架、站內通知已隨任務與簽核提前） |
| 階段 5：檢視與收尾 | 介面語言切換已完成；其餘未開始 |
| 平台段（2026-11-11 插入）：政策設定、維運、PostgreSQL、MCP | 未開始；政策 p1 開發中 |

## 開發順序（使用者 2026-11-10 確認）

1. **任務管理與簽核**（含 CRM 設計案情境）。簽核後端一條線；任務畫面與規則平行。
2. **多語系**（穿插進行）：模組選單分區多語系化；全面盤點後修補高、中兩級；之後做翻譯匯出／匯入（Excel，只含設計資料，介面固定文字不開放覆寫）。
3. **共用主檔收尾**：s2d-1g（Demo 公司跨應用程式共用等）、s2d-1h、s2d-2（品項共用、移除模擬實體）。
4. **訂單到出貨整合**：s2e-a 追溯與履約核心 → s2e-b 應用程式間整合 → s2f 訂單變更單。
5. **會計**：s3b 期間鎖定、分組小計、損益表列結構、Top N → s3c 會計應用程式。
6. **HRM**：依部門的資料範圍，以及員工、請假、出勤、費用報支。
7. **階段 5 其餘**：看板與日曆、專案與工時、客服與 IT 工單、應用程式開啟頁面、刪除應用程式、手動群組對應、備份還原。

第一個客戶正式上線前，必須決定套件更新入口（見路線設計「這版不做」；段 pkg1，2026-11-12 排在 O3 之後，先寫設計）。

## 2026-11-11 插入的平台段

設計：[政策設定](superpowers/specs/2026-11-11-policy-settings-design.md)、[維運、觀測與告警派工 agent](superpowers/specs/2026-11-11-ops-observability-design.md)、[MCP server](superpowers/specs/2026-11-11-mcp-server-design.md)。這幾段插在上面第 1 項「任務管理與簽核」的簽核主線中間。

順序：**p1 → O1 → un1 → pg0 → 簽核 a2d–a6（a2c 已完成）→ O2 → p2、p3 → i3、i4 → mcp1（mcp1a → mcp1b）→ O3 → pkg1 → O4–O6**，MCP 後續段 mcp2a → mcp2b → mcp3 → mcp4 依進度與 O4–O6 穿插。

- 使用者裁決（2026-11-11）：p1 → O1 → pg0 → a2d–a6 → O2 → mcp1 → O3…；O1 在 p1 之後、a2d 之前；O2 以後視情況穿插；pg0 在 O1 之後，之後的功能兩種資料庫都驗，其餘 PostgreSQL 改寫排在簽核之後；MCP 地基 mcp1 在簽核與 O2 之後，業務寫入、設計器工具、OAuth 後續分段，維運 O4、O6 直接用 MCP 工具；列表的使用者欄位顯示姓名用小段穿插。
- 使用者確認（2026-11-12）：p2、p3 排在 O2 之後、mcp1a 之前；O3 → O4 → O5 → O6 的先後；mcp1 之後是 mcp1a → mcp1b → mcp2a → mcp2b → mcp3 → mcp4，mcpO1 隨 O4、mcpO2 隨 O6，mcp4 可視雲端版第一個客戶的需求提前。
- 使用者排定（2026-11-12）：翻譯匯出、匯入（i4）排在 p3 之後，與 i3 一起；套件更新入口（pkg1）排在 O3 之後，先寫設計；應用程式匯出畫面併入階段 5。
- 主控判斷（位置）：un1 排在 O1 之後、pg0 之前；pkg1 緊接 O3，不等 O4。
- 段號命名：MCP 各段一律用 mcp 開頭（mcp1a、mcp1b、mcp2a、mcp2b、mcp3、mcp4、mcpO1、mcpO2），與已上線的多語系 m1（模組選單分區）區隔。

維運與 MCP 兩份設計的 C1–C7 已由使用者 2026-11-12 全部裁決（都選建議方案），紀錄在維運設計 §16、MCP 設計 §20。
