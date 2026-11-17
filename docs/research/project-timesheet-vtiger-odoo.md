# 專案與工時應用程式：vtiger／Odoo 研究報告

日期：2026-11-17。範圍：設計「專案與工時」（路線階段 5）之前，先弄清楚 vtiger CRM 與 Odoo 怎麼做，並對照 Formwright 核心已有的任務能力。

標記說明：
- 「已查證」＝有下列來源 URL 直接支持。
- 「推測」＝依來源間接推論或自行判斷。
- 「未查證」＝查不到、或來源被擋。
- 版本歸屬用「CE」＝Odoo Community（LGPL 公開原始碼）、「EE」＝Odoo Enterprise（閉源付費）。
- vtiger 沒有 CE／EE 之分，另標開源套件或雲端版。

## 0. 研究方法與可信度（先看這節）

1. 🔑 vtiger 官方說明站（help.vtiger.com、two.vtiger.com）與 code.vtiger.com 對自動化抓取回 403，抓不到全文。vtiger 部分靠三個來源：
   - 搜尋結果摘要。
   - GitHub 鏡像 `vtiger-crm/vtigercrm` 內的 `packages/vtiger/optional/Projects.zip`，我下載後解開讀 manifest 取欄位。
   - 第三方（dhtmlx、YetiForce 程式碼）。
2. ⚠️ 那個 Projects.zip 的 manifest 版本是 2.7、相容 vtiger 5.2（2010 年匯出）；該鏡像整體看起來是舊版程式碼。8.x 開源版實際欄位可能多於下表，**8.x 開源版的欄位以此為底稿，未逐欄查證**。
3. ⚠️ vtiger 的 Gantt、Timelogs、專案範本、從 Deal 建專案等說明，多來自雲端版（Vtiger One／Vtiger CRM Cloud）的說明文件，不確定開源 8.x 有沒有。一則搜尋摘要寫「Projects 模組只在 Vtiger One Professional 與 Enterprise 版」，另一則寫 Gantt 在 Pilot、Growth、Professional、Enterprise 都有，兩者互相矛盾，我無法裁定。
4. Odoo 的版本歸屬主要靠**公開原始碼**判斷：odoo/odoo 的 17.0、18.0 分支只含 Community。能在 `addons/` 裡找到的模組就是 CE；找不到的（`project_enterprise`、`timesheet_grid`、`planning`）是 EE。這比官方文件可靠，因為 Odoo 文件頁面幾乎不標註版本。
5. 第三方部落格（Cybrosys）對 CE／EE 的說法與原始碼互相矛盾（例如說里程碑、依工時開票是 EE，但原始碼顯示 CE 模組就有）。**以原始碼為準，部落格只當輔助並標明衝突。**
6. 「CE 有模組」不等於「CE 有完整體驗」：我沒有實際安裝 CE 驗證畫面（例如「依工時開票」要的 Accounting 在 18 的完整版可能是 EE，見 §2.8）。

## 1. vtiger CRM

### 1.1 模組與欄位（開源套件 manifest，已查證，版本見 §0.2）

來源：https://github.com/vtiger-crm/vtigercrm （`packages/vtiger/optional/Projects.zip`，內含 Project、ProjectMilestone、ProjectTask 三個子套件）。Projects 在 vtiger 開源版是**可選套件**，要在模組管理安裝（`packages/vtiger/optional/`，與 CustomerPortal、ModComments、RecycleBin 同列）。

**Project（專案）**
- 欄位：
  - `projectname`（必填）
  - `project_no`（自動編號）
  - `startdate`、`targetenddate`、`actualenddate`（後兩者驗證不得早於開始日）
  - `projectstatus`：prospecting／initiated／in progress／waiting for feedback／on hold／completed／delivered／archived
  - `projecttype`：administrative／operative／other
  - `projectpriority`：low／normal／high
  - `progress`：10% 到 100% 的下拉（手填，不自動計算）
  - `targetbudget`（單一金額欄）
  - `projecturl`
  - `linktoaccountscontacts`（「Related to」，可指向 Accounts〔Organizations〕或 Contacts，二選一）
  - `assigned_user_id`、`description`
- 相關清單：Project Tasks、Project Milestones、Project Team（`ProjectTeam`，manifest 有登錄，本次未查到欄位）、Documents、Trouble Tickets（HelpDesk，可「選取」既有工單掛上專案）。

**Project Milestone（里程碑）**
- 欄位：名稱（必填）、`projectmilestonedate`（單一日期）、`projectid`（必填，指向 Project）、`type`、負責人、編號、說明。
- 沒有「已達成」旗標、沒有進度、沒有開票欄位。

**Project Task（專案任務）**
- 欄位：
  - 名稱（必填）、`projecttasktype`、`projecttaskpriority`
  - `projectid`（必填）、負責人、`projecttasknumber`（序號）
  - `projecttaskprogress`（10% 到 100% 下拉）
  - `projecttaskhours`（Worked Hours，**手填數字**）
  - `startdate`、`enddate`（驗證不得早於開始日）、說明
- 相關清單：只有 Documents。
- 沒有狀態欄位、沒有子任務、沒有相依、沒有到 Milestone 的直接欄位。
  - 注意：雲端版說明寫「里程碑底下會顯示相關任務」，表示較新版本可能加了 Task 到 Milestone 的關聯，開源 8.x 未查證。

### 1.2 與 Organizations、Contacts、Potentials、Tickets 的關係

- Organizations／Contacts：Project 的 `Related to` 單一欄位二選一（已查證，manifest）。雲端版說明另寫「可從 Contacts、Organizations 建專案」（搜尋摘要，來源 https://help.vtiger.com/article/117252125-What-are-Projects ，原頁抓不到全文）。
- Potentials（雲端版叫 Deals）：manifest 沒有 Potentials 欄位。雲端版說明寫「從 Deal 的 Related 頁籤建立專案，會連結到該 Deal」，並有「贏單轉專案、欄位對應」（搜尋摘要，來源 https://help.vtiger.com/article/156170913-User-Guide-for-Project-Management ）。開源 8.x 未查證。
- Tickets：Project 的相關清單有 Trouble Tickets（已查證，manifest）。雲端版另有「Internal Ticket 掛到 Project Task」（搜尋摘要 https://help.vtiger.com/faq/114137898-How-do-I-add-an-Internal-Ticket-to-a-Project-Task ，開源 8.x 未查證）。

### 1.3 甘特圖

- 雲端版有：從專案摘要頁開 Gantt，里程碑與其任務成一組，可新增任務、里程碑、列印；任務**必須同時有開始與結束日**，否則圖是空的（搜尋摘要，來源 https://help.vtiger.com/article/147105248-Manage-Projects-with-Gantt-Chart 、https://help.vtiger.com/faq/164509511-Why-is-my-Gantt-chart-empty-even-after-adding-a-project-and-milestones ）。
- 第三方廠商 VGS Global 另做「完整拖放 Gantt」外掛，暗示原生沒有完整拖放（推測，來源 https://dhtmlx.com/blog/dhtmlxgantt-in-vtiger-crm/ ，頁面未直接說原生缺什麼）。
- 沒有看到任何相依線的說明（未查證）。
- 開源 8.x 有沒有原生 Gantt：未查證。

### 1.4 工時

- 開源套件的 Project Task 只有 `projecttaskhours` 一個手填數字，沒有工時明細（已查證，manifest）。
- 雲端版有 Timelogs：用在 Tasks、Cases、Internal Tickets、Work Orders，不能用在 Events；可記計費（billable）工時（搜尋摘要，來源 https://help.vtiger.com/article/139843122-Time-Tracking-using-Timelogs 、https://help.vtiger.com/faq/114131685-How-can-I-add-a-timelog-to-a-project-task 、https://help.vtiger.com/faq/146822146-How-do-I-log-time-for-a-billable-task ）。開源 8.x 有沒有 Timelogs：未查證。
- 專案層級的工時彙總要靠 workflow 把任務工時加總（搜尋摘要，來源 https://help.vtiger.com/article/156170913-User-Guide-for-Project-Management ）。

### 1.5 專案範本

- 沒有找到任何專案範本、任務範本功能。搜尋只找到「專案的列印範本」（來源 https://help.vtiger.com/article/117252125-What-are-Projects ）。結論：**未查到，推測沒有**。

### 1.6 vtiger 小結

vtiger 的專案是「CRM 旁邊的輕量附屬模組」：三個平面模組、手填進度、手填工時，重點是把專案掛在客戶與工單上。沒有階段看板、沒有相依、沒有範本（開源版）、沒有依工時開票。

## 2. Odoo（17／18）

來源策略見 §0.4。原始碼來源：
- https://github.com/odoo/odoo/tree/18.0/addons
- https://github.com/odoo/odoo/tree/17.0/addons

我讀了 18.0 的 `project/models/project_project.py`、`project_task.py`、`project_milestone.py`、`project_task_type.py`，以及 `hr_timesheet`、`sale_project`、`sale_timesheet`、`project_account`。17.0 只比對了關鍵欄位開關，兩版在本研究涉及的功能一致。

### 2.1 專案（project.project，CE）

- 欄位重點：
  - 名稱、說明、客戶（`partner_id`）、專案經理（`user_id`）
  - `date_start`、`date`
  - 標籤（`tag_ids`）、顏色、公司、分析帳戶（`account_id`，專案利潤分析的基礎）
  - 可見度 `privacy_visibility`：private 邀請制／所有內部使用者／portal 公開三種（文件 https://www.odoo.com/documentation/18.0/applications/services/project/project_management.html ）
  - 協作者 `collaborator_ids`（外部使用者分享，Read／Edit 限制／Edit 三種權限）
  - 每專案可自訂任務欄位（`task_properties_definition`）
  - 客戶評價（rating）
  - 專案更新 `project.update`（狀態：on track／at risk／off track／on hold／done，附進度與利潤摘要）
- 專案可獨立開關：階段（`group_project_stages`）、里程碑（`allow_milestones`）、任務相依（`allow_task_dependencies`）、週期任務、評價。這些在 `project/security/project_security.xml` 是**可選群組**，在 設定 裡打勾。
- 專案階段（`project.project.stage`）：專案本身在看板上依「階段」分欄（例如 規劃、進行中、完成）。程式碼在 CE，需在設定開啟 Project Stages。
- 專案範本：
  - 18.0 的 CE 程式碼中，`project.project` **沒有** `is_template` 欄位；我另外試抓 18.0 文件的「project_templates」頁回 404，所以「18.0 有原生專案範本功能」**未查證，程式碼證據傾向沒有**。
  - 有的是銷售用法：服務品項可指定 `project_template_id`，銷售訂單確認時用 `project_template.copy()` 複製出新專案（`sale_project/models/sale_order_line.py`，CE）。
  - 也就是說「範本」是一個被標記複製的普通專案，不是獨立功能。

### 2.2 任務（project.task，CE）

- 欄位重點：
  - 名稱、說明、優先度 `priority`、標籤、顏色
  - 看板階段 `stage_id`
  - 狀態 `state`：in progress／changes requested／approved／canceled／done（另有 waiting）
  - 專案、客戶、多人負責人 `user_ids`
  - `date_deadline`、`date_assign`、`date_end`
  - 預估時數 `allocated_hours`
  - 自訂欄位 `task_properties`
  - 附件、Email 別名建任務
- 「個人階段」：每個人有自己的私人看板欄（`personal_stage_id`）。
- 子任務：`parent_id`／`child_ids`，不限層數，有完成比例 `subtask_completion_percentage`、`subtask_allocated_hours`（程式碼 CE）。
- 里程碑：`project.milestone`（名稱、`deadline`、`is_reached`、`reached_date`、任務清單、完成任務數），任務指定 `milestone_id`（CE，需開啟 Milestones）。
- 相依：`depend_on_ids`／`dependent_ids`，被擋的任務顯示「等待」，前置完成才解鎖（程式碼 CE；文件 https://www.odoo.com/documentation/18.0/applications/services/project/tasks/task_dependencies.html ）。
- 週期任務：`recurring_task`、`repeat_interval`、`repeat_unit`、`repeat_until`，前一個完成才建下一個（CE）。

### 2.3 看板與甘特圖

- 看板（專案內依階段）、清單、日曆、活動等視圖：CE。
- **Gantt：EE。**
  - 證據：CE 的 `project/views/project_task_views.xml`、`project_views.xml` 在 17.0 與 18.0 都沒有 gantt 字樣（我實測 grep 為 0）。
  - 公開 `addons/` 沒有 `project_enterprise`。
  - 第三方 Cybrosys 也說 Gantt 是 EE（https://www.cybrosys.com/blog/what-are-the-differences-between-odoo-18-community-enterprise-in-project ）。
  - Odoo 文件提到「相依可在專案的 Gantt 檢視用拖拉建立」（https://www.odoo.com/documentation/18.0/applications/services/project/tasks/task_dependencies.html ），但頁面沒標版本。
- Gantt 特色（官方功能頁，https://www.odoo.com/app/project-features ）：依截止日、進度、相依、假日預測、排程衝突自動重排。
- 所以「相依」資料與 Task 欄位是 CE，但「在甘特圖上看見與拖拉相依線」是 EE。

### 2.4 工時（hr_timesheet，CE；grid、timer、驗證是 EE）

- CE：模組 `hr_timesheet`（顯示名稱 Task Logs）。
  - 工時行就是 `account.analytic.line`，欄位：專案、任務、員工、日期、描述、時數（`unit_amount`）、里程碑。
  - 專案設定可開「Timesheets」；任務詳細有 Timesheets 頁籤。
  - 員工的小時成本來自 `hr_hourly_cost`（CE）。
  - 設定項：提醒員工、提醒核准者（`reminder_user_allow`、`reminder_allow`）、輸入單位（小時／天）。
  - 這幾個設定欄位在 CE 的 `hr_timesheet/models/res_config_settings.py`，但**實際提醒功能是否只在 EE 生效，未查證**（Cybrosys 說提醒是 EE，與設定欄位在 CE 並存）。
- EE：公開 `addons/` 沒有 `timesheet_grid`，所以**週格狀（grid）輸入、計時器（timer）、工時驗證／核准流程**屬 EE（推測，依公開 repo 缺模組加 Cybrosys https://www.cybrosys.com/blog/differences-between-odoo-18-community-enterprise-in-timesheet ；官方文件頁也只列這些項目沒標版本）。第三方 `timesheet_grid_ce` 外掛的存在也佐證 CE 缺 grid（https://apps.odoo.com/apps/modules/18.0/timesheet_grid_ce ）。
- 請假自動扣工時：`project_timesheet_holidays`（CE）。

### 2.5 專案利潤分析

- 模組 `project_account`（CE 公開 repo）。
- 專案的 Dashboard／Profitability：以專案綁定的**分析帳戶**為中介，把有連到專案與分析帳戶的記錄拉進來（官方文件：https://www.odoo.com/documentation/18.0/applications/services/project/project_management/project_profitability.html ）。
  - 收入：工時（依品項開票政策計價）、銷售訂單的材料、客戶發票、訂閱、訂金、再開票費用。
  - 成本：工時（員工成本率）、採購單（供應商帳單過帳後才顯示）、庫存領料、已核准費用、供應商帳單、製造單等。
  - 金額分欄：預計（來自訂單）、待開票／待付款、已開票／已付款。
- 版本：文件沒寫；原始碼 CE 有 `project_account`、`project_hr_expense`、`project_purchase`，所以**基本利潤分析在 CE 程式碼就有**。Cybrosys 部落格對此前後矛盾（一處說 EE、一處說兩版都有），以原始碼為準。「預算管理」是 EE（官方功能頁提到，未查證歸屬，推測）。

### 2.6 客戶入口（portal）

- 專案可分享給外部（唯讀公開連結，或邀請協作者，權限 Read／Edit 限制／Edit），portal 使用者可看自己客戶的任務並留言（文件 https://www.odoo.com/documentation/18.0/applications/services/project/project_management.html ；官方功能頁 https://www.odoo.com/app/project-features ）。
- 程式碼：`project` 依賴 `portal`、`mail`，協作者模型在 CE。結論：**CE 有**（文件沒標版本，依原始碼）。
- 客戶評價（rating）也在 CE 程式碼（`portal_rating`、`rating_active`）。

### 2.7 從銷售訂單自動建專案／任務

- 服務品項的 `service_tracking`：`task_global_project`（在既有專案建任務）、`task_in_project`（建專案加任務）、`project_only`（只建專案）；可指定專案範本（`sale_project`、`sale_timesheet` 在 CE 公開 repo；`sale_timesheet` 是 auto_install，裝了 sales 與 timesheet 就自動帶）。
- 文件流程：確認訂單後訂單上出現 Projects／Tasks 智慧按鈕（https://www.odoo.com/documentation/18.0/applications/sales/sales/invoicing/time_materials.html ）。
- 品項開票政策（`service_policy`）：預付／固定價（ordered_prepaid）、依手動交貨量、依工時（delivered_timesheet）、依里程碑（service_type milestones）。

### 2.8 依工時開票

- 流程（同上文件）：服務品項設為「依工時」且 `Create on Order` 設為專案與任務，訂單確認後建專案；在任務 Timesheets 頁籤記工時，回訂單，Delivered 欄顯示工時，按「建立發票」。需要 Project、Accounting（文件寫要啟用 Analytic Accounting）、專案設定開 Timesheets。
- 版本：`sale_timesheet` 在 CE 公開 repo，所以**邏輯在 CE**；但文件要求 Accounting app，Odoo 18 的完整 Accounting（`account_accountant`）是 EE，CE 只有 Invoicing，所以實務上 CE 能走到哪一步**未查證（推測：CE 只有 Invoicing 也能走完建立發票，但分析帳戶功能可能受限）**。Cybrosys 說「Community 沒有依工時開票」，與原始碼矛盾，不採信。
- 另有 `sale_timesheet_margin`（CE）。

### 2.9 其他相關

- 費用報支掛專案並再開票：`project_hr_expense`、`project_sale_expense`（CE 公開 repo）。
- 工時單位：小時或天，可在設定改。
- 專案「路線圖」「預測／規劃（Planning、Forecast）」「Planning Gantt」：EE（公開 repo 沒有 `planning`、`project_forecast`；推測）。

## 3. 功能對照表

欄位值：有＝已查證有；無＝已查證沒有；推測／未查證。vtiger 欄分「開源套件」與「雲端版」。

| 功能點 | vtiger 開源（Projects.zip，舊版 manifest） | vtiger 雲端版 | Odoo CE | Odoo EE |
|---|---|---|---|---|
| 專案實體（客戶、PM、起迄日、預算欄） | 有（預算是單一文字／數字欄） | 有 | 有（客戶、PM、日期、分析帳戶、更新紀錄） | 同 CE |
| 專案狀態 | 有（8 個固定下拉） | 有 | 有（專案階段，可選開；加專案更新狀態） | 同 CE |
| 專案階段看板 | 無 | 未查證 | 有（Project Stages 設定開啟） | 同 CE |
| 任務欄位（負責人、起迄日、優先度） | 有（單人負責，優先度三級） | 有 | 有（多人負責、優先度、標籤、自訂欄位） | 同 CE |
| 任務狀態／階段 | 無狀態欄，只有進度下拉 | 未查證 | 有（階段加狀態） | 同 CE |
| 子任務 | 無 | 未查證 | 有（不限層數、進度、時數彙總） | 同 CE |
| 里程碑 | 有（名稱＋日期，無達成旗標） | 有（任務可群組於里程碑下，推測） | 有（deadline、達成、任務歸屬、可依里程碑開票） | 同 CE |
| 任務相依 | 無 | 未查到 | 有（資料與「等待」狀態，CE 程式碼） | 有，另有甘特圖拖拉建立與衝突重排 |
| 週期任務 | 無 | 未查證 | 有 | 同 CE |
| 看板 | 無 | 未查證 | 有（專案內任務看板、專案看板） | 同 CE |
| 甘特圖 | 未查證 | 有（任務需同時有起訖日；非完整拖放） | 無（程式碼無 gantt） | 有（含相依、排程重排） |
| 工時（Timesheets） | 只有任務上手填 `Worked Hours` | 有（Timelogs，可計費；開源 8.x 未查證） | 有（`hr_timesheet`，工時行掛專案、任務、員工） | 同 CE，加週格、計時器、核准流程、自動提醒（推測，見 §2.4） |
| 工時核准 | 無 | 未查證 | 管理者手動檢視（推測） | 有（驗證流程） |
| 專案範本 | 未查到 | 未查到（只有列印範本） | 無第一方功能（程式碼證據），有「用範本專案複製」於銷售流程 | 未查證 |
| 客戶入口／外部協作 | 有 Customer Portal 套件（可看哪些模組未查證） | 有（雲端入口，未細查） | 有（portal、協作者、公開連結、評價） | 同 CE |
| 專案利潤分析 | 無 | 未查證 | 有（`project_account`，經分析帳戶，收入對成本） | 同 CE，預算管理另列 EE（推測） |
| 銷售訂單自動建專案／任務 | 無 | 贏單轉專案（Deal，推測欄位對應） | 有（服務品項設定建專案加任務、可帶範本） | 同 CE |
| 依工時開票 | 無 | 未查證（Timelog 可記計費） | 邏輯在 CE 程式碼（`sale_timesheet`），但完整 Accounting 為 EE，CE 實際可用程度未查證 | 有 |
| 依里程碑開票 | 無 | 無 | 有（`service_type milestones`） | 同 CE |
| 費用掛專案再開票 | 無 | 無 | 有（`project_hr_expense`、`project_sale_expense`） | 同 CE |
| 與客服工單關聯 | 有（Trouble Tickets 相關清單） | 有（含 Internal Ticket 掛任務，推測） | 有（Helpdesk 為 EE，未查證與專案的連動） | 有（Helpdesk，未細查） |
| 規則產生任務（自動化） | 無（要用 workflow） | 有 workflow | 有自動化規則（CE 有 base_automation 推測） | 同 CE |

## 4. Formwright 現況對照

### 4.1 核心已有（不需要在專案應用程式再做）

來源：`docs/feature.md` 任務管理一節、`docs/superpowers/specs/2026-11-10-task-management-design.md`、`docs/superpowers/specs/2026-11-13-task-board-reports-gantt-design.md`、`docs/roadmap.md`。

- 任務資料：`Task` 可掛任何模組記錄或個人任務，欄位 `Title`、`Description`、`AssigneeUserId`、`DueDate`、`Status`（待辦、進行中、完成、取消）、`ModuleId`/`RecordId`。（t1a）
- 留言、@人、留言附件、站內通知、到期與逾期提醒、我的任務、首頁到期卡片。（t1b、t3a、t3b）
- 任務規則（觸發：建立、符合條件、欄位變成某值、手動；可抄欄位值當負責人與到期日；記錄上手動套用「任務範本」）。（t4a1、t4a2、t4b、t4c）
- 逐模組任務開關。（t4d）
- 主管看部屬任務與重派。（kb1，已上線）
- 任務看板（依狀態、負責人、到期區間，自訂看板與分享）。（kb2、kb3，已上線 2026-11-17）
- **排定但未上線**：任務統計報表 kr1、子任務一層與進度 st1、甘特圖 gt1（開始日、拖拉、依父子分組、只有到期日顯示菱形當里程碑感）。
- 其他核心：動態模組（欄位、狀態機、參照、明細）、報表設計器（含下鑽、儀表板）、簽核、附件、政策設定、應用程式套件（含任務規則）。（此為 roadmap 與 feature.md 所列；細節本次未逐一讀設計。）

### 4.2 明確決定不做（沿用，不可在專案應用程式繞回）

`docs/feature.md`「決定不做」：任務的**相依、優先度、重複任務、工時、Email 與推播**（使用者 11-13 裁決）。Email 通知待 au1 寄信能力完成後列第二版候選。

**重要含意**：「任務上的工時」被明確排除。要做工時，只能在**專案應用程式**裡另做一個「工時紀錄」模組，不能往核心任務加工時欄。（Odoo 的作法是工時行掛任務；Formwright 要掛專案，任務可選。）

### 4.3 做專案應用程式還缺什麼

| # | 缺口 | 屬性 | 備註 |
|---|---|---|---|
| G1 | 專案實體（客戶、PM、起迄日、狀態、預算、說明） | 應用程式套件 | 用動態模組＋狀態機即可表達；客戶用共用主檔「往來對象」；任務掛在專案記錄上（核心已支援）。 |
| G2 | 專案階段 | 套件（優先）；或核心看板擴充 | 專案狀態用狀態機。Odoo 的「階段」是看板欄；Formwright 階段 5 的「看板（記錄依狀態分欄）」項目另排，專案是它的第一個情境。 |
| G3 | 里程碑 | 套件（簡單版）；核心 | 簡單做法：專案下的明細或子模組（名稱、日期、達成旗標、備註）。任務與里程碑的歸屬需要核心能力，**核心任務沒有「歸屬於某個子記錄」**：任務只能掛在一筆記錄，所以里程碑若是專案的明細列，任務要掛里程碑記錄而非專案，兩者取捨見 §6 問題 2。gt1 的「只有到期日顯示菱形」是不依賴資料模型的替代呈現。 |
| G4 | 工時紀錄（日期、人、專案、可選任務、時數、可計費旗標、備註） | 套件；核心可能要補「任務參照欄位」 | 因為工時不進任務，工時要掛專案；若要選任務，需動態模組能參照 `Task`（核心表而非動態模組），**目前是否有此欄位型別未查證，推測沒有**。 |
| G5 | 工時彙總報表（依專案、人、月；預估對實際） | 套件 | 報表設計器已有聚合；預估時數存在專案欄位（G1）。 |
| G6 | 專案與銷售訂單或合約的關聯 | 套件欄位＋依賴 s2e／ct1 | 專案參照「銷貨訂單」（s2e）或「合約」（ct1）。順序上專案晚於兩者，可直接用參照。 |
| G7 | 依工時開票 | 套件；依賴 s3c 發票、單據流 | 見 §5。 |
| G8 | 專案範本 | **核心已有近似物**：任務規則的「手動套用」＋任務範本 | 在專案記錄上「套用範本」就產生一批任務。差距：Odoo 範本會連階段、里程碑一起複製；Formwright 只複製任務。 |
| G9 | 費用報支掛專案 | 套件欄位，依賴 HRM 費用報支 | HRM 段（階段 4）。 |
| G10 | 專案利潤分析 | 套件報表；依賴 s3c | 收入（發票）減成本（工時 × 時薪、費用、採購）。需要「時薪」來源。 |
| G11 | 客戶入口（外部看專案進度） | 核心（若做） | Formwright 沒有外部使用者（「客人自助線上預約不做」是同一類判斷；入口是否存在**未查證**）。第一版不做。 |
| G12 | 專案的任務視圖：同一專案內的看板／甘特 | 核心已有（kb、gt1），需「依記錄篩選」 | 確認看板與甘特能固定篩到單一專案記錄（推測，設計文件寫範圍與篩選同看板，細節未逐條驗證）。 |
| G13 | 專案內任務統計（進度％、工時進度） | 核心 kr1 加套件報表 | 子任務完成比例由 st1 提供。 |

### 4.4 與已排定工作段的關係

順序依 `docs/roadmap.md`：…→ s2e／s2f／s2g／s2h → s3b → s3c 會計 → s4p 生產與採購 → ct1 合約與週期開票 → HRM → 階段 5（含專案與工時）。

- **s2e（訂單到出貨）**：專案參照訂單（G6）；「訂單確認自動建專案」是 Odoo 招牌流程，可在專案設計時評估用事件或單據流做（推測）。
- **s3c（會計，含發票）**：依工時開票與利潤分析（G7、G10）的前置。沒有發票就先只做「已核可工時彙總」，不開票。
- **ct1（合約與週期開票）**：合約型專案（月費、維護）走 ct1；工時型專案走 G7。兩者別重疊：**ct1 管固定週期金額，G7 管實績時數。**需要的邊界問題：包時數的維護合約（合約含 N 小時、超過另計）屬哪邊（推測，兩邊都可能涉及，列入問題）。
- **HRM**：員工主檔與時薪成本、請假；費用報支掛專案（G9）；工時填寫者的身分是 Formwright 使用者還是 HRM 員工要先決定（見 §6）。
- **簽核**：工時送簽核（核准）可以直接用現有簽核，不必做 Odoo EE 的驗證流程。
- **看板與日曆（階段 5）**：專案狀態分欄看板；與專案同在階段 5，建議專案應用程式與「看板」同批設計。
- **s2d 共用主檔**：客戶用往來對象；品項要有服務型（s2d-2 併入品項實物／服務類型），開票時才有「工時品項」可用。

## 5. 差異與設計啟示

1. **vtiger 是附屬、Odoo 是核心**：vtiger 專案只是掛在 CRM 客戶與工單旁的三個平面模組（手填進度與工時）；Odoo 的專案是整個任務系統的容器，任務、工時、銷售、利潤分析都經專案與分析帳戶串起來。Formwright 的任務已經是核心能力（掛在任何記錄），所以專案應該像 vtiger 一樣是**一個普通模組**，不需要像 Odoo 那樣把任務改成屬於專案。
2. **Odoo 把大量功能做成可選開關**（階段、里程碑、相依、週期各自開關），符合「業務政策不寫死」；建議專案應用程式的階段、里程碑、工時計費等也是模組內可選的欄位與政策，不寫死。
3. **工時是 Odoo 專案的價值中心**（計時、核准、依工時開票、利潤分析全圍繞它），vtiger 開源版幾乎沒有。對 50 人以下服務業（設計、顧問、軟體、工程）工時加計費是最常見需求，所以工時要做，但要做簡單。
4. **Odoo 的 EE 差異主要在「體驗」**（Gantt、週格輸入、計時器、核准流程、預算）；資料模型的骨幹（專案、任務、里程碑、相依、工時行、分析帳戶）CE 都有。Formwright 已有 Gantt、簽核，所以 EE 體驗的差距比較小。
5. 「依工時開票」實質是：工時行（可計費）→ 彙總 → 單據明細（單價來自品項或客戶約定）→ 發票。Odoo 靠銷貨訂單的服務品項串接。Formwright 缺的是單據流如何把一批工時變成單據明細（推測可用既有單據轉換加彙總，**未驗證現有單據轉換能否彙總多筆來源**）。

## 6. 建議的「簡單版第一版」範圍

原則：跨產業常用、不重做核心已有的、不寫死業務政策。

### 6.1 第一版納入

1. **專案模組**（動態模組）
   - 欄位：名稱、編號、客戶（往來對象）、專案經理（User）、起迄日、預估時數、預算金額（選填）、狀態（狀態機：規劃、進行中、暫停、完成、取消，可改）、說明。
   - 任務直接掛專案記錄，使用核心任務區塊、我的任務、看板、（st1、gt1 上線後）子任務與甘特。
   - 理由：這是最小單位；vtiger 也是從這裡開始。
2. **工時紀錄模組**
   - 欄位：日期、填寫者（User）、專案（參照）、任務（選填；欄位型別未驗證，見 G4）、時數、可計費（是／否）、備註。
   - 時數單位與一天上限、是否可補填過去日期等一律走**政策設定**，不寫死。
   - 工時送簽核用既有簽核（可選，套件內附預設流程但可關）。
3. **內建報表**：工時依專案／人／月彙總、預估時數對實際時數。用報表設計器現成能力。
4. **專案範本＝任務規則的手動套用**：套件預設附一兩個示範範本（例如「網站案」「顧問案」），客戶自己改。不另做複製專案功能。
5. **範本套件附 Demo 資料**，沿用既有 Demo 公司。

### 6.2 第一版不納入（列第二版候選）

- 里程碑獨立模組：先用甘特圖「只有到期日顯示菱形」的任務當里程碑感；等使用者確認有「依里程碑開票」需求再加。
- 任務相依、任務優先度、任務上的工時、週期任務、Email 與推播：已決定不做，維持。
- 依工時開票、專案利潤分析：等 s3c 發票與 ct1 設計落地後，作為專案應用程式的**第二批**（G7、G10）。第一版只在工時紀錄預留「已開票」旗標欄或狀態，不做自動建單。
- 費用報支掛專案：等 HRM。
- 客戶入口：Formwright 目前無外部使用者，不做。
- 工時計時器、週格輸入：只做一般列表加單筆表單；週格與手機快速輸入是體驗優化，列候選。
- 專案階段看板：跟階段 5 的「看板」一起，第一版用專案狀態＋狀態機，看板上線後自動可用。

### 6.3 取捨理由

- 只加兩個模組，其餘全用核心，避免重做任務。
- 工時一定不掛核心任務（已決定不做），獨立模組掛專案，反而更貼近 vtiger 的簡單模型，也讓核心不必新增欄位。
- 開票與利潤分析依賴 s3c 與 ct1，在它們之前設計容易返工。
- 政策（單位、上限、補填）放設定，符合「業務政策不寫死」。

## 7. 需要使用者裁決的問題

1. 工時要不要能選到「任務」？選了就要核心新增「參照任務」欄位型別（或工時掛在任務上的專屬能力）；不選則只掛專案，工時分析到專案層。
2. 里程碑第一版要不要做？要的話，是做成專案下的獨立明細模組，還是用甘特的「只有到期日」任務代替？
3. 工時要不要走簽核？走既有簽核（可選）或第一版完全不核准。
4. 填工時的人是 Formwright 使用者（User 欄位）還是 HRM 員工？影響專案先於 HRM 的依賴關係與時薪成本來源。
5. 依工時開票排第一批還是等 s3c 之後的第二批？以及包時數維護合約（含 N 小時）歸 ct1 還是專案。
6. 專案範本只用任務規則手動套用，是否足夠？或要求「複製整個專案」？
7. 專案上線時是否同時要做專案階段看板，還是等階段 5 的通用看板？

## 8. 來源清單

vtiger：
- https://github.com/vtiger-crm/vtigercrm （`packages/vtiger/optional/Projects.zip` 與其中的 Project、ProjectMilestone、ProjectTask `manifest.xml`；本地解開讀取）
- https://help.vtiger.com/article/117252125-What-are-Projects （403，只用搜尋摘要）
- https://help.vtiger.com/article/147822916-Project-Management-Overview （403）
- https://help.vtiger.com/article/156170913-User-Guide-for-Project-Management （403，只用搜尋摘要）
- https://help.vtiger.com/article/147105248-Manage-Projects-with-Gantt-Chart （403，只用搜尋摘要）
- https://help.vtiger.com/faq/164509511-Why-is-my-Gantt-chart-empty-even-after-adding-a-project-and-milestones （搜尋摘要）
- https://help.vtiger.com/faq/154154336-What-are-the-important-fields-in-Project-milestones （僅見標題，內容未取得）
- https://help.vtiger.com/article/139843122-Time-Tracking-using-Timelogs 、https://help.vtiger.com/faq/114131685-How-can-I-add-a-timelog-to-a-project-task 、https://help.vtiger.com/faq/146822146-How-do-I-log-time-for-a-billable-task （搜尋摘要）
- https://help.vtiger.com/faq/114137898-How-do-I-add-an-Internal-Ticket-to-a-Project-Task （搜尋標題）
- https://two.vtiger.com/features/project-management/ （403）
- https://dhtmlx.com/blog/dhtmlxgantt-in-vtiger-crm/

Odoo：
- https://github.com/odoo/odoo/tree/18.0/addons （`project`、`hr_timesheet`、`sale_project`、`sale_timesheet`、`project_account` 的 `__manifest__.py`；`project/models/project_project.py`、`project_task.py`、`project_milestone.py`、`project_task_type.py`；`project/security/project_security.xml`；`hr_timesheet/models/hr_timesheet.py`；`sale_project/models/product_template.py`、`sale_order_line.py`；`sale_timesheet/models/product_template.py`）
- https://github.com/odoo/odoo/tree/17.0/addons （關鍵欄位開關比對）
- https://www.odoo.com/documentation/18.0/applications/services/project.html
- https://www.odoo.com/documentation/18.0/applications/services/project/project_management.html
- https://www.odoo.com/documentation/18.0/applications/services/project/project_management/project_profitability.html
- https://www.odoo.com/documentation/18.0/applications/services/project/tasks/task_dependencies.html
- https://www.odoo.com/documentation/18.0/applications/sales/sales/invoicing/time_materials.html
- https://www.odoo.com/documentation/18.0/applications/services/timesheets.html
- https://www.odoo.com/app/project-features
- https://www.cybrosys.com/blog/what-are-the-differences-between-odoo-18-community-enterprise-in-project （第三方，內部矛盾，僅輔助）
- https://www.cybrosys.com/blog/differences-between-odoo-18-community-enterprise-in-timesheet （第三方，與原始碼有衝突，僅輔助）
- https://apps.odoo.com/apps/modules/18.0/timesheet_grid_ce （第三方 CE 週格外掛，佐證 CE 缺 grid）
- 嘗試但 404：https://www.odoo.com/documentation/18.0/applications/services/project/tasks/sub_tasks.html 、.../project_management/project_templates.html

Formwright（本機）：
- `docs/feature.md`
- `docs/roadmap.md`
- `docs/superpowers/specs/2026-11-10-task-management-design.md`
- `docs/superpowers/specs/2026-11-13-task-board-reports-gantt-design.md`


## 使用者裁決（2026-11-17）

1. 工時可選任務：核心補「參照任務」欄位型別。
2. 里程碑：做成專案明細（名稱、預定日、是否達成、金額，金額預留給依里程碑開票）。
3. 工時核准：可選，套件附停用的簽核流程，預設不簽核。
4. 工時的人：用 HRM 員工（推翻 erp-data-model 的主控判斷），專案依賴 HRM。
5. 依工時開票：專案第一版就做；包時數維護合約歸 ct1 合約應用程式，讀工時計算超時。
6. 專案範本：「複製為新專案」，帶里程碑與任務、日期依新開始日平移。
7. 專案看板：用階段 5 的通用記錄看板，排在專案之前，不寫專屬畫面。

下一步：用 lifecycle:spec 寫專案與工時設計文件（含 ERP 對照、各開關組合、可寫死清單）。
