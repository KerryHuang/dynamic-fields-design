// 純任務版：客製設計需求（主管接案 → 業務 → 設計上傳圖 → 客戶意見來回 → 主管確認結案）。
// 旁白稿已由使用者審過（2026-11-16），改旁白要重新送審。被錄的站：本機全新資料庫（task-management/reset.sh，埠 5191，開啟上傳）。
// 設計圖是 make-files.mjs 產生的虛構示意圖。
const API = '/api/v3/dynamic'
const OPP = '客製控制盒外殼'
const T1 = '承接客製需求：控制盒外殼', T2 = '繪製控制盒外殼設計圖', T3 = '結案確認：客製控制盒外殼'
const day = (n) => { const d = new Date(); d.setDate(d.getDate() + n); return d.toISOString().slice(0, 10) }
let oppUrl = ''

const login = async (a, name) => {
  await a.click(a.p.getByRole('button', { name, exact: true }))
  await a.p.getByRole('button', { name: '登出' }).waitFor()
}
const logout = async (a) => {
  if (await dlg(a).count()) await a.click(dlg(a).getByRole('button', { name: '關閉', exact: true }))
  await a.click(a.p.getByRole('button', { name: '登出' }))
  await a.p.getByText('Demo 帳號（點一下直接登入）').waitFor()
}
// 換人：在背景用 API 換身分後直接開目標頁（省去登出、點帳號約 3 秒；畫面右上角會換成新的人名）
const switchTo = async (a, userId, path) => {
  const origin = new URL(a.p.url()).origin
  await a.p.request.post(origin + API + '/auth/logout')
  await a.p.request.post(origin + API + '/auth/demo-login', { data: { userId } })
  await a.p.goto(origin + (path || oppUrl))
  await a.p.getByRole('button', { name: '登出' }).waitFor()
}
const menu = (a, text) => a.p.locator('.q-drawer').getByText(text, { exact: true })
const dlg = (a) => a.p.locator('.q-dialog')
const pickIn = async (a, scope, label, option) => {
  await a.click(scope.getByLabel(label))
  await a.click(a.p.locator('.q-menu').last().getByText(option, { exact: true }))
}
const openOpp = async (a) => {
  await a.click(menu(a, '商機'))
  await a.click(a.p.locator('tbody tr').filter({ hasText: OPP }).first())
  await a.p.getByRole('button', { name: '新增任務' }).waitFor()
}
const taskRow = (a, title) => a.p.locator('.q-page .q-item').filter({ hasText: title }).first()
const attach = async (a, file) => {
  await a.upload(dlg(a).getByRole('button', { name: /附加檔案/ }), `files/${file}`)
}
const comment = async (a, mention, text) => {
  const box = a.p.getByLabel('輸入留言，用 @ 提到同事')
  await a.type(box, '@' + mention.slice(0, 1))   // 多打一個字縮小清單，否則目標可能在清單可視範圍外
  await a.click(a.p.locator('.q-menu').last().getByText(mention, { exact: true }))
  await a.type(box, ` ${text}`)
  await a.click(dlg(a).getByRole('button', { name: '送出', exact: true }))
  await dlg(a).getByText(text).waitFor()
}
const newTask = async (a, title, assignee, due) => {
  await a.click(a.p.getByRole('button', { name: '新增任務' }))
  await a.fill(dlg(a).getByLabel('標題'), title)
  await pickIn(a, dlg(a), '負責人', assignee)
  await a.fill(dlg(a).getByLabel('到期日'), due)
  await a.click(dlg(a).getByRole('button', { name: '建立', exact: true }))
  await taskRow(a, title).waitFor()
}

import { azureTts } from '../azure-tts.mjs'

export default {
  tts: azureTts,
  name: 'design-by-tasks',
  voice: 'zh-TW-HsiaoChenNeural',
  viewport: { width: 1280, height: 720 },
  timeoutMs: 10000,
  setup: async (page, actor, url) => {
    // 錄影前準備（不錄進畫面）：以業務主管建一筆新商機，負責業務是業務一
    const base = new URL(url).origin
    await page.request.post(base + API + '/auth/demo-login', { data: { userId: 'manager01' } })
    const r = await page.request.post(base + API + '/modules/crm-opportunity/records',
      { data: { values: { f_180: OPP, f_181: 53, f_186: 'sales01' } } })
    if (!r.ok()) throw new Error('建立商機失敗 ' + r.status() + ' ' + (await r.text()))
    oppUrl = `/dynamic/crm-opportunity/${(await r.json()).id}`
    await page.request.post(base + API + '/auth/logout')
    await page.goto(url)
    await page.getByText('Demo 帳號（點一下直接登入）').waitFor()
  },
  segments: [
    { text: '這支影片示範用任務管理跟一個客製設計需求：從主管接案，到確認結案。',
      run: async (a) => { await a.pause(2000) } },
    { text: '業務主管接到客戶需求，在這個客戶的商機上新增任務，指派給業務一。',
      run: async (a) => {
        await login(a, '業務主管')
        await openOpp(a)
        await a.click(a.p.getByRole('button', { name: '新增任務' }))
        await a.type(dlg(a).getByLabel('標題'), T1)
      } },
    { text: '任務寫明需求與期限，建立後業務一馬上收到通知。',
      run: async (a) => {
        await a.fill(dlg(a).getByLabel('說明'), '鋁合金外殼、正面指示燈，客戶希望兩週內看到初稿')
        await pickIn(a, dlg(a), '負責人', '業務一')
        await a.fill(dlg(a).getByLabel('到期日'), day(3))
        await a.click(dlg(a).getByRole('button', { name: '建立', exact: true }))
        await taskRow(a, T1).waitFor()
      } },
    { text: '業務一從鈴鐺打開任務，確認客戶要什麼。',
      run: async (a) => {
        await switchTo(a, 'sales01', '/')
        await a.click(a.p.getByRole('button', { name: '通知' }))
        await a.click(a.p.locator('.q-menu').last().getByText(T1).first())
        await a.hover(dlg(a).getByLabel('說明'))
      } },
    { text: '接著在同一筆商機新增任務，請設計人員繪製設計圖。',
      run: async (a) => {
        await a.click(dlg(a).getByRole('button', { name: '關閉', exact: true }))
        await a.p.goto(new URL(oppUrl, a.p.url()).href)
        await newTask(a, T2, '設計人員', day(7))
      } },
    { text: '設計人員畫好後，在任務留言上傳第一版設計圖，並 @業務一。',
      run: async (a) => {
        await switchTo(a, 'designer01', '/tasks')
        await a.click(a.p.getByText(T2, { exact: true }).first())
        await attach(a, '外殼設計-v1.png')
        await comment(a, '業務一', '第一版完成')
      } },
    { text: '業務一拿圖給客戶看，客戶的意見記在 CRM 的互動紀錄。',
      run: async (a) => {
        await switchTo(a, 'sales01')
        await a.click(a.p.getByRole('tab', { name: /互動紀錄/ }))
        await a.click(a.p.locator('.q-page').getByRole('link', { name: /新增/ }))
        await a.fill(a.p.getByLabel('主旨'), '客戶看第一版設計圖')
        await a.fill(a.p.getByLabel('內容'), '指示燈改到右上角，側面加散熱槽')
        await a.click(a.p.getByRole('button', { name: '儲存', exact: true }))
        await a.p.waitForURL(/crm-activity\/\d+$/)
      } },
    { text: '客戶要調整，業務在任務留言 @設計人員，附上客戶標註過的圖。',
      run: async (a) => {
        await a.p.goto(new URL(oppUrl, a.p.url()).href)
        await a.click(taskRow(a, T2).getByText(T2, { exact: true }))
        await attach(a, '客戶標註-v1.png')
        await comment(a, '設計人員', '客戶標註如附件')
      } },
    { text: '設計人員依標註修改，上傳第二版，再交回業務。',
      run: async (a) => {
        await switchTo(a, 'designer01', '/')
        await a.click(a.p.getByRole('button', { name: '通知' }))
        await a.click(a.p.locator('.q-menu').last().getByText(T2).first())
        await attach(a, '外殼設計-v2.png')
        await comment(a, '業務一', '第二版已修改')
      } },
    { text: '客戶確認第二版，業務把設計任務標成完成。',
      run: async (a) => {
        await switchTo(a, 'sales01')
        await taskRow(a, T2).waitFor()
        await a.click(taskRow(a, T1).getByRole('checkbox'))
        await a.click(taskRow(a, T2).getByRole('checkbox'))
      } },
    { text: '再新增一個結案確認任務，指派給業務主管。',
      run: async (a) => { await newTask(a, T3, '業務主管', day(1)) } },
    { text: '主管在商機上看得到整串來回，留言同意後把任務標成完成，案子結案。',
      run: async (a) => {
        await switchTo(a, 'manager01')
        await a.click(taskRow(a, T3).getByText(T3, { exact: true }))
        const box = a.p.getByLabel('輸入留言，用 @ 提到同事')
        await a.type(box, '同意結案')
        await a.click(dlg(a).getByRole('button', { name: '送出', exact: true }))
        await dlg(a).getByText('同意結案').waitFor()
        await a.click(dlg(a).getByRole('button', { name: '關閉', exact: true }))
        await a.click(taskRow(a, T3).getByRole('checkbox'))
      } },
    { text: '誰做了什麼、什麼時候、附了哪張圖，都留在任務和留言裡。',
      run: async (a) => {
        await a.click(taskRow(a, T2).getByText(T2, { exact: true }))
        await a.hover(dlg(a).getByText('外殼設計-v2.png').first())
      } },
  ],
}
