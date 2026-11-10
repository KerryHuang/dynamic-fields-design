import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import enUS from 'src/i18n/en-US.json';
import zhTW from 'src/i18n/zh-TW.json';
import { bodyOf, HttpFail, mockHttp, type Call } from 'src/test-utils/mock-http';
import TaskDialog from './TaskDialog.vue';
import { commentWire, taskDetailWire, USERS } from './task-fixtures';

interface Opts {
  task?: Record<string, unknown>;
  comments?: unknown[];
  settings?: Record<string, unknown>;
  candidates?: unknown[];
  taskStatus?: number;
  commentFail?: HttpFail;
  withdrawFail?: HttpFail;
  locale?: 'zh-TW' | 'en-US';
}
let calls: Call[];
const body = () => document.body;

async function mountDialog(o: Opts = {}) {
  setActivePinia(createPinia());
  calls = mockHttp((c) => {
    if (c.url === '/tasks/5' && c.method === 'get') return o.taskStatus ? new HttpFail(o.taskStatus, { message: '找不到' }) : taskDetailWire(o.task);
    if (c.url === '/tasks/5/comments' && c.method === 'get') return o.comments ?? [];
    if (c.url === '/tasks/5/comments' && c.method === 'post') return o.commentFail ?? commentWire();
    if (/\/withdraw$/.test(c.url)) return o.withdrawFail ?? commentWire({ withdrawn: true });
    if (c.url === '/users') return USERS;
    if (c.url === '/users/mention-candidates') return o.candidates ?? [USERS[1]];
    if (c.url === '/attachments/settings') return { uploadEnabled: true, maxBytes: 1048576, allowedExtensions: ['txt', 'pdf'], maxFilesPerField: 3, ...o.settings };
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
    const w = await mountDialog({ task: { frozen: true, canEdit: false, canComment: false, canDelete: false } });
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
    const w = await mountDialog({ task: { canEdit: false, canComment: false, canDelete: false } });
    expect(body().querySelector('[data-testid="task-save"]')).toBeNull();
    expect(body().querySelector('textarea[data-testid="mention-input"]')).toBeNull();
    expect(body().querySelector('[data-testid="task-delete"]')).toBeNull();
    w.unmount();
  });
});
