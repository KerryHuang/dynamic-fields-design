// 任務管理示範影片（Formwright）。旁白稿已由使用者審過（2026-11-16），改旁白要重新送審。
// 被錄的站：本機全新資料庫（server.sh；實際埠 5180，ASPNETCORE_URLS 不生效），Demo 資料以產生當天為基準。
const login = async (a, name) => {
  await a.click(a.p.getByRole('button', { name, exact: true }))
  await a.p.waitForURL((u) => !u.pathname.startsWith('/login'))
  await a.p.getByRole('button', { name: '登出' }).waitFor()
}
const logout = async (a) => {
  await a.click(a.p.getByRole('button', { name: '登出' }))
  await a.p.getByText('Demo 帳號（點一下直接登入）').waitFor()
}
const menu = (a, text) => a.p.locator('.q-drawer').getByText(text, { exact: true })
const pick = async (a, label, option) => {
  await a.click(a.p.getByLabel(label))
  await a.click(a.p.locator('.q-menu').last().getByText(option, { exact: true }))
}

export default {
  name: 'task-management',
  base: 'http://127.0.0.1:5180/',
  voice: 'zh-TW-HsiaoChenNeural',
  viewport: { width: 1280, height: 720 },
  timeoutMs: 10000,
  setup: async (page, actor, url) => {
    await page.goto(url)
    await page.getByText('Demo 帳號（點一下直接登入）').waitFor()
  },
  segments: [
    { text: '這支影片示範任務管理：行銷活動一建立，系統就自動派工給三位業務。',
      run: async (a) => { await a.pause(2000) } },
    { text: '先看設定。管理員在行銷活動模組的任務規則，設好建立活動時自動產生三個任務。',
      run: async (a) => {
        await login(a, '系統管理員')
        await a.p.goto(new URL('/system/dynamic-modules/crm-campaign', a.p.url()).href)
        await a.click(a.p.getByRole('tab', { name: '任務規則' }))
        await a.hover(a.p.getByText('行銷活動派工', { exact: true }))
      } },
    { text: '每個任務的負責人，取活動上的人員欄位；到期日用開始日往前或往後推算。',
      run: async (a) => {
        await a.click(a.p.getByText('行銷活動派工', { exact: true }))
        const tasks = a.p.getByText('要產生的任務')
        await tasks.waitFor()
        await tasks.scrollIntoViewIfNeeded()
        await a.hover(a.p.getByLabel('到期日偏移（天）').first())
      } },
    { text: '展場佈置多了一個條件，只有類型是展覽才會產生。',
      run: async (a) => {
        const cond = a.p.getByText('這個任務的條件').nth(1)
        await cond.scrollIntoViewIfNeeded()
        await a.click(cond)
        await a.pause(800)
      } },
    { text: '現在換業務一，新增一場秋季自動化展。',
      run: async (a) => {
        await a.click(a.p.locator('.q-dialog').getByRole('button', { name: '關閉', exact: true }))
        await logout(a)
        await login(a, '業務一')
        await a.click(menu(a, '行銷活動'))
        await a.click(a.p.locator('.q-page').getByRole('link', { name: /新增/ }))
        await a.fill(a.p.getByLabel('名稱'), '秋季自動化展')
      } },
    { text: '類型選展覽，填上開始和結束日期，三個負責人分別是業務一、業務二、業務三。',
      run: async (a) => {
        await pick(a, '類型', '展覽')
        await a.fill(a.p.getByLabel('開始日'), '2027-03-10')
        await a.fill(a.p.getByLabel('結束日'), '2027-03-12')
        await pick(a, '規劃負責人', '業務一')
        await pick(a, '佈置負責人', '業務二')
      } },
    { text: '存檔後，任務區塊馬上出現三個任務，負責人和到期日都排好了。',
      run: async (a) => {
        await pick(a, '回饋負責人', '業務三')   // 從上一段移來，避免上一段操作超過語音
        await a.click(a.p.getByRole('button', { name: '儲存', exact: true }))
        const setup = a.p.getByText('展場佈置', { exact: true })
        await setup.waitFor()
        await a.hover(setup)
      } },
    { text: '換業務二登入，鈴鐺跳出通知：展場佈置指派給你。',
      run: async (a) => {
        await logout(a)
        await login(a, '業務二')
        await a.click(a.p.getByRole('button', { name: '通知' }))
        await a.hover(a.p.locator('.q-menu').last().getByText('展場佈置').first())
      } },
    { text: '打開任務，在留言輸入 @，選業務三，請他幫忙確認攤位。',
      run: async (a) => {
        await a.click(a.p.locator('.q-menu').last().getByText('展場佈置').first())
        const box = a.p.getByLabel('輸入留言，用 @ 提到同事')
        await box.waitFor()
        await a.type(box, '@業務三')
        await a.click(a.p.locator('.q-menu').last().getByText('業務三', { exact: true }))
        await a.type(box, ' 攤位尺寸麻煩幫忙確認')
        await a.click(a.p.locator('.q-dialog').getByRole('button', { name: '送出', exact: true }))
        await a.p.locator('.q-dialog').getByText('攤位尺寸麻煩幫忙確認').waitFor()
      } },
    { text: '業務三登入就收到「你被提到」，我的任務也能切到提到我的。',
      run: async (a) => {
        await a.click(a.p.locator('.q-dialog').getByRole('button', { name: '關閉', exact: true }))
        await logout(a)
        await login(a, '業務三')
        await a.click(a.p.getByRole('button', { name: '通知' }))
        await a.hover(a.p.locator('.q-menu').last().getByText('你被提到').first())
        await a.p.keyboard.press('Escape')
        await a.click(menu(a, '我的任務'))
        await a.click(a.p.getByRole('tab', { name: '提到我的' }))
      } },
    { text: '最後是業務主管，在部屬的任務分頁，看得到整個團隊手上的工作。',
      run: async (a) => {
        await logout(a)
        await login(a, '業務主管')
        await a.click(menu(a, '我的任務'))
        await a.click(a.p.getByRole('tab', { name: '部屬的任務' }))
        await a.hover(a.p.getByText('整理名片', { exact: true }))
      } },
    { text: '主管查看時只能改負責人和留言，方便重新分派。',
      run: async (a) => {
        await a.click(a.p.getByText('整理名片', { exact: true }))
        await a.hover(a.p.getByText('你以主管身分查看，只能改負責人與留言'))
      } },
    { text: '從自動派工、留言到主管追蹤，都在同一個系統完成。',
      run: async (a) => { await a.pause(1200) } },
  ],
}
