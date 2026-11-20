import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import GenericSelect from 'src/components/framework/GenericSelect.vue';
import enUS from 'shared/i18n/en-US/ui.json';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import { bodyOf, HttpFail, mockHttp, type Call } from 'src/test-utils/mock-http';
import TaskDialog from './TaskDialog.vue';
import { commentWire, taskDetailWire, USERS } from './task-fixtures';

interface Opts {
  task?: Record<string, unknown>;
  comments?: unknown[];
  settings?: Record<string, unknown>;
  maxFiles?: number;
  maxBytes?: number;
  candidates?: unknown[];
  taskStatus?: number;
  commentFail?: HttpFail;
  withdrawFail?: HttpFail;
  locale?: 'zh-TW' | 'en-US';
  patchReply?: unknown;
  onPatch?: (c: Call) => unknown;
  subtaskReply?: unknown;
}
// The toast itself is covered in use-moved-out-notice.vitest.spec.ts; here only the hand-off matters.
const showNotice = vi.hoisted(() => vi.fn());
vi.mock('./use-moved-out-notice', () => ({ useMovedOutNotice: () => ({ show: showNotice }) }));

let calls: Call[];
const body = () => document.body;

async function mountDialog(o: Opts = {}) {
  setActivePinia(createPinia());
  calls = mockHttp((c) => {
    if (c.url === '/tasks/5' && c.method === 'get') return o.taskStatus ? new HttpFail(o.taskStatus, { message: '找不到' }) : taskDetailWire(o.task);
    if (c.url === '/tasks/5/comments' && c.method === 'get') return o.comments ?? [];
    if (c.url === '/tasks/5/comments' && c.method === 'post') return o.commentFail ?? commentWire();
    if (/\/withdraw$/.test(c.url)) return o.withdrawFail ?? commentWire({ withdrawn: true });
    if (c.url === '/tasks/5/subtasks' && c.method === 'post') return o.subtaskReply ?? taskDetailWire({ id: 50 });
    if (c.url === '/tasks/5' && c.method === 'patch' && o.onPatch) return o.onPatch(c);
    if (c.url === '/tasks/5' && c.method === 'patch') return o.patchReply ?? taskDetailWire(o.task);
    if (c.url === '/users') return USERS;
    if (c.url === '/users/mention-candidates') return o.candidates ?? [USERS[1]];
    if (c.url === '/attachments/settings') return { uploadEnabled: true, maxBytes: 1048576, allowedExtensions: ['txt', 'pdf'], maxFilesPerField: 3, ...o.settings };
    if (c.url === '/policies/effective') return { values: { 'attachment.maxFilesPerComment': o.maxFiles ?? 5, 'attachment.maxFileBytes': o.maxBytes ?? 1048576 } };
    return {};
  });
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/:p(.*)*', component: { render: () => null } }] });
  const i18n = createI18n({ legacy: false, locale: o.locale ?? 'zh-TW', messages: { 'zh-TW': zhTW, 'en-US': enUS } });
  const w = mount(TaskDialog, {
    props: { taskId: 5 },
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], createPinia(), router, i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
  });
  await flushPromises();
  await new Promise((r) => setTimeout(r, 20));
  await flushPromises();
  return w;
}

describe('TaskDialog', () => {
  beforeEach(() => { document.body.innerHTML = ''; });
  afterEach(() => { document.body.innerHTML = ''; });

  it('看不到記錄時顯示「相關記錄（無權限查看）」且沒有連結', async () => {
    const w = await mountDialog({ task: { record: { accessible: false, deleted: false } } });
    const rec = body().querySelector('[data-testid="task-record"]')!;
    expect(rec.textContent).toContain('相關記錄（無權限查看）');
    expect(rec.querySelector('a')).toBeNull();
    w.unmount();
  });

  it('看得到記錄時連到 /dynamic/{moduleKey}/{recordId}；已刪除的顯示標題與已刪除', async () => {
    let w = await mountDialog();
    expect(body().querySelector('[data-testid="task-record"] a')!.getAttribute('href')).toBe('/dynamic/crm_customer/9');
    w.unmount();
    w = await mountDialog({ task: { record: { accessible: true, moduleKey: 'crm_customer', recordId: 9, title: '甲公司', deleted: true } } });
    const rec = body().querySelector('[data-testid="task-record"]')!;
    expect(rec.textContent).toContain('「甲公司」已刪除');
    expect(rec.querySelector('a')).toBeNull();
    w.unmount();
  });

  it('撤回的留言顯示「已撤回」，不顯示原文與附件', async () => {
    const w = await mountDialog({
      comments: [commentWire({ id: 2, body: undefined, withdrawn: true, attachments: [{ id: 1, fileName: 'x.pdf', contentType: 'application/pdf', size: 10 }] })],
    });
    const item = body().querySelector('[data-testid="comment-2"]')!;
    expect(item.textContent).toContain('此留言已撤回');
    expect(item.querySelector('[data-testid^="comment-attachment"]')).toBeNull();
    w.unmount();
  });

  it('系統記錄 assigneeChanged 顯示新舊負責人名稱', async () => {
    const w = await mountDialog({
      comments: [commentWire({ id: 3, kind: 'system', body: '', systemEvent: 'assigneeChanged', systemData: { from: 'sales01', to: 'sales02' }, mentions: [] })],
    });
    const item = body().querySelector('[data-testid="comment-3"]')!;
    expect(item.textContent).toContain('負責人從「業務一」改為「業務二」');
    w.unmount();
  });

  it('系統記錄 statusChanged 把狀態代碼翻成文字', async () => {
    const w = await mountDialog({
      comments: [commentWire({ id: 3, kind: 'system', body: '', systemEvent: 'statusChanged', systemData: { from: 'todo', to: 'inProgress' } })],
    });
    expect(body().querySelector('[data-testid="comment-3"]')!.textContent).toContain('狀態從「待辦」改為「進行中」');
    w.unmount();
  });

  it('系統記錄 subtaskAdded／subtaskDeleted 顯示子任務標題（C44）', async () => {
    const w = await mountDialog({
      comments: [
        commentWire({ id: 3, kind: 'system', body: '', systemEvent: 'subtaskAdded', systemData: { subtaskId: 8, subtaskTitle: '訂場地' } }),
        commentWire({ id: 4, kind: 'system', body: '', systemEvent: 'subtaskDeleted', systemData: { subtaskId: 9, subtaskTitle: '印海報' } }),
      ],
    });
    expect(body().querySelector('[data-testid="comment-3"]')!.textContent).toContain('新增了子任務「訂場地」');
    expect(body().querySelector('[data-testid="comment-4"]')!.textContent).toContain('刪除了子任務「印海報」');
    w.unmount();
  });

  it('系統記錄 bySubtask／byParent／subtasksAllDone 顯示狀態與標題（C44）', async () => {
    const w = await mountDialog({
      comments: [
        commentWire({ id: 5, kind: 'system', body: '', systemEvent: 'bySubtask', systemData: { from: 'todo', to: 'inProgress', subtaskId: 8, subtaskTitle: '訂場地' } }),
        commentWire({ id: 6, kind: 'system', body: '', systemEvent: 'byParent', systemData: { from: 'inProgress', to: 'cancelled', parentId: 1, parentTitle: '辦活動' } }),
        commentWire({ id: 7, kind: 'system', body: '', systemEvent: 'subtasksAllDone', systemData: { subtaskId: 9, subtaskTitle: '印海報' } }),
      ],
    });
    expect(body().querySelector('[data-testid="comment-5"]')!.textContent).toContain('因子任務「訂場地」變更，狀態從「待辦」改為「進行中」');
    expect(body().querySelector('[data-testid="comment-6"]')!.textContent).toContain('因父任務「辦活動」變更，狀態從「進行中」改為「已取消」');
    expect(body().querySelector('[data-testid="comment-7"]')!.textContent).toContain('子任務都已完成或取消，最後一個是「印海報」');
    w.unmount();
  });

  it('留言內容的 @{id} 顯示成名稱，已停用者加「（已停用）」，不認得的照原文', async () => {
    const w = await mountDialog({
      comments: [commentWire({
        id: 4, body: '請 @{sales01} 與 @{admin} 看，@{ghost}',
        mentions: [{ userId: 'sales01', displayName: '業務一', active: true }, { userId: 'admin', displayName: '管理員', active: false }],
      })],
    });
    const text = body().querySelector('[data-testid="comment-4"]')!.textContent!;
    expect(text).toContain('請 @業務一 與 @管理員（已停用） 看，@{ghost}');
    w.unmount();
  });

  it('留言輸入框有可讀名稱（aria-label），不只靠 placeholder', async () => {
    const w = await mountDialog();
    const ta = body().querySelector<HTMLTextAreaElement>('textarea[data-testid="mention-input"]')!;
    expect(ta.getAttribute('aria-label')).toBe('輸入留言，用 @ 提到同事');
    w.unmount();
  });

  it('在留言框輸入 @業 列出候選人，選定後送出的 body 是 "@{sales01} …"', async () => {
    const w = await mountDialog();
    const ta = body().querySelector<HTMLTextAreaElement>('textarea[data-testid="mention-input"]')!;
    ta.value = '請 @業';
    ta.setSelectionRange(ta.value.length, ta.value.length);
    ta.dispatchEvent(new Event('input'));
    await flushPromises();
    await new Promise((r) => setTimeout(r, 20));
    const q = calls.find((c) => c.url === '/users/mention-candidates');
    expect(q?.params).toEqual({ keyword: '業' });
    body().querySelector<HTMLElement>('[data-testid="mention-option-sales01"]')!.click();
    await flushPromises();
    expect(ta.value).toBe('請 @業務一 ');
    ta.value = '請 @業務一 確認';
    ta.dispatchEvent(new Event('input'));
    await flushPromises();
    body().querySelector<HTMLElement>('[data-testid="comment-send"]')!.click();
    await flushPromises();
    const post = calls.find((c) => c.url === '/tasks/5/comments' && c.method === 'post');
    expect(bodyOf(post)).toEqual({ body: '請 @{sales01} 確認' });
    w.unmount();
  });

  it('同名候選人以帳號區分，兩人都選時內容各存各的 @{userId}', async () => {
    const dup = [{ userId: 'wang01', displayName: '王小明' }, { userId: 'wang02', displayName: '王小明' }];
    const w = await mountDialog({ candidates: dup });
    const ta = body().querySelector<HTMLTextAreaElement>('textarea[data-testid="mention-input"]')!;
    const type = async (v: string) => {
      ta.value = v;
      ta.setSelectionRange(v.length, v.length);
      ta.dispatchEvent(new Event('input'));
      await flushPromises();
      await new Promise((r) => setTimeout(r, 20));
    };
    await type('@王');
    expect(body().querySelector('[data-testid="mention-option-wang01"]')!.textContent).toContain('wang01');
    expect(body().querySelector('[data-testid="mention-option-wang02"]')!.textContent).toContain('wang02');
    body().querySelector<HTMLElement>('[data-testid="mention-option-wang01"]')!.click();
    await flushPromises();
    await type(`${ta.value}@王`);
    body().querySelector<HTMLElement>('[data-testid="mention-option-wang02"]')!.click();
    await flushPromises();
    body().querySelector<HTMLElement>('[data-testid="comment-send"]')!.click();
    await flushPromises();
    const post = calls.find((c) => c.url === '/tasks/5/comments' && c.method === 'post');
    expect(bodyOf(post)).toEqual({ body: '@{wang01} @{wang02}' });
    w.unmount();
  });

  it('寬度 390 時對話框 maximized', async () => {
    const original = window.innerWidth;
    window.innerWidth = 390;
    window.dispatchEvent(new Event('resize'));
    await new Promise((r) => setTimeout(r, 80));
    const w = await mountDialog();
    expect(body().querySelector('.q-dialog__inner--maximized')).not.toBeNull();
    w.unmount();
    window.innerWidth = 1366;
    window.dispatchEvent(new Event('resize'));
    await new Promise((r) => setTimeout(r, 80));
    const w2 = await mountDialog();
    expect(body().querySelector('.q-dialog__inner--maximized')).toBeNull();
    w2.unmount();
    window.innerWidth = original;
  });

  it('任務不存在或看不到（404）時顯示提示，不卡在載入', async () => {
    const w = await mountDialog({ taskStatus: 404 });
    expect(body().querySelector('[data-testid="task-not-found"]')!.textContent).toContain('找不到這個任務');
    w.unmount();
  });

  it('404 時有關閉鈕，按下後發出 update:taskId null', async () => {
    const w = await mountDialog({ taskStatus: 404 });
    const btn = body().querySelector<HTMLElement>('[data-testid="task-dialog-close"]');
    expect(btn).not.toBeNull();
    btn!.click();
    await flushPromises();
    await new Promise((r) => setTimeout(r, 30));
    await flushPromises();
    expect(w.emitted('update:taskId')?.at(-1)).toEqual([null]);
    w.unmount();
  });

  it('載入失敗（非 404）時同樣有關閉鈕', async () => {
    const w = await mountDialog({ taskStatus: 500 });
    expect(body().querySelector('[data-testid="task-not-found"]')!.textContent).toContain('任務載入失敗');
    expect(body().querySelector('[data-testid="task-dialog-close"]')).not.toBeNull();
    w.unmount();
  });

  it('記錄標題被欄位權限隱藏（title 缺）時，連結顯示「記錄 #9」且可點', async () => {
    const w = await mountDialog({ task: { record: { accessible: true, moduleKey: 'crm_customer', recordId: 9, deleted: false } } });
    const a = body().querySelector('[data-testid="task-record"] a')!;
    expect(a.textContent!.trim()).toBe('記錄 #9');
    expect(a.getAttribute('href')).toBe('/dynamic/crm_customer/9');
    w.unmount();
  });

  it('記錄已刪除且標題被隱藏時顯示「記錄 #9」已刪除', async () => {
    const w = await mountDialog({ task: { record: { accessible: true, moduleKey: 'crm_customer', recordId: 9, deleted: true } } });
    expect(body().querySelector('[data-testid="task-record"]')!.textContent).toContain('「記錄 #9」已刪除');
    w.unmount();
  });

  it('凍結時顯示提示、欄位唯讀、沒有儲存／刪除鈕與留言框', async () => {
    const w = await mountDialog({ task: { frozen: true, canEdit: false, canReassign: false, canComment: false, canDelete: false } });
    expect(body().querySelector('[data-testid="task-frozen"]')!.textContent).toContain('相關記錄已鎖定');
    expect(body().querySelector<HTMLInputElement>('input[data-testid="task-title"]')!.readOnly).toBe(true);
    expect(body().querySelector('[data-testid="task-save"]')).toBeNull();
    expect(body().querySelector('[data-testid="task-delete"]')).toBeNull();
    expect(body().querySelector('textarea[data-testid="mention-input"]')).toBeNull();
    w.unmount();
  });

  it('清掉說明送 description:null', async () => {
    const w = await mountDialog();
    const d = body().querySelector<HTMLTextAreaElement>('textarea[data-testid="task-description"]')!;
    d.value = '';
    d.dispatchEvent(new Event('input'));
    await flushPromises();
    body().querySelector<HTMLElement>('[data-testid="task-save"]')!.click();
    await flushPromises();
    expect(bodyOf(calls.find((c) => c.method === 'patch'))).toEqual({ description: null });
    w.unmount();
  });

  it('清掉到期日送 dueDate:null', async () => {
    const w = await mountDialog();
    const due = w.findAllComponents(quasar.QInput).find((i) => i.props('label') === '到期日')!;
    due.vm.$emit('update:modelValue', '');
    await flushPromises();
    body().querySelector<HTMLElement>('[data-testid="task-save"]')!.click();
    await flushPromises();
    expect(bodyOf(calls.find((c) => c.method === 'patch'))).toEqual({ dueDate: null });
    w.unmount();
  });

  describe('開始日（G12）', () => {
    const input = (w: Awaited<ReturnType<typeof mountDialog>>, label: string) => w.findAllComponents(quasar.QInput).find((i) => i.props('label') === label)!;
    const save = () => body().querySelector<HTMLButtonElement>('[data-testid="task-save"]')!;

    it('有開始日欄位，改開始日只送 startDate', async () => {
      const w = await mountDialog();
      input(w, '開始日').vm.$emit('update:modelValue', '2026-11-20');
      await flushPromises();
      save().click();
      await flushPromises();
      expect(bodyOf(calls.find((c) => c.method === 'patch'))).toEqual({ startDate: '2026-11-20' });
      w.unmount();
    });

    it('開始日晚於到期日時欄位下即時顯示錯誤、不能儲存', async () => {
      const w = await mountDialog();
      input(w, '開始日').vm.$emit('update:modelValue', '2026-12-05');
      await flushPromises();
      expect(body().querySelector('[data-testid="task-start"]')!.closest('.q-field')!.textContent).toContain('開始日不能晚於到期日');
      expect(save().disabled || save().classList.contains('disabled')).toBe(true);
      w.unmount();
    });

    it('只把到期日改得比開始日早：不擋，提示開始日會一併改', async () => {
      const w = await mountDialog({ task: { startDate: '2026-11-20' } });
      input(w, '到期日').vm.$emit('update:modelValue', '2026-11-19');
      await flushPromises();
      expect(body().querySelector('[data-testid="task-start-follows"]')!.textContent).toContain('儲存後開始日會一併改為 2026-11-19');
      save().click();
      await flushPromises();
      expect(bodyOf(calls.find((c) => c.method === 'patch'))).toEqual({ dueDate: '2026-11-19' });
      w.unmount();
    });

    it('伺服器 400 的 startDate 訊息掛在開始日欄位下', async () => {
      const w = await mountDialog({ onPatch: () => new HttpFail(400, { errors: { startDate: ['開始日不能晚於到期日'] } }) });
      input(w, '開始日').vm.$emit('update:modelValue', '2026-11-20');
      await flushPromises();
      save().click();
      await flushPromises();
      expect(body().querySelector('[data-testid="task-start"]')!.closest('.q-field')!.textContent).toContain('開始日不能晚於到期日');
      expect(body().querySelector('[data-testid="task-error"]')).toBeNull();
      w.unmount();
    });

    it('時間軸顯示 startChanged、startFollowedDue', async () => {
      const w = await mountDialog({
        comments: [
          commentWire({ id: 3, kind: 'system', body: '', systemEvent: 'startChanged', systemData: { from: null, to: '2026-11-20' } }),
          commentWire({ id: 4, kind: 'system', body: '', systemEvent: 'startFollowedDue', systemData: { from: '2026-11-20', to: '2026-11-19' } }),
        ],
      });
      expect(body().querySelector('[data-testid="comment-3"]')!.textContent).toContain('開始日從「無開始日」改為「2026-11-20」');
      expect(body().querySelector('[data-testid="comment-4"]')!.textContent).toContain('到期日提前，開始日從「2026-11-20」改為「2026-11-19」');
      w.unmount();
    });
  });

  it('撤回留言失敗時跳出錯誤提示', async () => {
    const w = await mountDialog({
      comments: [commentWire({ id: 8, canWithdraw: true })],
      withdrawFail: new HttpFail(409, { message: '應用程式已關閉' }),
    });
    const notify = vi.spyOn(w.vm.$q, 'notify');
    body().querySelector<HTMLElement>('[data-testid="comment-withdraw-8"]')!.click();
    await flushPromises();
    expect(notify).toHaveBeenCalledWith(expect.objectContaining({ type: 'negative', message: '應用程式已關閉' }));
    w.unmount();
  });

  it('英文語系下標點走語系檔：記錄標籤用半形冒號、錯誤用逗號串接', async () => {
    const w = await mountDialog({
      locale: 'en-US', commentFail: new HttpFail(400, { errors: { files: ['bad type'], body: ['too long'] } }),
    });
    const rec = body().querySelector('[data-testid="task-record"]')!.textContent!;
    expect(rec).not.toContain('：');
    expect(rec).toMatch(/Related record: /);
    const ta = body().querySelector<HTMLTextAreaElement>('textarea[data-testid="mention-input"]')!;
    ta.value = 'hi';
    ta.dispatchEvent(new Event('input'));
    await flushPromises();
    body().querySelector<HTMLElement>('[data-testid="comment-send"]')!.click();
    await flushPromises();
    expect(body().querySelector('[data-testid="comment-error"]')!.textContent).toBe('bad type, too long');
    w.unmount();
  });

  describe('留言附件', () => {
    const file = (n: string) => new File(['x'], n, { type: 'text/plain' });

    it('選 6 個檔案時只保留 5 個並提示上限', async () => {
      const w = await mountDialog();
      const qfile = w.findComponent(quasar.QFile);
      qfile.vm.$emit('update:modelValue', ['a', 'b', 'c', 'd', 'e', 'f'].map((n) => file(`${n}.txt`)));
      await flushPromises();
      expect((qfile.props('modelValue') as File[]).length).toBe(5);
      expect(body().querySelector('[data-testid="attach-limit-warning"]')!.textContent).toContain('最多 5 個檔案');
      w.unmount();
    });

    it('留言附件上限依任務模組生效值', async () => {
      const w = await mountDialog({ maxFiles: 2 });
      const qfile = w.findComponent(quasar.QFile);
      qfile.vm.$emit('update:modelValue', ['a', 'b', 'c'].map((n) => file(`${n}.txt`)));
      await flushPromises();
      expect((qfile.props('modelValue') as File[]).length).toBe(2);
      expect(body().querySelector('[data-testid="attach-limit-warning"]')!.textContent).toContain('最多 2 個檔案');
      expect(calls.find((c) => c.url === '/policies/effective')!.params).toEqual({ module: 'crm_customer' });
      w.unmount();
    });

    it('單檔大小上限也取生效值（不用全系統設定的 maxBytes）', async () => {
      const w = await mountDialog({ maxBytes: 2048 });
      expect(w.findComponent(quasar.QFile).props('maxFileSize')).toBe(2048);
      w.unmount();
    });

    it('生效值載入前附件鈕停用', async () => {
      setActivePinia(createPinia());
      let release: () => void = () => {};
      const gate = new Promise<void>((r) => { release = r; });
      calls = mockHttp((c) => {
        if (c.url === '/tasks/5' && c.method === 'get') return taskDetailWire();
        if (c.url === '/tasks/5/comments' && c.method === 'get') return [];
        if (c.url === '/users') return USERS;
        if (c.url === '/attachments/settings') return { uploadEnabled: true, maxBytes: 1048576, allowedExtensions: ['txt'], maxFilesPerField: 3 };
        if (c.url === '/policies/effective') return gate.then(() => ({ values: {} }));
        return {};
      });
      const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/:p(.*)*', component: { render: () => null } }] });
      const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW, 'en-US': enUS } });
      const w = mount(TaskDialog, {
        props: { taskId: 5 }, attachTo: document.body,
        global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], createPinia(), router, i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
      });
      await flushPromises();
      await new Promise((r) => setTimeout(r, 20));
      expect(w.findComponent(quasar.QFile).props('disable')).toBe(true);
      release();
      w.unmount();
    });

    it('有附件時送出的是 multipart，含 body 與 2 個 files', async () => {
      const w = await mountDialog();
      w.findComponent(quasar.QFile).vm.$emit('update:modelValue', [file('a.txt'), file('b.txt')]);
      const ta = body().querySelector<HTMLTextAreaElement>('textarea[data-testid="mention-input"]')!;
      ta.value = '附上資料';
      ta.dispatchEvent(new Event('input'));
      await flushPromises();
      body().querySelector<HTMLElement>('[data-testid="comment-send"]')!.click();
      await flushPromises();
      const post = calls.find((c) => c.url === '/tasks/5/comments' && c.method === 'post');
      const form = post!.data as FormData;
      expect(form).toBeInstanceOf(FormData);
      expect(form.get('body')).toBe('附上資料');
      expect(form.getAll('files')).toHaveLength(2);
      w.unmount();
    });

    it('只有附件、沒有文字也能送出', async () => {
      const w = await mountDialog();
      w.findComponent(quasar.QFile).vm.$emit('update:modelValue', [file('a.txt')]);
      await flushPromises();
      expect(body().querySelector('[data-testid="comment-send"]')!.hasAttribute('disabled')).toBe(false);
      w.unmount();
    });

    it('uploadEnabled=false 時附件鈕停用並顯示原因', async () => {
      const w = await mountDialog({ settings: { uploadEnabled: false, disabledReason: 'Demo 站不開放上傳' } });
      expect(w.findComponent(quasar.QFile).props('disable')).toBe(true);
      expect(body().querySelector('[data-testid="attach-off"]')!.textContent).toContain('Demo 站不開放上傳');
      w.unmount();
    });

    it('uploadEnabled=false 且沒有原因時，顯示通用說明', async () => {
      const w = await mountDialog({ settings: { uploadEnabled: false } });
      expect(body().querySelector('[data-testid="attach-off"]')!.textContent).toContain('此環境已停用附件上傳');
      w.unmount();
    });

    it('時間軸在留言下方列附件，連結到下載路徑並顯示檔名與大小', async () => {
      const w = await mountDialog({
        comments: [commentWire({ id: 7, attachments: [{ id: 3, fileName: '報價.pdf', contentType: 'application/pdf', size: 2048 }] })],
      });
      const a = body().querySelector('[data-testid="comment-attachment-3"]')!;
      expect(a.getAttribute('href')).toBe('/api/v3/dynamic/tasks/5/comments/7/attachments/3');
      expect(a.textContent).toContain('報價.pdf');
      expect(a.parentElement!.textContent).toContain('2 KB');
      w.unmount();
    });

    it('檔案超過大小上限時被擋下並提示，不進入待送清單', async () => {
      const w = await mountDialog();
      const qfile = w.findComponent(quasar.QFile);
      expect(qfile.props('maxFileSize')).toBe(1048576);
      qfile.vm.$emit('rejected', [{ failedPropValidation: 'max-file-size', file: file('big.txt') }]);
      await flushPromises();
      expect(body().querySelector('[data-testid="attach-rejected"]')!.textContent).toContain('超過大小上限');
      w.unmount();
    });

    it('400 依 files、body 欄位顯示錯誤，用頓號串接', async () => {
      const w = await mountDialog({ commentFail: new HttpFail(400, { errors: { files: ['不允許的副檔名'], body: ['內容太長'] } }) });
      w.findComponent(quasar.QFile).vm.$emit('update:modelValue', [file('a.txt')]);
      await flushPromises();
      body().querySelector<HTMLElement>('[data-testid="comment-send"]')!.click();
      await flushPromises();
      expect(body().querySelector('[data-testid="comment-error"]')!.textContent).toBe('不允許的副檔名、內容太長');
      w.unmount();
    });

    it('413 沒有 JSON 時顯示通用訊息，不會解析失敗', async () => {
      const w = await mountDialog();
      const original = calls;
      mockHttp((c) => (c.method === 'post' ? new HttpFail(413, '<html>too large</html>') : c.url === '/attachments/settings' ? { uploadEnabled: true, maxBytes: 1, allowedExtensions: [], maxFilesPerField: 1 } : []));
      void original;
      w.findComponent(quasar.QFile).vm.$emit('update:modelValue', [file('a.txt')]);
      await flushPromises();
      body().querySelector<HTMLElement>('[data-testid="comment-send"]')!.click();
      await flushPromises();
      expect(body().querySelector('[data-testid="comment-error"]')!.textContent).toContain('檔案太大');
      w.unmount();
    });
  });

  it('有編輯權限時改標題會送只含變動欄位的 PATCH', async () => {
    const w = await mountDialog();
    const title = body().querySelector<HTMLInputElement>('input[data-testid="task-title"]')!;
    title.value = '新標題';
    title.dispatchEvent(new Event('input'));
    await flushPromises();
    body().querySelector<HTMLElement>('[data-testid="task-save"]')!.click();
    await flushPromises();
    const patch = calls.find((c) => c.method === 'patch');
    expect(patch?.url).toBe('/tasks/5');
    expect(bodyOf(patch)).toEqual({ title: '新標題' });
    w.unmount();
  });

  it('沒有編輯權限時欄位唯讀、沒有儲存鈕；沒有留言權限時沒有留言框', async () => {
    const w = await mountDialog({ task: { canEdit: false, canReassign: false, canComment: false, canDelete: false } });
    expect(body().querySelector('[data-testid="task-save"]')).toBeNull();
    expect(body().querySelector('textarea[data-testid="mention-input"]')).toBeNull();
    expect(body().querySelector('[data-testid="task-delete"]')).toBeNull();
    w.unmount();
  });

  describe('manager view of a subordinate task (D5)', () => {
    const supervised = { canEdit: false, canReassign: true, supervised: true, canDelete: false };
    const fieldOf = (label: string) => [...body().querySelectorAll('.q-field')].find((f) => f.textContent?.includes(label))!;

    it('TaskDialog for a supervised task enables only the assignee and shows the hint', async () => {
      const w = await mountDialog({ task: supervised });
      expect(body().querySelector('[data-testid="task-supervisor-hint"]')!.textContent).toBe('你以主管身分查看，只能改負責人與留言');
      expect(fieldOf('負責人').classList.contains('q-field--readonly')).toBe(false);
      for (const label of ['標題', '說明', '狀態']) expect(fieldOf(label).classList.contains('q-field--readonly')).toBe(true);
      expect(body().querySelector('[data-testid="task-save"]')).not.toBeNull();
      w.unmount();
    });

    it('a normal editor sees no supervisor hint', async () => {
      const w = await mountDialog();
      expect(body().querySelector('[data-testid="task-supervisor-hint"]')).toBeNull();
      w.unmount();
    });

    it('sends only assigneeUserId', async () => {
      const w = await mountDialog({ task: supervised });
      w.findAllComponents(GenericSelect)[0].vm.$emit('update:modelValue', 'sales02');
      await flushPromises();
      body().querySelector<HTMLElement>('[data-testid="task-save"]')!.click();
      await flushPromises();
      const patch = calls.find((c) => c.method === 'patch');
      expect(bodyOf(patch)).toEqual({ assigneeUserId: 'sales02' });
      w.unmount();
    });

    it('closes and shows the moved-out notice when visible is false', async () => {
      const w = await mountDialog({ task: supervised, patchReply: { id: 5, visible: false, undoToken: 'tok-1' } });
      w.findAllComponents(GenericSelect)[0].vm.$emit('update:modelValue', 'admin');
      await flushPromises();
      body().querySelector<HTMLElement>('[data-testid="task-save"]')!.click();
      await flushPromises();
      await new Promise((r) => setTimeout(r, 50));
      await flushPromises();
      expect(w.emitted('update:taskId')?.at(-1)).toEqual([null]);
      expect(showNotice).toHaveBeenCalledWith(5, 'tok-1');
      w.unmount();
    });
  });
});

describe('TaskDialog subtasks', () => {
  beforeEach(() => { document.body.innerHTML = ''; });
  afterEach(() => { document.body.innerHTML = ''; });
  const sub = (id: number, o: Record<string, unknown> = {}) => ({ ...taskDetailWire({ id, title: `子${id}`, level: 'child', parentTaskId: 5, parentTitle: '辦活動' }), ...o });
  const parent = (o: Record<string, unknown> = {}) => ({
    level: 'parent', canAddSubtask: true, progress: { done: 1, cancelled: 1, total: 4 },
    subtasks: [sub(11, { status: 'done' }), sub(12), sub(13, { status: 'cancelled' }), sub(14, { assigneeUserId: null, assigneeName: null })], ...o,
  });
  const q = (id: string) => body().querySelector<HTMLElement>(`[data-testid="${id}"]`);
  const click = async (id: string) => { q(id)!.click(); await flushPromises(); await new Promise((r) => setTimeout(r, 20)); await flushPromises(); };
  const statusSelect = (w: Awaited<ReturnType<typeof mountDialog>>) => w.findAllComponents(GenericSelect).find((c) => c.props('label') === '狀態')!;

  it('父任務顯示子任務清單與「完成／（總數－取消）」進度；有取消時有提示文字', async () => {
    const w = await mountDialog({ task: parent() });
    expect(q('subtask-list')!.querySelectorAll('[data-testid^="subtask-row-"]')).toHaveLength(4);
    expect(q('subtask-progress-text')!.textContent).toBe('1／3');
    expect(q('subtask-progress')!.getAttribute('aria-label')).toContain('另有 1 個已取消');
    w.unmount();
  });

  it('點子任務列開那個子任務（update:taskId）', async () => {
    const w = await mountDialog({ task: parent() });
    await click('subtask-row-12');
    expect(w.emitted('update:taskId')?.at(-1)).toEqual([12]);
    w.unmount();
  });

  it('全部取消時不顯示進度（T20），沒有子任務也沒有進度但仍可新增', async () => {
    let w = await mountDialog({ task: parent({ progress: { done: 0, cancelled: 4, total: 4 } }) });
    expect(q('subtask-progress')).toBeNull();
    w.unmount();
    w = await mountDialog({ task: { level: 'standalone', canAddSubtask: true, progress: null, subtasks: [] } });
    expect(q('subtask-progress')).toBeNull();
    expect(q('subtask-empty')).not.toBeNull();
    expect(q('subtask-add')).not.toBeNull();
    w.unmount();
  });

  it('不能新增子任務、也沒有子任務的獨立任務不顯示子任務區塊；子任務自己也不顯示', async () => {
    let w = await mountDialog({ task: { level: 'standalone', canAddSubtask: false, subtasks: [] } });
    expect(q('subtask-section')).toBeNull();
    w.unmount();
    w = await mountDialog({ task: sub(5) });
    expect(q('subtask-section')).toBeNull();
    w.unmount();
  });

  it('新增子任務：負責人預填父任務負責人、到期日空，只送標題、負責人、到期日', async () => {
    const w = await mountDialog({ task: parent() });
    await click('subtask-add');
    const select = w.findAllComponents(GenericSelect).filter((c) => c.props('label') === '負責人').at(-1)!;
    expect(select.props('modelValue')).toBe('sales01');
    const input = q('subtask-form-title') as unknown as HTMLInputElement;
    input.value = '訂場地';
    input.dispatchEvent(new Event('input'));
    await flushPromises();
    await click('subtask-form-submit');
    const post = calls.find((c) => c.url === '/tasks/5/subtasks');
    expect(bodyOf(post)).toEqual({ title: '訂場地', assigneeUserId: 'sales01', dueDate: null });
    w.unmount();
  });

  it('父任務負責人已停用時預填空白；沒標題不送出', async () => {
    const w = await mountDialog({ task: parent({ assigneeActive: false }) });
    await click('subtask-add');
    const select = w.findAllComponents(GenericSelect).filter((c) => c.props('label') === '負責人').at(-1)!;
    expect(select.props('modelValue')).toBeNull();
    await click('subtask-form-submit');
    expect(calls.some((c) => c.url === '/tasks/5/subtasks')).toBe(false);
    expect(q('subtask-form')!.textContent).toContain('請輸入標題');
    w.unmount();
  });

  it('新增被後端擋下（上限 400）時顯示訊息、表單留著', async () => {
    const w = await mountDialog({ task: parent(), subtaskReply: new HttpFail(400, { errors: { subtasks: ['一個任務最多 2 個子任務'] } }) });
    await click('subtask-add');
    const input = q('subtask-form-title') as unknown as HTMLInputElement;
    input.value = 'x';
    input.dispatchEvent(new Event('input'));
    await flushPromises();
    await click('subtask-form-submit');
    expect(q('subtask-form-error')!.textContent).toContain('一個任務最多 2 個子任務');
    w.unmount();
  });

  it('子任務上方顯示父任務標題：看得到父任務可點開，看不到只是文字（U13）', async () => {
    let w = await mountDialog({ task: sub(5, { parentVisible: true }) });
    expect(q('task-parent')!.textContent).toContain('父任務：辦活動');
    await click('task-parent-open');
    expect(w.emitted('update:taskId')?.at(-1)).toEqual([5]);
    w.unmount();
    w = await mountDialog({ task: sub(5, { parentVisible: false }) });
    expect(q('task-parent')!.textContent).toContain('辦活動');
    expect(q('task-parent-open')).toBeNull();
    w.unmount();
  });

  it('改完成遇到 409 subtasksOpen：跳三選一，選一併取消就帶 cascade:cancel 重送', async () => {
    const w = await mountDialog({
      task: parent(),
      onPatch: (c) => ((bodyOf(c) as { cascade?: string }).cascade ? taskDetailWire({ status: 'done' }) : new HttpFail(409, { code: 'subtasksOpen', params: { count: 2 }, message: '還有 2 個未完成的子任務' })),
    });
    statusSelect(w).vm.$emit('update:modelValue', 'done');
    await flushPromises();
    await click('task-save');
    expect(q('subtasks-open-body')!.textContent).toContain('還有 2 個未完成的子任務');
    expect(q('task-error')).toBeNull();
    await click('subtasks-open-cancel');
    const patches = calls.filter((c) => c.method === 'patch');
    expect(patches).toHaveLength(2);
    expect(bodyOf(patches[0])).toEqual({ status: 'done' });
    expect(bodyOf(patches[1])).toEqual({ status: 'done', cascade: 'cancel' });
    expect(q('subtasks-open')).toBeNull();
    w.unmount();
  });

  it('三選一按「取消操作」不重送，任務維持原樣', async () => {
    const w = await mountDialog({ task: parent(), onPatch: () => new HttpFail(409, { code: 'subtasksOpen', params: { count: 2 } }) });
    statusSelect(w).vm.$emit('update:modelValue', 'done');
    await flushPromises();
    await click('task-save');
    await click('subtasks-open-abort');
    expect(calls.filter((c) => c.method === 'patch')).toHaveLength(1);
    expect(q('subtasks-open')).toBeNull();
    w.unmount();
  });

  it('子任務重開遇到 409 parentCancelled 顯示父任務已取消的訊息', async () => {
    const w = await mountDialog({
      task: sub(5, { status: 'cancelled', parentVisible: true }),
      onPatch: () => new HttpFail(409, { code: 'parentCancelled', message: 'server text' }),
    });
    statusSelect(w).vm.$emit('update:modelValue', 'todo');
    await flushPromises();
    await click('task-save');
    expect(q('task-error')!.textContent).toBe('父任務已取消，請先恢復父任務');
    w.unmount();
  });

  it('刪除父任務的確認框寫出子任務數（取 progress.total）；沒有子任務用原本文字', async () => {
    let w = await mountDialog({ task: parent() });
    await click('task-delete');
    expect(q('task-delete-text')!.textContent).toContain('這個任務有 4 個子任務');
    w.unmount();
    w = await mountDialog();
    await click('task-delete');
    expect(q('task-delete-text')!.textContent).toBe('確定要刪除這個任務嗎？');
    w.unmount();
  });

  it('有未存修改時新增子任務，重抓詳細不會蓋掉改到一半的標題', async () => {
    // 每次讀取回不同的版本，重抓才會得到新資料物件
    let n = 0;
    const w = await mountDialog({ task: { ...parent(), get version() { return `v${n++}`; } } });
    const titleInput = () => body().querySelector<HTMLInputElement>('input[data-testid="task-title"]')!;
    titleInput().value = '改到一半的標題';
    titleInput().dispatchEvent(new Event('input'));
    await flushPromises();
    await click('subtask-add');
    const input = q('subtask-form-title') as unknown as HTMLInputElement;
    input.value = '新子任務';
    input.dispatchEvent(new Event('input'));
    await flushPromises();
    await click('subtask-form-submit');
    expect(calls.filter((c) => c.url === '/tasks/5' && c.method === 'get').length).toBeGreaterThan(1);
    expect(titleInput().value).toBe('改到一半的標題');
    w.unmount();
  });

  it('連按 Enter 只送出一次新增子任務', async () => {
    const w = await mountDialog({ task: parent() });
    await click('subtask-add');
    const input = q('subtask-form-title') as unknown as HTMLInputElement;
    input.value = '訂場地';
    input.dispatchEvent(new Event('input'));
    await flushPromises();
    input.dispatchEvent(new KeyboardEvent('keyup', { key: 'Enter' }));
    input.dispatchEvent(new KeyboardEvent('keyup', { key: 'Enter' }));
    await flushPromises();
    await new Promise((r) => setTimeout(r, 20));
    expect(calls.filter((c) => c.url === '/tasks/5/subtasks')).toHaveLength(1);
    w.unmount();
  });
});
