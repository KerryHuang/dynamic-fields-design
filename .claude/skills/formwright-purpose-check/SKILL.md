---
name: formwright-purpose-check
description: Use when designing, planning, reviewing, or about to merge a Formwright feature or app package — to check it against the project's purpose (dynamic module design system, no hardcoded business policy, works with no apps installed, ERP conventions).
---

# Formwright 宗旨檢查

宗旨與鐵則見 `AGENTS.md`。這份是逐項檢查清單，設計文件定稿前、實作計畫寫完後、審查時各跑一次，結果寫進設計文件或審查報告。

## 檢查項

1. **機制還是政策？** 每個新行為歸類：平台機制（可寫死）或業務政策（要可設定）。政策列出設定放哪、誰能改、預設值多少。
2. **寫死掃描**（審查 diff 時）：

   ```bash
   git diff origin/main...HEAD -- poc/backend/src poc/frontend/src \
     | grep -E '^\+.*(Key|key)\s*===?\s*["'"'"'][a-z]+-|^\+.*const \w+ (Max|Min|Default)|^\+.*readonly .*(Max|Min|Default)|^\+.*(MAX|MIN|DEFAULT)_[A-Z_]+\s*='
   ```

   每筆命中判斷：是業務政策就改成可設定；是完整性守衛、安全檢查、技術限制就留下，並列進「可寫死清單」。
3. **單一來源**：同一個上限或預設值有沒有在 Domain、Application、前端各寫一份？
4. **兩個應用程式**：這個能力有沒有兩個不相干的應用程式用得上？只有一個用得上，就是在替它寫特例。
5. **空系統**：不開任何應用程式時，這個功能還能用、不報錯？
6. **套件只用定義組成**：應用程式需要的東西，是用模組、欄位、規則、流程、報表、權限組出來的，還是在程式裡替它開了後門？
7. **ERP 對照**：設計文件有「ERP 對照」一節；新應用程式盤點過和既有應用程式重複的主檔與單據（訂單、客戶、品項、出貨單），主檔全系統一份。
8. **權限 fail closed、驗證不只做在前端、沒有執行期 DDL。**

## 輸出

逐項寫「通過／不通過／不適用」加一句理由；不通過的附建議改法。業務政策被寫死，在審查裡算 must-fix。
