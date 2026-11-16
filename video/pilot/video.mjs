// 入門片：不寫程式建第一個模組（新版：選單分區、列表顯示、下拉欄位、設計頁分頁導覽、詳情頁歷程）。
// 製作：cd video && node <demo-video 引擎>/demo-video.mjs build pilot/video.mjs（錄影環境見 video/env.mjs）
const btn = (a, name) => a.p.getByRole('button', { name, exact: true })
const tab = (a, name) => a.p.getByRole('tab', { name, exact: true })
const dlg = (a) => a.p.locator('.q-dialog').last()

export default {
  name: 'pilot',
  viewport: { width: 1280, height: 720 },
  segments: [
    {
      text: '這支影片示範，怎麼在 Formwright 表匠建立第一個模組，全程不用寫程式。先用系統管理員登入，打開動態模組設計。',
      run: async (a) => {
        await a.pause(4000)
        await a.click(btn(a, '系統管理員'))
        await a.click(a.p.locator('.q-drawer').getByText('動態模組設計', { exact: true }))
        await a.p.waitForURL('**/system/dynamic-modules')
      },
      expect: (a) => btn(a, '新增模組'),
    },
    {
      text: '在模組清單按新增模組，填上代碼和名稱，選單分區填設備管理，然後按建立。',
      run: async (a) => {
        await a.click(btn(a, '新增模組'))
        await a.type(dlg(a).getByLabel('代碼'), 'inspection')
        await a.type(dlg(a).getByLabel('名稱 (zh-TW)'), '設備巡檢')
        await a.type(dlg(a).getByLabel('選單分區 (zh-TW)'), '設備管理')
        await a.click(btn(a, '建立'))
      },
      expect: (a) => btn(a, '新增欄位'),
    },
    {
      text: '系統已經備好名稱欄位。按新增欄位，加入巡檢日期，型別選日期，打開預設今天和列表顯示。',
      run: async (a) => {
        await a.click(btn(a, '新增欄位'))
        await a.type(dlg(a).getByLabel('欄位名稱 (zh-TW)'), '巡檢日期')
        await a.select(dlg(a).getByLabel('欄位型別'), '日期')
        await a.click(dlg(a).getByRole('switch', { name: '預設今天' }))
        await a.click(dlg(a).getByRole('switch', { name: '列表顯示' }))
        await a.click(btn(a, '儲存'))
        await a.p.locator('.q-dialog').filter({ hasText: '欄位名稱 (zh-TW)' }).waitFor({ state: 'hidden' })
      },
      expect: '巡檢日期',
    },
    {
      text: '再加一個巡檢結果，型別選下拉，一樣打開列表顯示，然後儲存。',
      run: async (a) => {
        await a.click(btn(a, '新增欄位'))
        await a.type(dlg(a).getByLabel('欄位名稱 (zh-TW)'), '巡檢結果')
        await a.select(dlg(a).getByLabel('欄位型別'), '下拉')
        await a.click(dlg(a).getByRole('switch', { name: '列表顯示' }))
        await a.click(btn(a, '儲存'))
      },
      expect: (a) => dlg(a).getByRole('button', { name: '新增選項' }),
    },
    {
      text: '存檔後畫面直接帶到選項設定，加入正常和異常兩個選項，再儲存。',
      run: async (a) => {
        for (const o of ['正常', '異常']) {
          await a.type(dlg(a).locator('input[aria-label="選項名稱 (zh-TW) *"]:not([value]), input[aria-label="選項名稱 (zh-TW) *"][value=""]'), o)
          await a.click(dlg(a).getByRole('button', { name: '新增選項' }))
        }
        await a.click(btn(a, '儲存'))
      },
    },
    {
      text: '切到預覽，可以先看表單實際的樣子。',
      run: async (a) => {
        await a.click(tab(a, '預覽'))
        await a.pause(800)
      },
    },
    {
      text: '設計頁還有狀態機、簽核流程和權限等分頁，送簽、鎖定和誰能看，都在這裡設定。',
      run: async (a) => {
        for (const t of ['狀態機', '簽核流程', '權限']) {
          await a.click(tab(a, t))
          await a.pause(1200)
        }
      },
    },
    {
      text: '回到模組清單按啟用，左邊選單馬上多了設備管理分區和設備巡檢。',
      run: async (a) => {
        await a.click(a.p.locator('a:has-text("arrow_back")'))
        const row = a.p.locator('tr').filter({ hasText: 'inspection' })
        await a.click(row.getByRole('button', { name: '啟用', exact: true }))
        await a.hover(a.p.locator('.q-drawer').getByText('設備巡檢', { exact: true }))
      },
    },
    {
      text: '點進去按新增，填上設備名稱，巡檢日期已經帶好今天，結果選正常，然後儲存。',
      run: async (a) => {
        await a.click(a.p.locator('.q-drawer').getByText('設備巡檢', { exact: true }))
        await a.click(a.p.locator('.q-page a, .q-page button').filter({ hasText: /^add\s*新增$/ }))
        await a.type(a.p.getByLabel(/^名稱/), '一號沖床')
        await a.select('巡檢結果', '正常')
        await a.click(btn(a, '儲存'))
      },
      expect: '歷程',
    },
    {
      text: '存好就進到詳情頁，下方的歷程，記下了誰在什麼時候建立這筆資料。',
      run: async (a) => {
        await a.hover(a.p.getByText('歷程', { exact: true }).first())
      },
    },
    {
      text: '這樣就完成了一個能實際使用的模組。規則、任務和簽核，都可以接著設定。',
      run: async (a) => {
        await a.pause(300)
      },
    },
  ],
}
