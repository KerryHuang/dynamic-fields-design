import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
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
}
let calls: Call[];
const body = () => document.body;

async function mountDialog(o: Opts = {}) {
  setActivePinia(createPinia());
  calls = mockHttp((c) => {
    if (c.url === '/tasks/5' && c.method === 'get') return o.taskStatus ? new HttpFail(o.taskStatus, { message: '找不到' }) : taskDetailWire(o.task);
    if (c.url === '/tasks/5/comments' && c.method === 'get') return o.comments ?? [];
    if (c.url === '/tasks/5/comments' && c.method === 'post') return commentWire();
    if (c.url === '/users') return USERS;
    if (c.url === '/users/mention-candidates') return o.candidates ?? [USERS[1]];
    if (c.url === '/attachments/settings') return { uploadEnabled: true, maxBytes: 1048576, allowedExtensions: ['txt', 'pdf'], maxFilesPerField: 3, ...o.settings };
    return {};
  });
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/:p(.*)*', component: { render: () => null } }] });
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(TaskDialog, {
    props: { taskId: 5 },
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], createPinia(), router, i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
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

  it('有編輯權限時改標題會送只含變動欄位的 PATCH；清掉到期日送 null', async () => {
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
