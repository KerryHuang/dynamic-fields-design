# 第 3 階段驗收

範圍：角色權限——群組與個人的動作權限、資料範圍、欄位權限、選項範圍，以及 401／403／404 的判定順序。

## 自動測試

驗收日：2026-11-03

| 指令 | 結果 |
|---|---|
| `dotnet test poc/backend --filter Phase3_Scenario` | 通過（1/1） |
| `dotnet test poc/backend`（不含 Benchmark） | 通過：DomainTests 120、ApplicationTests 220、WebAPITests 47，共 387，失敗 0 |
| `cd poc/frontend && bunx vitest run` | 通過：27 個檔案、110 個測試 |
| `cd poc/frontend && bunx vue-tsc --noEmit` | 通過（無錯誤） |

`Phase3_Scenario`（`poc/backend/tests/DynamicModules.WebAPITests/Phase3AcceptanceTests.cs`）以 API 走完下列情境，每步皆斷言：

1. `admin` 建模組 `repair-request` 並啟用：申請人（標題欄，`text`）、故障類別（`enum`，選項「模具損壞」「機台異常」「外包」）、預估費用（`currency`，必填、可排序）、說明（`textarea`）
2. 用 `PUT /modules/repair-request/permissions` 設定：`sales` 為 `Read`／`Add`／`Edit`、範圍 `Own`、沒有 `Pricing`；`sales-manager` 為 `Read`／`Add`／`Edit`／`Delete`／`Pricing`、範圍 `All`；`floor` 為 `Read`／`Add`／`Edit`、範圍 `Own`，「說明」唯讀；選項「外包」只給 `sales-manager`
3. `sales01` 建 2 筆、`sales02` 建 1 筆，都沒送預估費用，皆回 201（沒有 `Pricing` 的人不必填金額）
4. `sales01` 的列表只有自己 2 筆，`values` 沒有預估費用；schema 沒有預估費用欄，`permissions.delete` 為 `false`
5. `sales01` 讀 `sales02` 的記錄回 404（不洩漏存在）；刪除自己的記錄回 403
6. `sales01` 選「外包」回 400；`manager01` 選「外包」並送預估費用，回 201
7. `manager01` 的列表有全部 4 筆，看得到預估費用，依預估費用降冪排序時有填金額的排最前
8. `floor01` 建一筆記錄（送了「說明」，新增時被忽略）；`admin` 把「說明」設成原值；`floor01` 修改時送不同的「說明」，回 200，以 `admin` 讀回的「說明」仍是原值，其他欄位（標題）有更新（見[裁決 R6](../superpowers/plans/2026-11-03-phase3-rulings.md)）
9. 不帶 `X-User` 呼叫 `GET /menu` 回 401（`請先選擇使用者`）；`sales01` 呼叫 `POST /modules` 回 403（`沒有權限執行此操作`）

## 實機走查

執行 `poc/start.command` 後在瀏覽器走上述情境。截圖將存於 `docs/acceptance/phase3/`。

| # | 項目 | 結果 | 截圖 |
|---|---|---|---|
| 1 | 設計器權限分頁：群組權限、資料範圍、欄位權限、選項範圍，儲存後重新整理仍在 | 通過 | 新建 `p3walk` 模組並在 UI 設定四項，儲存後重新整理仍在（模組與欄位用 API 建立，啟用與權限設定走 UI）。[截圖](phase3/1-permissions-tab.png) |
| 2 | 切 `sales01`：選單沒有「動態模組設計」、列表只有自己的記錄、看不到金額欄、沒有刪除按鈕、「外包」選項停用 | 通過 | 選單沒有「動態模組設計」（「系統管理」標題仍留空）；列表只有 A1、A2；沒有金額欄、金額輸入與刪除按鈕；「外包」選項停用。[列表](phase3/2-sales01-list.png)、[表單](phase3/2-sales01-form.png) |
| 3 | 切 `manager01`：看得到全部記錄與金額 | 通過 | 列表 4 筆，有「預估費用」欄（M1 為 8,000），「外包」可選。[截圖](phase3/3-manager01-list.png) |
| 4 | 切 `floor01`：「說明」唯讀 | 通過 | 新增與編輯表單的「說明」皆為唯讀，標題仍可編輯。[截圖](phase3/4-floor01-form.png) |
| 5 | 在網址列直接輸入 `/system/dynamic-modules`，顯示「沒有權限使用此功能」 | 通過 | 以 `sales01` 直接開網址，顯示「沒有權限使用此功能」；console 只有一筆預期的 403。[截圖](phase3/5-forbidden.png) |

## 實作裁決

執行計畫時的補充與修正見 [`docs/superpowers/plans/2026-11-03-phase3-rulings.md`](../superpowers/plans/2026-11-03-phase3-rulings.md)（R1–R16）。對驗收影響最大的兩條：

- R6：驗收情境中 `floor` 多給 `Edit`，才能驗證唯讀欄位的輸入被忽略。
- R7：首次升級授權只在建立權限群組的那次 seed 執行，既有模組升級後照舊可用，新模組沒有授權（fail-closed）。
